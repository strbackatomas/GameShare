using Avalonia.Threading;
using GameShare.Client.ViewModels;
using GameShare.Client.Views;

namespace GameShare.Client.Services;

/// <summary>Opens nothing. The default where there is no display, as in tests.</summary>
internal sealed class NoRemoteWindows : IRemoteWindows
{
    public void Open(string machineId, string machineName) { }
}

/// <summary>
/// A managed PC sends no events here: its agent pushes them only to its own client. So its picture is loaded again every
/// few seconds instead, which is the same full load the local client does after a reconnect.
/// </summary>
public sealed class RemoteSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public RemoteSession(AppModel app, TimeSpan interval)
    {
        App = app;
        Interval = interval;
    }

    public AppModel App { get; }
    public TimeSpan Interval { get; }

    /// <summary>Starts loading. Call on the UI thread, the loads apply their results there.</summary>
    public void Start() => _loop = LoopAsync(_stop.Token);

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await App.RefreshAllAsync(ct).ConfigureAwait(true);
            try { await Task.Delay(Interval, ct).ConfigureAwait(true); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(true);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(true); }
            catch (OperationCanceledException) { /* stopped while loading */ }
        }
        _stop.Dispose();
    }
}

/// <summary>An event stream that never has anything to say, for a model fed by <see cref="RemoteSession"/>.</summary>
internal sealed class SilentEventStream : IEventStream
{
    public event EventHandler<AgentEvent>? Received { add { } remove { } }
    public event EventHandler<bool>? ConnectionChanged { add { } remove { } }
    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>One window per managed PC, the same window as this PC's own with only the pages that make sense there.</summary>
public sealed class AvaloniaRemoteWindows(IAgentClient local) : IRemoteWindows
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private readonly Dictionary<string, MainWindow> _open = new(StringComparer.Ordinal);

    public void Open(string machineId, string machineName)
    {
        if (_open.TryGetValue(machineId, out var existing))
        {
            TrayController.Restore(existing);
            return;
        }

        var app = new AppModel(local.ForTarget(machineId), new SilentEventStream(), new AvaloniaDispatcher()) { IsRemote = true, MachineName = machineName };
        var session = new RemoteSession(app, Interval);
        var window = new MainWindow { DataContext = new MainViewModel(app), Title = $"GameShare · správa: {machineName}" };
        _open[machineId] = window;
        window.Closed += async (_, _) =>
        {
            _open.Remove(machineId);
            await session.DisposeAsync().ConfigureAwait(true);
        };
        window.Show();
        Dispatcher.UIThread.Post(session.Start);
    }
}
