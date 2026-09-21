using GameShare.Protocol;

namespace GameShare.Client.Services;

/// <summary>An error the agent reported. The message is the agent's own explanation and is safe to show to the user.</summary>
public sealed class AgentException(string message, int? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>Everything the client asks of the local agent. The GUI holds no BitTorrent logic, it only calls this.</summary>
public interface IAgentClient
{
    Task<StatusDto> GetStatusAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GameDto>> GetGamesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PeerDto>> GetPeersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DownloadDto>> GetDownloadsAsync(CancellationToken ct = default);
    Task<SettingsDto> GetSettingsAsync(CancellationToken ct = default);
    Task<SettingsDto> SaveSettingsAsync(SettingsDto settings, CancellationToken ct = default);

    Task<ScanResultDto> ScanAsync(CancellationToken ct = default);
    Task<DownloadDto> InstallAsync(string contentHash, string? targetRoot = null, CancellationToken ct = default);
    Task<DownloadDto> UpdateAsync(string contentHash, CancellationToken ct = default);
    Task<DownloadDto> RepairAsync(string contentHash, CancellationToken ct = default);
    Task<GameChangesDto> CheckAsync(string contentHash, CancellationToken ct = default);
    Task<GameDto?> RegisterAsync(string contentHash, CancellationToken ct = default);
    Task<GameDto?> AddVolatileAsync(string contentHash, IReadOnlyList<string> patterns, CancellationToken ct = default);

    Task<DownloadDto> PauseAsync(long downloadId, CancellationToken ct = default);
    Task<DownloadDto> ResumeAsync(long downloadId, CancellationToken ct = default);
    Task CancelAsync(long downloadId, bool deleteFiles, CancellationToken ct = default);
}

/// <summary>One event pushed by the agent. <see cref="Payload"/> is the DTO documented for that event name in <see cref="GameShareEvents"/>.</summary>
public sealed record AgentEvent(string Name, object Payload);

/// <summary>Live events from the agent. Raises on any thread, the consumer marshals to the UI thread.</summary>
public interface IEventStream : IAsyncDisposable
{
    event EventHandler<AgentEvent>? Received;

    /// <summary>Raised with true when the connection is up and with false when it drops. The stream reconnects by itself.</summary>
    event EventHandler<bool>? ConnectionChanged;

    Task StartAsync(CancellationToken ct = default);
}

/// <summary>Runs work on the UI thread. A seam so view models can be tested without a display.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
