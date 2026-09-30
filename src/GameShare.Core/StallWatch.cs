namespace GameShare.Core;

internal enum StallAction { None, Nudge, Restart }

/// <summary>What is being done about a download that got stuck, so the UI can say so instead of showing a speed of zero.</summary>
public enum DownloadRecovery
{
    None,
    /// <summary>The connected source sent nothing for a while, the missing pieces were asked for again.</summary>
    Retrying,
    /// <summary>That did not help either, the connection was dropped and is being made again.</summary>
    Reconnecting,
}

/// <summary>
/// Notices a download that is connected to a source but receives nothing, and says what to do about it.
/// </summary>
/// <remarks>
/// Measured with three games at once between two engines, about one run in three had a game stop dead for a minute or more,
/// usually near its end, while the connection stayed up. Two kinds: a request the source never answered, which the library waits
/// on for about a minute, and a connection the source already dropped without this side noticing. The first is cured by asking
/// for the missing pieces again (<see cref="StallAction.Nudge"/>), the second only by a new connection (<see cref="StallAction.Restart"/>).
/// With both, no stall lasted longer than about 16 s.
/// </remarks>
internal sealed class StallWatch
{
    /// <summary>Nothing received for this long while connected: ask for the missing pieces again.</summary>
    public static readonly TimeSpan NudgeAfter = TimeSpan.FromSeconds(5);

    /// <summary>Still nothing this long after that: drop the connection and start over.</summary>
    public static readonly TimeSpan RestartAfter = NudgeAfter + TimeSpan.FromSeconds(10);

    private long _lastBytes = -1;
    private DateTime _quietSince;
    private bool _nudged;
    private DateTime _reconnectingSince;

    /// <summary>How long <see cref="DownloadRecovery.Reconnecting"/> is shown without a source before the download counts as waiting for one.</summary>
    public static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(30);

    /// <summary>What was last done about a stall, until data comes in again.</summary>
    public DownloadRecovery Stage { get; private set; }

    /// <param name="now">When the status was taken.</param>
    /// <param name="bytesDone">Bytes the download has, verified.</param>
    /// <param name="peers">PCs it is connected to. Without one there is nothing to wait for, it waits for a source instead.</param>
    /// <param name="downloading">False while it checks its files or is finished.</param>
    public StallAction Observe(DateTime now, long bytesDone, int peers, bool downloading)
    {
        if (!downloading || bytesDone != _lastBytes) Stage = DownloadRecovery.None;
        else if (peers == 0 && (Stage == DownloadRecovery.Retrying || now - _reconnectingSince >= ReconnectGrace))
            Stage = DownloadRecovery.None; // the source is gone, not stuck: the download waits for one, and says so

        if (!downloading || peers == 0 || bytesDone != _lastBytes)
        {
            _lastBytes = bytesDone;
            _quietSince = now;
            _nudged = false;
            return StallAction.None;
        }

        var quiet = now - _quietSince;
        if (quiet >= RestartAfter)
        {
            _quietSince = now; // a restart that did not help is tried again after as long, not on every tick
            _nudged = false;
            Stage = DownloadRecovery.Reconnecting;
            _reconnectingSince = now;
            return StallAction.Restart;
        }
        if (!_nudged && quiet >= NudgeAfter)
        {
            _nudged = true;
            if (Stage != DownloadRecovery.Reconnecting) Stage = DownloadRecovery.Retrying;
            return StallAction.Nudge;
        }
        return StallAction.None;
    }
}
