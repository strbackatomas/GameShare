using GameShare.Protocol;

namespace GameShare.Storage;

/// <summary>
/// Puts a verified package of GameShare in place of the installed files, and takes it back out again.
///
/// Nothing is overwritten or deleted while the update is undecided: each installed file is renamed aside to <c>*.gsold</c> first,
/// which Windows allows even for a program or library that is running, and the new file is copied to its name. So a client that is
/// still open keeps running from the renamed files and can restart into the new ones, and <see cref="Rollback"/> can restore the old
/// installation exactly. What was done is written to a journal before it is done, so a rollback also works after a crash halfway.
/// </summary>
public static class AppUpdateFiles
{
    public const string AsideSuffix = ".gsold";
    public const string JournalFileName = "gameshare-update-journal.json";
    public const string LeftoversFileName = "gameshare-update-leftovers.json";

    /// <summary>
    /// Files each PC sets up for itself (install-agent.ps1 writes the ports, folders and trust settings into appsettings.json).
    /// A package brings them for a new installation, an update never replaces them.
    /// </summary>
    public static readonly IReadOnlyList<string> KeptFiles = ["appsettings.json", "trust-public.key"];

    /// <param name="Path">Relative to the installation, forward slashes.</param>
    /// <param name="Aside">Where the file that was there was renamed to, relative as well. Null when there was none.</param>
    /// <param name="Added">Whether a file of the package is put at <paramref name="Path"/>. False for an old file that is only moved aside.</param>
    private sealed record Entry(string Path, string? Aside, bool Added);

    private sealed record Journal(string? Version, IReadOnlyList<Entry> Entries);

    private static bool IsKept(string relativePath) => KeptFiles.Contains(relativePath, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a swap in <paramref name="installDir"/> was done and is neither committed nor rolled back.</summary>
    public static bool HasPendingSwap(string installDir) => File.Exists(Path.Combine(installDir, JournalFileName));

    /// <summary>
    /// Replaces the files of <paramref name="installDir"/> with the package in <paramref name="stagingDir"/>. The package is checked against
    /// <paramref name="manifest"/> before anything is touched, and the installed files are checked again afterwards. On any failure
    /// everything is rolled back and the exception is rethrown. Afterwards call <see cref="Commit"/> once the new version runs,
    /// or <see cref="Rollback"/> when it does not.
    /// </summary>
    /// <param name="previousFiles">
    /// The files of the version that is installed, relative with forward slashes, when known. Those the package no longer has are moved aside too.
    /// Without it nothing else in the folder is touched: the portable exe may live in a Downloads folder full of other things.
    /// </param>
    /// <exception cref="InvalidDataException">The package is not what the manifest says.</exception>
    /// <exception cref="InvalidOperationException">An earlier swap of this folder is still undecided.</exception>
    public static async Task SwapAsync(
        string stagingDir, string installDir, GameManifest manifest, IReadOnlyCollection<string>? previousFiles = null, CancellationToken ct = default)
    {
        var staging = Path.GetFullPath(stagingDir);
        var install = Path.GetFullPath(installDir);
        if (HasPendingSwap(install))
            throw new InvalidOperationException($"An earlier update of {install} was neither finished nor undone. It has to be rolled back first.");

        var check = await ManifestVerifier.VerifyAsync(manifest, staging, VerifyMode.Full, cancellationToken: ct).ConfigureAwait(false);
        if (!check.IsValid) throw new InvalidDataException($"The update in {staging} is not what was signed: {check}.");

        Directory.CreateDirectory(install);
        var entries = new List<Entry>();
        var copied = new List<ManifestFile>();
        foreach (var file in manifest.Files)
        {
            var target = Full(install, file.Path);
            if (IsKept(file.Path) && File.Exists(target)) continue;
            entries.Add(new Entry(file.Path, File.Exists(target) ? FreeAsideName(install, file.Path) : null, Added: true));
            copied.Add(file);
        }
        var inPackage = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in previousFiles ?? [])
        {
            if (!ManifestValidator.IsSafeRelativePath(old)) throw new ArgumentException($"'{old}' is not a relative path inside the installation.", nameof(previousFiles));
            if (inPackage.Contains(old) || IsKept(old) || !File.Exists(Full(install, old))) continue;
            entries.Add(new Entry(old, FreeAsideName(install, old), Added: false));
        }

        // Written before the first file moves: whatever happens from here on can be undone from it.
        WriteJson(Path.Combine(install, JournalFileName), new Journal(manifest.Version, entries));
        try
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var target = Full(install, entry.Path);
                if (entry.Aside is not null) Retry(() => File.Move(target, Full(install, entry.Aside)));
                if (!entry.Added) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Full(staging, entry.Path), target, overwrite: false);
            }

            // What will run is what was signed, not only what lay in the staging folder a moment ago.
            var installed = await ManifestVerifier.VerifyAsync(manifest with { Files = copied }, install, VerifyMode.Full, cancellationToken: ct).ConfigureAwait(false);
            if (!installed.IsValid) throw new InvalidDataException($"The files copied to {install} are not what was signed: {installed}.");
        }
        catch (Exception ex)
        {
            try { Rollback(install); }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"The update failed ({ex.Message}) and the old files could not all be put back ({rollback.Message}). " +
                    $"The journal in {install} is kept, rolling back can be tried again.", ex);
            }
            throw;
        }
    }

    /// <summary>
    /// Keeps the new version: forgets the journal and deletes the old files. A file that is still in use (the client that has not
    /// restarted yet runs from it) is remembered and deleted by <see cref="CleanupLeftovers"/> later.
    /// </summary>
    public static void Commit(string installDir)
    {
        var install = Path.GetFullPath(installDir);
        var journal = ReadJournal(install);
        if (journal is null) return;

        var leftovers = ReadLeftovers(install);
        foreach (var aside in journal.Entries.Select(e => e.Aside).OfType<string>())
            if (!TryDelete(Full(install, aside)) && !leftovers.Contains(aside, StringComparer.OrdinalIgnoreCase)) leftovers.Add(aside);
        WriteLeftovers(install, leftovers);
        File.Delete(Path.Combine(install, JournalFileName));
    }

    /// <summary>
    /// Restores the installation as it was before the swap, from the journal. Safe to call again after it failed halfway, and after
    /// a crash in the middle of <see cref="SwapAsync"/>: an entry that was never carried out is left as it is.
    /// </summary>
    /// <returns>False when there was nothing to roll back.</returns>
    /// <exception cref="IOException">A file could not be put back, for example because the new version still runs. The journal stays, so it can be tried again.</exception>
    public static bool Rollback(string installDir)
    {
        var install = Path.GetFullPath(installDir);
        var journal = ReadJournal(install);
        if (journal is null) return false;

        foreach (var entry in journal.Entries.Reverse())
        {
            var target = Full(install, entry.Path);
            var aside = entry.Aside is null ? null : Full(install, entry.Aside);
            bool moved = aside is not null && File.Exists(aside);

            // With an aside name that does not exist the old file was never moved, so what is at the target is still the old one.
            if (entry.Added && File.Exists(target) && (aside is null || moved)) Retry(() => File.Delete(target));
            if (moved) Retry(() => File.Move(aside!, target));
        }
        File.Delete(Path.Combine(install, JournalFileName));
        return true;
    }

    /// <summary>Deletes old files a <see cref="Commit"/> could not delete because they were still in use. Never throws.</summary>
    public static void CleanupLeftovers(string installDir)
    {
        try
        {
            var install = Path.GetFullPath(installDir);
            var remaining = ReadLeftovers(install).Where(aside => !TryDelete(Full(install, aside))).ToList();
            WriteLeftovers(install, remaining);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { /* next time */ }
    }

    /// <summary>A name next to the file that is not taken. A stale one from an earlier update is deleted when it can be.</summary>
    private static string FreeAsideName(string install, string relativePath)
    {
        for (int i = 0; ; i++)
        {
            var aside = i == 0 ? relativePath + AsideSuffix : $"{relativePath}.{i}{AsideSuffix}";
            var full = Full(install, aside);
            if (!File.Exists(full) || TryDelete(full)) return aside;
        }
    }

    private static string Full(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static Journal? ReadJournal(string install)
    {
        var path = Path.Combine(install, JournalFileName);
        if (!File.Exists(path)) return null;
        var journal = GameShareJson.Deserialize<Journal>(File.ReadAllText(path));
        foreach (var e in journal.Entries)
            if (!ManifestValidator.IsSafeRelativePath(e.Path) || (e.Aside is not null && !ManifestValidator.IsSafeRelativePath(e.Aside)))
                throw new InvalidDataException($"The update journal in {install} names a path outside the installation.");
        return journal;
    }

    private static List<string> ReadLeftovers(string install)
    {
        var path = Path.Combine(install, LeftoversFileName);
        if (!File.Exists(path)) return [];
        return GameShareJson.Deserialize<List<string>>(File.ReadAllText(path)).Where(ManifestValidator.IsSafeRelativePath).ToList();
    }

    private static void WriteLeftovers(string install, List<string> leftovers)
    {
        var path = Path.Combine(install, LeftoversFileName);
        if (leftovers.Count == 0) File.Delete(path);
        else WriteJson(path, leftovers);
    }

    private static void WriteJson<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, GameShareJson.Serialize(value));
        File.Move(temp, path, overwrite: true);
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>An antivirus or the search indexer may hold a file for a moment right after it was written or renamed.</summary>
    private static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5) { Thread.Sleep(200 * attempt); }
        }
    }
}
