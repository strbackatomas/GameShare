using System.Text;

namespace GameShare.Storage;

/// <summary>
/// One half of a .reg file: what goes to the machine (HKEY_LOCAL_MACHINE and the other shared hives, needs administrator rights)
/// or to the player (HKEY_CURRENT_USER, imported as the player, so it lands in their profile and not the administrator's).
/// </summary>
/// <param name="Text">A complete .reg file with only this half, ready for reg import. Null when the half is empty.</param>
/// <param name="Preview">One line per key and value, for the player to read before agreeing.</param>
/// <param name="Keys">Every key the half writes or deletes, short form (HKLM\…), deletions with a leading '-'.</param>
public sealed record RegPart(string? Text, IReadOnlyList<string> Preview, int ValueCount, IReadOnlyList<string> Keys);

/// <summary>Reads a game's .reg file and prepares it for this PC. The file comes from the game, so it is text to show and import, never code.</summary>
public static class RegFile
{
    private const string Header = "Windows Registry Editor Version 5.00";
    private const int MaxPreviewValue = 90;
    private const string Crlf = "\r\n"; // what regedit writes

    /// <summary>.reg files are UTF-16 with a byte order mark when regedit exported them, older ones are ANSI or UTF-8.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    /// <summary>
    /// Points the file at the folder the game is installed in on this PC. Paths are written with doubled backslashes inside
    /// quoted values, and exporters were not consistent about case (Starcraft has both C:\Games\Starcraft and C:\GAMES\STARCRAFT),
    /// so both spellings of the separator are replaced, ignoring case.
    /// </summary>
    public static string ReplacePath(string text, string originalPath, string installPath)
    {
        var from = originalPath.TrimEnd('\\');
        var to = installPath.TrimEnd('\\');
        if (from.Length == 0 || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return text;
        text = text.Replace(from.Replace("\\", "\\\\"), to.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase);
        return text.Replace(from, to, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Splits the file into what goes to the machine and what goes to the player.</summary>
    public static (RegPart Machine, RegPart User) Split(string text)
    {
        var machine = new Builder();
        var user = new Builder();
        Builder? current = null;
        string? key = null;
        string? pending = null; // a value continued over several lines with a trailing backslash

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (pending is not null)
            {
                pending += line.TrimStart();
                if (pending.EndsWith('\\')) { pending = pending[..^1]; continue; }
                current?.Value(pending);
                pending = null;
                continue;
            }
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith("Windows Registry Editor", StringComparison.OrdinalIgnoreCase) || line == "REGEDIT4") continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1];
                bool delete = name.StartsWith('-');
                key = Short(delete ? name[1..] : name);
                current = key.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) || key.Equals("HKCU", StringComparison.OrdinalIgnoreCase) ? user : machine;
                current.Key(line, key, delete);
                continue;
            }
            if (current is null) continue; // a value before any key, regedit would refuse it too
            if (line.EndsWith('\\')) { pending = line[..^1]; continue; }
            current.Value(line);
        }
        return (machine.Build(), user.Build());
    }

    /// <summary>HKEY_LOCAL_MACHINE\… as HKLM\…, the way the player sees keys elsewhere in GameShare.</summary>
    private static string Short(string key)
    {
        foreach (var (longName, shortName) in new[] { ("HKEY_LOCAL_MACHINE", "HKLM"), ("HKEY_CURRENT_USER", "HKCU"), ("HKEY_CLASSES_ROOT", "HKCR"), ("HKEY_USERS", "HKU"), ("HKEY_CURRENT_CONFIG", "HKCC") })
            if (key.StartsWith(longName, StringComparison.OrdinalIgnoreCase)) return shortName + key[longName.Length..];
        return key;
    }

    private sealed class Builder
    {
        private readonly StringBuilder _text = new();
        private readonly List<string> _preview = [];
        private readonly List<string> _keys = [];
        private int _values;
        private bool _any;

        public void Key(string line, string key, bool delete)
        {
            _any = true;
            _text.Append(Crlf).Append(line).Append(Crlf);
            _preview.Add(delete ? $"smazat {key}" : key);
            _keys.Add(delete ? "-" + key : key);
        }

        public void Value(string line)
        {
            _text.Append(line).Append(Crlf);
            _values++;
            var eq = ValueSeparator(line);
            var name = eq < 0 ? line : line[..eq];
            var value = eq < 0 ? "" : line[(eq + 1)..];
            if (value.Length > MaxPreviewValue) value = value[..MaxPreviewValue] + "…";
            _preview.Add($"    {(name == "@" ? "(výchozí)" : name.Trim('"'))} = {value}");
        }

        public RegPart Build() => _any ? new RegPart(Header + Crlf + _text, _preview, _values, _keys) : new RegPart(null, [], 0, []);

        /// <summary>The = between name and value, skipping any inside the quoted name.</summary>
        private static int ValueSeparator(string line)
        {
            if (!line.StartsWith('"')) return line.IndexOf('=');
            for (int i = 1; i < line.Length; i++)
            {
                if (line[i] == '\\') { i++; continue; }
                if (line[i] == '"') return line.IndexOf('=', i + 1);
            }
            return -1;
        }
    }
}
