using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace GameShare.Discovery.Tests;

/// <summary>A pretend LAN. Every datagram sent by one endpoint reaches all endpoints, including the sender.</summary>
internal sealed class InMemoryBus
{
    private readonly List<Endpoint> _endpoints = [];

    public Endpoint Join(string address)
    {
        var e = new Endpoint(this, IPAddress.Parse(address));
        lock (_endpoints) _endpoints.Add(e);
        return e;
    }

    private void Broadcast(byte[] payload, IPAddress source)
    {
        Endpoint[] all;
        lock (_endpoints) all = [.. _endpoints];
        foreach (var e in all) e.Inbox.Writer.TryWrite(new Datagram(payload, source));
    }

    public sealed class Endpoint(InMemoryBus bus, IPAddress source) : IDatagramTransport
    {
        internal Channel<Datagram> Inbox { get; } = Channel.CreateUnbounded<Datagram>();

        /// <summary>The address other peers will see. Changing it simulates a DHCP renewal.</summary>
        public IPAddress SourceAddress { get; set; } = source;

        /// <summary>Drops everything sent. Simulates a crashed PC or a pulled cable: no goodbye, just silence.</summary>
        public bool Muted { get; set; }

        public ValueTask SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            if (!Muted) bus.Broadcast(payload, SourceAddress);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<Datagram> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var d in Inbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return d;
        }

        public void Dispose() => Inbox.Writer.TryComplete();
    }
}

/// <summary>One agent under test: a discovery service on a transport, with its events recorded.</summary>
internal sealed class Node : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _run;

    public DiscoveryService Service { get; }
    public IDatagramTransport Transport { get; }
    public ConcurrentQueue<PeerEvent> Events { get; } = new();
    public string MachineId { get; }

    public Node(string name, IDatagramTransport transport, TimeSpan? helloInterval = null, TimeSpan? timeout = null)
    {
        MachineId = "id-" + name;
        Transport = transport;
        Service = new DiscoveryService(
            new DiscoveryOptions
            {
                MachineId = MachineId,
                MachineName = name,
                AgentPort = 5150,
                HelloInterval = helloInterval ?? TimeSpan.FromMilliseconds(300),
                PeerTimeout = timeout ?? TimeSpan.FromMilliseconds(1000),
            },
            transport);
        Service.PeerEventRaised += (_, e) => Events.Enqueue(e);
    }

    public void Start() => _run = Service.RunAsync(_cts.Token);

    /// <summary>Graceful shutdown: sends a goodbye.</summary>
    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        if (_run is not null) await _run;
    }

    public IEnumerable<string> PeerNames => Service.Peers.Select(p => p.MachineName);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Transport.Dispose();
        _cts.Dispose();
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for: {what}");
            await Task.Delay(20);
        }
    }
}
