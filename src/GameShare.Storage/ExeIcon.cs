using System.Buffers.Binary;

namespace GameShare.Storage;

/// <summary>
/// Reads the icon out of a Windows program, the one Explorer shows, and returns it as a one-image .ico file.
/// Plain managed parsing of the PE resource section, so it needs no Windows API and never runs the program.
/// The program comes from a game folder and possibly another PC, so every offset is bounds-checked and a malformed file gives null.
/// </summary>
public static class ExeIcon
{
    private const int RtIcon = 3;
    private const int RtGroupIcon = 14;
    private const int MaxFileBytes = 512 * 1024 * 1024;
    private const int MaxResourceBytes = 4 * 1024 * 1024;

    /// <returns>The bytes of a .ico file with the largest image of the program's first icon, or null when it has none or cannot be read.</returns>
    public static byte[]? Extract(string exePath)
    {
        try
        {
            using var file = File.OpenRead(exePath);
            if (file.Length is < 64 or > MaxFileBytes) return null;
            return Extract(file);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static byte[]? Extract(Stream file)
    {
        try { return new Reader(file).Icon(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException or EndOfStreamException or InvalidDataException) { return null; }
    }

    private sealed class Reader(Stream file)
    {
        private readonly List<(uint Va, uint Size, uint Raw, uint RawSize)> _sections = [];
        private uint _resourceRva;
        private uint _resourceRaw;

        public byte[]? Icon()
        {
            if (!ReadHeaders()) return null;

            var groups = Directory(_resourceRaw, RtGroupIcon);
            if (groups is null) return null;
            // The first group is the program's own icon. Each id has one entry per language, the first will do.
            var groupData = FirstLeafData(groups.Value);
            if (groupData is null) return null;
            var group = groupData;
            if (group.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(2)) != 1) return null;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(4));
            if (count == 0 || group.Length < 6 + count * 14) return null;

            // Largest image, then the most colours. A width byte of 0 means 256.
            int best = -1, bestSize = -1, bestBits = -1;
            for (int i = 0; i < count; i++)
            {
                var e = group.AsSpan(6 + i * 14, 14);
                int size = e[0] == 0 ? 256 : e[0];
                int bits = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
                if (size > bestSize || size == bestSize && bits > bestBits) { best = i; bestSize = size; bestBits = bits; }
            }
            var entry = group.AsSpan(6 + best * 14, 14).ToArray();
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(12));

            var icons = Directory(_resourceRaw, RtIcon);
            if (icons is null) return null;
            var imageDir = Entry(icons.Value, id);
            if (imageDir is null) return null;
            var image = FirstLeafData(imageDir.Value);
            if (image is null || image.Length == 0) return null;

            // ICONDIR + one ICONDIRENTRY (the group entry with a file offset instead of the resource id) + the image.
            var ico = new byte[6 + 16 + image.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), 1);
            entry.AsSpan(0, 12).CopyTo(ico.AsSpan(6));
            BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(6 + 8), (uint)image.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(6 + 12), 22);
            image.CopyTo(ico, 22);
            return ico;
        }

        private bool ReadHeaders()
        {
            var dos = Read(0, 64);
            if (dos is null || dos[0] != 'M' || dos[1] != 'Z') return false;
            uint pe = BinaryPrimitives.ReadUInt32LittleEndian(dos.AsSpan(0x3C));
            var coff = Read(pe, 24);
            if (coff is null || coff[0] != 'P' || coff[1] != 'E' || coff[2] != 0 || coff[3] != 0) return false;
            int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(6));
            int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(20));
            var optional = Read(pe + 24, optionalSize);
            if (optional is null || optionalSize < 2) return false;

            // PE32 has the data directories at 96, PE32+ at 112. The resource table is directory 2.
            int dirs = BinaryPrimitives.ReadUInt16LittleEndian(optional) switch { 0x10B => 96, 0x20B => 112, _ => -1 };
            if (dirs < 0 || optionalSize < dirs + 3 * 8) return false;
            _resourceRva = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(dirs + 16));
            if (_resourceRva == 0) return false;

            var table = Read(pe + 24 + (uint)optionalSize, sectionCount * 40);
            if (table is null) return false;
            for (int i = 0; i < sectionCount; i++)
            {
                var s = table.AsSpan(i * 40, 40);
                _sections.Add((BinaryPrimitives.ReadUInt32LittleEndian(s[12..]), BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[20..]), BinaryPrimitives.ReadUInt32LittleEndian(s[16..])));
            }
            var raw = ToRaw(_resourceRva);
            if (raw is null) return false;
            _resourceRaw = raw.Value;
            return true;
        }

        /// <summary>The subdirectory for a type or id below a resource directory, as a file offset.</summary>
        private uint? Directory(uint dirRaw, int id) => Entry(dirRaw, id) is { } e ? e : null;

        private uint? Entry(uint dirRaw, int id)
        {
            var head = Read(dirRaw, 16);
            if (head is null) return null;
            int named = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(12));
            int ids = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(14));
            var entries = Read(dirRaw + 16, (named + ids) * 8);
            if (entries is null) return null;
            for (int i = named; i < named + ids; i++)
            {
                uint name = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8));
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8 + 4));
                if (name != id) continue;
                return _resourceRaw + (offset & 0x7FFFFFFF);
            }
            return null;
        }

        /// <summary>Follows the first entry of each level down to the data, for a directory whose ids do not matter.</summary>
        private byte[]? FirstLeafData(uint dirRaw)
        {
            for (int depth = 0; depth < 3; depth++)
            {
                var head = Read(dirRaw, 16);
                if (head is null) return null;
                int total = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(12)) + BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(14));
                if (total == 0) return null;
                var first = Read(dirRaw + 16, 8);
                if (first is null) return null;
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(4));
                if ((offset & 0x80000000) == 0) return Data(_resourceRaw + offset);
                dirRaw = _resourceRaw + (offset & 0x7FFFFFFF);
            }
            return null;
        }

        private byte[]? Data(uint entryRaw)
        {
            var e = Read(entryRaw, 16);
            if (e is null) return null;
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(e);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(4));
            if (size is 0 or > MaxResourceBytes) return null;
            return ToRaw(rva) is { } raw ? Read(raw, (int)size) : null;
        }

        private uint? ToRaw(uint rva)
        {
            foreach (var (va, size, raw, rawSize) in _sections)
            {
                var length = Math.Max(size, rawSize);
                if (rva >= va && rva - va < length) return raw + (rva - va);
            }
            return null;
        }

        private byte[]? Read(long offset, int count)
        {
            if (count < 0 || offset < 0 || offset + count > file.Length) return null;
            var buffer = new byte[count];
            file.Position = offset;
            file.ReadExactly(buffer);
            return buffer;
        }
    }
}
