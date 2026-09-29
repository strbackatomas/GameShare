using System.IO.Compression;
using GameShare.Protocol;
using GameShare.Storage;
using GameShare.Torrent;

namespace GameShare.Admin;

/// <summary>
/// Releases of GameShare itself: the key they are signed with, and the signed description of one build's package that every PC
/// checks before it takes the update. Run by scripts\package-release.ps1 on the build server, the private key comes from a secret there.
/// </summary>
public static class ReleaseCommands
{
    public const string PrivateKeyFileName = "release-private.key";
    public const string PublicKeyFileName = "release-public.key";

    /// <summary>Where the build server hands the private key over, as the key itself rather than a file.</summary>
    public const string KeyVariable = "GAMESHARE_RELEASE_KEY";

    public const string Usage = """
        gameshare-admin release-keygen --out <folder> [--password <text>]
            Makes release-private.key and release-public.key, the key GameShare's own updates are signed with. Not the trust list's key.
            release-public.key goes into scripts\ and is built into the program. The private key becomes the GitHub secret GAMESHARE_RELEASE_KEY.

        gameshare-admin release-sign --folder <package folder> --version <x.y.z> --flavor <agent|agent-net10|lanparty> --out <update json>
                                     [--zip <zip of the same files>] [--notes-file <file>] [--key <private key file>] [--password <text>]
            Describes the package in the folder (its files, their hashes and its torrent) and signs that. The zip is checked to hold exactly
            the same files. Without --key the key is taken from the environment variable GAMESHARE_RELEASE_KEY.

        gameshare-admin release-show --file <update json> [--pub <public key file or the key itself>]
            Checks the signature, by default against the key built into this program, and prints what the release says.
        """;

    public static int KeyGen(Dictionary<string, string> options, string? password, TextWriter output)
    {
        var folder = Required(options, "out");
        Directory.CreateDirectory(folder);
        var privatePath = Path.Combine(folder, PrivateKeyFileName);
        var publicPath = Path.Combine(folder, PublicKeyFileName);
        if (File.Exists(privatePath))
            throw new InvalidOperationException($"{privatePath} already exists. Not overwriting a key, delete it yourself if you mean to.");

        var keys = TrustSigning.GenerateKeyPair(password);
        File.WriteAllText(privatePath, keys.PrivateKey + Environment.NewLine);
        File.WriteAllText(publicPath, keys.PublicKey + Environment.NewLine);
        output.WriteLine($"Private key: {privatePath}  (whoever has it can make every PC run their code; store it as the secret {KeyVariable} and nowhere else)");
        output.WriteLine($"Public key:  {publicPath}  (copy it to scripts\\{PublicKeyFileName}, the build puts it into the program)");
        output.WriteLine($"Key id:      {TrustSigning.KeyId(keys.PublicKey)}");
        return 0;
    }

    public static async Task<int> SignAsync(Dictionary<string, string> options, string? password, TextWriter output, DateTimeOffset now)
    {
        var folder = Required(options, "folder");
        var version = Required(options, "version");
        var flavor = Required(options, "flavor");
        var outPath = Required(options, "out");
        if (!SemVer.TryParse(version, out _)) throw new ArgumentException($"--version '{version}' is not a version in the form MAJOR.MINOR.PATCH.");
        if (!AppFlavors.IsKnown(flavor)) throw new ArgumentException($"--flavor must be one of {string.Join(", ", AppFlavors.All)}.");
        var notes = options.TryGetValue("notes-file", out var notesFile) ? (await File.ReadAllTextAsync(notesFile).ConfigureAwait(false)).Trim() : null;
        var privateKey = PrivateKey(options);

        output.WriteLine($"Scanning {folder} ...");
        var release = await BuildAsync(folder, version, flavor, notes, options.GetValueOrDefault("zip"), now).ConfigureAwait(false);
        var envelope = ReleaseSigning.Sign(release, privateKey, password);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllBytesAsync(outPath, ReleaseSigning.Serialize(envelope)).ConfigureAwait(false);
        output.WriteLine($"GameShare {version} ({flavor}): {release.Manifest.Files.Count} files, {release.Manifest.TotalSize / 1024.0 / 1024:N1} MB, " +
                         $"content {release.Manifest.ContentHash}, signed with key {envelope.KeyId}, written to {outPath}");
        return 0;
    }

    public static int Show(Dictionary<string, string> options, TextWriter output)
    {
        var file = Required(options, "file");
        string publicKey;
        if (options.TryGetValue("pub", out var pub)) publicKey = File.Exists(pub) ? File.ReadAllText(pub).Trim() : pub.Trim();
        else publicKey = ReleaseKey.PublicKey ?? throw new ArgumentException($"This build has no release key built in. Give --pub.");

        var r = ReleaseSigning.Open(File.ReadAllBytes(file), publicKey);
        output.WriteLine($"Signature is valid, key {TrustSigning.KeyId(publicKey)}.");
        output.WriteLine($"GameShare {r.Version} for the {r.Flavor} build, released {r.ReleasedAt:yyyy-MM-dd HH:mm} UTC");
        output.WriteLine($"{r.Manifest.Files.Count} files, {r.Manifest.TotalSize / 1024.0 / 1024:N1} MB, content {r.Manifest.ContentHash}, torrent {r.Manifest.TorrentInfoHash}");
        output.WriteLine(r.ZipName is null ? "No zip, the LAN is the only source." : $"Zip {r.ZipName}, SHA-256 {r.ZipSha256}");
        if (!string.IsNullOrEmpty(r.Notes)) output.WriteLine($"\n{r.Notes}");
        return 0;
    }

    /// <summary>
    /// The description of the package in <paramref name="folder"/>. The torrent is built by the same code the agents use, from the same scan,
    /// so its info hash is the one every PC arrives at when it seeds the files it checked.
    /// </summary>
    public static async Task<AppRelease> BuildAsync(string folder, string version, string flavor, string? notes, string? zipPath, DateTimeOffset now, CancellationToken ct = default)
    {
        // No volatile rules: every file of a package is what was signed, nothing in it is expected to change.
        var scan = await ContentScanner.ScanAsync(folder, cancellationToken: ct).ConfigureAwait(false);
        if (scan.Files.Any(f => string.Equals(f.Path, ContentScanner.DefinitionFileName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"A package must not contain {ContentScanner.DefinitionFileName}.");
        var torrent = TorrentBuilder.Build(scan);
        var manifest = ManifestBuilder.Build(scan, torrentInfoHash: torrent.InfoHash) with { GameId = "gameshare", Name = "GameShare", Version = version };

        string? zipName = null, zipHash = null;
        if (zipPath is not null)
        {
            CheckZip(zipPath, manifest);
            zipName = Path.GetFileName(zipPath);
            zipHash = await ManifestVerifier.HashFileAsync(zipPath, ct).ConfigureAwait(false);
        }
        return new AppRelease(version, flavor, now, string.IsNullOrEmpty(notes) ? null : notes, manifest, torrent.TorrentBytes, zipName, zipHash);
    }

    /// <summary>The zip a PC falls back to must unpack into exactly the package, not into a folder of it or with a file missing.</summary>
    private static void CheckZip(string zipPath, GameManifest manifest)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var inZip = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToDictionary(e => e.FullName, e => e.Length, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var f in manifest.Files)
        {
            if (!inZip.Remove(f.Path, out var size)) problems.Add($"{f.Path} is missing");
            else if (size != f.Size) problems.Add($"{f.Path} has {size} bytes instead of {f.Size}");
        }
        problems.AddRange(inZip.Keys.Select(k => $"{k} is not in the package"));
        if (problems.Count > 0)
            throw new InvalidDataException($"{Path.GetFileName(zipPath)} does not hold the same files as the package: {string.Join("; ", problems.Take(10))}.");
    }

    private static string PrivateKey(Dictionary<string, string> options)
    {
        if (options.TryGetValue("key", out var path))
            return File.Exists(path) ? File.ReadAllText(path).Trim() : throw new FileNotFoundException($"Private key file '{path}' does not exist.");
        var fromEnvironment = Environment.GetEnvironmentVariable(KeyVariable)?.Trim();
        return string.IsNullOrEmpty(fromEnvironment)
            ? throw new ArgumentException($"Give --key, or put the private key into the environment variable {KeyVariable}.")
            : fromEnvironment;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new ArgumentException($"--{name} is required.");
}
