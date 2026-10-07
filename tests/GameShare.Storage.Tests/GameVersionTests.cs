using GameShare.Protocol;

namespace GameShare.Storage.Tests;

/// <summary>Whether another version of an installed game is offered as its update, with versions named the way games name them.</summary>
public class GameVersionTests
{
    [Theory]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("lan-v3", "lan-v2", 1)]
    [InlineData("LAN-v2", "lan-v2", 0)]
    [InlineData("23.1030a2", "23.1030a1", 1)]
    [InlineData("0.8.6", "0.8.6.1", -1)]
    [InlineData("1.5", null, 0)]
    [InlineData("", "1.0", 0)]
    public void Versions_compare_as_a_person_reads_them(string? a, string? b, int expected) =>
        Assert.Equal(expected, GameVersion.Compare(a, b));

    [Theory]
    [InlineData("1.1", TrustVerdict.Unknown, "1.0", TrustVerdict.Verified, true)]     // newer wins, vouched for or not
    [InlineData("1.0", TrustVerdict.Verified, "1.1", TrustVerdict.Unknown, false)]   // never back to an older one
    [InlineData("lan-v2", TrustVerdict.Verified, "lan-v2", TrustVerdict.Verified, false)] // two vouched copies of one number
    [InlineData("lan-v2", TrustVerdict.Unknown, "lan-v2", TrustVerdict.Verified, false)]
    [InlineData(null, TrustVerdict.Verified, null, TrustVerdict.Unknown, true)]     // to the administrator's version
    [InlineData("1.0", TrustVerdict.NotChecked, "1.0", TrustVerdict.NotChecked, true)] // no list: a patched game is the player's call
    [InlineData("2.0", TrustVerdict.Revoked, "1.0", TrustVerdict.Verified, false)]
    [InlineData("0.9", TrustVerdict.Unknown, "1.0", TrustVerdict.Revoked, true)]
    public void Only_a_newer_version_or_the_administrators_is_an_update(
        string? candidate, TrustVerdict candidateTrust, string? installed, TrustVerdict installedTrust, bool update) =>
        Assert.Equal(update, GameVersion.IsUpdate(candidate, candidateTrust, installed, installedTrust));
}
