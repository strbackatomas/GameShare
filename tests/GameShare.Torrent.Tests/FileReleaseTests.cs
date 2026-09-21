using GameShare.Storage;

namespace GameShare.Torrent.Tests;

/// <summary>
/// On Windows the library memory-maps the files it serves, and a program that saves by truncating such a file is refused.
/// Games do that with their settings. A game must never notice that it is being shared, so the seed holds few files open
/// and lets go of them when it has been idle. These tests write the way a game does, with File.WriteAllBytes.
/// </summary>
public class FileReleaseTests
{
    private static TorrentEngineOptions Options(TimeSpan? idleRelease, int openFiles = 8) => new()
    {
        ListenPort = 0,
        AllowMultipleConnectionsPerIp = true,
        IdleReleaseAfter = idleRelease,
        OpenFileLimit = openFiles,
    };

    /// <summary>Create-or-truncate, like File.WriteAllText or fopen("w"). The content stays the same.</summary>
    private static bool CanRewrite(string file)
    {
        try { File.WriteAllBytes(file, File.ReadAllBytes(file)); return true; }
        catch (IOException) { return false; }
    }

    private static List<string> Blocked(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => new FileInfo(f).Length > 0 && !CanRewrite(f)).ToList();

    private static async Task UntilAsync(Func<bool> condition, string what, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}");
            await Task.Delay(200);
        }
    }

    /// <summary>A seed serves the whole game to one leecher, which then leaves. The seed is idle afterwards.</summary>
    private static async Task ServeOnceAsync(TorrentEngine seedEngine, BuiltTorrent torrent, string parent, string? expectedDir = null)
    {
        var seed = seedEngine.Add(torrent.TorrentBytes, parent, uploadOnly: true);
        seed.Start();
        await seed.WaitForCompletionAsync(TimeSpan.FromSeconds(60));

        using var leechEngine = new TorrentEngine(Options(null));
        var target = TestGame.NewTempDir();
        try
        {
            var leech = leechEngine.Add(torrent.TorrentBytes, target);
            leech.Start();
            await leech.WaitForCompletionAsync(TimeSpan.FromSeconds(90));
            if (expectedDir is not null)
                Assert.Equal(TestGame.HashTree(expectedDir), TestGame.HashTree(Path.Combine(target, Path.GetFileName(expectedDir))));
            await leechEngine.RemoveAsync(leech);
        }
        finally { TestGame.DeleteQuietly(target); }
    }

    [Fact]
    public async Task Control_without_the_release_a_served_game_cannot_be_rewritten()
    {
        using var game = new TestGame(seed: 1, largeFileBytes: 3_000_000);
        var torrent = await TorrentBuilder.BuildAsync(game.GameDir);
        using var seeder = new TorrentEngine(Options(idleRelease: null));

        await ServeOnceAsync(seeder, torrent, game.ParentDir);

        // If this ever passes with nothing blocked, the library stopped holding the files and the release is no longer needed.
        Assert.NotEmpty(Blocked(game.GameDir));
    }

    [Fact]
    public async Task An_idle_seed_lets_go_of_its_files_and_still_seeds()
    {
        using var game = new TestGame(seed: 1, largeFileBytes: 3_000_000);
        var torrent = await TorrentBuilder.BuildAsync(game.GameDir);
        using var seeder = new TorrentEngine(Options(idleRelease: TimeSpan.FromSeconds(1)));

        await ServeOnceAsync(seeder, torrent, game.ParentDir);

        await UntilAsync(() => Blocked(game.GameDir).Count == 0, "the idle seed to release its files", TimeSpan.FromSeconds(20));

        // Letting go must not have switched seeding off: another PC can still install the game from it.
        using var second = new TorrentEngine(Options(null));
        var target = TestGame.NewTempDir();
        try
        {
            var leech = second.Add(torrent.TorrentBytes, target);
            leech.Start();
            await leech.WaitForCompletionAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(TestGame.HashTree(game.GameDir), TestGame.HashTree(Path.Combine(target, Path.GetFileName(game.GameDir))));
        }
        finally { TestGame.DeleteQuietly(target); }
    }

    [Fact]
    public async Task Only_a_handful_of_files_stay_open_however_many_a_game_has()
    {
        var parent = TestGame.NewTempDir();
        var dir = Path.Combine(parent, "ManyFiles");
        try
        {
            var rng = new Random(3);
            for (int i = 0; i < 120; i++)
            {
                var path = Path.Combine(dir, $"d{i % 6}", $"f{i}.dat");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var bytes = new byte[20_000 + rng.Next(10_000)];
                rng.NextBytes(bytes);
                File.WriteAllBytes(path, bytes);
            }
            var torrent = await TorrentBuilder.BuildAsync(dir);
            using var seeder = new TorrentEngine(Options(idleRelease: null, openFiles: 4));

            await ServeOnceAsync(seeder, torrent, parent);

            // The library treats the limit as a soft one, a few files in use at the moment can go over it. 120 without a limit.
            var blocked = Blocked(dir);
            Assert.True(blocked.Count <= 12, $"{blocked.Count} of 120 files were still held open, the limit is 4");
        }
        finally { TestGame.DeleteQuietly(parent); }
    }
}
