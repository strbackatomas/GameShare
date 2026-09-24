namespace GameShare.Protocol;

// Contracts shared by the agent's HTTP APIs, its SignalR events and the client. Plain data, no behaviour.

public enum GameState
{
    /// <summary>Complete and verified on this PC.</summary>
    Installed,
    /// <summary>Installed here, but its files changed or went missing. Repair it or register it again. Its unchanged pieces are still offered to other PCs.</summary>
    Damaged,
    /// <summary>Being installed right now.</summary>
    Downloading,
    /// <summary>Not on this PC but at least one other PC offers it.</summary>
    AvailableOnLan,
    /// <summary>Known from an earlier scan or download but nobody offers it right now.</summary>
    Unavailable,
}

/// <summary>One game version. <see cref="ContentHash"/> is its identity and the id used in URLs.</summary>
public sealed record GameDto(
    string ContentHash,
    string GameId,
    string Name,
    string? Version,
    long TotalSize,
    GameState State,
    string? InstallPath,
    IReadOnlyList<string> PeerNames,
    long? DownloadId,
    GameDefinition? Definition)
{
    /// <summary>Whether the game can be started from the client. Only for a game that is installed here.</summary>
    public LaunchState Launch { get; init; } = LaunchState.None;

    /// <summary>A picture is served at /api/games/{contentHash}/icon. Only for a game installed here, it is read from this PC's copy.</summary>
    public bool HasIcon { get; init; }

    /// <summary>The game has setup steps that were not run on this PC for its current version and folder. Play prepares it first.</summary>
    public bool NeedsSetup { get; init; }

    /// <summary>The programs of the game that can be started, the game itself first. Empty when <see cref="Launch"/> is not Ready.</summary>
    public IReadOnlyList<LaunchOptionDto> LaunchOptions { get; init; } = [];

    /// <summary>A program of this game is running on this PC right now. Its files are not rewritten and its seed steps aside meanwhile.</summary>
    public bool IsRunning { get; init; }

    /// <summary>What the administrator's signed list says about this version. <see cref="TrustVerdict.NotChecked"/> when checking is off or there is no list.</summary>
    public TrustVerdict Trust { get; init; } = TrustVerdict.NotChecked;

    /// <summary>For a revoked version, the administrator's reason.</summary>
    public string? TrustNote { get; init; }

    /// <summary>What the signed list says about <see cref="Definition"/>.</summary>
    public DefinitionVerdict DefinitionTrust { get; init; } = DefinitionVerdict.NotChecked;

    /// <summary>Files of this game that were seen changing while it was in use, and new files it created. Only for a damaged game installed here.</summary>
    public int ChangedFileCount { get; init; }

    /// <summary>Volatile patterns that would make the files behind <see cref="ChangedFileCount"/> stop counting. Suggestions only.</summary>
    public IReadOnlyList<string> SuggestedPatterns { get; init; } = [];

    /// <summary>The PCs among <see cref="PeerNames"/> that have only part of the game, for example because it was played there.</summary>
    public IReadOnlyList<string> PartialPeerNames { get; init; } = [];

    /// <summary>
    /// False when no PC has the whole game and the PCs that are online do not have all of it between them.
    /// Installing then would stall, so clients should wait. True when unknown or not relevant.
    /// </summary>
    public bool FullyAvailable { get; init; } = true;

    /// <summary>Share of the game's data that the PCs online have between them, when no single PC has it all.</summary>
    public double? CoveragePercent { get; init; }

    /// <summary>
    /// Set on a version that is not installed here while another version of the same game is. It is the content hash of that
    /// installed version, so the client can offer "update" instead of "install".
    /// </summary>
    public string? UpdatesContentHash { get; init; }
}

/// <param name="AppVersion">The peer's GameShare version, for example "0.1.0". Null for a peer that predates this field.</param>
public sealed record PeerDto(string MachineId, string MachineName, string Address, int Port, int GameCount, DateTimeOffset LastSeen, string? AppVersion = null);

public sealed record DownloadPeerDto(string Name, string Address, long DownloadRate, long UploadRate, bool IsSeed);

/// <summary>A download, also used as the payload of every download event. Rates are bytes per second.</summary>
public sealed record DownloadDto(
    long Id,
    string ContentHash,
    string GameName,
    string State,
    long BytesDone,
    long BytesTotal,
    double Percent,
    long SpeedBytesPerSecond,
    int Peers,
    double? EtaSeconds,
    string? Error,
    IReadOnlyList<DownloadPeerDto> PeerDetails)
{
    /// <summary>Install, Repair or Update.</summary>
    public string Kind { get; init; } = "Install";

    /// <summary>How long it took, from start to completion. Null until it is Completed.</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>The highest download speed seen while it ran, in bytes per second. Null until something was measured.</summary>
    public long? PeakSpeedBytesPerSecond { get; init; }
}

public sealed record SeedDto(string ContentHash, string GameName, string InstallPath);

/// <summary>One PC currently pulling data from this one.</summary>
public sealed record UploadPeerDto(string Name, string Address, long UploadRate);

/// <summary>One game this PC is seeding, and who is pulling it right now. Only games with at least one active peer are listed.</summary>
public sealed record UploadDto(string ContentHash, string GameName, long TotalUploadRate, IReadOnlyList<UploadPeerDto> Peers);

public sealed record InstallRequest(string? TargetRoot);

public sealed record ScanResultDto(
    int Added, int Unchanged, int Skipped, IReadOnlyList<string> Errors, IReadOnlyList<string> MissingRoots, IReadOnlyList<string> Damaged);

/// <summary>What differs between an installed game and its manifest, from a full verification.</summary>
/// <param name="SuggestedPatterns">Volatile patterns that would make the changed and added files stop counting. Suggestions only.</param>
public sealed record GameChangesDto(
    bool IsIntact, IReadOnlyList<string> Modified, IReadOnlyList<string> Missing, IReadOnlyList<string> Added, IReadOnlyList<string> SuggestedPatterns);

/// <param name="Patterns">Path patterns such as "saves/**" or "*.ini", relative to the game folder.</param>
public sealed record AddVolatileRequest(IReadOnlyList<string> Patterns);

/// <param name="MaxUploadMBps">Megabytes (10^6 bytes) per second, null for unlimited.</param>
/// <param name="AllowedVirtualAdapterIds">
/// Ids of network adapters that look virtual (VirtualBox, Hyper-V, …) but the player re-enabled for discovery anyway. Null is the
/// same as empty, kept nullable only so old clients that do not send it are not treated as clearing the list.
/// </param>
/// <param name="TorrentDebugLogging">
/// Off by default: libtorrent's own Storage/PerformanceWarning/SessionLog/TorrentLog/PeerLog notifications are noisy, only useful
/// to chase a stalled or slow transfer. Read once when the transfer engine starts, so a change needs the agent restarted.
/// </param>
public sealed record SettingsDto(
    IReadOnlyList<string> GameRoots, bool SeedingEnabled, int? MaxUploadMBps, int? MaxDownloadMBps,
    IReadOnlyList<string>? AllowedVirtualAdapterIds = null, bool TorrentDebugLogging = false);

/// <summary>One configured game folder, with how much room is left on its drive.</summary>
/// <param name="FreeBytes">Null when it could not be read, for example a network share.</param>
public sealed record GameRootDto(string Path, long? FreeBytes);

/// <summary>One network adapter discovery could use, for the settings screen.</summary>
/// <param name="Ignored">True when this adapter is currently skipped by discovery: it looks virtual and was not re-enabled.</param>
public sealed record NetworkAdapterDto(string Id, string Name, string? Description, bool IsLikelyVirtual, bool Ignored);

public enum LaunchState
{
    /// <summary>Nothing to start: not installed here, or the game has no program to start.</summary>
    None,
    Ready,
    /// <summary>The game does not say which program starts it. The player picks one of its programs.</summary>
    NeedsExecutable,
}

/// <summary>What the client starts. Checked by the agent: a program of the game, still as it was verified, inside the game folder.</summary>
/// <param name="RunAsAdmin">The definition asks for administrator rights, so the client starts it through the UAC prompt.</param>
public sealed record LaunchInfoDto(string ExecutablePath, string? Arguments, string WorkingDirectory, bool RunAsAdmin = false);

/// <summary>One program of a game the player can start, as listed in its definition and checked by the agent.</summary>
/// <param name="Index">What to pass to the launch call. 0 is the game itself.</param>
/// <param name="Name">What the definition calls it, null for the game itself.</param>
public sealed record LaunchOptionDto(int Index, string? Name, string Executable, bool RunAsAdmin);

/// <summary>The player's pick of the program to start, for this PC.</summary>
public sealed record LauncherChoiceRequest(string Executable, string? Arguments = null);

public enum SetupStepKind
{
    /// <summary>A redistributable installer (DirectX, Visual C++, .NET) or one shipped with the game, run silently.</summary>
    Redist,
    /// <summary>A registry key deleted with everything below it, for a clean import.</summary>
    RegistryDelete,
    /// <summary>Registry content from a .reg file of the game, with its paths pointing at this PC's game folder.</summary>
    RegistryImport,
    /// <summary>A Windows compatibility mode for a program of the game, for the player (HKCU).</summary>
    Compatibility,
    /// <summary>A folder of the game copied into the player's profile, unless the target exists already.</summary>
    Profile,
}

/// <summary>One thing preparing a PC for a game does, as the agent checked it and the player confirms it.</summary>
/// <param name="Title">What the player reads, in Czech.</param>
/// <param name="NeedsAdmin">Run in the one elevated process after the UAC prompt. Otherwise run by the client as the player.</param>
public sealed record SetupStepDto(SetupStepKind Kind, string Title, bool NeedsAdmin)
{
    /// <summary>Redist: the installer. Compatibility: the program. Profile: the folder to copy. Absolute paths on this PC.</summary>
    public string? File { get; init; }

    /// <summary>SHA-256 the installer must still have when it is started, the hash the game's manifest has for it.</summary>
    public string? FileHash { get; init; }

    /// <summary>Redist: the silent arguments. Compatibility: the Windows layers, such as "WINXPSP3".</summary>
    public string? Arguments { get; init; }

    /// <summary>RegistryDelete: the key, HKLM\… or HKCU\…. Profile: the target, which may start with {Documents}, {AppData} or {LocalAppData}.</summary>
    public string? Target { get; init; }

    /// <summary>RegistryImport: the exact .reg text imported, already pointing at this PC's game folder.</summary>
    public string? Content { get; init; }

    /// <summary>What the step writes, for the player to look at before agreeing: registry keys and values, say.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>Already true on this PC (a redistributable that is installed), shown but not run.</summary>
    public bool AlreadyDone { get; init; }
}

/// <summary>Everything preparing this PC for a game does, checked by the agent. The client shows it, the player confirms, the client runs it.</summary>
/// <param name="SetupHash">Identifies this preparation: the game's setup and where it is installed. Sent back when it is done.</param>
/// <param name="Blocked">Why the preparation must not run, in words the player can act on. Null when it may.</param>
/// <param name="Warning">Something the player should know before agreeing, for example that nobody signed the definition.</param>
/// <param name="MissingRedistContentHash">The shared redistributables package this game needs, on the LAN but not on this PC.</param>
public sealed record SetupPlanDto(
    string ContentHash, string GameName, string SetupHash, IReadOnlyList<SetupStepDto> Steps, DefinitionVerdict DefinitionTrust,
    string? Blocked = null, string? Warning = null, string? MissingRedistContentHash = null)
{
    public bool NeedsAdmin => Steps.Any(s => s.NeedsAdmin && !s.AlreadyDone);
}

public sealed record SetupDoneRequest(string SetupHash);

/// <summary>How running a preparation went, step by step. Written by the elevated process for the client to read back.</summary>
public sealed record SetupStepResultDto(string Title, bool Ok, string? Message);

/// <summary>How strictly this PC follows the administrator's signed list of verified games.</summary>
public enum TrustMode
{
    /// <summary>The list is not loaded and nothing is said about games.</summary>
    Off,
    /// <summary>Games are marked as verified or not, and everything can still be installed. Only a revoked version is refused.</summary>
    Warn,
    /// <summary>Only verified games are installed. Without a usable list nothing is.</summary>
    Require,
}

/// <summary>What the administrator's signed list says about a game's definition: how it starts and what preparing a PC for it does.</summary>
public enum DefinitionVerdict
{
    NotChecked,
    /// <summary>The definition is the one the administrator signed together with the files.</summary>
    Verified,
    /// <summary>The list has no signed definition for this version: the version is not listed, or it was listed before definitions were signed.</summary>
    NotSigned,
    /// <summary>The administrator signed a different definition. This one came from some PC and is not used for anything that needs trust.</summary>
    Different,
}

public enum TrustVerdict
{
    NotChecked,
    /// <summary>The content hash is in the administrator's list.</summary>
    Verified,
    /// <summary>A list is loaded and does not contain this version.</summary>
    Unknown,
    /// <summary>The administrator withdrew this version.</summary>
    Revoked,
}

/// <param name="Source">Where the list comes from, null when checking is off.</param>
/// <param name="HasList">A list that verified is loaded and still valid.</param>
/// <param name="LastError">Why the last attempt to load the list failed. The previous list stays in use.</param>
public sealed record TrustStatusDto(
    TrustMode Mode, string? Source, bool HasList, long? Sequence, DateTimeOffset? IssuedAt, DateTimeOffset? ValidUntil,
    int VerifiedCount, int RevokedCount, DateTimeOffset? LastRefreshed, string? LastError, string? KeyId);

public sealed record StatusDto(string MachineId, string MachineName, string Version, int PeerCount, int GameCount, int ActiveDownloads);

// ---- Agent to agent API, served to other PCs on the LAN ----

public sealed record PeerHelloDto(string MachineId, string MachineName, int ProtocolVersion, string? AppVersion = null);

/// <summary>A game this PC is willing to serve: installed, verified and seeding.</summary>
public sealed record OfferedGameDto(string ContentHash, string GameId, string Name, string? Version, long TotalSize)
{
    /// <summary>False for a game whose files changed here. Only the pieces that still match are served.</summary>
    public bool IsComplete { get; init; } = true;

    /// <summary>Share of the game's data this PC can serve, 0 to 100.</summary>
    public double PercentIntact { get; init; } = 100;
}

/// <summary>Which pieces of a game a PC can serve. <see cref="Bitfield"/> is base64 of the pieces packed 8 per byte, first piece in the lowest bit.</summary>
public sealed record PieceMapDto(int PieceCount, string Bitfield);

/// <summary>Names of the SignalR events pushed to the GUI. The payload type of each is noted.</summary>
public static class GameShareEvents
{
    public const string HubPath = "/hub/events";

    /// <summary>PeerDto. Also sent when a known peer changes address, so clients should upsert.</summary>
    public const string PeerConnected = nameof(PeerConnected);
    /// <summary>PeerDto.</summary>
    public const string PeerDisconnected = nameof(PeerDisconnected);
    /// <summary>GameDto. A game version seen for the first time, locally or on the LAN.</summary>
    public const string GameDiscovered = nameof(GameDiscovered);
    /// <summary>GameDto. State, peers or installation of a known game changed.</summary>
    public const string GameUpdated = nameof(GameUpdated);
    /// <summary>GameDto, the last state seen. A version that is neither installed here nor offered by any PC any more.</summary>
    public const string GameRemoved = nameof(GameRemoved);
    /// <summary>DownloadDto for all Download* events.</summary>
    public const string DownloadStarted = nameof(DownloadStarted);
    public const string DownloadProgress = nameof(DownloadProgress);
    public const string DownloadPaused = nameof(DownloadPaused);
    public const string DownloadCompleted = nameof(DownloadCompleted);
    public const string DownloadFailed = nameof(DownloadFailed);
    public const string DownloadCancelled = nameof(DownloadCancelled);
    /// <summary>SeedDto.</summary>
    public const string SeedStarted = nameof(SeedStarted);
    public const string SeedStopped = nameof(SeedStopped);
}
