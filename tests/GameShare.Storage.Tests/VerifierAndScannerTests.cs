using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.Storage.Tests;

public class VerifierAndScannerTests
{
    private static async Task<(TestGame Game, GameManifest Manifest)> NewGameAsync()
    {
        var g = new TestGame();
        var (m, _) = await ManifestBuilder.ScanAndBuildAsync(g.GameDir);
        return (g, m);
    }

    [Fact]
    public async Task Untouched_game_verifies_in_both_modes()
    {
        var (g, m) = await NewGameAsync();
        using (g)
        {
            Assert.True((await ManifestVerifier.VerifyAsync(m, g.GameDir, VerifyMode.Quick)).IsValid);
            var full = await ManifestVerifier.VerifyAsync(m, g.GameDir, VerifyMode.Full);
            Assert.True(full.IsValid, full.ToString());
        }
    }

    [Fact]
    public async Task Missing_file_makes_game_invalid()
    {
        var (g, m) = await NewGameAsync();
        using (g)
        {
            File.Delete(Path.Combine(g.GameDir, "Bin64", "engine.dll"));

            var r = await ManifestVerifier.VerifyAsync(m, g.GameDir, VerifyMode.Quick);

            Assert.False(r.IsValid);
            Assert.Equal(["Bin64/engine.dll"], r.Missing);
        }
    }

    [Fact]
    public async Task Truncated_file_is_caught_even_by_quick_mode()
    {
        var (g, m) = await NewGameAsync();
        using (g)
        {
            var path = Path.Combine(g.GameDir, "content", "big.pak");
            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write)) fs.SetLength(fs.Length - 1);

            var r = await ManifestVerifier.VerifyAsync(m, g.GameDir, VerifyMode.Quick);

            Assert.False(r.IsValid);
            Assert.Equal(["content/big.pak"], r.SizeMismatch);
        }
    }

    [Fact]
    public async Task Same_size_corruption_needs_full_mode()
    {
        var (g, m) = await NewGameAsync();
        using (g)
        {
            TestGame.CorruptOneByte(Path.Combine(g.GameDir, "content", "big.pak"));

            Assert.True((await ManifestVerifier.VerifyAsync(m, g.GameDir, VerifyMode.Quick)).IsValid); // documents the limit of quick mode
            var full = await ManifestVerifier.VerifyAsync(m, g.GameDir, VerifyMode.Full);

            Assert.False(full.IsValid);
            Assert.Equal(["content/big.pak"], full.HashMismatch);
        }
    }

    [Fact]
    public async Task Extra_files_are_reported_but_do_not_invalidate()
    {
        var (g, m) = await NewGameAsync();
        using (g)
        {
            await File.WriteAllTextAsync(Path.Combine(g.GameDir, "savegame.dat"), "progress");

            var r = await ManifestVerifier.VerifyAsync(m, g.GameDir, VerifyMode.Full);

            Assert.True(r.IsValid);
            Assert.Equal(["savegame.dat"], r.Extra);
        }
    }

    [Fact]
    public async Task Verifier_refuses_a_manifest_that_points_outside_the_game_folder()
    {
        var (g, m) = await NewGameAsync();
        using (g)
        {
            var evil = m with { Files = [new ManifestFile("../outside.txt", 1, new string('a', 64))] };
            await Assert.ThrowsAsync<InvalidDataException>(() => ManifestVerifier.VerifyAsync(evil, g.GameDir, VerifyMode.Quick));
        }
    }

    [Fact]
    public async Task Root_scanner_finds_game_folders_and_reports_missing_roots()
    {
        var rootA = TestGame.NewTempDir();
        var rootB = TestGame.NewTempDir();
        var missing = Path.Combine(rootA, "does-not-exist");
        try
        {
            Directory.CreateDirectory(Path.Combine(rootA, "BeamNG"));
            await File.WriteAllTextAsync(Path.Combine(rootA, "BeamNG", "a.exe"), "x");
            Directory.CreateDirectory(Path.Combine(rootA, "Empty"));
            Directory.CreateDirectory(Path.Combine(rootA, ".hidden"));
            await File.WriteAllTextAsync(Path.Combine(rootA, ".hidden", "x"), "x");
            Directory.CreateDirectory(Path.Combine(rootB, "ETS2", "bin"));
            await File.WriteAllTextAsync(Path.Combine(rootB, "ETS2", "bin", "b.exe"), "x");
            await File.WriteAllTextAsync(Path.Combine(rootB, "loose-file.txt"), "x");

            var missingRoots = new List<string>();
            var found = GameRootScanner.FindCandidateDirectories([rootA, rootB, missing], missingRoots);

            Assert.Equal(
                [Path.Combine(rootA, "BeamNG"), Path.Combine(rootB, "ETS2")],
                found.OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal));
            Assert.Equal([missing], missingRoots);
        }
        finally
        {
            TestGame.DeleteQuietly(rootA);
            TestGame.DeleteQuietly(rootB);
        }
    }

    [Fact]
    public async Task Scan_reports_progress_up_to_total_size()
    {
        using var g = new TestGame();
        long last = 0;
        var progress = new Progress<long>(v => last = Math.Max(last, v));

        var scan = await ContentScanner.ScanAsync(g.GameDir, bytesHashed: progress);
        await Task.Delay(100); // Progress<T> posts asynchronously

        Assert.Equal(scan.TotalSize, last);
    }

    [Fact]
    public async Task Scan_can_be_cancelled()
    {
        using var g = new TestGame();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ContentScanner.ScanAsync(g.GameDir, cancellationToken: cts.Token));
    }
}
