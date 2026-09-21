using GameShare.Protocol;

namespace GameShare.Agent;

/// <summary>
/// Startup configuration from appsettings.json (section "Agent") or environment variables such as Agent__LocalApiPort.
/// Things a user changes at runtime, like game folders and limits, are not here, they live in the database.
/// </summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>Database, logs. Defaults to %ProgramData%\GameShare.</summary>
    public string? DataDir { get; set; }

    /// <summary>Control API for the GUI. Bound to loopback only.</summary>
    public int LocalApiPort { get; set; } = 47701;

    /// <summary>Read-only API other PCs use to fetch manifests and torrents. Bound to all adapters, refuses non-LAN callers.</summary>
    public int PeerApiPort { get; set; } = 47702;

    public int DiscoveryPort { get; set; } = 47800;

    /// <summary>BitTorrent port. Must be allowed through the Windows firewall on the private profile.</summary>
    public int TorrentPort { get; set; } = 6881;

    public string? MachineName { get; set; }

    /// <summary>Game data and API calls stay on private networks. Leave on.</summary>
    public bool LanOnly { get; set; } = true;

    /// <summary>Only for several agents on one machine, as in tests.</summary>
    public bool AllowMultipleConnectionsPerIp { get; set; }

    /// <summary>Game roots used the first time, before anything is saved in the database.</summary>
    public List<string> InitialGameRoots { get; set; } = [];

    public TimeSpan HelloInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan PeerTimeout { get; set; } = TimeSpan.FromSeconds(35);
    public TimeSpan CatalogRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RescanInterval { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan DownloadTickInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan ResumeSaveInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Off: the administrator's list of verified games is ignored. Warn: games are marked, everything but a revoked version can be installed.
    /// Require: only verified games are installed. Set by the administrator here or with Agent__TrustMode, not from the GUI.
    /// </summary>
    public TrustMode TrustMode { get; set; } = TrustMode.Off;

    /// <summary>An https:// address, or a file path such as \\server\share\trust.json, of the signed list. Needed unless <see cref="TrustMode"/> is Off.</summary>
    public string? TrustListSource { get; set; }

    /// <summary>Public key of the administrator, one line of base64 as printed by gameshare-admin keygen.</summary>
    public string? TrustPublicKey { get; set; }

    public TimeSpan TrustRefreshInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How many game files the transfer engine keeps open at once. An open file cannot be replaced by a game that saves by truncating it,
    /// so this is kept low. See TorrentEngineOptions.OpenFileLimit.
    /// </summary>
    public int OpenFileLimit { get; set; } = 8;

    /// <summary>A seed that has uploaded nothing for this long lets go of its files. Null turns it off.</summary>
    public TimeSpan? SeedIdleRelease { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a game folder must be still before the files a game touched are looked at.</summary>
    public TimeSpan ChangeQuietPeriod { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the folders that are watched for changes are brought in line with the installed games.</summary>
    public TimeSpan ChangeWatchSyncInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>A seed of a damaged game is re-checked at most this often while the game keeps changing.</summary>
    public TimeSpan DamagedSeedRecheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often resume data of seeded games is stored, so a restart does not re-hash the whole library.</summary>
    public TimeSpan SeedResumeInterval { get; set; } = TimeSpan.FromMinutes(5);

    public string ResolveDataDir() =>
        Path.GetFullPath(DataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameShare"));

    public string ResolveMachineName() => string.IsNullOrWhiteSpace(MachineName) ? Environment.MachineName : MachineName;

    /// <summary>Fails fast with a message that names the setting, instead of a cryptic socket error later.</summary>
    public void Validate()
    {
        foreach (var (name, port) in new[]
        {
            (nameof(LocalApiPort), LocalApiPort), (nameof(PeerApiPort), PeerApiPort),
            (nameof(DiscoveryPort), DiscoveryPort), (nameof(TorrentPort), TorrentPort),
        })
            if (port is < 1 or > 65535) throw new InvalidOperationException($"Agent:{name} must be 1-65535 but is {port}.");

        if (LocalApiPort == PeerApiPort)
            throw new InvalidOperationException("Agent:LocalApiPort and Agent:PeerApiPort must differ, they are separate listeners with different trust.");
        if (TrustMode != TrustMode.Off)
        {
            if (string.IsNullOrWhiteSpace(TrustListSource))
                throw new InvalidOperationException($"Agent:TrustListSource is needed when Agent:TrustMode is {TrustMode}. Give the address or file of the signed list, or set the mode to Off.");
            if (string.IsNullOrWhiteSpace(TrustPublicKey))
                throw new InvalidOperationException($"Agent:TrustPublicKey is needed when Agent:TrustMode is {TrustMode}. Use the public key printed by gameshare-admin keygen.");
            try { Storage.TrustSigning.CheckPublicKey(TrustPublicKey); }
            catch (InvalidDataException ex) { throw new InvalidOperationException($"Agent:TrustPublicKey is not usable: {ex.Message}", ex); }
            if (TrustRefreshInterval < TimeSpan.FromSeconds(1))
                throw new InvalidOperationException("Agent:TrustRefreshInterval must be at least one second.");
        }
        if (PeerTimeout < HelloInterval * 2)
            throw new InvalidOperationException("Agent:PeerTimeout must be at least twice Agent:HelloInterval.");
    }
}
