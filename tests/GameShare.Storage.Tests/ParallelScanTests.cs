using System.Security.Cryptography;
using GameShare.Storage;

namespace GameShare.Storage.Tests;

/// <summary>The scanner hashes on several threads. Whatever the files and piece size, it must give exactly what one plain pass gives.</summary>
public class ParallelScanTests
{
    /// <summary>The scanner as it was before it went parallel: one reader, one SHA-1 and one SHA-256, in order.</summary>
    private static (byte[] Pieces, List<string> Files) Reference(string root, int pieceLen)
    {
        var entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => (Full: p, Rel: Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/')))
            .OrderBy(e => e.Rel, StringComparer.Ordinal).ToList();
        using var piece = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var pieces = new List<byte>();
        var files = new List<string>();
        int filled = 0;
        foreach (var (full, _) in entries)
        {
            var bytes = File.ReadAllBytes(full);
            files.Add(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            foreach (var b in bytes)
            {
                piece.AppendData([b]);
                if (++filled == pieceLen) { pieces.AddRange(piece.GetHashAndReset()); filled = 0; }
            }
        }
        if (filled > 0) pieces.AddRange(piece.GetHashAndReset());
        return ([.. pieces], files);
    }

    [Theory]
    [InlineData(16 * 1024)]   // many pieces, several per file and files across pieces
    [InlineData(64 * 1024)]
    [InlineData(1 << 20)]     // one piece for everything
    public async Task Parallel_hashing_gives_exactly_what_a_single_pass_gives(int pieceLen)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gs-parallel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var random = new Random(pieceLen);
            Directory.CreateDirectory(Path.Combine(dir, "sub", "deeper"));
            int[] sizes = [0, 1, 15_000, 16 * 1024, 16 * 1024 + 1, 100_003, 0, 250_000, 7, 65_536, 300_001];
            for (int i = 0; i < sizes.Length; i++)
            {
                var bytes = new byte[sizes[i]];
                random.NextBytes(bytes);
                var folder = i % 3 == 0 ? dir : i % 3 == 1 ? Path.Combine(dir, "sub") : Path.Combine(dir, "sub", "deeper");
                await File.WriteAllBytesAsync(Path.Combine(folder, $"f{i:00}.bin"), bytes);
            }

            var scan = await ContentScanner.ScanAsync(dir, pieceLen);
            var (pieces, files) = Reference(dir, pieceLen);

            Assert.Equal(pieces, scan.PieceHashes);
            Assert.Equal(files, scan.Files.Select(f => f.Sha256));
            Assert.Equal(sizes.Sum(s => (long)s), scan.TotalSize);
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", scan.Files.First(f => f.Size == 0).Sha256);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_fails_the_scan_with_its_own_error()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gs-parallel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "a.bin"), new byte[200_000]);
            await File.WriteAllBytesAsync(Path.Combine(dir, "b.bin"), new byte[200_000]);
            await using var locked = new FileStream(Path.Combine(dir, "b.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            await Assert.ThrowsAsync<IOException>(() => ContentScanner.ScanAsync(dir, 16 * 1024));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
