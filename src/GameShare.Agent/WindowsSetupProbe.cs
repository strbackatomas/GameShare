using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using GameShare.Protocol;
using GameShare.Storage;
using Microsoft.Win32;

namespace GameShare.Agent;

/// <summary>
/// Looks at this PC for what a redistributables package says marks it as installed. Only machine-wide places: the agent runs as
/// a service, so the current user's profile here is the service's, not the player's.
/// </summary>
public sealed class WindowsSetupProbe : ISetupProbe
{
    public bool IsInstalled(InstalledCheck check)
    {
        if (!OperatingSystem.IsWindows()) return false;
        bool any = false;
        if (check.File is { Length: > 0 } file)
        {
            any = true;
            if (!File.Exists(Expand(file))) return false;
        }
        if (check.RegistryKey is { Length: > 0 } key)
        {
            any = true;
            if (!RegistryHas(key, check.RegistryValue)) return false;
        }
        if (check.Uninstall is { Length: > 0 } pattern)
        {
            any = true;
            if (!UninstallHas(pattern)) return false;
        }
        return any;
    }

    /// <summary>{SysWOW64}\d3dx9_43.dll and the like, machine folders only.</summary>
    public static string Expand(string path)
    {
        foreach (var (token, folder) in new[]
        {
            ("{SysWOW64}", Environment.SpecialFolder.SystemX86), ("{System}", Environment.SpecialFolder.System),
            ("{Windows}", Environment.SpecialFolder.Windows), ("{ProgramFiles}", Environment.SpecialFolder.ProgramFiles),
            ("{ProgramFilesX86}", Environment.SpecialFolder.ProgramFilesX86), ("{CommonAppData}", Environment.SpecialFolder.CommonApplicationData),
        })
            path = path.Replace(token, Environment.GetFolderPath(folder), StringComparison.OrdinalIgnoreCase);
        return path;
    }

    public string? InstalledVersion(InstalledCheck check)
    {
        if (!OperatingSystem.IsWindows() || check.Uninstall is not { Length: > 0 } pattern) return null;
        var regex = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
        string? newest = null;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var app = uninstall.OpenSubKey(name);
                if (app?.GetValue("DisplayName") is not string display || !regex.IsMatch(display)) continue;
                // An old entry left behind next to the current one happens: the newest is what runs.
                if (app.GetValue("DisplayVersion") is string version && (newest is null || GameVersion.Compare(version, newest) > 0)) newest = version;
            }
        }
        return newest;
    }

    public bool FirewallAllows(string program)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return FirewallRulesAllow(Path.GetFullPath(program)); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or ArgumentException)
        {
            return false; // unknown, so preparing makes the rule anyway
        }
    }

    private const int ProfilePrivate = 2, ProtocolTcp = 6, ProtocolUdp = 17, ProtocolAny = 256;

    /// <summary>
    /// An enabled inbound rule for exactly this program lets in TCP and UDP on private networks, and none blocks it there: a block
    /// rule wins, and one is what Windows leaves behind when a player cancels its question.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool FirewallRulesAllow(string program)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
        bool tcp = false, udp = false;
        foreach (dynamic r in policy.Rules)
        {
            if (!(bool)r.Enabled || (int)r.Direction != 1 || ((int)r.Profiles & ProfilePrivate) == 0) continue; // 1 is inbound
            string? app = r.ApplicationName;
            if (string.IsNullOrEmpty(app) || !string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(app)), program, StringComparison.OrdinalIgnoreCase)) continue;
            if ((int)r.Action != 1) return false;
            int protocol = r.Protocol;
            tcp |= protocol is ProtocolTcp or ProtocolAny;
            udp |= protocol is ProtocolUdp or ProtocolAny;
        }
        return tcp && udp;
    }

    [SupportedOSPlatform("windows")]
    private static bool RegistryHas(string key, string? value)
    {
        var (hive, path) = Split(key);
        if (hive is null) return false;
        using var root = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64); // WOW6432Node is written out in the path when meant
        using var sub = root.OpenSubKey(path);
        return sub is not null && (value is null || sub.GetValue(value) is not null);
    }

    [SupportedOSPlatform("windows")]
    private static bool UninstallHas(string pattern)
    {
        var regex = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var app = uninstall.OpenSubKey(name);
                if (app?.GetValue("DisplayName") is string display && regex.IsMatch(display)) return true;
            }
        }
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static (RegistryHive? Hive, string Path) Split(string key)
    {
        var i = key.IndexOf('\\');
        var head = i < 0 ? key : key[..i];
        var rest = i < 0 ? "" : key[(i + 1)..];
        RegistryHive? hive = head.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            _ => null,
        };
        return (hive, rest);
    }
}
