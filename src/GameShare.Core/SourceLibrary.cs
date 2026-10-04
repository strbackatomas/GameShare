using GameShare.Core.Data;
using GameShare.Protocol;
using GameShare.Storage;
using GameShare.Torrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameShare.Core;

/// <summary>What a scan of the source folder did.</summary>
/// <param name="Problems">Folders that cannot serve as a source, each with the reason, for example a folder named differently from the game.</param>
public sealed record SourceScanSummary(int Added, int Unchanged, int Changed, IReadOnlyList<string> Problems);

/// <summary>
/// The source folder: untouched copies of games, typically on a second disk, kept as the truth to hand out. Nothing here is played
/// or written: a copy is only read, to verify it and to seed it. A copy is recognised by its files, like a game in a game folder,
/// and only ever compared with what is known, so a folder whose files changed is marked as such and is not turned into a new version.
/// </summary>
public sealed class SourceLibrary
{
    private readonly GameShareDb _db;
    private readonly ILogger _log;

    public SourceLibrary(GameShareDb db, ILogger<SourceLibrary>? logger = null)
    {
        _db = db;
        _log = logger ?? NullLogger<SourceLibrary>.Instance;
    }

    /// <summary>Every copy in the source folder as of the last scan, intact or not.</summary>
    public Task<IReadOnlyList<SourceCopy>> ListAsync(CancellationToken ct = default) => _db.ListSourceCopiesAsync(ct);

    /// <summary>A source copy was added, became intact or damaged, or went away. Raised on the scanning thread.</summary>
    public event EventHandler<string>? Changed;

    /// <param name="root">The source folder, null when there is none: every source copy is then forgotten.</param>
    /// <param name="progress">Reported for every folder, and while a folder is hashed, for every block read.</param>
    public async Task<SourceScanSummary> ScanAsync(string? root, CancellationToken ct = default, IProgress<ScanProgress>? progress = null)
    {
        var known = await _db.ListSourceCopiesAsync(ct).ConfigureAwait(false);
        var candidates = root is null ? [] : GameRootScanner.FindCandidateDirectories([root]);
        int added = 0, unchanged = 0, changed = 0;
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < candidates.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(candidates[index]);
            var name = Path.GetFileName(path);
            var number = index + 1;
            progress?.Report(new ScanProgress(number, candidates.Count, name, 0, 0));
            Action<long, long>? hashing = progress is null ? null : (done, total) => progress.Report(new ScanProgress(number, candidates.Count, name, done, total));
            seen.Add(path);

            try
            {
                var copy = known.FirstOrDefault(k => string.Equals(k.Path, path, StringComparison.OrdinalIgnoreCase));
                if (copy is null)
                {
                    var (hash, problem) = await IdentifyAsync(path, hashing, ct).ConfigureAwait(false);
                    if (problem is not null) { problems.Add($"{path}: {problem}"); continue; }
                    await _db.AddSourceCopyAsync(hash!, path, InstallationState.Installed, ct).ConfigureAwait(false);
                    _log.LogInformation("Source copy found: {Path} ({Hash})", path, hash![..12]);
                    Changed?.Invoke(this, hash);
                    added++;
                    continue;
                }

                var stored = await _db.GetManifestAsync(copy.ContentHash, ct).ConfigureAwait(false);
                if (stored is null) { await ForgetAsync(copy, ct).ConfigureAwait(false); continue; }

                // Sizes on every scan. A copy marked damaged gets a full check before it counts again: sizes prove little.
                var intact = (await ManifestVerifier.VerifyAsync(stored.Manifest, path, VerifyMode.Quick, cancellationToken: ct).ConfigureAwait(false)).IsValid;
                if (intact && copy.State == InstallationState.Invalid)
                    intact = (await ManifestVerifier.VerifyAsync(stored.Manifest, path, VerifyMode.Full, Bytes(hashing, stored.Manifest.TotalSize), ct).ConfigureAwait(false)).IsValid;

                var state = intact ? InstallationState.Installed : InstallationState.Invalid;
                if (state == copy.State) { unchanged++; continue; }
                await _db.SetSourceCopyStateAsync(copy.Id, state, ct).ConfigureAwait(false);
                if (intact) _log.LogInformation("Source copy is intact again: {Path}", path);
                else _log.LogWarning("Source copy changed and is no longer handed out: {Path}. Its files should not change, copy the game into it again.", path);
                Changed?.Invoke(this, copy.ContentHash);
                changed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                problems.Add($"{path}: {ex.Message}");
                _log.LogWarning(ex, "Could not scan source folder {Path}", path);
            }
        }

        // A copy whose folder is gone, or that is not in the source folder any more because the setting changed.
        foreach (var copy in known.Where(k => !seen.Contains(k.Path)))
            await ForgetAsync(copy, ct).ConfigureAwait(false);

        _log.LogInformation("Source scan finished: {Added} new, {Unchanged} unchanged, {Changed} changed, {Problems} not usable",
            added, unchanged, changed, problems.Count);
        return new SourceScanSummary(added, unchanged, changed, problems);
    }

    private async Task ForgetAsync(SourceCopy copy, CancellationToken ct)
    {
        await _db.DeleteSourceCopyAsync(copy.Id, ct).ConfigureAwait(false);
        _log.LogInformation("Source copy no longer there: {Path}", copy.Path);
        Changed?.Invoke(this, copy.ContentHash);
    }

    /// <summary>
    /// Which version the folder holds. A version this PC already knows under the same folder name is tried first, by verifying the
    /// files against it, so the copy gets exactly that version's identity and torrent whatever its gameshare.json says. Otherwise
    /// the folder is hashed as a new version, as a scan of a game folder would.
    /// </summary>
    private async Task<(string? Hash, string? Problem)> IdentifyAsync(string path, Action<long, long>? hashing, CancellationToken ct)
    {
        var folder = Path.GetFileName(path);
        var manifests = (await _db.ListManifestsAsync(ct).ConfigureAwait(false))
            .Where(m => m.TorrentBytes is not null && string.Equals(m.Manifest.FolderName, folder, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var known in manifests)
        {
            if (!(await ManifestVerifier.VerifyAsync(known.Manifest, path, VerifyMode.Quick, cancellationToken: ct).ConfigureAwait(false)).IsValid) continue;
            var full = await ManifestVerifier.VerifyAsync(known.Manifest, path, VerifyMode.Full, Bytes(hashing, known.Manifest.TotalSize), ct).ConfigureAwait(false);
            if (!full.IsValid) continue;
            return string.Equals(known.Manifest.FolderName, folder, StringComparison.Ordinal)
                ? (known.Manifest.ContentHash, null)
                : (null, $"the folder must be named exactly '{known.Manifest.FolderName}', as the game is on the other PCs");
        }

        // Not a version known here. Hashed as it is, leaving out what versions under this name left out.
        var recorded = manifests.SelectMany(m => m.Manifest.VolatilePatterns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var total = new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        var (manifest, scan) = await ManifestBuilder.ScanAndBuildAsync(path, Bytes(hashing, total), ct, alreadyRecorded: recorded).ConfigureAwait(false);
        var existing = await _db.GetManifestAsync(manifest.ContentHash, ct).ConfigureAwait(false);
        if (existing is not null && !string.Equals(existing.Manifest.FolderName, folder, StringComparison.Ordinal))
            return (null, $"the folder must be named exactly '{existing.Manifest.FolderName}', as the game is on the other PCs");
        if (existing?.TorrentBytes is not null) return (manifest.ContentHash, null);

        var torrent = TorrentBuilder.Build(scan);
        // A version known without its torrent keeps its manifest, with its definition: only the torrent is added.
        await _db.SaveManifestAsync((existing?.Manifest ?? manifest) with { TorrentInfoHash = torrent.InfoHash }, torrent.TorrentBytes, ct).ConfigureAwait(false);
        return (manifest.ContentHash, null);
    }

    private static IProgress<long>? Bytes(Action<long, long>? hashing, long total)
    {
        if (hashing is null) return null;
        hashing(0, total);
        return new CallbackProgress(done => hashing(done, total));
    }
}
