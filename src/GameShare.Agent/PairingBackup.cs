using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameShare.Protocol;

namespace GameShare.Agent;

/// <summary>What a pairing backup holds: who this PC is on the network, its certificate with the private key, and its pairings.</summary>
public sealed record PairingBackupPayload(string MachineId, string Identity, string State);

/// <summary>
/// A backup of this PC's pairings, as a file the user keeps. Whoever has the backup of a managing PC can manage every PC paired with
/// it, so it is always encrypted: AES-256-GCM with a key from the password (PBKDF2-SHA256, 600 000 rounds, a random salt). The plain
/// header (format, PC name, date, salt) is bound into the encryption, so it cannot be altered either.
/// </summary>
public static class PairingBackup
{
    public const string Format = "gameshare-pairing-backup";
    public const int Version = 1;
    public const int MinPasswordLength = 8;
    private const int Iterations = 600_000;

    private sealed record BackupFile(
        string Format, int Version, string MachineName, string CreatedAt, string Salt, int Iterations, string Nonce, string Ciphertext, string Tag);

    /// <exception cref="ArgumentException">The password is too short.</exception>
    public static byte[] Create(PairingBackupPayload payload, string password, string machineName)
    {
        RequirePassword(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var header = new BackupFile(Format, Version, machineName, DateTimeOffset.UtcNow.ToString("O"), Convert.ToBase64String(salt), Iterations, "", "", "");

        var plain = JsonSerializer.SerializeToUtf8Bytes(payload, GameShareJson.Options);
        var cipher = new byte[plain.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using (var aes = new AesGcm(Key(password, salt, Iterations), tag.Length))
            aes.Encrypt(nonce, plain, cipher, tag, Associated(header));
        CryptographicOperations.ZeroMemory(plain);

        var file = header with { Nonce = Convert.ToBase64String(nonce), Ciphertext = Convert.ToBase64String(cipher), Tag = Convert.ToBase64String(tag) };
        return JsonSerializer.SerializeToUtf8Bytes(file, new JsonSerializerOptions(GameShareJson.Options) { WriteIndented = true });
    }

    /// <exception cref="ArgumentException">Not a backup, a newer kind, damaged, or the password is wrong. The message says which.</exception>
    public static PairingBackupPayload Open(byte[] data, string password)
    {
        BackupFile? file;
        try { file = JsonSerializer.Deserialize<BackupFile>(data, GameShareJson.Options); }
        catch (JsonException) { file = null; }
        if (file is null || file.Format != Format)
            throw new ArgumentException("This file is not a GameShare pairing backup.");
        if (file.Version != Version)
            throw new ArgumentException($"This backup was made by a newer GameShare (backup version {file.Version}). Update GameShare first.");
        if (file.Iterations is < 100_000 or > 10_000_000)
            throw new ArgumentException("This backup is damaged.");

        byte[] salt, nonce, cipher, tag;
        try
        {
            salt = Convert.FromBase64String(file.Salt);
            nonce = Convert.FromBase64String(file.Nonce);
            cipher = Convert.FromBase64String(file.Ciphertext);
            tag = Convert.FromBase64String(file.Tag);
        }
        catch (FormatException) { throw new ArgumentException("This backup is damaged."); }

        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(Key(password ?? "", salt, file.Iterations), tag.Length);
            aes.Decrypt(nonce, cipher, tag, plain, Associated(file));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new ArgumentException("The password is wrong, or the backup was changed after it was made.");
        }

        try
        {
            var payload = JsonSerializer.Deserialize<PairingBackupPayload>(plain, GameShareJson.Options);
            if (payload is null || string.IsNullOrWhiteSpace(payload.MachineId) || string.IsNullOrWhiteSpace(payload.Identity) || payload.State is null)
                throw new ArgumentException("This backup is damaged.");
            return payload;
        }
        catch (JsonException) { throw new ArgumentException("This backup is damaged."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>The name of the PC the backup was made on, readable without the password.</summary>
    public static string? MachineNameOf(byte[] data)
    {
        try { return JsonSerializer.Deserialize<BackupFile>(data, GameShareJson.Options)?.MachineName; }
        catch (JsonException) { return null; }
    }

    private static void RequirePassword(string? password)
    {
        if (password is null || password.Length < MinPasswordLength)
            throw new ArgumentException($"The password must have at least {MinPasswordLength} characters.");
    }

    private static byte[] Key(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] Associated(BackupFile header) =>
        Encoding.UTF8.GetBytes(string.Join('\n', header.Format, header.Version, header.MachineName, header.CreatedAt, header.Salt, header.Iterations));
}
