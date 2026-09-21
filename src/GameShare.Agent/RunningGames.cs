using System.ComponentModel;
using System.Diagnostics;
using GameShare.Core;
using GameShare.Core.Data;

namespace GameShare.Agent;

/// <summary>A game started or stopped running on this PC.</summary>
public sealed record RunningChange(Installation Installation, bool Running);

/// <summary>
/// Knows which installed games are running, by looking for processes whose program lives inside a game's folder. That also finds a game
/// started some other way than through GameShare. A game that starts its real program from somewhere else, such as a store client,
/// is not recognised. Files of a running game are not rewritten by a repair or an update, and its seed steps aside.
/// </summary>
public sealed class RunningGames
{
    private readonly GameShareDb _db;
    private readonly AgentOptions _options;
    private readonly ILogger<RunningGames> _log;
    private readonly Func<IReadOnlyList<string>> _programs;
    private readonly SemaphoreSlim _looking = new(1, 1);
    private HashSet<long> _running = [];

    /// <param name="programs">Full paths of the programs that run now. Defaults to the processes of this PC. Tests give their own.</param>
    public RunningGames(GameShareDb db, AgentOptions options, ILogger<RunningGames> log, Func<IReadOnlyList<string>>? programs = null)
    {
        _db = db;
        _options = options;
        _log = log;
        _programs = programs ?? ProcessPaths;
    }

    public event EventHandler<RunningChange>? Changed;

    /// <summary>As of the last look, at most one interval old.</summary>
    public bool IsRunning(Installation installation) => Volatile.Read(ref _running).Contains(installation.Id);

    /// <summary>Looks now, for a decision that must not go by a picture that is a few seconds old.</summary>
    public async Task<bool> IsRunningNowAsync(Installation installation, CancellationToken ct = default)
    {
        await LookAsync(ct).ConfigureAwait(false);
        return IsRunning(installation);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.RunningCheckInterval);
        do
        {
            try { await LookAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "Could not look for running games, will try again"); }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task LookAsync(CancellationToken ct)
    {
        await _looking.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var installs = await _db.ListInstallationsAsync(ct).ConfigureAwait(false);
            var now = new HashSet<long>();
            if (installs.Count > 0)
            {
                var programs = await Task.Run(_programs, ct).ConfigureAwait(false); // walking every process is not for the caller's thread
                foreach (var inst in installs)
                {
                    var folder = Path.GetFullPath(inst.InstallPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (programs.Any(p => p.StartsWith(folder, StringComparison.OrdinalIgnoreCase))) now.Add(inst.Id);
                }
            }

            var before = Interlocked.Exchange(ref _running, now);
            foreach (var inst in installs)
            {
                bool was = before.Contains(inst.Id), isNow = now.Contains(inst.Id);
                if (was == isNow) continue;
                _log.LogInformation("{Path} {State}", inst.InstallPath, isNow ? "is running" : "stopped running");
                Changed?.Invoke(this, new RunningChange(inst, isNow));
            }
        }
        finally { _looking.Release(); }
    }

    private static IReadOnlyList<string> ProcessPaths()
    {
        var paths = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            try { if (process.MainModule?.FileName is { } path) paths.Add(path); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // A protected system process, or one that has just ended. Neither is a game.
            }
            finally { process.Dispose(); }
        }
        return paths;
    }
}
