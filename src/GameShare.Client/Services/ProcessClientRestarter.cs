using System.Diagnostics;
using System.Reflection;

namespace GameShare.Client.Services;

/// <summary>Restarts the client from its own program file, which an update of the service replaced while it ran.</summary>
public sealed class ProcessClientRestarter : IClientRestarter
{
    /// <remarks>
    /// Read from the client's assembly metadata, not from the version resource of its exe: asked about its own running exe, Windows
    /// answers from the image loaded in memory, which is the old version however the file on disk changed. Null for the portable
    /// single-file build, which has no assembly file of its own on disk and restarts itself anyway.
    /// </remarks>
    public string? VersionOnDisk()
    {
        try
        {
            // Not Assembly.Location, which a single-file build leaves empty; there the file simply is not on disk.
            var dll = Path.Combine(AppContext.BaseDirectory, typeof(ProcessClientRestarter).Assembly.GetName().Name + ".dll");
            if (!File.Exists(dll)) return null;
            // Written by the build from the one <Version>, as MAJOR.MINOR.PATCH.0; the fourth part is always 0.
            return AssemblyName.GetAssemblyName(dll).Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException) { return null; }
    }

    public void Restart()
    {
        if (Environment.ProcessPath is not { } exe) return;
        using (Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })) { }
        App.RequestExit();
    }
}
