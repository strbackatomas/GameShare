namespace GameShare.Core.Tests;

/// <summary>
/// A stall makes the smoothed download rate decay toward zero without ever quite reaching it (see TickAsync's exponential
/// moving average). Dividing the remaining bytes by that near-zero rate must not ask <see cref="TimeSpan"/> for a duration
/// it cannot hold: that used to crash the whole tick, silently freezing every download's progress in the GUI mid-transfer.
/// </summary>
public class DownloadManagerTests
{
    [Fact]
    public void A_near_zero_rate_after_a_long_stall_gives_no_eta_instead_of_overflowing() =>
        Assert.Null(DownloadManager.EstimateEta(remainingBytes: 500_000_000_000, bytesPerSecond: 1e-300));

    [Fact]
    public void A_zero_rate_gives_no_eta() =>
        Assert.Null(DownloadManager.EstimateEta(remainingBytes: 1_000, bytesPerSecond: 0));

    [Fact]
    public void A_normal_rate_gives_the_expected_eta()
    {
        var eta = DownloadManager.EstimateEta(remainingBytes: 1_000_000, bytesPerSecond: 1_000);
        Assert.Equal(TimeSpan.FromSeconds(1000), eta);
    }
}
