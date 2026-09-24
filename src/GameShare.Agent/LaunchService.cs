using System.Collections.Concurrent;
using GameShare.Core;
using GameShare.Core.Data;
using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.Agent;

/// <summary>
/// Decides whether a game may be started and what exactly is started. The agent is a service without a desktop, so it cannot start a game
/// the player can see. The client does that, and takes the answer of this class as it stands: a program that is part of the game and is
/// still what it was when the game was verified, and never a version the administrator withdrew.
/// </summary>
public sealed class LaunchService
{
    private const string KeyPrefix = "launcher.";

    private readonly GameShareDb _db;
    private readonly TrustService _trust;
    private readonly RunningGames _running;
    private readonly ConcurrentDictionary<string, LauncherChoice?> _choices = new(StringComparer.Ordinal);

    private readonly SeedManager _seeds;

    public LaunchService(GameShareDb db, TrustService trust, RunningGames running, SeedManager seeds)
    {
        _db = db;
        _trust = trust;
        _running = running;
        _seeds = seeds;
    }

    /// <summary>Whether a game can be started, needs the player to pick a program first, or has nothing to start.</summary>
    public async Task<LaunchState> StateAsync(GameManifest manifest, Installation? installation, CancellationToken ct = default)
    {
        if (installation is null) return LaunchState.None;
        var plan = LaunchRules.Plan(manifest, await ChoiceAsync(manifest.GameId, ct).ConfigureAwait(false), out _);
        if (plan is not null) return LaunchState.Ready;
        return LaunchRules.Candidates(manifest).Count > 0 ? LaunchState.NeedsExecutable : LaunchState.None;
    }

    /// <summary>The program the play button starts, relative to the game folder, or null when there is none yet.</summary>
    public async Task<string?> MainExecutableAsync(GameManifest manifest, CancellationToken ct = default) =>
        LaunchRules.Plan(manifest, await ChoiceAsync(manifest.GameId, ct).ConfigureAwait(false), 0, out _)?.Executable;

    /// <summary>The programs of the game that pass the checks, the game itself first. Empty unless the game can be started.</summary>
    public async Task<IReadOnlyList<LaunchOptionDto>> OptionsAsync(GameManifest manifest, Installation? installation, CancellationToken ct = default)
    {
        if (installation is null) return [];
        var choice = await ChoiceAsync(manifest.GameId, ct).ConfigureAwait(false);
        return LaunchRules.Entries(manifest, choice)
            .Select(e => new LaunchOptionDto(e.Index, e.Plan.Name, e.Plan.Executable, e.Plan.RunAsAdmin))
            .ToList();
    }

    /// <summary>Checks everything and says what to start.</summary>
    /// <param name="entry">Which of the programs the definition lists, 0 being the game itself.</param>
    /// <exception cref="InvalidOperationException">The game must not be started now. The message says why and what to do.</exception>
    public async Task<LaunchInfoDto> PrepareAsync(string contentHash, int entry = 0, CancellationToken ct = default)
    {
        var (installation, manifest) = await InstalledAsync(contentHash, ct).ConfigureAwait(false);

        var (verdict, note) = _trust.Check(contentHash);
        if (verdict == TrustVerdict.Revoked)
            throw new InvalidOperationException($"{manifest.Name} was withdrawn by the administrator: {note}. It will not be started.");
        if (await _running.IsRunningNowAsync(installation, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"{manifest.Name} is already running.");

        var plan = LaunchRules.Plan(manifest, await ChoiceAsync(manifest.GameId, ct).ConfigureAwait(false), entry, out var problem)
            ?? throw new InvalidOperationException(problem is null
                ? entry == 0 ? $"{manifest.Name} does not say which program starts it. Choose one first." : $"{manifest.Name} has no program number {entry}."
                : $"{manifest.Name} cannot be started: {problem} Choose the program to start.");

        var root = Path.GetFullPath(installation.InstallPath);
        var executable = Inside(root, plan.Executable);
        var file = manifest.Files.First(f => f.Path == plan.Executable);
        if (!File.Exists(executable))
            throw new InvalidOperationException($"The program {plan.Executable} is missing from the game folder. Repair the game.");

        // Anything else of the game may have changed, that is what playing does. The program that is about to run has to be what was verified.
        if (!string.Equals(await ManifestVerifier.HashFileAsync(executable, ct).ConfigureAwait(false), file.Hash, StringComparison.Ordinal))
            throw new InvalidOperationException($"The program {plan.Executable} is not what it was when the game was verified, so it is not started. Repair the game first.");

        var workingDirectory = Inside(root, plan.WorkingDirectory == "." ? "" : plan.WorkingDirectory);
        if (!Directory.Exists(workingDirectory)) workingDirectory = root;

        // The client starts the game as soon as this answers. The seed has to let go of the files before that, not when the next look
        // notices the game a few seconds later: a game that writes its settings on start (UT2004.ini) fails and quits when it cannot.
        await _running.ExpectLaunchAsync(installation, ct).ConfigureAwait(false);
        await _seeds.SuspendAsync(installation, ct).ConfigureAwait(false);
        return new LaunchInfoDto(executable, plan.Arguments, workingDirectory, plan.RunAsAdmin);
    }

    public async Task<IReadOnlyList<string>> CandidatesAsync(string contentHash, CancellationToken ct = default) =>
        LaunchRules.Candidates((await InstalledAsync(contentHash, ct).ConfigureAwait(false)).Manifest);

    /// <summary>Remembers, for this PC and this game, which program to start. Kept across versions of the game as long as it stays valid.</summary>
    /// <exception cref="ArgumentException">The program is not one that may be started.</exception>
    public async Task ChooseAsync(string contentHash, string executable, string? arguments, CancellationToken ct = default)
    {
        var (_, manifest) = await InstalledAsync(contentHash, ct).ConfigureAwait(false);
        var choice = new LauncherChoice(executable, string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim());
        if (LaunchRules.Plan(manifest, choice, out var problem) is null) throw new ArgumentException(problem ?? "Choose a program.");

        await _db.SetSettingAsync(KeyPrefix + manifest.GameId, GameShareJson.Serialize(choice), ct).ConfigureAwait(false);
        _choices[manifest.GameId] = choice;
    }

    private async Task<LauncherChoice?> ChoiceAsync(string gameId, CancellationToken ct)
    {
        if (_choices.TryGetValue(gameId, out var known)) return known;
        var json = await _db.GetSettingAsync(KeyPrefix + gameId, ct).ConfigureAwait(false);
        LauncherChoice? choice = null;
        if (json is not null)
        {
            try { choice = GameShareJson.Deserialize<LauncherChoice>(json); }
            catch (System.Text.Json.JsonException) { /* an unreadable choice is no choice */ }
        }
        return _choices[gameId] = choice;
    }

    private async Task<(Installation Installation, GameManifest Manifest)> InstalledAsync(string contentHash, CancellationToken ct)
    {
        var installation = (await _db.ListInstallationsAsync(ct).ConfigureAwait(false)).FirstOrDefault(i => i.ContentHash == contentHash)
            ?? throw new KeyNotFoundException($"Game {contentHash} is not installed on this PC.");
        var stored = await _db.GetManifestAsync(contentHash, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No manifest is stored for {contentHash}.");
        return (installation, stored.Manifest);
    }

    /// <summary>The full path of <paramref name="relative"/> below <paramref name="root"/>, and never anywhere else.</summary>
    private static string Inside(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var folder = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full != root && !full.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{relative}' is outside the game folder.");
        return full;
    }
}
