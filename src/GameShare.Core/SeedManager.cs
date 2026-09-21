using GameShare.Core.Data;
using GameShare.Torrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameShare.Core;

public enum SeedEventKind { Started, Stopped }

public sealed record SeedEvent(SeedEventKind Kind, Installation Installation, string GameName);

/// <summary>
/// Offers verified installed games to other PCs. Only installations in the Installed state are ever seeded,
/// so a half-written or corrupted game is never served.
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

    /// <summary>
    /// Global switch. While false nothing is offered to other PCs: <see cref="StartAsync"/> stops the game's transfer
    /// instead of starting it, which also covers a download that has just finished.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Starts seeding every verified installation that has seeding enabled. Call once at agent start.</summary>
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        foreach (var inst in await _db.ListInstallationsAsync(ct).ConfigureAwait(false))
        {
            if (inst.State != InstallationState.Installed || !inst.Seeding) continue;
            try { await StartAsync(inst, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            {
                // One broken game must not stop the others from being offered.
                _log.LogWarning(ex, "Cannot seed installation {Path}", inst.InstallPath);
            }
        }
    }

    /// <summary>Starts, or confirms, seeding of one installation. Data is read from where the game is installed.</summary>
    public async Task<bool> StartAsync(Installation installation, CancellationToken ct = default)
    {
        if (installation.State != InstallationState.Installed)
            throw new InvalidOperationException($"Only verified installations can be seeded, {installation.InstallPath} is {installation.State}.");
        if (!Enabled)
        {
            await StopAsync(installation, ct).ConfigureAwait(false);
            return false;
        }

        var stored = await _db.GetManifestAsync(installation.ContentHash, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No manifest stored for {installation.ContentHash}.");
        if (stored.TorrentBytes is null)
            throw new InvalidOperationException($"No torrent metadata stored for {stored.Manifest.Name}, rescan the game folder.");

        var folder = Path.GetFileName(installation.InstallPath.TrimEnd('\\', '/'));
        if (!string.Equals(folder, stored.Manifest.FolderName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Folder {installation.InstallPath} is named '{folder}' but the game was recorded as '{stored.Manifest.FolderName}'. Rescan to register it again.");

        var transfer = _engine.Transfers.FirstOrDefault(t => string.Equals(t.InfoHash, stored.Manifest.TorrentInfoHash, StringComparison.OrdinalIgnoreCase));
        if (transfer is null)
        {
            var parent = Path.GetDirectoryName(installation.InstallPath.TrimEnd('\\', '/'))!;
            transfer = await _engine.AddAsync(stored.TorrentBytes, parent, installation.ResumeData, uploadOnly: true, cancellationToken: ct).ConfigureAwait(false);
        }
        transfer.UploadOnly = true; // a finished download becomes a seed in place, from now on it must never write
        if (transfer.IsStopped) transfer.Start();

        _log.LogInformation("Seed started: {Name} from {Path}", stored.Manifest.Name, installation.InstallPath);
        SeedEventRaised?.Invoke(this, new SeedEvent(SeedEventKind.Started, installation, stored.Manifest.Name));
        return true;
    }

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
            // Only a complete seed has anything worth remembering. A partial one is re-checked anyway.
            if (transfer is null || !transfer.GetStatus().IsComplete) continue;

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
    }

    /// <summary>Stops offering every installed game, for example when the user turns seeding off. Downloads keep running.</summary>
    public async Task StopAllAsync(CancellationToken ct = default)
    {
        foreach (var inst in await _db.ListInstallationsAsync(ct).ConfigureAwait(false))
            if (inst.State == InstallationState.Installed) await StopAsync(inst, ct).ConfigureAwait(false);
    }

    /// <summary>Stops offering the game. Files are never touched.</summary>
    public async Task StopAsync(Installation installation, CancellationToken ct = default)
    {
        var stored = await _db.GetManifestAsync(installation.ContentHash, ct).ConfigureAwait(false);
        var transfer = _engine.Transfers.FirstOrDefault(t => string.Equals(t.InfoHash, stored?.Manifest.TorrentInfoHash, StringComparison.OrdinalIgnoreCase));
        if (transfer is null) return;

        await _engine.RemoveAsync(transfer, ct).ConfigureAwait(false);
        _log.LogInformation("Seed stopped: {Name}", stored!.Manifest.Name);
        SeedEventRaised?.Invoke(this, new SeedEvent(SeedEventKind.Stopped, installation, stored.Manifest.Name));
    }
}
