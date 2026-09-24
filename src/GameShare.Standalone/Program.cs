using Avalonia;
using GameShare.Agent;
using GameShare.Client;
using Microsoft.AspNetCore.Builder;

namespace GameShare.Standalone;

/// <summary>
/// Bundles the agent and the client into one portable exe, so a LAN-party guest can play and share games without
/// installing a Windows service or running as administrator. Nothing that needs Avalonia may run before the
/// framework is initialised, so the agent is started (or found) before that, and everything else is minimal.
/// </summary>
internal static class Program
{
    private static readonly Uri LocalAgentUrl = new("http://127.0.0.1:47701"); // AgentOptions.LocalApiPort default

    [STAThread]
    public static void Main(string[] args)
    {
        // Started again with administrator rights to run a game's preparation (GameShare.Client\Setup): only that, no agent, no window.
        if (GameShare.Client.Setup.SetupHost.TryRun(args) is { } exitCode)
        {
            Environment.ExitCode = exitCode;
            return;
        }

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
        // The tray icon and close-to-tray behavior are the client's own (GameShare.Client\TrayController.cs); this is
        // the one extra step its "Ukončit" must do that the plain client never needs: stop the agent hosted right here.
        App.BeforeShutdownAsync = async () =>
        {
            if (hosted is null) return;
            await hosted.StopAsync().ConfigureAwait(false);
            await hosted.DisposeAsync().ConfigureAwait(false);
        };

        GameShare.Client.Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>True when something already answers as a GameShare agent on the fixed local port — for example the
    /// real Windows-Service install on this same PC, or another copy of this exe started a moment ago. In that case
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
        }).ConfigureAwait(false);
        await app.StartAsync().ConfigureAwait(false);
        return app;
    }
}
