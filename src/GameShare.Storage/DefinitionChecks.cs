using GameShare.Protocol;

namespace GameShare.Storage;

/// <summary>
/// Checks a definition against a game folder the way the agents will, so the administrator hears about a wrong path or a refused
/// registry key while editing, not from a player whose preparation will not run. Also what the editor offers to choose from.
/// </summary>
public static class DefinitionChecks
{
    /// <summary>The files of the folder, relative with forward slashes, the way a manifest lists them.</summary>
    public static IReadOnlyList<string> Files(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Where(f => !string.Equals(f, ContentScanner.DefinitionFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Programs, those nearest the game folder first: what a launch entry or compatibility mode can name.</summary>
    public static IReadOnlyList<string> Programs(IEnumerable<string> files) => Pick(files, ".exe");

    public static IReadOnlyList<string> RegFiles(IEnumerable<string> files) => Pick(files, ".reg");

    public static IReadOnlyList<string> Installers(IEnumerable<string> files) =>
        files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.StartsWith("_redist/", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Folders directly in the game folder, what a profile step can copy.</summary>
    public static IReadOnlyList<string> TopFolders(string folder) =>
        Directory.EnumerateDirectories(folder).Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>What the redistributables packages next to the game offer (a sibling folder whose definition is a redist package), by id.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> KnownRedistsAsync(string gameFolder, CancellationToken ct = default)
    {
        var known = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parent = Path.GetDirectoryName(Path.GetFullPath(gameFolder).TrimEnd('\\'));
        if (parent is null) return known;
        foreach (var sibling in Directory.EnumerateDirectories(parent))
        {
            GameDefinition? definition;
            try { definition = await GameDefinitionFile.TryLoadAsync(sibling, ct).ConfigureAwait(false); }
            catch (InvalidDataException) { continue; }
            if (definition is not { Kind: GameKind.Redist }) continue;
            foreach (var (id, package) in definition.Provides) known.TryAdd(id, package.Name ?? id);
        }
        return known;
    }

    /// <summary>Everything an agent would refuse or not find, in words for the administrator. Empty when it is fine.</summary>
    public static async Task<IReadOnlyList<string>> ValidateAsync(GameDefinition definition, string folder, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var files = Files(folder);
        var manifest = new GameManifest
        {
            GameId = definition.GameId, Name = definition.Name, FolderName = Path.GetFileName(folder), TotalSize = 0, ContentHash = new string('0', 64),
            PieceLength = 1, Files = files.Select(f => new ManifestFile(f, 0, "")).ToList(), Definition = definition,
        };

        if (!GameDefinitionFile.IsValidGameId(definition.GameId))
            problems.Add($"Id hry '{definition.GameId}' smí mít jen malá písmena, číslice, tečku, podtržítko a pomlčku.");
        if (string.IsNullOrWhiteSpace(definition.Name)) problems.Add("Hra nemá název.");

        var entries = definition.LaunchEntries();
        for (int i = 0; i < entries.Count; i++)
            if (LaunchRules.Plan(manifest, null, i, out var problem) is null)
                problems.Add($"Spouštěč {i + 1} ({entries[i].Name ?? entries[i].Executable}): {problem ?? "nemá program."}");

        var setup = definition.Setup ?? new GameSetup();
        foreach (var r in setup.Redist)
            if (LaunchRules.GameFile(manifest, r.File, out var problem) is null) problems.Add($"Instalátor: {problem}");

        foreach (var r in setup.Registry)
        {
            if (r.Cleanup is { Length: > 0 } cleanup && SetupPlanner.CleanupKey(cleanup, out _, out var keyProblem) is null) problems.Add(keyProblem!);
            if (LaunchRules.GameFile(manifest, r.File, out var problem) is not { } file) { problems.Add($"Registry: {problem}"); continue; }
            var text = RegFile.Decode(await File.ReadAllBytesAsync(Path.Combine(folder, file.Path), ct).ConfigureAwait(false));
            var (machine, user) = RegFile.Split(text);
            var refused = machine.Keys.Concat(user.Keys).Where(k => !SetupPlanner.IsGameKey(k.TrimStart('-'), k.StartsWith('-'))).ToList();
            if (refused.Count > 0) problems.Add($"{file.Path} zapisuje do klíčů, které nepatří hře: {string.Join(", ", refused.Take(3))}.");
            if (r.OriginalPath is { Length: > 0 } original && !text.Contains(original.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase)
                && !text.Contains(original, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{file.Path} cestu {original} vůbec neobsahuje, nic se v něm nepřepíše.");
        }

        foreach (var c in setup.Compatibility)
        {
            if (LaunchRules.GameFile(manifest, c.Executable, out var problem) is null) problems.Add($"Kompatibilita: {problem}");
            if (string.IsNullOrWhiteSpace(c.Layers)) problems.Add($"Kompatibilita pro {c.Executable} nemá zvolený režim.");
        }

        foreach (var p in setup.Profile)
        {
            if (!Directory.Exists(Path.Combine(folder, p.From))) problems.Add($"Profil: složka {p.From} ve hře není.");
            if (!SetupPlanner.IsProfileTarget(p.To)) problems.Add($"Profil: cíl {p.To} musí začínat {string.Join(", ", SetupPlanner.ProfileTokens)}.");
        }
        return problems;
    }

    private static IReadOnlyList<string> Pick(IEnumerable<string> files, string extension) =>
        files.Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
}
