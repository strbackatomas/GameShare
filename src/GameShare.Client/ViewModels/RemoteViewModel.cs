using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameShare.Client.Services;
using GameShare.Protocol;

namespace GameShare.Client.ViewModels;

/// <summary>A PC that may manage this one, with the button that takes that away.</summary>
public sealed class ControllerRow(PairedMachineDto m, IAsyncRelayCommand remove)
{
    public string MachineId { get; } = m.MachineId;
    public string Name { get; } = m.MachineName;
    public string DetailText { get; } = (m.LastUsed is { } used ? $"naposledy {Format.Date(used)}" : "zatím nic neudělal")
        + $" · spárováno {Format.Date(m.PairedAt)}";
    public IAsyncRelayCommand RemoveCommand { get; } = remove;
}

/// <summary>A PC this one may manage, with what it is doing, for the overview.</summary>
public sealed partial class TargetRow : ObservableObject
{
    public TargetRow(PairedMachineDto m, IRelayCommand manage, IAsyncRelayCommand remove, IAsyncRelayCommand? wake = null)
    {
        MachineId = m.MachineId;
        ManageCommand = manage;
        RemoveCommand = remove;
        WakeCommand = wake ?? new AsyncRelayCommand(() => Task.CompletedTask);
        Update(m);
    }

    public string MachineId { get; }
    public IRelayCommand ManageCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public IAsyncRelayCommand WakeCommand { get; }

    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWake))]
    public partial bool Online { get; set; }

    /// <summary>Its MAC address is known, so it can be woken.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWake))]
    public partial bool CanWake { get; set; }

    /// <summary>"Zapnout" instead of "Spravovat": it is off and can be woken.</summary>
    public bool ShowWake => !Online && CanWake;
    [ObservableProperty] public partial string DetailText { get; set; } = "";

    /// <summary>"v0.7.0", empty until it answered.</summary>
    [ObservableProperty] public partial string VersionText { get; set; } = "";
    [ObservableProperty] public partial bool IsVersionMismatch { get; set; }

    /// <summary>"2 přenosy · 54 % · 120 MB/s" or "nic nestahuje".</summary>
    [ObservableProperty] public partial string ActivityText { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    /// <summary>0 to 100 over all its transfers, for the bar.</summary>
    [ObservableProperty] public partial double Percent { get; set; }

    /// <summary>Free space in its game folders.</summary>
    [ObservableProperty] public partial string SpaceText { get; set; } = "";
    [ObservableProperty] public partial bool IsLowOnSpace { get; set; }

    /// <summary>Why it could not be asked, when it could not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    internal void Update(PairedMachineDto m)
    {
        Name = m.MachineName;
        Online = m.Online;
        CanWake = m.CanWake;
        DetailText = (m.Online ? "na síti" : "teď není na síti") + $" · spárováno {Format.Date(m.PairedAt)}";
        if (!m.Online) ShowOffline();
    }

    internal void ShowOffline()
    {
        ActivityText = SpaceText = VersionText = "";
        IsBusy = IsLowOnSpace = IsVersionMismatch = false;
        Problem = null;
    }

    /// <summary>Below this a game folder counts as nearly full. One big game alone is more than that.</summary>
    internal const long LowSpaceBytes = 20L * 1000 * 1000 * 1000;

    internal void Show(StatusDto status, IReadOnlyList<DownloadDto> downloads, IReadOnlyList<GameRootDto> roots, string localVersion)
    {
        Problem = null;
        VersionText = $"v{status.Version}";
        IsVersionMismatch = status.Version != localVersion;

        var active = downloads.Where(d => d.State is "Downloading" or "Queued" or "Verifying" or "Paused").ToList();
        IsBusy = active.Count > 0;
        long total = active.Sum(d => d.BytesTotal), done = active.Sum(d => d.BytesDone);
        Percent = total > 0 ? 100.0 * done / total : 0;
        var speed = Format.Speed(active.Sum(d => d.SpeedBytesPerSecond));
        ActivityText = active.Count == 0 ? "nic nestahuje"
            : string.Join(" · ", new[] { Transfers(active.Count), Format.Percent(Percent), speed }.Where(p => p.Length > 0))
              + (active.Count == 1 ? $" · {active[0].GameName}" : "");

        var known = roots.Where(r => r.FreeBytes is not null).ToList();
        SpaceText = known.Count == 0 ? "" : "volno " + string.Join(", ", known.Select(r => $"{DriveOf(r.Path)} {Format.Size(r.FreeBytes!.Value)}"));
        IsLowOnSpace = known.Count > 0 && known.All(r => r.FreeBytes < LowSpaceBytes);
    }

    private static string Transfers(int n) => n switch { 1 => "1 přenos", >= 2 and <= 4 => $"{n} přenosy", _ => $"{n} přenosů" };

    private static string DriveOf(string path) => Path.GetPathRoot(path) is { Length: > 0 } root ? root.TrimEnd('\\') : path;
}

/// <summary>A PC on the network this one could pair with.</summary>
public sealed record PeerChoice(string MachineId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Remote management, both ways. This PC as a target: whether it may be managed, the pairing code, and who may. This PC as
/// a controller: pairing with another PC by its code, and opening the window that manages it.
/// </summary>
public sealed partial class RemoteViewModel : ViewModelBase
{
    private const int MaxRecent = 20;
    private readonly AppModel _app;
    private bool _showing; // set while the switch is moved to match the agent, so that does not count as the user's click

    public RemoteViewModel(AppModel app)
    {
        _app = app;
        app.EventReceived += (_, e) =>
        {
            switch (e.Payload)
            {
                case RemoteStatusDto status: Show(status); break;
                case RemoteActionDto action:
                    Recent.Insert(0, Describe(action, app));
                    while (Recent.Count > MaxRecent) Recent.RemoveAt(Recent.Count - 1);
                    HasRecent = true;
                    break;
            }
        };
        app.Peers.CollectionChanged += (_, _) => RefreshChoices();
    }

    public ObservableCollection<ControllerRow> Controllers { get; } = [];
    public ObservableCollection<TargetRow> Targets { get; } = [];
    public ObservableCollection<PeerChoice> Choices { get; } = [];

    /// <summary>What paired PCs did here since this client started, newest first.</summary>
    public ObservableCollection<string> Recent { get; } = [];

    [ObservableProperty] public partial bool IsLoaded { get; set; }
    [ObservableProperty] public partial bool Allowed { get; set; } = true;
    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial string StateText { get; set; } = "";
    [ObservableProperty] public partial string FingerprintText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPairing))]
    public partial string PairingCode { get; set; } = "";
    [ObservableProperty] public partial string PairingUntilText { get; set; } = "";
    public bool HasPairing => PairingCode.Length > 0;

    [ObservableProperty] public partial bool HasControllers { get; set; }
    [ObservableProperty] public partial bool HasTargets { get; set; }
    [ObservableProperty] public partial bool HasChoices { get; set; }
    [ObservableProperty] public partial bool HasRecent { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PairCommand))]
    public partial PeerChoice? SelectedPeer { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PairCommand))]
    public partial string CodeInput { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PairCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial string Message { get; set; } = "";

    // ---- overview of the PCs managed from here ----

    private CancellationTokenSource? _overview;

    /// <summary>How often the overview asks the managed PCs while the page is open.</summary>
    internal TimeSpan OverviewInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Asks every managed PC what it is doing, again and again, until <see cref="StopOverview"/>. Call on the UI thread.</summary>
    public void StartOverview()
    {
        StopOverview();
        var cts = new CancellationTokenSource();
        _overview = cts;
        _ = OverviewLoopAsync(cts.Token);
    }

    public void StopOverview()
    {
        _overview?.Cancel();
        _overview = null;
    }

    private async Task OverviewLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshOverviewAsync().ConfigureAwait(true);
            try { await Task.Delay(OverviewInterval, ct).ConfigureAwait(true); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One round: every managed PC that is on the network, all at once.</summary>
    public async Task RefreshOverviewAsync()
    {
        var peers = _app.Peers.Select(p => p.MachineId).ToHashSet(StringComparer.Ordinal);
        await Task.WhenAll(Targets.ToList().Select(row => AskAsync(row, peers.Contains(row.MachineId)))).ConfigureAwait(true);
    }

    private async Task AskAsync(TargetRow row, bool online)
    {
        row.Online = online;
        if (!online)
        {
            row.ShowOffline();
            return;
        }
        var target = _app.Client.ForTarget(row.MachineId);
        try
        {
            var status = target.GetStatusAsync();
            var downloads = target.GetDownloadsAsync();
            var roots = target.GetGameRootsAsync();
            await Task.WhenAll(status, downloads, roots).ConfigureAwait(true);
            row.Show(status.Result, downloads.Result, roots.Result, _app.ClientVersion);
        }
        catch (AgentException ex) { row.Problem = ex.Message; }
    }

    /// <summary>Reads the state from the agent. Called when the page is opened; later changes arrive as events.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        await TryAsync(async () =>
        {
            Show(await _app.Client.GetRemoteAsync());
            IsLoaded = true;
        }, m => Message = m).ConfigureAwait(true);
    }

    partial void OnEnabledChanged(bool value)
    {
        if (_showing) return;
        _ = SetEnabledAsync(value);
    }

    private async Task SetEnabledAsync(bool enabled)
    {
        Message = "";
        if (!await TryAsync(async () => Show(await _app.Client.SetRemoteEnabledAsync(enabled)), m => Message = m).ConfigureAwait(true))
            await LoadAsync().ConfigureAwait(true); // the switch goes back to what is true
    }

    [RelayCommand]
    private async Task StartPairingAsync()
    {
        Message = "";
        await TryAsync(async () =>
        {
            var pairing = await _app.Client.StartPairingAsync();
            ShowPairing(pairing);
        }, m => Message = m).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CancelPairingAsync()
    {
        await TryAsync(() => _app.Client.CancelPairingAsync(), m => Message = m).ConfigureAwait(true);
        ShowPairing(null);
    }

    private bool CanPair => !IsBusy && SelectedPeer is not null && CodeInput.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanPair))]
    private async Task PairAsync()
    {
        if (SelectedPeer is not { } peer) return;
        IsBusy = true;
        Message = "";
        try
        {
            if (await TryAsync(() => _app.Client.PairAsync(peer.MachineId, CodeInput.Trim()), m => Message = m).ConfigureAwait(true))
            {
                CodeInput = "";
                Message = $"Spárováno s {peer.Name}. Teď ho můžeš spravovat odsud.";
                await LoadAsync().ConfigureAwait(true);
            }
        }
        finally { IsBusy = false; }
    }

    private async Task RemoveControllerAsync(ControllerRow row)
    {
        Message = "";
        if (await TryAsync(() => _app.Client.RemoveControllerAsync(row.MachineId), m => Message = m).ConfigureAwait(true))
            await LoadAsync().ConfigureAwait(true);
    }

    private async Task WakeAsync(TargetRow row)
    {
        Message = "";
        if (await TryAsync(() => _app.Client.WakeAsync(row.MachineId), m => Message = m).ConfigureAwait(true))
            row.DetailText = "probouzím… zapnutí může trvat minutu, pak se tu objeví jako na síti";
    }

    private async Task RemoveTargetAsync(TargetRow row)
    {
        Message = "";
        if (await TryAsync(() => _app.Client.RemoveTargetAsync(row.MachineId), m => Message = m).ConfigureAwait(true))
            await LoadAsync().ConfigureAwait(true);
    }

    private void Show(RemoteStatusDto s)
    {
        _showing = true;
        try { Enabled = s.Enabled; }
        finally { _showing = false; }

        Allowed = s.Allowed;
        StateText = !s.Allowed ? "Tato verze GameShare vzdálenou správu nepovoluje."
            : !s.Enabled ? "Vypnuto. Tento PC nikdo jiný spravovat nemůže."
            : s.Listening ? $"Zapnuto. Spárovaná PC mohou tento PC spravovat přes port {s.Port}."
            : $"Zapnuto, ale port {s.Port} se nepodařilo otevřít. Důvod je v Protokolu.";
        FingerprintText = Fingerprint(s.Fingerprint);
        ShowPairing(s.Pairing);

        Controllers.Clear();
        foreach (var c in s.Controllers.OrderBy(c => c.MachineName, StringComparer.OrdinalIgnoreCase))
        {
            ControllerRow? row = null;
            row = new ControllerRow(c, new AsyncRelayCommand(() => RemoveControllerAsync(row!)));
            Controllers.Add(row);
        }
        HasControllers = Controllers.Count > 0;

        // Rows are kept and updated, so what the overview found out about a PC does not blink away with each change.
        var wanted = s.Targets.OrderBy(t => t.MachineName, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var gone in Targets.Where(r => wanted.All(t => t.MachineId != r.MachineId)).ToList()) Targets.Remove(gone);
        for (int i = 0; i < wanted.Count; i++)
        {
            var t = wanted[i];
            var row = Targets.FirstOrDefault(r => r.MachineId == t.MachineId);
            if (row is null)
            {
                TargetRow? created = null;
                created = new TargetRow(t, new RelayCommand(() => _app.RemoteWindows.Open(t.MachineId, created!.Name)),
                    new AsyncRelayCommand(() => RemoveTargetAsync(created!)), new AsyncRelayCommand(() => WakeAsync(created!)));
                Targets.Insert(i, created);
            }
            else
            {
                row.Update(t);
                int at = Targets.IndexOf(row);
                if (at != i) Targets.Move(at, i);
            }
        }
        HasTargets = Targets.Count > 0;
        RefreshChoices();
    }

    private void ShowPairing(RemotePairingDto? pairing)
    {
        PairingCode = pairing?.Code ?? "";
        PairingUntilText = pairing is null ? "" : $"Platí do {pairing.ExpiresAt.ToLocalTime():HH:mm:ss} a jen pro jedno spárování.";
    }

    /// <summary>The PCs on the network that are not paired from here yet.</summary>
    private void RefreshChoices()
    {
        var paired = Targets.Select(t => t.MachineId).ToHashSet(StringComparer.Ordinal);
        var wanted = _app.Peers.Where(p => !paired.Contains(p.MachineId)).Select(p => new PeerChoice(p.MachineId, p.Name)).ToList();
        if (wanted.SequenceEqual(Choices)) return;
        var selected = SelectedPeer?.MachineId;
        Choices.Clear();
        foreach (var c in wanted) Choices.Add(c);
        SelectedPeer = Choices.FirstOrDefault(c => c.MachineId == selected) ?? (Choices.Count == 1 ? Choices[0] : null);
        HasChoices = Choices.Count > 0;
    }

    /// <summary>The first 16 hex digits in groups of four, enough to compare by eye.</summary>
    internal static string Fingerprint(string hex) =>
        hex.Length < 16 ? hex : string.Join(' ', Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4).ToUpperInvariant()));

    private static readonly Regex GameAction = new("^POST games/([0-9a-f]{64})/(install|update|repair|check)$");

    /// <summary>"12:03 · PC-01: instalace hry BeamNG.drive", for the list on this page and the tray.</summary>
    internal static string Describe(RemoteActionDto a, AppModel app)
    {
        string what;
        var game = GameAction.Match(a.Action);
        if (game.Success)
        {
            var name = app.Games.FirstOrDefault(g => g.ContentHash == game.Groups[1].Value)?.Name ?? "hra";
            what = game.Groups[2].Value switch
            {
                "install" => $"instalace hry {name}",
                "update" => $"aktualizace hry {name}",
                "repair" => $"oprava hry {name}",
                _ => $"kontrola souborů hry {name}",
            };
        }
        else
        {
            what = a.Action switch
            {
                "POST games/scan" => "prohledání složek s hrami",
                "POST app-update/check" => "hledání nové verze GameShare",
                "POST app-update/apply" => "instalace nové verze GameShare",
                _ when a.Action.EndsWith("/pause", StringComparison.Ordinal) => "pozastavení přenosu",
                _ when a.Action.EndsWith("/resume", StringComparison.Ordinal) => "pokračování přenosu",
                _ when a.Action.StartsWith("DELETE downloads/", StringComparison.Ordinal) => "zrušení přenosu",
                _ => a.Action,
            };
        }
        var failed = a.Status >= 400 ? " (nepovedlo se)" : "";
        return $"{a.At.ToLocalTime():HH:mm} · {a.ControllerName}: {what}{failed}";
    }
}
