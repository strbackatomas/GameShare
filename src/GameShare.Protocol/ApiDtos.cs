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

    /// <summary>A program of this game is running on this PC right now. Its files are not rewritten and its seed steps aside meanwhile.</summary>
    public bool IsRunning { get; init; }

    /// <summary>What the administrator's signed list says about this version. <see cref="TrustVerdict.NotChecked"/> when checking is off or there is no list.</summary>
    public TrustVerdict Trust { get; init; } = TrustVerdict.NotChecked;

    /// <summary>For a revoked version, the administrator's reason.</summary>
    public string? TrustNote { get; init; }

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

public sealed record PeerDto(string MachineId, string MachineName, string Address, int Port, int GameCount, DateTimeOffset LastSeen);

public sealed record DownloadPeerDto(string Name, string Address, long DownloadRate, long UploadRate);

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
}

public sealed record SeedDto(string ContentHash, string GameName, string InstallPath);

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
public sealed record SettingsDto(IReadOnlyList<string> GameRoots, bool SeedingEnabled, int? MaxUploadMBps, int? MaxDownloadMBps);

public enum LaunchState
{
    /// <summary>Nothing to start: not installed here, or the game has no program to start.</summary>
    None,
    Ready,
    /// <summary>The game does not say which program starts it. The player picks one of its programs.</summary>
    NeedsExecutable,
}

/// <summary>What the client starts. Checked by the agent: a program of the game, still as it was verified, inside the game folder.</summary>
public sealed record LaunchInfoDto(string ExecutablePath, string? Arguments, string WorkingDirectory);

/// <summary>The player's pick of the program to start, for this PC.</summary>
public sealed record LauncherChoiceRequest(string Executable, string? Arguments = null);

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

public sealed record PeerHelloDto(string MachineId, string MachineName, int ProtocolVersion);

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
