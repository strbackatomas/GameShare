using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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
        IGameStarter? starter = null, IFolderPicker? folderPicker = null, IClipboard? clipboard = null, ISetupRunner? setupRunner = null)
    {
        Client = client;
        _events = events;
        _ui = ui;
        Starter = starter ?? new ProcessGameStarter();
        SetupRunner = setupRunner ?? new Setup.ProcessSetupRunner();
        FolderPicker = folderPicker ?? new NoFolderPicker();
        Clipboard = clipboard ?? new NoClipboard();
    }

    public IAgentClient Client { get; }

    /// <summary>What starts a game once the agent has said it may be started.</summary>
    public IGameStarter Starter { get; }

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

    /// <summary>Downloads that are running or paused. Drives the badge on the navigation bar.</summary>
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
            SyncGames(await Client.GetGamesAsync(ct).ConfigureAwait(true));
            SyncDownloads(await Client.GetDownloadsAsync(ct).ConfigureAwait(true));
            SyncPeers(await Client.GetPeersAsync(ct).ConfigureAwait(true));
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
                break;
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
        if (d.State is "Downloading" or "Queued" or "Verifying")
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
