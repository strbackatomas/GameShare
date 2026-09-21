using System.Net;
using System.Net.Http.Json;
using GameShare.Protocol;

namespace GameShare.Agent.Tests;

/// <summary>
/// Whole agents talking to each other over real sockets: UDP discovery, the peer HTTP API, the transfer engine and SignalR.
/// The only thing missing compared with a real LAN is that every agent shares one machine.
/// </summary>
public class AgentSwarmTests
{
    private static async Task WaitForFullMeshAsync(int expectedPeers, params TestAgent[] agents)
    {
        foreach (var a in agents)
            await Poll.UntilAsync(async () => (await a.PeersAsync()).Count == expectedPeers, $"{a.Name} to see {expectedPeers} peers");
    }

    [Fact]
    public async Task Game_offered_by_one_pc_is_discovered_installed_and_reoffered_by_the_next_with_live_events()
    {
        int discovery = TestAgent.DiscoveryPort();
        // The GUI is already connected when the other PC shows up, as it would be on a running desktop.
        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        await using var events = await EventRecorder.ConnectAsync(pc02);

        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true);
        var offered = await pc01.WaitForGameAsync(g => g.State == GameState.Installed, "PC-01 to register its game");

        // PC-02 finds PC-01 and learns what it offers, with no configuration and no server.
        var available = await pc02.WaitForGameAsync(g => g.State == GameState.AvailableOnLan, "PC-02 to see the game on the LAN");
        Assert.Equal(offered.ContentHash, available.ContentHash);
        Assert.Equal(["PC-01"], available.PeerNames);
        var peer = Assert.Single(await pc02.PeersAsync());
        Assert.Equal("PC-01", peer.MachineName);
        Assert.Equal(1, peer.GameCount);

        // One click.
        var started = await pc02.InstallAsync(available.ContentHash);
        Assert.Equal("Downloading", started.State);
        var done = await pc02.WaitForDownloadAsync(started.Id, "Completed");
        Assert.Equal(100, done.Percent);

        var installed = await pc02.WaitForGameAsync(g => g.State == GameState.Installed, "PC-02 to list the game as installed");
        Assert.Equal(pc02.InstalledPath, installed.InstallPath);
        Assert.Equal(TestGame.HashTree(pc01.InstalledPath), TestGame.HashTree(pc02.InstalledPath));

        // PC-02 now offers it too.
        await Poll.UntilAsync(async () => (await pc02.PeerApi.GetStringAsync("/peer/games")).Contains(offered.ContentHash), "PC-02 to offer the game");

        // The GUI saw all of it as it happened, in a sensible order.
        await Poll.UntilAsync(() => events.Names.Contains(GameShareEvents.SeedStarted), "SeedStarted event");
        var names = events.Names.ToList();
        Assert.Contains(GameShareEvents.PeerConnected, names);
        Assert.Contains(GameShareEvents.GameDiscovered, names);
        Assert.True(names.IndexOf(GameShareEvents.DownloadStarted) < names.IndexOf(GameShareEvents.DownloadCompleted));
        Assert.True(names.IndexOf(GameShareEvents.DownloadCompleted) < names.IndexOf(GameShareEvents.SeedStarted));

        var progress = events.Of<DownloadDto>(GameShareEvents.DownloadProgress).ToList();
        Assert.NotEmpty(progress);
        Assert.All(progress, p =>
        {
            Assert.Equal(started.Id, p.Id);
            Assert.Equal(offered.TotalSize, p.BytesTotal);
            Assert.InRange(p.Percent, 0, 100);
        });
        Assert.Equal(done.BytesTotal, events.Of<DownloadDto>(GameShareEvents.DownloadCompleted).Single().BytesTotal);
    }

    [Fact]
    public async Task Third_pc_downloads_from_both_and_the_gui_can_see_how_many_sources_it_uses()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true, bigFileBytes: 30_000_000);
        await pc01.WaitForGameAsync(g => g.State == GameState.Installed, "PC-01 to register its game");
        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        var game = await pc02.WaitForGameAsync(g => g.State == GameState.AvailableOnLan, "PC-02 to see the game");
        await pc02.WaitForDownloadAsync((await pc02.InstallAsync(game.ContentHash)).Id, "Completed");

        // Both sources are throttled so the download lasts long enough to observe.
        foreach (var source in new[] { pc01, pc02 })
            await source.SetSettingsAsync((await source.GetAsync<SettingsDto>("/api/settings")) with { MaxUploadMBps = 3 });

        await using var pc03 = await TestAgent.StartAsync("PC-03", discovery);
        await using var events = await EventRecorder.ConnectAsync(pc03);
        await WaitForFullMeshAsync(2, pc01, pc02, pc03);
        var seen = await pc03.WaitForGameAsync(g => g.State == GameState.AvailableOnLan && g.PeerNames.Count == 2, "PC-03 to see both sources");
        Assert.Equal(["PC-01", "PC-02"], seen.PeerNames);

        var d = await pc03.InstallAsync(seen.ContentHash);
        await pc03.WaitForDownloadAsync(d.Id, "Completed");

        // Every agent here shares this machine's IP address, so peers cannot be told apart by name in this test.
        // The number of live connections shows the download used more than one source at the same time.
        var progress = events.Of<DownloadDto>(GameShareEvents.DownloadProgress).ToList();
        Assert.True(progress.Max(p => p.PeerDetails.Count) >= 2, "the download never had two sources connected at once");
        Assert.True(progress.Max(p => p.Peers) >= 2);
        Assert.Equal(TestGame.HashTree(pc01.InstalledPath), TestGame.HashTree(pc03.InstalledPath));
    }

    [Fact]
    public async Task Pc_that_stops_disappears_from_the_others_and_its_offer_goes_with_it()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        await using var events = await EventRecorder.ConnectAsync(pc02);
        var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true);
        await pc01.WaitForGameAsync(g => g.State == GameState.Installed, "PC-01 to register its game");
        await pc02.WaitForGameAsync(g => g.State == GameState.AvailableOnLan, "PC-02 to see the game");

        await pc01.StopAsync(); // service stopped: goodbye goes out

        await Poll.UntilAsync(async () => (await pc02.PeersAsync()).Count == 0, "PC-02 to notice PC-01 left");
        await Poll.UntilAsync(async () => (await pc02.GamesAsync()).Count == 0, "the offer to vanish with its owner");
        await Poll.UntilAsync(() => events.Names.Contains(GameShareEvents.PeerDisconnected), "PeerDisconnected event");
        await Poll.UntilAsync(() => events.Names.Contains(GameShareEvents.GameRemoved), "GameRemoved event");
        Assert.Equal("testgame", events.Of<GameDto>(GameShareEvents.GameRemoved).Single().GameId); // the GUI is told which game left

        // Nothing can be installed from a PC that is gone, and the answer says so.
        var hash = events.Of<GameDto>(GameShareEvents.GameDiscovered).First().ContentHash; // seen while it was on the LAN
        var response = await pc02.SendAsync(HttpMethod.Post, $"/api/games/{hash}/install");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await pc01.DisposeAsync();
    }

    [Fact]
    public async Task Turning_seeding_off_withdraws_the_game_from_the_lan_and_turning_it_on_offers_it_again()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true);
        await pc01.WaitForGameAsync(g => g.State == GameState.Installed, "PC-01 to register its game");
        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        await pc02.WaitForGameAsync(g => g.State == GameState.AvailableOnLan, "PC-02 to see the game");
        var settings = await pc01.GetAsync<SettingsDto>("/api/settings");

        await pc01.SetSettingsAsync(settings with { SeedingEnabled = false });
        await Poll.UntilAsync(async () => (await pc02.GamesAsync()).Count == 0, "the offer to be withdrawn");

        await pc01.SetSettingsAsync(settings with { SeedingEnabled = true });
        await pc02.WaitForGameAsync(g => g.State == GameState.AvailableOnLan, "the game to be offered again");
    }

    [Fact]
    public async Task Pause_resume_and_cancel_work_through_the_api_and_the_gui_is_told()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true);
        await pc01.WaitForGameAsync(g => g.State == GameState.Installed, "PC-01 to register its game");
        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery, tweak: null);
        await pc02.SetSettingsAsync((await pc02.GetAsync<SettingsDto>("/api/settings")) with { MaxDownloadMBps = 2 });
        await using var events = await EventRecorder.ConnectAsync(pc02);
        var game = await pc02.WaitForGameAsync(g => g.State == GameState.AvailableOnLan, "PC-02 to see the game");

        var d = await pc02.InstallAsync(game.ContentHash);
        await Poll.UntilAsync(async () => (await pc02.GetAsync<DownloadDto>($"/api/downloads/{d.Id}")).Percent >= 10, "some progress");

        var paused = await (await pc02.SendAsync(HttpMethod.Post, $"/api/downloads/{d.Id}/pause")).Content.ReadFromJsonAsync<DownloadDto>(TestAgent.Json);
        Assert.Equal("Paused", paused!.State);
        Assert.Equal(GameState.AvailableOnLan, (await pc02.GamesAsync()).Single().State); // paused is not "downloading" any more

        var resumeResponse = await pc02.SendAsync(HttpMethod.Post, $"/api/downloads/{d.Id}/resume");
        Assert.True(resumeResponse.IsSuccessStatusCode, $"resume answered {(int)resumeResponse.StatusCode}: {await resumeResponse.Content.ReadAsStringAsync()}");
        var resumed = await resumeResponse.Content.ReadFromJsonAsync<DownloadDto>(TestAgent.Json);
        Assert.Equal("Downloading", resumed!.State);

        var response = await pc02.SendAsync(HttpMethod.Delete, $"/api/downloads/{d.Id}?deleteFiles=true");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await pc02.GetAsync<List<DownloadDto>>("/api/downloads"));
        Assert.False(Directory.Exists(pc02.InstalledPath));

        await Poll.UntilAsync(() => events.Names.Contains(GameShareEvents.DownloadCancelled), "cancel event");
        Assert.Contains(GameShareEvents.DownloadPaused, events.Names);
        Assert.Contains(GameShareEvents.DownloadStarted, events.Names);

        // Installing again is allowed after a cancel.
        var again = await pc02.InstallAsync(game.ContentHash);
        Assert.NotEqual(d.Id, again.Id);
    }

    [Fact]
    public async Task Installing_a_game_that_is_already_installed_is_a_conflict_with_the_reason()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true);
        var game = await pc01.WaitForGameAsync(g => g.State == GameState.Installed, "PC-01 to register its game");

        var response = await pc01.SendAsync(HttpMethod.Post, $"/api/games/{game.ContentHash}/install");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already installed", await response.Content.ReadAsStringAsync());
    }
}
