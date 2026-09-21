namespace GameShare.Protocol;

/// <summary>
/// How a game is presented and started. Says nothing about which bytes the game consists of
/// (that is <see cref="GameManifest"/>) or where it lives on this PC (that will be a local Installation record).
/// Optionally shipped as <c>gameshare.json</c> in the game root and excluded from the content hash,
/// so editing launch settings never changes game identity.
/// </summary>
public sealed record GameDefinition
{
    /// <summary>Stable lowercase slug such as "beamng". Groups versions of the same game.</summary>
    public required string GameId { get; init; }
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Version { get; init; }

    /// <summary>
    /// The program that starts the game, relative to the game root. It has to be a .exe that is one of the game's own files.
    /// This comes from another PC together with the manifest, so anything else is refused when the game is started.
    /// </summary>
    public string? Executable { get; init; }
    public string? Arguments { get; init; }
    public string WorkingDirectory { get; init; } = ".";
    public string? Icon { get; init; }

    /// <summary>
    /// Path patterns, relative to the game folder, of files the game rewrites while it is played: saves, settings, caches.
    /// They are left out of the manifest and the torrent, so they are never shared, verified or overwritten. Example: "saves/**", "*.ini".
    /// </summary>
    public IReadOnlyList<string> Volatile { get; init; } = [];
}
