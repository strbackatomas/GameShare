namespace GameShare.Agent;

/// <summary>
/// Reads a small document from a place the administrator configured: an http(s) address or a file, for example on a share.
/// Used for the trust list and for GameShare's own release descriptions. The size is capped even when a server does not say
/// how large the answer is, so a hostile or broken server cannot fill memory.
/// </summary>
public static class SourceFetch
{
    public static bool IsWeb(string source, out Uri uri) =>
        Uri.TryCreate(source, UriKind.Absolute, out uri!) && uri.Scheme is "http" or "https";

    /// <summary>A file next to <paramref name="source"/>, which is a folder or an address ending in one, such as a release's download folder.</summary>
    public static string Combine(string source, string fileName) =>
        IsWeb(source, out var uri)
            ? new Uri(uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/"), fileName).AbsoluteUri
            : Path.Combine(source, fileName);

    /// <param name="what">What is read, for the messages: "trust list".</param>
    /// <exception cref="InvalidDataException">Larger than <paramref name="maxBytes"/>.</exception>
    /// <exception cref="FileNotFoundException">A file that does not exist or cannot be reached.</exception>
    /// <exception cref="HttpRequestException">The server refused or could not be reached.</exception>
    public static async Task<byte[]> ReadAsync(HttpClient http, string source, int maxBytes, string what, CancellationToken ct)
    {
        if (IsWeb(source, out var uri))
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new InvalidDataException($"The {what} at {source} is larger than {maxBytes / 1024 / 1024} MB.");
            return await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), maxBytes, what, ct).ConfigureAwait(false);
        }

        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException($"The {what} file '{source}' does not exist or cannot be reached.");
        await using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await ReadLimitedAsync(file, maxBytes, what, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int maxBytes, string what, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes) throw new InvalidDataException($"The {what} is larger than {maxBytes / 1024 / 1024} MB.");
        }
        return buffer.ToArray();
    }
}
