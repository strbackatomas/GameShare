using System.Collections.Concurrent;
using GameShare.Core.Data;
using GameShare.Protocol;
using GameShare.Torrent;
using Microsoft.Extensions.Logging;

namespace GameShare.Core.Tests;

/// <summary>Keeps everything the transfer engine logs, so a test can see what the library did.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Enqueue(formatter(state, exception));
}

/// <summary>What happens to a game that changes after it was installed: damage, volatile files, repair, update.</summary>
public class GameCareTests
{
    private static async Task WriteAsync(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private static async Task<Pc> PcWithGameAsync(Action<string>? customise = null, int? uploadLimit = null)
    {
        var pc = await Pc.StartAsync(uploadLimit: uploadLimit);
        pc.AddGame(customise: customise);
        await pc.Library.ScanAsync([pc.GamesRoot]);
        return pc;
    }

    [Fact]
    public async Task A_game_whose_files_changed_size_is_marked_damaged_and_not_turned_into_a_new_version()
    {
        await using var pc = await PcWithGameAsync();
        var original = (await pc.Library.ListAsync()).Single().Stored.Manifest.ContentHash;
        var changes = new List<InstallationChange>();
        pc.Library.InstallationChanged += (_, c) => changes.Add(c);

        await using (var fs = new FileStream(Path.Combine(pc.GameDir, "content", "big.pak"), FileMode.Open, FileAccess.Write)) fs.SetLength(fs.Length - 100);
        var scan = await pc.Library.ScanAsync([pc.GamesRoot]);

        Assert.Equal(0, scan.Added);
        Assert.Equal([pc.GameDir], scan.Damaged);
        var game = Assert.Single(await pc.Library.ListAsync()); // still one version, no variant appeared
        Assert.Equal(original, game.Stored.Manifest.ContentHash);
        Assert.Equal(InstallationState.Invalid, game.Installation!.State);
        Assert.Null(await pc.Db.FindInstalledAsync(original)); // and so it is not offered
        Assert.Equal(InstallationChangeKind.StateChanged, Assert.Single(changes).Kind);

        // Scanning again neither repeats the event nor re-registers it.
        var again = await pc.Library.ScanAsync([pc.GamesRoot]);
        Assert.Equal([pc.GameDir], again.Damaged);
        Assert.Single(changes);
    }

    [Fact]
    public async Task A_same_size_change_is_invisible_to_a_scan_but_a_full_check_finds_it_and_it_stays_damaged()
    {
        await using var pc = await PcWithGameAsync();
        var hash = (await pc.Library.ListAsync()).Single().Stored.Manifest.ContentHash;
        TestGame.CorruptOneByte(Path.Combine(pc.GameDir, "content", "big.pak")); // a game rewriting a byte keeps the size

        var scan = await pc.Library.ScanAsync([pc.GamesRoot]);
        Assert.Equal(1, scan.Unchanged); // the quick check only sees sizes

        var changes = await pc.Library.CheckAsync(hash);
        Assert.Equal(["content/big.pak"], changes.Modified);
        Assert.Empty(changes.Missing);
        Assert.Equal(["content/**"], changes.SuggestedPatterns);
        Assert.Equal(InstallationState.Invalid, (await pc.Db.ListInstallationsAsync()).Single().State);

        // The bug this guards against: the next scan sees matching sizes and quietly puts the game back in service.
        var later = await pc.Library.ScanAsync([pc.GamesRoot]);
        Assert.Equal([pc.GameDir], later.Damaged);
        Assert.Equal(InstallationState.Invalid, (await pc.Db.ListInstallationsAsync()).Single().State);
    }

    [Fact]
    public async Task Putting_the_original_back_makes_a_full_check_pass_and_the_game_is_offered_again()
    {
        await using var pc = await PcWithGameAsync();
        var hash = (await pc.Library.ListAsync()).Single().Stored.Manifest.ContentHash;
        var big = Path.Combine(pc.GameDir, "content", "big.pak");
        TestGame.CorruptOneByte(big);
        await pc.Library.CheckAsync(hash);
        Assert.Null(await pc.Db.FindInstalledAsync(hash));

        TestGame.CorruptOneByte(big); // the same byte flipped back
        var changes = await pc.Library.CheckAsync(hash);

        Assert.True(changes.IsIntact);
        Assert.NotNull(await pc.Db.FindInstalledAsync(hash));
    }

    [Fact]
    public async Task A_deleted_game_folder_marks_the_installation_damaged_and_a_restored_one_recovers_it()
    {
        await using var pc = await PcWithGameAsync();
        var hash = (await pc.Library.ListAsync()).Single().Stored.Manifest.ContentHash;
        var backup = Path.Combine(pc.Dir, "backup");
        TestGame.CopyDirectory(pc.GameDir, backup);
        Directory.Delete(pc.GameDir, recursive: true); // for example an unplugged drive

        var gone = await pc.Library.ScanAsync([pc.GamesRoot]);
        Assert.Equal([pc.GameDir], gone.Damaged);
        Assert.Null(await pc.Db.FindInstalledAsync(hash));

        TestGame.CopyDirectory(backup, pc.GameDir); // plugged back in
        var back = await pc.Library.ScanAsync([pc.GamesRoot]);

        Assert.Empty(back.Damaged);
        Assert.NotNull(await pc.Db.FindInstalledAsync(hash)); // a full check confirmed it before putting it back
    }

    [Fact]
    public async Task Registering_the_current_files_replaces_the_old_version_and_reports_the_replacement()
    {
        await using var pc = await PcWithGameAsync();
        var v1 = (await pc.Library.ListAsync()).Single().Stored.Manifest.ContentHash;
        var changes = new List<InstallationChange>();
        pc.Library.InstallationChanged += (_, c) => changes.Add(c);

        TestGame.CorruptOneByte(Path.Combine(pc.GameDir, "content", "big.pak")); // a deliberate patch
        var v2Game = await pc.Library.RescanAsync(pc.GameDir);

        Assert.NotEqual(v1, v2Game.Stored.Manifest.ContentHash);
        var replaced = Assert.Single(changes);
        Assert.Equal(InstallationChangeKind.Replaced, replaced.Kind);
        Assert.Equal(v1, replaced.Previous!.ContentHash);
        Assert.Equal(v2Game.Stored.Manifest.ContentHash, replaced.Current.ContentHash);
        Assert.Equal(InstallationState.Installed, replaced.Current.State);
        Assert.Equal(2, (await pc.Library.ListAsync()).Count); // the old manifest is kept, it is still what other PCs may have
        Assert.Single(await pc.Db.ListInstallationsAsync());
    }

    [Fact]
    public async Task Registering_an_unknown_folder_is_an_error_that_says_so()
    {
        await using var pc = await Pc.StartAsync();
        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => pc.Library.RescanAsync(Path.Combine(pc.GamesRoot, "Nope")));
        Assert.Contains("not a registered game folder", ex.Message);
    }

    // ---------------- volatile files ----------------

    [Fact]
    public async Task Once_saves_are_declared_volatile_playing_never_makes_the_game_look_damaged_or_new()
    {
        await using var pc = await PcWithGameAsync(dir => WriteAsync(dir, "saves/slot1.sav", "level 1").GetAwaiter().GetResult());
        var v1 = (await pc.Library.ListAsync()).Single().Stored.Manifest;
        Assert.Contains(v1.Files, f => f.Path.StartsWith("saves/")); // before the declaration the save counted as content

        var registered = await pc.Library.RescanAsync(pc.GameDir, ["saves/**"]);
        var manifest = registered.Stored.Manifest;
        Assert.DoesNotContain(manifest.Files, f => f.Path.StartsWith("saves/"));
        Assert.Contains("saves/**", manifest.VolatilePatterns);

        // The game is played: saves change, grow and multiply. Also a log appears.
        await WriteAsync(pc.GameDir, "saves/slot1.sav", "level 99 and a much longer description of everything");
        await WriteAsync(pc.GameDir, "saves/slot2.sav", "new");
        await WriteAsync(pc.GameDir, "game.log", "started");

        var scan = await pc.Library.ScanAsync([pc.GamesRoot]);
        var changes = await pc.Library.CheckAsync(manifest.ContentHash);

        Assert.Equal(1, scan.Unchanged);
        Assert.Empty(scan.Damaged);
        Assert.True(changes.IsIntact);
        Assert.Empty(changes.Added);
        Assert.Single(await pc.Library.ListAsync(), g => g.Stored.Manifest.ContentHash == manifest.ContentHash);
    }

    [Fact]
    public async Task A_pc_that_received_the_game_applies_the_same_volatile_rules_without_any_definition_file()
    {
        await using var source = await PcWithGameAsync(dir => WriteAsync(dir, "saves/slot1.sav", "level 1").GetAwaiter().GetResult());
        var registered = await source.Library.RescanAsync(source.GameDir, ["saves/**"]);
        await source.Seeds.StartAllAsync();
        var offer = registered.Stored;

        await using var player = await Pc.StartAsync();
        var d = await player.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, player.GamesRoot);
        await Poll.DownloadStateAsync(player, d.Id, DownloadState.Completed);
        Assert.False(File.Exists(Path.Combine(player.GameDir, "saves", "slot1.sav"))); // saves are never shared

        // The player plays. Their saves appear inside the game folder.
        await WriteAsync(player.GameDir, "saves/mine.sav", "my own progress");
        await WriteAsync(player.GameDir, "content/shadercache.log", "built");
        var scan = await player.Library.ScanAsync([player.GamesRoot]);

        Assert.Equal(1, scan.Unchanged);
        Assert.Empty(scan.Damaged);
        var known = (await player.Library.ListAsync()).Where(g => g.Installation is not null).ToList();
        Assert.Equal(offer.Manifest.ContentHash, Assert.Single(known).Stored.Manifest.ContentHash); // still exactly the shared version
        Assert.Contains("saves/**", known[0].Stored.Manifest.VolatilePatterns);
    }

    [Fact]
    public async Task Adding_a_definition_file_with_patterns_makes_the_next_scan_register_the_version_without_those_files()
    {
        await using var pc = await PcWithGameAsync(dir => WriteAsync(dir, "options/video.cfg", "fullscreen").GetAwaiter().GetResult());
        var before = (await pc.Library.ListAsync()).Single().Stored.Manifest;
        Assert.Contains(before.Files, f => f.Path == "options/video.cfg");

        await WriteAsync(pc.GameDir, "gameshare.json", """{ "gameId": "testgame", "name": "TestGame", "volatile": ["options/**"] }""");
        var scan = await pc.Library.ScanAsync([pc.GamesRoot]);

        Assert.Equal(1, scan.Added); // the rules changed, so the content did
        var after = (await pc.Library.ListAsync()).First(g => g.Installation is not null).Stored.Manifest;
        Assert.DoesNotContain(after.Files, f => f.Path.StartsWith("options/"));
        Assert.NotEqual(before.ContentHash, after.ContentHash);
    }

    // ---------------- repair ----------------

    [Fact]
    public async Task Repair_restores_changed_files_from_other_pcs_and_leaves_volatile_files_alone()
    {
        await using var source = await PcWithGameAsync(dir => WriteAsync(dir, "saves/slot1.sav", "source save").GetAwaiter().GetResult());
        var offer = (await source.Library.RescanAsync(source.GameDir, ["saves/**"])).Stored;
        await source.Seeds.StartAllAsync();

        await using var pc = await Pc.StartAsync();
        var install = await pc.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, pc.GamesRoot);
        await Poll.DownloadStateAsync(pc, install.Id, DownloadState.Completed);
        await WriteAsync(pc.GameDir, "saves/mine.sav", "my progress");

        var original = await File.ReadAllBytesAsync(Path.Combine(pc.GameDir, "content", "big.pak"));
        TestGame.CorruptOneByte(Path.Combine(pc.GameDir, "content", "big.pak"));
        var damage = await pc.Library.CheckAsync(offer.Manifest.ContentHash);
        Assert.Equal(["content/big.pak"], damage.Modified);
        Assert.Null(await pc.Db.FindInstalledAsync(offer.Manifest.ContentHash)); // out of service until repaired
        var inst = (await pc.Db.ListInstallationsAsync()).Single();

        var repair = await pc.Downloads.StartRepairAsync(inst.Id);
        Assert.Equal(DownloadKind.Repair, repair.Kind);
        Assert.Null(await pc.Db.FindInstalledAsync(offer.Manifest.ContentHash)); // and stays out while the files are rewritten
        await Poll.DownloadStateAsync(pc, repair.Id, DownloadState.Completed);

        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(pc.GameDir, "content", "big.pak")));
        Assert.Equal("my progress", await File.ReadAllTextAsync(Path.Combine(pc.GameDir, "saves", "mine.sav"))); // untouched
        Assert.NotNull(await pc.Db.FindInstalledAsync(offer.Manifest.ContentHash));
        Assert.Single(await pc.Db.ListInstallationsAsync()); // repaired in place, not a second installation
        var fetched = pc.Engine.Transfers.Single().GetStatus().SessionDownloaded;
        Assert.True(fetched < offer.Manifest.TotalSize / 2, $"repair fetched {fetched} of {offer.Manifest.TotalSize} bytes for a one byte change");
    }

    [Fact]
    public async Task Cancelling_a_repair_never_deletes_the_installed_game_even_when_asked_to()
    {
        await using var source = await PcWithGameAsync();
        await source.Seeds.StartAllAsync();
        var offer = (await source.Library.ListAsync()).Single().Stored;

        await using var player = await Pc.StartAsync();
        var install = await player.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, player.GamesRoot);
        await Poll.DownloadStateAsync(player, install.Id, DownloadState.Completed);
        TestGame.CorruptOneByte(Path.Combine(player.GameDir, "content", "big.pak"));
        await player.Library.CheckAsync(offer.Manifest.ContentHash);
        var inst = (await player.Db.ListInstallationsAsync()).Single();
        var repair = await player.Downloads.StartRepairAsync(inst.Id);

        await player.Downloads.CancelAsync(repair.Id, deleteFiles: true); // asking for deletion must not be honoured for a repair

        Assert.True(Directory.Exists(player.GameDir));
        Assert.True(File.Exists(Path.Combine(player.GameDir, "Game.exe")));
        Assert.DoesNotContain(await player.Downloads.ListAsync(), d => d.Id == repair.Id); // the repair is gone, the finished install stays as history
    }

    [Fact]
    public async Task Repair_and_update_refuse_bad_requests_with_the_reason()
    {
        await using var pc = await PcWithGameAsync();
        var inst = (await pc.Db.ListInstallationsAsync()).Single();
        var stored = (await pc.Library.ListAsync()).Single().Stored;

        await Assert.ThrowsAsync<KeyNotFoundException>(() => pc.Downloads.StartRepairAsync(9999));

        var sameVersion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pc.Downloads.StartUpdateAsync(inst.Id, stored.Manifest, stored.TorrentBytes!));
        Assert.Contains("already at this version", sameVersion.Message);

        // A different game, whatever its content.
        using var other = new TestGame(seed: 7);
        var (otherManifest, otherScan) = await Storage.ManifestBuilder.ScanAndBuildAsync(other.GameDir);
        var otherTorrent = TorrentBuilder.Build(otherScan);
        otherManifest = otherManifest with { GameId = "another-game", TorrentInfoHash = otherTorrent.InfoHash };
        var notSameGame = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pc.Downloads.StartUpdateAsync(inst.Id, otherManifest, otherTorrent.TorrentBytes));
        Assert.Contains("is not another version", notSameGame.Message);

        // Same game but it would install somewhere else.
        var renamed = otherManifest with { GameId = stored.Manifest.GameId, FolderName = "SomeOtherFolder" };
        var wrongFolder = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pc.Downloads.StartUpdateAsync(inst.Id, renamed, otherTorrent.TorrentBytes));
        Assert.Contains("will not move it", wrongFolder.Message);

        // Nothing was started and the game is still in service.
        Assert.Empty(await pc.Downloads.ListAsync());
        Assert.NotNull(await pc.Db.FindInstalledAsync(stored.Manifest.ContentHash));
    }

    // ---------------- update ----------------

    [Fact]
    public async Task Update_fetches_only_what_changed_removes_obsolete_files_and_keeps_saves_and_edited_files()
    {
        // Version 2, on a PC that will serve it: one byte changed in the big file, one file added, one file dropped.
        await using var v2Source = await Pc.StartAsync();
        v2Source.AddGame(customise: dir =>
        {
            TestGame.CorruptOneByte(Path.Combine(dir, "content", "big.pak"));
            File.WriteAllText(Path.Combine(dir, "content", "new-level.bin"), "brand new content in version two");
            File.Delete(Path.Combine(dir, "content", "sub", "tiny.txt"));
            File.WriteAllText(Path.Combine(dir, "gameshare.json"), """{ "gameId": "testgame", "name": "TestGame", "version": "2.0", "volatile": ["saves/**"] }""");
        });
        await v2Source.Library.ScanAsync([v2Source.GamesRoot]);
        await v2Source.Seeds.StartAllAsync();
        var v2 = (await v2Source.Library.ListAsync()).Single().Stored;

        // This PC has version 1 installed and has played it.
        await using var pc = await Pc.StartAsync();
        pc.AddGame(customise: dir => File.WriteAllText(Path.Combine(dir, "gameshare.json"), """{ "gameId": "testgame", "name": "TestGame", "version": "1.0", "volatile": ["saves/**"] }"""));
        await pc.Library.ScanAsync([pc.GamesRoot]);
        var inst = (await pc.Db.ListInstallationsAsync()).Single();
        var v1Hash = inst.ContentHash;
        await WriteAsync(pc.GameDir, "saves/mine.sav", "my progress");
        // A dropped file the player edited is not ours to delete.
        var dropped = Path.Combine(pc.GameDir, "Bin64", "engine.dll");

        var update = await pc.Downloads.StartUpdateAsync(inst.Id, v2.Manifest, v2.TorrentBytes!);
        Assert.Equal(DownloadKind.Update, update.Kind);
        Assert.Null(await pc.Db.FindInstalledAsync(v1Hash)); // not offered while it changes
        await Poll.DownloadStateAsync(pc, update.Id, DownloadState.Completed);

        var now = Assert.Single(await pc.Db.ListInstallationsAsync());
        Assert.Equal(inst.Id, now.Id); // the same installation, moved to the new version
        Assert.Equal(v2.Manifest.ContentHash, now.ContentHash);
        Assert.Equal(InstallationState.Installed, now.State);
        Assert.Equal(TestGame.HashTree(Path.Combine(v2Source.GamesRoot, "TestGame")).Where(kv => kv.Key != "gameshare.json").OrderBy(k => k.Key).ToList(),
            TestGame.HashTree(pc.GameDir).Where(kv => kv.Key != "gameshare.json" && !kv.Key.StartsWith("saves/")).OrderBy(k => k.Key).ToList());
        Assert.False(File.Exists(Path.Combine(pc.GameDir, "content", "sub", "tiny.txt"))); // dropped in version 2 and unmodified, so removed
        Assert.Equal("my progress", await File.ReadAllTextAsync(Path.Combine(pc.GameDir, "saves", "mine.sav")));
        Assert.True(File.Exists(dropped));

        var fetched = pc.Engine.Transfers.Single().GetStatus().SessionDownloaded;
        Assert.True(fetched <= 3L * v2.Manifest.PieceLength, $"update fetched {fetched} bytes, pieces are {v2.Manifest.PieceLength}");
    }

    [Fact]
    public async Task Update_keeps_an_obsolete_file_the_user_modified()
    {
        await using var v2Source = await Pc.StartAsync();
        v2Source.AddGame(customise: dir =>
        {
            TestGame.CorruptOneByte(Path.Combine(dir, "content", "big.pak"));
            File.Delete(Path.Combine(dir, "content", "sub", "tiny.txt"));
        });
        await v2Source.Library.ScanAsync([v2Source.GamesRoot]);
        await v2Source.Seeds.StartAllAsync();
        var v2 = (await v2Source.Library.ListAsync()).Single().Stored;

        await using var pc = await PcWithGameAsync();
        var inst = (await pc.Db.ListInstallationsAsync()).Single();
        var edited = Path.Combine(pc.GameDir, "content", "sub", "tiny.txt");
        await File.WriteAllTextAsync(edited, "edited by the player"); // no longer what version 1 shipped

        var update = await pc.Downloads.StartUpdateAsync(inst.Id, v2.Manifest, v2.TorrentBytes!);
        await Poll.DownloadStateAsync(pc, update.Id, DownloadState.Completed);

        Assert.Equal("edited by the player", await File.ReadAllTextAsync(edited));
        Assert.Equal(v2.Manifest.ContentHash, (await pc.Db.ListInstallationsAsync()).Single().ContentHash);
    }

    // ---------------- seeds skip re-hashing ----------------

    [Fact]
    public async Task A_restarted_seed_uses_stored_resume_data_instead_of_re_hashing_the_files()
    {
        var dir = TestGame.NewTempDir();
        var first = new ListLogger<TorrentEngine>();
        var pc = await Pc.StartAsync(existingDir: dir, engineLogger: first);
        pc.AddGame();
        await pc.Library.ScanAsync([pc.GamesRoot]);
        await pc.Seeds.StartAllAsync();
        await pc.Engine.Transfers.Single().WaitForCompletionAsync(TimeSpan.FromSeconds(30));
        Assert.True(first.Messages.Any(m => m.Contains("checking_files")), "first start: " + string.Join(" | ", first.Messages)); // the first start has to hash everything

        await pc.Seeds.SaveResumeDataAsync();
        var stored = (await pc.Db.ListInstallationsAsync()).Single().ResumeData;
        Assert.NotEmpty(stored!);
        await pc.ShutdownAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var second = new ListLogger<TorrentEngine>();
        await using var restarted = await Pc.StartAsync(existingDir: dir, engineLogger: second);
        await restarted.Seeds.StartAllAsync();
        await restarted.Engine.Transfers.Single().WaitForCompletionAsync(TimeSpan.FromSeconds(30));

        var seen = "restart: " + string.Join(" | ", second.Messages);
        Assert.True(second.Messages.Any(m => m.Contains("checking_resume_data")), seen);
        Assert.False(second.Messages.Any(m => m.Contains("checking_files")), seen);
        await pc.DisposeAsync();
    }

    [Fact]
    public async Task Resume_data_is_dropped_when_a_game_is_damaged_so_the_files_are_looked_at_again()
    {
        await using var pc = await PcWithGameAsync();
        await pc.Seeds.StartAllAsync();
        await pc.Engine.Transfers.Single().WaitForCompletionAsync(TimeSpan.FromSeconds(30));
        await pc.Seeds.SaveResumeDataAsync();
        var hash = (await pc.Library.ListAsync()).Single().Stored.Manifest.ContentHash;
        Assert.NotNull((await pc.Db.ListInstallationsAsync()).Single().ResumeData);

        TestGame.CorruptOneByte(Path.Combine(pc.GameDir, "content", "big.pak"));
        await pc.Library.CheckAsync(hash);

        Assert.Null((await pc.Db.ListInstallationsAsync()).Single().ResumeData);
    }
}
