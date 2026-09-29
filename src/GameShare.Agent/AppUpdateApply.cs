using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using GameShare.Protocol;
using GameShare.Storage;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace GameShare.Agent;

/// <summary>What putting a checked update in place needs to know.</summary>
/// <param name="PreviousFiles">The files of the version that runs, when its package is here. Those the new version no longer has are removed too.</param>
public sealed record AppUpdateApplyContext(AppUpdateService.StagedRelease Staged, IReadOnlyList<string>? PreviousFiles, string UpdatesDir);

/// <summary>
/// Puts a downloaded and checked version of GameShare in place of the running one. Each build does it its own way: the service
/// through a helper that stops and restarts it, the portable exe by replacing itself. Registered by whoever hosts the agent.
/// </summary>
public interface IAppUpdateApplier
{
    /// <summary>Null when it can be applied here now, otherwise why not, in words for the user.</summary>
    string? CannotApplyReason(AppUpdateService.StagedRelease staged);

    /// <summary>Starts putting it in place. Returns once that is handed over; this process usually ends moments later.</summary>
    Task ApplyAsync(AppUpdateApplyContext context, CancellationToken ct);
}

/// <summary>For a host that registered nothing better, such as an agent started from a console: says so instead of trying.</summary>
public sealed class NoAppUpdateApplier : IAppUpdateApplier
{
    public string? CannotApplyReason(AppUpdateService.StagedRelease staged) =>
        "Tenhle agent neběží jako služba Windows, takže se neumí vyměnit sám. Novou verzi nainstaluj ručně.";

    public Task ApplyAsync(AppUpdateApplyContext context, CancellationToken ct) =>
        throw new InvalidOperationException(CannotApplyReason(context.Staged));
}

/// <summary>The request the service leaves for the helper, in the updates folder only SYSTEM and the administrators can write to.</summary>
public sealed record AppUpdateApplyRequest(
    string Version, GameManifest Manifest, string StagingDir, string InstallDir, IReadOnlyList<string>? PreviousFiles,
    string ServiceName, int AgentProcessId, int LocalApiPort, string ResultFile, string LogFile);

/// <summary>What happened, for the agent that starts next, whichever version that is.</summary>
public sealed record AppUpdateResult(string Version, bool Ok, string? Error, DateTimeOffset At)
{
    public const string FileName = "last-result.json";
}

/// <summary>
/// The installed service. The running agent never replaces its own files: it starts the new version's agent from the checked package
/// in the helper mode of <see cref="AppUpdateHelper"/>, which stops the service, swaps the files, starts it again and waits for the new
/// version to answer. Needs no prompt, the service already runs as SYSTEM.
/// </summary>
public sealed class ServiceUpdateApplier : IAppUpdateApplier
{
    private const string AgentExe = "GameShare.Agent.exe";

    private readonly AgentOptions _options;
    private readonly ILogger<ServiceUpdateApplier> _log;

    public ServiceUpdateApplier(AgentOptions options, ILogger<ServiceUpdateApplier> log)
    {
        _options = options;
        _log = log;
    }

    public string? CannotApplyReason(AppUpdateService.StagedRelease staged)
    {
        if (!OperatingSystem.IsWindows() || !WindowsServiceHelpers.IsWindowsService())
            return new NoAppUpdateApplier().CannotApplyReason(staged);
        if (!File.Exists(Path.Combine(staged.PackageDir, AgentExe)))
            return "Stažený balíček neobsahuje agenta, tuhle verzi je potřeba nainstalovat ručně.";
        return null;
    }

    public async Task ApplyAsync(AppUpdateApplyContext context, CancellationToken ct)
    {
        if (CannotApplyReason(context.Staged) is { } reason) throw new InvalidOperationException(reason);
        var release = context.Staged.Release;
        var request = new AppUpdateApplyRequest(
            release.Version, release.Manifest, context.Staged.PackageDir, _options.ResolveUpdateInstallDir(), context.PreviousFiles,
            AgentHost.ServiceName, Environment.ProcessId, _options.LocalApiPort,
            Path.Combine(context.UpdatesDir, AppUpdateResult.FileName),
            Path.Combine(_options.ResolveDataDir(), "logs", $"update-{release.Version}.log"));
        var requestPath = Path.Combine(context.UpdatesDir, "apply-request.json");
        await File.WriteAllTextAsync(requestPath, GameShareJson.Serialize(request), ct).ConfigureAwait(false);

        // The new version's own agent, straight from the package that was just checked again. Not a child the service waits for:
        // it outlives the service it stops.
        var helper = new ProcessStartInfo(Path.Combine(context.Staged.PackageDir, AgentExe))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = context.Staged.PackageDir,
        };
        helper.ArgumentList.Add(AppUpdateHelper.Argument);
        helper.ArgumentList.Add(requestPath);
        using var process = Process.Start(helper) ?? throw new InvalidOperationException("Pomocníka aktualizace se nepodařilo spustit.");
        _log.LogInformation("Handed GameShare {Version} over to the update helper (process {Pid}), the service stops now", release.Version, process.Id);
    }
}

/// <summary>What the helper does to the service. An interface so the steps can be tested without a real Windows service.</summary>
public interface IServiceControl
{
    /// <summary>Stops it and returns once its process is gone.</summary>
    void Stop();

    /// <summary>Starts it, and does nothing when it runs already.</summary>
    void Start();
}

/// <summary>
/// The helper mode of the agent's exe, started from the checked package of the new version by <see cref="ServiceUpdateApplier"/>:
/// stop the service, swap the files (<see cref="AppUpdateFiles"/>), start it, and keep the new files only when the new version answers.
/// Otherwise the old files are put back and started again. Either way the outcome is written for the next agent to show.
/// </summary>
public static class AppUpdateHelper
{
    public const string Argument = "--gameshare-apply-update";

    /// <summary>How long the new version gets to answer before the old one is put back.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);

    /// <returns>The exit code when this process was started as the helper, null for a normal start.</returns>
    public static int? TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != Argument) return null;
        if (!OperatingSystem.IsWindows()) return 2;

        AppUpdateApplyRequest request;
        try { request = GameShareJson.Deserialize<AppUpdateApplyRequest>(File.ReadAllText(args[1])); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return 2; }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        return RunAsync(request, new WindowsServiceControl(request.ServiceName, request.AgentProcessId),
            ct => ProbeVersionAsync(http, request.LocalApiPort, ct), StartTimeout).GetAwaiter().GetResult();
    }

    /// <param name="probeVersion">The version the agent on this PC answers with, or null while nothing answers.</param>
    public static async Task<int> RunAsync(
        AppUpdateApplyRequest request, IServiceControl service, Func<CancellationToken, Task<string?>> probeVersion, TimeSpan startTimeout)
    {
        void Log(string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(request.LogFile)!);
                File.AppendAllText(request.LogFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the result file still says what happened */ }
        }

        int Finish(bool ok, string? error)
        {
            Log(ok ? $"Done, GameShare {request.Version} runs." : $"Failed: {error}");
            try { File.WriteAllText(request.ResultFile, GameShareJson.Serialize(new AppUpdateResult(request.Version, ok, error, DateTimeOffset.UtcNow))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"Could not write the result: {ex.Message}"); }
            return ok ? 0 : 1;
        }

        Log($"Updating {request.InstallDir} to GameShare {request.Version} from {request.StagingDir}");
        try
        {
            // An earlier update whose helper never got to decide: the agent that asked for this one runs from those files, so they work.
            if (AppUpdateFiles.HasPendingSwap(request.InstallDir)) AppUpdateFiles.Commit(request.InstallDir);

            service.Stop();
            Log("Service stopped");
            try
            {
                await AppUpdateFiles.SwapAsync(request.StagingDir, request.InstallDir, request.Manifest, request.PreviousFiles).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // SwapAsync already put the old files back.
                service.Start();
                return Finish(false, $"Soubory se nepodařilo vyměnit, běží dál předchozí verze: {ex.Message}");
            }
            Log("Files swapped, starting the service");
            service.Start();

            if (await WaitForVersionAsync(probeVersion, request.Version, startTimeout).ConfigureAwait(false))
            {
                AppUpdateFiles.Commit(request.InstallDir);
                return Finish(true, null);
            }

            Log($"The new version did not answer within {startTimeout.TotalSeconds:N0} s, putting the previous one back");
            service.Stop();
            AppUpdateFiles.Rollback(request.InstallDir);
            service.Start();
            return Finish(false, $"Verze {request.Version} se nerozběhla, vrátila se předchozí. Podrobnosti jsou v {request.LogFile}.");
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            try { service.Start(); } catch (Exception start) { Log($"Could not start the service: {start.Message}"); }
            return Finish(false, $"Aktualizace se nepovedla: {ex.Message}");
        }
    }

    private static async Task<bool> WaitForVersionAsync(Func<CancellationToken, Task<string?>> probeVersion, string version, TimeSpan timeout)
    {
        using var limit = new CancellationTokenSource(timeout);
        while (!limit.IsCancellationRequested)
        {
            try
            {
                if (await probeVersion(limit.Token).ConfigureAwait(false) == version) return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { /* not up yet */ }
            try { await Task.Delay(500, limit.Token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
        return false;
    }

    private static async Task<string?> ProbeVersionAsync(HttpClient http, int port, CancellationToken ct)
    {
        var json = await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/api/status"), ct).ConfigureAwait(false);
        return GameShareJson.Deserialize<StatusDto>(json).Version;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceControl : IServiceControl
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly string _name;
    private int _pid;

    public WindowsServiceControl(string name, int pid)
    {
        _name = name;
        _pid = pid;
    }

    public void Stop()
    {
        using var service = new ServiceController(_name);
        if (service.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)) service.Stop();
        try { service.WaitForStatus(ServiceControllerStatus.Stopped, Timeout); }
        catch (System.ServiceProcess.TimeoutException) { /* a hanging shutdown; its process is ended below */ }

        // The service can report Stopped a moment before its process has let go of its files.
        if (_pid == 0) { Thread.Sleep(TimeSpan.FromSeconds(2)); return; } // a process started after the helper, its id is not known here
        try
        {
            using var process = Process.GetProcessById(_pid);
            if (!process.WaitForExit(Timeout)) { process.Kill(entireProcessTree: false); process.WaitForExit(Timeout); }
        }
        catch (ArgumentException) { /* already gone */ }
        _pid = 0;
    }

    public void Start()
    {
        using var service = new ServiceController(_name);
        if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return;
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, Timeout);
        service.Refresh();
    }
}
