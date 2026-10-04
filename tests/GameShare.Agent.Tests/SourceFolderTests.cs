using System.Net;
using System.Net.Http.Json;
using GameShare.Protocol;

namespace GameShare.Agent.Tests;

/// <summary>
/// The source folder: untouched copies of games, typically on a second disk, handed out to the other PCs in place of the copies
/// that are played. Real agents, real transfers.
/// </summary>
public class SourceFolderTests
{
    private const long SmallGame = 2_000_000;

    private static string SourceRoot(TestAgent pc) => Path.Combine(pc.Dir, "Source");

    /// <summary>An untouched copy of the test game in the PC's source folder, set in the settings and scanned.</summary>
    private static async Task<GameDto> AddSourceCopyAsync(TestAgent pc, string folderName = "TestGame")
    {
        using (var template = new TestGame(largeFileBytes: SmallGame))
            TestGame.CopyDirectory(template.GameDir, Path.Combine(SourceRoot(pc), folderName));
        await SetSourceRootAsync(pc, SourceRoot(pc));
        Assert.Equal(HttpStatusCode.OK, (await pc.SendAsync(HttpMethod.Post, "/api/games/scan")).StatusCode);
        return await pc.WaitForGameAsync(g => g.SourcePath is not null && g.SourceIntact, $"{pc.Name} to find the source copy");
    }

    private static async Task<HttpResponseMessage> TrySetSourceRootAsync(TestAgent pc, string root)
    {
        var settings = await pc.GetAsync<SettingsDto>("/api/settings");
        return await pc.SendAsync(HttpMethod.Put, "/api/settings", settings with { SourceRoot = root });
    }

    private static async Task SetSourceRootAsync(TestAgent pc, string root)
    {
        var response = await TrySetSourceRootAsync(pc, root);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_source_copy_is_handed_out_whole_while_the_copy_that_is_played_has_lost_a_file()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true, bigFileBytes: SmallGame);
        await pc01.WaitForGameAsync(g => g.State == GameState.Installed, "PC-01 to register its game");
        var game = await AddSourceCopyAsync(pc01);
        Assert.Equal(Path.Combine(SourceRoot(pc01), "TestGame"), game.SourcePath);
        Assert.Equal(pc01.InstalledPath, game.InstallPath); // still played from the game folder

        // Played until a file is gone. Seeded from there, nobody could get this game whole any more.
        File.Delete(Path.Combine(pc01.InstalledPath, "content", "big.pak"));

        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        var offered = await pc02.WaitForGameAsync(g => g.ContentHash == game.ContentHash && g.FullyAvailable, "PC-02 to see the game offered whole");
        var download = await pc02.InstallAsync(offered.ContentHash);
        await pc02.WaitForDownloadAsync(download.Id, "Completed");

        Assert.Equal(TestGame.HashTree(game.SourcePath!), TestGame.HashTree(pc02.InstalledPath));
    }

    [Fact]
    public async Task A_game_that_is_only_in_the_source_folder_is_handed_out_too()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery);
        var game = await AddSourceCopyAsync(pc01);
        Assert.Null(game.InstallPath); // nothing to play here, it is only kept

        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        await pc02.WaitForGameAsync(g => g.ContentHash == game.ContentHash && g.FullyAvailable, "PC-02 to see the game offered whole");
        var download = await pc02.InstallAsync(game.ContentHash);
        await pc02.WaitForDownloadAsync(download.Id, "Completed");

        Assert.Equal(TestGame.HashTree(game.SourcePath!), TestGame.HashTree(pc02.InstalledPath));
    }

    [Fact]
    public async Task A_source_copy_whose_files_changed_is_no_longer_handed_out_and_the_copy_that_is_played_takes_over()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true, bigFileBytes: SmallGame);
        await pc01.WaitForGameAsync(g => g.State == GameState.Installed, "PC-01 to register its game");
        var game = await AddSourceCopyAsync(pc01);

        await File.AppendAllTextAsync(Path.Combine(game.SourcePath!, "content", "sub", "tiny.txt"), "changed");
        Assert.Equal(HttpStatusCode.OK, (await pc01.SendAsync(HttpMethod.Post, "/api/games/scan")).StatusCode);
        await pc01.WaitForGameAsync(g => g.ContentHash == game.ContentHash && !g.SourceIntact, "PC-01 to see the source copy changed");

        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        var download = await pc02.InstallAsync((await pc02.WaitForGameAsync(g => g.ContentHash == game.ContentHash && g.FullyAvailable, "the game offered")).ContentHash);
        await pc02.WaitForDownloadAsync(download.Id, "Completed");
        Assert.Equal(TestGame.HashTree(pc01.InstalledPath), TestGame.HashTree(pc02.InstalledPath));
    }

    [Fact]
    public async Task A_copy_in_a_folder_named_differently_from_the_game_is_refused_with_the_name_it_needs()
    {
        await using var pc = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true, bigFileBytes: SmallGame);
        await pc.WaitForGameAsync(g => g.State == GameState.Installed, "PC-01 to register its game");
        using (var template = new TestGame(largeFileBytes: SmallGame))
            TestGame.CopyDirectory(template.GameDir, Path.Combine(SourceRoot(pc), "TestGame zaloha"));
        await SetSourceRootAsync(pc, SourceRoot(pc));

        var scan = await (await pc.SendAsync(HttpMethod.Post, "/api/games/scan")).Content.ReadFromJsonAsync<ScanResultDto>(TestAgent.Json);

        Assert.Contains(scan!.Errors, e => e.Contains("TestGame zaloha") && e.Contains("'TestGame'"));
        Assert.Null((await pc.GamesAsync()).Single().SourcePath);
    }

    [Fact]
    public async Task The_source_folder_must_be_apart_from_the_game_folders_and_removing_it_forgets_its_copies()
    {
        await using var pc = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true, bigFileBytes: SmallGame);
        await pc.WaitForGameAsync(g => g.State == GameState.Installed, "PC-01 to register its game");

        Assert.Equal(HttpStatusCode.BadRequest, (await TrySetSourceRootAsync(pc, Path.Combine(pc.GamesRoot, "Zdroj"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TrySetSourceRootAsync(pc, pc.Dir)).StatusCode); // the game folder is inside it

        await AddSourceCopyAsync(pc);
        await SetSourceRootAsync(pc, "");
        Assert.Equal(HttpStatusCode.OK, (await pc.SendAsync(HttpMethod.Post, "/api/games/scan")).StatusCode);
        Assert.Null((await pc.GamesAsync()).Single().SourcePath);
        Assert.True(Directory.Exists(Path.Combine(SourceRoot(pc), "TestGame"))); // forgotten, never deleted
    }
}
