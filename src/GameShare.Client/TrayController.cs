using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using GameShare.Client.Services;
using GameShare.Client.ViewModels;
using GameShare.Protocol;

namespace GameShare.Client;

/// <summary>
/// Closing the window hides it instead of exiting, so background work (live updates, and for the standalone build
/// its embedded agent) keeps going. The tray menu can start any game that is ready to play without opening the
/// window, and its tooltip reflects what the agent currently sees. Only "Ukončit" does a real shutdown.
/// </summary>
internal static class TrayController
{
    private static bool _exiting;
    private static CancellationTokenSource? _flashCts;

    public static void Attach(IClassicDesktopStyleApplicationLifetime desktop, Views.MainWindow window, AppModel app)
    {
        var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://GameShare/Assets/icon.ico")));

        var playMenu = new NativeMenu();
        var play = new NativeMenuItem("Hrát") { Menu = playMenu };
        RebuildPlayMenu(playMenu, app);
        app.GamesChanged += (_, _) => RebuildPlayMenu(playMenu, app);

        var open = new NativeMenuItem("Otevřít");
        open.Click += (_, _) => Restore(window);
        var exit = new NativeMenuItem("Ukončit");
        exit.Click += async (_, _) => await ShutdownAsync(desktop).ConfigureAwait(true);

        var tray = new TrayIcon
        {
            Icon = icon,
            Menu = new NativeMenu { Items = { play, new NativeMenuItemSeparator(), open, exit } },
        };
        tray.Clicked += (_, _) => Restore(window);

        RefreshTooltip(tray, app);
        app.GamesChanged += (_, _) => RefreshTooltip(tray, app);
        app.Peers.CollectionChanged += (_, _) => RefreshTooltip(tray, app);
        app.PropertyChanged += (_, _) => RefreshTooltip(tray, app);
        app.EventReceived += (_, e) => _ = FlashOnNotableEventAsync(tray, app, e);

        Avalonia.Controls.TrayIcon.SetIcons(Avalonia.Application.Current!, new TrayIcons { tray });

        window.Closing += (_, e) =>
        {
            if (_exiting) return; // a real shutdown is in progress, let this Closing (raised by desktop.Shutdown()) through
            e.Cancel = true;
            window.Hide();
        };
    }

    /// <summary>One item per game the player could press "Hrát" on right now. Reuses PlayCommand as-is, so a
    /// refusal or "Hra se spouští…" still ends up wherever the card normally shows it, once the window is reopened.</summary>
    private static void RebuildPlayMenu(NativeMenu menu, AppModel app)
    {
        menu.Items.Clear();
        var playable = app.Games.Where(g => g.CanPlay).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (playable.Count == 0)
        {
            menu.Items.Add(new NativeMenuItem("Žádné hry ke spuštění") { IsEnabled = false });
            return;
        }
        foreach (var game in playable)
        {
            var item = new NativeMenuItem(game.Name);
            item.Click += (_, _) => game.PlayCommand.Execute(null);
            menu.Items.Add(item);
        }
    }

    private static void RefreshTooltip(TrayIcon tray, AppModel app)
    {
        if (!app.IsConnected) { tray.ToolTipText = "GameShare — " + app.ConnectionText; return; }
        int installed = app.Games.Count(g => g.IsInstalled || g.IsDamaged);
        tray.ToolTipText = $"GameShare — {installed} her nainstalováno, {Format.PcCount(app.Peers.Count)} na síti";
    }

    /// <summary>No native balloon popup: the tooltip itself briefly shows what happened, then reverts to the normal
    /// status. Cheap, no platform-specific notification API, good enough for something as low-stakes as this.</summary>
    private static async Task FlashOnNotableEventAsync(TrayIcon tray, AppModel app, AgentEvent e)
    {
        string? message = e switch
        {
            { Name: GameShareEvents.DownloadCompleted, Payload: DownloadDto d } => $"Staženo: {d.GameName}",
            { Name: GameShareEvents.GameDiscovered, Payload: GameDto { State: GameState.AvailableOnLan } g } => $"Nová hra na síti: {g.Name}",
            _ => null,
        };
        if (message is null) return;

        _flashCts?.Cancel();
        var cts = new CancellationTokenSource();
        _flashCts = cts;
        tray.ToolTipText = "GameShare — " + message;
        try { await Task.Delay(TimeSpan.FromSeconds(6), cts.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; } // a newer flash took over, leave its message showing
        RefreshTooltip(tray, app);
    }

    private static void Restore(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static async Task ShutdownAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _exiting = true;
        if (App.BeforeShutdownAsync is { } beforeShutdown) await beforeShutdown().ConfigureAwait(true);
        desktop.Shutdown();
    }
}
