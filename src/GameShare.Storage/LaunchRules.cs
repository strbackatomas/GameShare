using GameShare.Protocol;

namespace GameShare.Storage;

/// <summary>What a player chose for a game on this PC, instead of what the game's definition says.</summary>
public sealed record LauncherChoice(string Executable, string? Arguments);

/// <summary>What to start. Paths are relative to the game folder and use forward slashes.</summary>
public sealed record LaunchPlan(string Executable, string? Arguments, string WorkingDirectory)
{
    /// <summary>The entry's name in the definition, null for the plain play button.</summary>
    public string? Name { get; init; }

    /// <summary>The definition asks for administrator rights. A player's own choice never does.</summary>
    public bool RunAsAdmin { get; init; }
}

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
    public static LaunchPlan? Plan(GameManifest manifest, LauncherChoice? choice, out string? problem) => Plan(manifest, choice, 0, out problem);

    /// <summary>
    /// The plan for one of the programs the definition lists, 0 being the game itself. The player's choice replaces only entry 0:
    /// it is how a player fixes a game whose definition names no program, or a wrong one.
    /// </summary>
    public static LaunchPlan? Plan(GameManifest manifest, LauncherChoice? choice, int entry, out string? problem)
    {
        problem = null;
        if (entry == 0 && choice is not null)
            return Check(manifest, new LaunchEntry { Executable = choice.Executable, Arguments = choice.Arguments }, runAsAdmin: false, out problem);

        var entries = manifest.Definition?.LaunchEntries() ?? [];
        if (entry < 0 || entry >= entries.Count) return null;
        var e = entries[entry];
        if (string.IsNullOrWhiteSpace(e.Executable)) return null;
        return Check(manifest, e, e.RunAsAdmin, out problem);
    }

    /// <summary>The programs of the definition that pass the checks, by their index, the game itself first.</summary>
    public static IReadOnlyList<(int Index, LaunchPlan Plan)> Entries(GameManifest manifest, LauncherChoice? choice)
    {
        var result = new List<(int, LaunchPlan)>();
        var count = Math.Max(1, manifest.Definition?.LaunchEntries().Count ?? 0);
        for (int i = 0; i < count; i++)
            if (Plan(manifest, choice, i, out _) is { } plan) result.Add((i, plan));
        return result;
    }

    private static LaunchPlan? Check(GameManifest manifest, LaunchEntry entry, bool runAsAdmin, out string? problem)
    {
        problem = null;
        var source = entry.Executable;
        var arguments = entry.Arguments;
        var workingDirectory = entry.WorkingDirectory;

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
        if (directory.Length == 0) directory = ".";
        if (workingDirectory is not null && IsRooted(workingDirectory) || directory != "." && !ManifestValidator.IsSafeRelativePath(directory))
            return Fail($"The working directory '{workingDirectory}' is not a folder inside the game folder.", out problem);

        return new LaunchPlan(listed.Path, string.IsNullOrWhiteSpace(arguments) ? null : arguments, directory)
        {
            Name = string.IsNullOrWhiteSpace(entry.Name) ? null : entry.Name.Trim(),
            RunAsAdmin = runAsAdmin,
        };
    }

    /// <summary>A file named by a definition (an installer, a .reg file, an icon): a file of the game, as the manifest spells it, or null.</summary>
    public static ManifestFile? GameFile(GameManifest manifest, string? path, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(path)) { problem = "No file is named."; return null; }
        var normalized = Normalize(path);
        if (IsRooted(path) || !ManifestValidator.IsSafeRelativePath(normalized))
        {
            problem = $"'{path}' is not a path inside the game folder.";
            return null;
        }
        var listed = manifest.Files.FirstOrDefault(f => string.Equals(f.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (listed is null) problem = $"'{path}' is not one of the files of this game.";
        return listed;
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
