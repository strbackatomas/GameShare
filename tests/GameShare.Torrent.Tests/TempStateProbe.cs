using GameShare.Storage;
using TorrentSharp.Wrap;
using TorrentSharp.Wrap.Configurations;
using TorrentSharp.Wrap.Configurations.Settings;
using Xunit.Abstractions;

namespace GameShare.Torrent.Tests;

// TEMPORARY probe, deleted after use. Prints the raw state numbers the binding reports in each situation.
public class TempStateProbe(ITestOutputHelper output)
{
    private static TorrentClient NewClient(int? downLimit = null)
    {
        var c = new TorrentClient();
        var pack = new SettingsPack().Set(new ListenInterfaces("0.0.0.0:0")).Set(new EnableLsd(true)).Set(new EnableDht(false))
            .Set(new AllowMultipleConnectionsPerIp(true)).Set(new DownloadRateLimit(downLimit ?? 0));
        c.UpdateSettings(pack);
        return c;
    }

    private async Task<List<string>> WatchAsync(string label, TorrentManager m, Func<bool> stop, int maxMs = 15000)
    {
        var seen = new List<string>(); string? last = null; var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            var s = m.GetCurrentStatus();
            var key = $"{(int)s.State}:{s.State}(done {s.TotalWantedDone * 100 / Math.Max(1, s.TotalWanted)}%)";
            if (key != last) { seen.Add($"+{sw.ElapsedMilliseconds}ms {key}"); last = key; }
            if (stop()) break;
            await Task.Delay(20);
        }
        output.WriteLine($"[{label}] " + string.Join("  ->  ", seen));
        return seen;
    }

    private async Task<long> TimeDownloadAsync(string label, byte[] torrentBytes, string parent, int? globalLimit, int? torrentLimit)
    {
        using var seeder = NewClient();
        var sm = seeder.AttachTorrent(new TorrentInfo(torrentBytes), parent, null); sm.Start();
        while ((int)sm.GetCurrentStatus().State != 5) await Task.Delay(20);

        var target = TestGame.NewTempDir();
        using var leech = NewClient(downLimit: globalLimit);
        var lm = leech.AttachTorrent(new TorrentInfo(torrentBytes), target, null);
        if (torrentLimit is not null) lm.DownloadLimit = torrentLimit.Value;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lm.Start();
        while ((int)lm.GetCurrentStatus().State != 5 && sw.ElapsedMilliseconds < 40000) await Task.Delay(20);
        output.WriteLine($"[{label}] 21.5 MB took {sw.ElapsedMilliseconds} ms  ({21.5 * 1000 / Math.Max(1, sw.ElapsedMilliseconds):F1} MB/s)  torrent limit now {lm.DownloadLimit}");
        TestGame.DeleteQuietly(target);
        return sw.ElapsedMilliseconds;
    }

    [Fact]
    public async Task Limits()
    {
        using var game = new TestGame(largeFileBytes: 20_000_000);
        var scan = await ContentScanner.ScanAsync(game.GameDir);
        var bytes = TorrentBuilder.Build(scan).TorrentBytes;

        await TimeDownloadAsync("no limit", bytes, game.ParentDir, null, null);
        await TimeDownloadAsync("GLOBAL limit 2 MB/s", bytes, game.ParentDir, 2_000_000, null);
        await TimeDownloadAsync("PER-TORRENT limit 2 MB/s", bytes, game.ParentDir, null, 2_000_000);
    }
}
