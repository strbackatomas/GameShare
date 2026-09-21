using TorrentSharp.Wrap;

namespace GameShare.Torrent.Tests;

public class TorrentBuilderTests
{
    [Fact]
    public async Task Built_torrent_is_parsed_by_libtorrent_with_the_same_info_hash()
    {
        using var game = new TestGame();

        var built = await TorrentBuilder.BuildAsync(game.GameDir, pieceLength: 1 << 20);
        var meta = new TorrentInfo(built.TorrentBytes).Metadata!;

        Assert.NotNull(meta);
        Assert.Equal(built.InfoHash, meta.InfoHash!.ToLowerInvariant());
        Assert.Equal(built.TotalSize, meta.TotalSize);
        Assert.Equal(5, meta.TotalFiles);
        Assert.Equal("TestGame", meta.Name);
    }

    [Fact]
    public async Task Same_content_gives_same_info_hash_on_different_machines()
    {
        using var a = new TestGame();
        using var b = new TestGame(); // different temp parent, identical content and directory name

        var ha = await TorrentBuilder.BuildAsync(a.GameDir);
        var hb = await TorrentBuilder.BuildAsync(b.GameDir);

        Assert.Equal(ha.InfoHash, hb.InfoHash);
    }

    [Fact]
    public async Task Changed_content_gives_different_info_hash()
    {
        using var a = new TestGame(seed: 1);
        using var b = new TestGame(seed: 2);

        var ha = await TorrentBuilder.BuildAsync(a.GameDir);
        var hb = await TorrentBuilder.BuildAsync(b.GameDir);

        Assert.NotEqual(ha.InfoHash, hb.InfoHash);
    }

    [Theory]
    [InlineData(0L, 1 << 20)]
    [InlineData(500L << 20, 1 << 20)]
    [InlineData(65L << 30, 16 << 20)]
    [InlineData(4L << 30, 2 << 20)]
    public void Piece_length_scales_with_game_size(long size, int expected) =>
        Assert.Equal(expected, GameShare.Storage.ContentScanner.ChoosePieceLength(size));

    [Fact]
    public async Task Empty_directory_is_rejected_with_a_clear_message()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gameshare-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TorrentBuilder.BuildAsync(dir));
            Assert.Contains("contains no files", ex.Message);
        }
        finally { Directory.Delete(dir); }
    }
}
