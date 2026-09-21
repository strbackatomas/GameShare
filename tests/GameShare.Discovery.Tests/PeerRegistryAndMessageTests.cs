using System.Net;
using System.Text;

namespace GameShare.Discovery.Tests;

public class PeerRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static DiscoveryMessage Hello(string id = "m1", string name = "PC-01", int port = 5150) =>
        new(DiscoveryMessage.Hello, DiscoveryMessage.CurrentVersion, id, name, port);
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    [Fact]
    public void First_hello_is_a_join_and_a_repeat_is_silent_but_refreshes_last_seen()
    {
        var r = new PeerRegistry();

        var joined = r.OnHello(Hello(), Ip("192.168.30.101"), T0);
        var repeat = r.OnHello(Hello(), Ip("192.168.30.101"), T0.AddSeconds(10));

        Assert.Equal(PeerEventKind.Joined, joined!.Kind);
        Assert.Null(repeat);
        Assert.Equal(T0.AddSeconds(10), r.Snapshot().Single().LastSeen);
    }

    [Theory]
    [InlineData("192.168.30.150", "PC-01", 5150)] // new IP
    [InlineData("192.168.30.101", "PC-01", 5151)] // new port
    [InlineData("192.168.30.101", "PC-01-renamed", 5150)] // new name
    public void Changed_address_port_or_name_is_reported_with_the_previous_state(string ip, string name, int port)
    {
        var r = new PeerRegistry();
        r.OnHello(Hello(), Ip("192.168.30.101"), T0);

        var e = r.OnHello(Hello(name: name, port: port), Ip(ip), T0.AddSeconds(10));

        Assert.Equal(PeerEventKind.Changed, e!.Kind);
        Assert.Equal(Ip("192.168.30.101"), e.Previous!.Address);
        Assert.Equal(Ip(ip), e.Peer.Address);
        Assert.Equal(Ip(ip), r.Snapshot().Single().Address);
    }

    [Fact]
    public void Goodbye_removes_the_peer_and_an_unknown_goodbye_is_ignored()
    {
        var r = new PeerRegistry();
        r.OnHello(Hello(), Ip("192.168.30.101"), T0);

        var left = r.OnGoodbye("m1");

        Assert.Equal(PeerEventKind.Left, left!.Kind);
        Assert.Equal(PeerLeftReason.Goodbye, left.Reason);
        Assert.Empty(r.Snapshot());
        Assert.Null(r.OnGoodbye("m1"));
        Assert.Null(r.OnGoodbye("never-seen"));
    }

    [Fact]
    public void Only_silent_peers_expire()
    {
        var r = new PeerRegistry();
        r.OnHello(Hello("old", "PC-old"), Ip("192.168.30.101"), T0);
        r.OnHello(Hello("fresh", "PC-fresh"), Ip("192.168.30.102"), T0.AddSeconds(30));

        var expired = r.ExpireOlderThan(T0.AddSeconds(10)); // heard before T0+10s means silent for too long

        var e = Assert.Single(expired);
        Assert.Equal("old", e.Peer.MachineId);
        Assert.Equal(PeerLeftReason.Timeout, e.Reason);
        Assert.Equal(["PC-fresh"], r.Snapshot().Select(p => p.MachineName));
    }

    [Fact]
    public void Snapshot_is_sorted_by_name_ignoring_case()
    {
        var r = new PeerRegistry();
        r.OnHello(Hello("a", "pc-10"), Ip("192.168.30.110"), T0);
        r.OnHello(Hello("b", "PC-02"), Ip("192.168.30.102"), T0);
        r.OnHello(Hello("c", "PC-07"), Ip("192.168.30.107"), T0);

        Assert.Equal(["PC-02", "PC-07", "pc-10"], r.Snapshot().Select(p => p.MachineName));
    }
}

public class DiscoveryMessageTests
{
    [Fact]
    public void Message_survives_a_round_trip()
    {
        var m = new DiscoveryMessage(DiscoveryMessage.Hello, 1, "abc", "PC-04", 5150);

        Assert.True(DiscoveryMessage.TryParse(m.Serialize(), out var back, out var error), error);
        Assert.Equal(m, back);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("""{"type":"attack","version":1,"machineId":"a","machineName":"b","agentPort":1}""")]
    [InlineData("""{"type":"hello","version":1,"machineId":"","machineName":"b","agentPort":1}""")]
    [InlineData("""{"type":"hello","version":1,"machineId":"a","machineName":"","agentPort":1}""")]
    [InlineData("""{"type":"hello","version":1,"machineId":"a","machineName":"b","agentPort":0}""")]
    [InlineData("""{"type":"hello","version":1,"machineId":"a","machineName":"b","agentPort":70000}""")]
    public void Garbage_is_rejected_with_a_reason(string json)
    {
        Assert.False(DiscoveryMessage.TryParse(Encoding.UTF8.GetBytes(json), out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Oversized_datagram_is_rejected_unread()
    {
        var big = new byte[DiscoveryMessage.MaxSize + 1];
        Assert.False(DiscoveryMessage.TryParse(big, out _, out var error));
        Assert.Contains("size", error);
    }
}
