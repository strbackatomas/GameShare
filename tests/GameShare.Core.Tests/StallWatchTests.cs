namespace GameShare.Core.Tests;

public class StallWatchTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 14, 0, 0, DateTimeKind.Utc);

    /// <summary>Observes the same bytes every half second, like the download tick, and returns what was asked for when.</summary>
    private static List<(double Seconds, StallAction Action)> Quiet(StallWatch w, double seconds, long bytes = 1000, int peers = 1, bool downloading = true)
    {
        var acted = new List<(double, StallAction)>();
        for (double t = 0; t <= seconds; t += 0.5)
            if (w.Observe(T0.AddSeconds(t), bytes, peers, downloading) is var a and not StallAction.None) acted.Add((t, a));
        return acted;
    }

    [Fact]
    public void A_connected_download_that_gets_nothing_is_nudged_after_5_s_and_restarted_after_15_s_and_again_every_15_s()
    {
        var acted = Quiet(new StallWatch(), 45);
        Assert.Equal([(5.0, StallAction.Nudge), (15.0, StallAction.Restart), (20.0, StallAction.Nudge), (30.0, StallAction.Restart),
            (35.0, StallAction.Nudge), (45.0, StallAction.Restart)], acted);
    }

    [Fact]
    public void Any_progress_starts_the_count_again()
    {
        var w = new StallWatch();
        Assert.Equal(StallAction.None, w.Observe(T0, 1000, 1, true));
        Assert.Equal(StallAction.None, w.Observe(T0.AddSeconds(4), 1000, 1, true));
        Assert.Equal(StallAction.None, w.Observe(T0.AddSeconds(4.5), 2000, 1, true)); // a piece came in
        Assert.Equal(StallAction.None, w.Observe(T0.AddSeconds(9), 2000, 1, true));
        Assert.Equal(StallAction.Nudge, w.Observe(T0.AddSeconds(9.5), 2000, 1, true));
    }

    [Fact]
    public void Nothing_is_done_without_a_source_or_while_checking()
    {
        Assert.Empty(Quiet(new StallWatch(), 60, peers: 0)); // it waits for a source, the UI says so
        Assert.Empty(Quiet(new StallWatch(), 60, downloading: false)); // checking files, or done
    }

    [Fact]
    public void Losing_the_source_in_between_starts_the_count_again()
    {
        var w = new StallWatch();
        Assert.Equal(StallAction.None, w.Observe(T0, 1000, 1, true));
        Assert.Equal(StallAction.None, w.Observe(T0.AddSeconds(4), 1000, 0, true)); // the restart dropped it, or the source left
        Assert.Equal(StallAction.None, w.Observe(T0.AddSeconds(6), 1000, 1, true)); // back, but only just
        Assert.Equal(StallAction.Nudge, w.Observe(T0.AddSeconds(11), 1000, 1, true));
    }
}
