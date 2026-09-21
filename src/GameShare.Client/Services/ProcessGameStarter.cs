using System.ComponentModel;
using System.Diagnostics;
using GameShare.Protocol;

namespace GameShare.Client.Services;

/// <summary>
/// Starts the program the agent has checked. The shell starts it, so a game that asks for administrator rights gets its prompt.
/// The path is a program of the game itself, the shell does not look at anything else.
/// </summary>
public sealed class ProcessGameStarter : IGameStarter
{
    public void Start(LaunchInfoDto info)
    {
        var start = new ProcessStartInfo(info.ExecutablePath) { WorkingDirectory = info.WorkingDirectory, UseShellExecute = true };
        if (!string.IsNullOrEmpty(info.Arguments)) start.Arguments = info.Arguments;

        try { Process.Start(start)?.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // Also what a player sees after refusing the administrator prompt.
            throw new AgentException($"Hru se nepodařilo spustit: {ex.Message}", inner: ex);
        }
    }
}
