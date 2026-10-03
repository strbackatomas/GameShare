using System.Net;
using System.Net.Http.Json;
using GameShare.Protocol;

namespace GameShare.Agent.Tests;

/// <summary>The network check on real agents, on this PC's real firewall and networks. It only reads, so it is safe to run anywhere.</summary>
public class NetworkCheckTests
{
    [Fact]
    public async Task The_check_lists_every_port_with_what_listens_and_reaches_the_other_pc()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery);
        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        await Poll.UntilAsync(async () => (await pc01.PeersAsync()).Count == 1, "PC-01 to see PC-02");

        var check = await pc01.GetAsync<NetworkCheckDto>("/api/network/check");

        PortCheckDto Port(string role, string protocol) => check.Ports.Single(p => p.Role == role && p.Protocol == protocol);
        Assert.Equal(6, check.Ports.Count);
        Assert.Equal(pc01.LocalPort, Port(PortRoles.Control, "TCP").Port);
        Assert.True(Port(PortRoles.Control, "TCP").Listening);
        Assert.False(Port(PortRoles.Control, "TCP").NeedsFirewall);
        Assert.True(Port(PortRoles.Peer, "TCP").Listening);
        Assert.True(Port(PortRoles.Peer, "TCP").NeedsFirewall);
        Assert.True(Port(PortRoles.Discovery, "UDP").Listening);
        Assert.False(Port(PortRoles.Remote, "TCP").Expected); // remote management is off
        Assert.False(Port(PortRoles.Remote, "TCP").Listening);

        // This PC's real firewall and networks were read; what they say depends on the PC, so only that they were read.
        if (OperatingSystem.IsWindows())
        {
            Assert.True(check.FirewallProblem is null, check.FirewallProblem);
            Assert.NotEmpty(check.Networks);
            Assert.All(check.Networks, n => Assert.Contains(n.Category, new[] { "Private", "Domain", "Public" }));
            Assert.All(check.Ports.Where(p => p.NeedsFirewall), p => Assert.NotNull(p.AllowedOn));
        }

        var peer = Assert.Single(check.Peers);
        Assert.Equal("PC-02", peer.Name);
        Assert.True(peer.Reachable, peer.Error);
        Assert.NotNull(peer.Milliseconds);
    }

    [Fact]
    public async Task A_paired_pc_may_run_the_check_on_the_one_it_manages()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var controller = await TestAgent.StartAsync("PC-01", discovery);
        await using var target = await TestAgent.StartAsync("PC-02", discovery);
        await Poll.UntilAsync(async () => (await controller.PeersAsync()).Count == 1, "PC-01 to see PC-02");
        Assert.True((await target.SendAsync(HttpMethod.Put, "/api/remote/enabled", new RemoteEnableRequest(true))).IsSuccessStatusCode);
        var code = (await (await target.SendAsync(HttpMethod.Post, "/api/remote/pairing")).Content.ReadFromJsonAsync<RemotePairingDto>(TestAgent.Json))!.Code;
        var targetId = (await target.GetAsync<StatusDto>("/api/status")).MachineId;
        Assert.True((await controller.SendAsync(HttpMethod.Post, "/api/remote/targets", new RemotePairRequest(targetId, code))).IsSuccessStatusCode);

        var response = await controller.SendAsync(HttpMethod.Get, $"/api/remote/targets/{targetId}/api/network/check");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var check = (await response.Content.ReadFromJsonAsync<NetworkCheckDto>(TestAgent.Json))!;
        var remotePort = check.Ports.Single(p => p.Role == PortRoles.Remote);
        Assert.True(remotePort.Expected && remotePort.Listening); // checked on PC-02, where remote management is on
        Assert.Equal(target.RemotePort, remotePort.Port);
    }
}
