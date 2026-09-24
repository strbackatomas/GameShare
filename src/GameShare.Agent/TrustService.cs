using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.Agent;

/// <summary>
/// Compares games with the administrator's signed list of content hashes. A content hash covers every file of a game, and a download
/// is checked against it before it is accepted, so a version that is in the list is exactly what the administrator vouched for,
/// whichever PC it came from.
/// The list comes from a web address or a file, and is used only when its signature verifies against the public key of this PC.
/// The last good list is kept on disk, so a server that is down never turns verification off. A list older than the one already held is refused.
/// </summary>
public sealed class TrustService
{
    private const string CacheFileName = "trust-list.json";

    private readonly AgentOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<TrustService> _log;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly TimeProvider _time;
    private volatile Loaded? _current;
    private volatile Outcome _outcome = new(null, null);

    private sealed record Loaded(TrustPayload List, Dictionary<string, TrustedGame> Games, Dictionary<string, RevokedGame> Revoked);
    private sealed record Outcome(DateTimeOffset? Refreshed, string? Error);

    public TrustService(AgentOptions options, HttpClient http, ILogger<TrustService> log, TimeProvider? time = null)
    {
        _options = options;
        _http = http;
        _log = log;
        _time = time ?? TimeProvider.System;
        _cachePath = Path.Combine(options.ResolveDataDir(), CacheFileName);
    }

    public TrustMode Mode => _options.TrustMode;

    /// <summary>Raised when the list in use changed or a refresh failed, so the GUI can show it.</summary>
    public event EventHandler? Changed;

    /// <summary>Where a game stands. Nothing is said about games when checking is off or there is no usable list.</summary>
    public (TrustVerdict Verdict, string? Note) Check(string contentHash)
    {
        var list = Usable();
        if (Mode == TrustMode.Off || list is null) return (TrustVerdict.NotChecked, null);
        if (list.Revoked.TryGetValue(contentHash, out var revoked)) return (TrustVerdict.Revoked, revoked.Reason);
        return (list.Games.ContainsKey(contentHash) ? TrustVerdict.Verified : TrustVerdict.Unknown, null);
    }

    /// <summary>
    /// Whether a definition is the one the administrator signed for this version. A definition travels outside the content hash,
    /// so this is what stands between another PC's gameshare.json and the steps that run with administrator rights.
    /// </summary>
    public DefinitionVerdict CheckDefinition(string contentHash, GameDefinition? definition)
    {
        var list = Usable();
        if (Mode == TrustMode.Off || list is null) return DefinitionVerdict.NotChecked;
        if (!list.Games.TryGetValue(contentHash, out var game) || game.DefinitionHash is null) return DefinitionVerdict.NotSigned;
        return definition is not null && DefinitionHasher.Compute(definition) == game.DefinitionHash ? DefinitionVerdict.Verified : DefinitionVerdict.Different;
    }

    /// <summary>
    /// Refuses to install a version the settings do not allow: a revoked one always, an unlisted one when verification is required,
    /// and every one when it is required and there is no usable list, because "cannot check" must not mean "allowed".
    /// </summary>
    /// <exception cref="InvalidOperationException">The message says why, in words a user can act on.</exception>
    public void RequireAllowed(string contentHash, string? gameName)
    {
        if (Mode == TrustMode.Off) return;
        var name = string.IsNullOrEmpty(gameName) ? "This version" : $"{gameName}";
        var (verdict, note) = Check(contentHash);

        if (verdict == TrustVerdict.Revoked)
            throw new InvalidOperationException($"{name} was withdrawn by the administrator: {note}. It will not be installed.");
        if (Mode != TrustMode.Require || verdict == TrustVerdict.Verified) return;

        throw new InvalidOperationException(verdict == TrustVerdict.Unknown
            ? $"{name} is not in the administrator's list of verified games, and this PC only installs verified games."
            : "This PC only installs verified games, but no trust list is available to check against. " +
              (_outcome.Error is { } error ? $"The last attempt to load it failed: {error}" : "Check the list address and try again."));
    }

    public TrustStatusDto Status()
    {
        var list = _current;
        var usable = Usable();
        return new TrustStatusDto(
            Mode, Mode == TrustMode.Off ? null : _options.TrustListSource,
            usable is not null, list?.List.Sequence, list?.List.IssuedAt, list?.List.ValidUntil,
            list?.Games.Count ?? 0, list?.Revoked.Count ?? 0, _outcome.Refreshed, _outcome.Error,
            Mode == TrustMode.Off || string.IsNullOrEmpty(_options.TrustPublicKey) ? null : TrustSigning.KeyId(_options.TrustPublicKey));
    }

    /// <summary>Loads the list that was kept on disk, then refreshes it now and on every interval, until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (Mode == TrustMode.Off) return;

        LoadCache();
        using var timer = new PeriodicTimer(_options.TrustRefreshInterval);
        do
        {
            try { await RefreshAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Fetches the list once. A failure never throws and never removes the list that is in use, it is recorded in the status.
    /// </summary>
    public async Task<TrustStatusDto> RefreshAsync(CancellationToken ct = default)
    {
        if (Mode == TrustMode.Off) return Status();

        await _refreshing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? error = null;
            bool replaced = false;
            try
            {
                var bytes = await FetchAsync(ct).ConfigureAwait(false);
                var payload = TrustSigning.Open(bytes, _options.TrustPublicKey!);
                replaced = Accept(payload);
                if (replaced) SaveCache(bytes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
            {
                error = ex is TaskCanceledException ? "The trust list did not answer in time." : ex.Message;
                _log.LogWarning("The trust list could not be loaded, the previous one stays in use: {Reason}", error);
            }

            var changed = replaced || error != _outcome.Error;
            _outcome = new Outcome(error is null ? _time.GetUtcNow() : _outcome.Refreshed, error);
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _refreshing.Release(); }
        return Status();
    }

    /// <summary>Uses <paramref name="payload"/> unless the list already held is newer. Returns whether the list in use changed.</summary>
    private bool Accept(TrustPayload payload)
    {
        var held = _current;
        if (held is not null && payload.Sequence < held.List.Sequence)
            throw new InvalidOperationException(
                $"The list found has sequence {payload.Sequence} but this PC already has {held.List.Sequence}. An older list is never used, it could hide a revocation.");
        if (held is not null && payload.Sequence == held.List.Sequence) return false;

        if (payload.ValidUntil is { } until && until < _time.GetUtcNow())
            throw new InvalidOperationException($"The list expired on {until:yyyy-MM-dd}. Ask the administrator to publish a new one.");

        _current = new Loaded(payload,
            payload.Games.ToDictionary(g => g.ContentHash, StringComparer.Ordinal),
            payload.Revoked.ToDictionary(r => r.ContentHash, StringComparer.Ordinal));
        _log.LogInformation("Trust list {Sequence} in use: {Games} verified game(s), {Revoked} revoked", payload.Sequence, payload.Games.Count, payload.Revoked.Count);
        return true;
    }

    /// <summary>A list past its validity date is not used, though it stays held so an older one cannot replace it.</summary>
    private Loaded? Usable() =>
        _current is { } c && (c.List.ValidUntil is not { } until || until >= _time.GetUtcNow()) ? c : null;

    private async Task<byte[]> FetchAsync(CancellationToken ct)
    {
        var source = _options.TrustListSource!;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > TrustSigning.MaxEnvelopeBytes)
                throw new InvalidDataException($"The trust list at {source} is larger than {TrustSigning.MaxEnvelopeBytes / 1024 / 1024} MB.");
            return await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }

        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException($"The trust list file '{source}' does not exist or cannot be reached.");
        await using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await ReadLimitedAsync(file, ct).ConfigureAwait(false);
    }

    /// <summary>A server that does not announce its size cannot make this PC read without end.</summary>
    private static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > TrustSigning.MaxEnvelopeBytes) throw new InvalidDataException($"The trust list is larger than {TrustSigning.MaxEnvelopeBytes / 1024 / 1024} MB.");
        }
        return buffer.ToArray();
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            Accept(TrustSigning.Open(File.ReadAllBytes(_cachePath), _options.TrustPublicKey!)); // a changed key makes the old cache fail, which is right
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("The stored trust list is not usable and is ignored: {Reason}", ex.Message);
        }
    }

    private void SaveCache(byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("The trust list could not be stored, it is used but a restart needs the source to be reachable: {Reason}", ex.Message);
        }
    }
}
