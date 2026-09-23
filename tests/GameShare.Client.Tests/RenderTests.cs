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
    public async Task Downloads_page_shows_progress_and_sources()
    {
        var main = await BuildAsync(Populated());
        var frame = await ShowAsync(main, "Přenosy", "downloads.png");
        Assert.True(DistinctColours(frame) > 200);
    }

    [AvaloniaFact]
    public async Task Network_page_lists_the_other_pcs()
    {
        var main = await BuildAsync(Populated());
        var frame = await ShowAsync(main, "Síť", "network.png");
        Assert.True(DistinctColours(frame) > 100);
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
