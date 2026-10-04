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
    public TrustStatusDto Trust { get; set; } = new(TrustMode.Off, null, false, null, null, null, 0, 0, null, null, null);
    public string MachineName { get; set; } = "PC-07";
    public string Version { get; set; } = "1.0";

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
        Do("GetStatus", () => new StatusDto("id", MachineName, Version, Peers.Count, Games.Count(g => g.State == GameState.Installed), Downloads.Count));

    public Task<IReadOnlyList<GameDto>> GetGamesAsync(CancellationToken ct = default) => Do<IReadOnlyList<GameDto>>("GetGames", () => [.. Games]);
    public Task<IReadOnlyList<PeerDto>> GetPeersAsync(CancellationToken ct = default) => Do<IReadOnlyList<PeerDto>>("GetPeers", () => [.. Peers]);

    public NetworkCheckDto NetworkCheck { get; set; } = new([], [], [], null);
    public Task<NetworkCheckDto> CheckNetworkAsync(CancellationToken ct = default) => Do("CheckNetwork", () => NetworkCheck);

    /// <summary>What each peer offers, keyed by machine id. A peer with no entry here answers with an empty list.</summary>
    public Dictionary<string, List<OfferedGameDto>> PeerGames { get; set; } = [];

    public Task<IReadOnlyList<OfferedGameDto>> GetPeerGamesAsync(string machineId, CancellationToken ct = default) =>
        Do<IReadOnlyList<OfferedGameDto>>($"GetPeerGames({machineId})", () => PeerGames.TryGetValue(machineId, out var g) ? [.. g] : []);
    public Task<IReadOnlyList<DownloadDto>> GetDownloadsAsync(CancellationToken ct = default) => Do<IReadOnlyList<DownloadDto>>("GetDownloads", () => [.. Downloads]);

    public List<UploadDto> Uploads { get; set; } = [];

    public Task<IReadOnlyList<UploadDto>> GetUploadsAsync(CancellationToken ct = default) => Do<IReadOnlyList<UploadDto>>("GetUploads", () => [.. Uploads]);
    public Task<SettingsDto> GetSettingsAsync(CancellationToken ct = default) => Do("GetSettings", () => Settings);

    public Task<SettingsDto> SaveSettingsAsync(SettingsDto settings, CancellationToken ct = default) =>
        Do($"SaveSettings({string.Join(";", settings.GameRoots)}|{settings.SeedingEnabled}|{settings.MaxUploadMBps}|{settings.MaxDownloadMBps})", () => Settings = settings);

    /// <summary>Defaults to the configured roots with no known free space. Set to control what a test sees.</summary>
    public List<GameRootDto> GameRoots { get; set; } = [];

    public Task<IReadOnlyList<GameRootDto>> GetGameRootsAsync(CancellationToken ct = default) =>
        Do<IReadOnlyList<GameRootDto>>("GetGameRoots", () => GameRoots.Count > 0 ? [.. GameRoots] : [.. Settings.GameRoots.Select(r => new GameRootDto(r, null))]);

    public List<NetworkAdapterDto> Adapters { get; set; } = [];

    public Task<IReadOnlyList<NetworkAdapterDto>> GetNetworkAdaptersAsync(CancellationToken ct = default) =>
        Do<IReadOnlyList<NetworkAdapterDto>>("GetNetworkAdapters", () => [.. Adapters]);

    public List<string> LogLines { get; set; } = [];

    public Task<IReadOnlyList<string>> GetLogTailAsync(int maxLines = 500, CancellationToken ct = default) =>
        Do<IReadOnlyList<string>>($"GetLogTail({maxLines})", () => [.. LogLines]);

    public Task ClearLogAsync(CancellationToken ct = default) => Do("ClearLog", () => (object?)null);

    public Task RestartAgentAsync(CancellationToken ct = default) => Do("RestartAgent", () => (object?)null);

    public Task<TrustStatusDto> GetTrustAsync(CancellationToken ct = default) => Do("GetTrust", () => Trust);
    public Task<TrustStatusDto> RefreshTrustAsync(CancellationToken ct = default) => Do("RefreshTrust", () => Trust);

    /// <summary>What the agent says about updates. Null plays an agent from before updates existed.</summary>
    public AppUpdateStatusDto? AppUpdate { get; set; }

    public Task<AppUpdateStatusDto?> GetAppUpdateAsync(CancellationToken ct = default) => Do("GetAppUpdate", () => AppUpdate);
    public Task<AppUpdateStatusDto> CheckAppUpdateAsync(CancellationToken ct = default) => Do("CheckAppUpdate", () => AppUpdate!);
    public Task<AppUpdateStatusDto> ApplyAppUpdateAsync(CancellationToken ct = default) =>
        Do("ApplyAppUpdate", () => AppUpdate = AppUpdate! with { State = AppUpdateState.Applying, CanApply = false });

    public Task<ScanResultDto> ScanAsync(CancellationToken ct = default) => Do("Scan", () => new ScanResultDto(2, 5, 0, [], [], []));

    /// <summary>What the agent says while a scan runs. Each call takes the next one, then null (no scan).</summary>
    public Queue<ScanProgressDto> ScanProgress { get; } = new();
    public Task<ScanProgressDto?> GetScanProgressAsync(CancellationToken ct = default) =>
        Task.FromResult(ScanProgress.TryDequeue(out var p) ? p : null);

    public Task<DownloadDto> InstallAsync(string contentHash, string? targetRoot = null, CancellationToken ct = default) =>
        Do($"Install({contentHash}{(targetRoot is null ? "" : $"|{targetRoot}")})", () => NewDownload(contentHash, "Install"));
    public Task<DownloadDto> UpdateAsync(string contentHash, CancellationToken ct = default) => Do($"Update({contentHash})", () => NewDownload(contentHash, "Update"));
    public Task<DownloadDto> RepairAsync(string contentHash, CancellationToken ct = default) => Do($"Repair({contentHash})", () => NewDownload(contentHash, "Repair"));
    public Task<GameChangesDto> CheckAsync(string contentHash, CancellationToken ct = default) => Do($"Check({contentHash})", () => CheckResult);
    public Task<GameDto?> RegisterAsync(string contentHash, CancellationToken ct = default) => Do<GameDto?>($"Register({contentHash})", () => null);
    public Task<GameDto?> AddVolatileAsync(string contentHash, IReadOnlyList<string> patterns, CancellationToken ct = default) =>
        Do<GameDto?>($"AddVolatile({contentHash}|{string.Join(";", patterns)})", () => null);
    public Task UninstallAsync(string contentHash, CancellationToken ct = default) => Do($"Uninstall({contentHash})", () => (object?)null);

    public LaunchInfoDto LaunchInfo { get; set; } = new(@"D:\Games\BeamNG\Game.exe", "-windowed", @"D:\Games\BeamNG");
    public List<string> Executables { get; set; } = [];

    public Task<LaunchInfoDto> LaunchAsync(string contentHash, int entry = 0, CancellationToken ct = default) =>
        Do(entry == 0 ? $"Launch({contentHash})" : $"Launch({contentHash}#{entry})", () => LaunchInfo);
    public Task<IReadOnlyList<string>> GetExecutablesAsync(string contentHash, CancellationToken ct = default) =>
        Do<IReadOnlyList<string>>($"GetExecutables({contentHash})", () => [.. Executables]);
    public Dictionary<string, byte[]> Icons { get; } = [];
    public SetupPlanDto? SetupPlan { get; set; }
    public Task<SetupPlanDto> GetSetupPlanAsync(string contentHash, CancellationToken ct = default) =>
        Do($"GetSetupPlan({contentHash})", () => SetupPlan ?? new SetupPlanDto(contentHash, "", "", [], DefinitionVerdict.NotChecked));
    public Task<GameDto?> SetupDoneAsync(string contentHash, string setupHash, CancellationToken ct = default) =>
        Do<GameDto?>($"SetupDone({contentHash}|{setupHash})", () =>
        {
            var i = Games.FindIndex(g => g.ContentHash == contentHash);
            if (i >= 0) Games[i] = Games[i] with { NeedsSetup = false };
            return i >= 0 ? Games[i] : null;
        });
    public Task<GameDto?> ResetSetupAsync(string contentHash, CancellationToken ct = default) =>
        Do<GameDto?>($"ResetSetup({contentHash})", () =>
        {
            var i = Games.FindIndex(g => g.ContentHash == contentHash);
            if (i >= 0) Games[i] = Games[i] with { NeedsSetup = true };
            return i >= 0 ? Games[i] : null;
        });
    public Task<byte[]?> GetIconAsync(string contentHash, CancellationToken ct = default) =>
        Task.FromResult(Icons.TryGetValue(contentHash, out var icon) ? icon : null);
    public Task<GameDto?> ChooseExecutableAsync(string contentHash, string executable, string? arguments, CancellationToken ct = default) =>
        Do<GameDto?>($"ChooseExecutable({contentHash}|{executable}|{arguments})", () => null);

    public Task<DownloadDto> PauseAsync(long downloadId, CancellationToken ct = default) => Do($"Pause({downloadId})", () => Downloads.First(d => d.Id == downloadId));
    public Task<DownloadDto> ResumeAsync(long downloadId, CancellationToken ct = default) => Do($"Resume({downloadId})", () => Downloads.First(d => d.Id == downloadId));
    public Task CancelAsync(long downloadId, bool deleteFiles, CancellationToken ct = default) => Do($"Cancel({downloadId}|{deleteFiles})", () => (object?)null);

    // ---- remote management ----

    public RemoteStatusDto Remote { get; set; } = new(true, false, false, 47703, new string('a', 64), null, [], []);

    public Task<RemoteStatusDto> GetRemoteAsync(CancellationToken ct = default) => Do("GetRemote", () => Remote);
    public Task<RemoteStatusDto> SetRemoteEnabledAsync(bool enabled, CancellationToken ct = default) =>
        Do($"SetRemoteEnabled({enabled})", () => Remote = Remote with { Enabled = enabled, Listening = enabled, Pairing = enabled ? Remote.Pairing : null });
    public Task<RemotePairingDto> StartPairingAsync(CancellationToken ct = default) =>
        Do("StartPairing", () => (Remote = Remote with { Pairing = new RemotePairingDto("K7QF-M2XP-9HTD", DateTimeOffset.UtcNow.AddMinutes(5)) }).Pairing!);
    public Task CancelPairingAsync(CancellationToken ct = default) => Do("CancelPairing", () => Remote = Remote with { Pairing = null });
    public Task RemoveControllerAsync(string machineId, CancellationToken ct = default) =>
        Do($"RemoveController({machineId})", () => Remote = Remote with { Controllers = Remote.Controllers.Where(c => c.MachineId != machineId).ToList() });
    public Task<PairedMachineDto> PairAsync(string machineId, string code, CancellationToken ct = default) =>
        Do($"Pair({machineId}|{code})", () =>
        {
            var paired = new PairedMachineDto(machineId, Peers.FirstOrDefault(p => p.MachineId == machineId)?.MachineName ?? machineId,
                new string('b', 64), DateTimeOffset.UtcNow, null, Online: true);
            Remote = Remote with { Targets = [.. Remote.Targets, paired] };
            return paired;
        });
    public Task WakeAsync(string machineId, CancellationToken ct = default) => Do($"Wake({machineId})", () => (object?)null);

    public byte[] Backup { get; set; } = "{\"format\":\"gameshare-pairing-backup\",\"machineName\":\"PC-07\"}"u8.ToArray();
    public Task<byte[]> ExportPairingAsync(string password, CancellationToken ct = default) => Do($"ExportPairing({password})", () => Backup);
    public Task<RemoteRestoreResultDto> RestorePairingAsync(byte[] backup, string password, CancellationToken ct = default) =>
        Do($"RestorePairing({backup.Length}|{password})", () => new RemoteRestoreResultDto("PC-07", 1, 4));
    public Task RemoveTargetAsync(string machineId, CancellationToken ct = default) =>
        Do($"RemoveTarget({machineId})", () => Remote = Remote with { Targets = Remote.Targets.Where(t => t.MachineId != machineId).ToList() });

    /// <summary>The agent of each managed PC, by machine id. One is made on first use.</summary>
    public Dictionary<string, FakeAgent> TargetAgents { get; } = [];
    public IAgentClient ForTarget(string machineId) =>
        TargetAgents.TryGetValue(machineId, out var agent) ? agent : TargetAgents[machineId] = new FakeAgent { MachineName = machineId };

    private DownloadDto NewDownload(string hash, string kind)
    {
        var d = Downloads.Count + 1;
        return new DownloadDto(d, hash, "Game", "Downloading", 0, 1000, 0, 0, 0, null, null, []) { Kind = kind };
    }
}

/// <summary>Records what the client would have started, and can refuse the way the shell does.</summary>
internal sealed class FakeStarter : IGameStarter
{
    public List<LaunchInfoDto> Started { get; } = [];
    public AgentException? Failure { get; set; }

    public void Start(LaunchInfoDto info)
    {
        if (Failure is not null) throw Failure;
        Started.Add(info);
    }
}

public sealed class FakeSetupRunner : ISetupRunner
{
    public List<SetupPlanDto> Ran { get; } = [];
    /// <summary>What the next run reports, once. After it, and without it, every step that is not done yet succeeds.</summary>
    public List<SetupStepResultDto>? Results { get; set; }

    public Task<IReadOnlyList<SetupStepResultDto>> RunAsync(SetupPlanDto plan, CancellationToken ct = default)
    {
        Ran.Add(plan);
        var results = Results ?? plan.Steps.Where(s => !s.AlreadyDone).Select(s => new SetupStepResultDto(s.Title, true, null)).ToList();
        Results = null;
        return Task.FromResult<IReadOnlyList<SetupStepResultDto>>(results);
    }
}

/// <summary>Answers with a preset path, or null for a cancelled dialog.</summary>
internal sealed class FakeFolderPicker : IFolderPicker
{
    public string? NextPath { get; set; }
    public int Calls { get; private set; }

    public Task<string?> PickFolderAsync(CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(NextPath);
    }
}

/// <summary>Records what was put on the clipboard, instead of touching the real one.</summary>
internal sealed class FakeClipboard : IClipboard
{
    public string? Text { get; private set; }

    public Task SetTextAsync(string text, CancellationToken ct = default)
    {
        Text = text;
        return Task.CompletedTask;
    }
}

internal sealed class FakeFileDialogs : IFileDialogs
{
    public List<(string Name, byte[] Content)> Saved { get; } = [];
    public PickedFile? ToOpen { get; set; }
    public bool Cancel { get; set; }

    public Task<bool> SaveAsync(string suggestedName, byte[] content, CancellationToken ct = default)
    {
        if (Cancel) return Task.FromResult(false);
        Saved.Add((suggestedName, content));
        return Task.FromResult(true);
    }

    public Task<PickedFile?> OpenAsync(CancellationToken ct = default) => Task.FromResult(Cancel ? null : ToOpen);
}

internal sealed class FakeRemoteWindows : IRemoteWindows
{
    public List<(string MachineId, string Name)> Opened { get; } = [];
    public void Open(string machineId, string machineName) => Opened.Add((machineId, machineName));
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
            (sources ?? []).Select(s => new DownloadPeerDto(s.Name, "10.0.0.1", s.Rate, 0, IsSeed: false)).ToList()) { Kind = kind };

    public static PeerDto Peer(string id, string name, int games = 1, string? appVersion = null, string? playing = null) =>
        new(id, name, "192.168.30.10", 47702, games, DateTimeOffset.UtcNow, appVersion, playing);

    public static OfferedGameDto OfferedGame(string hash, string name, bool isComplete = true, double percentIntact = 100, string gameId = "game") =>
        new(hash, gameId, name, "0.38", 70_000_000_000) { IsComplete = isComplete, PercentIntact = percentIntact };
}
