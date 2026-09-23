using System.Net;

namespace GameShare.Discovery.Tests;

/// <summary>Real service logic and real timers, over a pretend LAN with short intervals.</summary>
public class DiscoveryServiceTests
{
    [Fact]
    public async Task Three_agents_find_each_other_and_none_lists_itself()
    {
        var bus = new InMemoryBus();
        await using var a = new Node("PC-01", bus.Join("192.168.30.101"));
        await using var b = new Node("PC-04", bus.Join("192.168.30.104"));
        await using var c = new Node("PC-07", bus.Join("192.168.30.107"));
        a.Start(); b.Start(); c.Start();

        await Wait.UntilAsync(() => a.Service.Peers.Count == 2 && b.Service.Peers.Count == 2 && c.Service.Peers.Count == 2, "full mesh");

        Assert.Equal(["PC-04", "PC-07"], a.PeerNames);
        Assert.Equal(["PC-01", "PC-07"], b.PeerNames);
        Assert.Equal(["PC-01", "PC-04"], c.PeerNames);
        Assert.Equal(IPAddress.Parse("192.168.30.104"), a.Service.Peers.Single(p => p.MachineName == "PC-04").Address);
    }

    [Fact]
    public async Task Graceful_stop_is_noticed_immediately_via_goodbye()
    {
        var bus = new InMemoryBus();
        // Timeout far longer than the test, so only a goodbye can explain the departure.
        await using var a = new Node("PC-01", bus.Join("192.168.30.101"), TimeSpan.FromMilliseconds(300), TimeSpan.FromMinutes(5));
        await using var b = new Node("PC-04", bus.Join("192.168.30.104"), TimeSpan.FromMilliseconds(300), TimeSpan.FromMinutes(5));
        a.Start(); b.Start();
        await Wait.UntilAsync(() => a.Service.Peers.Count == 1, "a sees b");

        await b.StopAsync();

        await Wait.UntilAsync(() => a.Service.Peers.Count == 0, "a notices b left");
        var left = a.Events.Single(e => e.Kind == PeerEventKind.Left);
        Assert.Equal(PeerLeftReason.Goodbye, left.Reason);
    }

    [Fact]
    public async Task Crashed_agent_disappears_after_the_timeout_without_a_goodbye()
    {
        var bus = new InMemoryBus();
        await using var a = new Node("PC-01", bus.Join("192.168.30.101"));
        var endpointB = bus.Join("192.168.30.104");
        await using var b = new Node("PC-04", endpointB);
        a.Start(); b.Start();
        await Wait.UntilAsync(() => a.Service.Peers.Count == 1, "a sees b");

        endpointB.Muted = true; // pulled cable: b says nothing more, not even goodbye

        await Wait.UntilAsync(() => a.Service.Peers.Count == 0, "a times out b");
        Assert.Equal(PeerLeftReason.Timeout, a.Events.Single(e => e.Kind == PeerEventKind.Left).Reason);
    }

    [Fact]
    public async Task Agent_that_comes_back_after_a_timeout_joins_again()
    {
        var bus = new InMemoryBus();
        await using var a = new Node("PC-01", bus.Join("192.168.30.101"));
        var endpointB = bus.Join("192.168.30.104");
        await using var b = new Node("PC-04", endpointB);
        a.Start(); b.Start();
        await Wait.UntilAsync(() => a.Service.Peers.Count == 1, "a sees b");
        endpointB.Muted = true;
        await Wait.UntilAsync(() => a.Service.Peers.Count == 0, "a times out b");

        endpointB.Muted = false;

        await Wait.UntilAsync(() => a.Service.Peers.Count == 1, "a sees b again");
        Assert.Equal(2, a.Events.Count(e => e.Kind == PeerEventKind.Joined));
    }

    [Fact]
    public async Task Changed_ip_address_is_reported_with_old_and_new_address()
    {
        var bus = new InMemoryBus();
        await using var a = new Node("PC-01", bus.Join("192.168.30.101"));
        var endpointB = bus.Join("192.168.30.104");
        await using var b = new Node("PC-04", endpointB);
        a.Start(); b.Start();
        await Wait.UntilAsync(() => a.Service.Peers.Count == 1, "a sees b");

        endpointB.SourceAddress = IPAddress.Parse("192.168.30.204"); // DHCP gave PC-04 a new lease

        await Wait.UntilAsync(() => a.Events.Any(e => e.Kind == PeerEventKind.Changed), "a sees the change");
        var changed = a.Events.Single(e => e.Kind == PeerEventKind.Changed);
        Assert.Equal(IPAddress.Parse("192.168.30.104"), changed.Previous!.Address);
        Assert.Equal(IPAddress.Parse("192.168.30.204"), changed.Peer.Address);
        Assert.Single(a.Service.Peers); // same machine, not a second peer
    }

    [Fact]
    public async Task Newcomer_learns_about_everyone_long_before_the_next_hello_interval()
    {
        var bus = new InMemoryBus();
        var slow = TimeSpan.FromSeconds(10); // the periodic hello would need up to 10 s
        var timeout = TimeSpan.FromSeconds(35);
        await using var a = new Node("PC-01", bus.Join("192.168.30.101"), slow, timeout);
        await using var b = new Node("PC-04", bus.Join("192.168.30.104"), slow, timeout);
        a.Start(); b.Start();
        await Wait.UntilAsync(() => a.Service.Peers.Count == 1 && b.Service.Peers.Count == 1, "a and b know each other");

        await using var c = new Node("PC-07", bus.Join("192.168.30.107"), slow, timeout);
        c.Start();

        await Wait.UntilAsync(() => c.Service.Peers.Count == 2, "newcomer learns both peers", timeoutMs: 2000);
    }

    [Fact]
    public async Task Peer_speaking_another_protocol_version_is_ignored_not_fatal()
    {
        var bus = new InMemoryBus();
        await using var a = new Node("PC-01", bus.Join("192.168.30.101"));
        var future = bus.Join("192.168.30.199");
        a.Start();

        var v2 = new DiscoveryMessage(DiscoveryMessage.Hello, 2, "future", "PC-future", 5150);
        await future.SendAsync(v2.Serialize(), CancellationToken.None);
        await using var b = new Node("PC-04", bus.Join("192.168.30.104"));
        b.Start();

        await Wait.UntilAsync(() => a.Service.Peers.Count == 1, "a sees the compatible peer");
        Assert.Equal(["PC-04"], a.PeerNames);
    }

    [Fact]
    public async Task Throwing_event_handler_does_not_stop_discovery()
    {
        var bus = new InMemoryBus();
        await using var a = new Node("PC-01", bus.Join("192.168.30.101"));
        a.Service.PeerEventRaised += (_, _) => throw new InvalidOperationException("handler bug");
        a.Start();

        await using var b = new Node("PC-04", bus.Join("192.168.30.104"));
        b.Start();
        await Wait.UntilAsync(() => a.Service.Peers.Count == 1, "a sees b despite the bad handler");

        await using var c = new Node("PC-07", bus.Join("192.168.30.107"));
        c.Start();
        await Wait.UntilAsync(() => a.Service.Peers.Count == 2, "a still works afterwards");
    }

    [Fact]
    public async Task Peers_learn_each_others_app_version()
    {
        var bus = new InMemoryBus();
        await using var a = new Node("PC-01", bus.Join("192.168.30.101"), appVersion: "0.1.0");
        await using var b = new Node("PC-04", bus.Join("192.168.30.104"), appVersion: "0.2.0");
        a.Start(); b.Start();

        await Wait.UntilAsync(() => a.Service.Peers.Count == 1 && b.Service.Peers.Count == 1, "a and b see each other");

        Assert.Equal("0.2.0", a.Service.Peers.Single().AppVersion);
        Assert.Equal("0.1.0", b.Service.Peers.Single().AppVersion);
    }

    [Fact]
    public void Timeout_shorter_than_two_hellos_is_rejected_because_peers_would_flap()
    {
        var options = new DiscoveryOptions
        {
            MachineId = "x", MachineName = "x", AgentPort = 1,
            HelloInterval = TimeSpan.FromSeconds(10), PeerTimeout = TimeSpan.FromSeconds(15),
        };
        Assert.Throws<ArgumentException>(() => new DiscoveryService(options, new InMemoryBus().Join("10.0.0.1")));
    }
}

/// <summary>The same behaviour over real sockets on this machine: multicast and broadcast, not a fake.</summary>
public class UdpTransportTests
{
    [Fact]
    public async Task Two_agents_discover_each_other_over_real_udp_and_see_the_goodbye()
    {
        int port = Random.Shared.Next(41000, 49000); // avoids the real agent port and parallel test runs
        await using var a = new Node("PC-01", new UdpDatagramTransport(port), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        await using var b = new Node("PC-04", new UdpDatagramTransport(port), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        a.Start(); b.Start();

        await Wait.UntilAsync(() => a.Service.Peers.Count == 1 && b.Service.Peers.Count == 1, "both see each other over UDP", timeoutMs: 8000);
        var seen = a.Service.Peers.Single();
        Assert.Equal("PC-04", seen.MachineName);
        Assert.Equal(5150, seen.AgentPort);
        Assert.NotEqual(IPAddress.Any, seen.Address); // the address is the real source of the datagram, not a placeholder

        await b.StopAsync();
        await Wait.UntilAsync(() => a.Service.Peers.Count == 0, "goodbye arrives over UDP", timeoutMs: 4000);
        Assert.Equal(PeerLeftReason.Goodbye, a.Events.Single(e => e.Kind == PeerEventKind.Left).Reason);
    }
}
