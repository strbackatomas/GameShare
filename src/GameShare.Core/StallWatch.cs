namespace GameShare.Core;

internal enum StallAction { None, Nudge, Restart }

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

    /// <param name="now">When the status was taken.</param>
    /// <param name="bytesDone">Bytes the download has, verified.</param>
    /// <param name="peers">PCs it is connected to. Without one there is nothing to wait for, it waits for a source instead.</param>
    /// <param name="downloading">False while it checks its files or is finished.</param>
    public StallAction Observe(DateTime now, long bytesDone, int peers, bool downloading)
    {
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
            return StallAction.Restart;
        }
        if (!_nudged && quiet >= NudgeAfter)
        {
            _nudged = true;
            return StallAction.Nudge;
        }
        return StallAction.None;
    }
}
