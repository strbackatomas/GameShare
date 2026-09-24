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

    /// <summary>A game, or a package of redistributables other games ask for through <see cref="GameSetup.Requires"/>.</summary>
    public GameKind Kind { get; init; } = GameKind.Game;

    /// <summary>
    /// The program that starts the game, relative to the game root. It has to be a .exe that is one of the game's own files.
    /// This comes from another PC together with the manifest, so anything else is refused when the game is started.
    /// Short for a single entry in <see cref="Launch"/>, and ignored when <see cref="Launch"/> has entries.
    /// </summary>
    public string? Executable { get; init; }
    public string? Arguments { get; init; }
    public string WorkingDirectory { get; init; } = ".";

    /// <summary>
    /// The programs a player can start, the first one being the game itself: a game with an editor, a dedicated server or
    /// a launcher with options has several. Each one is checked like <see cref="Executable"/>.
    /// </summary>
    public IReadOnlyList<LaunchEntry> Launch { get; init; } = [];

    /// <summary>A file of the game to show as its picture: .ico, .png or .exe. Without it, the icon of the first program is used.</summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Path patterns, relative to the game folder, of files the game rewrites while it is played: saves, settings, caches.
    /// They are left out of the manifest and the torrent, so they are never shared, verified or overwritten. Example: "saves/**", "*.ini".
    /// </summary>
    public IReadOnlyList<string> Volatile { get; init; } = [];

    /// <summary>What has to happen on a PC once before the game is first started: redistributables, registry, compatibility, profile.</summary>
    public GameSetup? Setup { get; init; }

    /// <summary>For a <see cref="GameKind.Redist"/> package: the redistributables it holds, by the id games ask for, such as "directx9".</summary>
    public IReadOnlyDictionary<string, RedistPackage> Provides { get; init; } = new Dictionary<string, RedistPackage>();

    /// <summary>
    /// The programs to offer, first the game itself. <see cref="Launch"/> when it has entries, else the single
    /// <see cref="Executable"/>, else none.
    /// </summary>
    public IReadOnlyList<LaunchEntry> LaunchEntries() =>
        Launch.Count > 0 ? Launch
        : string.IsNullOrWhiteSpace(Executable) ? []
        : [new LaunchEntry { Executable = Executable, Arguments = Arguments, WorkingDirectory = WorkingDirectory }];
}

public enum GameKind
{
    Game,
    /// <summary>Redistributables (DirectX, Visual C++, .NET) shared like a game. Not played, only installed from by other games' setup.</summary>
    Redist,
}

/// <summary>One program of a game a player can start.</summary>
public sealed record LaunchEntry
{
    /// <summary>What the player sees, for example "Editor". Null for the first entry means the plain play button.</summary>
    public string? Name { get; init; }
    public required string Executable { get; init; }
    public string? Arguments { get; init; }
    public string WorkingDirectory { get; init; } = ".";

    /// <summary>
    /// Started with administrator rights, so the player gets a UAC prompt each time. For old games that write to
    /// HKEY_LOCAL_MACHINE or other protected places while they run and fail when Windows redirects that.
    /// </summary>
    public bool RunAsAdmin { get; init; }
}

/// <summary>
/// One-time preparation of a PC for a game, what the old LAN party installer did after copying the files.
/// Every file named here is a file of the game, checked against the manifest before it is used.
/// </summary>
public sealed record GameSetup
{
    /// <summary>Ids of redistributables from a <see cref="GameKind.Redist"/> package, such as "directx9". Each is skipped on a PC that has it.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>Installers shipped with the game itself, run silently.</summary>
    public IReadOnlyList<RedistStep> Redist { get; init; } = [];

    public IReadOnlyList<RegistryStep> Registry { get; init; } = [];

    public IReadOnlyList<CompatibilityStep> Compatibility { get; init; } = [];

    public IReadOnlyList<ProfileStep> Profile { get; init; } = [];

    public bool IsEmpty => Requires.Count == 0 && Redist.Count == 0 && Registry.Count == 0 && Compatibility.Count == 0 && Profile.Count == 0;
}

/// <summary>An installer file of the game and the arguments that make it silent.</summary>
public sealed record RedistStep
{
    public required string File { get; init; }
    public string? Args { get; init; }
}

/// <summary>A .reg file of the game, imported with administrator rights.</summary>
public sealed record RegistryStep
{
    public required string File { get; init; }

    /// <summary>A key deleted with everything below it before the import, for a clean install. HKLM\… or HKCU\….</summary>
    public string? Cleanup { get; init; }

    /// <summary>
    /// The folder the .reg file was exported from, such as C:\Games\Battlefield 2. Every occurrence is replaced with the
    /// folder the game is installed in on this PC before importing.
    /// </summary>
    public string? OriginalPath { get; init; }
}

/// <summary>A Windows compatibility mode for a program of the game, for the player who starts it.</summary>
public sealed record CompatibilityStep
{
    public required string Executable { get; init; }

    /// <summary>Windows compatibility layers, for example "WINXPSP3" or "WIN98 256COLOR".</summary>
    public required string Layers { get; init; }
}

/// <summary>A folder of the game copied into the player's profile, for games that keep their settings in Documents.</summary>
public sealed record ProfileStep
{
    public required string From { get; init; }

    /// <summary>The target, starting with {Documents}, {AppData} or {LocalAppData}. An existing folder is left alone, it may hold saves.</summary>
    public required string To { get; init; }
}

/// <summary>One redistributable in a <see cref="GameKind.Redist"/> package.</summary>
public sealed record RedistPackage
{
    /// <summary>What the player sees, for example "DirectX 9.0c (June 2010)".</summary>
    public string? Name { get; init; }
    public required string File { get; init; }
    public string? Args { get; init; }

    /// <summary>How to tell it is on the PC already. Without it, it is installed every time a game asking for it is prepared.</summary>
    public InstalledCheck? InstalledIf { get; init; }
}

/// <summary>Every condition that is set has to hold. With none set, nothing counts as installed.</summary>
public sealed record InstalledCheck
{
    /// <summary>A file that exists, such as {SysWOW64}\d3dx9_43.dll.</summary>
    public string? File { get; init; }

    /// <summary>A registry key that exists, HKLM\… or HKCU\…, optionally with <see cref="RegistryValue"/>.</summary>
    public string? RegistryKey { get; init; }
    public string? RegistryValue { get; init; }

    /// <summary>The name of an installed program as in Apps and Features, * as a wildcard: "Microsoft Visual C++ 2005 Redistributable*".</summary>
    public string? Uninstall { get; init; }
}
