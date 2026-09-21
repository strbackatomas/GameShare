using GameShare.Core.Data;
using GameShare.Storage;
using GameShare.Torrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameShare.Core;

/// <summary>What a scan did. Failures are listed with the folder and reason, never hidden.</summary>
/// <param name="Damaged">Installed games whose files changed or went missing. They are no longer offered until repaired or re-registered.</param>
public sealed record ScanSummary(
    int Added, int Unchanged, int Skipped, IReadOnlyList<string> Errors, IReadOnlyList<string> MissingRoots, IReadOnlyList<string> Damaged);

/// <summary>A game version known to this PC, with its local installation if there is one.</summary>
public sealed record LibraryGame(StoredManifest Stored, Installation? Installation);

/// <summary>What differs between an installed game and its manifest.</summary>
/// <param name="Modified">Content files whose size or hash no longer match.</param>
/// <param name="Missing">Content files that are gone.</param>
/// <param name="Added">New files that are not game content and not covered by a volatile pattern.</param>
/// <param name="SuggestedPatterns">Volatile patterns an admin could add so these files stop counting. Never applied automatically.</param>
public sealed record GameChanges(
    IReadOnlyList<string> Modified, IReadOnlyList<string> Missing, IReadOnlyList<string> Added, IReadOnlyList<string> SuggestedPatterns)
{
    public bool IsIntact => Modified.Count == 0 && Missing.Count == 0;
}

public enum InstallationChangeKind
{
    /// <summary>The same installation became Installed or Invalid.</summary>
    StateChanged,
    /// <summary>A folder was registered again with different content. Previous is the old record.</summary>
    Replaced,
}

public sealed record InstallationChange(InstallationChangeKind Kind, Installation Current, Installation? Previous);

/// <summary>Finds games in the configured roots and records what they are. Identity comes from content, never from the folder name.</summary>
public sealed class GameLibrary
{
    private readonly GameShareDb _db;
    private readonly ILogger _log;

    public GameLibrary(GameShareDb db, ILogger<GameLibrary>? logger = null)
    {
        _db = db;
        _log = logger ?? NullLogger<GameLibrary>.Instance;
    }

    /// <summary>Raised for every newly registered game, on the scanning thread.</summary>
    public event EventHandler<LibraryGame>? GameDiscovered;

    /// <summary>Raised when an installation becomes Installed or Invalid, or is registered again with new content.</summary>
    public event EventHandler<InstallationChange>? InstallationChanged;

    public async Task<ScanSummary> ScanAsync(IEnumerable<string> roots, CancellationToken cancellationToken = default)
    {
        var missingRoots = new List<string>();
        var candidates = GameRootScanner.FindCandidateDirectories(roots, missingRoots);
        foreach (var root in missingRoots) _log.LogWarning("Game root does not exist and was skipped: {Root}", root);

        var busy = await PathsBeingInstalledAsync(cancellationToken).ConfigureAwait(false);
        int added = 0, unchanged = 0, skipped = 0;
        var errors = new List<string>();
        var damaged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(dir);
            seen.Add(path);

            if (busy.Contains(path))
            {
                _log.LogDebug("Skipping {Path}, a download is writing into it", path);
                skipped++;
                continue;
            }

            try
            {
                switch (await CheckFolderAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    case FolderOutcome.Registered: added++; break;
                    case FolderOutcome.Unchanged: unchanged++; break;
                    case FolderOutcome.Damaged: damaged.Add(path); break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                errors.Add($"{path}: {ex.Message}");
                _log.LogWarning(ex, "Could not scan game folder {Path}", path);
            }
        }

        // An installed game whose folder has vanished, for example an unplugged drive or a deleted folder, is not offered any more.
        var inScannedRoots = roots.Select(r => Path.GetFullPath(r)).ToList();
        foreach (var inst in await _db.ListInstallationsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (inst.State != InstallationState.Installed || seen.Contains(inst.InstallPath) || busy.Contains(inst.InstallPath)) continue;
            if (!inScannedRoots.Any(r => inst.InstallPath.StartsWith(r, StringComparison.OrdinalIgnoreCase))) continue; // its root is not scanned now
            await MarkAsync(inst, InstallationState.Invalid, cancellationToken).ConfigureAwait(false);
            damaged.Add(inst.InstallPath);
            _log.LogWarning("Game folder is gone, marked as damaged: {Path}", inst.InstallPath);
        }

        _log.LogInformation("Scan finished: {Added} new, {Unchanged} unchanged, {Skipped} skipped, {Damaged} damaged, {Errors} failed",
            added, unchanged, skipped, damaged.Count, errors.Count);
        return new ScanSummary(added, unchanged, skipped, errors, missingRoots, damaged);
    }

    private enum FolderOutcome { Registered, Unchanged, Damaged }

    private async Task<FolderOutcome> CheckFolderAsync(string path, CancellationToken ct)
    {
        var known = await FindByPathAsync(path, ct).ConfigureAwait(false);
        if (known is null)
        {
            await RebuildAsync(path, null, null, null, ct).ConfigureAwait(false);
            return FolderOutcome.Registered;
        }

        var stored = await _db.GetManifestAsync(known.ContentHash, ct).ConfigureAwait(false);
        var definition = await GameDefinitionFile.TryLoadAsync(path, ct).ConfigureAwait(false);
        var patterns = VolatileRules.Resolve(definition, stored?.Manifest.VolatilePatterns);

        // Existence and size only. Cheap enough to run on every scan, catches deleted and truncated files.
        bool intact = stored is not null
            && (await ManifestVerifier.VerifyAsync(stored.Manifest, path, VerifyMode.Quick, cancellationToken: ct).ConfigureAwait(false)).IsValid;

        if (intact && !VolatileRules.SameSet(stored!.Manifest.VolatilePatterns, patterns.Patterns))
        {
            // The definition file gained patterns, so files that used to count as content no longer do.
            await RebuildAsync(path, known, stored.Manifest.VolatilePatterns, null, ct).ConfigureAwait(false);
            return FolderOutcome.Registered;
        }
        if (intact)
        {
            if (known.State == InstallationState.Installed) return FolderOutcome.Unchanged;

            // Marked damaged earlier and the sizes look right again, for example a drive was plugged back in. Sizes prove little,
            // a game that rewrote a file keeps its size, so only a full verification may put it back in service.
            var changes = await CheckAsync(known.ContentHash, ct).ConfigureAwait(false);
            return changes.IsIntact ? FolderOutcome.Unchanged : FolderOutcome.Damaged;
        }

        // Changed on disk. Do not quietly turn it into a new version: every PC that plays would end up with its own variant.
        if (known.State == InstallationState.Installed)
        {
            await MarkAsync(known, InstallationState.Invalid, ct).ConfigureAwait(false);
            _log.LogWarning("Game files changed or missing, marked as damaged: {Path}. Repair it, or register the current files as a new version.", path);
        }
        return FolderOutcome.Damaged;
    }

    /// <summary>
    /// Registers the folder's current content as the game's new version, replacing the old record.
    /// Use it after deliberately patching a game, and to add volatile patterns.
    /// </summary>
    /// <param name="additionalPatterns">Files matching these are left out from now on and remembered for this game.</param>
    public async Task<LibraryGame> RescanAsync(string installPath, IEnumerable<string>? additionalPatterns = null, CancellationToken ct = default)
    {
        var path = Path.GetFullPath(installPath);
        var known = await FindByPathAsync(path, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"{path} is not a registered game folder.");
        var stored = await _db.GetManifestAsync(known.ContentHash, ct).ConfigureAwait(false);
        return await RebuildAsync(path, known, stored?.Manifest.VolatilePatterns, additionalPatterns, ct).ConfigureAwait(false);
    }

    private async Task<LibraryGame> RebuildAsync(string path, Installation? known, IEnumerable<string>? recorded, IEnumerable<string>? additional, CancellationToken ct)
    {
        var (manifest, scan) = await ManifestBuilder.ScanAndBuildAsync(path, cancellationToken: ct, alreadyRecorded: recorded, additional: additional).ConfigureAwait(false);
        var torrent = TorrentBuilder.Build(scan);
        manifest = manifest with { TorrentInfoHash = torrent.InfoHash };

        await _db.SaveManifestAsync(manifest, torrent.TorrentBytes, ct).ConfigureAwait(false);
        if (known is not null) await _db.DeleteInstallationAsync(known.Id, ct).ConfigureAwait(false);
        var installation = await _db.AddInstallationAsync(manifest.ContentHash, path, InstallationState.Installed, ct: ct).ConfigureAwait(false);

        _log.LogInformation("Game discovered: {Name} {Version} at {Path} ({Size:N0} bytes, {ContentHash})",
            manifest.Name, manifest.Version, path, manifest.TotalSize, manifest.ContentHash[..12]);
        var game = new LibraryGame(new StoredManifest(manifest, torrent.TorrentBytes, DateTimeOffset.UtcNow), installation);

        if (known is null) GameDiscovered?.Invoke(this, game);
        else InstallationChanged?.Invoke(this, new InstallationChange(InstallationChangeKind.Replaced, installation, known));
        return game;
    }

    /// <summary>
    /// Fully verifies an installed game and reports what differs. Also brings the recorded state in line with the result,
    /// so a game with changed content stops being offered and an intact one starts being offered again.
    /// </summary>
    public async Task<GameChanges> CheckAsync(string contentHash, CancellationToken ct = default)
    {
        var inst = (await _db.ListInstallationsAsync(ct).ConfigureAwait(false)).FirstOrDefault(i => i.ContentHash == contentHash)
            ?? throw new KeyNotFoundException($"Game {contentHash} is not installed on this PC.");
        var stored = await _db.GetManifestAsync(contentHash, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No manifest is stored for {contentHash}.");

        var result = await ManifestVerifier.VerifyAsync(stored.Manifest, inst.InstallPath, VerifyMode.Full, cancellationToken: ct).ConfigureAwait(false);

        var modified = result.SizeMismatch.Concat(result.HashMismatch).Order(StringComparer.Ordinal).ToList();
        var changes = new GameChanges(modified, result.Missing, result.Extra,
            VolatileRules.Suggest(modified.Concat(result.Extra)));

        var target = result.IsValid ? InstallationState.Installed : InstallationState.Invalid;
        if (inst.State != target) await MarkAsync(inst, target, ct).ConfigureAwait(false);
        return changes;
    }

    private async Task MarkAsync(Installation inst, InstallationState state, CancellationToken ct)
    {
        await _db.SetInstallationStateAsync(inst.Id, state, ct).ConfigureAwait(false);
        if (state == InstallationState.Invalid) await _db.SaveInstallationResumeDataAsync(inst.Id, null, ct).ConfigureAwait(false); // the files changed
        var updated = (await _db.GetInstallationAsync(inst.Id, ct).ConfigureAwait(false))!;
        InstallationChanged?.Invoke(this, new InstallationChange(InstallationChangeKind.StateChanged, updated, inst));
    }

    private async Task<Installation?> FindByPathAsync(string path, CancellationToken ct) =>
        (await _db.ListInstallationsAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(i => string.Equals(i.InstallPath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Folders an unfinished download is writing into. Scanning them would register half a game.</summary>
    private async Task<HashSet<string>> PathsBeingInstalledAsync(CancellationToken ct)
    {
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in (await _db.ListDownloadsAsync(ct).ConfigureAwait(false)).Where(d => d.IsActive))
        {
            var m = await _db.GetManifestAsync(d.ContentHash, ct).ConfigureAwait(false);
            if (m is not null) busy.Add(Path.GetFullPath(Path.Combine(d.TargetRoot, m.Manifest.FolderName)));
        }
        return busy;
    }

    /// <summary>Every known game version, each with its local installation when there is one, whatever its state.</summary>
    public async Task<IReadOnlyList<LibraryGame>> ListAsync(CancellationToken ct = default)
    {
        var manifests = await _db.ListManifestsAsync(ct).ConfigureAwait(false);
        var installs = await _db.ListInstallationsAsync(ct).ConfigureAwait(false);
        return manifests
            .Select(m => new LibraryGame(m, installs.FirstOrDefault(i => i.ContentHash == m.Manifest.ContentHash)))
            .OrderBy(g => g.Stored.Manifest.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
