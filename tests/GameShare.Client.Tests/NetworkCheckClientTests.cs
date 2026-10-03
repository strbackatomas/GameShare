using GameShare.Agent.Tests;
using GameShare.Client.ViewModels;
using GameShare.Protocol;

namespace GameShare.Client.Tests;

public class NetworkCheckClientTests
{
    internal static readonly string[] PrivateAndDomain = ["Private", "Domain"];

    internal static NetworkCheckDto Typical(params NetworkProfileDto[] networks) => new(
        networks,
        [
            new(PortRoles.Control, "TCP", 47701, true, true, false, null),
            new(PortRoles.Peer, "TCP", 47702, true, true, true, PrivateAndDomain),
            new(PortRoles.Discovery, "UDP", 47800, true, true, true, PrivateAndDomain),
            new(PortRoles.Transfer, "TCP", 6881, true, true, true, PrivateAndDomain),
            new(PortRoles.Transfer, "UDP", 6881, true, true, true, PrivateAndDomain),
            new(PortRoles.Remote, "TCP", 47703, false, false, true, PrivateAndDomain),
        ],
        [new("PC-02", "192.168.1.12:47702", true, 2.4, null)],
        null);

    internal static NetworkProfileDto Net(string name, string category, bool isVirtual = false) =>
        new(name, category, [$"Ethernet ({name})"], FirewallOn: true, BlocksAllInbound: false, isVirtual);

    private static async Task<(NetworkCheckViewModel Check, FakeAgent Agent)> RunAsync(NetworkCheckDto result)
    {
        var agent = new FakeAgent { NetworkCheck = result };
        var app = new AppModel(agent, new FakeEvents(), new ImmediateDispatcher());
        await app.StartAsync();
        var check = new NetworkCheckViewModel(app);
        await check.CheckCommand.ExecuteAsync(null);
        return (check, agent);
    }

    [Fact]
    public async Task A_private_network_with_the_installers_rules_is_all_green()
    {
        var (check, _) = await RunAsync(Typical(Net("Domov", "Private")));

        Assert.Equal(CheckLevel.Ok, check.Overall);
        Assert.Equal("Všechno je v pořádku.", check.Summary);
        Assert.False(check.HasAdvice);
        Assert.Equal("Soukromá · Ethernet (Domov)", check.Networks.Single().Detail);
        Assert.Equal("Seznam her pro ostatní PC · TCP 47702", check.Ports[1].Title);
        Assert.Equal("firewall pouští: Soukromá, Doménová", check.Ports[1].Detail);
        Assert.Equal(CheckLevel.Off, check.Ports.Single(p => p.Title.StartsWith("Vzdálená")).Level); // remote management is off
        Assert.Equal("192.168.1.12:47702 · odpovídá (2 ms)", check.Peers.Single().Detail);
    }

    [Fact]
    public async Task A_public_network_is_red_with_the_way_to_make_it_private()
    {
        var (check, _) = await RunAsync(Typical(Net("Síť 3", "Public")));

        Assert.Equal(CheckLevel.Bad, check.Overall);
        Assert.Equal(CheckLevel.Bad, check.Networks.Single().Level);
        Assert.Equal("firewall ho nepouští na síti „Síť 3“ (veřejná)", check.Ports[1].Detail);
        Assert.Contains(check.Advice, a => a.StartsWith("Síť „Síť 3“ je ve Windows nastavená jako veřejná") && a.Contains("Soukromá síť"));
    }

    [Fact]
    public async Task A_public_virtual_adapter_next_to_a_private_network_does_not_matter()
    {
        var (check, _) = await RunAsync(Typical(Net("Domov", "Private"), Net("VirtualBox Host-Only", "Public", isVirtual: true)));

        Assert.Equal(CheckLevel.Ok, check.Overall);
        Assert.Equal(CheckLevel.Off, check.Networks[1].Level);
        Assert.EndsWith("virtuální, GameShare ji nepoužívá", check.Networks[1].Detail);
    }

    [Fact]
    public async Task Missing_rules_a_port_nobody_listens_on_and_a_silent_pc_each_say_what_to_do()
    {
        var result = Typical(Net("Domov", "Private")) with
        {
            Peers = [new("PC-02", "192.168.1.12:47702", false, null, "Timeout")],
        };
        result = result with
        {
            Ports =
            [
                .. result.Ports.Take(3),
                new(PortRoles.Transfer, "TCP", 6881, true, true, true, []), // no rule at all
                new(PortRoles.Transfer, "UDP", 6881, false, true, true, PrivateAndDomain), // something else holds it
            ],
        };

        var (check, _) = await RunAsync(result);

        Assert.Equal(CheckLevel.Bad, check.Overall);
        Assert.Equal("firewall ho nepouští na síti „Domov“ (soukromá)", check.Ports[3].Detail);
        Assert.Equal("agent na něm neposlouchá", check.Ports[4].Detail);
        Assert.Equal("192.168.1.12:47702 · neodpovídá (nic se nevrátilo do 3 s, nejspíš firewall)", check.Peers.Single().Detail);
        Assert.Contains(check.Advice, a => a.StartsWith("Firewall nepouští TCP 6881 na soukromé síti. Spusť znovu install-agent.ps1"));
        Assert.Contains(check.Advice, a => a.StartsWith("Agent neposlouchá na UDP 6881"));
        Assert.Contains(check.Advice, a => a.StartsWith("PC, které neodpovídá"));
    }

    [Fact]
    public async Task The_check_runs_by_itself_the_first_time_the_page_opens_and_then_only_when_asked()
    {
        var agent = new FakeAgent { NetworkCheck = Typical(Net("Domov", "Private")) };
        var app = new AppModel(agent, new FakeEvents(), new ImmediateDispatcher());
        await app.StartAsync();
        var main = new MainViewModel(app);

        main.SelectedItem = main.Items.Single(i => i.Title == "Síť");
        await Poll.UntilAsync(() => main.Network.Check.HasResult, "the check to run");
        main.SelectedItem = main.Items[0];
        main.SelectedItem = main.Items.Single(i => i.Title == "Síť");

        Assert.Equal(1, agent.Calls.Count(c => c == "CheckNetwork"));
    }
}
