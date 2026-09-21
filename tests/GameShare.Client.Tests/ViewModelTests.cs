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
        var app = new AppModel(agent, events, new ImmediateDispatcher());
        var main = new MainViewModel(app);
        await app.StartAsync();
        return (main, app, agent, events);
    }

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
        Assert.Contains("nenabízí", card.Message);
        Assert.True(card.HasSuggestion);
        Assert.Contains("saves/**", card.Suggestion);

        await card.MarkVolatileCommand.ExecuteAsync(null);

        Assert.Contains($"AddVolatile({A}|saves/**)", agent.Calls);
        Assert.False(card.HasSuggestion);
        Assert.Contains("Označeno", card.Message);
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
