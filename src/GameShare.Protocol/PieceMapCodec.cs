namespace GameShare.Protocol;

/// <summary>Packs which pieces of a game a PC has into the small form sent between PCs, and unpacks it.</summary>
public static class PieceMapCodec
{
    public static PieceMapDto Pack(IReadOnlyList<bool> have)
    {
        var bytes = new byte[(have.Count + 7) / 8];
        for (int i = 0; i < have.Count; i++)
            if (have[i]) bytes[i / 8] |= (byte)(1 << (i % 8));
        return new PieceMapDto(have.Count, Convert.ToBase64String(bytes));
    }

    /// <summary>A map claiming every piece, for a PC that has the whole game.</summary>
    public static PieceMapDto Full(int pieceCount) => Pack(Enumerable.Repeat(true, pieceCount).ToArray());

    /// <exception cref="InvalidDataException">The map is malformed or absurdly large. Maps come from other PCs and are never trusted.</exception>
    public static bool[] Unpack(PieceMapDto map, int maxPieces = 1_000_000)
    {
        if (map.PieceCount < 0 || map.PieceCount > maxPieces)
            throw new InvalidDataException($"Piece map claims {map.PieceCount} pieces.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(map.Bitfield); }
        catch (FormatException ex) { throw new InvalidDataException("Piece map is not valid base64.", ex); }
        if (bytes.Length != (map.PieceCount + 7) / 8)
            throw new InvalidDataException($"Piece map has {bytes.Length} bytes for {map.PieceCount} pieces.");

        var have = new bool[map.PieceCount];
        for (int i = 0; i < have.Length; i++) have[i] = (bytes[i / 8] & (1 << (i % 8))) != 0;
        return have;
    }
}
