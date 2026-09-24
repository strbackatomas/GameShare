using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GameShare.Protocol;

namespace GameShare.Storage;

/// <summary>Tells whether a redistributable is on this PC already, by the checks its package lists.</summary>
public interface ISetupProbe
{
    bool IsInstalled(InstalledCheck check);
}

/// <summary>A redistributables package installed on this PC, the kind other games' setup draws from.</summary>
public sealed record InstalledPackage(GameManifest Manifest, string InstallPath);

/// <param name="SetupHash">Identifies this preparation, see <see cref="SetupPlanner.SetupHash"/>.</param>
/// <param name="Problems">What is wrong with the definition, each a reason not to run any of it.</param>
/// <param name="MissingRedists">Ids the game requires that no installed package provides.</param>
/// <param name="FilesToVerify">Installers to hash before the plan is shown: they must still be what the manifest says.</param>
public sealed record SetupPlan(
    string SetupHash, IReadOnlyList<SetupStepDto> Steps, IReadOnlyList<string> Problems, IReadOnlyList<string> MissingRedists,
    IReadOnlyList<(string Path, string Hash)> FilesToVerify);

/// <summary>
/// Turns a game's setup section into the steps to run on this PC. The definition comes from another PC, so everything it names is
/// checked: files must be files of the game, registry keys must be a game's own, and targets must stay in the player's profile.
/// </summary>
public static partial class SetupPlanner
{
    /// <summary>Tokens a profile target may start with. The client resolves them for the player who runs the preparation.</summary>
    public static readonly IReadOnlyList<string> ProfileTokens = ["{Documents}", "{AppData}", "{LocalAppData}"];

    [GeneratedRegex(@"^[A-Za-z0-9_~ ]{1,100}$")]
    private static partial Regex LayersPattern();

    /// <summary>
    /// What was prepared: the setup section and the folder, since the registry points at the folder. A new version whose setup is
    /// the same needs no preparing again, a game moved to another folder does.
    /// </summary>
    public static string SetupHash(GameDefinition definition, string installPath) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            GameShareJson.Serialize(definition.Setup) + "\n" + Path.GetFullPath(installPath).TrimEnd('\\').ToLowerInvariant())));

    public static bool HasSetup(GameManifest manifest) => manifest.Definition?.Setup is { IsEmpty: false } && manifest.Definition.Kind == GameKind.Game;

    public static async Task<SetupPlan> PlanAsync(
        GameManifest game, string installPath, IReadOnlyList<InstalledPackage> packages, ISetupProbe probe, CancellationToken ct = default)
    {
        var definition = game.Definition ?? throw new ArgumentException("The game has no definition.", nameof(game));
        var setup = definition.Setup ?? new GameSetup();
        var root = Path.GetFullPath(installPath);
        var steps = new List<SetupStepDto>();
        var problems = new List<string>();
        var missing = new List<string>();
        var verify = new List<(string, string)>();

        foreach (var id in setup.Requires.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = packages.Select(p => (p, entry: p.Manifest.Definition?.Provides.GetValueOrDefault(id))).FirstOrDefault(x => x.entry is not null);
            if (found.entry is not { } entry) { missing.Add(id); continue; }
            var file = LaunchRules.GameFile(found.p.Manifest, entry.File, out var problem);
            if (file is null) { problems.Add($"Balíček knihoven: {problem}"); continue; }
            var done = entry.InstalledIf is { } check && probe.IsInstalled(check);
            var full = Inside(found.p.InstallPath, file.Path);
            steps.Add(new SetupStepDto(SetupStepKind.Redist, $"Nainstalovat {entry.Name ?? id}", NeedsAdmin: true)
            {
                File = full, FileHash = file.Hash, Arguments = entry.Args, AlreadyDone = done,
                Details = [done ? "Na tomto PC už je." : $"{file.Path} {entry.Args}".Trim()],
            });
            if (!done) verify.Add((full, file.Hash));
        }

        foreach (var redist in setup.Redist)
        {
            var file = LaunchRules.GameFile(game, redist.File, out var problem);
            if (file is null) { problems.Add(problem!); continue; }
            if (!IsInstaller(file.Path)) { problems.Add($"'{redist.File}' is not an installer (.exe or .msi)."); continue; }
            var full = Inside(root, file.Path);
            steps.Add(new SetupStepDto(SetupStepKind.Redist, $"Nainstalovat {Path.GetFileName(file.Path)}", NeedsAdmin: true)
            {
                File = full, FileHash = file.Hash, Arguments = redist.Args, Details = [$"{file.Path} {redist.Args}".Trim()],
            });
            verify.Add((full, file.Hash));
        }

        foreach (var registry in setup.Registry)
        {
            if (registry.Cleanup is { } cleanup)
            {
                if (CleanupKey(cleanup, out var key, out var problem) is not { } isMachine) problems.Add(problem!);
                else steps.Add(new SetupStepDto(SetupStepKind.RegistryDelete, $"Smazat klíč registru {key}", NeedsAdmin: isMachine)
                {
                    Target = key, Details = ["Pro čistou instalaci, se vším, co je pod ním."],
                });
            }

            var file = LaunchRules.GameFile(game, registry.File, out var fileProblem);
            if (file is null) { problems.Add(fileProblem!); continue; }
            var bytes = await File.ReadAllBytesAsync(Inside(root, file.Path), ct).ConfigureAwait(false);
            // Hashed as read, so what is imported is exactly what was verified.
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Hash)
            {
                problems.Add($"{file.Path} is not what it was when the game was verified. Repair the game.");
                continue;
            }
            var text = RegFile.Decode(bytes);
            var moved = registry.OriginalPath is { Length: > 0 } original && !string.Equals(original.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (moved) text = RegFile.ReplacePath(text, registry.OriginalPath!, root);
            var (machine, user) = RegFile.Split(text);
            var refused = machine.Keys.Concat(user.Keys).Where(k => !IsGameKey(k.TrimStart('-'), deleting: k.StartsWith('-'))).ToList();
            if (refused.Count > 0)
            {
                problems.Add($"{file.Path} writes to registry keys that are not a game's own: {string.Join(", ", refused.Take(3))}. " +
                    "Only keys below HKLM\\SOFTWARE or HKCU\\Software are imported, and not Windows' own.");
                continue;
            }
            IReadOnlyList<string> pathNote = moved ? [$"Cesty {registry.OriginalPath} se přepíšou na {root}."] : [];

            if (machine.Text is not null)
                steps.Add(new SetupStepDto(SetupStepKind.RegistryImport, $"Zapsat {file.Path} do registru počítače ({Values(machine.ValueCount)})", NeedsAdmin: true)
                {
                    Content = machine.Text, Details = [.. pathNote, .. machine.Preview],
                });
            if (user.Text is not null)
                steps.Add(new SetupStepDto(SetupStepKind.RegistryImport, $"Zapsat {file.Path} do registru hráče ({Values(user.ValueCount)})", NeedsAdmin: false)
                {
                    Content = user.Text, Details = [.. pathNote, .. user.Preview],
                });
        }

        foreach (var compat in setup.Compatibility)
        {
            var file = LaunchRules.GameFile(game, compat.Executable, out var problem);
            if (file is null) { problems.Add(problem!); continue; }
            var layers = compat.Layers.Trim().TrimStart('~').Trim();
            if (!file.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !LayersPattern().IsMatch(layers))
            {
                problems.Add($"The compatibility mode '{compat.Layers}' for '{compat.Executable}' is not valid.");
                continue;
            }
            steps.Add(new SetupStepDto(SetupStepKind.Compatibility, $"Režim kompatibility {layers} pro {Path.GetFileName(file.Path)}", NeedsAdmin: false)
            {
                File = Inside(root, file.Path), Arguments = layers, Details = ["Jen pro tohoto hráče, jako ve vlastnostech programu → Kompatibilita."],
            });
        }

        foreach (var profile in setup.Profile)
        {
            var from = profile.From.Trim().Replace('\\', '/').Trim('/');
            var files = game.Files.Count(f => f.Path.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase));
            if (from.Length == 0 || !ManifestValidator.IsSafeRelativePath(from) || files == 0)
            {
                problems.Add($"'{profile.From}' is not a folder of this game.");
                continue;
            }
            if (!IsProfileTarget(profile.To))
            {
                problems.Add($"'{profile.To}' has to be a folder in the player's profile, starting with {string.Join(", ", ProfileTokens)}.");
                continue;
            }
            steps.Add(new SetupStepDto(SetupStepKind.Profile, $"Zkopírovat profil do {profile.To}", NeedsAdmin: false)
            {
                File = Inside(root, from), Target = profile.To,
                Details = [$"{from} ({files} souborů). Když cílová složka už existuje, nechá se být, může v ní mít uložené hry."],
            });
        }

        return new SetupPlan(SetupHash(definition, root), steps, problems, missing, verify);
    }

    /// <summary>{Documents}\Battlefield 2: a token and a plain relative path below it.</summary>
    public static bool IsProfileTarget(string target)
    {
        var token = ProfileTokens.FirstOrDefault(t => target.StartsWith(t, StringComparison.OrdinalIgnoreCase));
        if (token is null) return false;
        var rest = target[token.Length..].Replace('\\', '/').Trim('/');
        return rest.Length > 0 && ManifestValidator.IsSafeRelativePath(rest);
    }

    /// <summary>
    /// A key a game may have deleted: HKLM or HKCU, under SOFTWARE, and at least a vendor and a product deep, so a definition
    /// can clear "SOFTWARE\WOW6432Node\EA Games\Battlefield 2" but never "SOFTWARE" or "SOFTWARE\Microsoft".
    /// </summary>
    /// <returns>True for a machine key, false for a player's key, null when refused.</returns>
    public static bool? CleanupKey(string raw, out string key, out string? problem)
    {
        key = raw.Trim().Replace("Registry::", "", StringComparison.OrdinalIgnoreCase).Replace('/', '\\').Trim('\\');
        foreach (var (longName, shortName) in new[] { ("HKEY_LOCAL_MACHINE", "HKLM"), ("HKEY_CURRENT_USER", "HKCU"), ("HKLM:", "HKLM"), ("HKCU:", "HKCU") })
            if (key.StartsWith(longName, StringComparison.OrdinalIgnoreCase)) key = shortName + key[longName.Length..];
        key = string.Join('\\', key.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        problem = null;
        if (IsGameKey(key, deleting: true)) return key.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase);
        problem = $"The registry key '{raw}' is not one a game may delete. Only a game's own key below HKLM\\SOFTWARE or HKCU\\SOFTWARE is.";
        return null;
    }

    /// <summary>Vendors whose keys below SOFTWARE belong to Windows, not to a game: autostart, file associations, policies.</summary>
    private static readonly HashSet<string> WindowsKeys = new(StringComparer.OrdinalIgnoreCase) { "Microsoft", "Classes", "Policies", "RegisteredApplications", "Clients" };

    /// <summary>
    /// A game's own key, HKLM\SOFTWARE\Vendor\… or HKCU\Software\Vendor\… (WOW6432Node allowed), never one of Windows' own.
    /// Deleting needs a product below the vendor too, writing may create the vendor key itself.
    /// </summary>
    public static bool IsGameKey(string key, bool deleting)
    {
        var parts = key.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Any(p => p is "." or "..")) return false;
        if (!parts[0].Equals("HKLM", StringComparison.OrdinalIgnoreCase) && !parts[0].Equals("HKCU", StringComparison.OrdinalIgnoreCase)) return false;
        if (!parts[1].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase)) return false;
        var below = parts.Skip(2).Where(p => !p.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)).ToList();
        return below.Count >= (deleting ? 2 : 1) && !WindowsKeys.Contains(below[0]);
    }

    private static bool IsInstaller(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);

    private static string Values(int count) => count == 1 ? "1 hodnota" : count is >= 2 and <= 4 ? $"{count} hodnoty" : $"{count} hodnot";

    private static string Inside(string root, string relative) =>
        Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
}
