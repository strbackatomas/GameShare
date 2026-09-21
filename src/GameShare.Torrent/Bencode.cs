using System.Text;

namespace GameShare.Torrent;

/// <summary>
/// Minimal bencode writer. Only what is needed to emit a v1 .torrent file.
/// Dictionary keys are emitted in raw byte order as the spec requires.
/// </summary>
internal static class Bencode
{
    public static void WriteInt(Stream s, long value)
    {
        s.WriteByte((byte)'i');
        WriteAscii(s, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        s.WriteByte((byte)'e');
    }

    public static void WriteBytes(Stream s, ReadOnlySpan<byte> value)
    {
        WriteAscii(s, value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        s.WriteByte((byte)':');
        s.Write(value);
    }

    public static void WriteString(Stream s, string value) => WriteBytes(s, Encoding.UTF8.GetBytes(value));

    public static void BeginList(Stream s) => s.WriteByte((byte)'l');
    public static void BeginDict(Stream s) => s.WriteByte((byte)'d');
    public static void End(Stream s) => s.WriteByte((byte)'e');

    private static void WriteAscii(Stream s, string value)
    {
        Span<byte> buf = stackalloc byte[value.Length];
        Encoding.ASCII.GetBytes(value, buf);
        s.Write(buf);
    }
}
