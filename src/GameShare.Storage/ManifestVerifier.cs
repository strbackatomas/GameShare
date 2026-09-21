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
        var missing = new List<string>();
        var badSize = new List<string>();
        var badHash = new List<string>();
        long done = 0;

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ManifestValidator.IsSafeRelativePath(file.Path))
                throw new InvalidDataException($"Refusing to verify unsafe manifest path '{file.Path}'.");

            var full = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(full);
            if (!info.Exists) { missing.Add(file.Path); continue; }
            if (info.Length != file.Size) { badSize.Add(file.Path); continue; }

            if (mode == VerifyMode.Full)
            {
                if (!string.Equals(await HashFileAsync(full, cancellationToken).ConfigureAwait(false), file.Hash, StringComparison.Ordinal))
                    badHash.Add(file.Path);
            }

            done += file.Size;
            bytesVerified?.Report(done);
        }

        var known = new HashSet<string>(manifest.Files.Select(f => f.Path), StringComparer.Ordinal);
        var extra = Directory.Exists(root)
            ? ContentScanner.EnumerateContent(root, VolatileMatcher.Create(manifest.VolatilePatterns))
                .Where(e => !known.Contains(e.RelativePath)).Select(e => e.RelativePath).ToList()
            : [];

        return new VerificationResult(missing, badSize, badHash, extra);
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
