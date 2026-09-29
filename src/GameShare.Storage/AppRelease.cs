using System.Text.Json;
using System.Text.RegularExpressions;
using GameShare.Protocol;

namespace GameShare.Storage;

/// <summary>
/// One published version of GameShare itself, for one build (<see cref="AppFlavors"/>). Signed by the release key, published as
/// <c>update-&lt;flavor&gt;.json</c> next to the release on GitHub, on a share, and by every PC on the LAN that holds the package.
/// </summary>
/// <param name="Manifest">
/// The package: every file in the layout of an installation (for the service the agent at the root and the client in <c>Client/</c>),
/// with its SHA-256. Its <see cref="GameManifest.Version"/> is <paramref name="Version"/>.
/// </param>
/// <param name="Torrent">
/// The .torrent of the package. It is part of what is signed, so every PC adds the same torrent and they can send the package to each other.
/// </param>
/// <param name="ZipName">The file next to the description that holds the same files, for a PC that finds nobody on the LAN with them.</param>
/// <param name="ZipSha256">SHA-256 of that zip, checked before anything in it is unpacked.</param>
public sealed record AppRelease(
    string Version, string Flavor, DateTimeOffset ReleasedAt, string? Notes, GameManifest Manifest, byte[] Torrent, string? ZipName, string? ZipSha256)
{
    /// <summary>The name a release description is published under.</summary>
    public static string FileName(string flavor) => $"update-{flavor}.json";
}

/// <summary>
/// Signing and checking of <see cref="AppRelease"/>. The key is not the trust list's: the release key is held by whoever builds GameShare,
/// and its public half is built into the program, because what it signs runs with the rights of the service.
/// </summary>
public static partial class ReleaseSigning
{
    private const string What = "release description";
    private const string Signer = "the GameShare release key";

    public const int MaxNotesLength = 20_000;
    public const int MaxTorrentBytes = 2 * 1024 * 1024;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.zip$")]
    private static partial Regex ZipFileName();

    public static TrustEnvelope Sign(AppRelease release, string privateKey, string? password = null)
    {
        Validate(release);
        return TrustSigning.SignBytes(JsonSerializer.SerializeToUtf8Bytes(release, GameShareJson.Options), privateKey, password);
    }

    public static byte[] Serialize(TrustEnvelope envelope) => TrustSigning.Serialize(envelope);

    /// <summary>Checks a release description against the public key and returns what it says. Says nothing about whether this PC should take it, see <see cref="CheckApplies"/>.</summary>
    /// <exception cref="InvalidDataException">Malformed, signed by another key, changed after signing, or not a valid release. The message says which.</exception>
    public static AppRelease Open(byte[] envelopeJson, string publicKey)
    {
        var payloadBytes = TrustSigning.OpenBytes(envelopeJson, publicKey, What, Signer);

        AppRelease release;
        try { release = JsonSerializer.Deserialize<AppRelease>(payloadBytes, GameShareJson.Options) ?? throw new JsonException("empty"); }
        catch (JsonException ex) { throw new InvalidDataException($"The signed content of the {What} is not valid.", ex); }

        Validate(release);
        return release;
    }

    /// <summary>
    /// Whether a release that verified is one this PC should take: for the build it runs, newer than what it runs, and not older than
    /// one it already holds. An older signed release is never taken, it may have a flaw the newer one fixed.
    /// </summary>
    /// <exception cref="InvalidDataException">It does not apply, with the reason.</exception>
    public static void CheckApplies(AppRelease release, string flavor, string runningVersion, string? heldVersion = null)
    {
        if (!string.Equals(release.Flavor, flavor, StringComparison.Ordinal))
            throw new InvalidDataException($"Version {release.Version} is for the {release.Flavor} build, this is the {flavor} build.");
        if (!SemVer.IsNewer(release.Version, runningVersion))
            throw new InvalidDataException($"Version {release.Version} is not newer than {runningVersion}, which is running.");
        if (heldVersion is not null && SemVer.IsNewer(heldVersion, release.Version))
            throw new InvalidDataException($"Version {release.Version} is older than {heldVersion}, which this PC already has.");
    }

    /// <exception cref="InvalidDataException">The release is not complete or not consistent.</exception>
    public static void Validate(AppRelease r)
    {
        if (r.Manifest is null || r.Torrent is null) throw new InvalidDataException($"The {What} is incomplete.");
        if (!SemVer.TryParse(r.Version, out _)) throw new InvalidDataException($"'{r.Version}' is not a version in the form MAJOR.MINOR.PATCH.");
        if (!AppFlavors.IsKnown(r.Flavor)) throw new InvalidDataException($"'{r.Flavor}' is not a GameShare build this version knows.");
        if (r.Notes is { Length: > MaxNotesLength }) throw new InvalidDataException($"The release notes are longer than {MaxNotesLength} characters.");
        if (r.Torrent.Length == 0 || r.Torrent.Length > MaxTorrentBytes) throw new InvalidDataException("The torrent of the release is empty or too large.");

        var errors = ManifestValidator.Validate(r.Manifest);
        if (errors.Count > 0) throw new InvalidDataException($"The package of version {r.Version} is not valid: {string.Join(" ", errors)}");
        if (r.Manifest.Version != r.Version) throw new InvalidDataException($"The package says version {r.Manifest.Version}, the release says {r.Version}.");
        if (r.Manifest.TorrentInfoHash is null) throw new InvalidDataException("The package has no torrent info hash.");
        if (r.Manifest.VolatilePatterns.Count > 0) throw new InvalidDataException("A package of GameShare has no files that may change.");

        if (r.ZipName is null != r.ZipSha256 is null) throw new InvalidDataException("The zip of the release needs both a name and a hash.");
        if (r.ZipName is not null && !ZipFileName().IsMatch(r.ZipName)) throw new InvalidDataException($"'{r.ZipName}' is not a plain zip file name.");
        if (r.ZipSha256 is not null && !Sha256Hex().IsMatch(r.ZipSha256)) throw new InvalidDataException("The hash of the zip is not a SHA-256.");
    }
}
