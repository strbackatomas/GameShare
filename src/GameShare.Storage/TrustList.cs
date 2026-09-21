using System.Security.Cryptography;
using System.Text.Json;
using GameShare.Protocol;

namespace GameShare.Storage;

/// <summary>A version of a game the administrator vouches for. Identity is the content hash, which covers every file.</summary>
public sealed record TrustedGame(string ContentHash, string GameId, string Name, string? Version);

/// <summary>A version the administrator withdrew, for example because it was found to be modified.</summary>
public sealed record RevokedGame(string ContentHash, string Reason);

/// <summary>What the administrator signs.</summary>
/// <param name="Sequence">Grows with every published list. A PC never accepts a list older than the one it already has.</param>
/// <param name="ValidUntil">Optional. A list past this date is not used, so a replayed old list cannot hide a revocation for ever.</param>
public sealed record TrustPayload(
    long Sequence, DateTimeOffset IssuedAt, DateTimeOffset? ValidUntil, IReadOnlyList<TrustedGame> Games, IReadOnlyList<RevokedGame> Revoked)
{
    public static TrustPayload Empty(DateTimeOffset now) => new(0, now, null, [], []);
}

/// <summary>
/// The signed document as it is published on a web server or a file share. The payload is signed as the exact bytes it was
/// serialised to, so nothing has to be canonicalised and nothing can change between signing and checking.
/// </summary>
/// <param name="Payload">Base64 of the UTF-8 JSON of a <see cref="TrustPayload"/>.</param>
/// <param name="Signature">Base64 ECDSA P-256 / SHA-256 signature (IEEE P1363, 64 bytes) over the payload bytes.</param>
/// <param name="KeyId">Short id of the signing key, so a wrong key gives a clear message instead of "bad signature".</param>
public sealed record TrustEnvelope(int Format, string KeyId, string Payload, string Signature);

public sealed record TrustKeyPair(string PublicKey, string PrivateKey);

/// <summary>
/// Signing and checking of the trust list. The administrator holds the private key, every PC holds only the public key.
/// A list that does not verify is treated as if it did not exist.
/// </summary>
public static class TrustSigning
{
    public const int CurrentFormat = 1;

    /// <summary>A list is a few hundred bytes per game. This is far more than any real one and stops a hostile server from filling memory.</summary>
    public const int MaxEnvelopeBytes = 4 * 1024 * 1024;
    private const int MaxEntries = 20_000;

    /// <summary>New signing key. Both keys are one line of base64: the public key as SubjectPublicKeyInfo, the private key as PKCS#8.</summary>
    /// <param name="password">Encrypts the private key when given.</param>
    public static TrustKeyPair GenerateKeyPair(string? password = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var priv = string.IsNullOrEmpty(password)
            ? key.ExportPkcs8PrivateKey()
            : key.ExportEncryptedPkcs8PrivateKey(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 200_000));
        return new TrustKeyPair(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(priv));
    }

    /// <summary>Short id of a public key: the first 16 hex digits of the SHA-256 of its encoding.</summary>
    /// <exception cref="InvalidDataException">The key is not a valid public key.</exception>
    public static string KeyId(string publicKey)
    {
        var bytes = ImportPublic(publicKey, out var key);
        key.Dispose();
        return Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
    }

    /// <exception cref="InvalidDataException">The key is not a valid public key.</exception>
    public static void CheckPublicKey(string publicKey)
    {
        ImportPublic(publicKey, out var key);
        key.Dispose();
    }

    /// <summary>The public key that belongs to a private key, so a list can be checked with the key that signs it.</summary>
    /// <exception cref="InvalidDataException">The private key cannot be read, or the password is wrong.</exception>
    public static string PublicKeyOf(string privateKey, string? password = null)
    {
        using var key = ImportPrivate(privateKey, password);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    private static ECDsa ImportPrivate(string privateKey, string? password)
    {
        var key = ECDsa.Create();
        try
        {
            var bytes = Convert.FromBase64String((privateKey ?? "").Trim());
            if (string.IsNullOrEmpty(password)) key.ImportPkcs8PrivateKey(bytes, out _);
            else key.ImportEncryptedPkcs8PrivateKey(password, bytes, out _);
            return key;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            key.Dispose();
            throw new InvalidDataException(string.IsNullOrEmpty(password)
                ? "The private key could not be read. If it was created with a password, give the password."
                : "The private key could not be read with that password.", ex);
        }
    }

    public static TrustEnvelope Sign(TrustPayload payload, string privateKey, string? password = null)
    {
        Validate(payload);
        using var key = ImportPrivate(privateKey, password);

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, GameShareJson.Options);
        var signature = key.SignData(payloadBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var keyId = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();
        return new TrustEnvelope(CurrentFormat, keyId, Convert.ToBase64String(payloadBytes), Convert.ToBase64String(signature));
    }

    public static byte[] Serialize(TrustEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions(GameShareJson.Options) { WriteIndented = true });

    /// <summary>Checks a published list against the public key and returns what it says.</summary>
    /// <exception cref="InvalidDataException">The list is malformed, signed by another key, or the signature does not match. The message says which.</exception>
    public static TrustPayload Open(byte[] envelopeJson, string publicKey)
    {
        if (envelopeJson.Length > MaxEnvelopeBytes) throw new InvalidDataException($"The trust list is larger than {MaxEnvelopeBytes / 1024 / 1024} MB.");

        TrustEnvelope envelope;
        try { envelope = JsonSerializer.Deserialize<TrustEnvelope>(envelopeJson, GameShareJson.Options) ?? throw new JsonException("empty"); }
        catch (JsonException ex) { throw new InvalidDataException("The trust list is not a valid signed list.", ex); }

        if (envelope.Format != CurrentFormat) throw new InvalidDataException($"The trust list has format {envelope.Format}, this version understands {CurrentFormat}.");
        if (string.IsNullOrEmpty(envelope.Payload) || string.IsNullOrEmpty(envelope.Signature)) throw new InvalidDataException("The trust list has no payload or no signature.");

        var expected = KeyId(publicKey);
        if (!string.Equals(envelope.KeyId, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The trust list is signed with key {envelope.KeyId}, this PC trusts key {expected}.");

        byte[] payloadBytes, signature;
        try { payloadBytes = Convert.FromBase64String(envelope.Payload); signature = Convert.FromBase64String(envelope.Signature); }
        catch (FormatException ex) { throw new InvalidDataException("The trust list is not valid base64.", ex); }

        ImportPublic(publicKey, out var key);
        using (key)
        {
            if (!key.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new InvalidDataException("The signature of the trust list is wrong. The list was changed, or it was not signed by the administrator.");
        }

        TrustPayload payload;
        try { payload = JsonSerializer.Deserialize<TrustPayload>(payloadBytes, GameShareJson.Options) ?? throw new JsonException("empty"); }
        catch (JsonException ex) { throw new InvalidDataException("The signed content of the trust list is not valid.", ex); }

        Validate(payload);
        return payload;
    }

    private static byte[] ImportPublic(string publicKey, out ECDsa key)
    {
        key = ECDsa.Create();
        try
        {
            var bytes = Convert.FromBase64String((publicKey ?? "").Trim());
            key.ImportSubjectPublicKeyInfo(bytes, out _);
            if (key.KeySize != 256) throw new CryptographicException("not a P-256 key");
            return bytes;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            key.Dispose();
            throw new InvalidDataException("The public key is not a valid P-256 key in base64.", ex);
        }
    }

    private static void Validate(TrustPayload p)
    {
        if (p.Games is null || p.Revoked is null) throw new InvalidDataException("The trust list is incomplete.");
        if (p.Games.Count + p.Revoked.Count > MaxEntries) throw new InvalidDataException($"The trust list has more than {MaxEntries} entries.");
        foreach (var hash in p.Games.Select(g => g.ContentHash).Concat(p.Revoked.Select(r => r.ContentHash)))
            if (hash is null || hash.Length != 64 || !hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
                throw new InvalidDataException($"'{hash}' is not a content hash (64 lowercase hex digits).");
    }
}

/// <summary>Changes to a trust list. Every change makes a new list with a higher sequence number.</summary>
public static class TrustListEditor
{
    /// <summary>Adds or replaces games. A version that was revoked and is added again is no longer revoked, that is a deliberate act.</summary>
    public static TrustPayload Add(TrustPayload list, IEnumerable<TrustedGame> games, DateTimeOffset now, TimeSpan? validFor = null)
    {
        var added = games.ToList();
        var hashes = added.Select(g => g.ContentHash).ToHashSet(StringComparer.Ordinal);
        return Next(list, now, validFor) with
        {
            Games = list.Games.Where(g => !hashes.Contains(g.ContentHash)).Concat(added).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Version).ToList(),
            Revoked = list.Revoked.Where(r => !hashes.Contains(r.ContentHash)).ToList(),
        };
    }

    /// <summary>Withdraws a version. It is removed from the vouched games and listed as revoked with the reason.</summary>
    public static TrustPayload Revoke(TrustPayload list, string contentHash, string reason, DateTimeOffset now, TimeSpan? validFor = null) =>
        Next(list, now, validFor) with
        {
            Games = list.Games.Where(g => g.ContentHash != contentHash).ToList(),
            Revoked = list.Revoked.Where(r => r.ContentHash != contentHash).Append(new RevokedGame(contentHash, reason)).ToList(),
        };

    /// <summary>Forgets a version altogether. It is then simply not in the list, which is not the same as revoked.</summary>
    public static TrustPayload Remove(TrustPayload list, string contentHash, DateTimeOffset now, TimeSpan? validFor = null) =>
        Next(list, now, validFor) with
        {
            Games = list.Games.Where(g => g.ContentHash != contentHash).ToList(),
            Revoked = list.Revoked.Where(r => r.ContentHash != contentHash).ToList(),
        };

    private static TrustPayload Next(TrustPayload list, DateTimeOffset now, TimeSpan? validFor) =>
        list with { Sequence = list.Sequence + 1, IssuedAt = now, ValidUntil = validFor is null ? null : now + validFor };
}
