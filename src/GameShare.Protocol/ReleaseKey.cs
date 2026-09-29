namespace GameShare.Protocol;

/// <summary>
/// The public key GameShare's own releases are signed with, built into the program from <c>scripts\release-public.key</c>.
/// It is not a setting on purpose: an update runs with the rights of the service, so whoever can change a settings file
/// must not be able to choose whose updates this PC takes.
/// </summary>
public static class ReleaseKey
{
    /// <summary>The key as one line of base64, or null for a build made without the key file, which never updates itself.</summary>
    public static readonly string? PublicKey = Load();

    private static string? Load()
    {
        using var stream = typeof(ReleaseKey).Assembly.GetManifestResourceStream("release-public.key");
        if (stream is null) return null;
        var key = new StreamReader(stream).ReadToEnd().Trim();
        return key.Length == 0 ? null : key;
    }
}
