namespace GameShare.Core.Tests;

/// <summary>
/// Games rewrite files inside their own folder: settings, saves, caches. Seeding must never treat that as damage to repair.
/// </summary>
public class SeedSafetyTests
{
    [Fact]
    public async Task Seeding_never_overwrites_a_file_the_game_changed_even_when_other_pcs_have_the_original()
    {
        await using var source = await Pc.StartAsync();
        source.AddGame();
        await source.Library.ScanAsync([source.GamesRoot]);
        await source.Seeds.StartAllAsync(); // a healthy copy exists on the LAN and could "repair" the change

        var pc = await Pc.StartAsync();
        pc.AddGame();
        await pc.Library.ScanAsync([pc.GamesRoot]);
        await pc.Seeds.StartAllAsync();
        var dir = pc.Dir;
        var settingsFile = Path.Combine(pc.GamesRoot, "TestGame", "content", "sub", "tiny.txt");
        var original = await File.ReadAllBytesAsync(settingsFile);
        await pc.ShutdownAsync();

        // The user plays. The game rewrites a small settings file, same size.
        var changedByGame = Enumerable.Repeat((byte)'X', original.Length).ToArray();
        await File.WriteAllBytesAsync(settingsFile, changedByGame);

        // The agent starts again and seeds again.
        await using var restarted = await Pc.StartAsync(existingDir: dir);
        await restarted.Seeds.StartAllAsync();
        await Task.Delay(3000); // long enough for a re-check and, in the buggy version, for the "repair" download

        Assert.Equal(changedByGame, await File.ReadAllBytesAsync(settingsFile));
        Assert.NotEqual(original, await File.ReadAllBytesAsync(settingsFile));
        Assert.True(restarted.Engine.Transfers.Single().UploadOnly);
        // Honest status: with a changed piece the seed is not complete and does not claim to offer everything.
        Assert.False(restarted.Engine.Transfers.Single().GetStatus().IsComplete);
    }

    [Fact]
    public async Task A_finished_download_switches_to_upload_only_so_it_cannot_overwrite_later_changes()
    {
        await using var source = await Pc.StartAsync();
        source.AddGame();
        await source.Library.ScanAsync([source.GamesRoot]);
        await source.Seeds.StartAllAsync();
        var offer = await source.OnlyKnownGameAsync();

        await using var pc = await Pc.StartAsync();
        var d = await pc.Downloads.StartInstallAsync(offer.Manifest, offer.TorrentBytes!, pc.GamesRoot);
        Assert.False(pc.Engine.Transfers.Single().UploadOnly); // installing must be allowed to write
        await Poll.DownloadStateAsync(pc, d.Id, Core.DownloadState.Completed);

        Assert.True(pc.Engine.Transfers.Single().UploadOnly);
    }
}
