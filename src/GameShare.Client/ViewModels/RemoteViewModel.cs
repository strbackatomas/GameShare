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

/// <summary>A PC this one may manage.</summary>
public sealed class TargetRow(PairedMachineDto m, IRelayCommand manage, IAsyncRelayCommand remove)
{
    public string MachineId { get; } = m.MachineId;
    public string Name { get; } = m.MachineName;
    public bool Online { get; } = m.Online;
    public string DetailText { get; } = (m.Online ? "na síti" : "teď není na síti") + $" · spárováno {Format.Date(m.PairedAt)}";
    public IRelayCommand ManageCommand { get; } = manage;
    public IAsyncRelayCommand RemoveCommand { get; } = remove;
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

        Targets.Clear();
        foreach (var t in s.Targets.OrderBy(t => t.MachineName, StringComparer.OrdinalIgnoreCase))
        {
            TargetRow? row = null;
            row = new TargetRow(t, new RelayCommand(() => _app.RemoteWindows.Open(t.MachineId, t.MachineName)),
                new AsyncRelayCommand(() => RemoveTargetAsync(row!)));
            Targets.Add(row);
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
