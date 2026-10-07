using GameShare.Core;
using GameShare.Core.Data;
using GameShare.Protocol;

namespace GameShare.Agent;

/// <summary>
/// Keeps the definitions of what is installed here the ones the administrator signed. gameshare.json is outside the content hash, so
/// fixing it (an installer's arguments, how a game starts) makes no new version to download: the signed list names the definition, and
/// a PC whose own is not that one fetches it from a PC on the LAN that has it. Done when a new list arrives and when what the PCs on the
/// LAN offer changes, so the fix reaches every PC by itself, not only one where somebody happens to prepare a game.
/// A game found by a scan, copied in by hand without its gameshare.json, has no definition at all: how it starts is taken from a PC that
/// offers the same files, as a download would have brought it. Done when the scan finds it, so it is ready to play without a wait.
/// </summary>
public sealed class DefinitionSync
{
    /// <summary>Triggers closer together than this are one round: the catalog changes often while PCs come and go.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);
    /// <summary>PCs that did not have the signed definition are asked again after this, or at once when the list or the PCs change.</summary>
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, (HashSet<string> Peers, DateTimeOffset At)> _asked = new(StringComparer.Ordinal);

    private readonly GameShareDb _db;
    private readonly GameLibrary _library;
    private readonly TrustService _trust;
    private readonly PeerCatalog _catalog;
    private readonly ILogger<DefinitionSync> _log;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public DefinitionSync(GameShareDb db, GameLibrary library, TrustService trust, PeerCatalog catalog, ILogger<DefinitionSync> log)
    {
        _db = db;
        _library = library;
        _trust = trust;
        _catalog = catalog;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _trust.Changed += OnChanged;
        _catalog.Changed += OnChanged;
        _library.GameDiscovered += OnDiscovered;
        _library.InstallationChanged += OnInstallationChanged;
        try
        {
            while (true)
            {
                // Also every so often by itself: a PC that offered an old definition when it was asked may have the signed one now,
                // and nothing else on the LAN may change to say so.
                await _wake.WaitAsync(AskAgainAfter, ct).ConfigureAwait(false);
                await Task.Delay(Settle, ct).ConfigureAwait(false);
                if (_wake.CurrentCount > 0) await _wake.WaitAsync(ct).ConfigureAwait(false); // what came meanwhile is this round
                try { await SyncAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "Fetching the signed definitions failed"); }
            }
        }
        finally
        {
            _trust.Changed -= OnChanged;
            _catalog.Changed -= OnChanged;
            _library.GameDiscovered -= OnDiscovered;
            _library.InstallationChanged -= OnInstallationChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        lock (_asked) _asked.Clear(); // a new list can name other definitions: ask everybody again
        Wake();
    }

    private void OnChanged(object? sender, CatalogChange e) => Wake();

    private void OnDiscovered(object? sender, LibraryGame game) => Forget(game.Stored.Manifest.ContentHash);

    private void OnInstallationChanged(object? sender, InstallationChange change)
    {
        if (change.Current.State == InstallationState.Installed) Forget(change.Current.ContentHash);
    }

    /// <summary>A game that is here anew: the PCs that did not have its definition before are asked again, now.</summary>
    private void Forget(string contentHash)
    {
        lock (_asked) _asked.Remove(contentHash);
        Wake();
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a round is due already */ }
    }

    /// <summary>
    /// Fetches the signed definition of everything installed here whose own is not it, and a definition for everything that has none,
    /// where a PC on the LAN offers it.
    /// </summary>
    /// <returns>How many were replaced.</returns>
    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        int replaced = 0;
        var offered = _catalog.Offers.Select(o => o.Game.ContentHash).ToHashSet(StringComparer.Ordinal);
        foreach (var installation in await _db.ListInstallationsAsync(ct).ConfigureAwait(false))
        {
            if (installation.State != InstallationState.Installed || !offered.Contains(installation.ContentHash)) continue;
            var stored = await _db.GetManifestAsync(installation.ContentHash, ct).ConfigureAwait(false);
            if (stored is null) continue;
            var verdict = _trust.CheckDefinition(installation.ContentHash, stored.Manifest.Definition);
            var missing = stored.Manifest.Definition is null && verdict is DefinitionVerdict.NotChecked or DefinitionVerdict.NotSigned;
            if (verdict != DefinitionVerdict.Different && !missing) continue;

            // A manifest can be megabytes: the same PCs are not asked again and again for what they did not have a moment ago.
            var peers = _catalog.Offers.Where(o => o.Game.ContentHash == installation.ContentHash).Select(o => o.Peer.MachineId).ToHashSet(StringComparer.Ordinal);
            lock (_asked)
                if (_asked.TryGetValue(installation.ContentHash, out var asked) && peers.IsSubsetOf(asked.Peers) && DateTimeOffset.UtcNow - asked.At < AskAgainAfter)
                    continue;
            var found = missing ? await AdoptAsync(installation.ContentHash, ct).ConfigureAwait(false) : await RefreshAsync(installation.ContentHash, ct).ConfigureAwait(false);
            if (found is not null) replaced++;
            else lock (_asked) _asked[installation.ContentHash] = (peers, DateTimeOffset.UtcNow);
        }
        return replaced;
    }

    /// <summary>The signed definition of an installed game or package, from a PC on the LAN, put in place of this PC's. Null when none has it.</summary>
    public async Task<GameManifest?> RefreshAsync(string contentHash, CancellationToken ct = default)
    {
        // Signed, only for the version the folder is about to become (a file added to a package, not registered yet): putting the
        // old version's definition back would undo the administrator's change before the folder can be registered as the new one.
        var stored = await _db.GetManifestAsync(contentHash, ct).ConfigureAwait(false);
        if (_trust.IsSignedForAnotherVersion(contentHash, stored?.Manifest.Definition))
        {
            _log.LogInformation("The definition of {Name} is the one signed for another version of it, it is kept", stored!.Manifest.Name);
            return null;
        }
        try
        {
            var (fetched, _) = await _catalog.FetchAsync(contentHash, ct, m => _trust.CheckDefinition(contentHash, m.Definition) == DefinitionVerdict.Verified).ConfigureAwait(false);
            if (_trust.CheckDefinition(contentHash, fetched.Definition) != DefinitionVerdict.Verified || fetched.Definition is null) return null;
            var manifest = await _library.ReplaceDefinitionAsync(contentHash, fetched.Definition, ct).ConfigureAwait(false);
            _log.LogInformation("The definition of {Name} is now the one the administrator signed, from a PC on the LAN", manifest.Name);
            return manifest;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or HttpRequestException)
        {
            _log.LogInformation("No PC on the LAN has the signed definition of {Hash}: {Reason}", contentHash[..12], ex.Message);
            return null;
        }
    }

    /// <summary>
    /// A definition for an installed game that has none, from a PC on the LAN with the same files. Nobody signed one to compare with,
    /// so it is trusted as far as a download's is: setup steps still need what the trust settings ask for. Null when no PC has one.
    /// </summary>
    public async Task<GameManifest?> AdoptAsync(string contentHash, CancellationToken ct = default)
    {
        try
        {
            var (fetched, _) = await _catalog.FetchAsync(contentHash, ct, m => m.Definition is not null).ConfigureAwait(false);
            if (fetched.Definition is null) return null;
            var manifest = await _library.ReplaceDefinitionAsync(contentHash, fetched.Definition, ct).ConfigureAwait(false);
            _log.LogInformation("{Name} had no definition here, it now has the one a PC on the LAN uses for the same files", manifest.Name);
            return manifest;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or HttpRequestException or InvalidDataException)
        {
            _log.LogInformation("No PC on the LAN has a definition of {Hash}: {Reason}", contentHash[..12], ex.Message);
            return null;
        }
    }
}
