using GameShare.Core.Data;
using GameShare.Torrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameShare.Core;

public enum SeedEventKind { Started, Stopped }

/// <param name="Path">The folder the game is handed out from: the copy that is played, or the untouched one in the source folder.</param>
public sealed record SeedEvent(SeedEventKind Kind, string ContentHash, string Path, string GameName);

/// <summary>What this PC can hand out of an installed game right now.</summary>
/// <param name="IsComplete">Every piece is present and verified.</param>
/// <param name="PercentIntact">Share of the game's data that is present and verified, 0 to 100.</param>
public sealed record SeedOffer(bool IsComplete, double PercentIntact);

/// <summary>
/// Offers installed games to other PCs. A complete game is offered whole. A game whose files changed, for example because it
/// was played, is still offered, but only the pieces that still match their hashes: the transfer is upload-only, so it never
/// writes, and it never claims a piece it cannot prove. Several damaged copies can therefore complete a third PC together.
/// When the source folder holds an intact copy of the game, that copy is handed out instead of the one that is played: it never
/// changes, and the played copy is left to the game. A game that is only in the source folder is handed out too.
/// </summary>
public sealed class SeedManager
{
    private readonly TorrentEngine _engine;
    private readonly GameShareDb _db;
    private readonly ILogger _log;

    public SeedManager(TorrentEngine engine, GameShareDb db, ILogger<SeedManager>? logger = null)
    {
        _engine = engine;
        _db = db;
        _log = logger ?? NullLogger<SeedManager>.Instance;
    }

    public event EventHandler<SeedEvent>? SeedEventRaised;

    // Installations whose game is being played. Their seed is stopped, so it neither reads the files nor holds them open.
    private readonly HashSet<long> _playing = [];
    // Files of a game that is played changed. Its seed is checked again when it steps back in.
    private readonly HashSet<long> _stale = [];

    private bool IsPlaying(Installation installation) { lock (_playing) return _playing.Contains(installation.Id); }

    /// <summary>
    /// The game is being played. While it is, its seed does not send, does not read the folder and does not keep its files open,
    /// so the game can save into them. Nothing else about the game changes: it is still listed and offered as before.
    /// </summary>
    public async Task SuspendAsync(Installation installation, CancellationToken ct = default)
    {
        lock (_playing) _playing.Add(installation.Id);
        var transfer = FindTransfer(await _db.GetManifestAsync(installation.ContentHash, ct).ConfigureAwait(false));
        if (transfer is null || !SeedsFrom(transfer, installation.InstallPath)) return; // from the source copy: the game is not in its way
        transfer.Suspend();
        _log.LogInformation("Seed of {Path} stepped aside, the game is running", installation.InstallPath);
    }

    /// <summary>The game was closed. The seed sends again, after a check when the files changed meanwhile.</summary>
    public async Task ResumeAsync(Installation installation, CancellationToken ct = default)
    {
        bool stale;
        lock (_playing) { _playing.Remove(installation.Id); stale = _stale.Remove(installation.Id); }
        if (!Enabled) return;

        var transfer = FindTransfer(await _db.GetManifestAsync(installation.ContentHash, ct).ConfigureAwait(false));
        if (transfer is null || !SeedsFrom(transfer, installation.InstallPath))
        {
            await StartAsync(installation, ct).ConfigureAwait(false);
            return;
        }
        transfer.Resume();
        if (stale) transfer.ForceRecheck();
        _log.LogInformation("Seed of {Path} sends again{Checked}", installation.InstallPath, stale ? ", after a check" : "");
    }

    /// <summary>
    /// Global switch. While false nothing is offered to other PCs: <see cref="StartAsync"/> stops the game's transfer
    /// instead of starting it, which also covers a download that has just finished.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Starts seeding every installation that has seeding enabled, damaged ones too, and every intact source copy. Call once at agent start.
    /// </summary>
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        var installations = await _db.ListInstallationsAsync(ct).ConfigureAwait(false);
        foreach (var inst in installations)
        {
            if (!inst.Seeding) continue;
            try { await StartAsync(inst, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            {
                // One broken game must not stop the others from being offered.
                _log.LogWarning(ex, "Cannot seed installation {Path}", inst.InstallPath);
            }
        }
        foreach (var copy in await _db.ListSourceCopiesAsync(ct).ConfigureAwait(false))
        {
            if (copy.State != InstallationState.Installed || installations.Any(i => i.ContentHash == copy.ContentHash && i.Seeding)) continue; // started above
            try { await StartSourceAsync(copy, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            {
                _log.LogWarning(ex, "Cannot seed source copy {Path}", copy.Path);
            }
        }
    }

    /// <summary>
    /// Seeds the game from where it should be seeded now: its intact source copy, else the copy that is played, else nowhere.
    /// Call it when a source copy appeared, changed or went away, and when a download of the game ended without completing.
    /// </summary>
    public async Task RefreshAsync(string contentHash, CancellationToken ct = default)
    {
        if (await _db.FindSourceCopyAsync(contentHash, ct).ConfigureAwait(false) is { } copy)
        {
            await StartSourceAsync(copy, ct).ConfigureAwait(false);
            return;
        }
        var installation = (await _db.ListInstallationsAsync(ct).ConfigureAwait(false)).FirstOrDefault(i => i.ContentHash == contentHash);
        if (installation is { Seeding: true })
        {
            await StartAsync(installation, ct).ConfigureAwait(false);
            return;
        }
        // Nothing to seed from any more, or the copy that is played is not to be seeded: a seed of the source copy stops.
        var stored = await _db.GetManifestAsync(contentHash, ct).ConfigureAwait(false);
        if (FindTransfer(stored) is { } transfer && !await HasActiveDownloadAsync(contentHash, ct).ConfigureAwait(false)
            && (installation is null || !SeedsFrom(transfer, installation.InstallPath)))
            await RemoveAsync(transfer, contentHash, Path.Combine(transfer.SavePath, transfer.Name), stored!.Manifest.Name, ct).ConfigureAwait(false);
    }

    /// <summary>A download of the game ended without completing: its intact source copy, if there is one, is handed out again.</summary>
    public async Task ReturnToSourceAsync(string contentHash, CancellationToken ct = default)
    {
        if (await _db.FindSourceCopyAsync(contentHash, ct).ConfigureAwait(false) is { } copy)
            await StartSourceAsync(copy, ct).ConfigureAwait(false);
    }

    /// <summary>Seeds the untouched copy in the source folder, in place of the copy that is played if that one was being seeded.</summary>
    /// <returns>False when nothing was started: seeding is off, or a download is working on the game.</returns>
    public async Task<bool> StartSourceAsync(SourceCopy copy, CancellationToken ct = default)
    {
        if (!Enabled) return false;
        // A download, repair or update owns the transfer of that game, and it must be allowed to write. It hands over when it is done.
        if (await HasActiveDownloadAsync(copy.ContentHash, ct).ConfigureAwait(false)) return false;

        var stored = await _db.GetManifestAsync(copy.ContentHash, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No manifest stored for {copy.ContentHash}.");
        if (stored.TorrentBytes is null)
            throw new InvalidOperationException($"No torrent metadata stored for {stored.Manifest.Name}.");
        var folder = Path.GetFileName(copy.Path.TrimEnd('\\', '/'));
        if (!string.Equals(folder, stored.Manifest.FolderName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Source folder {copy.Path} must be named '{stored.Manifest.FolderName}', as the game is.");

        var transfer = FindTransfer(stored);
        if (transfer is not null && !SeedsFrom(transfer, copy.Path))
        {
            await _engine.RemoveAsync(transfer, ct).ConfigureAwait(false); // it was the copy that is played: the source copy takes over
            transfer = null;
        }
        if (transfer is null)
        {
            var parent = Path.GetDirectoryName(copy.Path.TrimEnd('\\', '/'))!;
            transfer = await _engine.AddAsync(stored.TorrentBytes, parent, copy.ResumeData, uploadOnly: true, cancellationToken: ct).ConfigureAwait(false);
        }
        transfer.UploadOnly = true;
        if (transfer.IsStopped) transfer.Start();

        _log.LogInformation("Seed started: {Name} from the source copy {Path}", stored.Manifest.Name, copy.Path);
        SeedEventRaised?.Invoke(this, new SeedEvent(SeedEventKind.Started, copy.ContentHash, copy.Path, stored.Manifest.Name));
        return true;
    }

    /// <summary>Whether the transfer reads the game from <paramref name="gamePath"/>.</summary>
    private static bool SeedsFrom(TorrentTransfer transfer, string gamePath) =>
        string.Equals(
            Path.GetFullPath(Path.Combine(transfer.SavePath, transfer.Name)).TrimEnd('\\', '/'),
            Path.GetFullPath(gamePath).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts, or confirms, seeding of one installation. Data is read from where the game is installed.</summary>
    /// <returns>False when nothing was started: seeding is off, or a repair or update is working on the game.</returns>
    public async Task<bool> StartAsync(Installation installation, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            await StopAsync(installation, ct).ConfigureAwait(false);
            return false;
        }
        // A repair or update owns the transfer of that game and must be allowed to write. Never make it upload-only.
        if (await HasActiveDownloadAsync(installation, ct).ConfigureAwait(false)) return false;
        if (await _db.FindSourceCopyAsync(installation.ContentHash, ct).ConfigureAwait(false) is { } copy)
            return await StartSourceAsync(copy, ct).ConfigureAwait(false); // handed out from there, the copy that is played is left alone
        if (IsPlaying(installation)) return false; // it starts when the game is closed

        var stored = await _db.GetManifestAsync(installation.ContentHash, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No manifest stored for {installation.ContentHash}.");
        if (stored.TorrentBytes is null)
            throw new InvalidOperationException($"No torrent metadata stored for {stored.Manifest.Name}, rescan the game folder.");

        var folder = Path.GetFileName(installation.InstallPath.TrimEnd('\\', '/'));
        if (!string.Equals(folder, stored.Manifest.FolderName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Folder {installation.InstallPath} is named '{folder}' but the game was recorded as '{stored.Manifest.FolderName}'. Rescan to register it again.");

        var transfer = _engine.Transfers.FirstOrDefault(t => string.Equals(t.InfoHash, stored.Manifest.TorrentInfoHash, StringComparison.OrdinalIgnoreCase));
        if (transfer is not null && !SeedsFrom(transfer, installation.InstallPath))
        {
            await _engine.RemoveAsync(transfer, ct).ConfigureAwait(false); // a source copy that is no longer intact: the copy that is played takes over
            transfer = null;
        }
        if (transfer is null)
        {
            var parent = Path.GetDirectoryName(installation.InstallPath.TrimEnd('\\', '/'))!;
            transfer = await _engine.AddAsync(stored.TorrentBytes, parent, installation.ResumeData, uploadOnly: true, cancellationToken: ct).ConfigureAwait(false);
        }
        transfer.UploadOnly = true; // a finished download becomes a seed in place, from now on it must never write
        if (transfer.IsStopped) transfer.Start();

        _log.LogInformation("Seed started: {Name} from {Path}", stored.Manifest.Name, installation.InstallPath);
        SeedEventRaised?.Invoke(this, new SeedEvent(SeedEventKind.Started, installation.ContentHash, installation.InstallPath, stored.Manifest.Name));
        return true;
    }

    /// <summary>
    /// The game's files changed. Re-hashes them so the seed stops claiming pieces that no longer match, and keeps offering the rest.
    /// A running seed does not notice a change on disk by itself, so without this it would keep advertising stale pieces.
    /// </summary>
    public async Task RecheckAsync(Installation installation, CancellationToken ct = default)
    {
        if (!Enabled || await HasActiveDownloadAsync(installation, ct).ConfigureAwait(false)) return;
        if (IsPlaying(installation))
        {
            lock (_playing) _stale.Add(installation.Id); // reading the folder now would hold its files open under the game
            return;
        }

        var stored = await _db.GetManifestAsync(installation.ContentHash, ct).ConfigureAwait(false);
        var transfer = FindTransfer(stored);
        if (transfer is null)
        {
            await StartAsync(installation, ct).ConfigureAwait(false); // adding it checks the files
            return;
        }
        if (!SeedsFrom(transfer, installation.InstallPath)) return; // handed out from the source copy, which did not change
        transfer.UploadOnly = true;
        transfer.ForceRecheck();
        _log.LogInformation("Seed re-checked after the files of {Name} changed", stored!.Manifest.Name);
    }

    /// <summary>What can be handed out of this game right now, or null when it is not being seeded.</summary>
    public SeedOffer? GetOffer(StoredManifest stored)
    {
        var transfer = FindTransfer(stored);
        if (transfer is null) return null;
        var s = transfer.GetStatus();
        return new SeedOffer(s.IsComplete, Math.Round(Math.Clamp(s.Progress, 0, 1) * 100, 1));
    }

    /// <summary>Which pieces of the game this PC has and can prove, or null when it is not being seeded.</summary>
    public bool[]? GetPiecesHave(StoredManifest stored) => FindTransfer(stored)?.GetPiecesHave();

    /// <summary>Peers connected to this game's seed right now, sending or not. Empty when it is not being seeded.</summary>
    public IReadOnlyList<PeerSnapshot> GetPeers(StoredManifest stored) => FindTransfer(stored)?.GetPeers() ?? [];

    private TorrentTransfer? FindTransfer(StoredManifest? stored) =>
        _engine.Transfers.FirstOrDefault(t => string.Equals(t.InfoHash, stored?.Manifest.TorrentInfoHash, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> HasActiveDownloadAsync(Installation installation, CancellationToken ct) =>
        (await _db.ListDownloadsAsync(ct).ConfigureAwait(false)).Any(d => d.IsActive && d.InstallationId == installation.Id);

    private async Task<bool> HasActiveDownloadAsync(string contentHash, CancellationToken ct) =>
        (await _db.ListDownloadsAsync(ct).ConfigureAwait(false)).Any(d => d.IsActive && d.ContentHash == contentHash);

    /// <summary>
    /// Stores resume data of every complete seed, so the next start does not have to re-hash the whole library.
    /// Call it now and then and on shutdown. A seed that cannot produce it in time is skipped and simply re-checked later.
    /// </summary>
    public async Task SaveResumeDataAsync(CancellationToken ct = default)
    {
        foreach (var inst in await _db.ListInstallationsAsync(ct).ConfigureAwait(false))
        {
            if (inst.State != InstallationState.Installed) continue;
            var stored = await _db.GetManifestAsync(inst.ContentHash, ct).ConfigureAwait(false);
            var transfer = _engine.Transfers.FirstOrDefault(t => string.Equals(t.InfoHash, stored?.Manifest.TorrentInfoHash, StringComparison.OrdinalIgnoreCase));
            // Only a complete seed has anything worth remembering. A partial one is re-checked anyway. One of the source copy is its own.
            if (transfer is null || !SeedsFrom(transfer, inst.InstallPath) || !transfer.GetStatus().IsComplete) continue;

            try
            {
                var data = await transfer.SaveResumeDataAsync(ct).ConfigureAwait(false);
                await _db.SaveInstallationResumeDataAsync(inst.Id, data, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                _log.LogDebug("No resume data for seed {Name}: {Reason}", stored!.Manifest.Name, ex.Message);
            }
        }
        foreach (var copy in await _db.ListSourceCopiesAsync(ct).ConfigureAwait(false))
        {
            if (copy.State != InstallationState.Installed) continue;
            var stored = await _db.GetManifestAsync(copy.ContentHash, ct).ConfigureAwait(false);
            var transfer = FindTransfer(stored);
            if (transfer is null || !SeedsFrom(transfer, copy.Path) || !transfer.GetStatus().IsComplete) continue;
            try { await _db.SaveSourceCopyResumeDataAsync(copy.Id, await transfer.SaveResumeDataAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                _log.LogDebug("No resume data for the source copy of {Name}: {Reason}", stored!.Manifest.Name, ex.Message);
            }
        }
    }

    /// <summary>Stops offering every game, for example when the user turns seeding off. Downloads keep running.</summary>
    public async Task StopAllAsync(CancellationToken ct = default)
    {
        foreach (var inst in await _db.ListInstallationsAsync(ct).ConfigureAwait(false))
            if (!await HasActiveDownloadAsync(inst, ct).ConfigureAwait(false)) await StopAsync(inst, ct).ConfigureAwait(false);
        foreach (var copy in await _db.ListSourceCopiesAsync(ct).ConfigureAwait(false))
            if (!await HasActiveDownloadAsync(copy.ContentHash, ct).ConfigureAwait(false)) await StopAsync(copy.ContentHash, ct).ConfigureAwait(false);
    }

    /// <summary>Stops offering the game, from whichever copy it was offered. Files are never touched.</summary>
    public Task StopAsync(Installation installation, CancellationToken ct = default) => StopAsync(installation.ContentHash, ct);

    /// <summary>
    /// Stops offering the game, from whichever copy it was offered, for example because a download of it is about to start and needs
    /// the transfer. Files are never touched. <see cref="RefreshAsync"/> puts the seed back.
    /// </summary>
    public async Task StopAsync(string contentHash, CancellationToken ct = default)
    {
        var stored = await _db.GetManifestAsync(contentHash, ct).ConfigureAwait(false);
        var transfer = FindTransfer(stored);
        if (transfer is null) return;
        await RemoveAsync(transfer, contentHash, Path.Combine(transfer.SavePath, transfer.Name), stored!.Manifest.Name, ct).ConfigureAwait(false);
    }

    private async Task RemoveAsync(TorrentTransfer transfer, string contentHash, string path, string name, CancellationToken ct)
    {
        await _engine.RemoveAsync(transfer, ct).ConfigureAwait(false);
        _log.LogInformation("Seed stopped: {Name}", name);
        SeedEventRaised?.Invoke(this, new SeedEvent(SeedEventKind.Stopped, contentHash, path, name));
    }
}
