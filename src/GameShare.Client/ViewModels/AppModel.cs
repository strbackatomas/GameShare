using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameShare.Client.Services;
using GameShare.Protocol;

namespace GameShare.Client.ViewModels;

/// <summary>
/// The client's picture of the agent: games, downloads and peers, loaded once and then kept current by the agent's events.
/// The agent is the source of truth. After every reconnect the whole picture is loaded again, so a missed event cannot linger.
/// </summary>
public sealed partial class AppModel : ViewModelBase, IAsyncDisposable
{
    private readonly IEventStream _events;
    private readonly IUiDispatcher _ui;

    public AppModel(
        IAgentClient client, IEventStream events, IUiDispatcher ui,
        IGameStarter? starter = null, IFolderPicker? folderPicker = null, IClipboard? clipboard = null, ISetupRunner? setupRunner = null,
        IClientRestarter? restarter = null, IRemoteWindows? remoteWindows = null, IFileDialogs? fileDialogs = null, IDeviceCheck? devices = null)
    {
        FileDialogs = fileDialogs ?? new NoFileDialogs();
        RemoteWindows = remoteWindows ?? new NoRemoteWindows();
        Restarter = restarter ?? new ProcessClientRestarter();
        Client = client;
        _events = events;
        _ui = ui;
        Starter = starter ?? new ProcessGameStarter();
        Devices = devices ?? new WindowsDeviceCheck();
        SetupRunner = setupRunner ?? new Setup.ProcessSetupRunner();
        FolderPicker = folderPicker ?? new NoFolderPicker();
        Clipboard = clipboard ?? new NoClipboard();
    }

    public IAgentClient Client { get; }

    /// <summary>
    /// This model shows another PC, managed from here through its agent. What only the person at that PC may do (play, prepare,
    /// uninstall, settings) is not offered, and this client never restarts itself because of that PC's version.
    /// </summary>
    public bool IsRemote { get; init; }

    /// <summary>Opens the window that manages a paired PC.</summary>
    public IRemoteWindows RemoteWindows { get; }

    /// <summary>Save and open dialogs, for the pairing backup.</summary>
    public IFileDialogs FileDialogs { get; }

    /// <summary>This PC manages at least one other. Games then offer to be installed there too.</summary>
    public bool HasRemoteTargets
    {
        get;
        private set
        {
            if (field == value) return;
            field = value;
            OnPropertyChanged();
            foreach (var card in Games) card.OnRemoteTargetsChanged();
        }
    }

    private async Task RefreshRemoteTargetsAsync(CancellationToken ct)
    {
        if (IsRemote) return; // that PC's pairings are its own business
        try { HasRemoteTargets = (await Client.GetRemoteAsync(ct).ConfigureAwait(true)).Targets.Count > 0; }
        catch (AgentException) { HasRemoteTargets = false; } // an agent from before remote management
    }

    /// <summary>Starts the client again once an update replaced its files.</summary>
    public IClientRestarter Restarter { get; }

    /// <summary>What starts a game once the agent has said it may be started.</summary>
    public IGameStarter Starter { get; }

    /// <summary>What is plugged in, for a game that crashes without it.</summary>
    public IDeviceCheck Devices { get; }

    /// <summary>Something changed where games are looked for (a folder was added), so the library should scan and show it.</summary>
    public event EventHandler? ScanRequested;

    public void RequestScan() => ScanRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Runs the preparation of a game the player confirmed, before it is first played.</summary>
    public ISetupRunner SetupRunner { get; }

    /// <summary>Lets the settings page offer a real folder dialog instead of a path typed by hand.</summary>
    public IFolderPicker FolderPicker { get; }

    /// <summary>Lets the log view copy its lines as plain text, since dragging the mouse across them cannot select text.</summary>
    public IClipboard Clipboard { get; }

    public ObservableCollection<GameCardViewModel> Games { get; } = [];
    public ObservableCollection<DownloadViewModel> Downloads { get; } = [];
    public ObservableCollection<PeerViewModel> Peers { get; } = [];

    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial string MachineName { get; set; } = "";
    [ObservableProperty] public partial string ConnectionText { get; set; } = "Připojuji se k agentovi…";

    /// <summary>This client's own build, always known. Shown in the header and compared against <see cref="AgentVersion"/>.</summary>
    public string ClientVersion => AppVersion.Current;

    /// <summary>What the agent this client is talking to reports. Normally the same as <see cref="ClientVersion"/>, they ship together.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersionMismatch))]
    public partial string AgentVersion { get; set; } = "";

    /// <summary>True once the agent has answered and its version differs from this client's own build.</summary>
    public bool HasVersionMismatch => AgentVersion.Length > 0 && AgentVersion != ClientVersion;

    // ---- GameShare's own updates ----

    /// <summary>Where the update of GameShare itself stands, as the agent says. Null until it answered, or from an agent too old to know.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateBanner), nameof(UpdateBannerText), nameof(CanApplyUpdate), nameof(UpdateText), nameof(UpdateNotes), nameof(HasUpdateNotes))]
    [NotifyCanExecuteChangedFor(nameof(ApplyUpdateCommand))]
    public partial AppUpdateStatusDto? AppUpdate { get; set; }

    /// <summary>Why asking for the update failed, when it did. Cleared by the next attempt.</summary>
    [ObservableProperty] public partial string UpdateMessage { get; set; } = "";

    /// <summary>The strip under the header: a new version is ready, or is being put in place.</summary>
    public bool HasUpdateBanner => AppUpdate?.State is AppUpdateState.Ready or AppUpdateState.Applying;

    public bool CanApplyUpdate => AppUpdate is { State: AppUpdateState.Ready, CanApply: true };

    public string UpdateBannerText => AppUpdate switch
    {
        { State: AppUpdateState.Applying } u => $"Instaluji GameShare {u.Version}. Za chvíli se sám restartuje.",
        { State: AppUpdateState.Ready, CanApply: true } u => $"Je připravená nová verze GameShare {u.Version}.",
        { State: AppUpdateState.Ready } u => $"Je stažená nová verze GameShare {u.Version}, ale tady se sama nainstalovat nedá. {u.CannotApplyReason}",
        _ => "",
    };

    /// <summary>For the settings page: the whole state in one sentence, with the last error.</summary>
    public string UpdateText => DescribeUpdate(AppUpdate);

    /// <summary>What changed in the version that is known, from CHANGELOG.md.</summary>
    public string UpdateNotes => AppUpdate?.State is AppUpdateState.Downloading or AppUpdateState.Ready or AppUpdateState.Applying or AppUpdateState.Available
        ? AppUpdate.Notes ?? "" : "";

    public bool HasUpdateNotes => UpdateNotes.Length > 0;

    internal static string DescribeUpdate(AppUpdateStatusDto? u)
    {
        if (u is null) return "Agent o aktualizacích neví, je ze starší verze.";
        var text = u.State switch
        {
            AppUpdateState.Disabled => u.DisabledReason ?? "Toto sestavení se samo neaktualizuje.",
            AppUpdateState.UpToDate => "Máš nejnovější verzi." + (u.LastChecked is { } at ? $" Naposledy ověřeno {Format.Date(at)}." : ""),
            AppUpdateState.Available => $"Je k dispozici verze {u.Version}, stažení se zkusí znovu.",
            AppUpdateState.Downloading when u.WaitingForLanUntil is { } until =>
                $"Je venku verze {u.Version}. Do {until.ToLocalTime():HH:mm} čekám, jestli ji nemá některé PC v síti, pak ji stáhnu z internetu. Zkontrolovat aktualizace ji stáhne hned.",
            AppUpdateState.Downloading => $"Stahuji verzi {u.Version}{FromWhere(u.Source)}: {Format.Percent(u.BytesTotal > 0 ? 100.0 * u.BytesDone / u.BytesTotal : 0)}.",
            AppUpdateState.Ready => $"Verze {u.Version} je stažená a ověřená." + (u.CannotApplyReason is { } why ? $" {why}" : " Nainstaluje se, až klikneš na Aktualizovat."),
            AppUpdateState.Applying => $"Instaluji verzi {u.Version}. GameShare se za chvíli restartuje.",
            _ => "",
        };
        return u.Error is { } error ? $"{text} {error}" : text;
    }

    private static string FromWhere(string? source) => source switch
    {
        null => "",
        "LAN" => " od ostatních PC v síti",
        _ when source.StartsWith("http", StringComparison.OrdinalIgnoreCase) => " z internetu",
        _ => " ze sdílené složky",
    };

    [RelayCommand(CanExecute = nameof(CanApplyUpdate))]
    private async Task ApplyUpdateAsync()
    {
        UpdateMessage = "";
        try { AppUpdate = await Client.ApplyAppUpdateAsync().ConfigureAwait(true); }
        catch (AgentException ex)
        {
            UpdateMessage = ex.Message;
            try { AppUpdate = await Client.GetAppUpdateAsync().ConfigureAwait(true); }
            catch (AgentException) { /* the banner keeps what it had */ }
        }
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        UpdateMessage = "";
        try { AppUpdate = await Client.CheckAppUpdateAsync().ConfigureAwait(true); }
        catch (AgentException ex) { UpdateMessage = ex.Message; }
    }

    private bool _restarting;

    /// <summary>
    /// After the service updated itself and this client's files with it, the agent answers with the new version and the program file
    /// on disk is the new one too, while this process still runs the old one. Then it starts again, once.
    /// </summary>
    private void RestartIfUpdated()
    {
        if (_restarting || IsRemote || AgentVersion.Length == 0 || AgentVersion == ClientVersion) return;
        if (Restarter.VersionOnDisk() != AgentVersion) return; // a thin client of another build, or a mismatch no restart would fix
        _restarting = true;
        Restarter.Restart();
    }

    /// <summary>Downloads that are running or paused. Drives the badge on the navigation bar.</summary>
    /// <summary>Total speed of everything this PC downloads, for the graph above the downloads. Recorded only while something downloads.</summary>
    public SpeedHistory DownloadSpeed { get; } = new();

    /// <summary>The clock the speed graphs go by. Tests set their own.</summary>
    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public int ActiveDownloadCount => Downloads.Count(d => d.HasProgress);

    /// <summary>Raised after games were added, removed or changed state, so lists built from them can be rebuilt.</summary>
    public event EventHandler? GamesChanged;

    /// <summary>Every event exactly as the agent sent it, after it was applied. For anything that reacts to one
    /// specific kind of event (for example the tray icon's notifications) without AppModel knowing about it.</summary>
    public event EventHandler<AgentEvent>? EventReceived;

    public async Task StartAsync(CancellationToken ct = default)
    {
        _events.Received += (_, e) => _ui.Post(() => Apply(e));
        _events.ConnectionChanged += (_, connected) => _ui.Post(() => _ = OnConnectionAsync(connected));

        await RefreshAllAsync(ct).ConfigureAwait(true); // works even if the event stream is slower to connect than the plain API
        await _events.StartAsync(ct).ConfigureAwait(true);
    }

    private async Task OnConnectionAsync(bool connected)
    {
        if (connected) await RefreshAllAsync().ConfigureAwait(true);
        else
        {
            IsConnected = false;
            ConnectionText = "Spojení s agentem se přerušilo. Zkouším se připojit znovu…";
        }
    }

    /// <summary>Loads everything. On failure the client shows that the agent is unreachable and keeps what it had.</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        try
        {
            var status = await Client.GetStatusAsync(ct).ConfigureAwait(true);
            MachineName = status.MachineName;
            AgentVersion = status.Version;
            RestartIfUpdated();
            AppUpdate = await Client.GetAppUpdateAsync(ct).ConfigureAwait(true);
            SyncGames(await Client.GetGamesAsync(ct).ConfigureAwait(true));
            SyncDownloads(await Client.GetDownloadsAsync(ct).ConfigureAwait(true));
            if (IsRemote) RecordDownloadSpeed(); // a managed PC sends no events, its graph is fed by these loads
            SyncPeers(await Client.GetPeersAsync(ct).ConfigureAwait(true));
            await RefreshRemoteTargetsAsync(ct).ConfigureAwait(true);
            IsConnected = true;
            ConnectionText = "Připojeno";
        }
        catch (AgentException ex)
        {
            IsConnected = false;
            ConnectionText = ex.Message;
        }
    }

    public async Task RefreshGamesAsync()
    {
        try { SyncGames(await Client.GetGamesAsync().ConfigureAwait(true)); }
        catch (AgentException ex) { ConnectionText = ex.Message; }
    }

    public async Task RefreshDownloadsAsync()
    {
        try { SyncDownloads(await Client.GetDownloadsAsync().ConfigureAwait(true)); SyncGames(await Client.GetGamesAsync().ConfigureAwait(true)); }
        catch (AgentException ex) { ConnectionText = ex.Message; }
    }

    // ---- events ----

    /// <summary>Applies one event. Must run on the UI thread.</summary>
    internal void Apply(AgentEvent e)
    {
        switch (e.Payload)
        {
            case PeerDto p when e.Name == GameShareEvents.PeerDisconnected: RemovePeer(p.MachineId); break;
            case PeerDto p: UpsertPeer(p); break;

            case GameDto g when e.Name == GameShareEvents.GameRemoved: RemoveGame(g.ContentHash); break;
            case GameDto g: UpsertGame(g); break;

            case DownloadDto d when e.Name == GameShareEvents.DownloadCancelled: RemoveDownload(d.Id); break;
            case DownloadDto d:
                UpsertDownload(d);
                RecordDownloadSpeed();
                break;

            case AppUpdateStatusDto u: AppUpdate = u; break;
            case RemoteStatusDto r: HasRemoteTargets = r.Targets.Count > 0; break;
        }

        EventReceived?.Invoke(this, e);
    }

    // ---- games ----

    private void SyncGames(IReadOnlyList<GameDto> games)
    {
        var wanted = games.ToDictionary(g => g.ContentHash);
        foreach (var card in Games.Where(c => !wanted.ContainsKey(c.ContentHash)).ToList()) Games.Remove(card);
        foreach (var g in games) UpsertGame(g, notify: false);
        GamesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpsertGame(GameDto g, bool notify = true)
    {
        var card = Games.FirstOrDefault(c => c.ContentHash == g.ContentHash);
        if (card is null) Games.Add(new GameCardViewModel(g, this));
        else card.Apply(g);
        if (notify) GamesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveGame(string contentHash)
    {
        var card = Games.FirstOrDefault(c => c.ContentHash == contentHash);
        if (card is not null) Games.Remove(card);
        GamesChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- downloads ----

    private void SyncDownloads(IReadOnlyList<DownloadDto> downloads)
    {
        var wanted = downloads.ToDictionary(d => d.Id);
        foreach (var vm in Downloads.Where(v => !wanted.ContainsKey(v.Id)).ToList()) Downloads.Remove(vm);
        foreach (var d in downloads) UpsertDownload(d);
        OnPropertyChanged(nameof(ActiveDownloadCount));
    }

    private void UpsertDownload(DownloadDto d)
    {
        // The card of the game shows the same progress. This runs for snapshots too, so a window opened mid-download is not stuck at 0.
        if (d.State is "Downloading" or "Queued" or "Verifying" or "Paused")
            Games.FirstOrDefault(c => c.ContentHash == d.ContentHash)?.ApplyProgress(d);

        var vm = Downloads.FirstOrDefault(v => v.Id == d.Id);
        if (vm is null)
        {
            Downloads.Insert(0, new DownloadViewModel(d, this)); // newest on top
            ResortDownloads();
            OnPropertyChanged(nameof(ActiveDownloadCount));
            return;
        }

        bool wasActive = vm.HasProgress;
        vm.Apply(d);
        if (wasActive != vm.HasProgress) // progress events are frequent, so only reorder when the state class changed
        {
            ResortDownloads();
            OnPropertyChanged(nameof(ActiveDownloadCount));
        }
    }

    /// <summary>One sample of the total speed. Each download reports on its own, so this sums the latest report of every one of them.</summary>
    private void RecordDownloadSpeed()
    {
        if (Downloads.Any(v => v.State == "Downloading"))
            DownloadSpeed.Add(Now(), Downloads.Sum(v => v.Speed));
    }

    /// <summary>What is happening now first, newest first within each group. Moves only what is out of place.</summary>
    private void ResortDownloads()
    {
        var desired = Downloads.OrderByDescending(v => v.HasProgress).ThenByDescending(v => v.Id).ToList();
        for (int i = 0; i < desired.Count; i++)
        {
            int current = Downloads.IndexOf(desired[i]);
            if (current != i) Downloads.Move(current, i);
        }
    }

    private void RemoveDownload(long id)
    {
        var vm = Downloads.FirstOrDefault(v => v.Id == id);
        if (vm is not null) Downloads.Remove(vm);
        OnPropertyChanged(nameof(ActiveDownloadCount));
    }

    // ---- peers ----

    private void SyncPeers(IReadOnlyList<PeerDto> peers)
    {
        var wanted = peers.ToDictionary(p => p.MachineId);
        foreach (var vm in Peers.Where(v => !wanted.ContainsKey(v.MachineId)).ToList()) Peers.Remove(vm);
        foreach (var p in peers) UpsertPeer(p);
    }

    private void UpsertPeer(PeerDto p)
    {
        var vm = Peers.FirstOrDefault(v => v.MachineId == p.MachineId);
        if (vm is null)
        {
            var newVm = new PeerViewModel(p, Client, ClientVersion);
            int i = 0;
            while (i < Peers.Count && string.Compare(Peers[i].Name, newVm.Name, StringComparison.OrdinalIgnoreCase) < 0) i++;
            Peers.Insert(i, newVm);
        }
        else vm.Apply(p);
    }

    private void RemovePeer(string machineId)
    {
        var vm = Peers.FirstOrDefault(v => v.MachineId == machineId);
        if (vm is not null) Peers.Remove(vm);
    }

    public ValueTask DisposeAsync() => _events.DisposeAsync();
}
