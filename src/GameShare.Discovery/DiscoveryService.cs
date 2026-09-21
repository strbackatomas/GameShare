using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameShare.Discovery;

public sealed record DiscoveryOptions
{
    public required string MachineId { get; init; }
    public required string MachineName { get; init; }

    /// <summary>Port of this agent's HTTP API, which peers use to fetch manifests and torrents.</summary>
    public required int AgentPort { get; init; }

    public TimeSpan HelloInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A peer not heard from for this long is considered gone. Must cover a few missed hellos.</summary>
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(35);
}

/// <summary>
/// Announces this agent, listens for others and keeps <see cref="Peers"/> up to date.
/// Detects arrival, graceful departure, disappearance, and a peer changing IP address, port or name.
/// </summary>
public sealed class DiscoveryService
{
    private readonly DiscoveryOptions _options;
    private readonly IDatagramTransport _transport;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly PeerRegistry _registry = new();
    private readonly HashSet<string> _reportedVersions = [];

    // A new peer is answered with an immediate hello so it learns about us in well under a second
    // instead of waiting up to a full hello interval. Bounded to one pending request.
    private readonly Channel<bool> _replyRequests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public DiscoveryService(
        DiscoveryOptions options, IDatagramTransport transport, ILogger<DiscoveryService>? logger = null, TimeProvider? timeProvider = null)
    {
        if (options.PeerTimeout < options.HelloInterval * 2)
            throw new ArgumentException("PeerTimeout must be at least twice the HelloInterval, or peers would flap.", nameof(options));

        _options = options;
        _transport = transport;
        _log = logger ?? NullLogger<DiscoveryService>.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised on a background thread. Handlers must be quick and must not throw.</summary>
    public event EventHandler<PeerEvent>? PeerEventRaised;

    public IReadOnlyList<PeerInfo> Peers => _registry.Snapshot();

    /// <summary>Runs until cancelled. Sends a goodbye on the way out so peers notice immediately.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("Discovery started as {MachineName} ({MachineId}), agent port {Port}",
            _options.MachineName, _options.MachineId, _options.AgentPort);

        try
        {
            await Task.WhenAll(
                HelloLoopAsync(cancellationToken),
                ReceiveLoopAsync(cancellationToken),
                SweepLoopAsync(cancellationToken),
                ReplyLoopAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }

        await SendAsync(DiscoveryMessage.Goodbye, CancellationToken.None, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        _log.LogInformation("Discovery stopped");
    }

    private Task SendAsync(string type, CancellationToken ct, TimeSpan? timeout = null) =>
        SafeAsync(async () =>
        {
            var message = new DiscoveryMessage(type, DiscoveryMessage.CurrentVersion, _options.MachineId, _options.MachineName, _options.AgentPort);
            using var cts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
            using var linked = cts is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            await _transport.SendAsync(message.Serialize(), linked?.Token ?? ct).ConfigureAwait(false);
        }, $"send {type}");

    private async Task HelloLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.HelloInterval, _time);
        do { await SendAsync(DiscoveryMessage.Hello, ct).ConfigureAwait(false); }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task ReplyLoopAsync(CancellationToken ct)
    {
        while (await _replyRequests.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            _replyRequests.Reader.TryRead(out _);
            // Jitter avoids every agent answering a newcomer at the same instant.
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(0, 300)), _time, ct).ConfigureAwait(false);
            await SendAsync(DiscoveryMessage.Hello, ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), _time, ct).ConfigureAwait(false); // at most one reply per second
        }
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.HelloInterval / 2, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            foreach (var e in _registry.ExpireOlderThan(_time.GetUtcNow() - _options.PeerTimeout))
                Publish(e);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var datagram in _transport.ReceiveAsync(ct).ConfigureAwait(false))
                    Handle(datagram);
                if (!ct.IsCancellationRequested)
                    await Task.Delay(TimeSpan.FromSeconds(1), _time, ct).ConfigureAwait(false); // transport ended early, retry
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Discovery receive loop failed, retrying in 1 second");
                await Task.Delay(TimeSpan.FromSeconds(1), _time, ct).ConfigureAwait(false);
            }
        }
    }

    private void Handle(Datagram datagram)
    {
        if (!DiscoveryMessage.TryParse(datagram.Payload, out var msg, out var error))
        {
            _log.LogDebug("Ignored bad discovery datagram from {Source}: {Error}", datagram.Source, error);
            return;
        }
        if (msg.MachineId == _options.MachineId) return; // our own echo

        if (msg.Version != DiscoveryMessage.CurrentVersion)
        {
            if (_reportedVersions.Add($"{msg.MachineId}:{msg.Version}"))
                _log.LogWarning("Ignoring {MachineName} at {Source}: it speaks discovery protocol {Version}, this agent speaks {Ours}",
                    msg.MachineName, datagram.Source, msg.Version, DiscoveryMessage.CurrentVersion);
            return;
        }

        var evt = msg.Type == DiscoveryMessage.Goodbye
            ? _registry.OnGoodbye(msg.MachineId)
            : _registry.OnHello(msg, datagram.Source, _time.GetUtcNow());
        if (evt is null) return;

        if (evt.Kind == PeerEventKind.Joined) _replyRequests.Writer.TryWrite(true);
        Publish(evt);
    }

    private void Publish(PeerEvent e)
    {
        switch (e.Kind)
        {
            case PeerEventKind.Joined:
                _log.LogInformation("Peer discovered: {Name} {Address}", e.Peer.MachineName, e.Peer.Address);
                break;
            case PeerEventKind.Changed:
                _log.LogInformation("Peer changed: {Name} {OldAddress}:{OldPort} -> {Address}:{Port}",
                    e.Peer.MachineName, e.Previous!.Address, e.Previous.AgentPort, e.Peer.Address, e.Peer.AgentPort);
                break;
            case PeerEventKind.Left:
                _log.LogInformation("Peer lost: {Name} {Address} ({Reason})", e.Peer.MachineName, e.Peer.Address, e.Reason);
                break;
        }

        try { PeerEventRaised?.Invoke(this, e); }
        catch (Exception ex) { _log.LogError(ex, "A discovery event handler threw for {Kind} {Name}", e.Kind, e.Peer.MachineName); }
    }

    private async Task SafeAsync(Func<Task> action, string what)
    {
        try { await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { _log.LogWarning(ex, "Discovery could not {What}, will retry on the next interval", what); }
    }
}
