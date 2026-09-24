using System.Collections.Concurrent;
using GameShare.Core.Data;
using GameShare.Protocol;
using GameShare.Storage;
using GameShare.Torrent;
using Microsoft.Data.Sqlite;

namespace GameShare.Core.Tests;

/// <summary>One simulated PC: its own database, engine, game root and background loop, exactly as an agent will wire them.</summary>
internal sealed class Pc : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public string Dir { get; }
    public string GamesRoot { get; }
    public GameShareDb Db { get; private set; } = null!;
    public TorrentEngine Engine { get; private set; } = null!;
    public GameLibrary Library { get; private set; } = null!;
    public SeedManager Seeds { get; private set; } = null!;
    public DownloadManager Downloads { get; private set; } = null!;
    public ConcurrentQueue<DownloadEvent> DownloadEvents { get; } = new();
    public ConcurrentQueue<SeedEvent> SeedEvents { get; } = new();

    private Pc(string dir) { Dir = dir; GamesRoot = Path.Combine(dir, "Games"); Directory.CreateDirectory(GamesRoot); }

    public static async Task<Pc> StartAsync(
        string? existingDir = null, int? uploadLimit = null, int? downloadLimit = null, Func<string, long?>? freeSpace = null,
        Microsoft.Extensions.Logging.ILogger<TorrentEngine>? engineLogger = null)
    {
        var pc = new Pc(existingDir ?? TestGame.NewTempDir());
        await pc.BootAsync(uploadLimit, downloadLimit, freeSpace, engineLogger);
        return pc;
    }

    private async Task BootAsync(int? up, int? down, Func<string, long?>? freeSpace, Microsoft.Extensions.Logging.ILogger<TorrentEngine>? engineLogger)
    {
        Db = await GameShareDb.OpenAsync(Path.Combine(Dir, "gameshare.db"));
        Engine = new TorrentEngine(new TorrentEngineOptions
        {
            ListenPort = 0,
            AllowMultipleConnectionsPerIp = true, // several "PCs" share this machine's IP
            MaxUploadBytesPerSecond = up,
            MaxDownloadBytesPerSecond = down,
        }, engineLogger);
        Library = new GameLibrary(Db);
        Seeds = new SeedManager(Engine, Db);
        Seeds.SeedEventRaised += (_, e) => SeedEvents.Enqueue(e);
        Downloads = new DownloadManager(Engine, Db, Seeds, options: new DownloadManagerOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(100),
            ResumeSaveInterval = TimeSpan.FromMilliseconds(500),
            FreeSpaceProvider = freeSpace,
        });
        Downloads.DownloadEventRaised += (_, e) => DownloadEvents.Enqueue(e);
        await Downloads.RecoverAsync();
        _loop = Downloads.RunAsync(_cts.Token);
    }

    /// <summary>Puts a fake game into this PC's game root, the way a user would have installed it.</summary>
    /// <param name="customise">Change the game folder after it was copied, for example to make a second version of it.</param>
    public void AddGame(int seed = 1, long bigFileBytes = 20_000_000, Action<string>? customise = null)
    {
        using var template = new TestGame(seed: seed, largeFileBytes: bigFileBytes);
        var target = Path.Combine(GamesRoot, "TestGame");
        TestGame.CopyDirectory(template.GameDir, target);
        customise?.Invoke(target);
    }

    public string GameDir => Path.Combine(GamesRoot, "TestGame");

    public async Task<StoredManifest> OnlyKnownGameAsync() => (await Library.ListAsync()).Single().Stored;

    /// <summary>Graceful shutdown: the download loop saves resume data, the engine stops.</summary>
    public async Task ShutdownAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null) await _loop;
        Engine.Dispose();
        _loop = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_loop is not null) await ShutdownAsync();
        SqliteConnection.ClearAllPools();
        TestGame.DeleteQuietly(Dir);
        _cts.Dispose();
    }
}

internal static class Poll
{
    public static async Task UntilAsync(Func<Task<bool>> condition, string what, int timeoutMs = 60_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for: {what}");
            await Task.Delay(50);
        }
    }

    public static Task DownloadStateAsync(Pc pc, long id, DownloadState state, int timeoutMs = 60_000) =>
        UntilAsync(async () => (await pc.Downloads.GetAsync(id))?.State == state, $"download {id} to reach {state}", timeoutMs);
}

public class InstallWorkflowTests
{
    private static string InstalledDir(Pc pc, GameManifest m) => Path.Combine(pc.GamesRoot, m.FolderName);

    [Fact]
    public async Task Scan_registers_a_game_and_is_idempotent()
    {
        await using var pc = await Pc.StartAsync();
        pc.AddGame();

        var first = await pc.Library.ScanAsync([pc.GamesRoot, Path.Combine(pc.Dir, "NoSuchRoot")]);
        var second = await pc.Library.ScanAsync([pc.GamesRoot]);

        Assert.Equal(1, first.Added);
        Assert.Equal([Path.Combine(pc.Dir, "NoSuchRoot")], first.MissingRoots);
        Assert.Equal(0, second.Added);
        Assert.Equal(1, second.Unchanged);
        var game = Assert.Single(await pc.Library.ListAsync());
        Assert.Equal(InstallationState.Installed, game.Installation!.State);
        Assert.NotNull(game.Stored.TorrentBytes);
        Assert.Equal("testgame", game.Stored.Manifest.GameId);
    }

    [Fact]
    public async Task Game_travels_from_pc01_to_pc02_and_on_to_pc03_from_both()
    {
        // Upload limits keep the swarm busy long enough for the third PC to use both sources.
        await using var pc01 = await Pc.StartAsync(uploadLimit: 3_000_000);
        pc01.AddGame(bigFileBytes: 24_000_000);
        await pc01.Library.ScanAsync([pc01.GamesRoot]);
        await pc01.Seeds.StartAllAsync();
        var offer = await pc01.OnlyKnownGameAsync();
        Assert.Contains(pc01.SeedEvents, e => e.Kind == SeedEventKind.Started);

        // PC02 installs. In the real system the manifest and torrent arrive over the agent API.
        await using var pc02 = await Pc.StartAsync(uploadLimit: 3_000_000);
        var d2 = await pc02.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, pc02.GamesRoot);
        Assert.Equal(DownloadState.Downloading, d2.State);
        await Poll.DownloadStateAsync(pc02, d2.Id, DownloadState.Completed);

        var installed02 = await pc02.Db.FindInstalledAsync(offer.Manifest.ContentHash);
        Assert.NotNull(installed02);
        Assert.Equal(InstalledDir(pc02, offer.Manifest), installed02.InstallPath);
        Assert.Equal(TestGame.HashTree(Path.Combine(pc01.GamesRoot, "TestGame")), TestGame.HashTree(installed02.InstallPath));
        Assert.Contains(pc02.SeedEvents, e => e.Kind == SeedEventKind.Started); // finished games seed straight away
        Assert.Contains(pc02.Engine.Transfers, t => t.IsComplete());

        var kinds = pc02.DownloadEvents.Select(e => e.Kind).ToList();
        Assert.Equal(DownloadEventKind.Started, kinds.First());
        Assert.Equal(DownloadEventKind.Completed, kinds.Last());

        // PC03 installs while both others serve.
        long up1 = pc01.Engine.Transfers.Single().GetStatus().SessionUploaded;
        long up2 = pc02.Engine.Transfers.Single().GetStatus().SessionUploaded;
        await using var pc03 = await Pc.StartAsync();
        var d3 = await pc03.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, pc03.GamesRoot);
        await Poll.DownloadStateAsync(pc03, d3.Id, DownloadState.Completed);

        Assert.True(pc01.Engine.Transfers.Single().GetStatus().SessionUploaded > up1, "PC01 served nothing to PC03");
        Assert.True(pc02.Engine.Transfers.Single().GetStatus().SessionUploaded > up2, "PC02 served nothing to PC03, so it did not act as a source");
        Assert.NotNull(await pc03.Db.FindInstalledAsync(offer.Manifest.ContentHash));
    }

    [Fact]
    public async Task Download_continues_after_a_graceful_agent_restart_without_refetching_finished_data()
    {
        await using var source = await Pc.StartAsync();
        source.AddGame();
        await source.Library.ScanAsync([source.GamesRoot]);
        await source.Seeds.StartAllAsync();
        var offer = await source.OnlyKnownGameAsync();

        var pc = await Pc.StartAsync(downloadLimit: 2_000_000);
        var download = await pc.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, pc.GamesRoot);
        await Poll.UntilAsync(async () => (await pc.Downloads.GetAsync(download.Id))!.Percent >= 30, "30% downloaded");
        var dir = pc.Dir;
        await pc.ShutdownAsync(); // agent stops: resume data is saved to the database

        // Resume data is an optimisation. The library may not manage to produce it in time, then the files are simply re-checked.
        // Either way finished data must not be fetched again, which is asserted below.
        var saved = await pc.Db.GetDownloadAsync(download.Id);
        Assert.NotEqual(DownloadState.Completed, saved!.State);
        SqliteConnection.ClearAllPools();

        await using var restarted = await Pc.StartAsync(existingDir: dir); // new process, same database, RecoverAsync runs in boot
        await Poll.DownloadStateAsync(restarted, download.Id, DownloadState.Completed);

        var fetchedAfterRestart = restarted.Engine.Transfers.Single().GetStatus().SessionDownloaded;
        Assert.True(fetchedAfterRestart < offer.Manifest.TotalSize * 0.9,
            $"after the restart {fetchedAfterRestart} of {offer.Manifest.TotalSize} bytes were fetched again, progress was lost");
        Assert.NotNull(await restarted.Db.FindInstalledAsync(offer.Manifest.ContentHash));
        await pc.DisposeAsync();
    }

    [Fact]
    public async Task A_scan_reports_each_folder_and_how_much_of_a_new_game_is_hashed()
    {
        await using var pc = await Pc.StartAsync();
        pc.AddGame();
        var reports = new List<ScanProgress>();

        await pc.Library.ScanAsync([pc.GamesRoot], progress: new SyncProgress(reports.Add));

        Assert.Equal((1, 1, "TestGame"), (reports[0].Folder, reports[0].Folders, reports[0].Name));
        var hashing = reports.Where(r => r.TotalBytes > 0).ToList();
        Assert.NotEmpty(hashing);
        Assert.Equal(hashing[^1].TotalBytes, hashing[^1].Bytes); // ends with all of it
        Assert.True(hashing.Select(r => r.Bytes).SequenceEqual(hashing.Select(r => r.Bytes).Order()));

        reports.Clear();
        await pc.Library.ScanAsync([pc.GamesRoot], progress: new SyncProgress(reports.Add));
        Assert.All(reports, r => Assert.Equal(0, r.TotalBytes)); // a known, unchanged game is not hashed again
    }

    private sealed class SyncProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }

    [Fact]
    public async Task The_definition_travels_with_the_game_into_its_folder_and_an_edited_one_is_picked_up_by_a_scan()
    {
        await using var source = await Pc.StartAsync();
        source.AddGame(customise: dir => File.WriteAllText(Path.Combine(dir, "gameshare.json"),
            """{ "gameId": "testgame", "name": "Test Game", "launch": [ { "executable": "Game.exe" } ] }"""));
        await source.Library.ScanAsync([source.GamesRoot]);
        await source.Seeds.StartAllAsync();
        var offer = await source.OnlyKnownGameAsync();

        await using var pc = await Pc.StartAsync();
        var d = await pc.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, pc.GamesRoot);
        await Poll.DownloadStateAsync(pc, d.Id, DownloadState.Completed);

        // The installed folder carries the definition, so a scan here, or a third PC installing from here, sees the same.
        var written = await GameDefinitionFile.TryLoadAsync(pc.GameDir);
        Assert.Equal("Game.exe", Assert.Single(written!.Launch).Executable);

        // Editing how the game starts keeps it the same game, and the next scan uses the new definition.
        var changed = new List<Installation>();
        pc.Library.DefinitionChanged += (_, i) => changed.Add(i);
        await File.WriteAllTextAsync(Path.Combine(pc.GameDir, "gameshare.json"),
            """{ "gameId": "testgame", "name": "Test Game", "launch": [ { "executable": "Game.exe", "runAsAdmin": true } ] }""");
        var scan = await pc.Library.ScanAsync([pc.GamesRoot]);

        Assert.Equal(0, scan.Added);
        var stored = await pc.OnlyKnownGameAsync();
        Assert.Equal(offer.Manifest.ContentHash, stored.Manifest.ContentHash);
        Assert.True(Assert.Single(stored.Manifest.Definition!.Launch).RunAsAdmin);
        Assert.Single(changed);

        await pc.Library.ScanAsync([pc.GamesRoot]);
        Assert.Single(changed); // unchanged the second time
    }

    [Fact]
    public async Task Pause_stops_the_transfer_and_resume_finishes_the_install()
    {
        await using var source = await Pc.StartAsync();
        source.AddGame();
        await source.Library.ScanAsync([source.GamesRoot]);
        await source.Seeds.StartAllAsync();
        var offer = await source.OnlyKnownGameAsync();

        await using var pc = await Pc.StartAsync(downloadLimit: 2_000_000);
        var d = await pc.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, pc.GamesRoot);
        await Poll.UntilAsync(async () => (await pc.Downloads.GetAsync(d.Id))!.Percent >= 15, "15% downloaded");

        await pc.Downloads.PauseAsync(d.Id);

        var paused = (await pc.Downloads.GetAsync(d.Id))!;
        Assert.Equal(DownloadState.Paused, paused.State);
        Assert.Empty(pc.Engine.Transfers);
        Assert.NotEmpty((await pc.Db.GetDownloadAsync(d.Id))!.ResumeData!);
        await Task.Delay(700);
        Assert.Equal(paused.BytesDone, (await pc.Downloads.GetAsync(d.Id))!.BytesDone); // nothing moves while paused

        await pc.Downloads.ResumeAsync(d.Id);
        await Poll.DownloadStateAsync(pc, d.Id, DownloadState.Completed);

        Assert.NotNull(await pc.Db.FindInstalledAsync(offer.Manifest.ContentHash));
        Assert.Contains(pc.DownloadEvents, e => e.Kind == DownloadEventKind.Paused);
        Assert.Contains(pc.DownloadEvents, e => e.Kind == DownloadEventKind.Resumed);
    }

    [Fact]
    public async Task Pausing_right_after_the_start_returns_quickly_and_the_download_can_still_finish()
    {
        await using var source = await Pc.StartAsync();
        source.AddGame();
        await source.Library.ScanAsync([source.GamesRoot]);
        await source.Seeds.StartAllAsync();
        var offer = await source.OnlyKnownGameAsync();

        await using var pc = await Pc.StartAsync();
        var d = await pc.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, pc.GamesRoot);

        // The library may not be able to produce resume data yet. That must slow nothing down and lose nothing.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await pc.Downloads.PauseAsync(d.Id);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"pausing took {sw.Elapsed.TotalSeconds:F1} seconds");
        Assert.Equal(DownloadState.Paused, (await pc.Downloads.GetAsync(d.Id))!.State);

        await pc.Downloads.ResumeAsync(d.Id); // straight away, while the torrent may still be being removed
        await Poll.DownloadStateAsync(pc, d.Id, DownloadState.Completed);
        Assert.NotNull(await pc.Db.FindInstalledAsync(offer.Manifest.ContentHash));
    }

    [Fact]
    public async Task Cancel_can_delete_the_partial_files_and_a_scan_never_registers_a_half_downloaded_game()
    {
        await using var source = await Pc.StartAsync();
        source.AddGame();
        await source.Library.ScanAsync([source.GamesRoot]);
        await source.Seeds.StartAllAsync();
        var offer = await source.OnlyKnownGameAsync();

        await using var pc = await Pc.StartAsync(downloadLimit: 1_000_000);
        var d = await pc.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, pc.GamesRoot);
        await Poll.UntilAsync(async () => (await pc.Downloads.GetAsync(d.Id))!.Percent >= 5, "some data on disk");

        var scan = await pc.Library.ScanAsync([pc.GamesRoot]); // runs while the download writes into the folder
        Assert.Equal(0, scan.Added);
        Assert.Equal(1, scan.Skipped);
        Assert.Empty(await pc.Db.ListInstallationsAsync());

        await pc.Downloads.CancelAsync(d.Id, deleteFiles: true);

        Assert.False(Directory.Exists(InstalledDir(pc, offer.Manifest)));
        Assert.Empty(await pc.Downloads.ListAsync());
        Assert.Empty(pc.Engine.Transfers);
        Assert.Contains(pc.DownloadEvents, e => e.Kind == DownloadEventKind.Cancelled);
    }

    /// <summary>Cancelling keeps the files by default. A later install into that same folder must still be able to reclaim them, not be refused as a foreign folder forever.</summary>
    [Fact]
    public async Task Cancelling_without_deleting_files_still_lets_a_later_install_into_that_folder_repair_them()
    {
        await using var source = await Pc.StartAsync();
        source.AddGame();
        await source.Library.ScanAsync([source.GamesRoot]);
        await source.Seeds.StartAllAsync();
        var offer = await source.OnlyKnownGameAsync();

        await using var pc = await Pc.StartAsync(downloadLimit: 1_000_000);
        var d = await pc.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, pc.GamesRoot);
        await Poll.UntilAsync(async () => (await pc.Downloads.GetAsync(d.Id))!.Percent >= 5, "some data on disk");

        await pc.Downloads.CancelAsync(d.Id, deleteFiles: false);
        Assert.True(Directory.Exists(InstalledDir(pc, offer.Manifest)));

        // Retrying must not be refused as an unrecognised folder, even though the cancelled download's own id is gone.
        var retry = await pc.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, pc.GamesRoot);
        await Poll.DownloadStateAsync(pc, retry.Id, DownloadState.Completed);
        Assert.NotNull(await pc.Db.FindInstalledAsync(offer.Manifest.ContentHash));
    }

    /// <summary>
    /// The kept partial files are only recognised through the cancelled download's own record. A scan that ran
    /// before a retry used to have no way to know that, so it hashed whatever partial or corrupt bytes were on disk
    /// and registered them as a brand new, self-consistently "valid" game version — which then got seeded to the LAN
    /// as if it were the real thing, and blocked both the retry and forgetting the old download afterwards.
    /// </summary>
    [Fact]
    public async Task A_scan_between_cancelling_and_retrying_does_not_register_the_partial_files_as_a_new_game()
    {
        await using var source = await Pc.StartAsync();
        source.AddGame();
        await source.Library.ScanAsync([source.GamesRoot]);
        await source.Seeds.StartAllAsync();
        var offer = await source.OnlyKnownGameAsync();

        await using var pc = await Pc.StartAsync(downloadLimit: 1_000_000);
        var d = await pc.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, pc.GamesRoot);
        await Poll.UntilAsync(async () => (await pc.Downloads.GetAsync(d.Id))!.Percent >= 5, "some data on disk");
        await pc.Downloads.CancelAsync(d.Id, deleteFiles: false);

        var scan = await pc.Library.ScanAsync([pc.GamesRoot]);
        Assert.Equal(0, scan.Added);
        Assert.Equal(1, scan.Skipped);
        Assert.Empty(await pc.Db.ListInstallationsAsync());

        var retry = await pc.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, pc.GamesRoot);
        await Poll.DownloadStateAsync(pc, retry.Id, DownloadState.Completed);
        Assert.NotNull(await pc.Db.FindInstalledAsync(offer.Manifest.ContentHash));
    }

    /// <summary>
    /// Right after the seed stops, a big file can still be briefly held open (by the engine's own asynchronous
    /// teardown, or by something external like an indexer). Uninstall must ride that out instead of giving up on the
    /// first try and leaving a half-deleted, untracked folder for a later install to trip over as "already exists".
    /// </summary>
    [Fact]
    public async Task Uninstall_retries_past_a_file_still_briefly_locked_right_after_the_seed_stops()
    {
        await using var source = await Pc.StartAsync();
        source.AddGame();
        await source.Library.ScanAsync([source.GamesRoot]);
        await source.Seeds.StartAllAsync();
        var offer = await source.OnlyKnownGameAsync();

        await using var pc = await Pc.StartAsync();
        var d = await pc.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, pc.GamesRoot);
        await Poll.DownloadStateAsync(pc, d.Id, DownloadState.Completed);
        var inst = Assert.Single(await pc.Db.ListInstallationsAsync());

        var lockedFile = Path.Combine(inst.InstallPath, "content", "big.pak");
        Task uninstall;
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            uninstall = pc.Downloads.UninstallAsync(inst.Id, deleteFiles: true);
            await Task.Delay(400); // outlasts the first retry while the file is still locked
            Assert.False(uninstall.IsCompleted, "the delete should still be retrying, not already given up");
        } // releasing the lock here lets the next retry succeed
        await uninstall;

        Assert.False(Directory.Exists(inst.InstallPath));
        Assert.Empty(await pc.Db.ListInstallationsAsync());
    }

    [Fact]
    public async Task Bad_requests_are_refused_up_front_with_clear_messages_and_write_nothing()
    {
        await using var source = await Pc.StartAsync();
        source.AddGame(seed: 1);
        await source.Library.ScanAsync([source.GamesRoot]);
        var offer = await source.OnlyKnownGameAsync();

        // Not enough disk space.
        await using (var small = await Pc.StartAsync(freeSpace: _ => 1000))
        {
            var ex = await Assert.ThrowsAsync<IOException>(() => small.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, small.GamesRoot));
            Assert.Contains("Not enough free space", ex.Message);
            Assert.Empty(await small.Db.ListDownloadsAsync());
            Assert.False(Directory.Exists(InstalledDir(small, offer.Manifest)));
        }

        // Target folder exists and belongs to someone else.
        await using (var occupied = await Pc.StartAsync())
        {
            Directory.CreateDirectory(InstalledDir(occupied, offer.Manifest));
            await File.WriteAllTextAsync(Path.Combine(InstalledDir(occupied, offer.Manifest), "mine.txt"), "do not overwrite");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => occupied.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, occupied.GamesRoot));
            Assert.Contains("will not overwrite", ex.Message);
        }

        // Manifest that points outside the game folder.
        await using (var target = await Pc.StartAsync())
        {
            var evil = offer.Manifest with { Files = [.. offer.Manifest.Files, new ManifestFile("../../evil.dll", 1, new string('a', 64))] };
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => target.Downloads.StartInstallAsync(evil, offer.TorrentBytes!, target.GamesRoot));
            Assert.Contains("Unsafe file path", ex.Message);
            Assert.Empty(await target.Db.ListDownloadsAsync());
        }

        // Torrent that belongs to a different game than the manifest claims.
        using var other = new TestGame(seed: 99);
        var otherScan = await Storage.ContentScanner.ScanAsync(other.GameDir);
        var wrongTorrent = TorrentBuilder.Build(otherScan);
        await using (var mismatch = await Pc.StartAsync())
        {
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => mismatch.Downloads.StartInstallAsync(offer.Manifest, wrongTorrent.TorrentBytes, mismatch.GamesRoot));
            Assert.Contains("does not match its manifest", ex.Message);
            Assert.Empty(mismatch.Engine.Transfers);
            Assert.Equal(DownloadState.Failed, (await mismatch.Downloads.ListAsync()).Single().State);
        }

        // Already installed: the source PC itself.
        var already = await Assert.ThrowsAsync<InvalidOperationException>(() => source.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, source.GamesRoot));
        Assert.Contains("already installed", already.Message);
    }

    /// <summary>A different version of an already-installed game must be fetched as an update, not started as a second, parallel install.</summary>
    [Fact]
    public async Task Installing_a_different_version_of_an_already_installed_game_is_refused_in_favour_of_update()
    {
        await using var v2Source = await Pc.StartAsync();
        v2Source.AddGame(customise: dir => TestGame.CorruptOneByte(Path.Combine(dir, "content", "big.pak")));
        await v2Source.Library.ScanAsync([v2Source.GamesRoot]);
        var v2 = (await v2Source.Library.ListAsync()).Single().Stored;

        await using var pc = await Pc.StartAsync();
        pc.AddGame();
        await pc.Library.ScanAsync([pc.GamesRoot]);
        var installed = Assert.Single(await pc.Db.ListInstallationsAsync());
        Assert.NotEqual(installed.ContentHash, v2.Manifest.ContentHash); // same game, different version

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pc.Downloads.StartInstallAsync(v2.Manifest, v2.TorrentBytes!, pc.GamesRoot));
        Assert.Contains("already installed as a different version", ex.Message);
        Assert.Contains(installed.InstallPath, ex.Message);
        Assert.Empty(await pc.Db.ListDownloadsAsync());
    }

    [Fact]
    public async Task Install_whose_files_fail_the_manifest_is_not_marked_installed_and_is_not_seeded()
    {
        await using var source = await Pc.StartAsync();
        source.AddGame();
        await source.Library.ScanAsync([source.GamesRoot]);
        await source.Seeds.StartAllAsync();
        var offer = await source.OnlyKnownGameAsync();

        // A manifest that lies about one file's hash but keeps a matching ContentHash, so it passes validation.
        var lying = offer.Manifest.Files.Select(f => f.Path == "Game.exe" ? f with { Hash = new string('b', 64) } : f).ToList();
        var forged = offer.Manifest with { Files = lying, ContentHash = ContentHasher.Compute(lying) };
        Assert.Empty(Storage.ManifestValidator.Validate(forged));

        await using var pc = await Pc.StartAsync();
        var d = await pc.Downloads.StartInstallAsync(forged, offer.TorrentBytes!, pc.GamesRoot);
        await Poll.DownloadStateAsync(pc, d.Id, DownloadState.Failed);

        var status = (await pc.Downloads.GetAsync(d.Id))!;
        Assert.Contains("Verification failed", status.Error);
        Assert.Contains("Game.exe", status.Error);
        Assert.Null(await pc.Db.FindInstalledAsync(forged.ContentHash));
        Assert.Empty(await pc.Db.ListInstallationsAsync());
        Assert.Empty(pc.Engine.Transfers); // possibly corrupt data is not offered to anyone
        Assert.Contains(pc.DownloadEvents, e => e.Kind == DownloadEventKind.Failed);
    }
}

internal static class TransferExtensions
{
    public static bool IsComplete(this TorrentTransfer t) => t.GetStatus().IsComplete;
}
