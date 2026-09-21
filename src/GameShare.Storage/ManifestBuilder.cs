using GameShare.Protocol;

namespace GameShare.Storage;

public static class ManifestBuilder
{
    /// <summary>Turns a completed scan into a manifest. The torrent info hash is attached later by whoever builds the torrent.</summary>
    public static GameManifest Build(ScanResult scan, GameDefinition? definition = null, string? torrentInfoHash = null)
    {
        var files = scan.Files.Select(f => new ManifestFile(f.Path, f.Size, f.Sha256)).ToList();

        return new GameManifest
        {
            GameId = definition?.GameId ?? GameDefinitionFile.SlugFromFolderName(scan.FolderName),
            Name = definition?.DisplayName ?? definition?.Name ?? scan.FolderName,
            Version = definition?.Version,
            FolderName = scan.FolderName,
            TotalSize = scan.TotalSize,
            ContentHash = ContentHasher.Compute(files),
            PieceLength = scan.PieceLength,
            TorrentInfoHash = torrentInfoHash,
            Files = files,
            Definition = definition,
            VolatilePatterns = scan.VolatilePatterns,
        };
    }

    /// <summary>
    /// Scans a directory and builds its manifest, picking up <c>gameshare.json</c> if present.
    /// Files a game rewrites are left out, see <see cref="VolatileRules.Resolve"/>.
    /// </summary>
    /// <param name="alreadyRecorded">Patterns a previous manifest of this folder already excluded.</param>
    /// <param name="additional">Extra patterns to exclude from now on.</param>
    public static async Task<(GameManifest Manifest, ScanResult Scan)> ScanAndBuildAsync(
        string directory, IProgress<long>? bytesHashed = null, CancellationToken cancellationToken = default,
        IEnumerable<string>? alreadyRecorded = null, IEnumerable<string>? additional = null)
    {
        var definition = await GameDefinitionFile.TryLoadAsync(directory, cancellationToken).ConfigureAwait(false);
        var matcher = VolatileRules.Resolve(definition, alreadyRecorded, additional);
        var scan = await ContentScanner.ScanAsync(directory, bytesHashed: bytesHashed, cancellationToken: cancellationToken, volatileFiles: matcher).ConfigureAwait(false);
        return (Build(scan, definition), scan);
    }
}
