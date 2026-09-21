using System.Threading.Channels;
using GameShare.Core;
using GameShare.Discovery;
using GameShare.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace GameShare.Agent;

/// <summary>The hub the GUI connects to. Server to client only, the GUI reads state through the REST API.</summary>
public sealed class EventsHub : Hub;

/// <summary>
/// Listens to everything happening inside the agent and pushes it to connected GUIs.
/// Events go through one queue and one sender, so a client sees them in the order they happened
/// and a slow client can never block discovery or downloads.
/// </summary>
public sealed class EventBridge : IHostedService
{
    private readonly IHubContext<EventsHub> _hub;
    private readonly DiscoveryService _discovery;
    private readonly PeerCatalog _catalog;
    private readonly GameLibrary _library;
    private readonly DownloadManager _downloads;
    private readonly SeedManager _seeds;
    private readonly GameChangeTracker _changes;
    private readonly TrustService _trust;
    private readonly RunningGames _running;
    private readonly GameView _view;
    private readonly ILogger<EventBridge> _log;
    private readonly Channel<Func<Task<(string Name, object Payload)?>>> _queue =
        Channel.CreateUnbounded<Func<Task<(string, object)?>>>(new UnboundedChannelOptions { SingleReader = true });

    // Only touched by the single sender loop. The last state sent per game version, so a game that vanishes can still be described.
    private readonly Dictionary<string, GameDto> _lastKnown = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;
    private Task? _sender;

    public EventBridge(
        IHubContext<EventsHub> hub, DiscoveryService discovery, PeerCatalog catalog, GameLibrary library,
        DownloadManager downloads, SeedManager seeds, GameChangeTracker changes, TrustService trust, RunningGames running, GameView view, ILogger<EventBridge> log)
    {
        _running = running;
        _changes = changes;
        _trust = trust;
        _hub = hub;
        _discovery = discovery;
        _catalog = catalog;
        _library = library;
        _downloads = downloads;
        _seeds = seeds;
        _view = view;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stop = new CancellationTokenSource();
        _sender = Task.Run(() => SendLoopAsync(_stop.Token), CancellationToken.None);

        _discovery.PeerEventRaised += OnPeer;
        _catalog.Changed += OnCatalogChanged;
        _library.GameDiscovered += OnLocalGameDiscovered;
        _library.InstallationChanged += OnInstallationChanged;
        _downloads.DownloadEventRaised += OnDownload;
        _seeds.SeedEventRaised += OnSeed;
        _changes.Changed += OnTrackedChange;
        _trust.Changed += OnTrustChanged;
        _running.Changed += OnRunningChanged;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _discovery.PeerEventRaised -= OnPeer;
        _catalog.Changed -= OnCatalogChanged;
        _library.GameDiscovered -= OnLocalGameDiscovered;
        _library.InstallationChanged -= OnInstallationChanged;
        _downloads.DownloadEventRaised -= OnDownload;
        _seeds.SeedEventRaised -= OnSeed;
        _changes.Changed -= OnTrackedChange;
        _trust.Changed -= OnTrustChanged;
        _running.Changed -= OnRunningChanged;

        _queue.Writer.TryComplete();
        if (_sender is not null)
        {
            // Give queued events a moment to flush, but never hold up shutdown for a stuck client.
            try { await _sender.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { /* stop anyway */ }
        }
        _stop?.Cancel();
    }

    private void Enqueue(Func<Task<(string, object)?>> produce) => _queue.Writer.TryWrite(produce);
    private void Enqueue(string name, object payload) => Enqueue(() => Task.FromResult<(string, object)?>((name, payload)));

    private async Task SendLoopAsync(CancellationToken ct)
    {
        await foreach (var produce in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                if (await produce().ConfigureAwait(false) is not var (name, payload)) continue;
                await _hub.Clients.All.SendAsync(name, payload, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Could not push an event to the GUI"); }
        }
    }

    private void OnPeer(object? sender, PeerEvent e) =>
        Enqueue(e.Kind == PeerEventKind.Left ? GameShareEvents.PeerDisconnected : GameShareEvents.PeerConnected, _view.ToDto(e.Peer));

    private void OnCatalogChanged(object? sender, CatalogChange change)
    {
        foreach (var hash in change.ContentHashes) Enqueue(() => GameChangedAsync(hash));
    }

    private void OnInstallationChanged(object? sender, InstallationChange change)
    {
        Enqueue(() => GameChangedAsync(change.Current.ContentHash));
        if (change.Previous is not null && change.Previous.ContentHash != change.Current.ContentHash)
            Enqueue(() => GameChangedAsync(change.Previous.ContentHash));
    }

    /// <summary>What the game changed while it was in use is part of its card: how many files, and which patterns would hide them.</summary>
    private void OnTrackedChange(object? sender, TrackedChange change) =>
        Enqueue(() => GameChangedAsync(change.Installation.ContentHash));

    /// <summary>The card of a game that started or stopped running changes its buttons.</summary>
    private void OnRunningChanged(object? sender, RunningChange change) =>
        Enqueue(() => GameChangedAsync(change.Installation.ContentHash));

    /// <summary>A new list can change the badge of every game.</summary>
    private void OnTrustChanged(object? sender, EventArgs e) =>
        Enqueue(async () =>
        {
            foreach (var g in await _view.ListGamesAsync().ConfigureAwait(false))
            {
                var hash = g.ContentHash;
                Enqueue(() => GameChangedAsync(hash));
            }
            return null;
        });

    private void OnLocalGameDiscovered(object? sender, LibraryGame g) =>
        Enqueue(() => GameChangedAsync(g.Stored.Manifest.ContentHash));

    /// <summary>
    /// First sighting of a version is "discovered", later changes are "updated", and a version that has left the game list
    /// is "removed" with what was last known about it.
    /// </summary>
    private async Task<(string, object)?> GameChangedAsync(string hash)
    {
        var game = await _view.GetGameAsync(hash).ConfigureAwait(false);
        if (game is null)
            return _lastKnown.Remove(hash, out var last) ? (GameShareEvents.GameRemoved, last) : null;

        bool first = !_lastKnown.ContainsKey(hash);
        _lastKnown[hash] = game;
        return (first ? GameShareEvents.GameDiscovered : GameShareEvents.GameUpdated, game);
    }

    private void OnDownload(object? sender, DownloadEvent e)
    {
        var name = e.Kind switch
        {
            DownloadEventKind.Started or DownloadEventKind.Resumed => GameShareEvents.DownloadStarted,
            DownloadEventKind.Progress => GameShareEvents.DownloadProgress,
            DownloadEventKind.Paused => GameShareEvents.DownloadPaused,
            DownloadEventKind.Completed => GameShareEvents.DownloadCompleted,
            DownloadEventKind.Failed => GameShareEvents.DownloadFailed,
            _ => GameShareEvents.DownloadCancelled,
        };
        Enqueue(name, _view.ToDto(e.Status));

        // The card in the GUI changes state, so tell it about the game too.
        if (e.Kind is DownloadEventKind.Started or DownloadEventKind.Completed or DownloadEventKind.Failed or DownloadEventKind.Cancelled)
        {
            var hash = e.Status.ContentHash;
            Enqueue(() => GameChangedAsync(hash));
        }
    }

    private void OnSeed(object? sender, SeedEvent e) =>
        Enqueue(e.Kind == SeedEventKind.Started ? GameShareEvents.SeedStarted : GameShareEvents.SeedStopped,
            GameView.ToDto(e, e.Installation.ContentHash));
}
