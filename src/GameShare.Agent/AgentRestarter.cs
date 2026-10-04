using System.Diagnostics;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace GameShare.Agent;

/// <summary>Restarts the agent, so what is only read at startup takes effect. A seam, so tests restart their agents themselves.</summary>
public interface IAgentRestarter
{
    /// <summary>Starts a graceful stop and comes back. Returns at once; the caller answers its request before the process goes.</summary>
    void Restart();
}

/// <summary>
/// Waits for the normal graceful shutdown (hosted services get to stop cleanly, e.g. resume data is saved) before relaunching.
/// A Windows service is left to its own configured failure/restart action (see scripts/install-agent.ps1) rather than self-launched,
/// so it stays under the Service Control Manager.
/// </summary>
public sealed class AgentRestarter(IHostApplicationLifetime lifetime) : IAgentRestarter
{
    public void Restart()
    {
        lifetime.ApplicationStopped.Register(() =>
        {
            if (!WindowsServiceHelpers.IsWindowsService())
            {
                var exe = Environment.ProcessPath;
                if (exe is not null)
                {
                    try { Process.Start(exe, Environment.GetCommandLineArgs().Skip(1)); }
                    catch { /* best effort; this process is exiting anyway */ }
                }
            }
            Environment.Exit(0);
        });
        lifetime.StopApplication();
    }
}
