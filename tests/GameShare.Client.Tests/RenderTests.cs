using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GameShare.Client.ViewModels;
using GameShare.Client.Views;
using GameShare.Protocol;
using static GameShare.Client.Tests.Data;

[assembly: AvaloniaTestApplication(typeof(GameShare.Client.Tests.TestAppBuilder))]

namespace GameShare.Client.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}

/// <summary>
/// Renders the real window with real controls and real styles, without a display, to a PNG.
/// Bindings are compiled, so a wrong property name already fails the build. These tests catch what that cannot:
/// a view that shows nothing, a page that crashes when opened, controls that are not there.
/// </summary>
public class RenderTests
{
    private static readonly string OutputDir = Path.Combine(Path.GetTempPath(), "gameshare-ui");

    private static async Task<MainViewModel> BuildAsync(FakeAgent agent)
    {
        var app = new AppModel(agent, new FakeEvents(), new ImmediateDispatcher());
        var main = new MainViewModel(app);
        await app.StartAsync();
        return main;
    }

    private static FakeAgent Populated() => new()
    {
        MachineName = "PC-07",
        Games =
        [
            Game(A, "BeamNG.drive", GameState.Installed, version: "0.38", size: 70_264_000_000, installPath: @"D:\Games\BeamNG") with { Trust = TrustVerdict.Verified, Launch = LaunchState.Ready },
            Game(B, "Grand Theft Auto V", GameState.Downloading, version: null, size: 118_111_600_640),
            Game(C, "Euro Truck Simulator 2", GameState.Damaged, version: "1.53", size: 28_000_000_000, installPath: @"D:\Games\ETS2") with { Launch = LaunchState.Ready, IsRunning = true },
            Game(D, "Assetto Corsa", GameState.AvailableOnLan, version: "1.16", size: 45_000_000_000, peers: ["PC-01", "PC-04", "PC-08", "PC-10"]) with { Trust = TrustVerdict.Unknown },
            Game("e" + new string('e', 63), "Farming Simulator 25", GameState.AvailableOnLan, version: "1.4", size: 60_000_000_000, peers: ["PC-04"]) with { Trust = TrustVerdict.Revoked, TrustNote = "Obsahuje upravený spustitelný soubor." },
            Game("f" + new string('f', 63), "BeamNG.drive", GameState.AvailableOnLan, version: "0.39", size: 71_000_000_000, peers: ["PC-01", "PC-04"], updates: A, gameId: "beamng"),
            Game("9" + new string('9', 63), "Forza Horizon", GameState.AvailableOnLan, version: "5.0", size: 110_000_000_000, peers: ["PC-01", "PC-04"]) with
            {
                PartialPeerNames = ["PC-01", "PC-04"], FullyAvailable = false, CoveragePercent = 96.5,
            },
        ],
        Peers = [Peer("p1", "PC-01", 3), Peer("p4", "PC-04", 4), Peer("p8", "PC-08", 1), Peer("p10", "PC-10", 0)],
        Downloads =
        [
            Download(3, B, "Grand Theft Auto V", "Downloading", 54.2, done: 64_000_000_000, total: 118_111_600_640, speed: 301_000_000, eta: 130,
                sources: [("PC-01", 157_000_000), ("PC-04", 96_000_000), ("PC-08", 48_000_000)]),
            Download(2, C, "Euro Truck Simulator 2", "Paused", 12, done: 3_000_000_000, total: 28_000_000_000, kind: "Repair"),
            Download(1, A, "BeamNG.drive", "Completed", 100, done: 70_264_000_000, total: 70_264_000_000),
        ],
    };

    private static async Task<WriteableBitmap> ShowAsync(MainViewModel main, string page, string file)
    {
        main.SelectedItem = main.Items.Single(i => i.Title == page);
        var window = new MainWindow { DataContext = main };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window produced no frame.");
        Directory.CreateDirectory(OutputDir);
        frame.Save(Path.Combine(OutputDir, file));
        window.Close();
        return frame;
    }

    /// <summary>How many distinct colours the image has. A blank or broken render has almost none.</summary>
    private static int DistinctColours(WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        var seen = new HashSet<int>();
        for (int i = 0; i + 3 < bytes.Length && seen.Count < 5000; i += 4)
            seen.Add(bytes[i] | bytes[i + 1] << 8 | bytes[i + 2] << 16);
        return seen.Count;
    }

    [AvaloniaFact]
    public async Task Remote_management_page_shows_the_code_the_paired_pcs_and_what_they_did()
    {
        var agent = Populated();
        var now = DateTimeOffset.UtcNow;
        agent.Remote = new RemoteStatusDto(true, true, true, 47703, "3f9a0c1b2d4e5f60718293a4b5c6d7e8f90112233445566778899aabbccddeeff",
            new RemotePairingDto("K7QF-M2XP-9HTD", now.AddMinutes(5)),
            [new PairedMachineDto("c1", "PC-01", new string('c', 64), now.AddDays(-2), now.AddMinutes(-3))],
            [new PairedMachineDto("p4", "PC-04", new string('d', 64), now.AddDays(-1), null, Online: true),
             new PairedMachineDto("p9", "PC-09", new string('e', 64), now.AddDays(-5), null, Online: false)]);
        agent.TargetAgents["p4"] = new FakeAgent
        {
            Version = "0.6.8",
            Downloads = [Download(7, D, "Assetto Corsa", "Downloading", 38, done: 17_000_000_000, total: 45_000_000_000, speed: 96_000_000)],
            GameRoots = [new GameRootDto(@"D:\Games", 12_000_000_000)],
        };
        var events = new FakeEvents();
        var app = new AppModel(agent, events, new ImmediateDispatcher());
        var main = new MainViewModel(app);
        await app.StartAsync();
        await main.Remote.LoadAsync();
        await main.Remote.RefreshOverviewAsync();
        events.Raise(GameShareEvents.RemoteAction, new RemoteActionDto("c1", "PC-01", $"POST games/{D}/install", 202, now));

        var frame = await ShowAsync(main, "Vzdálená správa", "remote.png");
        main.Remote.StopOverview();

        Assert.True(DistinctColours(frame) > 100, "the remote page rendered as an almost blank image");
        Assert.Equal("K7QF-M2XP-9HTD", main.Remote.PairingCode);
        Assert.Equal(2, main.Remote.Targets.Count);
        Assert.Single(main.Remote.Controllers);
        Assert.Contains("PC-01: instalace hry Assetto Corsa", Assert.Single(main.Remote.Recent));
        Assert.Equal(["PC-01", "PC-08", "PC-10"], main.Remote.Choices.Select(c => c.Name)); // PC-04 is paired already
    }

    [AvaloniaFact]
    public async Task The_window_of_a_managed_pc_says_so_and_offers_nothing_that_only_its_owner_may_do()
    {
        var agent = Populated();
        var app = new AppModel(agent, new FakeEvents(), new ImmediateDispatcher()) { IsRemote = true };
        var main = new MainViewModel(app);
        await app.StartAsync();

        var frame = await ShowAsync(main, "Knihovna", "remote-window.png");

        Assert.True(DistinctColours(frame) > 200);
        Assert.Equal(["Knihovna", "Přenosy", "Síť"], main.Items.Select(i => i.Title));
        var installed = main.Library.MyGames.Single(g => g.ContentHash == A);
        Assert.False(installed.CanPlay);
        Assert.False(installed.ShowUninstallButton);
        Assert.DoesNotContain(installed.MenuEntries, e => e.Header is { } h && (h.StartsWith("Odinstalovat") || h.StartsWith("Znovu připravit")));
        Assert.Contains(installed.MenuEntries, e => e.Header == "Zkontrolovat soubory");
        Assert.True(main.Library.LanGames.Single(g => g.ContentHash == D).CanInstall);
    }

    [AvaloniaFact]
    public async Task Installing_on_other_pcs_lists_each_one_under_the_card()
    {
        var agent = Populated();
        var at = DateTimeOffset.UtcNow;
        agent.Remote = agent.Remote with
        {
            Targets = [new("p2", "PC-02", "f", at, null, true), new("p3", "PC-03", "f", at, null, true), new("p9", "PC-09", "f", at, null, false)],
        };
        agent.TargetAgents["p2"] = new FakeAgent { Games = [Game(D, "Assetto Corsa", GameState.AvailableOnLan)] };
        agent.TargetAgents["p3"] = new FakeAgent { Games = [Game(D, "Assetto Corsa", GameState.Installed)] };
        var main = await BuildAsync(agent);
        var card = main.Library.LanGames.Single(g => g.ContentHash == D);
        await card.OpenSpreadCommand.ExecuteAsync(null);

        var frame = await ShowAsync(main, "Knihovna", "library-spread.png");

        Assert.True(DistinctColours(frame) > 200);
        Assert.True(card.CanSpread);
        Assert.Equal(3, card.Spread!.Targets.Count);
    }

    [AvaloniaFact]
    public async Task Library_shows_installed_downloading_damaged_and_available_games()
    {
        var main = await BuildAsync(Populated());
        var frame = await ShowAsync(main, "Knihovna", "library.png");

        Assert.True(frame.PixelSize.Width >= 1000);
        Assert.True(DistinctColours(frame) > 200, "the library rendered as an almost blank image");
        Assert.Equal(3, main.Library.MyGames.Count);
        Assert.Equal(4, main.Library.LanGames.Count);
    }

    [AvaloniaFact]
    public async Task A_games_icon_is_decoded_and_shown_with_the_menu_of_its_other_programs()
    {
        var agent = Populated();
        agent.Games[0] = agent.Games[0] with
        {
            HasIcon = true,
            LaunchOptions = [new LaunchOptionDto(0, null, "BeamNG.drive.exe", true), new LaunchOptionDto(1, "Editor", "Bin64/World Editor.exe", false)],
        };
        var ico = GameShare.Storage.ExeIcon.Extract(Path.Combine(Environment.SystemDirectory, "notepad.exe"))!;
        agent.Icons[A] = ico;

        var decoded = Views.IconConverter.Instance.Convert(ico, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsType<Bitmap>(decoded); // an .ico as the agent serves it is an image Avalonia can show

        var main = await BuildAsync(agent);
        await ShowAsync(main, "Knihovna", "library-icons.png");

        var card = main.Library.MyGames.Single(g => g.ContentHash == A);
        Assert.True(card.HasIconImage);
        Assert.True(card.HasOtherLaunchOptions);
        Assert.True(card.PlayNeedsAdmin);
    }

    [AvaloniaFact]
    public async Task The_preparation_of_a_game_lists_its_steps_before_anything_runs()
    {
        var agent = Populated();
        agent.Games[0] = agent.Games[0] with { NeedsSetup = true };
        agent.SetupPlan = new SetupPlanDto(A, "BeamNG.drive", "hash",
        [
            new SetupStepDto(SetupStepKind.Redist, "Nainstalovat DirectX 9.0c (June 2010)", true) { AlreadyDone = true, Details = ["Na tomto PC už je."] },
            new SetupStepDto(SetupStepKind.Redist, "Nainstalovat Visual C++ 2005 (x86)", true) { Details = ["vcredist_x86.exe /q"] },
            new SetupStepDto(SetupStepKind.RegistryDelete, @"Smazat klíč registru HKLM\SOFTWARE\WOW6432Node\EA Games\Battlefield 2", true),
            new SetupStepDto(SetupStepKind.RegistryImport, "Zapsat registry-import.reg do registru počítače (4 hodnoty)", true)
            {
                Details = [@"Cesty C:\Games\Battlefield 2 se přepíšou na D:\Games\Battlefield 2.", @"HKLM\SOFTWARE\WOW6432Node\EA Games\Battlefield 2", "    InstallDir = \"D:\\\\Games\\\\Battlefield 2\""],
            },
            new SetupStepDto(SetupStepKind.Compatibility, "Režim kompatibility WINXPSP3 pro BF2.exe", false),
            new SetupStepDto(SetupStepKind.Profile, @"Zkopírovat profil do {Documents}\Battlefield 2", false),
        ], DefinitionVerdict.NotSigned, Warning: "Správce definici této hry nepodepsal. Kroky níže přišly z PC, od kterého je hra.");

        var main = await BuildAsync(agent);
        var card = main.Library.MyGames.Single(g => g.ContentHash == A);
        await card.PlayCommand.ExecuteAsync(null);
        card.SetupSteps[3].ShowDetails = true; // what the registry import writes, opened
        var frame = await ShowAsync(main, "Knihovna", "library-setup.png");

        Assert.True(card.IsShowingSetup);
        Assert.Equal(6, card.SetupSteps.Count);
        Assert.True(DistinctColours(frame) > 200);
    }

    [AvaloniaFact]
    public async Task A_running_scan_shows_which_game_is_hashed_and_how_far()
    {
        var main = await BuildAsync(Populated());
        main.Library.IsScanning = true;
        main.Library.HasScanBar = true;
        main.Library.ScanPercent = 42;
        main.Library.ScanText = "Počítám otisk hry Battlefield 2 (3/16): 42 % · 1,4 GB z 3,3 GB";

        var frame = await ShowAsync(main, "Knihovna", "library-scanning.png");

        Assert.True(DistinctColours(frame) > 200);
    }

    [AvaloniaFact]
    public async Task Downloads_page_shows_progress_and_sources()
    {
        var main = await BuildAsync(Populated());
        var frame = await ShowAsync(main, "Přenosy", "downloads.png");
        Assert.True(DistinctColours(frame) > 200);
    }

    /// <summary>Two games taking turns, the way a busy source hands out its speed: the total stays up while each one rises and falls.</summary>
    [AvaloniaFact]
    public async Task Downloads_page_draws_the_speed_graphs()
    {
        var agent = Populated();
        var events = new FakeEvents();
        var app = new AppModel(agent, events, new ImmediateDispatcher());
        var main = new MainViewModel(app);
        await app.StartAsync();

        var start = new DateTime(2026, 9, 29, 14, 0, 0, DateTimeKind.Utc);
        for (int s = 0; s < 240; s++)
        {
            app.Now = () => start.AddSeconds(s);
            long first = (long)(20_000_000 * (0.5 + 0.5 * Math.Cos(s / 30.0)));
            events.Raise(GameShareEvents.DownloadProgress, Download(3, B, "Grand Theft Auto V", "Downloading", 54, speed: first, sources: [("PC-01", first)]));
            events.Raise(GameShareEvents.DownloadProgress, Download(4, D, "Assetto Corsa", "Downloading", 20, speed: 20_000_000 - first, sources: [("PC-01", 20_000_000 - first)]));
        }

        var frame = await ShowAsync(main, "Přenosy", "downloads-graph.png");
        Assert.True(DistinctColours(frame) > 200);
        Assert.Equal(240, app.DownloadSpeed.Values.Count);
        Assert.InRange(app.DownloadSpeed.Peak, 19_000_000, 20_000_000); // the two add up to the total all along
    }

    [AvaloniaFact]
    public async Task Network_page_lists_the_other_pcs()
    {
        var agent = Populated();
        agent.NetworkCheck = NetworkCheckClientTests.Typical(
            NetworkCheckClientTests.Net("Domov", "Private"), NetworkCheckClientTests.Net("Síť 3", "Public"),
            NetworkCheckClientTests.Net("VirtualBox Host-Only Network", "Public", isVirtual: true));
        var main = await BuildAsync(agent);
        var frame = await ShowAsync(main, "Síť", "network.png");
        Assert.True(DistinctColours(frame) > 100);
        Assert.True(main.Network.Check.HasResult);
        Assert.Equal(CheckLevel.Bad, main.Network.Check.Overall); // a real network is public
        Assert.Equal(CheckLevel.Warn, main.Network.Check.Ports[1].Level); // fine on Domov, not on Síť 3
    }

    [AvaloniaFact]
    public async Task Settings_page_opens_and_shows_the_agents_settings()
    {
        var agent = Populated();
        agent.Settings = new SettingsDto([@"D:\Games", @"E:\Games"], true, 80, null);
        var main = await BuildAsync(agent);
        await main.Settings.LoadAsync();
        var frame = await ShowAsync(main, "Nastavení", "settings.png");
        Assert.True(DistinctColours(frame) > 100);
    }

    [AvaloniaFact]
    public async Task Settings_page_unfolds_the_advanced_transfer_tuning()
    {
        var agent = Populated();
        agent.Settings = new SettingsDto([@"D:\Games"], true, null, null) { Tuning = new TransferTuningDto(OpenFiles: 2000, SendBufferKb: 8192) };
        var main = await BuildAsync(agent);
        await main.Settings.LoadAsync();
        main.Settings.IsTuningOpen = true;
        var frame = await ShowAsync(main, "Nastavení", "settings-tuning.png");
        Assert.True(DistinctColours(frame) > 100);
    }

    [AvaloniaFact]
    public async Task A_new_version_that_is_ready_shows_a_strip_with_its_button_and_its_notes_in_the_settings()
    {
        var agent = Populated();
        agent.AppUpdate = new AppUpdateStatusDto("0.4.3", "agent", AppUpdateState.Ready, "0.5.0",
            "### Added\n- GameShare updates itself: from the internet, or from the other PCs on the LAN.", DateTimeOffset.UtcNow,
            226_000_000, 226_000_000, "LAN", null, DateTimeOffset.UtcNow, true, null, null);
        var main = await BuildAsync(agent);
        await main.Settings.LoadAsync();

        var library = await ShowAsync(main, "Knihovna", "update-ready-library.png");
        var settings = await ShowAsync(main, "Nastavení", "update-ready-settings.png");

        Assert.True(main.App.HasUpdateBanner);
        Assert.All(new[] { library, settings }, f => Assert.True(DistinctColours(f) > 100));
    }

    [AvaloniaFact]
    public async Task A_missing_agent_shows_a_banner_and_empty_pages_still_render()
    {
        var agent = new FakeAgent { Failure = new GameShare.Client.Services.AgentException("Agent GameShare neodpovídá. Zkontroluj, že služba běží.") };
        var main = await BuildAsync(agent);

        var library = await ShowAsync(main, "Knihovna", "no-agent-library.png");
        var downloads = await ShowAsync(main, "Přenosy", "no-agent-downloads.png");
        var network = await ShowAsync(main, "Síť", "no-agent-network.png");

        Assert.All(new[] { library, downloads, network }, f => Assert.True(DistinctColours(f) > 30));
        Assert.False(main.App.IsConnected);
    }
}
