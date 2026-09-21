using System.Net.Http.Json;
using System.Text.Json;
using GameShare.Protocol;

namespace GameShare.Client.Services;

/// <summary>HTTP client for the agent's local control API.</summary>
public sealed class AgentClient : IAgentClient
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(20);
    // Hashing a large game takes minutes, and the agent does it while the request is open.
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;

    public AgentClient(HttpClient http) => _http = http;

    public static AgentClient Create(Uri baseAddress) =>
        new(new HttpClient { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan }); // limits are per call

    public Task<StatusDto> GetStatusAsync(CancellationToken ct = default) => SendAsync<StatusDto>(HttpMethod.Get, "/api/status", null, Quick, ct);
    public Task<IReadOnlyList<GameDto>> GetGamesAsync(CancellationToken ct = default) => ListAsync<GameDto>("/api/games", ct);
    public Task<IReadOnlyList<PeerDto>> GetPeersAsync(CancellationToken ct = default) => ListAsync<PeerDto>("/api/peers", ct);
    public Task<IReadOnlyList<DownloadDto>> GetDownloadsAsync(CancellationToken ct = default) => ListAsync<DownloadDto>("/api/downloads", ct);
    public Task<SettingsDto> GetSettingsAsync(CancellationToken ct = default) => SendAsync<SettingsDto>(HttpMethod.Get, "/api/settings", null, Quick, ct);
    public Task<SettingsDto> SaveSettingsAsync(SettingsDto settings, CancellationToken ct = default) => SendAsync<SettingsDto>(HttpMethod.Put, "/api/settings", settings, Quick, ct);

    public Task<TrustStatusDto> GetTrustAsync(CancellationToken ct = default) => SendAsync<TrustStatusDto>(HttpMethod.Get, "/api/trust", null, Quick, ct);
    public Task<TrustStatusDto> RefreshTrustAsync(CancellationToken ct = default) => SendAsync<TrustStatusDto>(HttpMethod.Post, "/api/trust/refresh", null, Slow, ct);

    public Task<ScanResultDto> ScanAsync(CancellationToken ct = default) => SendAsync<ScanResultDto>(HttpMethod.Post, "/api/games/scan", null, Slow, ct);

    public Task<DownloadDto> InstallAsync(string contentHash, string? targetRoot = null, CancellationToken ct = default) =>
        SendAsync<DownloadDto>(HttpMethod.Post, $"/api/games/{contentHash}/install", targetRoot is null ? null : new InstallRequest(targetRoot), Quick, ct);

    public Task<DownloadDto> UpdateAsync(string contentHash, CancellationToken ct = default) =>
        SendAsync<DownloadDto>(HttpMethod.Post, $"/api/games/{contentHash}/update", null, Quick, ct);

    public Task<DownloadDto> RepairAsync(string contentHash, CancellationToken ct = default) =>
        SendAsync<DownloadDto>(HttpMethod.Post, $"/api/games/{contentHash}/repair", null, Quick, ct);

    public Task<GameChangesDto> CheckAsync(string contentHash, CancellationToken ct = default) =>
        SendAsync<GameChangesDto>(HttpMethod.Post, $"/api/games/{contentHash}/check", null, Slow, ct);

    public Task<GameDto?> RegisterAsync(string contentHash, CancellationToken ct = default) =>
        SendAsync<GameDto?>(HttpMethod.Post, $"/api/games/{contentHash}/register", null, Slow, ct);

    public Task<GameDto?> AddVolatileAsync(string contentHash, IReadOnlyList<string> patterns, CancellationToken ct = default) =>
        SendAsync<GameDto?>(HttpMethod.Post, $"/api/games/{contentHash}/volatile", new AddVolatileRequest(patterns), Slow, ct);

    public Task<LaunchInfoDto> LaunchAsync(string contentHash, CancellationToken ct = default) =>
        SendAsync<LaunchInfoDto>(HttpMethod.Post, $"/api/games/{contentHash}/launch", null, Slow, ct);

    public Task<IReadOnlyList<string>> GetExecutablesAsync(string contentHash, CancellationToken ct = default) =>
        ListAsync<string>($"/api/games/{contentHash}/executables", ct);

    public Task<GameDto?> ChooseExecutableAsync(string contentHash, string executable, string? arguments, CancellationToken ct = default) =>
        SendAsync<GameDto?>(HttpMethod.Put, $"/api/games/{contentHash}/launcher", new LauncherChoiceRequest(executable, arguments), Quick, ct);

    public Task<DownloadDto> PauseAsync(long downloadId, CancellationToken ct = default) =>
        SendAsync<DownloadDto>(HttpMethod.Post, $"/api/downloads/{downloadId}/pause", null, Quick, ct);

    public Task<DownloadDto> ResumeAsync(long downloadId, CancellationToken ct = default) =>
        SendAsync<DownloadDto>(HttpMethod.Post, $"/api/downloads/{downloadId}/resume", null, Quick, ct);

    public async Task CancelAsync(long downloadId, bool deleteFiles, CancellationToken ct = default) =>
        await SendAsync<object?>(HttpMethod.Delete, $"/api/downloads/{downloadId}?deleteFiles={(deleteFiles ? "true" : "false")}", null, Quick, ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<T>> ListAsync<T>(string path, CancellationToken ct) =>
        await SendAsync<List<T>>(HttpMethod.Get, path, null, Quick, ct).ConfigureAwait(false) ?? [];

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, TimeSpan timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: GameShareJson.Options);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            using var response = await _http.SendAsync(request, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw await ToExceptionAsync(response, limit.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength == 0 || response.StatusCode == System.Net.HttpStatusCode.NoContent) return default!;
            return await response.Content.ReadFromJsonAsync<T>(GameShareJson.Options, limit.Token).ConfigureAwait(false) ?? default!;
        }
        catch (HttpRequestException ex)
        {
            throw new AgentException("Agent GameShare neodpovídá. Zkontroluj, že služba běží.", inner: ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AgentException($"Agent neodpověděl do {timeout.TotalSeconds:F0} sekund.");
        }
    }

    /// <summary>The agent answers failures with a problem document whose detail says what is wrong. Show that, not a status code.</summary>
    private static async Task<AgentException> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string message = $"Agent odpověděl chybou {(int)response.StatusCode}.";
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } d) message = d;
            else if (doc.RootElement.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } t) message = t;
        }
        catch (JsonException) { /* not a problem document, keep the generic message */ }
        return new AgentException(message, (int)response.StatusCode);
    }
}
