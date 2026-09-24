using System.Collections.Concurrent;
using GameShare.Core;
using GameShare.Core.Data;
using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.Agent;

/// <summary>
/// The picture shown next to a game's name: the icon of its program, or the file its definition names. Taken from this PC's own copy
/// of the game, never from another PC, and cached in the data folder by content hash so each game's program is read once.
/// </summary>
public sealed class IconService
{
    public const string IcoType = "image/x-icon";
    public const string PngType = "image/png";
    private const long MaxImageFileBytes = 2 * 1024 * 1024;

    private readonly string _cacheDir;
    private readonly LaunchService _launch;
    private readonly ILogger<IconService> _log;
    private readonly ConcurrentDictionary<string, bool> _none = new(StringComparer.Ordinal);

    public IconService(AgentOptions options, LaunchService launch, GameLibrary library, ILogger<IconService> log)
    {
        _cacheDir = Path.Combine(options.ResolveDataDir(), "icons");
        _launch = launch;
        _log = log;
        library.DefinitionChanged += (_, installation) => Forget(installation.ContentHash);
    }

    /// <summary>Whether the game has a picture, reading it out of the game the first time.</summary>
    public async Task<bool> HasIconAsync(GameManifest manifest, Installation? installation, CancellationToken ct = default) =>
        await GetAsync(manifest, installation, ct).ConfigureAwait(false) is not null;

    /// <returns>The image and its media type, or null when the game is not installed here or has no icon.</returns>
    public async Task<(byte[] Bytes, string ContentType)?> GetAsync(GameManifest manifest, Installation? installation, CancellationToken ct = default)
    {
        if (installation is null || _none.ContainsKey(manifest.ContentHash)) return null;

        foreach (var (ext, type) in new[] { (".png", PngType), (".ico", IcoType) })
        {
            var cached = Path.Combine(_cacheDir, manifest.ContentHash + ext);
            if (File.Exists(cached)) return (await File.ReadAllBytesAsync(cached, ct).ConfigureAwait(false), type);
        }

        var found = await ReadFromGameAsync(manifest, installation, ct).ConfigureAwait(false);
        if (found is not { } icon)
        {
            _none[manifest.ContentHash] = true;
            return null;
        }
        try
        {
            Directory.CreateDirectory(_cacheDir);
            await File.WriteAllBytesAsync(Path.Combine(_cacheDir, manifest.ContentHash + (icon.ContentType == PngType ? ".png" : ".ico")), icon.Bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogDebug(ex, "Could not cache the icon of {Name}", manifest.Name); }
        return icon;
    }

    /// <summary>A definition may have changed the file to use, so what was cached for this version is dropped.</summary>
    public void Forget(string contentHash)
    {
        _none.TryRemove(contentHash, out _);
        foreach (var ext in new[] { ".png", ".ico" })
        {
            try { File.Delete(Path.Combine(_cacheDir, contentHash + ext)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* stays until the next attempt */ }
        }
    }

    private async Task<(byte[] Bytes, string ContentType)?> ReadFromGameAsync(GameManifest manifest, Installation installation, CancellationToken ct)
    {
        var root = Path.GetFullPath(installation.InstallPath);
        string? source = null;
        if (manifest.Definition?.Icon is { } named)
        {
            source = LaunchRules.GameFile(manifest, named, out var problem)?.Path;
            if (source is null) _log.LogDebug("Icon of {Name} not used: {Problem}", manifest.Name, problem);
        }
        source ??= await _launch.MainExecutableAsync(manifest, ct).ConfigureAwait(false);
        if (source is null) return null;

        var full = Path.GetFullPath(Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(full)) return null;

        switch (Path.GetExtension(full).ToLowerInvariant())
        {
            case ".exe":
                return await Task.Run(() => ExeIcon.Extract(full), ct).ConfigureAwait(false) is { } ico ? (ico, IcoType) : null;
            case ".ico" or ".png" when new FileInfo(full).Length <= MaxImageFileBytes:
                var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
                var isPng = bytes is [0x89, 0x50, 0x4E, 0x47, ..];
                var isIco = bytes is [0, 0, 1, 0, ..];
                return isPng ? (bytes, PngType) : isIco ? (bytes, IcoType) : null;
            default:
                return null;
        }
    }
}
