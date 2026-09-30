using GameShare.Core;
using GameShare.Core.Data;
using GameShare.Protocol;

namespace GameShare.Agent;

/// <summary>
/// Keeps the definitions of what is installed here the ones the administrator signed. gameshare.json is outside the content hash, so
/// fixing it (an installer's arguments, how a game starts) makes no new version to download: the signed list names the definition, and
/// a PC whose own is not that one fetches it from a PC on the LAN that has it. Done when a new list arrives and when what the PCs on the
/// LAN offer changes, so the fix reaches every PC by itself, not only one where somebody happens to prepare a game.
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
        try
        {
            while (true)
            {
                await _wake.WaitAsync(ct).ConfigureAwait(false);
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
        }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        lock (_asked) _asked.Clear(); // a new list can name other definitions: ask everybody again
        Wake();
    }

    private void OnChanged(object? sender, CatalogChange e) => Wake();

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a round is due already */ }
    }

    /// <summary>Fetches the signed definition of everything installed here whose own is not it, where a PC on the LAN offers it.</summary>
    /// <returns>How many were replaced.</returns>
    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        int replaced = 0;
        var offered = _catalog.Offers.Select(o => o.Game.ContentHash).ToHashSet(StringComparer.Ordinal);
        foreach (var installation in await _db.ListInstallationsAsync(ct).ConfigureAwait(false))
        {
            if (installation.State != InstallationState.Installed || !offered.Contains(installation.ContentHash)) continue;
            var stored = await _db.GetManifestAsync(installation.ContentHash, ct).ConfigureAwait(false);
            if (stored is null || _trust.CheckDefinition(installation.ContentHash, stored.Manifest.Definition) != DefinitionVerdict.Different) continue;

            // A manifest can be megabytes: the same PCs are not asked again and again for what they did not have a moment ago.
            var peers = _catalog.Offers.Where(o => o.Game.ContentHash == installation.ContentHash).Select(o => o.Peer.MachineId).ToHashSet(StringComparer.Ordinal);
            lock (_asked)
                if (_asked.TryGetValue(installation.ContentHash, out var asked) && peers.IsSubsetOf(asked.Peers) && DateTimeOffset.UtcNow - asked.At < AskAgainAfter)
                    continue;
            if (await RefreshAsync(installation.ContentHash, ct).ConfigureAwait(false) is not null) replaced++;
            else lock (_asked) _asked[installation.ContentHash] = (peers, DateTimeOffset.UtcNow);
        }
        return replaced;
    }

    /// <summary>The signed definition of an installed game or package, from a PC on the LAN, put in place of this PC's. Null when none has it.</summary>
    public async Task<GameManifest?> RefreshAsync(string contentHash, CancellationToken ct = default)
    {
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
}
