using GameShare.Protocol;

namespace GameShare.Agent.Tests;

/// <summary>Piece maps come from other PCs, so unpacking must survive garbage.</summary>
public class PieceMapCodecTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(2048)]
    public void A_map_survives_a_round_trip_whatever_the_piece_count(int count)
    {
        var have = Enumerable.Range(0, count).Select(i => i % 3 != 0).ToArray();

        Assert.Equal(have, PieceMapCodec.Unpack(PieceMapCodec.Pack(have)));
    }

    [Fact]
    public void A_full_map_has_every_piece()
    {
        Assert.All(PieceMapCodec.Unpack(PieceMapCodec.Full(100)), Assert.True);
        Assert.Equal(100, PieceMapCodec.Unpack(PieceMapCodec.Full(100)).Length);
    }

    [Fact]
    public void Garbage_from_another_pc_is_rejected_not_trusted()
    {
        Assert.Throws<InvalidDataException>(() => PieceMapCodec.Unpack(new PieceMapDto(8, "not base64!!")));
        Assert.Throws<InvalidDataException>(() => PieceMapCodec.Unpack(new PieceMapDto(16, Convert.ToBase64String(new byte[1]))));
        Assert.Throws<InvalidDataException>(() => PieceMapCodec.Unpack(new PieceMapDto(-1, "")));
        Assert.Throws<InvalidDataException>(() => PieceMapCodec.Unpack(new PieceMapDto(int.MaxValue, "")));
    }
}
