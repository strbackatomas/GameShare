using System.Security.Cryptography;
using GameShare.Storage;

namespace GameShare.Torrent;

/// <summary>Result of building a torrent from a game directory.</summary>
/// <param name="TorrentBytes">The complete .torrent file. Internal detail, never shown to the user.</param>
/// <param name="InfoHash">Lowercase hex SHA-1 of the info dictionary (BitTorrent v1 identity).</param>
public sealed record BuiltTorrent(byte[] TorrentBytes, string InfoHash, long TotalSize, int PieceLength, int FileCount);

/// <summary>
/// Builds a BitTorrent v1 metainfo from a completed <see cref="ScanResult"/>.
/// The output is deterministic: identical directory contents produce an identical info hash on every PC,
/// because files are sorted ordinally and no timestamps, comments or creator strings are written.
/// </summary>
public static class TorrentBuilder
{
    public static BuiltTorrent Build(ScanResult scan)
    {
        // Info dictionary. Keys are already in byte order: files, name, piece length, pieces.
        using var info = new MemoryStream();
        Bencode.BeginDict(info);
        Bencode.WriteString(info, "files");
        Bencode.BeginList(info);
        foreach (var f in scan.Files)
        {
            Bencode.BeginDict(info);
            Bencode.WriteString(info, "length");
            Bencode.WriteInt(info, f.Size);
            Bencode.WriteString(info, "path");
            Bencode.BeginList(info);
            foreach (var part in f.Path.Split('/')) Bencode.WriteString(info, part);
            Bencode.End(info);
            Bencode.End(info);
        }
        Bencode.End(info);
        Bencode.WriteString(info, "name");
        Bencode.WriteString(info, scan.FolderName);
        Bencode.WriteString(info, "piece length");
        Bencode.WriteInt(info, scan.PieceLength);
        Bencode.WriteString(info, "pieces");
        Bencode.WriteBytes(info, scan.PieceHashes);
        Bencode.End(info);

        byte[] infoBytes = info.ToArray();
        string infoHash = Convert.ToHexString(SHA1.HashData(infoBytes)).ToLowerInvariant();

        using var outer = new MemoryStream(infoBytes.Length + 16);
        Bencode.BeginDict(outer);
        Bencode.WriteString(outer, "info");
        outer.Write(infoBytes);
        Bencode.End(outer);

        return new BuiltTorrent(outer.ToArray(), infoHash, scan.TotalSize, scan.PieceLength, scan.Files.Count);
    }

    /// <summary>Convenience for callers that only need the torrent and not the manifest.</summary>
    public static async Task<BuiltTorrent> BuildAsync(
        string directory,
        int? pieceLength = null,
        IProgress<long>? bytesHashed = null,
        CancellationToken cancellationToken = default) =>
        Build(await ContentScanner.ScanAsync(directory, pieceLength, bytesHashed, cancellationToken).ConfigureAwait(false));
}
