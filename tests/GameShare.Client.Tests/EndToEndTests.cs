using System.Collections.Concurrent;
using GameShare.Agent.Tests;
using GameShare.Client.Services;
using GameShare.Client.ViewModels;
using GameShare.Protocol;

namespace GameShare.Client.Tests;

/// <summary>Collects what the event stream wants done on the UI thread. The test thread plays the UI thread by draining it.</summary>
internal sealed class PumpDispatcher : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();
    public void Post(Action action) => _queue.Enqueue(action);
    public void Drain() { while (_queue.TryDequeue(out var a)) a(); }

    public async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 30_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        Drain();
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for: {what}");
            await Task.Delay(30);
            Drain();
        }
    }
}

/// <summary>
/// The whole client stack, the real HTTP client, the real SignalR stream and the real view models,
/// driven against real agents that talk to each other. Only the window itself is missing.
/// </summary>
public class EndToEndTests
{
    private static string Big(TestAgent a) => Path.Combine(a.InstalledPath, "content", "big.pak");

    private static async Task<(AppModel App, MainViewModel Main, PumpDispatcher Ui)> ConnectAsync(TestAgent agent)
    {
        var uri = new Uri($"http://127.0.0.1:{agent.LocalPort}");
        var ui = new PumpDispatcher();
        var app = new AppModel(AgentClient.Create(uri), new AgentEventStream(uri), ui);
        var main = new MainViewModel(app);
        await app.StartAsync();
        return (app, main, ui);
    }

    [Fact]
    public async Task A_user_finds_a_game_on_the_lan_installs_it_watches_it_download_and_then_checks_and_repairs_it()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var pc02 = await TestAgent.StartAsync("PC-02", discovery);
        var (app, main, ui) = await ConnectAsync(pc02);
        await using var _ = app;
        Assert.True(app.IsConnected);
        Assert.Equal("PC-02", app.MachineName);
        Assert.True(main.Library.IsEmpty);

        // Another PC with a game appears while the window is open.
        await using var pc01 = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true);
        await ui.UntilAsync(() => main.Library.LanGames.Count == 1, "the game to appear under 'Na LAN'");
        await ui.UntilAsync(() => main.Network.Peers.Count == 1, "the other PC to appear");
        var card = main.Library.LanGames.Single();
        Assert.Equal("TestGame", card.Name);
        Assert.True(card.CanInstall);
        Assert.Equal("Nabízí 1 PC: PC-01", card.PeersText);
        Assert.Equal("PC-01", main.Network.Peers.Single().Name);

        // One click on Install.
        await card.InstallCommand.ExecuteAsync(null);
        await ui.UntilAsync(() => main.Downloads.Downloads.Count == 1, "the download to be listed");
        await ui.UntilAsync(() => main.Library.MyGames.Count == 1 && main.Library.MyGames[0].IsInstalled, "the game to be installed");

        var download = main.Downloads.Downloads.Single();
        Assert.Equal("Hotovo", download.StateText);
        Assert.Equal("100 %", download.PercentText);
        Assert.Equal(pc02.InstalledPath, main.Library.MyGames.Single().InstallPath);
        Assert.Equal(TestGame.HashTree(pc01.InstalledPath), TestGame.HashTree(pc02.InstalledPath));
        Assert.Empty(main.Library.LanGames); // installed, so no longer an offer
        Assert.Equal(0, main.Items.Single(i => i.Title == "Stahování").Badge);

        // The game changes on disk. A check explains it and the game leaves the LAN.
        var mine = main.Library.MyGames.Single();
        var original = await File.ReadAllBytesAsync(Big(pc02));
        TestGame.CorruptOneByte(Big(pc02));
        await mine.CheckCommand.ExecuteAsync(null);
        await ui.UntilAsync(() => mine.IsDamaged, "the game to show as changed");
        Assert.Contains("Změněno souborů: 1", mine.Message);
        Assert.True(mine.HasSuggestion);
        Assert.Contains("content/**", mine.Suggestion);

        // Repair, then everything is as before.
        await mine.RepairCommand.ExecuteAsync(null);
        await ui.UntilAsync(() => main.Downloads.Downloads.Any(d => d.KindText == "Oprava" && d.IsFinished), "the repair to finish");
        await ui.UntilAsync(() => mine.IsInstalled, "the game to be installed again");
        Assert.Equal(original, await File.ReadAllBytesAsync(Big(pc02)));

        await mine.CheckCommand.ExecuteAsync(null);
        Assert.Equal("Vše je v pořádku.", mine.Message);
    }

    [Fact]
    public async Task Settings_edited_in_the_window_reach_the_agent_and_a_bad_value_is_explained_by_the_agent()
    {
        await using var pc = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort());
        var (app, main, ui) = await ConnectAsync(pc);
        await using var _ = app;

        await main.Settings.LoadAsync();
        Assert.Equal([pc.GamesRoot], main.Settings.Roots.Select(r => r.Path));

        main.Settings.NewRoot = @"E:\More\Games\";
        main.Settings.AddRootCommand.Execute(null);
        main.Settings.SeedingEnabled = false;
        main.Settings.MaxUploadText = "80";
        await main.Settings.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Uloženo.", main.Settings.Message);
        var saved = await pc.GetAsync<SettingsDto>("/api/settings");
        Assert.Equal([pc.GamesRoot, @"E:\More\Games"], saved.GameRoots); // the agent tidied the trailing slash, and the window shows that
        Assert.Equal([pc.GamesRoot, @"E:\More\Games"], main.Settings.Roots.Select(r => r.Path));
        Assert.False(saved.SeedingEnabled);
        Assert.Equal(80, saved.MaxUploadMBps);

        main.Settings.NewRoot = "relative\\path";
        main.Settings.AddRootCommand.Execute(null);
        await main.Settings.SaveCommand.ExecuteAsync(null);
        Assert.Contains("full path", main.Settings.Message); // the agent's own words
    }

    [Fact]
    public async Task Stopping_the_agent_shows_the_banner_and_a_scan_reports_what_it_found()
    {
        await using var pc = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true);
        var (app, main, ui) = await ConnectAsync(pc);
        await using var _ = app;
        await ui.UntilAsync(() => main.Library.MyGames.Count == 1, "the game to be listed");

        await main.Library.ScanCommand.ExecuteAsync(null);
        Assert.Contains("Nalezeno nových her: 0", main.Library.ScanText);
        Assert.Contains("Beze změny: 1", main.Library.ScanText);

        await pc.StopAsync();
        await ui.UntilAsync(() => !app.IsConnected, "the client to notice the agent is gone");

        Assert.False(app.IsConnected);
        Assert.NotEmpty(app.ConnectionText);
        Assert.Single(main.Library.MyGames); // what it knew stays on screen, greyed by the banner rather than blanked
    }
}
