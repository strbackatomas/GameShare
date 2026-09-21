using System.Net;
using System.Net.Http.Json;
using GameShare.Protocol;

namespace GameShare.Agent.Tests;

public class AgentApiTests
{
    private static async Task<string> ProblemAsync(HttpResponseMessage r) => (await r.Content.ReadAsStringAsync());

    [Fact]
    public async Task Status_and_settings_round_trip_and_survive_an_agent_restart()
    {
        int discovery = TestAgent.DiscoveryPort();
        var agent = await TestAgent.StartAsync("PC-01", discovery);
        string dir = agent.Dir;
        var status = await agent.GetAsync<StatusDto>("/api/status");
        Assert.Equal("PC-01", status.MachineName);
        Assert.Equal(32, status.MachineId.Length);

        // Trailing slash and duplicate in a different case are the same folder.
        await agent.SetSettingsAsync(new SettingsDto([@"D:\Games\", @"d:\games", @"E:\More"], false, 80, null));
        var saved = await agent.GetAsync<SettingsDto>("/api/settings");
        Assert.Equal([@"D:\Games", @"E:\More"], saved.GameRoots);
        Assert.False(saved.SeedingEnabled);
        Assert.Equal(80, saved.MaxUploadMBps);

        await agent.StopAsync();
        SqliteClear();
        await using var again = await TestAgent.StartAsync("PC-01", discovery, existingDir: dir);
        Assert.Equal(status.MachineId, (await again.GetAsync<StatusDto>("/api/status")).MachineId); // same PC after a restart
        var reloaded = await again.GetAsync<SettingsDto>("/api/settings");
        Assert.Equal(saved.GameRoots, reloaded.GameRoots); // records compare lists by reference, so compare the content
        Assert.Equal(saved.SeedingEnabled, reloaded.SeedingEnabled);
        Assert.Equal(saved.MaxUploadMBps, reloaded.MaxUploadMBps);
    }

    private static void SqliteClear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    [Theory]
    [InlineData("""{"gameRoots":["relative\\path"],"seedingEnabled":true}""", "full path")]
    [InlineData("""{"gameRoots":[""],"seedingEnabled":true}""", "empty")]
    [InlineData("""{"gameRoots":["D:\\Games"],"seedingEnabled":true,"maxUploadMBps":0}""", "MaxUploadMBps")]
    [InlineData("""{"gameRoots":["D:\\Games"],"seedingEnabled":true,"maxDownloadMBps":999999}""", "MaxDownloadMBps")]
    [InlineData("""{"seedingEnabled":true}""", "GameRoots")]
    [InlineData("not json", "")]
    public async Task Invalid_settings_are_rejected_with_a_message_that_names_the_problem(string body, string expectedInMessage)
    {
        await using var agent = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort());

        var response = await agent.Api.PutAsync("/api/settings", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedInMessage, await ProblemAsync(response));
        Assert.Single((await agent.GetAsync<SettingsDto>("/api/settings")).GameRoots); // unchanged
    }

    [Fact]
    public async Task The_two_listeners_keep_their_roles_apart()
    {
        await using var agent = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort());

        Assert.Equal(HttpStatusCode.OK, (await agent.PeerApi.GetAsync("/peer/hello")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.Api.GetAsync("/api/status")).StatusCode);

        // The peer port, which listens on the whole LAN, never exposes the control API.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PeerApi.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PeerApi.PutAsJsonAsync("/api/settings", new SettingsDto([], true, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PeerApi.PostAsync("/api/games/scan", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PeerApi.GetAsync("/hub/events/negotiate")).StatusCode);
        // A forged Host header does not change which socket a request arrived on.
        using var forged = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        forged.Headers.Host = $"127.0.0.1:{agent.LocalPort}";
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PeerApi.SendAsync(forged)).StatusCode);

        // And the control port does not serve the peer API.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Api.GetAsync("/peer/games")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.Api.GetAsync("/anything-else")).StatusCode);
    }

    [Fact]
    public async Task Scan_registers_a_game_and_lists_it_as_installed()
    {
        await using var agent = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true);

        var game = await agent.WaitForGameAsync(g => g.State == GameState.Installed, "the startup scan to register the game");

        Assert.Equal("testgame", game.GameId);
        Assert.Equal(agent.InstalledPath, game.InstallPath);
        Assert.Equal(64, game.ContentHash.Length);

        var scan = await (await agent.SendAsync(HttpMethod.Post, "/api/games/scan")).Content.ReadFromJsonAsync<ScanResultDto>(TestAgent.Json);
        Assert.Equal(0, scan!.Added);
        Assert.Equal(1, scan.Unchanged);

        var one = await agent.GetAsync<GameDto>($"/api/games/{game.ContentHash}");
        Assert.Equal(game.ContentHash, one.ContentHash);
    }

    [Fact]
    public async Task Unknown_and_invalid_requests_get_specific_answers()
    {
        await using var agent = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort());
        string unknown = new('a', 64);

        var notFound = await agent.Api.GetAsync($"/api/games/{unknown}");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Contains("No game with content hash", await ProblemAsync(notFound));

        var badHash = await agent.Api.GetAsync("/api/games/xyz");
        Assert.Equal(HttpStatusCode.BadRequest, badHash.StatusCode);
        Assert.Contains("64 lowercase hexadecimal", await ProblemAsync(badHash));

        var noOffer = await agent.SendAsync(HttpMethod.Post, $"/api/games/{unknown}/install");
        Assert.Equal(HttpStatusCode.NotFound, noOffer.StatusCode);
        Assert.Contains("No PC on the LAN offers", await ProblemAsync(noOffer));

        Assert.Equal(HttpStatusCode.NotFound, (await agent.SendAsync(HttpMethod.Post, "/api/downloads/999/pause")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.SendAsync(HttpMethod.Post, "/api/downloads/999/resume")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.SendAsync(HttpMethod.Delete, "/api/downloads/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.Api.GetAsync("/api/downloads/999")).StatusCode);
    }

    [Fact]
    public async Task Install_without_a_game_folder_or_into_an_unlisted_folder_is_refused()
    {
        await using var agent = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort());
        string hash = new('b', 64);

        var outside = await agent.SendAsync(HttpMethod.Post, $"/api/games/{hash}/install", new InstallRequest(@"C:\Windows\System32"));
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        Assert.Contains("not one of the configured game folders", await ProblemAsync(outside));

        await agent.SetSettingsAsync(new SettingsDto([], true, null, null));
        var none = await agent.SendAsync(HttpMethod.Post, $"/api/games/{hash}/install");
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("No game folder is configured", await ProblemAsync(none));
    }

    [Fact]
    public async Task Peer_api_offers_only_installed_seeding_games_and_never_reveals_paths()
    {
        await using var agent = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true);
        var game = await agent.WaitForGameAsync(g => g.State == GameState.Installed, "the game to be registered");

        var offers = await agent.PeerApi.GetFromJsonAsync<List<OfferedGameDto>>("/peer/games", TestAgent.Json);
        Assert.Equal(game.ContentHash, Assert.Single(offers!).ContentHash);

        var manifestText = await agent.PeerApi.GetStringAsync($"/peer/games/{game.ContentHash}/manifest");
        Assert.DoesNotContain(agent.Dir, manifestText);
        Assert.DoesNotContain(agent.Dir, await agent.PeerApi.GetStringAsync("/peer/games"));
        var torrent = await agent.PeerApi.GetByteArrayAsync($"/peer/games/{game.ContentHash}/torrent");
        Assert.True(torrent.Length > 100);
        Assert.Equal((byte)'d', torrent[0]); // bencoded dictionary

        Assert.Equal(HttpStatusCode.NotFound, (await agent.PeerApi.GetAsync($"/peer/games/{new string('c', 64)}/manifest")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PeerApi.GetAsync("/peer/games/not-a-hash/torrent")).StatusCode);

        // Turning seeding off withdraws the offer at once.
        await agent.SetSettingsAsync((await agent.GetAsync<SettingsDto>("/api/settings")) with { SeedingEnabled = false });
        Assert.Empty((await agent.PeerApi.GetFromJsonAsync<List<OfferedGameDto>>("/peer/games", TestAgent.Json))!);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.PeerApi.GetAsync($"/peer/games/{game.ContentHash}/torrent")).StatusCode);
    }
}
