using System.Net;
using System.Net.Http.Json;
using GameShare.Protocol;

namespace GameShare.Agent.Tests;

/// <summary>Looking after installed games through the API: damage, volatile files, registering a patch, repair and update.</summary>
public class AgentCareTests
{
    private static string Big(TestAgent a) => Path.Combine(a.InstalledPath, "content", "big.pak");

    private static async Task<T> PostAsync<T>(TestAgent a, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await a.SendAsync(HttpMethod.Post, path, body);
        Assert.True(response.StatusCode == expected, $"{path} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(TestAgent.Json))!;
    }

    private static async Task<GameDto> InstalledGameAsync(TestAgent a) =>
        await a.WaitForGameAsync(g => g.State == GameState.Installed, $"{a.Name} to list the game as installed");

    /// <summary>Installs the offered game on <paramref name="target"/> through the API and waits for it.</summary>
    private static async Task InstallAsync(TestAgent target, string hash)
    {
        var d = await target.InstallAsync(hash);
        await target.WaitForDownloadAsync(d.Id, "Completed");
        await target.WaitForGameAsync(g => g.ContentHash == hash && g.State == GameState.Installed, $"{target.Name} to list {hash[..8]} as installed");
    }

    [Fact]
    public async Task A_game_that_changed_is_reported_damaged_and_withdrawn_from_the_lan()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        await using var events = await EventRecorder.ConnectAsync(pc02);
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true);
        var game = await InstalledGameAsync(pc01);
        await pc02.WaitForGameAsync(g => g.State == GameState.AvailableOnLan, "PC-02 to see the offer");

        TestGame.CorruptOneByte(Big(pc01)); // a game rewriting a file, same size
        var changes = await PostAsync<GameChangesDto>(pc01, $"/api/games/{game.ContentHash}/check");

        Assert.False(changes.IsIntact);
        Assert.Equal(["content/big.pak"], changes.Modified);
        Assert.Empty(changes.Missing);
        Assert.Equal(["content/**"], changes.SuggestedPatterns);
        var damaged = await pc01.WaitForGameAsync(g => g.State == GameState.Damaged, "the game to show as damaged");
        Assert.Equal(pc01.InstalledPath, damaged.InstallPath);

        // Other PCs stop seeing it at once, nobody is handed data that no longer matches.
        Assert.Empty((await pc01.PeerApi.GetFromJsonAsync<List<OfferedGameDto>>("/peer/games", TestAgent.Json))!);
        await Poll.UntilAsync(async () => (await pc02.GamesAsync()).Count == 0, "PC-02 to lose the offer");
        await Poll.UntilAsync(() => events.Names.Contains(GameShareEvents.GameRemoved), "GameRemoved event on PC-02");

        // A scan keeps reporting it instead of registering a variant.
        var scan = await PostAsync<ScanResultDto>(pc01, "/api/games/scan");
        Assert.Equal([pc01.InstalledPath], scan.Damaged);
        Assert.Equal(0, scan.Added);
    }

    [Fact]
    public async Task Volatile_patterns_can_be_added_through_the_api_and_reach_the_manifest_other_pcs_get()
    {
        await using var pc = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true);
        var game = await InstalledGameAsync(pc);
        Directory.CreateDirectory(Path.Combine(pc.InstalledPath, "saves"));
        await File.WriteAllTextAsync(Path.Combine(pc.InstalledPath, "saves", "slot1.sav"), "level 1");

        var updated = await PostAsync<GameDto>(pc, $"/api/games/{game.ContentHash}/volatile", new AddVolatileRequest(["saves/**"]));

        Assert.Equal(game.ContentHash, updated.ContentHash); // the save was never part of the game, so identity is unchanged
        var manifest = await pc.PeerApi.GetStringAsync($"/peer/games/{game.ContentHash}/manifest");
        Assert.Contains("saves/**", manifest);
        Assert.DoesNotContain("slot1.sav", manifest);

        await File.WriteAllTextAsync(Path.Combine(pc.InstalledPath, "saves", "slot1.sav"), "level 99, changed size and content");
        var changes = await PostAsync<GameChangesDto>(pc, $"/api/games/{game.ContentHash}/check");
        Assert.True(changes.IsIntact);
        Assert.Empty(changes.Added);
        Assert.Equal(GameState.Installed, (await pc.GamesAsync()).Single().State);
    }

    [Theory]
    [InlineData("""{"patterns":["../evil"]}""", "../evil")]
    [InlineData("""{"patterns":["**"]}""", "every file")]
    [InlineData("""{"patterns":[]}""", "at least one pattern")]
    public async Task Bad_volatile_patterns_are_answered_with_400_and_the_reason(string body, string expected)
    {
        await using var pc = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true);
        var game = await InstalledGameAsync(pc);

        var response = await pc.Api.PostAsync($"/api/games/{game.ContentHash}/volatile", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
        Assert.Equal(GameState.Installed, (await pc.GamesAsync()).Single().State); // nothing was changed
    }

    [Fact]
    public async Task Repair_through_the_api_restores_the_files_and_puts_the_game_back_on_the_lan()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true);
        var offered = await InstalledGameAsync(pc01);
        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        await pc02.WaitForGameAsync(g => g.State == GameState.AvailableOnLan, "PC-02 to see the offer");
        await InstallAsync(pc02, offered.ContentHash);
        var original = await File.ReadAllBytesAsync(Big(pc02));

        TestGame.CorruptOneByte(Big(pc02));
        var changes = await PostAsync<GameChangesDto>(pc02, $"/api/games/{offered.ContentHash}/check");
        Assert.Equal(["content/big.pak"], changes.Modified);
        await pc02.WaitForGameAsync(g => g.State == GameState.Damaged, "PC-02 to mark the game damaged");

        var repair = await PostAsync<DownloadDto>(pc02, $"/api/games/{offered.ContentHash}/repair", expected: HttpStatusCode.Accepted);
        Assert.Equal("Repair", repair.Kind);
        await pc02.WaitForDownloadAsync(repair.Id, "Completed");

        Assert.Equal(original, await File.ReadAllBytesAsync(Big(pc02)));
        await pc02.WaitForGameAsync(g => g.State == GameState.Installed, "PC-02 to list the game as installed again");
        Assert.True((await PostAsync<GameChangesDto>(pc02, $"/api/games/{offered.ContentHash}/check")).IsIntact);
        await Poll.UntilAsync(async () => (await pc02.PeerApi.GetStringAsync("/peer/games")).Contains(offered.ContentHash), "PC-02 to offer the game again");
    }

    [Fact]
    public async Task A_patched_game_is_registered_on_the_source_and_the_other_pc_updates_to_it_fetching_little()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true);
        var v1 = await InstalledGameAsync(pc01);
        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        await using var events = await EventRecorder.ConnectAsync(pc02);
        await pc02.WaitForGameAsync(g => g.State == GameState.AvailableOnLan, "PC-02 to see version 1");
        await InstallAsync(pc02, v1.ContentHash);

        // The admin patches the game on PC-01 and registers the result as the new version.
        TestGame.CorruptOneByte(Big(pc01));
        var v2 = await PostAsync<GameDto>(pc01, $"/api/games/{v1.ContentHash}/register");
        Assert.NotEqual(v1.ContentHash, v2.ContentHash);
        Assert.Equal(GameState.Installed, v2.State);

        // PC-02 learns there is a newer version of the game it has, and is told which install it would replace.
        var offer = await pc02.WaitForGameAsync(g => g.ContentHash == v2.ContentHash && g.State == GameState.AvailableOnLan, "PC-02 to see version 2");
        Assert.Equal(v1.ContentHash, offer.UpdatesContentHash);
        await Poll.UntilAsync(() => events.Of<GameDto>(GameShareEvents.GameDiscovered).Any(g => g.ContentHash == v2.ContentHash), "GameDiscovered for version 2");

        var update = await PostAsync<DownloadDto>(pc02, $"/api/games/{v2.ContentHash}/update", expected: HttpStatusCode.Accepted);
        Assert.Equal("Update", update.Kind);
        var done = await pc02.WaitForDownloadAsync(update.Id, "Completed");

        var installed = await pc02.WaitForGameAsync(g => g.ContentHash == v2.ContentHash && g.State == GameState.Installed, "version 2 to be installed");
        Assert.Equal(pc02.InstalledPath, installed.InstallPath);
        Assert.DoesNotContain(await pc02.GamesAsync(), g => g.ContentHash == v1.ContentHash && g.State == GameState.Installed);
        Assert.Equal(TestGame.HashTree(pc01.InstalledPath), TestGame.HashTree(pc02.InstalledPath));
        Assert.Equal(v2.TotalSize, done.BytesTotal);
        await Poll.UntilAsync(async () => (await pc02.PeerApi.GetStringAsync("/peer/games")).Contains(v2.ContentHash), "PC-02 to offer version 2");
    }

    [Fact]
    public async Task Updating_a_game_that_is_not_installed_or_repairing_an_unknown_one_says_why()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true);
        var game = await InstalledGameAsync(pc01);
        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        await pc02.WaitForGameAsync(g => g.State == GameState.AvailableOnLan, "PC-02 to see the offer");

        var nothingToUpdate = await pc02.SendAsync(HttpMethod.Post, $"/api/games/{game.ContentHash}/update");
        Assert.Equal(HttpStatusCode.Conflict, nothingToUpdate.StatusCode);
        Assert.Contains("nothing to update", await nothingToUpdate.Content.ReadAsStringAsync());

        var unknownRepair = await pc02.SendAsync(HttpMethod.Post, $"/api/games/{game.ContentHash}/repair");
        Assert.Equal(HttpStatusCode.NotFound, unknownRepair.StatusCode);
        Assert.Contains("not installed on this PC", await unknownRepair.Content.ReadAsStringAsync());

        var unknownCheck = await pc02.SendAsync(HttpMethod.Post, $"/api/games/{new string('c', 64)}/check");
        Assert.Equal(HttpStatusCode.NotFound, unknownCheck.StatusCode);

        var sameVersion = await pc01.SendAsync(HttpMethod.Post, $"/api/games/{game.ContentHash}/update");
        Assert.Equal(HttpStatusCode.Conflict, sameVersion.StatusCode);
        Assert.Contains("already at this version", await sameVersion.Content.ReadAsStringAsync());
    }
}
