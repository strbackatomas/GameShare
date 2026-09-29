using System.Diagnostics;

namespace GameShare.Client.Services;

/// <summary>Restarts the client from its own program file, which an update of the service replaced while it ran.</summary>
public sealed class ProcessClientRestarter : IClientRestarter
{
    public string? VersionOnDisk()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe || !File.Exists(exe)) return null;
            var info = FileVersionInfo.GetVersionInfo(exe);
            // Written by the build from the one <Version>, as MAJOR.MINOR.PATCH.0; the fourth part is always 0.
            return Version.TryParse(info.FileVersion, out var v) ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Restart()
    {
        if (Environment.ProcessPath is not { } exe) return;
        using (Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })) { }
        App.RequestExit();
    }
}
