namespace GameShare.Protocol;

/// <summary>
/// Whether another version of an installed game is an update of it. Games name their versions as they like ("1.5", "0.8.6",
/// "23.1030a1", "lan-v2"), so they are compared the way a person reads them: runs of digits as numbers, the rest as text.
/// </summary>
public static class GameVersion
{
    /// <summary>
    /// A newer version is an update, an older one is not. With the same version, or none to compare, the files alone cannot say
    /// which is newer: a version the administrator vouches for is then never left for another one, two vouched copies of "lan-v2"
    /// included, since taking one for the other would undo whatever was changed in it (the administrator gives a new version a new
    /// number). Without the administrator's word it is the player's call, as a patched game registered here is. A revoked version
    /// never is an update, and anything is one over it.
    /// </summary>
    public static bool IsUpdate(string? candidateVersion, TrustVerdict candidateTrust, string? installedVersion, TrustVerdict installedTrust)
    {
        if (candidateTrust == TrustVerdict.Revoked) return false;
        if (installedTrust == TrustVerdict.Revoked) return true;
        var order = Compare(candidateVersion, installedVersion);
        if (order != 0) return order > 0;
        return installedTrust != TrustVerdict.Verified;
    }

    /// <summary>Above 0 when <paramref name="a"/> is newer, below when older, 0 when the same or when either has no version.</summary>
    public static int Compare(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
        var x = Parts(a.Trim());
        var y = Parts(b.Trim());
        for (int i = 0; i < Math.Min(x.Count, y.Count); i++)
        {
            int c = (x[i].Number, y[i].Number) switch
            {
                ({ } m, { } n) => m.CompareTo(n),
                _ => string.Compare(x[i].Text, y[i].Text, StringComparison.OrdinalIgnoreCase),
            };
            if (c != 0) return Math.Sign(c);
        }
        return x.Count.CompareTo(y.Count);
    }

    private static List<(decimal? Number, string Text)> Parts(string version)
    {
        var parts = new List<(decimal?, string)>();
        int start = 0;
        for (int i = 1; i <= version.Length; i++)
        {
            if (i < version.Length && char.IsAsciiDigit(version[i]) == char.IsAsciiDigit(version[start])) continue;
            var part = version[start..i];
            parts.Add((char.IsAsciiDigit(part[0]) && decimal.TryParse(part, out var n) ? n : null, part));
            start = i;
        }
        return parts;
    }
}
