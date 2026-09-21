using System.Diagnostics;
using GameShare.Storage;

namespace GameShare.Torrent.Tests;

/// <summary>
/// The library ignores its own global limits for peers on the local network, and here every peer is. These tests measure real
/// transfers, because the setting being stored proves nothing: it was stored, and did nothing, before the limits were enforced per torrent.
/// </summary>
public class RateLimitTests
{
    private const long BigFile = 10_000_000; // about 11.5 MB of game in total

    private static TorrentEngineOptions Local(int? up = null, int? down = null) => new()
    {
        ListenPort = 0,
        AllowMultipleConnectionsPerIp = true,
        MaxUploadBytesPerSecond = up,
        MaxDownloadBytesPerSecond = down,
    };

    private static async Task<(BuiltTorrent Torrent, TestGame Game)> MakeGameAsync(int seed = 1)
    {
        var game = new TestGame(seed: seed, largeFileBytes: BigFile);
        var torrent = await TorrentBuilder.BuildAsync(game.GameDir);
        return (torrent, game);
    }

    private static async Task<TimeSpan> DownloadAsync(TorrentEngine seederEngine, TorrentEngine leechEngine, BuiltTorrent torrent, string parent)
    {
        var seed = seederEngine.Add(torrent.TorrentBytes, parent);
        seed.Start();
        await seed.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

        var target = TestGame.NewTempDir();
        try
        {
            var leech = leechEngine.Add(torrent.TorrentBytes, target);
            var sw = Stopwatch.StartNew();
            leech.Start();
            await leech.WaitForCompletionAsync(TimeSpan.FromSeconds(60));
            return sw.Elapsed;
        }
        finally { TestGame.DeleteQuietly(target); }
    }

    [Theory]
    [InlineData(1_000_000, 4, 250_000)]   // shared by four transfers
    [InlineData(64_000, 1, 64_000)]
    [InlineData(1_000, 1, 16_384)]        // never starved to nothing
    public void A_limit_is_split_evenly_with_a_floor(int total, int active, int expected) =>
        Assert.Equal(expected, TorrentEngine.EvenShare(total, active));

    [Fact]
    public void Zero_active_transfers_or_no_limit_never_divides_by_zero()
    {
        Assert.Null(TorrentEngine.EvenShare(0, 3));
        Assert.Null(TorrentEngine.EvenShare(null, 0));
        Assert.Equal(2_000_000, TorrentEngine.EvenShare(2_000_000, 0));
    }

    [Fact]
    public async Task Download_limit_really_slows_a_download_on_the_local_network()
    {
        var (torrent, game) = await MakeGameAsync();
        using (game)
        using (var seeder = new TorrentEngine(Local()))
        using (var leech = new TorrentEngine(Local(down: 3_000_000)))
        {
            var took = await DownloadAsync(seeder, leech, torrent, game.ParentDir);

            double rate = torrent.TotalSize / took.TotalSeconds;
            Assert.True(rate < 5_500_000, $"limited to 3 MB/s but ran at {rate / 1_000_000:F1} MB/s ({took.TotalSeconds:F1} s)");
            Assert.True(took < TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task Upload_limit_really_slows_the_sender_on_the_local_network()
    {
        var (torrent, game) = await MakeGameAsync();
        using (game)
        using (var seeder = new TorrentEngine(Local(up: 3_000_000)))
        using (var leech = new TorrentEngine(Local()))
        {
            var took = await DownloadAsync(seeder, leech, torrent, game.ParentDir);

            double rate = torrent.TotalSize / took.TotalSeconds;
            Assert.True(rate < 5_500_000, $"sender limited to 3 MB/s but ran at {rate / 1_000_000:F1} MB/s ({took.TotalSeconds:F1} s)");
        }
    }

    [Fact]
    public async Task A_limit_changed_while_downloading_applies_to_the_running_transfer()
    {
        var (torrent, game) = await MakeGameAsync();
        var target = TestGame.NewTempDir();
        try
        {
            using (game)
            using (var seeder = new TorrentEngine(Local()))
            using (var leech = new TorrentEngine(Local(down: 500_000))) // 11.5 MB at 0.5 MB/s would take about 23 s
            {
                var seed = seeder.Add(torrent.TorrentBytes, game.ParentDir);
                seed.Start();
                await seed.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

                var t = leech.Add(torrent.TorrentBytes, target);
                var sw = Stopwatch.StartNew();
                t.Start();
                await t.WaitUntilAsync(s => s.Progress > 0.03, "started", TimeSpan.FromSeconds(20));
                Assert.False(t.GetStatus().IsComplete);

                leech.SetLimits(null, null);
                await t.WaitForCompletionAsync(TimeSpan.FromSeconds(15));

                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"still slow after the limit was lifted: {sw.Elapsed.TotalSeconds:F1} s");
            }
        }
        finally { TestGame.DeleteQuietly(target); }
    }

    [Fact]
    public async Task Two_downloads_share_the_total_limit_instead_of_each_getting_all_of_it()
    {
        var (torrentA, gameA) = await MakeGameAsync(seed: 1);
        var (torrentB, gameB) = await MakeGameAsync(seed: 2);
        var targetA = TestGame.NewTempDir();
        var targetB = TestGame.NewTempDir();
        try
        {
            using (gameA) using (gameB)
            using (var seeder = new TorrentEngine(Local()))
            using (var leech = new TorrentEngine(Local(down: 4_000_000)))
            {
                var sa = seeder.Add(torrentA.TorrentBytes, gameA.ParentDir);
                var sb = seeder.Add(torrentB.TorrentBytes, gameB.ParentDir);
                sa.Start(); sb.Start();
                await Task.WhenAll(sa.WaitForCompletionAsync(TimeSpan.FromSeconds(30)), sb.WaitForCompletionAsync(TimeSpan.FromSeconds(30)));

                var a = leech.Add(torrentA.TorrentBytes, targetA);
                var b = leech.Add(torrentB.TorrentBytes, targetB);
                var sw = Stopwatch.StartNew();
                a.Start(); b.Start();
                await Task.WhenAll(a.WaitForCompletionAsync(TimeSpan.FromSeconds(60)), b.WaitForCompletionAsync(TimeSpan.FromSeconds(60)));

                double total = torrentA.TotalSize + torrentB.TotalSize;
                double rate = total / sw.Elapsed.TotalSeconds;
                Assert.True(rate < 7_500_000, $"two downloads with a 4 MB/s total ran at {rate / 1_000_000:F1} MB/s together");
            }
        }
        finally { TestGame.DeleteQuietly(targetA); TestGame.DeleteQuietly(targetB); }
    }
}
