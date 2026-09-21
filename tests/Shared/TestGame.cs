using System.Security.Cryptography;

namespace GameShare.Tests;

/// <summary>Creates a throw-away fake game directory with deterministic pseudo-random content.</summary>
internal sealed class TestGame : IDisposable
{
    /// <summary>Number of content files created by the constructor.</summary>
    public const int FileCount = 5;

    public string ParentDir { get; }
    public string GameDir { get; }

    public TestGame(string name = "TestGame", int seed = 1, long largeFileBytes = 6_000_000)
    {
        ParentDir = NewTempDir();
        GameDir = Path.Combine(ParentDir, name);
        Directory.CreateDirectory(Path.Combine(GameDir, "Bin64"));
        Directory.CreateDirectory(Path.Combine(GameDir, "content", "sub"));

        WriteRandom(Path.Combine(GameDir, "Game.exe"), 300_000, seed);
        WriteRandom(Path.Combine(GameDir, "Bin64", "engine.dll"), 1_234_567, seed + 1);
        WriteRandom(Path.Combine(GameDir, "content", "big.pak"), largeFileBytes, seed + 2);
        WriteRandom(Path.Combine(GameDir, "content", "sub", "tiny.txt"), 17, seed + 3);
        File.WriteAllBytes(Path.Combine(GameDir, "content", "empty.bin"), []);
    }

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gameshare-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void DeleteQuietly(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort cleanup */ }
    }

    private static void WriteRandom(string path, long length, int seed)
    {
        var rng = new Random(seed);
        var buf = new byte[81920];
        using var fs = File.Create(path);
        for (long left = length; left > 0;)
        {
            int n = (int)Math.Min(buf.Length, left);
            rng.NextBytes(buf.AsSpan(0, n));
            fs.Write(buf, 0, n);
            left -= n;
        }
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }

    /// <summary>Flips one byte in the middle of a file without changing its size.</summary>
    public static void CorruptOneByte(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Position = fs.Length / 2;
        int b = fs.ReadByte();
        fs.Position = fs.Length / 2;
        fs.WriteByte((byte)(b ^ 0xFF));
    }

    /// <summary>Relative path -> SHA-256, used to compare a downloaded copy with the original.</summary>
    public static Dictionary<string, string> HashTree(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetRelativePath(dir, p).Replace('\\', '/'),
                p =>
                {
                    using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return Convert.ToHexString(SHA256.HashData(fs));
                },
                StringComparer.Ordinal);

    public void Dispose() => DeleteQuietly(ParentDir);
}
