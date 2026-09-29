using System.Diagnostics;
using GameShare.Agent;
using GameShare.Storage;

namespace GameShare.Standalone;

/// <summary>
/// The portable exe replaces itself: the running file is renamed aside, which Windows allows, the checked new one is copied to its
/// name, and the new one is started while this one quits. It waits for this process to be gone before it starts its agent, see
/// <see cref="AfterUpdate"/>. Works under whatever name the guest saved the exe, and needs no administrator rights.
/// </summary>
internal sealed class StandaloneUpdateApplier : IAppUpdateApplier
{
    public const string PackagedExe = "GameShare-LanParty.exe";
    private const string KeyFile = "trust-public.key";

    private readonly Action _exit;

    /// <param name="exit">Closes the application the way the tray's "Ukončit" does, stopping the agent hosted here.</param>
    public StandaloneUpdateApplier(Action exit) => _exit = exit;

    private static string? RunningExe => Environment.ProcessPath;

    public string? CannotApplyReason(AppUpdateService.StagedRelease staged)
    {
        if (RunningExe is not { } exe) return "Nepodařilo se zjistit, odkud program běží. Novou verzi stáhni ručně.";
        if (!staged.Release.Manifest.Files.Any(f => f.Path == PackagedExe))
            return $"Stažený balíček neobsahuje {PackagedExe}, novou verzi stáhni ručně.";
        var folder = Path.GetDirectoryName(exe)!;
        if (!CanWrite(folder))
            return $"Do složky {folder} nejde zapisovat, takže se program nemůže vyměnit. Přesuň ho jinam (třeba do Dokumentů), nebo stáhni novou verzi ručně.";
        return null;
    }

    public async Task ApplyAsync(AppUpdateApplyContext context, CancellationToken ct)
    {
        if (CannotApplyReason(context.Staged) is { } reason) throw new InvalidOperationException(reason);
        var exe = RunningExe!;
        var folder = Path.GetDirectoryName(exe)!;
        var expected = context.Staged.Release.Manifest.Files.Single(f => f.Path == PackagedExe);

        var aside = FreeAsideName(exe);
        File.Move(exe, aside);
        try
        {
            File.Copy(Path.Combine(context.Staged.PackageDir, PackagedExe), exe);
            // What will be started is what was signed, not only what lay in the updates folder a moment ago.
            if (await ManifestVerifier.HashFileAsync(exe, ct).ConfigureAwait(false) != expected.Hash)
                throw new InvalidDataException("The copied program is not the one that was signed.");
        }
        catch
        {
            try { File.Delete(exe); } catch (IOException) { }
            File.Move(aside, exe);
            throw;
        }

        // The public key turns verified games on. A copy handed out without it gets the one from the release.
        var key = Path.Combine(context.Staged.PackageDir, KeyFile);
        if (File.Exists(key) && !File.Exists(Path.Combine(folder, KeyFile)))
        {
            try { File.Copy(key, Path.Combine(folder, KeyFile)); }
            catch (IOException) { /* verified games stay as they were */ }
        }

        var next = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = folder };
        next.ArgumentList.Add(AfterUpdateArgument);
        next.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using (Process.Start(next)) { }

        // A moment for the answer to the request that asked for this to reach the window, then the same exit as "Ukončit".
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            _exit();
        }, CancellationToken.None);
    }

    public const string AfterUpdateArgument = "--gameshare-after-update";

    /// <summary>
    /// In the new exe, started by <see cref="ApplyAsync"/>: waits until the old process is gone, so its ports are free and this one hosts
    /// the agent rather than attaching to the old one, then deletes the old exe.
    /// </summary>
    /// <returns>The arguments without the ones meant for this.</returns>
    public static string[] AfterUpdate(string[] args)
    {
        if (args.Length < 2 || args[0] != AfterUpdateArgument) return args;
        if (int.TryParse(args[1], out var pid))
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                old.WaitForExit(TimeSpan.FromSeconds(30));
            }
            catch (ArgumentException) { /* gone already */ }
        }

        if (RunningExe is { } exe)
            foreach (var leftover in Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, Path.GetFileName(exe) + "*" + AppUpdateFiles.AsideSuffix))
            {
                try { File.Delete(leftover); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next time */ }
            }
        return args[2..];
    }

    private static string FreeAsideName(string exe)
    {
        for (int i = 0; ; i++)
        {
            var aside = i == 0 ? exe + AppUpdateFiles.AsideSuffix : $"{exe}.{i}{AppUpdateFiles.AsideSuffix}";
            if (!File.Exists(aside)) return aside;
            try { File.Delete(aside); return aside; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still running from an earlier update */ }
        }
    }

    private static bool CanWrite(string folder)
    {
        var probe = Path.Combine(folder, $".gameshare-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
