namespace GameShare.Core;

public enum InstallationState
{
    /// <summary>Full manifest verification passed. The only state that counts as installed.</summary>
    Installed,
    /// <summary>Verification found missing or corrupted files.</summary>
    Invalid,
}

/// <summary>A game version present on this PC. Rows exist only for complete games, an unfinished one is a <see cref="Download"/>.</summary>
/// <param name="ContentHash">Which version. Joins to the stored manifest.</param>
/// <param name="Seeding">Whether this PC offers the game to others.</param>
/// <param name="ResumeData">Lets seeding start without re-hashing the whole game. Optional, never required for correctness.</param>
public sealed record Installation(
    long Id,
    string ContentHash,
    string InstallPath,
    InstallationState State,
    DateTimeOffset InstalledAt,
    DateTimeOffset? VerifiedAt,
    bool Seeding,
    byte[]? ResumeData = null);

public enum DownloadState
{
    Queued,
    Downloading,
    Paused,
    /// <summary>All data arrived, the manifest is being checked.</summary>
    Verifying,
    Completed,
    Failed,
}

public enum DownloadKind
{
    /// <summary>A game version that is not on this PC yet.</summary>
    Install,
    /// <summary>Restores the files of an installed game to what its manifest says. Overwrites changed content files, never volatile ones.</summary>
    Repair,
    /// <summary>Brings an installed game to another version, fetching only the pieces that differ.</summary>
    Update,
}

/// <summary>One attempt to bring a game version onto this PC. Survives restarts through its resume data.</summary>
/// <param name="InstallationId">The installation being repaired or updated. Null for a new install.</param>
/// <param name="CompletedAt">When the download reached <see cref="DownloadState.Completed"/>. Null until then.</param>
/// <param name="PeakDownloadRate">The highest download speed seen, in bytes per second. Null until something was measured.</param>
public sealed record Download(
    long Id,
    string ContentHash,
    string TargetRoot,
    DownloadState State,
    string? Error,
    byte[]? ResumeData,
    long BytesDone,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DownloadKind Kind = DownloadKind.Install,
    long? InstallationId = null,
    DateTimeOffset? CompletedAt = null,
    long? PeakDownloadRate = null)
{
    /// <summary>Still expected to make progress or be resumed.</summary>
    public bool IsActive => State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Paused or DownloadState.Verifying;
}
