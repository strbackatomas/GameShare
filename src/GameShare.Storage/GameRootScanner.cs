namespace GameShare.Storage;

/// <summary>Finds candidate game directories below the configured game roots (D:\Games, E:\Games, ...).</summary>
public static class GameRootScanner
{
    /// <summary>
    /// Every immediate subdirectory of a root that contains at least one file. This is cheap on purpose:
    /// it does not hash anything. Identity comes from the manifest, so a directory is only a candidate.
    /// Roots that do not exist are skipped and reported through <paramref name="missingRoots"/>.
    /// </summary>
    public static IReadOnlyList<string> FindCandidateDirectories(IEnumerable<string> roots, ICollection<string>? missingRoots = null)
    {
        var result = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) { missingRoots?.Add(root); continue; }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || name.StartsWith('$')) continue;
                if (Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any())
                    result.Add(dir);
            }
        }
        return result;
    }
}
