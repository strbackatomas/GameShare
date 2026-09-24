using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using GameShare.Protocol;
using Microsoft.Win32;

namespace GameShare.Client.Setup;

/// <summary>
/// Runs the steps of a preparation the agent planned and the player confirmed. The same code runs in the elevated process
/// (redistributables, the machine's registry) and in the client itself (the player's registry, compatibility mode, profile).
/// </summary>
[SupportedOSPlatform("windows")]
public static class SetupSteps
{
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Exit codes installers use for "done": 3010 and 1641 ask for a restart, 1638 means a newer version is there already.</summary>
    private static readonly HashSet<int> InstallerOk = [0, 3010, 1641, 1638];

    private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    public static async Task<IReadOnlyList<SetupStepResultDto>> RunAsync(IEnumerable<SetupStepDto> steps, CancellationToken ct = default)
    {
        var results = new List<SetupStepResultDto>();
        foreach (var step in steps.Where(s => !s.AlreadyDone))
        {
            try
            {
                var note = step.Kind switch
                {
                    SetupStepKind.Redist => await RunInstallerAsync(step, ct).ConfigureAwait(false),
                    SetupStepKind.RegistryDelete => DeleteKey(step),
                    SetupStepKind.RegistryImport => await ImportAsync(step, ct).ConfigureAwait(false),
                    SetupStepKind.Compatibility => SetLayers(step),
                    SetupStepKind.Profile => CopyProfile(step),
                    _ => throw new InvalidOperationException($"Neznámý krok {step.Kind}."),
                };
                results.Add(new SetupStepResultDto(step.Title, true, note));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new SetupStepResultDto(step.Title, false, ex.Message));
            }
        }
        return results;
    }

    private static async Task<string?> RunInstallerAsync(SetupStepDto step, CancellationToken ct)
    {
        var file = step.File ?? throw new InvalidOperationException("Krok neříká, co spustit.");
        // Checked again right before starting: the file must still be the one the agent verified against the game's manifest.
        await using (var stream = File.OpenRead(file))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            if (!string.Equals(hash, step.FileHash, StringComparison.Ordinal))
                throw new InvalidOperationException($"{Path.GetFileName(file)} se od kontroly změnil, nespouštím ho.");
        }

        var msi = file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        var start = msi
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/i \"{file}\" {step.Arguments ?? "/quiet /norestart"}")
            : new ProcessStartInfo(file, step.Arguments ?? "");
        start.UseShellExecute = false;
        start.WorkingDirectory = Path.GetDirectoryName(file)!;

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{Path.GetFileName(file)} se nespustil.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(InstallerTimeout);
        try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{Path.GetFileName(file)} neskončil do {InstallerTimeout.TotalMinutes:F0} minut.");
        }
        if (!InstallerOk.Contains(process.ExitCode))
            throw new InvalidOperationException($"{Path.GetFileName(file)} skončil s chybou {process.ExitCode}.");
        return process.ExitCode is 3010 or 1641 ? "Hotovo, Windows bude chtít restart." : null;
    }

    private static string? DeleteKey(SetupStepDto step)
    {
        var (hive, path) = Hive(step.Target ?? "");
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        return null;
    }

    /// <summary>reg import, 64-bit view, so the WOW6432Node written in the file is taken literally, as regedit would.</summary>
    private static async Task<string?> ImportAsync(SetupStepDto step, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"gameshare-{Guid.NewGuid():N}.reg");
        try
        {
            await File.WriteAllTextAsync(temp, step.Content ?? "", Encoding.Unicode, ct).ConfigureAwait(false); // UTF-16 with BOM, what reg expects
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"), $"import \"{temp}\" /reg:64")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("reg.exe se nespustil.");
            var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"Import do registru selhal: {error.Trim()}");
            return null;
        }
        finally { TryDelete(temp); }
    }

    private static string? SetLayers(SetupStepDto step)
    {
        using var key = Registry.CurrentUser.CreateSubKey(LayersKey);
        key.SetValue(step.File ?? throw new InvalidOperationException("Krok neříká, pro který program."), "~ " + step.Arguments);
        return null;
    }

    private static string? CopyProfile(SetupStepDto step)
    {
        var source = step.File ?? throw new InvalidOperationException("Krok neříká, odkud kopírovat.");
        var target = ExpandProfile(step.Target ?? "");
        if (Directory.Exists(target)) return "Složka už existuje, nechávám ji být.";
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        Directory.CreateDirectory(target);
        return null;
    }

    /// <summary>{Documents}\Battlefield 2 for the player running this, never anywhere outside their profile folders.</summary>
    public static string ExpandProfile(string target)
    {
        foreach (var (token, folder) in new[]
        {
            ("{Documents}", Environment.SpecialFolder.MyDocuments), ("{AppData}", Environment.SpecialFolder.ApplicationData),
            ("{LocalAppData}", Environment.SpecialFolder.LocalApplicationData),
        })
        {
            if (!target.StartsWith(token, StringComparison.OrdinalIgnoreCase)) continue;
            var root = Environment.GetFolderPath(folder);
            var full = Path.GetFullPath(Path.Combine(root, target[token.Length..].TrimStart('\\', '/')));
            if (!full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{target}' míří mimo profil hráče.");
            return full;
        }
        throw new InvalidOperationException($"'{target}' není složka v profilu hráče.");
    }

    private static (RegistryHive Hive, string Path) Hive(string key)
    {
        var i = key.IndexOf('\\');
        if (i < 0) throw new InvalidOperationException($"'{key}' není klíč registru.");
        return key[..i].ToUpperInvariant() switch
        {
            "HKLM" => (RegistryHive.LocalMachine, key[(i + 1)..]),
            "HKCU" => (RegistryHive.CurrentUser, key[(i + 1)..]),
            _ => throw new InvalidOperationException($"'{key}' není klíč, který smí hra mazat."),
        };
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The elevated half: the client starts itself again with administrator rights and these arguments, and this process only runs the
/// steps from the plan file, writes how they went, and exits. One UAC prompt for the whole preparation.
/// </summary>
public static class SetupHost
{
    public const string Argument = "--gameshare-setup";

    /// <returns>The process exit code when this process was started to run a preparation, null for a normal start.</returns>
    public static int? TryRun(string[] args)
    {
        if (args.Length != 3 || args[0] != Argument) return null;
        if (!OperatingSystem.IsWindows()) return 2;
        try
        {
            var steps = GameShareJson.Deserialize<List<SetupStepDto>>(File.ReadAllText(args[1]));
            var results = SetupSteps.RunAsync(steps.Where(s => s.NeedsAdmin)).GetAwaiter().GetResult();
            File.WriteAllText(args[2], GameShareJson.Serialize(results));
            return results.All(r => r.Ok) ? 0 : 1;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(args[2], GameShareJson.Serialize(new[] { new SetupStepResultDto("Příprava", false, ex.Message) })); }
            catch (IOException) { }
            return 1;
        }
    }
}

/// <summary>Runs a confirmed preparation: the machine's steps in one elevated process, then the player's own here.</summary>
public sealed class ProcessSetupRunner : Services.ISetupRunner
{
    public async Task<IReadOnlyList<SetupStepResultDto>> RunAsync(SetupPlanDto plan, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return [new SetupStepResultDto("Příprava", false, "Příprava her funguje jen ve Windows.")];

        var results = new List<SetupStepResultDto>();
        var admin = plan.Steps.Where(s => s.NeedsAdmin && !s.AlreadyDone).ToList();
        if (admin.Count > 0)
        {
            var elevated = await RunElevatedAsync(admin, ct).ConfigureAwait(false);
            results.AddRange(elevated);
            if (elevated.Any(r => !r.Ok)) return results; // the player's steps can wait until the machine's ones work
        }
        results.AddRange(await SetupSteps.RunAsync(plan.Steps.Where(s => !s.NeedsAdmin), ct).ConfigureAwait(false));
        return results;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<IReadOnlyList<SetupStepResultDto>> RunElevatedAsync(List<SetupStepDto> steps, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "gameshare-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var planFile = Path.Combine(folder, "plan.json");
        var resultFile = Path.Combine(folder, "result.json");
        try
        {
            await File.WriteAllTextAsync(planFile, GameShareJson.Serialize(steps), ct).ConfigureAwait(false);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { SetupHost.Argument, planFile, resultFile },
            };
            Process? process;
            try { process = Process.Start(start); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // the player said no to the UAC prompt
            {
                return [new SetupStepResultDto("Oprávnění správce", false, "Příprava byla zrušena v dotazu Windows na oprávnění.")];
            }
            if (process is null) return [new SetupStepResultDto("Oprávnění správce", false, "Proces přípravy se nespustil.")];
            using (process) await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return File.Exists(resultFile)
                ? GameShareJson.Deserialize<List<SetupStepResultDto>>(await File.ReadAllTextAsync(resultFile, ct).ConfigureAwait(false))
                : [new SetupStepResultDto("Oprávnění správce", false, "Proces přípravy skončil bez výsledku.")];
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
