using System.Globalization;
using GameShare.Protocol;
using Microsoft.Data.Sqlite;

namespace GameShare.Core.Data;

/// <summary>A game version this PC knows about, from a scan or from another PC.</summary>
/// <param name="TorrentBytes">Transport metadata needed to seed or download. Null until known.</param>
public sealed record StoredManifest(GameManifest Manifest, byte[]? TorrentBytes, DateTimeOffset CreatedAt);

/// <summary>
/// The per-PC SQLite database. Deliberately thin: plain SQL, one method per question the agent asks.
/// Per-file and per-chunk rows are not stored, they live inside the manifest JSON and the torrent,
/// where they are already hash-verified. Peers are not stored either, discovery keeps them in memory.
/// </summary>
public sealed class GameShareDb
{
    /// <summary>Each entry upgrades the schema by one version. Append only, never edit a released entry.</summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE manifests (
            content_hash      TEXT PRIMARY KEY,
            game_id           TEXT NOT NULL,
            name              TEXT NOT NULL,
            version           TEXT,
            folder_name       TEXT NOT NULL,
            total_size        INTEGER NOT NULL,
            piece_length      INTEGER NOT NULL,
            torrent_info_hash TEXT,
            manifest_json     TEXT NOT NULL,
            torrent           BLOB,
            created_at        TEXT NOT NULL
        );
        CREATE INDEX ix_manifests_game_id ON manifests(game_id);

        CREATE TABLE installations (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            content_hash TEXT NOT NULL REFERENCES manifests(content_hash),
            install_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
            state        TEXT NOT NULL,
            installed_at TEXT NOT NULL,
            verified_at  TEXT,
            seeding      INTEGER NOT NULL DEFAULT 1
        );
        CREATE INDEX ix_installations_content ON installations(content_hash);

        CREATE TABLE downloads (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            content_hash TEXT NOT NULL REFERENCES manifests(content_hash),
            target_root  TEXT NOT NULL,
            state        TEXT NOT NULL,
            error        TEXT,
            resume_data  BLOB,
            bytes_done   INTEGER NOT NULL DEFAULT 0,
            created_at   TEXT NOT NULL,
            updated_at   TEXT NOT NULL
        );
        -- At most one unfinished download per game version.
        CREATE UNIQUE INDEX ux_downloads_active ON downloads(content_hash)
            WHERE state IN ('Queued', 'Downloading', 'Paused', 'Verifying');

        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,
        // 2: repairs and updates are downloads of an installed game, and seeds can skip re-hashing.
        """
        ALTER TABLE downloads ADD COLUMN kind TEXT NOT NULL DEFAULT 'Install';
        ALTER TABLE downloads ADD COLUMN installation_id INTEGER;
        ALTER TABLE installations ADD COLUMN resume_data BLOB;
        """,
        // 3: a finished download remembers when it finished and how fast it went, for a small stats line in the GUI.
        """
        ALTER TABLE downloads ADD COLUMN completed_at TEXT;
        ALTER TABLE downloads ADD COLUMN peak_download_rate INTEGER;
        """,
    ];

    private readonly string _connectionString;
    private readonly TimeProvider _time;

    private GameShareDb(string path, TimeProvider time)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        _time = time;
    }

    /// <summary>Opens the database, creating it or upgrading its schema as needed.</summary>
    public static async Task<GameShareDb> OpenAsync(string path, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var db = new GameShareDb(path, timeProvider ?? TimeProvider.System);
        await db.MigrateAsync(cancellationToken).ConfigureAwait(false);
        return db;
    }

    private async Task MigrateAsync(CancellationToken ct)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(conn, "PRAGMA journal_mode = WAL;", ct).ConfigureAwait(false); // readers never block the writer

        int version = Convert.ToInt32(await ScalarAsync(conn, "PRAGMA user_version;", ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (version > Migrations.Length)
            throw new InvalidOperationException(
                $"Database schema version {version} is newer than this GameShare build understands ({Migrations.Length}). Update GameShare.");

        for (int v = version; v < Migrations.Length; v++)
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(conn, Migrations[v], ct, tx).ConfigureAwait(false);
            await ExecuteAsync(conn, $"PRAGMA user_version = {v + 1};", ct, tx).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(await ScalarAsync(conn, "PRAGMA user_version;", ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    // ---- manifests ----

    /// <summary>Inserts or updates a manifest. A null torrent never erases a torrent that is already stored.</summary>
    public async Task SaveManifestAsync(GameManifest manifest, byte[]? torrentBytes, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO manifests (content_hash, game_id, name, version, folder_name, total_size, piece_length, torrent_info_hash, manifest_json, torrent, created_at)
            VALUES ($hash, $gameId, $name, $version, $folder, $size, $piece, $infoHash, $json, $torrent, $now)
            ON CONFLICT(content_hash) DO UPDATE SET
                manifest_json     = excluded.manifest_json,
                torrent_info_hash = COALESCE(excluded.torrent_info_hash, torrent_info_hash),
                torrent           = COALESCE(excluded.torrent, torrent);
            """;
        cmd.Parameters.AddWithValue("$hash", manifest.ContentHash);
        cmd.Parameters.AddWithValue("$gameId", manifest.GameId);
        cmd.Parameters.AddWithValue("$name", manifest.Name);
        cmd.Parameters.AddWithValue("$version", (object?)manifest.Version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$folder", manifest.FolderName);
        cmd.Parameters.AddWithValue("$size", manifest.TotalSize);
        cmd.Parameters.AddWithValue("$piece", manifest.PieceLength);
        cmd.Parameters.AddWithValue("$infoHash", (object?)manifest.TorrentInfoHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$json", GameShareJson.Serialize(manifest));
        cmd.Parameters.AddWithValue("$torrent", (object?)torrentBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", Now());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<StoredManifest?> GetManifestAsync(string contentHash, CancellationToken ct = default) =>
        (await QueryManifestsAsync("WHERE content_hash = $p", contentHash, ct).ConfigureAwait(false)).FirstOrDefault();

    public Task<IReadOnlyList<StoredManifest>> ListManifestsAsync(CancellationToken ct = default) =>
        QueryManifestsAsync("", null, ct);

    /// <summary>All known versions of one game, newest first.</summary>
    public Task<IReadOnlyList<StoredManifest>> ListManifestsForGameAsync(string gameId, CancellationToken ct = default) =>
        QueryManifestsAsync("WHERE game_id = $p", gameId, ct);

    private async Task<IReadOnlyList<StoredManifest>> QueryManifestsAsync(string where, string? parameter, CancellationToken ct)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT manifest_json, torrent, created_at FROM manifests {where} ORDER BY created_at DESC, content_hash;";
        if (parameter is not null) cmd.Parameters.AddWithValue("$p", parameter);

        var result = new List<StoredManifest>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            result.Add(new StoredManifest(
                GameShareJson.Deserialize<GameManifest>(r.GetString(0)),
                r.IsDBNull(1) ? null : (byte[])r[1],
                ParseTime(r.GetString(2))));
        return result;
    }

    // ---- installations ----

    public async Task<Installation> AddInstallationAsync(
        string contentHash, string installPath, InstallationState state, bool seeding = true, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO installations (content_hash, install_path, state, installed_at, verified_at, seeding)
            VALUES ($hash, $path, $state, $now, $verified, $seeding)
            RETURNING id;
            """;
        var now = Now();
        cmd.Parameters.AddWithValue("$hash", contentHash);
        cmd.Parameters.AddWithValue("$path", installPath);
        cmd.Parameters.AddWithValue("$state", state.ToString());
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$verified", state == InstallationState.Installed ? now : DBNull.Value);
        cmd.Parameters.AddWithValue("$seeding", seeding ? 1 : 0);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return (await GetInstallationAsync(id, ct).ConfigureAwait(false))!;
    }

    /// <summary>Records a verification result. <see cref="InstallationState.Installed"/> also stamps the verification time.</summary>
    public async Task SetInstallationStateAsync(long id, InstallationState state, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE installations
            SET state = $state, verified_at = CASE WHEN $state = 'Installed' THEN $now ELSE verified_at END
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$state", state.ToString());
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            throw new KeyNotFoundException($"Installation {id} does not exist.");
    }

    /// <summary>Points an installation at another version of the same game, after an update has been verified.</summary>
    public async Task UpdateInstallationContentAsync(long id, string contentHash, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE installations
            SET content_hash = $hash, state = 'Installed', verified_at = $now, resume_data = NULL
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$hash", contentHash);
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            throw new KeyNotFoundException($"Installation {id} does not exist.");
    }

    /// <param name="resumeData">Null clears it, which forces the next seed start to re-hash the files.</param>
    public async Task SaveInstallationResumeDataAsync(long id, byte[]? resumeData, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE installations SET resume_data = $data WHERE id = $id;";
        cmd.Parameters.AddWithValue("$data", (object?)resumeData ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SetSeedingAsync(long id, bool seeding, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE installations SET seeding = $s WHERE id = $id;";
        cmd.Parameters.AddWithValue("$s", seeding ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<Installation?> GetInstallationAsync(long id, CancellationToken ct = default) =>
        (await QueryInstallationsAsync("WHERE id = $p", id, ct).ConfigureAwait(false)).FirstOrDefault();

    public Task<IReadOnlyList<Installation>> ListInstallationsAsync(CancellationToken ct = default) =>
        QueryInstallationsAsync("", null, ct);

    /// <summary>The verified installation of exactly this version, if any.</summary>
    public async Task<Installation?> FindInstalledAsync(string contentHash, CancellationToken ct = default) =>
        (await QueryInstallationsAsync("WHERE content_hash = $p AND state = 'Installed'", contentHash, ct).ConfigureAwait(false)).FirstOrDefault();

    public async Task DeleteInstallationAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(conn, "DELETE FROM installations WHERE id = $id;", ct, parameters: ("$id", id)).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Installation>> QueryInstallationsAsync(string where, object? parameter, CancellationToken ct)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, content_hash, install_path, state, installed_at, verified_at, seeding, resume_data FROM installations {where} ORDER BY id;";
        if (parameter is not null) cmd.Parameters.AddWithValue("$p", parameter);

        var result = new List<Installation>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            result.Add(new Installation(
                r.GetInt64(0), r.GetString(1), r.GetString(2), Enum.Parse<InstallationState>(r.GetString(3)),
                ParseTime(r.GetString(4)), r.IsDBNull(5) ? null : ParseTime(r.GetString(5)), r.GetInt64(6) != 0,
                r.IsDBNull(7) ? null : (byte[])r[7]));
        return result;
    }

    // ---- downloads ----

    /// <exception cref="InvalidOperationException">An unfinished download of this version already exists.</exception>
    public async Task<Download> CreateDownloadAsync(
        string contentHash, string targetRoot, DownloadKind kind = DownloadKind.Install, long? installationId = null, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO downloads (content_hash, target_root, state, created_at, updated_at, kind, installation_id)
            VALUES ($hash, $root, 'Queued', $now, $now, $kind, $installation)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$hash", contentHash);
        cmd.Parameters.AddWithValue("$root", targetRoot);
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$installation", (object?)installationId ?? DBNull.Value);

        long id;
        try { id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture); }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067 /* SQLITE_CONSTRAINT_UNIQUE */)
        {
            throw new InvalidOperationException($"A download of game version {contentHash} is already in progress.", ex);
        }
        return (await GetDownloadAsync(id, ct).ConfigureAwait(false))!;
    }

    public async Task UpdateDownloadAsync(
        long id, DownloadState state, string? error = null, long? bytesDone = null, long? peakDownloadRate = null, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE downloads
            SET state = $state, error = $error, bytes_done = COALESCE($bytes, bytes_done), updated_at = $now,
                completed_at = CASE WHEN $state = 'Completed' THEN $now ELSE completed_at END,
                peak_download_rate = COALESCE($peak, peak_download_rate)
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$state", state.ToString());
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bytes", (object?)bytesDone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$peak", (object?)peakDownloadRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            throw new KeyNotFoundException($"Download {id} does not exist.");
    }

    public async Task SaveResumeDataAsync(long id, byte[] resumeData, long bytesDone, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE downloads SET resume_data = $data, bytes_done = $bytes, updated_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$data", resumeData);
        cmd.Parameters.AddWithValue("$bytes", bytesDone);
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<Download?> GetDownloadAsync(long id, CancellationToken ct = default) =>
        (await QueryDownloadsAsync("WHERE id = $p", id, ct).ConfigureAwait(false)).FirstOrDefault();

    public Task<IReadOnlyList<Download>> ListDownloadsAsync(CancellationToken ct = default) =>
        QueryDownloadsAsync("", null, ct);

    public async Task DeleteDownloadAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(conn, "DELETE FROM downloads WHERE id = $id;", ct, parameters: ("$id", id)).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Download>> QueryDownloadsAsync(string where, object? parameter, CancellationToken ct)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, content_hash, target_root, state, error, resume_data, bytes_done, created_at, updated_at, kind, installation_id,
                   completed_at, peak_download_rate
            FROM downloads {where} ORDER BY id;
            """;
        if (parameter is not null) cmd.Parameters.AddWithValue("$p", parameter);

        var result = new List<Download>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            result.Add(new Download(
                r.GetInt64(0), r.GetString(1), r.GetString(2), Enum.Parse<DownloadState>(r.GetString(3)),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : (byte[])r[5],
                r.GetInt64(6), ParseTime(r.GetString(7)), ParseTime(r.GetString(8)),
                Enum.Parse<DownloadKind>(r.GetString(9)), r.IsDBNull(10) ? null : r.GetInt64(10),
                r.IsDBNull(11) ? null : ParseTime(r.GetString(11)), r.IsDBNull(12) ? null : r.GetInt64(12)));
        return result;
    }

    // ---- settings ----

    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        return await ScalarAsync(conn, "SELECT value FROM settings WHERE key = $key;", ct, ("$key", key)).ConfigureAwait(false) as string;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(conn, "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            ct, parameters: [("$key", key), ("$value", value)]).ConfigureAwait(false);
    }

    // ---- plumbing ----

    private async Task<SqliteConnection> ConnectAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        // Foreign keys and the busy timeout are per connection, not stored in the file.
        await ExecuteAsync(conn, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;", ct).ConfigureAwait(false);
        return conn;
    }

    private static async Task ExecuteAsync(SqliteConnection conn, string sql, CancellationToken ct, SqliteTransaction? tx = null,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private string Now() => _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
