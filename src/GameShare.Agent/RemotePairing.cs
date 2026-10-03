using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace GameShare.Agent;

/// <summary>
/// The cryptography of remote management, kept in one place.
///
/// Every PC has its own certificate, made the first time it is needed and kept in its database. After pairing, two PCs only talk
/// over TLS where both show their certificate, and each side compares the other's with the fingerprint it kept. Pairing is how
/// those fingerprints get exchanged: the controller connects to the target, both see each other's certificate, and the controller
/// proves it knows the code shown on the target's screen with an HMAC of both fingerprints keyed by the code. The target answers
/// with its own HMAC. A PC in between would show its own certificate to each side, so the proofs would not match the fingerprints
/// the real PCs saw. Guessing the code from a proof it caught is the only way around that, and the code has 60 bits, it expires
/// in minutes and a wrong proof ends the pairing.
/// </summary>
public static class RemotePairing
{
    /// <summary>Crockford's base32: no I, L, O or U, so nothing on the screen can be read as something else.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int CodeLength = 12; // 12 characters of 5 bits

    private const string OfferLabel = "gameshare-remote-pair-v1/offer";
    private const string AcceptLabel = "gameshare-remote-pair-v1/accept";

    /// <summary>A new code, grouped by four for reading: K7QF-M2XP-9HTD.</summary>
    public static string NewCode()
    {
        var chars = new char[CodeLength];
        for (int i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var s = new string(chars);
        return $"{s[..4]}-{s[4..8]}-{s[8..]}";
    }

    /// <summary>
    /// The code as typed, made comparable: case, dashes and spaces do not matter, and O, I and L count as 0, 1 and 1.
    /// </summary>
    /// <exception cref="ArgumentException">Not a code: wrong length or a character that is never in one.</exception>
    public static string NormalizeCode(string? typed)
    {
        var sb = new StringBuilder(CodeLength);
        foreach (var raw in typed ?? "")
        {
            if (raw is '-' or ' ' or '\t') continue;
            var c = char.ToUpperInvariant(raw) switch { 'O' => '0', 'I' or 'L' => '1', var other => other };
            if (!Alphabet.Contains(c)) throw new ArgumentException($"'{raw}' is not in a pairing code. Copy the code as the other PC shows it.");
            sb.Append(c);
        }
        if (sb.Length != CodeLength) throw new ArgumentException($"A pairing code has {CodeLength} characters, this one has {sb.Length}.");
        return sb.ToString();
    }

    /// <summary>What the controller sends: it knows the code, and it saw these two certificates.</summary>
    public static string OfferProof(string code, string controllerId, string controllerFingerprint, string targetId, string targetFingerprint) =>
        Proof(code, OfferLabel, controllerId, controllerFingerprint, targetId, targetFingerprint);

    /// <summary>What the target answers. A different label, so the controller's own proof cannot be sent back to it.</summary>
    public static string AcceptProof(string code, string controllerId, string controllerFingerprint, string targetId, string targetFingerprint) =>
        Proof(code, AcceptLabel, controllerId, controllerFingerprint, targetId, targetFingerprint);

    public static bool ProofsEqual(string expected, string? received) =>
        received is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(received.ToLowerInvariant()));

    private static string Proof(string code, string label, string controllerId, string controllerFingerprint, string targetId, string targetFingerprint)
    {
        var key = Encoding.UTF8.GetBytes(NormalizeCode(code));
        var data = Encoding.UTF8.GetBytes(string.Join('\n', label, controllerId, controllerFingerprint, targetId, targetFingerprint));
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, data));
    }

    /// <summary>SHA-256 of the whole certificate, lower case hex. What is pinned.</summary>
    public static string Fingerprint(X509Certificate certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();

    /// <summary>A new identity for this PC: a self-signed certificate with a P-256 key, as PKCS#12 for the database.</summary>
    public static byte[] NewIdentity(string machineId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=GameShare {machineId}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], critical: false)); // server and client authentication
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(50));
        return certificate.Export(X509ContentType.Pkcs12);
    }

    /// <summary>
    /// Loads the identity. Windows TLS cannot use a key that only lives in memory, so the key is loaded into a key container, which
    /// is deleted again when the certificate is disposed.
    /// </summary>
    public static X509Certificate2 LoadIdentity(byte[] pkcs12) =>
        X509CertificateLoader.LoadPkcs12(pkcs12, password: null, X509KeyStorageFlags.DefaultKeySet);
}
