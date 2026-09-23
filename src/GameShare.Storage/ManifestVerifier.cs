using System.Collections.Concurrent;
using System.Security.Cryptography;
using GameShare.Protocol;

namespace GameShare.Storage;

public enum VerifyMode
{
    /// <summary>Existence and size only. Fast, catches missing and truncated files.</summary>
    Quick,
    /// <summary>Also SHA-256 of every file. Catches any corruption.</summary>
    Full,
}

/// <summary>
/// Outcome of comparing a directory with a manifest. <see cref="Extra"/> files (saves, configs)
/// are reported but do not make the game invalid.
/// </summary>
public sealed record VerificationResult(
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> SizeMismatch,
    IReadOnlyList<string> HashMismatch,
    IReadOnlyList<string> Extra)
{
    public bool IsValid => Missing.Count == 0 && SizeMismatch.Count == 0 && HashMismatch.Count == 0;

    public override string ToString() =>
        IsValid
            ? $"valid ({Extra.Count} extra files)"
            : $"INVALID: {Missing.Count} missing, {SizeMismatch.Count} wrong size, {HashMismatch.Count} wrong hash";
}

public static class ManifestVerifier
{
    public static async Task<VerificationResult> VerifyAsync(
        GameManifest manifest,
        string gameDirectory,
        VerifyMode mode,
        IProgress<long>? bytesVerified = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(gameDirectory);

        // Cheap, no I/O: reject an unsafe manifest before any file is touched, instead of partway through.
        foreach (var file in manifest.Files)
        {
            if (!ManifestValidator.IsSafeRelativePath(file.Path))
                throw new InvalidDataException($"Refusing to verify unsafe manifest path '{file.Path}'.");
        }

        // Each file is independent, so many are checked (and, in Full mode, hashed) at once. This is what makes
        // verifying a game with thousands of small files take seconds instead of minutes.
        var missing = new ConcurrentBag<(int Index, string Path)>();
        var badSize = new ConcurrentBag<(int Index, string Path)>();
        var badHash = new ConcurrentBag<(int Index, string Path)>();
        long done = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(manifest.Files.Select((file, index) => (file, index)), parallelOptions, async (item, ct) =>
        {
            var (file, index) = item;
            var full = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(full);
            if (!info.Exists) { missing.Add((index, file.Path)); return; }
            if (info.Length != file.Size) { badSize.Add((index, file.Path)); return; }

            if (mode == VerifyMode.Full)
            {
                if (!string.Equals(await HashFileAsync(full, ct).ConfigureAwait(false), file.Hash, StringComparison.Ordinal))
                    badHash.Add((index, file.Path));
            }

            bytesVerified?.Report(Interlocked.Add(ref done, file.Size));
        }).ConfigureAwait(false);

        var known = new HashSet<string>(manifest.Files.Select(f => f.Path), StringComparer.Ordinal);
        var extra = Directory.Exists(root)
            ? ContentScanner.EnumerateContent(root, VolatileMatcher.Create(manifest.VolatilePatterns))
                .Where(e => !known.Contains(e.RelativePath)).Select(e => e.RelativePath).ToList()
            : [];

        return new VerificationResult(
            missing.OrderBy(x => x.Index).Select(x => x.Path).ToList(),
            badSize.OrderBy(x => x.Index).Select(x => x.Path).ToList(),
            badHash.OrderBy(x => x.Index).Select(x => x.Path).ToList(),
            extra);
    }

    /// <summary>Lowercase hex SHA-256 of one file. Tolerates the file being open in a running game.</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite, // a running game may still hold the file
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1 << 20,
        });
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
    }
}
