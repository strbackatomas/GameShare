using GameShare.Protocol;

namespace GameShare.Storage;

/// <summary>What a player chose for a game on this PC, instead of what the game's definition says.</summary>
public sealed record LauncherChoice(string Executable, string? Arguments);

/// <summary>What to start. Paths are relative to the game folder and use forward slashes.</summary>
public sealed record LaunchPlan(string Executable, string? Arguments, string WorkingDirectory);

/// <summary>
/// Which file of a game may be started. A game definition comes from another PC together with the manifest, so it is untrusted:
/// it must not be able to start anything but a program that is part of the game's own verified files.
/// </summary>
public static class LaunchRules
{
    public const int MaxArgumentsLength = 1024;

    /// <summary>The programs of a game a player can pick from, those nearest the game folder first.</summary>
    public static IReadOnlyList<string> Candidates(GameManifest manifest) =>
        manifest.Files.Select(f => f.Path)
            .Where(IsProgram)
            .OrderBy(p => p.Count(c => c == '/'))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The plan from the player's choice, else from the game's definition.
    /// Returns null with no problem when nothing is configured, and null with a problem when what is configured cannot be started.
    /// </summary>
    public static LaunchPlan? Plan(GameManifest manifest, LauncherChoice? choice, out string? problem)
    {
        problem = null;
        var source = choice is null ? manifest.Definition?.Executable : choice.Executable;
        if (string.IsNullOrWhiteSpace(source)) return null;

        var arguments = choice is null ? manifest.Definition?.Arguments : choice.Arguments;
        var workingDirectory = choice is null ? manifest.Definition?.WorkingDirectory : ".";

        var executable = Normalize(source);
        if (IsRooted(source) || !ManifestValidator.IsSafeRelativePath(executable))
            return Fail($"'{source}' is not a path inside the game folder.", out problem);
        if (!IsProgram(executable))
            return Fail($"'{source}' is not a program (.exe).", out problem);
        var listed = manifest.Files.FirstOrDefault(f => string.Equals(f.Path, executable, StringComparison.OrdinalIgnoreCase));
        if (listed is null)
            return Fail($"'{source}' is not one of the files of this game, so it is not started.", out problem);

        if (arguments is { Length: > MaxArgumentsLength } || arguments?.Any(char.IsControl) == true)
            return Fail($"The arguments are longer than {MaxArgumentsLength} characters or contain control characters.", out problem);

        var directory = string.IsNullOrWhiteSpace(workingDirectory) ? "." : Normalize(workingDirectory);
        if (workingDirectory is not null && IsRooted(workingDirectory) || directory != "." && !ManifestValidator.IsSafeRelativePath(directory))
            return Fail($"The working directory '{workingDirectory}' is not a folder inside the game folder.", out problem);

        return new LaunchPlan(listed.Path, string.IsNullOrWhiteSpace(arguments) ? null : arguments, directory);
    }

    /// <summary>A UNC path or "/etc" starts at a root. Trimming the slash would turn it into a folder inside the game, which is not what was written.</summary>
    private static bool IsRooted(string path) => path.TrimStart() is [Slash or Backslash, ..];

    private const char Slash = '/';
    private const char Backslash = (char)92;

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').Trim('/');

    private static bool IsProgram(string path) => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static LaunchPlan? Fail(string message, out string problem)
    {
        problem = message;
        return null;
    }
}
