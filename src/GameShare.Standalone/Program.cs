using System.Runtime.InteropServices;
using Avalonia;
using GameShare.Agent;
using GameShare.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace GameShare.Standalone;

/// <summary>
/// Bundles the agent and the client into one portable exe, so a LAN-party guest can play and share games without
/// installing a Windows service or running as administrator. Nothing that needs Avalonia may run before the
/// framework is initialised, so the agent is started (or found) before that, and everything else is minimal.
/// </summary>
internal static class Program
{
    private static readonly Uri LocalAgentUrl = new("http://127.0.0.1:47701"); // AgentOptions.LocalApiPort default

    // Per signed-in user: a copy started by someone else on this PC still finds the agent through the probe below.
    private const string InstanceName = @"Local\GameShare.LanParty";
    private const string ShowWindowName = @"Local\GameShare.LanParty.ShowWindow";

    [STAThread]
    public static void Main(string[] args)
    {
        // Started again with administrator rights to run a game's preparation (GameShare.Client\Setup): only that, no agent, no window.
        if (GameShare.Client.Setup.SetupHost.TryRun(args) is { } exitCode)
        {
            Environment.ExitCode = exitCode;
            return;
        }

        // Started by the previous version after it put this one in place: wait until it has quit, then carry on as a normal start.
        args = StandaloneUpdateApplier.AfterUpdate(args);

        // One copy at a time. The probe alone let a second copy started while the first was still starting host an agent too,
        // which then ran next to the first one on the same data and game files. Started again, the exe only brings the window up.
        using var instance = new Mutex(false, InstanceName, out var firstCopy);
        using var showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowName);
        if (!firstCopy)
        {
            AllowSetForegroundWindow(AsfwAny); // this process was started by the user, so it may hand the foreground on
            showWindow.Set();
            return;
        }
        var showWindowWait = ThreadPool.RegisterWaitForSingleObject(showWindow, (_, _) => App.ShowMainWindow(), null, Timeout.Infinite, executeOnlyOnce: false);

        WebApplication? hosted = null;

        try
        {
            if (!ProbeExistingAgentAsync().GetAwaiter().GetResult())
                hosted = SelfHostAgentAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Never let a startup failure kill the process before a window exists to explain it. hosted stays null;
            // the client's own "agent unreachable" banner (it retries forever) takes it from here.
            Console.Error.WriteLine($"GameShare: could not start the built-in agent: {ex}");
        }

        App.AgentUrl = LocalAgentUrl;
        App.IsPortable = true;
        // The tray icon and close-to-tray behavior are the client's own (GameShare.Client\TrayController.cs); this is
        // the one extra step its "Ukončit" must do that the plain client never needs: stop the agent hosted right here.
        App.BeforeShutdownAsync = async () =>
        {
            if (hosted is null) return;
            await hosted.StopAsync().ConfigureAwait(false);
            await hosted.DisposeAsync().ConfigureAwait(false);
        };

        GameShare.Client.Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        showWindowWait.Unregister(null);
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>True when something already answers as a GameShare agent on the fixed local port — for example the
    /// real Windows-Service install on this same PC, or this exe run by another user signed in here. In that case
    /// this process must not also try to bind the agent's ports, it just becomes a plain client of the existing one.</summary>
    private static async Task<bool> ProbeExistingAgentAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) }; // loopback, this is generous
        try
        {
            using var response = await http.GetAsync(new Uri(LocalAgentUrl, "/api/status")).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; } // connection refused, timeout, whatever: nothing usable is there
    }

    private static async Task<WebApplication> SelfHostAgentAsync()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameShare");
        var gamesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GameShare Games");
        Directory.CreateDirectory(gamesRoot); // InitialGameRoots is only used the very first time settings are loaded

        var app = await AgentHost.BuildAsync([], o =>
        {
            o.DataDir = dataDir; // never %ProgramData%: this process is not elevated and must not collide with a real install
            o.InitialGameRoots = [gamesRoot];
            o.RemoteManagementAllowed = false; // a guest's PC is nobody else's to manage
            // No installer sets trust up for a guest: a trust-public.key handed out next to this exe does (publish.ps1 puts it there).
            if (o.UseTrustKeyFileIn(AppContext.BaseDirectory) is { } note) Console.WriteLine($"GameShare: {note}");
        }, configureServices: services =>
            // Only a self-hosted instance updates itself. One that attached to an installed agent leaves that to the service.
            services.AddSingleton<IAppUpdateApplier>(new StandaloneUpdateApplier(App.RequestExit))).ConfigureAwait(false);
        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            // Background work starts before the ports are bound, so a failed start (a port taken) would leave a second agent
            // seeding and downloading from the same files with nothing to stop it. Stop it here.
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return app;
    }
}
