namespace GameShare.Storage;

/// <summary>A loaded or freshly made key: the private key stays on disk, this is what the rest of the tool needs.</summary>
public sealed record TrustKeyInfo(string PrivateKeyPath, string PublicKey, string KeyId);

/// <summary>
/// The steps an administrator's tool needs around the trust list: making keys, scanning a game folder into a vouched entry,
/// and signing and publishing a change. Shared by the CLI (<c>gameshare-admin</c>) and the GUI, so both behave the same
/// and a bug fixed in one is fixed in the other.
/// </summary>
public static class TrustWorkflow
{
    /// <exception cref="InvalidOperationException">A private key already exists in <paramref name="folder"/>.</exception>
    public static TrustKeyInfo GenerateKeys(string folder, string? password = null)
    {
        Directory.CreateDirectory(folder);
        var privatePath = Path.Combine(folder, "trust-private.key");
        var publicPath = Path.Combine(folder, "trust-public.key");
        if (File.Exists(privatePath))
            throw new InvalidOperationException($"{privatePath} already exists. Not overwriting a key, delete it yourself if you mean to.");

        var keys = TrustSigning.GenerateKeyPair(password);
        File.WriteAllText(privatePath, keys.PrivateKey + Environment.NewLine);
        File.WriteAllText(publicPath, keys.PublicKey + Environment.NewLine);
        return new TrustKeyInfo(privatePath, keys.PublicKey, TrustSigning.KeyId(keys.PublicKey));
    }

    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The key cannot be read, or the password is wrong.</exception>
    public static TrustKeyInfo LoadKey(string privateKeyPath, string? password = null)
    {
        if (!File.Exists(privateKeyPath)) throw new FileNotFoundException($"Private key file '{privateKeyPath}' does not exist.");
        var publicKey = TrustSigning.PublicKeyOf(File.ReadAllText(privateKeyPath).Trim(), password);
        return new TrustKeyInfo(privateKeyPath, publicKey, TrustSigning.KeyId(publicKey));
    }

    /// <summary>The list at <paramref name="listPath"/>, checked against the key, or a new empty list when there is no file yet.</summary>
    /// <exception cref="InvalidDataException">The file exists but does not verify against the key.</exception>
    public static TrustPayload LoadOrStartList(string listPath, string publicKey, DateTimeOffset now) =>
        File.Exists(listPath) ? TrustSigning.Open(File.ReadAllBytes(listPath), publicKey) : TrustPayload.Empty(now);

    /// <summary>Scans a game folder as it is now (a clean install, no saves) into a candidate entry. Nothing is added yet.</summary>
    /// <exception cref="DirectoryNotFoundException"><paramref name="gameFolder"/> is not a folder.</exception>
    public static async Task<TrustedGame> ScanAsync(string gameFolder, CancellationToken ct = default)
    {
        if (!Directory.Exists(gameFolder)) throw new DirectoryNotFoundException($"'{gameFolder}' is not a folder.");
        var (manifest, _) = await ManifestBuilder.ScanAndBuildAsync(gameFolder, cancellationToken: ct).ConfigureAwait(false);
        return new TrustedGame(manifest.ContentHash, manifest.GameId, manifest.Name, manifest.Version);
    }

    /// <summary>Signs and writes the list. Every change goes through here, so a list on disk is always signed.</summary>
    /// <exception cref="FileNotFoundException">The private key file does not exist.</exception>
    /// <exception cref="InvalidDataException">The key or the list content is not usable, or the password is wrong.</exception>
    public static void Publish(TrustPayload list, string privateKeyPath, string? password, string listPath)
    {
        if (!File.Exists(privateKeyPath)) throw new FileNotFoundException($"Private key file '{privateKeyPath}' does not exist.");
        var bytes = TrustSigning.Serialize(TrustSigning.Sign(list, File.ReadAllText(privateKeyPath).Trim(), password));
        var directory = Path.GetDirectoryName(Path.GetFullPath(listPath));
        if (directory is not null) Directory.CreateDirectory(directory);
        File.WriteAllBytes(listPath, bytes);
    }
}
