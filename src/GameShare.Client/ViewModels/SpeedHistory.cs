using CommunityToolkit.Mvvm.ComponentModel;
using GameShare.Client.Services;

namespace GameShare.Client.ViewModels;

/// <summary>
/// The speed of a transfer over the last few minutes, one sample per second, for a graph like Steam's.
/// The agent reports twice a second, so reports within the same second are merged into one sample, the last one wins.
/// Average and maximum cover everything recorded, not only what still fits into the graph, and each second counts with its last value.
/// </summary>
public sealed partial class SpeedHistory : ObservableObject
{
    /// <summary>Samples kept for the graph: five minutes at one per second.</summary>
    public const int Capacity = 300;

    private readonly List<long> _values = new(Capacity);
    private DateTime _lastSecond = DateTime.MinValue;
    private long _sum;
    private long _count;
    private long _peakOfPastSeconds; // seconds that are over, including those scrolled out of the graph

    /// <summary>Oldest first. Read by the graph when <see cref="Changed"/> fires.</summary>
    public IReadOnlyList<long> Values => _values;

    /// <summary>Raised after every <see cref="Add"/>, so the graph redraws.</summary>
    public event EventHandler? Changed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsText))]
    public partial long Current { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsText))]
    public partial long Average { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsText))]
    public partial long Peak { get; private set; }

    [ObservableProperty] public partial bool HasSamples { get; private set; }

    public string StatsText => $"Aktuálně {Show(Current)} · průměr {Show(Average)} · maximum {Show(Peak)}";

    private static string Show(long bytesPerSecond) => Format.Size(bytesPerSecond) + "/s";

    /// <param name="at">When the speed was measured. Only whole seconds matter.</param>
    public void Add(DateTime at, long bytesPerSecond)
    {
        bytesPerSecond = Math.Max(0, bytesPerSecond);
        var second = new DateTime(at.Ticks - at.Ticks % TimeSpan.TicksPerSecond, at.Kind);
        if (_values.Count > 0 && second == _lastSecond)
        {
            _sum += bytesPerSecond - _values[^1];
            _values[^1] = bytesPerSecond;
        }
        else
        {
            if (_values.Count > 0) _peakOfPastSeconds = Math.Max(_peakOfPastSeconds, _values[^1]);
            if (_values.Count == Capacity) _values.RemoveAt(0);
            _values.Add(bytesPerSecond);
            _sum += bytesPerSecond;
            _count++;
            _lastSecond = second;
        }

        Current = bytesPerSecond;
        Average = _sum / _count;
        // From what each second ended up as: a total is summed from reports arriving one by one, a value in between is not a speed that was reached.
        Peak = Math.Max(_peakOfPastSeconds, bytesPerSecond);
        HasSamples = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
