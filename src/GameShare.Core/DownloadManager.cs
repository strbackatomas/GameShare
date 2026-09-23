using GameShare.Core.Data;
using GameShare.Protocol;
using GameShare.Storage;
using GameShare.Torrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameShare.Core;

public sealed record DownloadManagerOptions
{
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How often resume data of running downloads is written to the database.</summary>
    public TimeSpan ResumeSaveInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Free bytes on the drive of a path, or null when unknown. Replaceable for tests and network shares.</summary>
    public Func<string, long?>? FreeSpaceProvider { get; init; }
}

/// <summary>A download as the UI needs to show it: database state plus live transfer numbers.</summary>
public sealed record DownloadStatus(
    long Id,
    string ContentHash,
    string GameName,
    DownloadState State,
    long BytesDone,
    long BytesTotal,
    double Percent,
    long DownloadRate,
    long UploadRate,
    int Peers,
    TimeSpan? Eta,
    string? Error)
{
    /// <summary>Who the data comes from right now. Empty unless the transfer is running.</summary>
    public IReadOnlyList<PeerSnapshot> PeerDetails { get; init; } = [];

    /// <summary>Install, repair or update.</summary>
    public DownloadKind Kind { get; init; }

    /// <summary>When the download reached <see cref="DownloadState.Completed"/>. Null until then.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>How long it took, from start to <see cref="CompletedAt"/>. Null until then.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>The highest download speed seen while it ran, in bytes per second. Null until something was measured.</summary>
    public long? PeakDownloadRate { get; init; }
}

public enum DownloadEventKind { Started, Progress, Paused, Resumed, Completed, Failed, Cancelled }

public sealed record DownloadEvent(DownloadEventKind Kind, DownloadStatus Status);

/// <summary>
/// The install workflow. Takes a manifest and torrent that came from another PC, downloads straight into the
/// target folder, verifies the result against the manifest and only then records the game as installed.
/// Progress survives restarts through resume data kept in the database.
/// </summary>
public sealed class DownloadManager
{
    private sealed class Active(Download row, GameManifest manifest, TorrentTransfer transfer)
    {
        public Download Row { get; set; } = row;
        public GameManifest Manifest { get; } = manifest;
        public TorrentTransfer Transfer { get; } = transfer;
        public DateTimeOffset LastResumeSave { get; set; } = DateTimeOffset.UtcNow;
        public Task? Verification { get; set; }
        public TransferStatus? Last { get; set; }
        public long PeakDownloadRate { get; set; }

        /// <summary>
        /// Exponential moving average of <see cref="TransferStatus.DownloadRate"/>, used only for <see cref="DownloadStatus.Eta"/>.
        /// A torrent with few pieces (a small game) has a very noisy instant rate tick to tick, which would otherwise make the ETA
        /// swing between minutes and days from one poll to the next. The displayed download rate itself is left raw.
        /// </summary>
        public double SmoothedDownloadRate { get; set; }

        /// <summary>
        /// Orders resume saves against removing the transfer. A save that is still in flight when the torrent is removed
        /// leaves the library holding on to it, and adding it again then fails. Saves take this lock, removal retires first.
        /// </summary>
        public SemaphoreSlim SaveLock { get; } = new(1, 1);
        public bool Retired { get; set; }
        public string InstallPath => Path.GetFullPath(Path.Combine(Row.TargetRoot, Manifest.FolderName));
    }

    private readonly TorrentEngine _engine;
    private readonly GameShareDb _db;
    private readonly SeedManager _seeds;
    private readonly ILogger _log;
    private readonly DownloadManagerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, Active> _active = [];

    public DownloadManager(
        TorrentEngine engine, GameShareDb db, SeedManager seeds, ILogger<DownloadManager>? logger = null, DownloadManagerOptions? options = null)
    {
        _engine = engine;
        _db = db;
        _seeds = seeds;
        _log = logger ?? NullLogger<DownloadManager>.Instance;
        _options = options ?? new DownloadManagerOptions();
    }

    /// <summary>Raised on a background thread. Handlers must be quick and must not throw.</summary>
    public event EventHandler<DownloadEvent>? DownloadEventRaised;

    // ---- commands ----

    /// <summary>Starts installing a game version. Everything that can be checked up front is checked before a byte is written.</summary>
    /// <exception cref="InvalidDataException">The manifest is invalid or does not match the torrent.</exception>
    /// <exception cref="InvalidOperationException">Already installed, already downloading, or the target folder is in use.</exception>
    /// <exception cref="IOException">Not enough free disk space.</exception>
    public async Task<DownloadStatus> StartInstallAsync(GameManifest manifest, byte[] torrentBytes, string targetRoot, CancellationToken ct = default)
    {
        var problems = ManifestValidator.Validate(manifest);
        if (problems.Count > 0)
            throw new InvalidDataException($"Refusing manifest for '{manifest.Name}': {string.Join(" ", problems)}");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await _db.FindInstalledAsync(manifest.ContentHash, ct).ConfigureAwait(false);
            if (existing is not null)
                throw new InvalidOperationException($"{manifest.Name} is already installed at {existing.InstallPath}.");

            // A different version of the same game is an update, not a second, parallel install.
            foreach (var inst in await _db.ListInstallationsAsync(ct).ConfigureAwait(false))
            {
                var installedManifest = (await _db.GetManifestAsync(inst.ContentHash, ct).ConfigureAwait(false))?.Manifest;
                if (installedManifest?.GameId == manifest.GameId)
                    throw new InvalidOperationException(
                        $"{manifest.Name} is already installed as a different version at {inst.InstallPath}. Use update instead of installing it again.");
            }

            targetRoot = Path.GetFullPath(targetRoot);
            var installPath = Path.GetFullPath(Path.Combine(targetRoot, manifest.FolderName));
            if (Directory.Exists(installPath) && Directory.EnumerateFileSystemEntries(installPath).Any())
            {
                // A folder left by an earlier failed attempt of this same version may be retried, it is repaired by re-checking.
                bool retry = (await _db.ListDownloadsAsync(ct).ConfigureAwait(false))
                    .Any(d => d.ContentHash == manifest.ContentHash && string.Equals(Path.GetFullPath(d.TargetRoot), targetRoot, StringComparison.OrdinalIgnoreCase));
                if (!retry)
                    throw new InvalidOperationException(
                        $"Target folder {installPath} already exists and is not empty. GameShare will not overwrite a folder it did not create.");
            }

            long? free = (_options.FreeSpaceProvider ?? GetFreeSpace)(targetRoot);
            if (free is not null && free < manifest.TotalSize)
                throw new IOException($"Not enough free space on {Path.GetPathRoot(targetRoot)}: {manifest.Name} needs {manifest.TotalSize:N0} bytes, {free:N0} are free.");

            return await BeginAsync(manifest, torrentBytes, targetRoot, DownloadKind.Install, installationId: null, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Brings an installed game to another version of the same game. Only the pieces that differ are fetched, files the game
    /// changes itself (volatile) are left alone, and old files that the new version no longer has are removed when unmodified.
    /// The game is not offered to other PCs while this runs.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not the same game, already at that version, or the folder names differ.</exception>
    public async Task<DownloadStatus> StartUpdateAsync(long installationId, GameManifest manifest, byte[] torrentBytes, CancellationToken ct = default)
    {
        var problems = ManifestValidator.Validate(manifest);
        if (problems.Count > 0)
            throw new InvalidDataException($"Refusing manifest for '{manifest.Name}': {string.Join(" ", problems)}");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var inst = await _db.GetInstallationAsync(installationId, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Installation {installationId} does not exist.");
            var old = (await _db.GetManifestAsync(inst.ContentHash, ct).ConfigureAwait(false))?.Manifest
                ?? throw new InvalidOperationException($"No manifest is stored for the installed version {inst.ContentHash}.");

            if (manifest.GameId != old.GameId)
                throw new InvalidOperationException($"'{manifest.Name}' is not another version of '{old.Name}'.");
            if (manifest.ContentHash == old.ContentHash)
                throw new InvalidOperationException($"{old.Name} is already at this version. Use repair to restore its files.");
            if (await _db.FindInstalledAsync(manifest.ContentHash, ct).ConfigureAwait(false) is { } already)
                throw new InvalidOperationException($"{manifest.Name} is already installed at {already.InstallPath}.");

            var folder = Path.GetFileName(inst.InstallPath.TrimEnd('\\', '/'));
            if (!string.Equals(folder, manifest.FolderName, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The new version installs into a folder named '{manifest.FolderName}' but this game is in '{folder}'. GameShare will not move it.");
            await RequireNoOtherWorkAsync(inst, ct).ConfigureAwait(false);

            var root = Path.GetDirectoryName(inst.InstallPath.TrimEnd('\\', '/'))!;
            long needed = Math.Max(0, manifest.TotalSize - old.TotalSize) + manifest.TotalSize / 20; // growth plus 5 percent for rewritten pieces
            RequireFreeSpace(root, needed, manifest.Name);

            return await BeginInPlaceAsync(inst, manifest, torrentBytes, root, DownloadKind.Update, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Restores the content files of an installed game to what its manifest says, fetching the damaged pieces from other PCs.
    /// This is the only thing that overwrites files a game or user changed, so it is always an explicit request.
    /// Volatile files are not part of the game and are never touched.
    /// </summary>
    public async Task<DownloadStatus> StartRepairAsync(long installationId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var inst = await _db.GetInstallationAsync(installationId, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Installation {installationId} does not exist.");
            var stored = await _db.GetManifestAsync(inst.ContentHash, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"No manifest is stored for {inst.ContentHash}.");
            if (stored.TorrentBytes is null)
                throw new InvalidOperationException($"No torrent metadata is stored for {stored.Manifest.Name}.");

            var folder = Path.GetFileName(inst.InstallPath.TrimEnd('\\', '/'));
            if (!string.Equals(folder, stored.Manifest.FolderName, StringComparison.Ordinal))
                throw new InvalidOperationException($"Folder {inst.InstallPath} is named '{folder}' but the game was recorded as '{stored.Manifest.FolderName}'.");
            await RequireNoOtherWorkAsync(inst, ct).ConfigureAwait(false);

            var root = Path.GetDirectoryName(inst.InstallPath.TrimEnd('\\', '/'))!;
            if (!Directory.Exists(inst.InstallPath)) RequireFreeSpace(root, stored.Manifest.TotalSize, stored.Manifest.Name);

            return await BeginInPlaceAsync(inst, stored.Manifest, stored.TorrentBytes, root, DownloadKind.Repair, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Takes the game out of service, then starts the transfer. If starting fails the game goes back the way it was. Caller holds the gate.</summary>
    private async Task<DownloadStatus> BeginInPlaceAsync(
        Installation inst, GameManifest manifest, byte[] torrentBytes, string root, DownloadKind kind, CancellationToken ct)
    {
        // While files are being rewritten nothing may be served from them.
        await _seeds.StopAsync(inst, ct).ConfigureAwait(false);
        await _db.SetInstallationStateAsync(inst.Id, InstallationState.Invalid, ct).ConfigureAwait(false);
        await _db.SaveInstallationResumeDataAsync(inst.Id, null, ct).ConfigureAwait(false);
        try
        {
            return await BeginAsync(manifest, torrentBytes, root, kind, inst.Id, ct).ConfigureAwait(false);
        }
        catch
        {
            if (inst.State == InstallationState.Installed)
            {
                await _db.SetInstallationStateAsync(inst.Id, InstallationState.Installed, CancellationToken.None).ConfigureAwait(false);
                try { await _seeds.StartAsync((await _db.GetInstallationAsync(inst.Id, CancellationToken.None).ConfigureAwait(false))!, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException) { _log.LogWarning(ex, "Could not resume seeding {Name} after a failed {Kind}", manifest.Name, kind); }
            }
            throw;
        }
    }

    private async Task RequireNoOtherWorkAsync(Installation inst, CancellationToken ct)
    {
        if ((await _db.ListDownloadsAsync(ct).ConfigureAwait(false)).Any(d => d.IsActive && d.InstallationId == inst.Id))
            throw new InvalidOperationException($"{inst.InstallPath} is already being repaired or updated.");
    }

    private void RequireFreeSpace(string root, long needed, string gameName)
    {
        long? free = (_options.FreeSpaceProvider ?? GetFreeSpace)(root);
        if (free is not null && free < needed)
            throw new IOException($"Not enough free space on {Path.GetPathRoot(root)}: {gameName} needs about {needed:N0} bytes, {free:N0} are free.");
    }

    /// <summary>Common start of every download: store the manifest, check the torrent belongs to it, begin transferring. Caller holds the gate.</summary>
    private async Task<DownloadStatus> BeginAsync(
        GameManifest manifest, byte[] torrentBytes, string targetRoot, DownloadKind kind, long? installationId, CancellationToken ct)
    {
        await _db.SaveManifestAsync(manifest, torrentBytes, ct).ConfigureAwait(false);
        var row = await _db.CreateDownloadAsync(manifest.ContentHash, targetRoot, kind, installationId, ct).ConfigureAwait(false);

        TorrentTransfer transfer;
        try
        {
            transfer = await _engine.AddAsync(torrentBytes, targetRoot, cancellationToken: ct).ConfigureAwait(false);
            if (!string.Equals(transfer.InfoHash, manifest.TorrentInfoHash, StringComparison.OrdinalIgnoreCase)
                || transfer.Name != manifest.FolderName || transfer.TotalSize != manifest.TotalSize)
            {
                await _engine.RemoveAsync(transfer, ct).ConfigureAwait(false);
                throw new InvalidDataException(
                    $"The torrent for '{manifest.Name}' does not match its manifest (info hash {transfer.InfoHash} vs {manifest.TorrentInfoHash}).");
            }
        }
        catch (Exception ex)
        {
            await _db.UpdateDownloadAsync(row.Id, DownloadState.Failed, ex.Message, ct: CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        transfer.Start();
        await _db.UpdateDownloadAsync(row.Id, DownloadState.Downloading, ct: ct).ConfigureAwait(false);
        row = (await _db.GetDownloadAsync(row.Id, ct).ConfigureAwait(false))!;

        var active = new Active(row, manifest, transfer);
        _active[row.Id] = active;

        _log.LogInformation("Torrent started: {Kind} {Name} into {Path}", kind, manifest.Name, active.InstallPath);
        var status = ToStatus(active);
        Publish(DownloadEventKind.Started, status);
        return status;
    }

    /// <summary>Stops transferring and keeps everything needed to continue. Survives restarts.</summary>
    public async Task PauseAsync(long id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_active.TryGetValue(id, out var a))
            {
                var row = await RequireAsync(id, ct).ConfigureAwait(false);
                if (row.State == DownloadState.Paused) return;
                throw new InvalidOperationException($"Download {id} is {row.State} and cannot be paused.");
            }

            a.Transfer.Stop();
            await SaveResumeAsync(a, ct).ConfigureAwait(false);
            await RetireAsync(a).ConfigureAwait(false);
            await _engine.RemoveAsync(a.Transfer, CancellationToken.None).ConfigureAwait(false);
            _active.Remove(id);
            await _db.UpdateDownloadAsync(id, DownloadState.Paused, ct: ct).ConfigureAwait(false);

            _log.LogInformation("Download paused: {Name}", a.Manifest.Name);
            Publish(DownloadEventKind.Paused, await StatusAsync(id, a.Manifest, live: null, ct).ConfigureAwait(false));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Continues a paused download, or retries a failed one from whatever is already on disk.</summary>
    public async Task ResumeAsync(long id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var row = await RequireAsync(id, ct).ConfigureAwait(false);
            if (_active.ContainsKey(id)) return;
            if (row.State is not (DownloadState.Paused or DownloadState.Failed))
                throw new InvalidOperationException($"Download {id} is {row.State} and cannot be resumed.");

            var active = await AttachAsync(row, ct).ConfigureAwait(false);
            await _db.UpdateDownloadAsync(id, DownloadState.Downloading, ct: ct).ConfigureAwait(false);
            active.Row = (await _db.GetDownloadAsync(id, ct).ConfigureAwait(false))!;

            _log.LogInformation("Download resumed: {Name}", active.Manifest.Name);
            Publish(DownloadEventKind.Resumed, ToStatus(active));
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Removes an installed game: stops offering it, optionally deletes its files, and forgets it was installed.
    /// The stored manifest is kept, other installations or another PC may still need it.
    /// </summary>
    public async Task UninstallAsync(long installationId, bool deleteFiles, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var inst = await _db.GetInstallationAsync(installationId, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Installation {installationId} does not exist.");
            await RequireNoOtherWorkAsync(inst, ct).ConfigureAwait(false);

            await _seeds.StopAsync(inst, ct).ConfigureAwait(false);
            if (deleteFiles && Directory.Exists(inst.InstallPath))
                await DeleteFolderAsync(inst.InstallPath, ct).ConfigureAwait(false);
            await _db.DeleteInstallationAsync(inst.Id, ct).ConfigureAwait(false);
            _log.LogInformation("Uninstalled {Path} (files deleted: {Deleted})", inst.InstallPath, deleteFiles);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Deletes a folder just stopped seeding. A file in it can still be briefly locked right after: the engine's own
    /// removal is asynchronous, and Windows itself can take a moment to release a handle after the last reader of a
    /// large file closes it. Retried a few times before giving up, so an uninstall does not routinely leave a
    /// half-deleted folder behind for a later install to trip over as "already exists".
    /// </summary>
    private async Task DeleteFolderAsync(string path, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { Directory.Delete(path, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 5)
                {
                    _log.LogWarning(ex, "Could not delete all files of {Path} after {Attempts} tries, the game is uninstalled anyway", path, attempt);
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Forgets the download. Downloaded files are kept unless <paramref name="deleteFiles"/> is set.</summary>
    public async Task CancelAsync(long id, bool deleteFiles = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var row = await RequireAsync(id, ct).ConfigureAwait(false);
            var stored = await _db.GetManifestAsync(row.ContentHash, ct).ConfigureAwait(false);
            var status = await StatusAsync(id, stored!.Manifest, live: null, ct).ConfigureAwait(false);

            if (_active.Remove(id, out var a))
            {
                await RetireAsync(a).ConfigureAwait(false);
                a.Transfer.Stop();
                await _engine.RemoveAsync(a.Transfer, CancellationToken.None).ConfigureAwait(false);
            }
            if (deleteFiles && row.Kind == DownloadKind.Install)
            {
                await DeletePartialFilesAsync(row, stored.Manifest, ct).ConfigureAwait(false);
                await _db.DeleteDownloadAsync(id, ct).ConfigureAwait(false); // forgotten either way: a kept folder is someone else's install now, not this row's to track
            }
            else if (deleteFiles)
            {
                _log.LogInformation("Files of {Name} are kept: cancelling a {Kind} never deletes an installed game", stored.Manifest.Name, row.Kind);
                await _db.DeleteDownloadAsync(id, ct).ConfigureAwait(false);
            }
            else if (row.Kind == DownloadKind.Install)
                // The partial files stay on disk, so this download must stay remembered too: it is what lets a later
                // install into the same folder recognise them as its own and repair them instead of refusing the folder as foreign.
                await _db.UpdateDownloadAsync(id, DownloadState.Failed, "Cancelled", ct: ct).ConfigureAwait(false);
            else
                await _db.DeleteDownloadAsync(id, ct).ConfigureAwait(false); // an update or repair targets an existing installation, nothing is orphaned by forgetting it

            _log.LogInformation("Download cancelled: {Name} (files deleted: {Deleted})", stored.Manifest.Name, deleteFiles);
            Publish(DownloadEventKind.Cancelled, status with { State = DownloadState.Failed, Error = "Cancelled" });
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Deletes the folder this download's partial files are in, unless something else has since made it a real
    /// installed game (for example another download reused the folder after this one was abandoned) — that game's
    /// files are kept, only this download's own bookkeeping is forgotten by the caller.
    /// </summary>
    private async Task DeletePartialFilesAsync(Download row, GameManifest manifest, CancellationToken ct)
    {
        var path = Path.GetFullPath(Path.Combine(row.TargetRoot, manifest.FolderName));
        bool registered = (await _db.ListInstallationsAsync(ct).ConfigureAwait(false))
            .Any(i => string.Equals(i.InstallPath, path, StringComparison.OrdinalIgnoreCase));
        if (registered)
        {
            _log.LogInformation("Not deleting {Path}, it belongs to an installed game now. Forgetting this old download anyway.", path);
            return;
        }
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    // ---- queries ----

    public async Task<IReadOnlyList<DownloadStatus>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<DownloadStatus>();
        foreach (var row in await _db.ListDownloadsAsync(ct).ConfigureAwait(false))
        {
            var stored = await _db.GetManifestAsync(row.ContentHash, ct).ConfigureAwait(false);
            if (stored is null) continue;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { result.Add(_active.TryGetValue(row.Id, out var a) ? ToStatus(a) : ToStatus(row, stored.Manifest, null)); }
            finally { _gate.Release(); }
        }
        return result;
    }

    public async Task<DownloadStatus?> GetAsync(long id, CancellationToken ct = default) =>
        (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(d => d.Id == id);

    // ---- lifecycle ----

    /// <summary>Continues every download that was running when the agent last stopped. Paused ones stay paused.</summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var row in await _db.ListDownloadsAsync(ct).ConfigureAwait(false))
            {
                if (!row.IsActive || row.State == DownloadState.Paused || _active.ContainsKey(row.Id)) continue;
                try
                {
                    var a = await AttachAsync(row, ct).ConfigureAwait(false);
                    _log.LogInformation("Download recovered after restart: {Name} at {Progress:P0}", a.Manifest.Name, row.BytesDone / (double)Math.Max(1, a.Manifest.TotalSize));
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
                {
                    _log.LogError(ex, "Cannot recover download {Id}", row.Id);
                    await _db.UpdateDownloadAsync(row.Id, DownloadState.Failed, ex.Message, ct: ct).ConfigureAwait(false);
                }
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Drives progress, persistence and completion. Run for the lifetime of the agent. Saves resume data on shutdown.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await TickAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogError(ex, "Download tick failed, will retry"); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }

        await SaveAllResumeDataAsync().ConfigureAwait(false);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var dueForResumeSave = new List<Active>();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var a in _active.Values.ToList())
            {
                if (a.Verification is not null) continue;

                var s = a.Last = a.Transfer.GetStatus();
                if (s.DownloadRate > a.PeakDownloadRate) a.PeakDownloadRate = s.DownloadRate;
                a.SmoothedDownloadRate = a.SmoothedDownloadRate <= 0
                    ? s.DownloadRate
                    : (0.25 * s.DownloadRate) + (0.75 * a.SmoothedDownloadRate);
                if (s.State == TransferState.Error)
                {
                    await FailAsync(a, $"The transfer engine reported an error: {s}", ct).ConfigureAwait(false);
                }
                else if (s.IsComplete)
                {
                    await _db.UpdateDownloadAsync(a.Row.Id, DownloadState.Verifying, bytesDone: s.BytesDone, ct: ct).ConfigureAwait(false);
                    a.Row = (await _db.GetDownloadAsync(a.Row.Id, ct).ConfigureAwait(false))!;
                    _log.LogInformation("Download finished, verifying against manifest: {Name}", a.Manifest.Name);
                    a.Verification = Task.Run(() => VerifyAsync(a), CancellationToken.None);
                }
                else
                {
                    if (DateTimeOffset.UtcNow - a.LastResumeSave >= _options.ResumeSaveInterval)
                    {
                        a.LastResumeSave = DateTimeOffset.UtcNow; // also when the save fails, so a stuck one is not retried every tick
                        dueForResumeSave.Add(a);
                        _log.LogInformation("Download progress: {Percent:F1}% {Name}", s.Progress * 100, a.Manifest.Name);
                    }
                    Publish(DownloadEventKind.Progress, ToStatus(a));
                }
            }
        }
        finally { _gate.Release(); }

        // Outside the gate on purpose. The library can take seconds to answer, and pause, cancel and status must not wait for it.
        foreach (var a in dueForResumeSave) await SaveResumeAsync(a, ct).ConfigureAwait(false);
    }

    /// <summary>Full manifest check. Runs off the tick loop because hashing a large game takes minutes.</summary>
    private async Task VerifyAsync(Active a)
    {
        VerificationResult result;
        try
        {
            result = await ManifestVerifier.VerifyAsync(a.Manifest, a.InstallPath, VerifyMode.Full).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { await FailAsync(a, $"Verification could not run: {ex.Message}", CancellationToken.None).ConfigureAwait(false); }
            finally { _gate.Release(); }
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_active.ContainsKey(a.Row.Id)) return; // cancelled while verifying

            if (!result.IsValid)
            {
                var first = result.Missing.Concat(result.SizeMismatch).Concat(result.HashMismatch).First();
                await FailAsync(a, $"Verification failed: {result}. First problem: {first}. Retry will repair it.", CancellationToken.None).ConfigureAwait(false);
                return;
            }

            Installation installation;
            switch (a.Row.Kind)
            {
                case DownloadKind.Repair:
                case DownloadKind.Update:
                    var current = a.Row.InstallationId is { } id ? await _db.GetInstallationAsync(id).ConfigureAwait(false) : null;
                    if (current is null)
                    {
                        await FailAsync(a, "The installation this download belongs to no longer exists.", CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    if (a.Row.Kind == DownloadKind.Update)
                    {
                        var oldManifest = (await _db.GetManifestAsync(current.ContentHash).ConfigureAwait(false))?.Manifest;
                        if (oldManifest is not null) await RemoveObsoleteFilesAsync(oldManifest, a.Manifest, a.InstallPath).ConfigureAwait(false);
                        await _db.UpdateInstallationContentAsync(current.Id, a.Manifest.ContentHash).ConfigureAwait(false);
                    }
                    else
                    {
                        await _db.SetInstallationStateAsync(current.Id, InstallationState.Installed).ConfigureAwait(false);
                        await _db.SaveInstallationResumeDataAsync(current.Id, null).ConfigureAwait(false);
                    }
                    installation = (await _db.GetInstallationAsync(current.Id).ConfigureAwait(false))!;
                    break;
                default:
                    installation = await _db.AddInstallationAsync(a.Manifest.ContentHash, a.InstallPath, InstallationState.Installed, ct: CancellationToken.None).ConfigureAwait(false);
                    break;
            }
            await _db.UpdateDownloadAsync(
                a.Row.Id, DownloadState.Completed, bytesDone: a.Manifest.TotalSize, peakDownloadRate: a.PeakDownloadRate, ct: CancellationToken.None).ConfigureAwait(false);
            _active.Remove(a.Row.Id);

            _log.LogInformation("Download completed: {Kind} {Name} verified at {Path}", a.Row.Kind, a.Manifest.Name, a.InstallPath);
            Publish(DownloadEventKind.Completed, ToStatus((await _db.GetDownloadAsync(a.Row.Id).ConfigureAwait(false))!, a.Manifest, null));

            // The transfer is already complete and in the engine, so this only registers it as a seed.
            await _seeds.StartAsync(installation).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// After an update, removes files the old version had and the new one does not, so old archives do not pile up.
    /// Only files that are still exactly what the old manifest says are deleted. Anything the user or a game changed is kept.
    /// </summary>
    private async Task RemoveObsoleteFilesAsync(GameManifest oldManifest, GameManifest newManifest, string installPath)
    {
        var stillNeeded = new HashSet<string>(newManifest.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var volatileFiles = VolatileMatcher.Create(newManifest.VolatilePatterns);
        int removed = 0, kept = 0;

        foreach (var f in oldManifest.Files.Where(f => !stillNeeded.Contains(f.Path) && !volatileFiles.IsMatch(f.Path)))
        {
            var full = Path.Combine(installPath, f.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var info = new FileInfo(full);
                if (!info.Exists) continue;
                if (info.Length != f.Size || await ManifestVerifier.HashFileAsync(full).ConfigureAwait(false) != f.Hash) { kept++; continue; }

                info.Delete();
                removed++;
                for (var dir = info.Directory; dir is not null && dir.FullName.Length > installPath.Length && !dir.EnumerateFileSystemInfos().Any(); dir = dir.Parent)
                    dir.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                kept++;
                _log.LogWarning("Could not remove obsolete file {Path}: {Reason}", full, ex.Message);
            }
        }
        _log.LogInformation("Update of {Name}: removed {Removed} obsolete files, kept {Kept} that were modified or in use", newManifest.Name, removed, kept);
    }

    /// <summary>Marks failed and stops uploading, so possibly corrupt data is never served to others. Caller holds the gate.</summary>
    private async Task FailAsync(Active a, string error, CancellationToken ct)
    {
        await RetireAsync(a).ConfigureAwait(false);
        a.Transfer.Stop();
        await _engine.RemoveAsync(a.Transfer, CancellationToken.None).ConfigureAwait(false);
        _active.Remove(a.Row.Id);
        await _db.UpdateDownloadAsync(a.Row.Id, DownloadState.Failed, error, ct: ct).ConfigureAwait(false);
        _log.LogError("Download failed: {Name}: {Error}", a.Manifest.Name, error);
        Publish(DownloadEventKind.Failed, ToStatus((await _db.GetDownloadAsync(a.Row.Id, ct).ConfigureAwait(false))!, a.Manifest, null));
    }

    private async Task SaveAllResumeDataAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var a in _active.Values)
            {
                if (await SaveResumeAsync(a, CancellationToken.None).ConfigureAwait(false))
                    _log.LogInformation("Saved resume data for {Name} before shutdown", a.Manifest.Name);
            }
        }
        finally { _gate.Release(); }
    }

    // ---- helpers ----

    /// <summary>
    /// Asks the transfer for resume data and stores it. Resume data is an optimisation: if the library cannot produce it in time,
    /// for example while it is still checking files, carry on and the next start re-checks the files instead.
    /// </summary>
    /// <returns>True when data was stored.</returns>
    private async Task<bool> SaveResumeAsync(Active a, CancellationToken ct)
    {
        await a.SaveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (a.Retired) return false; // being removed, nothing more may be asked of the transfer

            byte[] data;
            try { data = await a.Transfer.SaveResumeDataAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning("No resume data for {Name}: {Reason}", a.Manifest.Name, ex.Message);
                return false;
            }

            // Stored inside the lock, so a slow periodic save can never overwrite the newer data written by a pause.
            await _db.SaveResumeDataAsync(a.Row.Id, data, a.Transfer.GetStatus().BytesDone, ct).ConfigureAwait(false);
            return true;
        }
        finally { a.SaveLock.Release(); }
    }

    /// <summary>Waits for a save that is in flight and makes sure no other one starts. Call before removing the transfer.</summary>
    private static async Task RetireAsync(Active a)
    {
        await a.SaveLock.WaitAsync().ConfigureAwait(false);
        a.Retired = true;
        a.SaveLock.Release();
    }

    /// <summary>Re-adds a stored download to the engine with its resume data and starts it. Caller holds the gate.</summary>
    private async Task<Active> AttachAsync(Download row, CancellationToken ct)
    {
        var stored = await _db.GetManifestAsync(row.ContentHash, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Manifest {row.ContentHash} for download {row.Id} is missing from the database.");
        if (stored.TorrentBytes is null)
            throw new InvalidOperationException($"Torrent metadata for {stored.Manifest.Name} is missing from the database.");

        var transfer = await _engine.AddAsync(stored.TorrentBytes, row.TargetRoot, row.ResumeData, cancellationToken: ct).ConfigureAwait(false);
        transfer.Start();
        var active = new Active(row, stored.Manifest, transfer);
        _active[row.Id] = active;
        return active;
    }

    private async Task<Download> RequireAsync(long id, CancellationToken ct) =>
        await _db.GetDownloadAsync(id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Download {id} does not exist.");

    private async Task<DownloadStatus> StatusAsync(long id, GameManifest manifest, TransferStatus? live, CancellationToken ct) =>
        ToStatus((await _db.GetDownloadAsync(id, ct).ConfigureAwait(false))!, manifest, live);

    private static DownloadStatus ToStatus(Active a) =>
        ToStatus(a.Row, a.Manifest, a.Last ?? a.Transfer.GetStatus(), a.SmoothedDownloadRate) with { PeerDetails = a.Transfer.GetPeers() };

    private static DownloadStatus ToStatus(Download row, GameManifest manifest, TransferStatus? live, double? smoothedDownloadRate = null)
    {
        long done = live?.BytesDone ?? (row.State == DownloadState.Completed ? manifest.TotalSize : row.BytesDone);

        TimeSpan? eta = live is { State: TransferState.Downloading }
            ? EstimateEta(manifest.TotalSize - done, smoothedDownloadRate ?? live.DownloadRate)
            : null;

        return new DownloadStatus(
            row.Id, row.ContentHash, manifest.Name, row.State, done, manifest.TotalSize,
            manifest.TotalSize == 0 ? 100 : Math.Min(100, done * 100.0 / manifest.TotalSize),
            live?.DownloadRate ?? 0, live?.UploadRate ?? 0, live?.PeerCount ?? 0, eta, row.Error)
        {
            Kind = row.Kind, CompletedAt = row.CompletedAt, PeakDownloadRate = row.PeakDownloadRate,
            Duration = row.CompletedAt - row.CreatedAt,
        };
    }

    private void Publish(DownloadEventKind kind, DownloadStatus status)
    {
        try { DownloadEventRaised?.Invoke(this, new DownloadEvent(kind, status)); }
        catch (Exception ex) { _log.LogError(ex, "A download event handler threw for {Kind}", kind); }
    }

    /// <summary>
    /// How long <paramref name="remainingBytes"/> will take at <paramref name="bytesPerSecond"/>, or null when that cannot be said.
    /// After a stall the smoothed rate decays toward zero without ever quite reaching it, so dividing by a near-zero rate can ask
    /// for an ETA of thousands of years, which <see cref="TimeSpan"/> cannot hold. Internal for its own test, otherwise only
    /// reached through <see cref="ToStatus(Active)"/>.
    /// </summary>
    internal static TimeSpan? EstimateEta(long remainingBytes, double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return null;
        double seconds = remainingBytes / bytesPerSecond;
        return seconds >= 0 && seconds <= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>Free bytes on the drive of <paramref name="path"/>, or null when unknown (a network share, or the drive can't be statted).</summary>
    public static long? GetFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null; // network share or unknown drive: skip the check rather than block the install
        }
    }
}
