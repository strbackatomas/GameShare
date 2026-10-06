using System.Runtime.InteropServices;

namespace GameShare.Client.Services;

/// <summary>
/// Asks Windows the way the old games themselves ask: waveInGetNumDevs counts the recording devices that are there and switched on,
/// so a headset that is unplugged or a microphone that is disabled is not counted.
/// </summary>
public sealed class WindowsDeviceCheck : IDeviceCheck
{
    public bool HasMicrophone()
    {
        if (!OperatingSystem.IsWindows()) return true; // nothing to ask, so nothing to warn about
        try { return WaveInGetNumDevs() > 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return true; }
    }

    [DllImport("winmm.dll", EntryPoint = "waveInGetNumDevs")]
    private static extern uint WaveInGetNumDevs();
}
