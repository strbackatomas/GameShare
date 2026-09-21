using System.Text.RegularExpressions;
using GameShare.Protocol;

namespace GameShare.Storage;

/// <summary>
/// Validates a manifest that arrived from another PC before anything touches the disk.
/// The LAN is trusted, but a corrupt or hostile manifest must never write outside the game folder.
/// </summary>
public static partial class ManifestValidator
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Sha1Hex();

    /// <returns>Empty when the manifest is acceptable, otherwise one message per problem.</returns>
    public static IReadOnlyList<string> Validate(GameManifest m)
    {
        var errors = new List<string>();

        if (m.SchemaVersion != GameManifest.CurrentSchemaVersion)
            errors.Add($"Unsupported manifest schema version {m.SchemaVersion}, this build understands {GameManifest.CurrentSchemaVersion}.");
        if (!GameDefinitionFile.IsValidGameId(m.GameId))
            errors.Add($"Invalid GameId '{m.GameId}'.");
        if (!IsSafeSegment(m.FolderName))
            errors.Add($"Unsafe FolderName '{m.FolderName}'.");
        if (m.Files.Count == 0)
            errors.Add("Manifest has no files.");
        if (m.PieceLength < 16 * 1024 || (m.PieceLength & (m.PieceLength - 1)) != 0)
            errors.Add($"Invalid PieceLength {m.PieceLength}.");
        if (!Sha256Hex().IsMatch(m.ContentHash))
            errors.Add("ContentHash is not a SHA-256 hex string.");
        if (m.TorrentInfoHash is not null && !Sha1Hex().IsMatch(m.TorrentInfoHash))
            errors.Add("TorrentInfoHash is not a SHA-1 hex string.");

        if (m.VolatilePatterns.Count > VolatileRules.MaxPatterns)
            errors.Add($"Too many volatile patterns ({m.VolatilePatterns.Count}).");
        else
            foreach (var pattern in m.VolatilePatterns)
                if (!PathPattern.TryCreate(pattern, out _, out var patternError)) errors.Add($"Invalid volatile pattern '{pattern}': {patternError}.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Windows paths are case-insensitive
        long sum = 0;
        foreach (var f in m.Files)
        {
            if (!IsSafeRelativePath(f.Path)) errors.Add($"Unsafe file path '{f.Path}'.");
            else if (!seen.Add(f.Path)) errors.Add($"Duplicate file path '{f.Path}'.");
            if (f.Size < 0) errors.Add($"Negative size for '{f.Path}'.");
            if (!Sha256Hex().IsMatch(f.Hash)) errors.Add($"Bad hash for '{f.Path}'.");
            sum += f.Size;
        }

        if (sum != m.TotalSize)
            errors.Add($"TotalSize {m.TotalSize} does not match the sum of file sizes {sum}.");
        if (errors.Count == 0 && ContentHasher.Compute(m.Files) != m.ContentHash)
            errors.Add("ContentHash does not match the file list.");

        return errors;
    }

    public static bool IsSafeRelativePath(string path) =>
        path.Length > 0 && path.Split('/').All(IsSafeSegment);

    private static bool IsSafeSegment(string s) =>
        s.Length > 0
        && s is not "." and not ".."
        && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0   // also rejects '\\', ':', '/' and control chars
        && !s.EndsWith(' ') && !s.EndsWith('.');
}
