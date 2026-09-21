using GameShare.Client.Services;
using GameShare.Client.ViewModels;
using GameShare.Protocol;
using static GameShare.Client.Tests.Data;

namespace GameShare.Client.Tests;

public class ConnectionTests
{
    private static (AppModel App, FakeAgent Agent, FakeEvents Events) Create()
    {
        var agent = new FakeAgent();
        var events = new FakeEvents();
        return (new AppModel(agent, events, new ImmediateDispatcher()), agent, events);
    }

    [Fact]
    public async Task Startup_loads_the_whole_picture_and_starts_listening_for_events()
    {
        var (app, agent, events) = Create();
        agent.Games = [Game(A, "BeamNG.drive", GameState.Installed)];
        agent.Peers = [Peer("p1", "PC-01"), Peer("p2", "PC-04")];
        agent.Downloads = [Download(1, B, "GTA V", "Downloading", 40)];

        await app.StartAsync();

        Assert.True(app.IsConnected);
        Assert.Equal("PC-07", app.MachineName);
        Assert.Single(app.Games);
        Assert.Equal(["PC-01", "PC-04"], app.Peers.Select(p => p.Name));
        Assert.Single(app.Downloads);
        Assert.True(events.Started);
    }

    [Fact]
    public async Task An_unreachable_agent_is_shown_as_such_and_the_window_does_not_fail()
    {
        var (app, agent, _) = Create();
        agent.Failure = new AgentException("Agent GameShare neodpovídá. Zkontroluj, že služba běží.");

        await app.StartAsync();

        Assert.False(app.IsConnected);
        Assert.Contains("neodpovídá", app.ConnectionText);
        Assert.Empty(app.Games);
    }

    [Fact]
    public async Task When_the_connection_returns_everything_is_loaded_again_so_a_missed_event_cannot_linger()
    {
        var (app, agent, events) = Create();
        agent.Games = [Game(A, "BeamNG.drive", GameState.Installed), Game(B, "GTA V", GameState.AvailableOnLan, peers: ["PC-01"])];
        await app.StartAsync();
        Assert.Equal(2, app.Games.Count);

        events.SetConnected(false);
        Assert.False(app.IsConnected);
        Assert.Contains("přerušilo", app.ConnectionText);

        // While disconnected the world changed and the events were missed.
        agent.Games = [Game(A, "BeamNG.drive", GameState.Installed), Game(C, "ETS2", GameState.AvailableOnLan, peers: ["PC-04"])];
        events.SetConnected(true);
        await Task.Yield();

        Assert.True(app.IsConnected);
        Assert.Equal(["BeamNG.drive", "ETS2"], app.Games.Select(g => g.Name).Order());
    }
}

public class LibraryTests
{
    private static async Task<(MainViewModel Main, AppModel App, FakeAgent Agent, FakeEvents Events)> StartAsync(params GameDto[] games)
    {
        var agent = new FakeAgent { Games = [.. games] };
        var events = new FakeEvents();
        var app = new AppModel(agent, events, new ImmediateDispatcher(), new FakeStarter());
        var main = new MainViewModel(app);
        await app.StartAsync();
        return (main, app, agent, events);
    }

    private static FakeStarter StarterOf(AppModel app) => (FakeStarter)app.Starter;

    [Fact]
    public async Task Games_are_split_into_mine_and_on_the_lan_and_sorted_by_name()
    {
        var (main, _, _, _) = await StartAsync(
            Game(A, "GTA V", GameState.Installed, installPath: @"D:\Games\GTA V"),
            Game(B, "Assetto Corsa", GameState.AvailableOnLan, peers: ["PC-01", "PC-04"]),
            Game(C, "BeamNG.drive", GameState.Damaged, installPath: @"D:\Games\BeamNG"),
            Game(D, "Farming Simulator", GameState.Downloading),
            Game("e" + new string('e', 63), "Old thing nobody offers", GameState.Unavailable));

        Assert.Equal(["BeamNG.drive", "Farming Simulator", "GTA V"], main.Library.MyGames.Select(g => g.Name));
        Assert.Equal(["Assetto Corsa"], main.Library.LanGames.Select(g => g.Name));
        Assert.True(main.Library.HasMyGames);
        Assert.True(main.Library.HasLanGames);
        Assert.False(main.Library.IsEmpty);
    }

    [Fact]
    public async Task With_no_games_at_all_the_library_says_so()
    {
        var (main, _, _, _) = await StartAsync();

        Assert.True(main.Library.IsEmpty);
        Assert.True(main.Library.ShowEmptyHint);
        Assert.False(main.Library.HasMyGames);
        Assert.False(main.Library.HasLanGames);
    }

    [Fact]
    public async Task Without_an_agent_the_library_does_not_claim_there_are_no_games()
    {
        var agent = new FakeAgent { Failure = new AgentException("Agent GameShare neodpovídá. Zkontroluj, že služba běží.") };
        var app = new AppModel(agent, new FakeEvents(), new ImmediateDispatcher());
        var main = new MainViewModel(app);

        await app.StartAsync();

        Assert.True(main.Library.IsEmpty);
        Assert.False(main.Library.ShowEmptyHint); // the banner explains the situation, "no games" would be wrong

        agent.Failure = null;
        await app.RefreshAllAsync();
        Assert.True(main.Library.ShowEmptyHint);
    }

    [Fact]
    public async Task A_window_opened_in_the_middle_of_a_download_shows_its_progress_at_once()
    {
        var agent = new FakeAgent
        {
            Games = [Game(A, "BeamNG.drive", GameState.Downloading)],
            Downloads = [Download(1, A, "BeamNG.drive", "Downloading", 54.2, done: 38_000_000_000, total: 70_000_000_000, speed: 287_000_000, eta: 130)],
        };
        var app = new AppModel(agent, new FakeEvents(), new ImmediateDispatcher());
        var main = new MainViewModel(app);

        await app.StartAsync();

        var card = main.Library.MyGames.Single();
        Assert.Equal(54.2, card.Percent);
        Assert.Contains("54,2 %", card.ProgressText);
    }

    [Fact]
    public async Task A_card_says_what_the_game_is_and_which_actions_apply()
    {
        var (main, _, _, _) = await StartAsync(
            Game(A, "BeamNG.drive", GameState.AvailableOnLan, version: "0.38", size: 70_264_000_000, peers: ["PC-01", "PC-04", "PC-08"]),
            Game(B, "GTA V", GameState.AvailableOnLan, version: null, size: 118_111_600_640, peers: ["PC-01"]),
            Game(C, "Assetto Corsa", GameState.AvailableOnLan, version: "1.2", size: 1_000_000_000, peers: ["PC-01"], updates: D),
            Game(D, "Assetto Corsa", GameState.Installed, version: "1.1", size: 1_000_000_000));

        var beam = main.Library.LanGames.Single(g => g.Name == "BeamNG.drive");
        Assert.Equal("0.38 · 65,4 GB", beam.Details);
        Assert.Equal("Nabízí 3 PC: PC-01, PC-04, PC-08", beam.PeersText);
        Assert.True(beam.CanInstall);
        Assert.False(beam.CanUpdate);
        Assert.Equal("Dostupné na LAN", beam.StateText);

        var gta = main.Library.LanGames.Single(g => g.Name == "GTA V");
        Assert.Equal("110 GB", gta.Details); // no version, only the size
        Assert.Equal("Nabízí 1 PC: PC-01", gta.PeersText);

        // Another version of a game that is installed is offered as an update, never as a second install.
        var update = main.Library.LanGames.Single(g => g.Name == "Assetto Corsa");
        Assert.True(update.CanUpdate);
        Assert.False(update.CanInstall);
        Assert.Equal("Nová verze na LAN", update.StateText);
        Assert.Single(main.Library.MyGames);
    }

    [Fact]
    public async Task Partial_sources_are_named_and_installing_waits_until_the_parts_add_up()
    {
        var (main, _, _, _) = await StartAsync(
            Game(A, "BeamNG.drive", GameState.AvailableOnLan, peers: ["PC-01", "PC-04"]) with
            {
                PartialPeerNames = ["PC-01", "PC-04"], FullyAvailable = false, CoveragePercent = 96.5,
            });

        var card = main.Library.LanGames.Single();

        Assert.Contains("(každé jen část hry)", card.PeersText);
        Assert.False(card.CanInstall);
        Assert.True(card.IsWaitingForParts);
        Assert.Equal("Zatím nekompletní", card.StateText);
        Assert.Contains("96,5 %", card.CoverageText);
        Assert.Contains("dat hry", card.CoverageText);
    }

    [Fact]
    public async Task A_game_that_some_pc_has_whole_is_installable_even_if_other_pcs_only_have_part_of_it()
    {
        var (main, _, _, _) = await StartAsync(
            Game(A, "BeamNG.drive", GameState.AvailableOnLan, peers: ["PC-01", "PC-04"]) with { PartialPeerNames = ["PC-04"] });

        var card = main.Library.LanGames.Single();

        Assert.Contains("(jen část: PC-04)", card.PeersText);
        Assert.True(card.CanInstall);
        Assert.False(card.IsWaitingForParts);
        Assert.Equal("", card.CoverageText);
    }

    [Fact]
    public async Task Parts_that_add_up_make_the_game_installable_when_the_agent_says_so()
    {
        var (main, _, _, events) = await StartAsync(
            Game(A, "BeamNG.drive", GameState.AvailableOnLan, peers: ["PC-01"]) with { PartialPeerNames = ["PC-01"], FullyAvailable = false, CoveragePercent = 90 });
        var card = main.Library.LanGames.Single();
        Assert.False(card.CanInstall);

        events.Raise(GameShareEvents.GameUpdated,
            Game(A, "BeamNG.drive", GameState.AvailableOnLan, peers: ["PC-01", "PC-04"]) with { PartialPeerNames = ["PC-01", "PC-04"], FullyAvailable = true });

        Assert.Same(card, main.Library.LanGames.Single()); // same row, updated in place
        Assert.True(card.CanInstall);
        Assert.Equal("Dostupné na LAN", card.StateText);
    }

    [Fact]
    public async Task Events_move_a_game_between_the_lists_add_new_ones_and_remove_gone_ones()
    {
        var (main, _, _, events) = await StartAsync(Game(A, "BeamNG.drive", GameState.AvailableOnLan, peers: ["PC-01"]));

        events.Raise(GameShareEvents.GameDiscovered, Game(B, "ETS2", GameState.AvailableOnLan, peers: ["PC-04"]));
        Assert.Equal(["BeamNG.drive", "ETS2"], main.Library.LanGames.Select(g => g.Name));

        events.Raise(GameShareEvents.GameUpdated, Game(A, "BeamNG.drive", GameState.Downloading));
        Assert.Equal(["ETS2"], main.Library.LanGames.Select(g => g.Name));
        Assert.Equal(["BeamNG.drive"], main.Library.MyGames.Select(g => g.Name));

        events.Raise(GameShareEvents.GameUpdated, Game(A, "BeamNG.drive", GameState.Installed, installPath: @"D:\Games\BeamNG"));
        Assert.True(main.Library.MyGames.Single().IsInstalled);
        Assert.False(main.Library.MyGames.Single().IsDownloading);

        events.Raise(GameShareEvents.GameRemoved, Game(B, "ETS2", GameState.Unavailable));
        Assert.Empty(main.Library.LanGames);
        Assert.False(main.Library.HasLanGames);
    }

    [Fact]
    public async Task Progress_events_update_the_card_of_the_game_being_downloaded()
    {
        var (main, _, _, events) = await StartAsync(Game(A, "BeamNG.drive", GameState.Downloading));

        events.Raise(GameShareEvents.DownloadProgress,
            Download(1, A, "BeamNG.drive", "Downloading", 54.2, done: 38_000_000_000, total: 70_000_000_000, speed: 287_000_000, eta: 130, sources: [("PC01", 150_000_000), ("PC04", 137_000_000)]));

        var card = main.Library.MyGames.Single();
        Assert.Equal(54.2, card.Percent);
        Assert.Contains("54,2 %", card.ProgressText);
        Assert.Contains("MB/s", card.ProgressText);
        Assert.Contains("2 min 10 s", card.ProgressText);
    }

    [Fact]
    public async Task Install_asks_the_agent_and_says_so_and_a_refusal_shows_the_agents_own_reason()
    {
        var (main, _, agent, _) = await StartAsync(Game(A, "BeamNG.drive", GameState.AvailableOnLan, peers: ["PC-01"]));
        var card = main.Library.LanGames.Single();

        await card.InstallCommand.ExecuteAsync(null);
        Assert.Contains($"Install({A})", agent.Calls);
        Assert.Equal("Instalace začala.", card.Message);

        agent.FailNext["Install"] = new AgentException("Not enough free space on D:\\: BeamNG needs 70 GB, 10 GB are free.", 507);
        await card.InstallCommand.ExecuteAsync(null);
        Assert.Equal("Not enough free space on D:\\: BeamNG needs 70 GB, 10 GB are free.", card.Message);
        Assert.False(card.IsBusy);
    }

    [Fact]
    public async Task While_an_operation_runs_the_buttons_of_that_game_are_off()
    {
        var (main, _, agent, _) = await StartAsync(Game(A, "BeamNG.drive", GameState.AvailableOnLan, peers: ["PC-01"]));
        var card = main.Library.LanGames.Single();
        agent.Gate = new TaskCompletionSource();

        var running = card.InstallCommand.ExecuteAsync(null);

        Assert.True(card.IsBusy);
        Assert.False(card.InstallCommand.CanExecute(null));
        agent.Gate.SetResult();
        await running;
        Assert.False(card.IsBusy);
        Assert.True(card.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_unexpected_failure_is_shown_on_the_card_and_never_thrown_into_the_window()
    {
        var (main, _, agent, _) = await StartAsync(Game(A, "BeamNG.drive", GameState.AvailableOnLan, peers: ["PC-01"]));
        var card = main.Library.LanGames.Single();
        agent.Failure = new AgentException("Agent GameShare neodpovídá. Zkontroluj, že služba běží.");

        await card.InstallCommand.ExecuteAsync(null);

        Assert.Contains("neodpovídá", card.Message);
    }

    [Fact]
    public async Task Update_and_repair_call_the_matching_agent_operations()
    {
        var (main, _, agent, _) = await StartAsync(
            Game(A, "Assetto Corsa", GameState.AvailableOnLan, updates: B, peers: ["PC-01"], version: "1.2"),
            Game(B, "Assetto Corsa", GameState.Damaged, version: "1.1", installPath: @"D:\Games\AC"));

        await main.Library.LanGames.Single().UpdateCommand.ExecuteAsync(null);
        await main.Library.MyGames.Single().RepairCommand.ExecuteAsync(null);

        Assert.Contains($"Update({A})", agent.Calls);
        Assert.Contains($"Repair({B})", agent.Calls);
    }

    [Fact]
    public async Task A_check_that_finds_changes_explains_them_and_the_suggested_patterns_can_be_applied()
    {
        var (main, _, agent, _) = await StartAsync(Game(A, "BeamNG.drive", GameState.Installed, installPath: @"D:\Games\BeamNG"));
        var card = main.Library.MyGames.Single();
        agent.CheckResult = new GameChangesDto(false, ["saves/slot1.sav", "saves/slot2.sav"], [], [], ["saves/**"]);

        await card.CheckCommand.ExecuteAsync(null);

        Assert.Contains("Změněno souborů: 2", card.Message);
        Assert.Contains("dál nabízejí", card.Message); // the unchanged parts of a damaged game are still shared
        Assert.True(card.HasSuggestion);
        Assert.Contains("saves/**", card.Suggestion);

        await card.MarkVolatileCommand.ExecuteAsync(null);

        Assert.Contains($"AddVolatile({A}|saves/**)", agent.Calls);
        Assert.False(card.HasSuggestion);
        Assert.Contains("Označeno", card.Message);
    }

    // ---- starting games ----

    private static GameDto Installed(GameState state = GameState.Installed, LaunchState launch = LaunchState.Ready, bool running = false) =>
        Game(A, "BeamNG.drive", state, installPath: @"D:\Games\BeamNG") with { Launch = launch, IsRunning = running };

    [Theory]
    [InlineData(GameState.Installed, LaunchState.Ready, false, true)]
    [InlineData(GameState.Damaged, LaunchState.Ready, false, true)]      // playing changes files, that is what marks a game
    [InlineData(GameState.Installed, LaunchState.Ready, true, false)]    // it is running already
    [InlineData(GameState.Installed, LaunchState.None, false, false)]    // nothing to start
    [InlineData(GameState.Installed, LaunchState.NeedsExecutable, false, false)]
    public async Task Play_is_offered_for_a_game_that_can_be_started_and_is_not_running(GameState state, LaunchState launch, bool running, bool canPlay)
    {
        var (main, _, _, _) = await StartAsync(Installed(state, launch, running));

        Assert.Equal(canPlay, main.Library.MyGames.Single().CanPlay);
    }

    [Fact]
    public async Task A_game_that_is_only_on_the_lan_cannot_be_played()
    {
        var (main, _, _, _) = await StartAsync(Game(A, "BeamNG.drive", GameState.AvailableOnLan, peers: ["PC-01"]) with { Launch = LaunchState.Ready });

        Assert.False(main.Library.LanGames.Single().CanPlay);
    }

    [Fact]
    public async Task Play_asks_the_agent_and_starts_what_the_agent_says()
    {
        var (main, app, agent, _) = await StartAsync(Installed());
        agent.LaunchInfo = new LaunchInfoDto(@"D:\Games\BeamNG\Bin64\Game.exe", "-windowed", @"D:\Games\BeamNG");

        await main.Library.MyGames.Single().PlayCommand.ExecuteAsync(null);

        Assert.Contains($"Launch({A})", agent.Calls);
        Assert.Equal(agent.LaunchInfo, Assert.Single(StarterOf(app).Started));
        Assert.Contains("spouští", main.Library.MyGames.Single().Message);
    }

    [Fact]
    public async Task When_the_agent_refuses_to_start_a_game_nothing_is_started_and_the_reason_is_shown()
    {
        var (main, app, agent, _) = await StartAsync(Installed());
        agent.FailNext["Launch"] = new AgentException("The program Game.exe is not what it was when the game was verified, so it is not started. Repair the game first.", 409);

        await main.Library.MyGames.Single().PlayCommand.ExecuteAsync(null);

        Assert.Empty(StarterOf(app).Started);
        Assert.Contains("Repair the game first", main.Library.MyGames.Single().Message);
        Assert.True(main.Library.MyGames.Single().CanPlay); // and it can be tried again
    }

    [Fact]
    public async Task When_windows_will_not_start_the_program_the_player_is_told()
    {
        var (main, app, _, _) = await StartAsync(Installed());
        StarterOf(app).Failure = new AgentException("Hru se nepodařilo spustit: Operace vyžaduje zvýšení oprávnění.");

        await main.Library.MyGames.Single().PlayCommand.ExecuteAsync(null);

        Assert.Contains("nepodařilo spustit", main.Library.MyGames.Single().Message);
        Assert.False(main.Library.MyGames.Single().IsBusy);
    }

    [Fact]
    public async Task While_a_game_runs_it_cannot_be_repaired_updated_or_registered_and_it_can_be_again_when_it_is_closed()
    {
        var (main, _, _, events) = await StartAsync(Installed(GameState.Damaged, running: true));
        var card = main.Library.MyGames.Single();

        Assert.False(card.CanModify);
        Assert.False(card.RepairCommand.CanExecute(null));
        Assert.False(card.RegisterCommand.CanExecute(null));
        Assert.False(card.UpdateCommand.CanExecute(null));
        Assert.True(card.CheckCommand.CanExecute(null)); // looking is fine

        events.Raise(GameShareEvents.GameUpdated, Installed(GameState.Damaged, running: false));

        Assert.Same(card, main.Library.MyGames.Single());
        Assert.True(card.CanModify);
        Assert.True(card.RepairCommand.CanExecute(null));
        Assert.True(card.RegisterCommand.CanExecute(null));
        Assert.True(card.CanPlay);
    }

    [Fact]
    public async Task A_game_that_does_not_say_which_program_starts_it_lets_the_player_pick_one()
    {
        var (main, _, agent, _) = await StartAsync(Installed(launch: LaunchState.NeedsExecutable));
        agent.Executables = ["Launcher.exe", "Bin64/Game.exe"];
        var card = main.Library.MyGames.Single();
        Assert.True(card.NeedsExecutable);
        Assert.False(card.CanPlay);

        await card.ChooseExecutableCommand.ExecuteAsync(null);

        Assert.True(card.IsChoosingExecutable);
        Assert.Equal(["Launcher.exe", "Bin64/Game.exe"], card.Executables);
        Assert.Equal("Launcher.exe", card.SelectedExecutable);

        card.SelectedExecutable = "Bin64/Game.exe";
        agent.Games = [Installed()]; // the agent now says it can be started
        await card.SaveExecutableCommand.ExecuteAsync(null);

        Assert.Contains($"ChooseExecutable({A}|Bin64/Game.exe|)", agent.Calls);
        Assert.False(card.IsChoosingExecutable);
        Assert.True(card.CanPlay);
    }

    [Fact]
    public async Task Choosing_can_be_cancelled_and_a_game_without_any_program_says_so()
    {
        var (main, _, agent, _) = await StartAsync(Installed(launch: LaunchState.NeedsExecutable));
        var card = main.Library.MyGames.Single();
        agent.Executables = ["Game.exe"];

        await card.ChooseExecutableCommand.ExecuteAsync(null);
        card.CancelChoosingExecutableCommand.Execute(null);
        Assert.False(card.IsChoosingExecutable);
        Assert.True(card.NeedsExecutable); // the button to choose is back

        agent.Executables = [];
        await card.ChooseExecutableCommand.ExecuteAsync(null);
        Assert.False(card.IsChoosingExecutable);
        Assert.Contains("žádný program", card.Message);
    }

    [Fact]
    public async Task Each_verdict_of_the_administrators_list_has_its_own_badge_and_nothing_is_shown_when_checking_is_off()
    {
        var (main, _, _, _) = await StartAsync(
            Game(A, "Verified", GameState.AvailableOnLan, peers: ["PC-01"]) with { Trust = TrustVerdict.Verified },
            Game(B, "Unknown", GameState.AvailableOnLan, peers: ["PC-01"]) with { Trust = TrustVerdict.Unknown },
            Game(C, "Revoked", GameState.AvailableOnLan, peers: ["PC-01"]) with { Trust = TrustVerdict.Revoked, TrustNote = "modified executable" },
            Game(D, "Off", GameState.AvailableOnLan, peers: ["PC-01"]));

        GameCardViewModel Card(string name) => main.Library.LanGames.Single(g => g.Name == name);

        Assert.Equal(("Ověřeno správcem", true, false, false), (Card("Verified").TrustText, Card("Verified").IsTrustVerified, Card("Verified").IsTrustUnknown, Card("Verified").IsTrustRevoked));
        Assert.Equal("Není v seznamu správce", Card("Unknown").TrustText);
        Assert.True(Card("Unknown").IsTrustUnknown);
        Assert.Equal("Zrušeno správcem", Card("Revoked").TrustText);
        Assert.True(Card("Revoked").IsTrustRevoked);
        Assert.Equal("modified executable", Card("Revoked").TrustNote);
        Assert.False(Card("Off").HasTrust);
        Assert.Equal("", Card("Off").TrustText);
    }

    [Fact]
    public async Task A_new_list_changes_the_badge_of_a_game_that_is_already_shown()
    {
        var (main, _, _, events) = await StartAsync(Game(A, "BeamNG.drive", GameState.AvailableOnLan, peers: ["PC-01"]) with { Trust = TrustVerdict.Unknown });
        var card = main.Library.LanGames.Single();

        events.Raise(GameShareEvents.GameUpdated, Game(A, "BeamNG.drive", GameState.AvailableOnLan, peers: ["PC-01"]) with { Trust = TrustVerdict.Verified });

        Assert.Same(card, main.Library.LanGames.Single());
        Assert.True(card.IsTrustVerified);
        Assert.False(card.IsTrustUnknown);
    }

    [Theory]
    [InlineData(TrustMode.Off, false, null, "vypnuté")]
    [InlineData(TrustMode.Warn, true, null, "neověřené hry se jen označí")]
    [InlineData(TrustMode.Require, true, null, "instalují se jen ověřené hry")]
    [InlineData(TrustMode.Require, false, "The list file does not exist", "Seznam zatím není k dispozici")]
    public async Task The_settings_page_says_how_the_list_is_followed_and_why_it_did_not_load(TrustMode mode, bool hasList, string? error, string expected)
    {
        var (main, _, agent, _) = await StartAsync();
        agent.Trust = new TrustStatusDto(mode, mode == TrustMode.Off ? null : "list.json", hasList, hasList ? 7 : null, hasList ? DateTimeOffset.UtcNow : null, null, 12, 1, null, error, null);

        await main.Settings.LoadAsync();

        Assert.Contains(expected, main.Settings.TrustText);
        Assert.Equal(mode != TrustMode.Off, main.Settings.TrustEnabled);
        if (hasList) Assert.Contains("ověřených her: 12, zrušených verzí: 1", main.Settings.TrustText);
        if (error is not null) Assert.Contains(error, main.Settings.TrustText);
    }

    [Fact]
    public async Task Loading_the_list_again_asks_the_agent_and_shows_the_result()
    {
        var (main, _, agent, _) = await StartAsync();
        agent.Trust = new TrustStatusDto(TrustMode.Warn, "list.json", false, null, null, null, 0, 0, null, "offline", null);
        await main.Settings.LoadAsync();
        Assert.Contains("offline", main.Settings.TrustText);

        agent.Trust = new TrustStatusDto(TrustMode.Warn, "list.json", true, 8, DateTimeOffset.UtcNow, null, 3, 0, DateTimeOffset.UtcNow, null, null);
        await main.Settings.RefreshTrustCommand.ExecuteAsync(null);

        Assert.Contains("RefreshTrust", agent.Calls);
        Assert.Contains("Seznam č. 8", main.Settings.TrustText);
        Assert.DoesNotContain("offline", main.Settings.TrustText);
    }

    [Fact]
    public async Task Files_the_agent_noticed_a_game_rewriting_are_offered_as_patterns_without_running_a_check()
    {
        var (main, _, agent, events) = await StartAsync(Game(A, "BeamNG.drive", GameState.Installed, installPath: @"D:\Games\BeamNG"));
        var card = main.Library.MyGames.Single();
        Assert.False(card.HasSuggestion);

        events.Raise(GameShareEvents.GameUpdated,
            Game(A, "BeamNG.drive", GameState.Damaged, installPath: @"D:\Games\BeamNG") with { ChangedFileCount = 3, SuggestedPatterns = ["saves/**", "settings.ini"] });

        Assert.True(card.HasSuggestion);
        Assert.Contains("3 soubory", card.Suggestion);
        Assert.Contains("saves/**, settings.ini", card.Suggestion);

        await card.MarkVolatileCommand.ExecuteAsync(null);

        Assert.Contains($"AddVolatile({A}|saves/**;settings.ini)", agent.Calls);
        Assert.False(card.HasSuggestion);
    }

    [Fact]
    public async Task What_the_agent_noticed_goes_away_when_the_game_is_intact_again_but_a_checks_result_survives_a_refresh()
    {
        var (main, _, agent, events) = await StartAsync(Game(A, "BeamNG.drive", GameState.Damaged, installPath: @"D:\Games\BeamNG") with { ChangedFileCount = 1, SuggestedPatterns = ["a.ini"] });
        var card = main.Library.MyGames.Single();
        Assert.True(card.HasSuggestion);

        events.Raise(GameShareEvents.GameUpdated, Game(A, "BeamNG.drive", GameState.Installed, installPath: @"D:\Games\BeamNG"));
        Assert.False(card.HasSuggestion);

        // A check the user ran explains more than the watcher did, and a refresh of the card must not wipe that.
        agent.CheckResult = new GameChangesDto(false, ["saves/slot1.sav"], [], [], ["saves/**"]);
        await card.CheckCommand.ExecuteAsync(null);
        events.Raise(GameShareEvents.GameUpdated,
            Game(A, "BeamNG.drive", GameState.Damaged, installPath: @"D:\Games\BeamNG") with { ChangedFileCount = 1, SuggestedPatterns = ["other.ini"] });

        Assert.Contains("saves/**", card.Suggestion);
        Assert.DoesNotContain("other.ini", card.Suggestion);
    }

    [Fact]
    public async Task A_check_that_finds_nothing_says_everything_is_fine_and_offers_no_suggestion()
    {
        var (main, _, agent, _) = await StartAsync(Game(A, "BeamNG.drive", GameState.Installed, installPath: @"D:\Games\BeamNG"));
        var card = main.Library.MyGames.Single();

        await card.CheckCommand.ExecuteAsync(null);

        Assert.Equal("Vše je v pořádku.", card.Message);
        Assert.False(card.HasSuggestion);
        Assert.Contains($"Check({A})", agent.Calls);
    }

    [Fact]
    public async Task Registering_the_current_files_as_a_new_version_calls_the_agent()
    {
        var (main, _, agent, _) = await StartAsync(Game(A, "BeamNG.drive", GameState.Damaged, installPath: @"D:\Games\BeamNG"));

        await main.Library.MyGames.Single().RegisterCommand.ExecuteAsync(null);

        Assert.Contains($"Register({A})", agent.Calls);
        Assert.Contains("nová verze", main.Library.MyGames.Single().Message);
    }

    [Fact]
    public async Task Scanning_reports_what_was_found()
    {
        var (main, _, agent, _) = await StartAsync();

        await main.Library.ScanCommand.ExecuteAsync(null);

        Assert.Contains("Scan", agent.Calls);
        Assert.Contains("Nalezeno nových her: 2", main.Library.ScanText);
    }
}

public class DownloadsTests
{
    private static async Task<(MainViewModel Main, AppModel App, FakeAgent Agent, FakeEvents Events)> StartAsync(params DownloadDto[] downloads)
    {
        var agent = new FakeAgent { Downloads = [.. downloads] };
        var events = new FakeEvents();
        var app = new AppModel(agent, events, new ImmediateDispatcher());
        var main = new MainViewModel(app);
        await app.StartAsync();
        return (main, app, agent, events);
    }

    [Fact]
    public async Task A_running_download_shows_progress_speed_and_only_the_sources_that_are_sending()
    {
        var (main, _, _, _) = await StartAsync(
            Download(1, A, "BeamNG.drive", "Downloading", 91, done: 64_000_000_000, total: 70_264_000_000, speed: 287_000_000, eta: 75,
                sources: [("PC08", 0), ("PC04", 262_144_000), ("PC01", 314_572_800)]));

        var d = main.Downloads.Downloads.Single();
        Assert.Equal("91 %", d.PercentText);
        Assert.Equal("59,6 GB z 65,4 GB", d.SizeText);
        Assert.Equal("273,7 MB/s", d.SpeedText);
        Assert.Equal("1 min 15 s", d.EtaText);
        Assert.Equal("Instalace", d.KindText);
        Assert.Equal(["PC01", "PC04"], d.Sources.Select(s => s.Name)); // fastest first, idle PC08 left out
        Assert.Equal("300 MB/s", d.Sources[0].Speed);
        Assert.True(d.IsRunning);
    }

    [Theory]
    [InlineData("Downloading", "Stahuje se")]
    [InlineData("Queued", "Ve frontě")]
    [InlineData("Verifying", "Ověřuji soubory")]
    [InlineData("Paused", "Pozastaveno")]
    [InlineData("Completed", "Hotovo")]
    [InlineData("Failed", "Selhalo")]
    public async Task States_are_shown_in_czech(string state, string expected)
    {
        var (main, _, _, _) = await StartAsync(Download(1, A, "BeamNG.drive", state, 10));
        Assert.Equal(expected, main.Downloads.Downloads.Single().StateText);
    }

    [Theory]
    [InlineData("Install", "Instalace")]
    [InlineData("Update", "Aktualizace")]
    [InlineData("Repair", "Oprava")]
    public async Task The_kind_of_download_is_named(string kind, string expected)
    {
        var (main, _, _, _) = await StartAsync(Download(1, A, "BeamNG.drive", "Downloading", 10, kind: kind));
        Assert.Equal(expected, main.Downloads.Downloads.Single().KindText);
    }

    [Fact]
    public async Task Progress_events_update_the_row_in_place_and_a_finished_download_sinks_below_active_ones()
    {
        var (main, app, _, events) = await StartAsync(
            Download(1, A, "BeamNG.drive", "Downloading", 10),
            Download(2, B, "GTA V", "Downloading", 20));
        Assert.Equal([2L, 1L], main.Downloads.Downloads.Select(d => d.Id)); // newest first
        var first = main.Downloads.Downloads.Single(d => d.Id == 1);

        events.Raise(GameShareEvents.DownloadProgress, Download(1, A, "BeamNG.drive", "Downloading", 55, speed: 100_000_000));
        Assert.Same(first, main.Downloads.Downloads.Single(d => d.Id == 1)); // same row, no rebuild
        Assert.Equal(55, first.Percent);

        events.Raise(GameShareEvents.DownloadCompleted, Download(2, B, "GTA V", "Completed", 100));
        Assert.Equal([1L, 2L], main.Downloads.Downloads.Select(d => d.Id));
        Assert.Equal(1, app.ActiveDownloadCount);
    }

    [Fact]
    public async Task A_new_download_appears_on_top_and_a_cancelled_one_disappears()
    {
        var (main, app, _, events) = await StartAsync(Download(1, A, "BeamNG.drive", "Downloading", 10));

        events.Raise(GameShareEvents.DownloadStarted, Download(2, B, "GTA V", "Downloading", 0));
        Assert.Equal([2L, 1L], main.Downloads.Downloads.Select(d => d.Id));
        Assert.Equal(2, app.ActiveDownloadCount);

        events.Raise(GameShareEvents.DownloadCancelled, Download(1, A, "BeamNG.drive", "Failed", 10));
        Assert.Equal([2L], main.Downloads.Downloads.Select(d => d.Id));
        Assert.Equal(1, app.ActiveDownloadCount);
    }

    [Fact]
    public async Task Pause_resume_and_cancel_call_the_agent_and_cancel_keeps_the_files()
    {
        var (main, _, agent, _) = await StartAsync(Download(7, A, "BeamNG.drive", "Downloading", 10));
        var d = main.Downloads.Downloads.Single();

        await d.PauseCommand.ExecuteAsync(null);
        await d.ResumeCommand.ExecuteAsync(null);
        await d.CancelCommand.ExecuteAsync(null);

        Assert.Contains("Pause(7)", agent.Calls);
        Assert.Contains("Resume(7)", agent.Calls);
        Assert.Contains("Cancel(7|False)", agent.Calls);
    }

    [Fact]
    public async Task A_failed_download_shows_why_and_a_refused_action_shows_the_reason()
    {
        var (main, _, agent, _) = await StartAsync(Download(1, A, "BeamNG.drive", "Failed", 80, error: "Verification failed: 1 wrong hash. Retry will repair it."));
        var d = main.Downloads.Downloads.Single();
        Assert.True(d.IsFailed);
        Assert.Contains("Verification failed", d.Error);

        agent.FailNext["Resume"] = new AgentException("Download 1 is Completed and cannot be resumed.", 409);
        await d.ResumeCommand.ExecuteAsync(null);
        Assert.Equal("Download 1 is Completed and cannot be resumed.", d.Message);
    }

    [Fact]
    public async Task The_downloads_tab_badge_counts_active_downloads_and_follows_them()
    {
        var (main, _, _, events) = await StartAsync(Download(1, A, "BeamNG.drive", "Downloading", 10));
        var tab = main.Items.Single(i => i.Title == "Stahování");
        Assert.Equal(1, tab.Badge);
        Assert.True(tab.HasBadge);

        events.Raise(GameShareEvents.DownloadCompleted, Download(1, A, "BeamNG.drive", "Completed", 100));

        Assert.Equal(0, tab.Badge);
        Assert.False(tab.HasBadge);
    }
}

public class NetworkAndSettingsTests
{
    private static async Task<(MainViewModel Main, FakeAgent Agent, FakeEvents Events)> StartAsync(FakeAgent? agent = null)
    {
        agent ??= new FakeAgent();
        var events = new FakeEvents();
        var app = new AppModel(agent, events, new ImmediateDispatcher());
        var main = new MainViewModel(app);
        await app.StartAsync();
        return (main, agent, events);
    }

    [Fact]
    public async Task Peers_come_and_go_with_events_and_stay_sorted()
    {
        var (main, _, events) = await StartAsync(new FakeAgent { Peers = [Peer("p4", "PC-04"), Peer("p9", "PC-09")] });

        events.Raise(GameShareEvents.PeerConnected, Peer("p1", "PC-01", games: 3));
        Assert.Equal(["PC-01", "PC-04", "PC-09"], main.Network.Peers.Select(p => p.Name));
        Assert.Equal("nabízí 3 hry", main.Network.Peers[0].GamesText);

        events.Raise(GameShareEvents.PeerConnected, Peer("p1", "PC-01", games: 5)); // known peer, upserted not duplicated
        Assert.Equal(3, main.Network.Peers.Count);
        Assert.Equal("nabízí 5 her", main.Network.Peers[0].GamesText);

        events.Raise(GameShareEvents.PeerDisconnected, Peer("p4", "PC-04"));
        Assert.Equal(["PC-01", "PC-09"], main.Network.Peers.Select(p => p.Name));
        Assert.Equal(2, main.Items.Single(i => i.Title == "Síť").Badge);
    }

    [Theory]
    [InlineData(0, "nenabízí žádnou hru")]
    [InlineData(1, "nabízí 1 hru")]
    [InlineData(2, "nabízí 2 hry")]
    [InlineData(4, "nabízí 4 hry")]
    [InlineData(5, "nabízí 5 her")]
    [InlineData(12, "nabízí 12 her")]
    public async Task Game_counts_are_worded_in_correct_czech(int games, string expected)
    {
        var (main, _, _) = await StartAsync(new FakeAgent { Peers = [Peer("p1", "PC-01", games)] });
        Assert.Equal(expected, main.Network.Peers.Single().GamesText);
    }

    [Fact]
    public async Task Opening_the_settings_page_reads_the_current_settings_from_the_agent()
    {
        var agent = new FakeAgent { Settings = new SettingsDto([@"D:\Games", @"E:\Games"], false, 80, 120) };
        var (main, _, _) = await StartAsync(agent);

        main.SelectedItem = main.Items.Single(i => i.Title == "Nastavení");
        await Task.Yield();
        await main.Settings.LoadAsync();

        Assert.Equal([@"D:\Games", @"E:\Games"], main.Settings.Roots.Select(r => r.Path));
        Assert.False(main.Settings.SeedingEnabled);
        Assert.Equal("80", main.Settings.MaxUploadText);
        Assert.Equal("120", main.Settings.MaxDownloadText);
    }

    [Fact]
    public async Task Folders_can_be_added_once_removed_and_saved_with_parsed_limits()
    {
        var agent = new FakeAgent { Settings = new SettingsDto([@"D:\Games"], true, null, null) };
        var (main, _, _) = await StartAsync(agent);
        var s = main.Settings;
        await s.LoadAsync();

        s.NewRoot = @"  E:\Games  ";
        s.AddRootCommand.Execute(null);
        s.NewRoot = @"e:\games"; // same folder, different case
        s.AddRootCommand.Execute(null);
        s.NewRoot = "   ";       // nothing
        s.AddRootCommand.Execute(null);
        Assert.Equal([@"D:\Games", @"E:\Games"], s.Roots.Select(r => r.Path));
        Assert.Equal("", s.NewRoot);

        s.Roots.First(r => r.Path == @"D:\Games").RemoveCommand.Execute(null);
        s.SeedingEnabled = false;
        s.MaxUploadText = " 80 ";
        s.MaxDownloadText = "";
        await s.SaveCommand.ExecuteAsync(null);

        Assert.Contains(@"SaveSettings(E:\Games|False|80|)", agent.Calls);
        Assert.Equal("Uloženo.", s.Message);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public async Task An_invalid_speed_limit_is_refused_before_anything_is_sent(string limit)
    {
        var agent = new FakeAgent();
        var (main, _, _) = await StartAsync(agent);
        main.Settings.MaxUploadText = limit;

        await main.Settings.SaveCommand.ExecuteAsync(null);

        Assert.DoesNotContain(agent.Calls, c => c.StartsWith("SaveSettings"));
        Assert.Contains("celé číslo", main.Settings.Message);
    }

    [Fact]
    public async Task When_the_agent_rejects_the_settings_its_reason_is_shown()
    {
        var agent = new FakeAgent();
        var (main, _, _) = await StartAsync(agent);
        main.Settings.NewRoot = "relative\\path";
        main.Settings.AddRootCommand.Execute(null);
        agent.FailNext["SaveSettings"] = new AgentException("Game folder 'relative\\path' must be a full path such as D:\\Games.", 400);

        await main.Settings.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Game folder 'relative\\path' must be a full path such as D:\\Games.", main.Settings.Message);
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1,5 KB")]
    [InlineData(5_000_000, "4,8 MB")]
    [InlineData(70_264_000_000, "65,4 GB")]
    [InlineData(2_199_023_255_552, "2 TB")]
    [InlineData(-5, "0 B")]
    public void Sizes_use_binary_units_and_a_decimal_comma(long bytes, string expected) => Assert.Equal(expected, Format.Size(bytes));

    [Fact]
    public void Speed_is_empty_when_nothing_moves() => Assert.Equal("", Format.Speed(0));

    [Fact]
    public void Speed_is_size_per_second() => Assert.Equal("287 MB/s", Format.Speed(300_941_312));

    [Theory]
    [InlineData(null, "")]
    [InlineData(-1.0, "")]
    [InlineData(5.0, "5 s")]
    [InlineData(59.2, "1 min 0 s")]
    [InlineData(130.0, "2 min 10 s")]
    [InlineData(3725.0, "1 h 2 min")]
    public void Eta_reads_naturally(double? seconds, string expected) => Assert.Equal(expected, Format.Eta(seconds));

    [Theory]
    [InlineData(0, "0 %")]
    [InlineData(54.24, "54,2 %")]
    [InlineData(100, "100 %")]
    [InlineData(150, "100 %")]
    [InlineData(-3, "0 %")]
    public void Percent_is_clamped_and_uses_a_comma(double value, string expected) => Assert.Equal(expected, Format.Percent(value));
}
