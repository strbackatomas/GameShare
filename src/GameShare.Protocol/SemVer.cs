using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace GameShare.Protocol;

/// <summary>
/// A version as <c>src\Directory.Build.props</c> writes it, following semver.org: MAJOR.MINOR.PATCH with an optional
/// pre-release after a dash. Build metadata ("+...") is not used by GameShare and is refused. Compared the way semver.org
/// says, so "0.10.0" is newer than "0.9.0" and "0.5.0-beta.2" is older than "0.5.0".
/// </summary>
public sealed partial record SemVer(int Major, int Minor, int Patch, string? PreRelease) : IComparable<SemVer>
{
    [GeneratedRegex(@"^(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$")]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, [NotNullWhen(true)] out SemVer? version)
    {
        version = null;
        if (text is null || text.Length > 64) return false;
        var m = Pattern().Match(text);
        if (!m.Success) return false;
        version = new SemVer(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
            m.Groups[4].Success ? m.Groups[4].Value : null);
        return true;
    }

    /// <exception cref="FormatException">Not a version in the form MAJOR.MINOR.PATCH.</exception>
    public static SemVer Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a version in the form MAJOR.MINOR.PATCH.");

    /// <summary>Whether <paramref name="candidate"/> is a newer version than <paramref name="current"/>. Anything that is not a version is never newer.</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        TryParse(candidate, out var c) && (!TryParse(current, out var cur) || c.CompareTo(cur) > 0);

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // A release is newer than any of its pre-releases.
        if (PreRelease is null || other.PreRelease is null) return (PreRelease is null ? 1 : 0) - (other.PreRelease is null ? 1 : 0);

        var a = PreRelease.Split('.');
        var b = other.PreRelease.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool aNum = int.TryParse(a[i], out var an), bNum = int.TryParse(b[i], out var bn);
            c = (aNum, bNum) switch
            {
                (true, true) => an.CompareTo(bn),
                (true, false) => -1, // numeric identifiers are older than alphanumeric ones
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() => PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
