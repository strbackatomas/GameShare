using System.Collections.Concurrent;
using GameShare.Core.Data;

namespace GameShare.Core.Tests;

/// <summary>Noticing which files a game rewrites while it is played, without anyone running a check.</summary>
public class GameChangeTrackerTests
{
    /// <summary>A PC with one installed game and a tracker that has started watching it.</summary>
    private sealed class Watched : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task _loop = Task.CompletedTask;

        public required Pc Pc { get; init; }
        public required GameChangeTracker Tracker { get; init; }
        public ConcurrentQueue<TrackedChange> Changes { get; } = new();

        public static async Task<Watched> StartAsync()
        {
            var pc = await Pc.StartAsync();
            pc.AddGame(bigFileBytes: 2_000_000);
            await pc.Library.ScanAsync([pc.GamesRoot]);

            var tracker = new GameChangeTracker(pc.Db, pc.Library, new ListLogger<GameChangeTracker>(), quiet: TimeSpan.FromMilliseconds(300));
            var watched = new Watched { Pc = pc, Tracker = tracker };
            tracker.Changed += (_, c) => watched.Changes.Enqueue(c);
            await tracker.SyncAsync(); // the watcher exists before the test touches any file
            watched._loop = tracker.RunAsync(TimeSpan.FromSeconds(1), watched._cts.Token);
            return watched;
        }

        public string BigFile => Path.Combine(Pc.GameDir, "content", "big.pak");
        public async Task<Installation> InstallationAsync() => (await Pc.Db.ListInstallationsAsync()).Single();
        public async Task<string> ContentHashAsync() => (await Pc.Library.ListAsync()).Single().Stored.Manifest.ContentHash;
        public async Task<ObservedChanges> SeenAsync() => Tracker.Get((await InstallationAsync()).Id);

        public async Task WriteAsync(string relative, string content)
        {
            var path = Path.Combine(Pc.GameDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { await _loop; } catch (OperationCanceledException) { /* stopped */ }
            await Pc.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_game_that_rewrites_a_content_file_is_noticed_marked_damaged_and_the_file_is_suggested()
    {
        await using var w = await Watched.StartAsync();

        TestGame.CorruptOneByte(w.BigFile); // a game rewriting a byte keeps the size, a scan cannot see that

        await Poll.UntilAsync(async () => (await w.InstallationAsync()).State == InstallationState.Invalid, "the game to be marked damaged", 20_000);
        var seen = await w.SeenAsync();
        Assert.Equal(["content/big.pak"], seen.Modified);
        Assert.Empty(seen.Added);
        Assert.Equal(["content/big.pak"], seen.SuggestedPatterns); // the file, not all of content/, which is the game itself
        var first = Assert.Single(w.Changes, c => c.BecameDamaged);
        Assert.Equal((await w.InstallationAsync()).Id, first.Installation.Id);
    }

    [Fact]
    public async Task A_file_the_game_creates_is_recorded_but_does_not_damage_the_game()
    {
        await using var w = await Watched.StartAsync();

        await w.WriteAsync("saves/slot1.sav", "progress");
        await w.WriteAsync("game.log", "not interesting"); // volatile by default

        await Poll.UntilAsync(async () => (await w.SeenAsync()).Added.Count > 0, "the new file to be noticed", 20_000);
        var seen = await w.SeenAsync();
        Assert.Equal(["saves/slot1.sav"], seen.Added);
        Assert.Empty(seen.Modified);
        Assert.Equal(["saves/**"], seen.SuggestedPatterns);
        Assert.Equal(InstallationState.Installed, (await w.InstallationAsync()).State);
        Assert.DoesNotContain(w.Changes, c => c.BecameDamaged);
    }

    [Fact]
    public async Task Writing_the_same_bytes_back_is_not_a_change()
    {
        await using var w = await Watched.StartAsync();

        await File.WriteAllBytesAsync(w.BigFile, await File.ReadAllBytesAsync(w.BigFile)); // touched, identical
        await w.WriteAsync("marker.dat", "x"); // tells us the folder was looked at

        await Poll.UntilAsync(async () => (await w.SeenAsync()).Added.Contains("marker.dat"), "the folder to be looked at", 20_000);
        await Task.Delay(500);
        Assert.Empty((await w.SeenAsync()).Modified);
        Assert.Equal(InstallationState.Installed, (await w.InstallationAsync()).State);
    }

    [Fact]
    public async Task A_file_that_is_deleted_counts_as_changed()
    {
        await using var w = await Watched.StartAsync();

        File.Delete(Path.Combine(w.Pc.GameDir, "content", "sub", "tiny.txt"));

        await Poll.UntilAsync(async () => (await w.InstallationAsync()).State == InstallationState.Invalid, "the game to be marked damaged", 20_000);
        Assert.Equal(["content/sub/tiny.txt"], (await w.SeenAsync()).Modified);
    }

    [Fact]
    public async Task Once_the_game_is_intact_again_what_was_noticed_is_forgotten()
    {
        await using var w = await Watched.StartAsync();
        var original = await File.ReadAllBytesAsync(w.BigFile);
        TestGame.CorruptOneByte(w.BigFile);
        await Poll.UntilAsync(async () => (await w.SeenAsync()).Modified.Count == 1, "the change to be noticed", 20_000);

        await File.WriteAllBytesAsync(w.BigFile, original);
        var changes = await w.Pc.Library.CheckAsync(await w.ContentHashAsync());

        Assert.True(changes.IsIntact);
        Assert.Equal(0, (await w.SeenAsync()).Count);
        Assert.Equal(InstallationState.Installed, (await w.InstallationAsync()).State);
    }

    [Fact]
    public async Task After_a_pattern_is_registered_the_files_it_covers_are_no_longer_reported()
    {
        await using var w = await Watched.StartAsync();
        await w.WriteAsync("saves/slot1.sav", "progress");
        await Poll.UntilAsync(async () => (await w.SeenAsync()).Added.Count > 0, "the new file to be noticed", 20_000);

        await w.Pc.Library.RescanAsync(w.Pc.GameDir, ["saves/**"]); // the registered game replaces the old record
        await w.Tracker.SyncAsync();
        Assert.Equal(0, (await w.SeenAsync()).Count);

        await w.WriteAsync("saves/slot2.sav", "more progress");
        await w.WriteAsync("other/new.dat", "x");

        await Poll.UntilAsync(async () => (await w.SeenAsync()).Added.Contains("other/new.dat"), "the folder to be looked at", 20_000);
        Assert.Equal(["other/new.dat"], (await w.SeenAsync()).Added);
    }

    [Fact]
    public async Task A_burst_of_writes_is_reported_once_the_folder_goes_quiet()
    {
        await using var w = await Watched.StartAsync();

        for (int i = 0; i < 20; i++)
        {
            await w.WriteAsync($"cache/block{i}.bin", "x");
            await Task.Delay(30);
        }

        await Poll.UntilAsync(async () => (await w.SeenAsync()).Added.Count == 20, "all twenty files to be noticed", 20_000);
        Assert.Equal(["cache/**"], (await w.SeenAsync()).SuggestedPatterns);
        Assert.InRange(w.Changes.Count, 1, 5); // a few looks at most, not one per file
    }
}
