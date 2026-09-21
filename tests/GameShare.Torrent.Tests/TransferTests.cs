using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.Torrent.Tests;

/// <summary>
/// End-to-end transfers between engines in one process. Peers find each other through
/// Local Service Discovery, exactly as PCs on a LAN will. No tracker, no DHT.
/// </summary>
public class TransferTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>Engine options for running several engines on one machine.</summary>
    private static TorrentEngineOptions Local(int? up = null, int? down = null) => new()
    {
        ListenPort = 0,
        AllowMultipleConnectionsPerIp = true,
        MaxUploadBytesPerSecond = up,
        MaxDownloadBytesPerSecond = down,
    };

    /// <summary>A game on disk, its manifest and torrent, and an engine that already seeds it.</summary>
    private sealed class Source : IDisposable
    {
        public required TestGame Game { get; init; }
        public required GameManifest Manifest { get; init; }
        public required BuiltTorrent Torrent { get; init; }
        public required TorrentEngine Engine { get; init; }
        public required TorrentTransfer Transfer { get; init; }

        public static async Task<Source> StartAsync(long bigFileBytes = 20_000_000, int? uploadLimit = null)
        {
            var game = new TestGame(largeFileBytes: bigFileBytes);
            var (manifest, scan) = await ManifestBuilder.ScanAndBuildAsync(game.GameDir);
            var torrent = TorrentBuilder.Build(scan);
            manifest = manifest with { TorrentInfoHash = torrent.InfoHash };

            var engine = new TorrentEngine(Local(up: uploadLimit));
            var transfer = engine.Add(torrent.TorrentBytes, game.ParentDir);
            transfer.Start();
            await transfer.WaitForCompletionAsync(Timeout); // existing files are recognised as complete
            return new Source { Game = game, Manifest = manifest, Torrent = torrent, Engine = engine, Transfer = transfer };
        }

        public void Dispose() { Engine.Dispose(); Game.Dispose(); }
    }

    private static string InstalledDir(string target, GameManifest m) => Path.Combine(target, m.FolderName);

    [Fact]
    public async Task Download_lands_directly_in_target_and_passes_full_manifest_verification()
    {
        using var src = await Source.StartAsync();
        var target = TestGame.NewTempDir();
        try
        {
            using var leecher = new TorrentEngine(Local());
            var t = leecher.Add(src.Torrent.TorrentBytes, target);
            t.Start();
            await t.WaitForCompletionAsync(Timeout);

            var result = await ManifestVerifier.VerifyAsync(src.Manifest, InstalledDir(target, src.Manifest), VerifyMode.Full);
            Assert.True(result.IsValid, result.ToString());
            Assert.Empty(result.Extra); // no archive, no partial-file leftovers
            Assert.Equal(TestGame.HashTree(src.Game.GameDir), TestGame.HashTree(InstalledDir(target, src.Manifest)));
            Assert.True(t.GetStatus().SessionDownloaded >= src.Manifest.TotalSize);
        }
        finally { TestGame.DeleteQuietly(target); }
    }

    /// <param name="saveResumeData">
    /// true: clean shutdown that persisted resume data. false: crash, so the next run must rediscover progress by re-hashing files.
    /// </param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Interrupted_download_continues_without_refetching_verified_data(bool saveResumeData)
    {
        using var src = await Source.StartAsync();
        var target = TestGame.NewTempDir();
        try
        {
            byte[]? resume = null;
            double progressBeforeRestart;
            using (var first = new TorrentEngine(Local(down: 2_000_000)))
            {
                var t = first.Add(src.Torrent.TorrentBytes, target);
                t.Start();
                await t.WaitUntilAsync(s => s.Progress >= 0.25, "25% done", Timeout);
                if (saveResumeData)
                {
                    t.Stop();
                    resume = await t.SaveResumeDataAsync();
                    Assert.NotEmpty(resume);
                }
                progressBeforeRestart = t.GetStatus().Progress;
            } // engine disposed: the "process" is gone

            using var second = new TorrentEngine(Local());
            var t2 = second.Add(src.Torrent.TorrentBytes, target, resume);
            t2.Start();
            await t2.WaitForCompletionAsync(Timeout);

            Assert.True(progressBeforeRestart >= 0.25);
            Assert.True(
                t2.GetStatus().SessionDownloaded < src.Manifest.TotalSize * 0.9,
                $"second run fetched {t2.GetStatus().SessionDownloaded} of {src.Manifest.TotalSize} bytes, so earlier progress was lost");

            var result = await ManifestVerifier.VerifyAsync(src.Manifest, InstalledDir(target, src.Manifest), VerifyMode.Full);
            Assert.True(result.IsValid, result.ToString());
        }
        finally { TestGame.DeleteQuietly(target); }
    }

    [Fact]
    public async Task Corrupted_file_is_detected_and_repaired_by_recheck()
    {
        using var src = await Source.StartAsync();
        var target = TestGame.NewTempDir();
        try
        {
            using var leecher = new TorrentEngine(Local());
            var t = leecher.Add(src.Torrent.TorrentBytes, target);
            t.Start();
            await t.WaitForCompletionAsync(Timeout);

            var dir = InstalledDir(target, src.Manifest);
            TestGame.CorruptOneByte(Path.Combine(dir, "content", "big.pak"));
            Assert.False((await ManifestVerifier.VerifyAsync(src.Manifest, dir, VerifyMode.Full)).IsValid);

            t.ForceRecheck();

            // Recheck finds the bad piece and re-fetches it from the seeder. Poll until the tree is valid again.
            var deadline = DateTime.UtcNow + Timeout;
            VerificationResult result;
            do
            {
                await Task.Delay(500);
                result = await ManifestVerifier.VerifyAsync(src.Manifest, dir, VerifyMode.Full);
            } while (!result.IsValid && DateTime.UtcNow < deadline);

            Assert.True(result.IsValid, $"still invalid after recheck: {result}; transfer: {t.GetStatus()}");
        }
        finally { TestGame.DeleteQuietly(target); }
    }

    [Fact]
    public async Task Third_client_downloads_from_both_the_original_seeder_and_the_second_client()
    {
        using var a = await Source.StartAsync(bigFileBytes: 24_000_000, uploadLimit: 3_000_000);
        var dirB = TestGame.NewTempDir();
        var dirC = TestGame.NewTempDir();
        try
        {
            using var engineB = new TorrentEngine(Local(up: 3_000_000));
            var b = engineB.Add(a.Torrent.TorrentBytes, dirB);
            b.Start();
            await b.WaitForCompletionAsync(Timeout);

            long aBefore = a.Transfer.GetStatus().SessionUploaded;
            long bBefore = b.GetStatus().SessionUploaded;

            using var engineC = new TorrentEngine(Local());
            var c = engineC.Add(a.Torrent.TorrentBytes, dirC);
            c.Start();
            await c.WaitForCompletionAsync(Timeout);

            long fromA = a.Transfer.GetStatus().SessionUploaded - aBefore;
            long fromB = b.GetStatus().SessionUploaded - bBefore;
            Assert.True(fromA > 0, "original seeder served nothing to the third client");
            Assert.True(fromB > 0, "second client served nothing to the third client, so swarming did not happen");

            var result = await ManifestVerifier.VerifyAsync(a.Manifest, InstalledDir(dirC, a.Manifest), VerifyMode.Full);
            Assert.True(result.IsValid, result.ToString());
        }
        finally { TestGame.DeleteQuietly(dirB); TestGame.DeleteQuietly(dirC); }
    }

    /// <summary>
    /// Attaching the next version's torrent over an installed older version keeps every piece that still matches,
    /// so an update costs roughly the changed data. This also holds when a new file shifts the piece layout,
    /// because libtorrent re-slices the existing files under the new layout.
    /// </summary>
    [Theory]
    [InlineData("flip-one-byte-in-large-file")]
    [InlineData("add-small-file-that-sorts-first")]
    public async Task Update_fetches_only_the_pieces_touched_by_the_change(string change)
    {
        using var v1 = await Source.StartAsync();

        var v2Parent = TestGame.NewTempDir();
        var target = TestGame.NewTempDir();
        try
        {
            var v2Dir = Path.Combine(v2Parent, "TestGame");
            TestGame.CopyDirectory(v1.Game.GameDir, v2Dir);
            if (change == "flip-one-byte-in-large-file")
                TestGame.CorruptOneByte(Path.Combine(v2Dir, "content", "big.pak"));
            else
                await File.WriteAllTextAsync(Path.Combine(v2Dir, "0-new.txt"), "a small new file that sorts before every other file");
            var scan2 = await ContentScanner.ScanAsync(v2Dir);
            var torrent2 = TorrentBuilder.Build(scan2);
            var manifest2 = ManifestBuilder.Build(scan2, torrentInfoHash: torrent2.InfoHash);
            Assert.NotEqual(v1.Manifest.ContentHash, manifest2.ContentHash);

            using var v2Seeder = new TorrentEngine(Local());
            var seed2 = v2Seeder.Add(torrent2.TorrentBytes, v2Parent);
            seed2.Start();
            await seed2.WaitForCompletionAsync(Timeout);

            // This PC has version 1 installed. Attaching the version 2 torrent re-hashes it and keeps every matching piece.
            TestGame.CopyDirectory(v1.Game.GameDir, InstalledDir(target, manifest2));
            using var client = new TorrentEngine(Local());
            var update = client.Add(torrent2.TorrentBytes, target);
            update.Start();
            await update.WaitForCompletionAsync(Timeout);

            long fetched = update.GetStatus().SessionDownloaded;
            Assert.True(fetched <= 2L * torrent2.PieceLength,
                $"update fetched {fetched} bytes for a one-byte change with {torrent2.PieceLength} byte pieces");
            var result = await ManifestVerifier.VerifyAsync(manifest2, InstalledDir(target, manifest2), VerifyMode.Full);
            Assert.True(result.IsValid, result.ToString());
        }
        finally { TestGame.DeleteQuietly(v2Parent); TestGame.DeleteQuietly(target); }
    }

    [Fact]
    public async Task Lan_only_engine_refuses_peers_outside_allowed_ranges()
    {
        using var src = await Source.StartAsync(bigFileBytes: 5_000_000);
        var okDir = TestGame.NewTempDir();
        var blockedDir = TestGame.NewTempDir();
        try
        {
            // Documentation range 203.0.113.0/24 never matches this machine, standing in for "the internet".
            var blockedOptions = Local() with { AllowedRanges = [new IpRange("203.0.113.0", "203.0.113.255")] };
            using var blockedEngine = new TorrentEngine(blockedOptions);
            using var controlEngine = new TorrentEngine(Local());

            var blocked = blockedEngine.Add(src.Torrent.TorrentBytes, blockedDir);
            var control = controlEngine.Add(src.Torrent.TorrentBytes, okDir);
            blocked.Start();
            control.Start();

            // The control shows discovery works right now, so silence from the other one is the filter, not luck.
            await control.WaitForCompletionAsync(Timeout);
            await Task.Delay(3000);

            var s = blocked.GetStatus();
            Assert.Equal(0, s.SessionDownloaded);
            Assert.Equal(0, s.PeerCount);
            Assert.False(s.IsComplete);
        }
        finally { TestGame.DeleteQuietly(okDir); TestGame.DeleteQuietly(blockedDir); }
    }
}
