using GameShare.Agent.Tests;
using GameShare.Client.Services;
using GameShare.Client.ViewModels;
using GameShare.Protocol;
using static GameShare.Client.Tests.Data;

namespace GameShare.Client.Tests;

/// <summary>The remote management page and the window of a managed PC, with a fake agent and then with real ones.</summary>
public class RemoteClientTests
{
    private static async Task<(AppModel App, RemoteViewModel Remote, FakeAgent Agent, FakeEvents Events, FakeRemoteWindows Windows)> StartAsync(FakeAgent? agent = null)
    {
        agent ??= new FakeAgent { Peers = [Peer("p1", "PC-01"), Peer("p2", "PC-02")] };
        var events = new FakeEvents();
        var windows = new FakeRemoteWindows();
        var app = new AppModel(agent, events, new ImmediateDispatcher(), remoteWindows: windows);
        await app.StartAsync();
        var remote = new RemoteViewModel(app);
        await remote.LoadAsync();
        return (app, remote, agent, events, windows);
    }

    [Fact]
    public async Task Turning_it_on_and_opening_a_pairing_shows_the_code_and_cancelling_hides_it()
    {
        var (_, remote, agent, _, _) = await StartAsync();
        Assert.False(remote.Enabled);
        Assert.StartsWith("Vypnuto", remote.StateText);

        remote.Enabled = true;
        await Poll.UntilAsync(() => agent.Calls.Contains("SetRemoteEnabled(True)"), "the switch to reach the agent");
        Assert.StartsWith("Zapnuto", remote.StateText);

        await remote.StartPairingCommand.ExecuteAsync(null);
        Assert.True(remote.HasPairing);
        Assert.Equal("K7QF-M2XP-9HTD", remote.PairingCode);
        Assert.StartsWith("Platí do", remote.PairingUntilText);

        await remote.CancelPairingCommand.ExecuteAsync(null);
        Assert.False(remote.HasPairing);
    }

    [Fact]
    public async Task A_refused_switch_goes_back_and_says_why()
    {
        var (_, remote, agent, _, _) = await StartAsync();
        agent.FailNext["SetRemoteEnabled"] = new AgentException("This build of GameShare does not take remote management.", 409);

        remote.Enabled = true;
        await Poll.UntilAsync(() => agent.Calls.Count(c => c == "GetRemote") >= 2, "the page to read the state again");

        Assert.False(remote.Enabled);
        Assert.Contains("does not take remote management", remote.Message);
    }

    [Fact]
    public async Task Pairing_sends_the_chosen_pc_and_the_code_then_lists_it_and_opens_its_window()
    {
        var (_, remote, agent, _, windows) = await StartAsync();
        Assert.Equal(["PC-01", "PC-02"], remote.Choices.Select(c => c.Name));
        Assert.False(remote.PairCommand.CanExecute(null)); // no PC and no code yet

        remote.SelectedPeer = remote.Choices.Single(c => c.Name == "PC-02");
        remote.CodeInput = " k7qf m2xp 9htd ";
        await remote.PairCommand.ExecuteAsync(null);

        Assert.Contains("Pair(p2|k7qf m2xp 9htd)", agent.Calls); // the agent normalizes the code, the client sends what was typed
        Assert.Equal("", remote.CodeInput);
        Assert.StartsWith("Spárováno s PC-02.", remote.Message);
        var target = Assert.Single(remote.Targets);
        Assert.Equal("PC-02", target.Name);
        Assert.Equal(["PC-01"], remote.Choices.Select(c => c.Name)); // a paired PC is not offered again

        target.ManageCommand.Execute(null);
        Assert.Equal([("p2", "PC-02")], windows.Opened);

        await target.RemoveCommand.ExecuteAsync(null);
        Assert.Empty(remote.Targets);
    }

    [Fact]
    public async Task A_wrong_code_shows_the_agents_reason_and_keeps_nothing()
    {
        var (_, remote, agent, _, _) = await StartAsync();
        agent.FailNext["Pair"] = new AgentException("The code does not match the one PC-02 showed.", 400);
        remote.SelectedPeer = remote.Choices[1];
        remote.CodeInput = "AAAA-BBBB-CCCC";

        await remote.PairCommand.ExecuteAsync(null);

        Assert.Equal("The code does not match the one PC-02 showed.", remote.Message);
        Assert.Equal("AAAA-BBBB-CCCC", remote.CodeInput); // kept, to fix a typo
        Assert.Empty(remote.Targets);
    }

    [Fact]
    public async Task Changes_and_actions_pushed_by_the_agent_show_up_without_reloading()
    {
        var (app, remote, agent, events, _) = await StartAsync(new FakeAgent { Games = [Game(A, "BeamNG.drive", GameState.Installed)] });

        var at = DateTimeOffset.UtcNow;
        events.Raise(GameShareEvents.RemoteChanged, agent.Remote with
        {
            Enabled = true, Listening = true,
            Controllers = [new PairedMachineDto("c1", "PC-01", new string('c', 64), at, null)],
        });
        events.Raise(GameShareEvents.RemoteAction, new RemoteActionDto("c1", "PC-01", $"POST games/{A}/update", 202, at));
        events.Raise(GameShareEvents.RemoteAction, new RemoteActionDto("c1", "PC-01", "POST downloads/3/pause", 409, at));

        Assert.True(remote.Enabled);
        Assert.Equal("PC-01", Assert.Single(remote.Controllers).Name);
        Assert.Equal(2, remote.Recent.Count);
        Assert.EndsWith("PC-01: pozastavení přenosu (nepovedlo se)", remote.Recent[0]);
        Assert.EndsWith("PC-01: aktualizace hry BeamNG.drive", remote.Recent[1]);
        Assert.Equal(1, agent.Calls.Count(c => c == "GetRemote")); // nothing was asked again

        await remote.Controllers[0].RemoveCommand.ExecuteAsync(null);
        Assert.Contains("RemoveController(c1)", agent.Calls);
        _ = app;
    }

    [Fact]
    public async Task The_client_of_a_managed_pc_sends_its_calls_through_the_local_agent()
    {
        var handler = new RecordingHandler();
        var local = new AgentClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:47701") });
        var remote = local.ForTarget("abc123");

        await remote.GetGamesAsync();
        await remote.InstallAsync(A, @"E:\Hry");
        await remote.GetIconAsync(A);
        await local.GetGamesAsync();

        Assert.Equal(
        [
            "GET /api/remote/targets/abc123/api/games",
            $"POST /api/remote/targets/abc123/api/games/{A}/install",
            $"GET /api/remote/targets/abc123/api/games/{A}/icon",
            "GET /api/games",
        ], handler.Requests);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            var body = request.RequestUri.AbsolutePath.EndsWith("/install", StringComparison.Ordinal)
                ? GameShareJson.Serialize(Download(1, A, "G", "Downloading", 0))
                : "[]";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>
    /// The whole thing with real agents: at PC-02 its client turns remote management on and shows a code, at PC-01 the code is
    /// entered, and PC-01's window of PC-02 installs a game there. PC-02's client is told who did it.
    /// </summary>
    [Fact]
    public async Task One_pc_pairs_with_another_and_installs_a_game_there_from_its_window()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true);
        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);

        var (here, _, hereUi) = await ConnectAsync(pc01);
        await using var _h = here;
        var (there, _, thereUi) = await ConnectAsync(pc02);
        await using var _t = there;
        var hereRemote = new RemoteViewModel(here);
        var thereRemote = new RemoteViewModel(there);

        // At PC-02.
        await thereRemote.LoadAsync();
        thereRemote.Enabled = true;
        await thereUi.UntilAsync(() => thereRemote.StateText.Contains("přes port"), "remote management to be on at PC-02");
        await thereRemote.StartPairingCommand.ExecuteAsync(null);
        var code = thereRemote.PairingCode;
        Assert.Matches("^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$", code);

        // At PC-01.
        await hereUi.UntilAsync(() => here.Peers.Count == 1, "PC-01 to see PC-02");
        await hereRemote.LoadAsync();
        hereRemote.SelectedPeer = hereRemote.Choices.Single();
        hereRemote.CodeInput = code;
        await hereRemote.PairCommand.ExecuteAsync(null);
        var target = Assert.Single(hereRemote.Targets);
        Assert.Equal("PC-02", target.Name);
        await thereUi.UntilAsync(() => thereRemote.Controllers.Count == 1 && !thereRemote.HasPairing, "PC-02's page to list PC-01 and drop the code");

        // PC-01's window of PC-02. The real one loads again every two seconds (RemoteSession); here the test does, on its own thread.
        var managed = new AppModel(here.Client.ForTarget(target.MachineId), new SilentEventStream(), hereUi) { IsRemote = true };
        var window = new MainViewModel(managed);
        async Task ReloadUntilAsync(Func<bool> condition, string what, int timeoutMs = 30_000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                await managed.RefreshAllAsync();
                if (condition()) return;
                if (Environment.TickCount64 > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
                await Task.Delay(300);
            }
        }
        await ReloadUntilAsync(() => managed.IsConnected && window.Library.LanGames.Count == 1, "PC-02's library, seen from PC-01");
        Assert.Equal("PC-02", managed.MachineName);

        await window.Library.LanGames.Single().InstallCommand.ExecuteAsync(null);
        await ReloadUntilAsync(() => window.Library.MyGames.Count == 1 && window.Library.MyGames[0].IsInstalled, "the game to be installed on PC-02", 60_000);
        Assert.False(window.Library.MyGames[0].CanPlay); // playing is for the person at PC-02
        Assert.Equal(TestGame.HashTree(pc01.InstalledPath), TestGame.HashTree(pc02.InstalledPath));

        await thereUi.UntilAsync(() => thereRemote.Recent.Count >= 1, "PC-02's client to be told");
        Assert.EndsWith("PC-01: instalace hry TestGame", Assert.Single(thereRemote.Recent));
    }

    private static async Task<(AppModel App, MainViewModel Main, PumpDispatcher Ui)> ConnectAsync(TestAgent agent)
    {
        var uri = new Uri($"http://127.0.0.1:{agent.LocalPort}");
        var ui = new PumpDispatcher();
        var app = new AppModel(AgentClient.Create(uri), new AgentEventStream(uri), ui);
        var main = new MainViewModel(app);
        await app.StartAsync();
        return (app, main, ui);
    }
}
