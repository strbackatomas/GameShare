using GameShare.Core.Data;
using GameShare.Protocol;
using GameShare.Storage;
using GameShare.Torrent;
using Microsoft.Data.Sqlite;

namespace GameShare.Core.Tests;

public sealed class GameShareDbTests : IAsyncLifetime
{
    private string _dir = "";
    private string DbPath => Path.Combine(_dir, "gameshare.db");

    public Task InitializeAsync() { _dir = TestGame.NewTempDir(); return Task.CompletedTask; }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools(); // release the file so the folder can be deleted
        TestGame.DeleteQuietly(_dir);
        return Task.CompletedTask;
    }

    private static async Task<(GameManifest Manifest, byte[] Torrent)> SampleAsync(int seed = 1)
    {
        using var game = new TestGame(seed: seed);
        var (manifest, scan) = await ManifestBuilder.ScanAndBuildAsync(game.GameDir);
        var torrent = TorrentBuilder.Build(scan);
        return (manifest with { TorrentInfoHash = torrent.InfoHash }, torrent.TorrentBytes);
    }

    [Fact]
    public async Task Fresh_database_is_created_at_the_latest_schema_version_and_reopening_is_harmless()
    {
        var db = await GameShareDb.OpenAsync(DbPath);
        var version = await db.GetSchemaVersionAsync();
        Assert.True(version >= 1);

        var again = await GameShareDb.OpenAsync(DbPath);
        Assert.Equal(version, await again.GetSchemaVersionAsync());
    }

    [Fact]
    public async Task Database_from_a_newer_build_is_refused_with_a_clear_message()
    {
        await GameShareDb.OpenAsync(DbPath);
        await using (var conn = new SqliteConnection($"Data Source={DbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 999;";
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => GameShareDb.OpenAsync(DbPath));
        Assert.Contains("999", ex.Message);
        Assert.Contains("Update GameShare", ex.Message);
    }

    [Fact]
    public async Task Manifest_round_trips_with_its_torrent_and_a_missing_torrent_never_erases_a_stored_one()
    {
        var db = await GameShareDb.OpenAsync(DbPath);
        var (manifest, torrent) = await SampleAsync();

        await db.SaveManifestAsync(manifest, torrent);
        await db.SaveManifestAsync(manifest, null); // e.g. re-announced by a peer without the torrent

        var stored = await db.GetManifestAsync(manifest.ContentHash);
        Assert.NotNull(stored);
        Assert.Equal(manifest.Files, stored.Manifest.Files);
        Assert.Equal(manifest.TorrentInfoHash, stored.Manifest.TorrentInfoHash);
        Assert.Equal(torrent, stored.TorrentBytes);
        Assert.Single(await db.ListManifestsAsync());
        Assert.Null(await db.GetManifestAsync(new string('0', 64)));
    }

    [Fact]
    public async Task Versions_of_one_game_are_grouped_by_game_id()
    {
        var db = await GameShareDb.OpenAsync(DbPath);
        var (v1, t1) = await SampleAsync(seed: 1);
        var (v2, t2) = await SampleAsync(seed: 2);
        await db.SaveManifestAsync(v1 with { Version = "0.37" }, t1);
        await db.SaveManifestAsync(v2 with { Version = "0.38" }, t2);

        var versions = await db.ListManifestsForGameAsync(v1.GameId);

        Assert.Equal(2, versions.Count);
        Assert.Equal(["0.37", "0.38"], versions.Select(v => v.Manifest.Version).Order());
        Assert.Empty(await db.ListManifestsForGameAsync("some-other-game"));
    }

    [Fact]
    public async Task Installation_lifecycle_and_only_verified_installs_count_as_installed()
    {
        var db = await GameShareDb.OpenAsync(DbPath);
        var (manifest, torrent) = await SampleAsync();
        await db.SaveManifestAsync(manifest, torrent);

        var inst = await db.AddInstallationAsync(manifest.ContentHash, @"D:\Games\TestGame", InstallationState.Invalid);
        Assert.Null(inst.VerifiedAt);
        Assert.Null(await db.FindInstalledAsync(manifest.ContentHash)); // not verified, so not offered and not launchable

        await db.SetInstallationStateAsync(inst.Id, InstallationState.Installed);
        var installed = await db.FindInstalledAsync(manifest.ContentHash);
        Assert.NotNull(installed?.VerifiedAt);

        await db.SetInstallationStateAsync(inst.Id, InstallationState.Invalid); // a later check found corruption
        Assert.Null(await db.FindInstalledAsync(manifest.ContentHash));
        Assert.Equal(InstallationState.Invalid, (await db.GetInstallationAsync(inst.Id))!.State);

        await db.SetSeedingAsync(inst.Id, false);
        Assert.False((await db.GetInstallationAsync(inst.Id))!.Seeding);

        await db.DeleteInstallationAsync(inst.Id);
        Assert.Empty(await db.ListInstallationsAsync());
    }

    [Fact]
    public async Task Same_folder_cannot_be_registered_twice_ignoring_case()
    {
        var db = await GameShareDb.OpenAsync(DbPath);
        var (manifest, torrent) = await SampleAsync();
        await db.SaveManifestAsync(manifest, torrent);
        await db.AddInstallationAsync(manifest.ContentHash, @"D:\Games\TestGame", InstallationState.Installed);

        await Assert.ThrowsAsync<SqliteException>(() =>
            db.AddInstallationAsync(manifest.ContentHash, @"d:\games\testgame", InstallationState.Installed));
    }

    [Fact]
    public async Task Installation_of_an_unknown_manifest_is_rejected_by_the_database()
    {
        var db = await GameShareDb.OpenAsync(DbPath);
        await Assert.ThrowsAsync<SqliteException>(() =>
            db.AddInstallationAsync(new string('a', 64), @"D:\Games\Ghost", InstallationState.Installed));
    }

    [Fact]
    public async Task Download_keeps_resume_data_and_progress_across_reopening_the_database()
    {
        var db = await GameShareDb.OpenAsync(DbPath);
        var (manifest, torrent) = await SampleAsync();
        await db.SaveManifestAsync(manifest, torrent);

        var d = await db.CreateDownloadAsync(manifest.ContentHash, @"D:\Games");
        Assert.Equal(DownloadState.Queued, d.State);
        Assert.True(d.IsActive);
        await db.UpdateDownloadAsync(d.Id, DownloadState.Downloading);
        await db.SaveResumeDataAsync(d.Id, [1, 2, 3, 4], bytesDone: 12345);

        var reopened = await GameShareDb.OpenAsync(DbPath); // "restart of the agent"
        var back = await reopened.GetDownloadAsync(d.Id);

        Assert.Equal(DownloadState.Downloading, back!.State);
        Assert.Equal([1, 2, 3, 4], back.ResumeData);
        Assert.Equal(12345, back.BytesDone);
        Assert.Equal(@"D:\Games", back.TargetRoot);
    }

    [Fact]
    public async Task Only_one_unfinished_download_per_version_but_a_finished_one_does_not_block_a_new_one()
    {
        var db = await GameShareDb.OpenAsync(DbPath);
        var (manifest, torrent) = await SampleAsync();
        await db.SaveManifestAsync(manifest, torrent);

        var first = await db.CreateDownloadAsync(manifest.ContentHash, @"D:\Games");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.CreateDownloadAsync(manifest.ContentHash, @"E:\Games"));
        Assert.Contains("already in progress", ex.Message);

        await db.UpdateDownloadAsync(first.Id, DownloadState.Failed, error: "disk full");
        var second = await db.CreateDownloadAsync(manifest.ContentHash, @"E:\Games"); // retry after failure is allowed

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("disk full", (await db.GetDownloadAsync(first.Id))!.Error);
        Assert.False((await db.GetDownloadAsync(first.Id))!.IsActive);
    }

    [Fact]
    public async Task Updating_or_deleting_downloads_works_and_a_missing_id_is_an_error_not_silence()
    {
        var db = await GameShareDb.OpenAsync(DbPath);
        var (manifest, torrent) = await SampleAsync();
        await db.SaveManifestAsync(manifest, torrent);
        var d = await db.CreateDownloadAsync(manifest.ContentHash, @"D:\Games");

        await db.DeleteDownloadAsync(d.Id);

        Assert.Null(await db.GetDownloadAsync(d.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => db.UpdateDownloadAsync(d.Id, DownloadState.Paused));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => db.SetInstallationStateAsync(9999, InstallationState.Installed));
    }

    [Fact]
    public async Task Settings_are_stored_and_overwritten()
    {
        var db = await GameShareDb.OpenAsync(DbPath);

        Assert.Null(await db.GetSettingAsync("upload.limit"));
        await db.SetSettingAsync("upload.limit", "80");
        await db.SetSettingAsync("upload.limit", "120");

        Assert.Equal("120", await db.GetSettingAsync("upload.limit"));
    }

    [Fact]
    public async Task Concurrent_writers_do_not_lose_updates_or_hit_lock_errors()
    {
        var db = await GameShareDb.OpenAsync(DbPath);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => db.SetSettingAsync($"key{i}", i.ToString())));

        for (int i = 0; i < 40; i++) Assert.Equal(i.ToString(), await db.GetSettingAsync($"key{i}"));
    }
}
