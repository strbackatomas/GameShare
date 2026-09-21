using System.Security.Cryptography;

namespace GameShare.Storage;

public sealed record ScannedFile(string Path, long Size, string Sha256);

/// <param name="FolderName">Name of the scanned directory. Becomes the torrent name and install folder.</param>
/// <param name="PieceHashes">Concatenated 20-byte SHA-1 of each piece, as BitTorrent v1 needs them.</param>
/// <param name="VolatilePatterns">Patterns that were applied. Files matching them are not in <paramref name="Files"/>.</param>
public sealed record ScanResult(
    string FolderName,
    IReadOnlyList<ScannedFile> Files,
    long TotalSize,
    int PieceLength,
    byte[] PieceHashes,
    IReadOnlyList<string> VolatilePatterns);

/// <summary>
/// Reads a game directory once and produces everything identity and transport need:
/// SHA-256 per file (manifest) and SHA-1 per piece (torrent). One pass means the disk is read only once.
/// </summary>
public static class ContentScanner
{
    /// <summary>Launch metadata file. Excluded from content so editing it never changes game identity.</summary>
    public const string DefinitionFileName = "gameshare.json";

    private const int MinPieceLength = 1 << 20;   // 1 MiB
    private const int MaxPieceLength = 16 << 20;  // 16 MiB
    private const int TargetPieceCount = 2048;
    private const int ReadBufferSize = 4 << 20;

    /// <summary>Chooses a piece length so that large games end up with a few thousand pieces.</summary>
    public static int ChoosePieceLength(long totalSize)
    {
        long wanted = totalSize / TargetPieceCount;
        int len = MinPieceLength;
        while (len < MaxPieceLength && len < wanted) len <<= 1;
        return len;
    }

    /// <summary>Content files below <paramref name="root"/>, ordinal by relative path, forward slashes.</summary>
    internal static List<(string FullPath, string RelativePath, long Size)> EnumerateContent(string root, VolatileMatcher? volatileFiles = null)
    {
        // Reparse points (junctions, symlinks) are skipped so we never follow a loop or leave the game directory.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };

        return Directory.EnumerateFiles(root, "*", options)
            .Select(p => (FullPath: p, RelativePath: System.IO.Path.GetRelativePath(root, p).Replace('\\', '/')))
            .Where(f => !string.Equals(f.RelativePath, DefinitionFileName, StringComparison.OrdinalIgnoreCase))
            .Where(f => volatileFiles is null || !volatileFiles.IsMatch(f.RelativePath))
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .Select(f => (f.FullPath, f.RelativePath, new FileInfo(f.FullPath).Length))
            .ToList();
    }

    public static async Task<ScanResult> ScanAsync(
        string directory,
        int? pieceLength = null,
        IProgress<long>? bytesHashed = null,
        CancellationToken cancellationToken = default,
        VolatileMatcher? volatileFiles = null)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Cannot scan game, directory does not exist: {directory}");

        var root = System.IO.Path.GetFullPath(directory);
        var entries = EnumerateContent(root, volatileFiles);
        if (entries.Count == 0)
            throw new InvalidOperationException($"Cannot scan game, directory contains no files: {root}");

        long total = entries.Sum(e => e.Size);
        int pieceLen = pieceLength ?? ChoosePieceLength(total);
        if (pieceLen < 16 * 1024 || (pieceLen & (pieceLen - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(pieceLength), pieceLen, "Piece length must be a power of two and at least 16 KiB.");

        int pieceCount = checked((int)((total + pieceLen - 1) / pieceLen));
        var pieceHashes = new byte[pieceCount * 20];
        var files = new List<ScannedFile>(entries.Count);
        var buffer = new byte[Math.Min(pieceLen, ReadBufferSize)];

        using var pieceHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var fileHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int pieceIndex = 0;
        int filled = 0;   // bytes already fed into the current piece
        long done = 0;

        foreach (var (fullPath, relPath, size) in entries)
        {
            await using var fs = new FileStream(fullPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0,
            });

            long remaining = size;
            while (remaining > 0)
            {
                // Never read across a piece boundary, so a piece is completed exactly when filled == pieceLen.
                int want = (int)Math.Min(Math.Min(buffer.Length, remaining), pieceLen - filled);
                int read = await fs.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException($"File shrank while hashing: {fullPath}");

                pieceHasher.AppendData(buffer, 0, read);
                fileHasher.AppendData(buffer, 0, read);
                filled += read;
                remaining -= read;
                done += read;

                if (filled == pieceLen)
                {
                    pieceHasher.GetHashAndReset(pieceHashes.AsSpan(pieceIndex * 20, 20));
                    pieceIndex++;
                    filled = 0;
                }
                bytesHashed?.Report(done);
            }

            files.Add(new ScannedFile(relPath, size, Convert.ToHexString(fileHasher.GetHashAndReset()).ToLowerInvariant()));
        }

        if (filled > 0)
        {
            pieceHasher.GetHashAndReset(pieceHashes.AsSpan(pieceIndex * 20, 20));
            pieceIndex++;
        }

        if (pieceIndex != pieceCount)
            throw new InvalidOperationException($"Internal error: expected {pieceCount} pieces but hashed {pieceIndex}.");

        return new ScanResult(new DirectoryInfo(root).Name, files, total, pieceLen, pieceHashes, volatileFiles?.Patterns ?? []);
    }
}
