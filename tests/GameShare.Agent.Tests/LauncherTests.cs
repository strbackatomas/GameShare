using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameShare.Core;
using GameShare.Core.Data;
using GameShare.Protocol;
using GameShare.Storage;
using GameShare.Torrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameShare.Agent.Tests;

/// <summary>
/// Starting games. The agent decides what may be started and watches whether it runs, the client does the starting.
/// These tests start a real program, a copy of ping.exe that lives in the game folder and runs for a while.
/// </summary>
public class LauncherTests
{
    private const long SmallGame = 2_000_000;
    private const string Program = "launch.exe";

    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "ping.exe");

    /// <summary>A game with a program that runs for a while, and optionally a definition that says it starts the game.</summary>
    private static Action<string> Game(string? executable = Program, string arguments = "-n 40 127.0.0.1", string? workingDirectory = null) => folder =>
    {
        File.Copy(Ping, Path.Combine(folder, Program));
        if (executable is null) return;
        var definition = new Dictionary<string, object?>
        {
            ["gameId"] = "testgame", ["name"] = "Test Game", ["version"] = "1", ["executable"] = executable, ["arguments"] = arguments,
        };
        if (workingDirectory is not null) definition["workingDirectory"] = workingDirectory;
        File.WriteAllText(Path.Combine(folder, "gameshare.json"), JsonSerializer.Serialize(definition));
    };

    private static Task<TestAgent> StartAsync(Action<string> game, Action<AgentOptions>? tweak = null) =>
        TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true, bigFileBytes: SmallGame, customiseGame: game, tweak: o =>
        {
            o.RunningCheckInterval = TimeSpan.FromMilliseconds(300);
            tweak?.Invoke(o);
        });

    private static async Task<HttpResponseMessage> PostAsync(TestAgent a, string path, object? body = null) => await a.SendAsync(HttpMethod.Post, path, body);

    private static async Task<LaunchInfoDto> LaunchAsync(TestAgent a, string hash)
    {
        var response = await PostAsync(a, $"/api/games/{hash}/launch");
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"launch answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<LaunchInfoDto>(TestAgent.Json))!;
    }

    /// <summary>What the client does with the answer.</summary>
    private static Process Start(LaunchInfoDto info) =>
        Process.Start(new ProcessStartInfo(info.ExecutablePath, info.Arguments ?? "")
        {
            WorkingDirectory = info.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;

    private static async Task<string> Refusal(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_game_is_started_from_its_definition_the_agent_sees_it_run_and_keeps_its_hands_off_the_files_meanwhile()
    {
        await using var pc = await StartAsync(Game());
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed && g.Launch == LaunchState.Ready, "the game to be ready to start");
        Assert.False(game.IsRunning);

        var info = await LaunchAsync(pc, game.ContentHash);
        Assert.Equal(Path.Combine(pc.InstalledPath, Program), info.ExecutablePath);
        Assert.Equal("-n 40 127.0.0.1", info.Arguments);
        Assert.Equal(pc.InstalledPath, info.WorkingDirectory);

        using var process = Start(info);
        try
        {
            await pc.WaitForGameAsync(g => g.IsRunning, "the agent to see the game run");

            // A game that runs holds its files. Nothing may rewrite them, and it cannot be started twice.
            Assert.Contains("running", await Refusal(await PostAsync(pc, $"/api/games/{game.ContentHash}/repair")));
            Assert.Contains("running", await Refusal(await PostAsync(pc, $"/api/games/{game.ContentHash}/register")));
            Assert.Contains("running", await Refusal(await PostAsync(pc, $"/api/games/{game.ContentHash}/volatile", new AddVolatileRequest(["saves/**"]))));
            Assert.Contains("already running", await Refusal(await PostAsync(pc, $"/api/games/{game.ContentHash}/launch")));
        }
        finally { process.Kill(entireProcessTree: true); }

        await pc.WaitForGameAsync(g => !g.IsRunning, "the agent to see the game stop");
        await LaunchAsync(pc, game.ContentHash); // and it can be started again
    }

    [Fact]
    public async Task While_a_game_runs_its_seed_steps_aside_and_it_sends_again_when_the_game_is_closed()
    {
        await using var pc = await StartAsync(Game());
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed && g.Launch == LaunchState.Ready, "the game to be ready to start");
        var engine = pc.Services.GetRequiredService<TorrentEngine>();
        await Poll.UntilAsync(() => engine.Transfers.Count == 1 && !engine.Transfers.Single().IsStopped, "the game to be seeded");

        using var process = Start(await LaunchAsync(pc, game.ContentHash));
        try
        {
            await Poll.UntilAsync(() => engine.Transfers.Single().Suspended, "the seed to step aside");
            Assert.True(engine.Transfers.Single().IsStopped);
            // The game is still listed and offered as before, it just does not send while it is played.
            Assert.Equal(GameState.Installed, (await pc.GamesAsync()).Single().State);
        }
        finally { process.Kill(entireProcessTree: true); }

        await Poll.UntilAsync(() => !engine.Transfers.Single().Suspended && !engine.Transfers.Single().IsStopped, "the seed to send again");
    }

    [Fact]
    public async Task A_seed_that_stepped_aside_stays_stopped_when_something_asks_it_to_check_or_start()
    {
        await using var pc = await StartAsync(Game());
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed && g.Launch == LaunchState.Ready, "the game to be ready to start");
        var engine = pc.Services.GetRequiredService<TorrentEngine>();
        var seeds = pc.Services.GetRequiredService<SeedManager>();
        var db = pc.Services.GetRequiredService<GameShareDb>();
        await Poll.UntilAsync(() => engine.Transfers.Count == 1, "the game to be seeded");
        var installation = (await db.ListInstallationsAsync()).Single();

        await seeds.SuspendAsync(installation);
        await seeds.RecheckAsync(installation);   // the tracker does this when the game changed a file
        await seeds.StartAsync(installation);     // and so does anything that wants the game offered

        var transfer = engine.Transfers.Single();
        Assert.True(transfer.Suspended);
        Assert.True(transfer.IsStopped); // reading the folder now would hold its files open under the game

        await seeds.ResumeAsync(installation);
        Assert.False(transfer.Suspended);
        Assert.False(transfer.IsStopped);
    }

    [Fact]
    public async Task A_game_that_changed_files_while_it_was_played_is_checked_when_it_is_closed_and_not_before()
    {
        await using var pc = await StartAsync(Game());
        await pc.WaitForGameAsync(g => g.State == GameState.Installed && g.Launch == LaunchState.Ready, "the game to be ready to start");
        var engine = pc.Services.GetRequiredService<TorrentEngine>();
        var seeds = pc.Services.GetRequiredService<SeedManager>();
        var installation = (await pc.Services.GetRequiredService<GameShareDb>().ListInstallationsAsync()).Single();
        await Poll.UntilAsync(() => engine.Transfers.Count == 1 && engine.Transfers.Single().GetPiecesHave().All(have => have), "the whole game to be seeded");
        var transfer = engine.Transfers.Single();

        await seeds.SuspendAsync(installation);
        TestGame.CorruptOneByte(Path.Combine(pc.InstalledPath, "content", "big.pak")); // the game rewrites a byte
        await seeds.RecheckAsync(installation); // what the watcher does when it notices
        await Task.Delay(1500);

        // Reading the folder now would hold its files open under the game, so nothing was read: the seed still claims what it claimed.
        Assert.All(transfer.GetPiecesHave(), have => Assert.True(have));

        await seeds.ResumeAsync(installation);
        await Poll.UntilAsync(() => transfer.GetPiecesHave().Count(have => !have) == 1, "the changed piece to be found once the game is closed");
    }

    [Theory]
    [InlineData(@"..\..\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\server\share\evil.exe")]
    [InlineData("content/sub/tiny.txt")]
    public async Task A_definition_that_names_something_outside_the_game_or_not_a_program_starts_nothing(string executable)
    {
        await using var pc = await StartAsync(Game(executable));
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed, "the game to be installed");

        Assert.Equal(LaunchState.NeedsExecutable, game.Launch); // the player is asked which program instead
        var message = await Refusal(await PostAsync(pc, $"/api/games/{game.ContentHash}/launch"));
        Assert.Contains("cannot be started", message);
    }

    [Fact]
    public async Task A_program_that_is_not_one_of_the_games_files_is_not_started_even_when_it_exists()
    {
        await using var pc = await StartAsync(Game("dropped.exe"));
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed, "the game to be installed");
        File.Copy(Ping, Path.Combine(pc.InstalledPath, "dropped.exe")); // not there when the game was verified

        var message = await Refusal(await PostAsync(pc, $"/api/games/{game.ContentHash}/launch"));

        Assert.Contains("not one of the files", message);
    }

    [Fact]
    public async Task A_program_that_changed_is_not_started_but_other_files_of_the_game_changing_is_only_playing()
    {
        await using var pc = await StartAsync(Game());
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed && g.Launch == LaunchState.Ready, "the game to be ready to start");

        File.WriteAllText(Path.Combine(pc.InstalledPath, "content", "sub", "tiny.txt"), "settings=changed!");
        await LaunchAsync(pc, game.ContentHash); // a game changes its settings, that is no reason not to start it

        await using (var fs = new FileStream(Path.Combine(pc.InstalledPath, Program), FileMode.Append)) fs.WriteByte(0);
        var message = await Refusal(await PostAsync(pc, $"/api/games/{game.ContentHash}/launch"));

        Assert.Contains("not what it was when the game was verified", message);
        Assert.Contains("Repair", message);
    }

    [Fact]
    public async Task A_missing_program_is_reported_and_points_to_the_repair()
    {
        await using var pc = await StartAsync(Game());
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed && g.Launch == LaunchState.Ready, "the game to be ready to start");
        File.Delete(Path.Combine(pc.InstalledPath, Program));

        var message = await Refusal(await PostAsync(pc, $"/api/games/{game.ContentHash}/launch"));

        Assert.Contains("missing", message);
    }

    [Fact]
    public async Task The_working_directory_of_the_definition_is_used_when_it_is_a_folder_of_the_game()
    {
        await using var pc = await StartAsync(Game(workingDirectory: "content"));
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed && g.Launch == LaunchState.Ready, "the game to be ready to start");

        var info = await LaunchAsync(pc, game.ContentHash);

        Assert.Equal(Path.Combine(pc.InstalledPath, "content"), info.WorkingDirectory);
    }

    [Fact]
    public async Task The_player_picks_the_program_of_a_game_that_does_not_say_which_one_starts_it()
    {
        await using var pc = await StartAsync(Game(executable: null));
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed, "the game to be installed");
        Assert.Equal(LaunchState.NeedsExecutable, game.Launch);
        Assert.Contains("does not say which program starts it", await Refusal(await PostAsync(pc, $"/api/games/{game.ContentHash}/launch")));

        var candidates = await pc.GetAsync<List<string>>($"/api/games/{game.ContentHash}/executables");
        Assert.Contains(Program, candidates);
        Assert.All(candidates, c => Assert.EndsWith(".exe", c, StringComparison.OrdinalIgnoreCase)); // programs, not every file

        var chosen = await pc.SendAsync(HttpMethod.Put, $"/api/games/{game.ContentHash}/launcher", new LauncherChoiceRequest(Program, "-n 3 127.0.0.1"));
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        Assert.Equal(LaunchState.Ready, (await pc.GamesAsync()).Single().Launch);
        var info = await LaunchAsync(pc, game.ContentHash);
        Assert.Equal("-n 3 127.0.0.1", info.Arguments);
    }

    [Theory]
    [InlineData("not-there.exe")]
    [InlineData("content/sub/tiny.txt")]
    [InlineData(@"..\evil.exe")]
    [InlineData("")]
    public async Task A_program_that_is_not_part_of_the_game_cannot_be_chosen(string executable)
    {
        await using var pc = await StartAsync(Game(executable: null));
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed, "the game to be installed");

        var response = await pc.SendAsync(HttpMethod.Put, $"/api/games/{game.ContentHash}/launcher", new LauncherChoiceRequest(executable));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(LaunchState.NeedsExecutable, (await pc.GamesAsync()).Single().Launch); // nothing was stored
    }

    [Fact]
    public async Task A_version_the_administrator_withdrew_is_not_started()
    {
        var keys = TrustSigning.GenerateKeyPair();
        var list = Path.Combine(TestGame.NewTempDir(), "trust.json");
        await using var pc = await StartAsync(Game(), o =>
        {
            o.TrustMode = TrustMode.Warn;
            o.TrustListSource = list;
            o.TrustPublicKey = keys.PublicKey;
            o.TrustRefreshInterval = TimeSpan.FromHours(1);
        });
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed && g.Launch == LaunchState.Ready, "the game to be ready to start");
        var payload = new TrustPayload(1, DateTimeOffset.UtcNow, null, [], [new RevokedGame(game.ContentHash, "contains a modified executable")]);
        File.WriteAllBytes(list, TrustSigning.Serialize(TrustSigning.Sign(payload, keys.PrivateKey)));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(pc, "/api/trust/refresh")).StatusCode);

        var message = await Refusal(await PostAsync(pc, $"/api/games/{game.ContentHash}/launch"));

        Assert.Contains("withdrawn by the administrator: contains a modified executable", message);
        TestGame.DeleteQuietly(Path.GetDirectoryName(list)!);
    }

    [Fact]
    public async Task A_game_that_is_not_installed_here_cannot_be_started()
    {
        await using var pc = await StartAsync(Game());
        await pc.WaitForGameAsync(g => g.State == GameState.Installed, "the game to be installed");

        var response = await PostAsync(pc, $"/api/games/{new string('a', 64)}/launch");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- what counts as running ----

    private sealed class Rig : IAsyncDisposable
    {
        public required GameShareDb Db { get; init; }
        public required TestGame Game { get; init; }
        public required Installation Installation { get; init; }
        public List<string> Programs { get; } = [];
        public RunningGames Running { get; set; } = null!;
        public List<RunningChange> Changes { get; } = [];
        private string Dir { get; init; } = "";

        public static async Task<Rig> StartAsync()
        {
            var dir = TestGame.NewTempDir();
            var db = await GameShareDb.OpenAsync(Path.Combine(dir, "gameshare.db"));
            var game = new TestGame();
            var (manifest, _) = await ManifestBuilder.ScanAndBuildAsync(game.GameDir);
            await db.SaveManifestAsync(manifest, null);
            var installation = await db.AddInstallationAsync(manifest.ContentHash, game.GameDir, InstallationState.Installed);
            var rig = new Rig { Db = db, Game = game, Installation = installation, Dir = dir };
            rig.Running = new RunningGames(db, new AgentOptions(), NullLogger<RunningGames>.Instance, () => rig.Programs.ToList());
            rig.Running.Changed += (_, c) => rig.Changes.Add(c);
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            Game.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TestGame.DeleteQuietly(Dir);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_program_inside_the_game_folder_means_the_game_runs_whatever_the_spelling_of_the_path()
    {
        await using var rig = await Rig.StartAsync();
        Assert.False(await rig.Running.IsRunningNowAsync(rig.Installation));

        rig.Programs.Add(Path.Combine(rig.Game.GameDir, "Bin64", "Helper.EXE").ToUpperInvariant());

        Assert.True(await rig.Running.IsRunningNowAsync(rig.Installation));
    }

    [Fact]
    public async Task A_program_in_a_folder_that_only_starts_with_the_same_letters_is_not_this_game()
    {
        await using var rig = await Rig.StartAsync();

        rig.Programs.Add(rig.Game.GameDir + "Two" + Path.DirectorySeparatorChar + "game.exe"); // ...\TestGameTwo\game.exe
        rig.Programs.Add(Path.Combine(Path.GetDirectoryName(rig.Game.GameDir)!, "game.exe"));  // beside the game folder, not in it

        Assert.False(await rig.Running.IsRunningNowAsync(rig.Installation));
    }

    [Fact]
    public async Task Starting_and_stopping_are_each_reported_once()
    {
        await using var rig = await Rig.StartAsync();
        var program = Path.Combine(rig.Game.GameDir, "Game.exe");

        await rig.Running.IsRunningNowAsync(rig.Installation);
        Assert.Empty(rig.Changes);

        rig.Programs.Add(program);
        await rig.Running.IsRunningNowAsync(rig.Installation);
        await rig.Running.IsRunningNowAsync(rig.Installation);
        rig.Programs.Clear();
        await rig.Running.IsRunningNowAsync(rig.Installation);
        await rig.Running.IsRunningNowAsync(rig.Installation);

        Assert.Equal([true, false], rig.Changes.Select(c => c.Running));
        Assert.All(rig.Changes, c => Assert.Equal(rig.Installation.Id, c.Installation.Id));
    }
}
