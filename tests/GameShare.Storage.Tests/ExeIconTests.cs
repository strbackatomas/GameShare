using System.Buffers.Binary;

namespace GameShare.Storage.Tests;

/// <summary>The picture next to a game's name comes out of its program. The program is untrusted input, so a bad one must give nothing, not throw.</summary>
public class ExeIconTests
{
    private static readonly string Notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
    private static readonly string DemoGame = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "TestGames", "DemoGame", "DemoGame.exe");

    [Fact]
    public void The_largest_image_of_a_programs_icon_comes_out_as_a_valid_ico_file()
    {
        Assert.True(File.Exists(Notepad), Notepad); // GameShare runs on Windows only

        var ico = ExeIcon.Extract(Notepad);

        Assert.NotNull(ico);
        Assert.Equal([0, 0, 1, 0, 1, 0], ico[..6]);                          // ICONDIR with one image
        Assert.Equal(0, ico[6]);                                             // 0 means 256 px wide
        var size = BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(14));
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(18));
        Assert.Equal(22u, offset);
        Assert.Equal(ico.Length, (int)(offset + size));
    }

    [Fact]
    public void A_program_without_an_icon_has_none()
    {
        Assert.True(File.Exists(DemoGame), DemoGame);

        Assert.Null(ExeIcon.Extract(DemoGame));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(1000)]
    public void A_file_that_is_not_a_program_gives_nothing(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        if (length >= 2) { bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; }

        Assert.Null(ExeIcon.Extract(new MemoryStream(bytes)));
    }

    [Fact]
    public void A_cut_off_or_corrupted_program_gives_nothing_and_never_throws()
    {
        Assert.True(File.Exists(Notepad), Notepad); // GameShare runs on Windows only
        var original = File.ReadAllBytes(Notepad);
        var rng = new Random(7);

        foreach (var cut in new[] { 64, 400, original.Length / 2, original.Length - 1 })
            _ = ExeIcon.Extract(new MemoryStream(original[..cut]));
        for (int i = 0; i < 200; i++)
        {
            var damaged = (byte[])original.Clone();
            for (int j = 0; j < 20; j++) damaged[rng.Next(damaged.Length)] = (byte)rng.Next(256);
            _ = ExeIcon.Extract(new MemoryStream(damaged)); // whatever comes out, nothing may escape
        }
    }

    [Fact]
    public void A_missing_file_gives_nothing() => Assert.Null(ExeIcon.Extract(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")));
}
