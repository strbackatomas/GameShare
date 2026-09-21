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

    private static async Task<OfferedGameDto> WaitForPartialOfferAsync(TestAgent a, string hash)
    {
        OfferedGameDto? offer = null;
        await Poll.UntilAsync(async () =>
        {
            var offers = await a.PeerApi.GetFromJsonAsync<List<OfferedGameDto>>("/peer/games", TestAgent.Json);
            offer = offers!.FirstOrDefault(o => o.ContentHash == hash && !o.IsComplete && o.PercentIntact > 0);
            return offer is not null;
        }, $"{a.Name} to offer part of the game");
        return offer!;
    }

    [Fact]
    public async Task A_game_that_changed_is_reported_damaged_but_keeps_offering_the_pieces_that_still_match()
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
        var damaged = await pc01.WaitForGameAsync(g => g.State == GameState.Damaged, "the game to show as damaged");
        Assert.Equal(pc01.InstalledPath, damaged.InstallPath);

        // Not withdrawn. The seed was re-checked, so it offers the pieces that match and says how much that is.
        var offer = await WaitForPartialOfferAsync(pc01, game.ContentHash);
        Assert.InRange(offer.PercentIntact, 1, 99.9);

        // While the files are being re-hashed the seed claims less than it will end up with, never more. Wait until it settles.
        bool[] have = [];
        await Poll.UntilAsync(async () =>
        {
            var pieces = await pc01.PeerApi.GetFromJsonAsync<PieceMapDto>($"/peer/games/{game.ContentHash}/pieces", TestAgent.Json);
            have = PieceMapCodec.Unpack(pieces!);
            return have.Count(h => !h) == 1;
        }, "the re-check to finish with exactly one bad piece");
        Assert.True(have.Count(h => h) > 1); // one flipped byte damages exactly one piece

        // The other PC sees an offer that is only partly there, and is told the game cannot be installed yet.
        var seen = await pc02.WaitForGameAsync(g => g.State == GameState.AvailableOnLan && g.PartialPeerNames.Count == 1, "PC-02 to see the partial offer");
        Assert.Equal(["PC-01"], seen.PartialPeerNames);
        Assert.False(seen.FullyAvailable);
        Assert.InRange(seen.CoveragePercent!.Value, 1, 99.9);
        var refused = await pc02.SendAsync(HttpMethod.Post, $"/api/games/{game.ContentHash}/install");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("cannot be installed yet", await refused.Content.ReadAsStringAsync());

        // A scan keeps reporting it instead of registering a variant.
        var scan = await PostAsync<ScanResultDto>(pc01, "/api/games/scan");
        Assert.Equal([pc01.InstalledPath], scan.Damaged);
        Assert.Equal(0, scan.Added);
    }

    [Fact]
    public async Task Two_damaged_copies_that_are_damaged_in_different_places_complete_a_third_pc_together()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true);
        var v1 = await InstalledGameAsync(pc01);
        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        await pc02.WaitForGameAsync(g => g.State == GameState.AvailableOnLan, "PC-02 to see the offer");
        await InstallAsync(pc02, v1.ContentHash);
        var pristine = TestGame.HashTree(pc01.InstalledPath);

        // Everybody plays. Each copy is damaged, in a different piece.
        TestGame.CorruptOneByteAt(Big(pc01), 0.25);
        TestGame.CorruptOneByteAt(Big(pc02), 0.75);
        await PostAsync<GameChangesDto>(pc01, $"/api/games/{v1.ContentHash}/check");
        await PostAsync<GameChangesDto>(pc02, $"/api/games/{v1.ContentHash}/check");
        await WaitForPartialOfferAsync(pc01, v1.ContentHash);
        await WaitForPartialOfferAsync(pc02, v1.ContentHash);

        // Neither has the whole game, but together they have all of it.
        await using var pc03 = await TestAgent.StartAsync("PC-03", discovery);
        var offered = await pc03.WaitForGameAsync(g => g.State == GameState.AvailableOnLan && g.PartialPeerNames.Count == 2, "PC-03 to see both partial offers");
        Assert.Equal(["PC-01", "PC-02"], offered.PartialPeerNames);
        Assert.True(offered.FullyAvailable, $"coverage was {offered.CoveragePercent}");
        Assert.Null(offered.CoveragePercent);

        await InstallAsync(pc03, v1.ContentHash);

        Assert.Equal(pristine, TestGame.HashTree(pc03.InstalledPath)); // the original, assembled from two damaged copies
        Assert.True((await PostAsync<GameChangesDto>(pc03, $"/api/games/{v1.ContentHash}/check")).IsIntact);
    }

    [Fact]
    public async Task A_third_pc_waits_when_the_partial_copies_have_the_same_hole_and_installs_once_a_whole_copy_appears()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true);
        var v1 = await InstalledGameAsync(pc01);
        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        await pc02.WaitForGameAsync(g => g.State == GameState.AvailableOnLan, "PC-02 to see the offer");
        await InstallAsync(pc02, v1.ContentHash);

        TestGame.CorruptOneByteAt(Big(pc01), 0.5);
        TestGame.CorruptOneByteAt(Big(pc02), 0.5); // the very same piece is lost on both
        await PostAsync<GameChangesDto>(pc01, $"/api/games/{v1.ContentHash}/check");
        await PostAsync<GameChangesDto>(pc02, $"/api/games/{v1.ContentHash}/check");
        await WaitForPartialOfferAsync(pc01, v1.ContentHash);
        await WaitForPartialOfferAsync(pc02, v1.ContentHash);

        await using var pc03 = await TestAgent.StartAsync("PC-03", discovery);
        var stuck = await pc03.WaitForGameAsync(g => g.State == GameState.AvailableOnLan && g.PartialPeerNames.Count == 2 && !g.FullyAvailable, "PC-03 to see that the game is not complete");
        Assert.InRange(stuck.CoveragePercent!.Value, 90, 99.9);
        Assert.Equal(HttpStatusCode.Conflict, (await pc03.SendAsync(HttpMethod.Post, $"/api/games/{v1.ContentHash}/install")).StatusCode);

        // PC-01 puts the original byte back and checks again. It is whole once more, and PC-03 can install.
        TestGame.CorruptOneByteAt(Big(pc01), 0.5);
        await PostAsync<GameChangesDto>(pc01, $"/api/games/{v1.ContentHash}/check");
        var ready = await pc03.WaitForGameAsync(g => g.State == GameState.AvailableOnLan && g.FullyAvailable && g.PeerNames.Count >= 1, "PC-03 to see a whole copy");
        Assert.Null(ready.CoveragePercent);
        await InstallAsync(pc03, v1.ContentHash);
        Assert.True((await PostAsync<GameChangesDto>(pc03, $"/api/games/{v1.ContentHash}/check")).IsIntact);
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

    [Fact]
    public async Task A_game_that_rewrites_its_own_file_is_noticed_without_a_check_and_the_suggested_pattern_fixes_it()
    {
        await using var pc = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true, tweak: o =>
        {
            o.ChangeQuietPeriod = TimeSpan.FromMilliseconds(300);
            o.SeedIdleRelease = TimeSpan.FromSeconds(1); // the seed lets go of the files quickly, so the "game" can save
        });
        var game = await InstalledGameAsync(pc);

        // The game saves its settings: same size, other content. Nobody runs a check.
        // A game saves again and again, and so does this: a save can fail while the seed still has the file open, and one made
        // in the first moments after the game was found can come before anything is watching it.
        var settings = Path.Combine(pc.InstalledPath, "content", "sub", "tiny.txt");
        GameDto? damaged = null;
        for (int save = 0; save < 40 && damaged is null; save++)
        {
            try { await File.WriteAllTextAsync(settings, save % 2 == 0 ? "settings=changed!" : "settings=CHANGED!"); }
            catch (IOException) { await Task.Delay(250); continue; }

            try { damaged = await pc.WaitForGameAsync(g => g.State == GameState.Damaged && g.SuggestedPatterns.Count > 0, "the agent to notice the changed file", timeoutMs: 2_000); }
            catch (TimeoutException) { /* save again */ }
        }
        Assert.NotNull(damaged);
        Assert.Equal(1, damaged.ChangedFileCount);
        Assert.Equal(["content/sub/tiny.txt"], damaged.SuggestedPatterns);

        // Accepting the suggestion is all it takes. The file stops being game content, so the game is a new, intact version.
        await PostAsync<GameDto>(pc, $"/api/games/{game.ContentHash}/volatile", new AddVolatileRequest(damaged.SuggestedPatterns));
        var intact = await pc.WaitForGameAsync(g => g.State == GameState.Installed && g.ContentHash != game.ContentHash, "the game to be installed again");
        Assert.Equal(0, intact.ChangedFileCount);
        Assert.Empty(intact.SuggestedPatterns);
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
