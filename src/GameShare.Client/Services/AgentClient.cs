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
    private readonly string _prefix;
    private readonly string _whose;

    /// <param name="prefix">Put before every path. Empty for this PC's agent, /api/remote/targets/{id} for a paired PC.</param>
    /// <param name="whose">Who does not answer, for the message when nothing answers.</param>
    public AgentClient(HttpClient http, string prefix = "", string whose = "Agent GameShare")
    {
        _http = http;
        _prefix = prefix;
        _whose = whose;
    }

    public static AgentClient Create(Uri baseAddress) =>
        new(new HttpClient { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan }); // limits are per call

    public IAgentClient ForTarget(string machineId) =>
        new AgentClient(_http, $"/api/remote/targets/{Uri.EscapeDataString(machineId)}", "Agent GameShare na tomto PC");

    public Task<RemoteStatusDto> GetRemoteAsync(CancellationToken ct = default) => SendAsync<RemoteStatusDto>(HttpMethod.Get, "/api/remote", null, Quick, ct);
    public Task<RemoteStatusDto> SetRemoteEnabledAsync(bool enabled, CancellationToken ct = default) =>
        SendAsync<RemoteStatusDto>(HttpMethod.Put, "/api/remote/enabled", new RemoteEnableRequest(enabled), Quick, ct);
    public Task<RemotePairingDto> StartPairingAsync(CancellationToken ct = default) => SendAsync<RemotePairingDto>(HttpMethod.Post, "/api/remote/pairing", null, Quick, ct);
    public async Task CancelPairingAsync(CancellationToken ct = default) =>
        await SendAsync<object?>(HttpMethod.Delete, "/api/remote/pairing", null, Quick, ct).ConfigureAwait(false);
    public async Task RemoveControllerAsync(string machineId, CancellationToken ct = default) =>
        await SendAsync<object?>(HttpMethod.Delete, $"/api/remote/controllers/{Uri.EscapeDataString(machineId)}", null, Quick, ct).ConfigureAwait(false);
    public Task<PairedMachineDto> PairAsync(string machineId, string code, CancellationToken ct = default) =>
        SendAsync<PairedMachineDto>(HttpMethod.Post, "/api/remote/targets", new RemotePairRequest(machineId, code), Quick, ct);
    public Task<RemoteRestoreResultDto> RestorePairingAsync(byte[] backup, string password, CancellationToken ct = default) =>
        SendAsync<RemoteRestoreResultDto>(HttpMethod.Post, "/api/remote/restore", new RemoteRestoreRequest(password, Convert.ToBase64String(backup)), Slow, ct);

    /// <summary>The backup comes back as a file, not as JSON.</summary>
    public async Task<byte[]> ExportPairingAsync(string password, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _prefix + "/api/remote/backup")
        {
            Content = JsonContent.Create(new RemoteBackupRequest(password), options: GameShareJson.Options),
        };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Quick);
        try
        {
            using var response = await _http.SendAsync(request, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw await ToExceptionAsync(response, limit.Token).ConfigureAwait(false);
            return await response.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { throw new AgentException($"{_whose} neodpovídá. Zkontroluj, že služba běží.", inner: ex); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AgentException($"Agent neodpověděl do {Quick.TotalSeconds:F0} sekund."); }
    }

    public async Task WakeAsync(string machineId, CancellationToken ct = default) =>
        await SendAsync<object?>(HttpMethod.Post, $"/api/remote/targets/{Uri.EscapeDataString(machineId)}/wake", null, Quick, ct).ConfigureAwait(false);
    public async Task RemoveTargetAsync(string machineId, CancellationToken ct = default) =>
        await SendAsync<object?>(HttpMethod.Delete, $"/api/remote/targets/{Uri.EscapeDataString(machineId)}", null, Quick, ct).ConfigureAwait(false);

    public Task<StatusDto> GetStatusAsync(CancellationToken ct = default) => SendAsync<StatusDto>(HttpMethod.Get, "/api/status", null, Quick, ct);
    public Task<IReadOnlyList<GameDto>> GetGamesAsync(CancellationToken ct = default) => ListAsync<GameDto>("/api/games", ct);
    public Task<IReadOnlyList<PeerDto>> GetPeersAsync(CancellationToken ct = default) => ListAsync<PeerDto>("/api/peers", ct);
    public Task<NetworkCheckDto> CheckNetworkAsync(CancellationToken ct = default) => SendAsync<NetworkCheckDto>(HttpMethod.Get, "/api/network/check", null, Quick, ct);
    public Task<IReadOnlyList<OfferedGameDto>> GetPeerGamesAsync(string machineId, CancellationToken ct = default) => ListAsync<OfferedGameDto>($"/api/peers/{machineId}/games", ct);
    public Task<IReadOnlyList<DownloadDto>> GetDownloadsAsync(CancellationToken ct = default) => ListAsync<DownloadDto>("/api/downloads", ct);
    public Task<IReadOnlyList<UploadDto>> GetUploadsAsync(CancellationToken ct = default) => ListAsync<UploadDto>("/api/uploads", ct);
    public Task<SettingsDto> GetSettingsAsync(CancellationToken ct = default) => SendAsync<SettingsDto>(HttpMethod.Get, "/api/settings", null, Quick, ct);
    public Task<SettingsDto> SaveSettingsAsync(SettingsDto settings, CancellationToken ct = default) => SendAsync<SettingsDto>(HttpMethod.Put, "/api/settings", settings, Quick, ct);
    public Task<IReadOnlyList<GameRootDto>> GetGameRootsAsync(CancellationToken ct = default) => ListAsync<GameRootDto>("/api/settings/roots", ct);
    public Task<IReadOnlyList<NetworkAdapterDto>> GetNetworkAdaptersAsync(CancellationToken ct = default) => ListAsync<NetworkAdapterDto>("/api/settings/adapters", ct);
    public Task<IReadOnlyList<string>> GetLogTailAsync(int maxLines = 500, CancellationToken ct = default) => ListAsync<string>($"/api/logs?lines={maxLines}", ct);
    public async Task ClearLogAsync(CancellationToken ct = default) =>
        await SendAsync<object?>(HttpMethod.Post, "/api/logs/clear", null, Quick, ct).ConfigureAwait(false);

    public async Task RestartAgentAsync(CancellationToken ct = default) =>
        await SendAsync<object?>(HttpMethod.Post, "/api/agent/restart", null, Quick, ct).ConfigureAwait(false);

    public Task<TrustStatusDto> GetTrustAsync(CancellationToken ct = default) => SendAsync<TrustStatusDto>(HttpMethod.Get, "/api/trust", null, Quick, ct);
    public Task<TrustStatusDto> RefreshTrustAsync(CancellationToken ct = default) => SendAsync<TrustStatusDto>(HttpMethod.Post, "/api/trust/refresh", null, Slow, ct);

    public async Task<AppUpdateStatusDto?> GetAppUpdateAsync(CancellationToken ct = default)
    {
        try { return await SendAsync<AppUpdateStatusDto>(HttpMethod.Get, "/api/app-update", null, Quick, ct).ConfigureAwait(false); }
        catch (AgentException ex) when (ex.StatusCode == 404) { return null; } // an agent from before updates existed
    }

    public Task<AppUpdateStatusDto> CheckAppUpdateAsync(CancellationToken ct = default) => SendAsync<AppUpdateStatusDto>(HttpMethod.Post, "/api/app-update/check", null, Slow, ct);
    public Task<AppUpdateStatusDto> ApplyAppUpdateAsync(CancellationToken ct = default) => SendAsync<AppUpdateStatusDto>(HttpMethod.Post, "/api/app-update/apply", null, Slow, ct);

    public Task<ScanResultDto> ScanAsync(CancellationToken ct = default) => SendAsync<ScanResultDto>(HttpMethod.Post, "/api/games/scan", null, Slow, ct);
    public Task<ScanProgressDto?> GetScanProgressAsync(CancellationToken ct = default) => SendAsync<ScanProgressDto?>(HttpMethod.Get, "/api/games/scan", null, Quick, ct);

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

    public async Task UninstallAsync(string contentHash, CancellationToken ct = default) =>
        await SendAsync<object?>(HttpMethod.Delete, $"/api/games/{contentHash}", null, Quick, ct).ConfigureAwait(false);

    public Task<LaunchInfoDto> LaunchAsync(string contentHash, int entry = 0, CancellationToken ct = default) =>
        SendAsync<LaunchInfoDto>(HttpMethod.Post, entry == 0 ? $"/api/games/{contentHash}/launch" : $"/api/games/{contentHash}/launch?entry={entry}", null, Slow, ct);

    public Task<IReadOnlyList<string>> GetExecutablesAsync(string contentHash, CancellationToken ct = default) =>
        ListAsync<string>($"/api/games/{contentHash}/executables", ct);

    // Planning hashes the installers, which can take a moment for large ones.
    public Task<SetupPlanDto> GetSetupPlanAsync(string contentHash, CancellationToken ct = default) =>
        SendAsync<SetupPlanDto>(HttpMethod.Get, $"/api/games/{contentHash}/setup", null, Slow, ct);

    public Task<GameDto?> SetupDoneAsync(string contentHash, string setupHash, CancellationToken ct = default) =>
        SendAsync<GameDto?>(HttpMethod.Post, $"/api/games/{contentHash}/setup/done", new SetupDoneRequest(setupHash), Quick, ct);

    public Task<GameDto?> ResetSetupAsync(string contentHash, CancellationToken ct = default) =>
        SendAsync<GameDto?>(HttpMethod.Delete, $"/api/games/{contentHash}/setup", null, Quick, ct);

    public async Task<byte[]?> GetIconAsync(string contentHash, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Quick);
        try
        {
            using var response = await _http.GetAsync($"{_prefix}/api/games/{contentHash}/icon", limit.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false) : null;
        }
        catch (HttpRequestException) { return null; } // a picture is not worth an error message
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

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
        using var request = new HttpRequestMessage(method, _prefix + path);
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
            throw new AgentException($"{_whose} neodpovídá. Zkontroluj, že služba běží.", inner: ex);
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
