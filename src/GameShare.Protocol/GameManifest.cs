using System.Security.Cryptography;
using System.Text;

namespace GameShare.Protocol;

/// <summary>One file of a game. <see cref="Hash"/> is lowercase hex SHA-256 of the file content.</summary>
/// <param name="Path">Relative to the game root, forward slashes.</param>
public sealed record ManifestFile(string Path, long Size, string Hash);

/// <summary>
/// Describes exactly which bytes make up one version of a game.
/// Identity is <see cref="ContentHash"/>, never the directory name.
/// </summary>
public sealed record GameManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Slug from the definition file, or derived from the folder name when there is none.</summary>
    public required string GameId { get; init; }

    /// <summary>Human readable name shown to the user.</summary>
    public required string Name { get; init; }
    public string? Version { get; init; }

    /// <summary>Folder the game is installed into, below a game root. Also the torrent name.</summary>
    public required string FolderName { get; init; }

    public required long TotalSize { get; init; }

    /// <summary>SHA-256 over the sorted file list. See <see cref="ContentHasher.Compute"/>.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Chunk size of the P2P transport. Independent of <see cref="ContentHash"/>.</summary>
    public required int PieceLength { get; init; }

    /// <summary>BitTorrent v1 info hash of the transport metadata for this exact content.</summary>
    public string? TorrentInfoHash { get; init; }

    public required IReadOnlyList<ManifestFile> Files { get; init; }

    public GameDefinition? Definition { get; init; }

    /// <summary>
    /// Patterns of files that were left out of <see cref="Files"/> because a game rewrites them. Recorded so that a PC which
    /// received the game applies the same rules when it looks at the folder again. Not part of the identity, only the resulting file list is.
    /// </summary>
    public IReadOnlyList<string> VolatilePatterns { get; init; } = [];
}

/// <summary>Computes the content identity of a game from its file list.</summary>
public static class ContentHasher
{
    /// <summary>
    /// SHA-256 over "path\nsize\nhash\n" for every file in ordinal path order.
    /// Independent of piece length, torrent metadata and definition file.
    /// </summary>
    public static string Compute(IEnumerable<ManifestFile> files)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            h.AppendData(Encoding.UTF8.GetBytes($"{f.Path}\n{f.Size}\n{f.Hash}\n"));
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }
}
