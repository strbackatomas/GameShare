using System.Net;
using System.Net.Http.Json;
using System.Text;
using GameShare.Protocol;

namespace GameShare.Agent.Tests;

/// <summary>
/// Preparing a PC for a game through the agent's API: the agent plans and checks, remembers when it was done, and asks again
/// when the setup or the folder changes. Running the steps is the client's (see GameShare.Client.Tests.SetupStepsTests).
/// </summary>
public class SetupApiTests
{
    private const long SmallGame = 2_000_000;

    private static string Definition(string requires = "") => $$"""
        {
          "gameId": "testgame", "name": "Test Game", "version": "1",
          "launch": [ { "executable": "Game.exe" } ],
          "setup": {
            "requires": [ {{requires}} ],
            "registry": [ { "file": "registry-import.reg", "originalPath": "C:\\Games\\Test Game" } ],
            "compatibility": [ { "executable": "Game.exe", "layers": "WINXPSP3" } ],
            "profile": [ { "from": "Test Game-profile", "to": "{Documents}\\Test Game" } ]
          }
        }
        """;

    private const string Reg = "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\Test Vendor\\Test Game]\r\n\"Path\"=\"C:\\\\Games\\\\Test Game\"\r\n";

    private static Action<string> Game(string definition) => folder =>
    {
        File.WriteAllText(Path.Combine(folder, "gameshare.json"), definition);
        File.WriteAllBytes(Path.Combine(folder, "registry-import.reg"), [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Reg)]);
        Directory.CreateDirectory(Path.Combine(folder, "Test Game-profile"));
        File.WriteAllText(Path.Combine(folder, "Test Game-profile", "settings.ini"), "x=1");
    };

    private static Task<TestAgent> StartAsync(string definition) =>
        TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true, bigFileBytes: SmallGame, customiseGame: Game(definition));

    [Fact]
    public async Task A_game_with_setup_needs_it_until_it_is_done_and_again_after_a_reset()
    {
        await using var pc = await StartAsync(Definition());
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed, "the game to be scanned");
        Assert.True(game.NeedsSetup);

        var plan = await pc.GetAsync<SetupPlanDto>($"/api/games/{game.ContentHash}/setup");
        Assert.Null(plan.Blocked);
        Assert.Equal([SetupStepKind.RegistryImport, SetupStepKind.Compatibility, SetupStepKind.Profile], plan.Steps.Select(s => s.Kind));
        Assert.False(plan.NeedsAdmin); // everything here is the player's own
        Assert.Contains(pc.InstalledPath.Replace("\\", "\\\\"), plan.Steps[0].Content); // pointed at this PC's folder

        var wrong = await pc.SendAsync(HttpMethod.Post, $"/api/games/{game.ContentHash}/setup/done", new SetupDoneRequest("not-the-plan"));
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);

        var done = await pc.SendAsync(HttpMethod.Post, $"/api/games/{game.ContentHash}/setup/done", new SetupDoneRequest(plan.SetupHash));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.False((await done.Content.ReadFromJsonAsync<GameDto>(TestAgent.Json))!.NeedsSetup);

        var reset = await pc.SendAsync(HttpMethod.Delete, $"/api/games/{game.ContentHash}/setup");
        Assert.True((await reset.Content.ReadFromJsonAsync<GameDto>(TestAgent.Json))!.NeedsSetup);
    }

    [Fact]
    public async Task Changing_the_setup_in_gameshare_json_asks_for_preparing_again()
    {
        await using var pc = await StartAsync(Definition());
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed, "the game to be scanned");
        var plan = await pc.GetAsync<SetupPlanDto>($"/api/games/{game.ContentHash}/setup");
        await pc.SendAsync(HttpMethod.Post, $"/api/games/{game.ContentHash}/setup/done", new SetupDoneRequest(plan.SetupHash));
        await pc.WaitForGameAsync(g => !g.NeedsSetup, "the setup to be done");

        File.WriteAllText(Path.Combine(pc.InstalledPath, "gameshare.json"), Definition().Replace("WINXPSP3", "WIN98"));
        await pc.SendAsync(HttpMethod.Post, "/api/games/scan");

        var changed = await pc.WaitForGameAsync(g => g.NeedsSetup, "the changed setup to need preparing again");
        Assert.Equal(game.ContentHash, changed.ContentHash); // the same game, only how it is prepared changed
    }

    [Fact]
    public async Task Asking_how_far_a_scan_got_when_none_runs_says_so()
    {
        await using var pc = await StartAsync(Definition());
        await pc.WaitForGameAsync(g => g.State == GameState.Installed, "the first scan to finish");

        var response = await pc.SendAsync(HttpMethod.Get, "/api/games/scan");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task A_required_redistributable_without_a_package_blocks_the_preparation_and_says_why()
    {
        await using var pc = await StartAsync(Definition("\"directx9\""));
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed, "the game to be scanned");

        var plan = await pc.GetAsync<SetupPlanDto>($"/api/games/{game.ContentHash}/setup");

        Assert.NotNull(plan.Blocked);
        Assert.Contains("directx9", plan.Blocked);
        Assert.Null(plan.MissingRedistContentHash); // nobody offers the package here
    }
}
