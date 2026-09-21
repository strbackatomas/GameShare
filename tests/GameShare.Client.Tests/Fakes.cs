using GameShare.Client.Services;
using GameShare.Protocol;

namespace GameShare.Client.Tests;

/// <summary>Runs work immediately, so view models can be tested without a display or a UI thread.</summary>
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>An agent whose answers the test controls, and which records what the client asked of it.</summary>
internal sealed class FakeAgent : IAgentClient
{
    public List<GameDto> Games { get; set; } = [];
    public List<PeerDto> Peers { get; set; } = [];
    public List<DownloadDto> Downloads { get; set; } = [];
    public SettingsDto Settings { get; set; } = new(["D:\\Games"], true, null, null);
    public string MachineName { get; set; } = "PC-07";

    /// <summary>When set, every call throws this, as if the agent were stopped.</summary>
    public AgentException? Failure { get; set; }

    /// <summary>When set, the next call to that member throws it.</summary>
    public Dictionary<string, AgentException> FailNext { get; } = [];

    public List<string> Calls { get; } = [];
    public GameChangesDto CheckResult { get; set; } = new(true, [], [], [], []);
    public TaskCompletionSource? Gate { get; set; }

    private async Task<T> Do<T>(string call, Func<T> result)
    {
        Calls.Add(call);
        if (Failure is not null) throw Failure;
        var key = call.Split('(')[0];
        if (FailNext.Remove(key, out var ex)) throw ex;
        if (Gate is not null) await Gate.Task; // lets a test look at the state while an operation is in flight
        return result();
    }

    public Task<StatusDto> GetStatusAsync(CancellationToken ct = default) =>
        Do("GetStatus", () => new StatusDto("id", MachineName, "1.0", Peers.Count, Games.Count(g => g.State == GameState.Installed), Downloads.Count));

    public Task<IReadOnlyList<GameDto>> GetGamesAsync(CancellationToken ct = default) => Do<IReadOnlyList<GameDto>>("GetGames", () => [.. Games]);
    public Task<IReadOnlyList<PeerDto>> GetPeersAsync(CancellationToken ct = default) => Do<IReadOnlyList<PeerDto>>("GetPeers", () => [.. Peers]);
    public Task<IReadOnlyList<DownloadDto>> GetDownloadsAsync(CancellationToken ct = default) => Do<IReadOnlyList<DownloadDto>>("GetDownloads", () => [.. Downloads]);
    public Task<SettingsDto> GetSettingsAsync(CancellationToken ct = default) => Do("GetSettings", () => Settings);

    public Task<SettingsDto> SaveSettingsAsync(SettingsDto settings, CancellationToken ct = default) =>
        Do($"SaveSettings({string.Join(";", settings.GameRoots)}|{settings.SeedingEnabled}|{settings.MaxUploadMBps}|{settings.MaxDownloadMBps})", () => Settings = settings);

    public Task<ScanResultDto> ScanAsync(CancellationToken ct = default) => Do("Scan", () => new ScanResultDto(2, 5, 0, [], [], []));

    public Task<DownloadDto> InstallAsync(string contentHash, string? targetRoot = null, CancellationToken ct = default) =>
        Do($"Install({contentHash})", () => NewDownload(contentHash, "Install"));
    public Task<DownloadDto> UpdateAsync(string contentHash, CancellationToken ct = default) => Do($"Update({contentHash})", () => NewDownload(contentHash, "Update"));
    public Task<DownloadDto> RepairAsync(string contentHash, CancellationToken ct = default) => Do($"Repair({contentHash})", () => NewDownload(contentHash, "Repair"));
    public Task<GameChangesDto> CheckAsync(string contentHash, CancellationToken ct = default) => Do($"Check({contentHash})", () => CheckResult);
    public Task<GameDto?> RegisterAsync(string contentHash, CancellationToken ct = default) => Do<GameDto?>($"Register({contentHash})", () => null);
    public Task<GameDto?> AddVolatileAsync(string contentHash, IReadOnlyList<string> patterns, CancellationToken ct = default) =>
        Do<GameDto?>($"AddVolatile({contentHash}|{string.Join(";", patterns)})", () => null);

    public Task<DownloadDto> PauseAsync(long downloadId, CancellationToken ct = default) => Do($"Pause({downloadId})", () => Downloads.First(d => d.Id == downloadId));
    public Task<DownloadDto> ResumeAsync(long downloadId, CancellationToken ct = default) => Do($"Resume({downloadId})", () => Downloads.First(d => d.Id == downloadId));
    public Task CancelAsync(long downloadId, bool deleteFiles, CancellationToken ct = default) => Do($"Cancel({downloadId}|{deleteFiles})", () => (object?)null);

    private DownloadDto NewDownload(string hash, string kind)
    {
        var d = Downloads.Count + 1;
        return new DownloadDto(d, hash, "Game", "Downloading", 0, 1000, 0, 0, 0, null, null, []) { Kind = kind };
    }
}

internal sealed class FakeEvents : IEventStream
{
    public event EventHandler<AgentEvent>? Received;
    public event EventHandler<bool>? ConnectionChanged;
    public bool Started { get; private set; }
    public Task StartAsync(CancellationToken ct = default) { Started = true; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Raise(string name, object payload) => Received?.Invoke(this, new AgentEvent(name, payload));
    public void SetConnected(bool up) => ConnectionChanged?.Invoke(this, up);
}

internal static class Data
{
    public const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string C = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    public const string D = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    public static GameDto Game(string hash, string name, GameState state, string? version = "0.38", long size = 70_000_000_000, string[]? peers = null,
        string? installPath = null, string? updates = null, string gameId = "game") =>
        new(hash, gameId, name, version, size, state, installPath, peers ?? [], null, null) { UpdatesContentHash = updates };

    public static DownloadDto Download(long id, string hash, string name, string state, double percent, long done = 0, long total = 1000, long speed = 0,
        string kind = "Install", (string Name, long Rate)[]? sources = null, double? eta = null, string? error = null) =>
        new(id, hash, name, state, done, total, percent, speed, sources?.Length ?? 0, eta, error,
            (sources ?? []).Select(s => new DownloadPeerDto(s.Name, "10.0.0.1", s.Rate, 0)).ToList()) { Kind = kind };

    public static PeerDto Peer(string id, string name, int games = 1) => new(id, name, "192.168.30.10", 47702, games, DateTimeOffset.UtcNow);
}
