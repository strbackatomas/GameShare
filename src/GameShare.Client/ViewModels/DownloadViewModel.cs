using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameShare.Client.Services;
using GameShare.Protocol;

namespace GameShare.Client.ViewModels;

/// <summary>One line of a download's source list: which PC the data comes from and how fast.</summary>
public sealed record PeerSpeed(string Name, string Speed);

/// <summary>One row of the expanded peer table: every peer of a download, not just the ones currently sending.</summary>
public sealed record PeerDetailRow(string Name, string Address, string DownloadText, string UploadText, string RoleText);

public sealed partial class DownloadViewModel : ViewModelBase
{
    private readonly AppModel _app;

    public DownloadViewModel(DownloadDto d, AppModel app)
    {
        _app = app;
        Id = d.Id;
        ContentHash = d.ContentHash;
        Apply(d);
    }

    public long Id { get; }
    public string ContentHash { get; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string KindText { get; set; } = "";
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial string PercentText { get; set; } = "";
    [ObservableProperty] public partial string SizeText { get; set; } = "";
    [ObservableProperty] public partial string SpeedText { get; set; } = "";
    [ObservableProperty] public partial string EtaText { get; set; } = "";
    [ObservableProperty] public partial string StatsText { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    public partial string? Error { get; set; }

    /// <summary>Why it failed, or why the agent paused it by itself (a full disk).</summary>
    public bool ShowError => !string.IsNullOrEmpty(Error) && (IsFailed || IsPaused);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsRunning), nameof(IsVerifying), nameof(IsPaused), nameof(IsFinished), nameof(IsFailed), nameof(HasProgress), nameof(IsWaitingForSource), nameof(IsRunningWithSource), nameof(ShowError))]
    public partial string State { get; set; } = "";

    /// <summary>What the agent does about a stall: null, "Retrying" or "Reconnecting". See <see cref="DownloadDto.Recovery"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsWaitingForSource), nameof(IsRunningWithSource), nameof(IsRecovering))]
    public partial string? Recovery { get; set; }

    /// <summary>How many PCs this download is connected to right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsWaitingForSource), nameof(IsRunningWithSource))]
    public partial int PeerCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    /// <summary>Download speed as the agent reported it last, in bytes per second.</summary>
    public long Speed { get; private set; }

    /// <summary>This download's speed over the last minutes, for its own small graph.</summary>
    public SpeedHistory History { get; } = new();

    /// <summary>Where the data comes from right now, with each source's speed.</summary>
    public ObservableCollection<PeerSpeed> Sources { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    public partial bool IsExpanded { get; set; }

    public string Chevron => IsExpanded ? "▾" : "▸";

    /// <summary>Every peer of this download, for the expanded detail table.</summary>
    public ObservableCollection<PeerDetailRow> PeerRows { get; } = [];

    public bool IsRunning => State is "Downloading" or "Queued" or "Verifying";
    public bool IsPaused => State == "Paused";
    public bool IsFinished => State == "Completed";
    public bool IsFailed => State == "Failed";

    /// <summary>Running, but connected to no PC, so nothing is coming in. Saying "downloading" then would be a lie.</summary>
    public bool IsWaitingForSource => State == "Downloading" && PeerCount == 0 && !IsRecovering;

    /// <summary>Stuck, and the agent is getting it going again. Shown instead of a speed of zero, so nobody wonders why it stands.</summary>
    public bool IsRecovering => State == "Downloading" && Recovery is not null;
    public bool IsRunningWithSource => IsRunning && !IsWaitingForSource && !IsRecovering;
    public bool HasProgress => IsRunning || IsPaused;
    public bool CanAct => !IsBusy;
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public bool IsVerifying => State == "Verifying";

    public string StateText => PhaseText(State, IsRecovering, IsWaitingForSource);

    /// <summary>Which phase a download is in, in a few words. Shared with the game's card, so both say the same.</summary>
    internal static string PhaseText(string state, bool recovering, bool waiting) => state switch
    {
        "Downloading" when recovering => "Obnovuji spojení",
        "Downloading" => waiting ? "Čeká na zdroj" : "Stahuje se",
        "Queued" => "Ve frontě",
        "Verifying" => "Ověřuji soubory",
        "Paused" => "Pozastaveno",
        "Completed" => "Hotovo",
        "Failed" => "Selhalo",
        _ => state,
    };

    /// <summary>What a download does to the game. Shared with the game's card.</summary>
    internal static string KindLabel(string kind) => kind switch { "Update" => "Aktualizace", "Repair" => "Oprava", _ => "Instalace" };

    /// <summary>Why a download stands and what is being done, in a few words. Shared with the game's card.</summary>
    internal static string RecoveryText(string? recovery) => recovery switch
    {
        "Reconnecting" => "zdroj neposílal data, připojuji se znovu",
        _ => "zdroj přestal posílat data, žádám o ně znovu",
    };

    public void Apply(DownloadDto d)
    {
        Name = d.GameName;
        KindText = KindLabel(d.Kind);
        State = d.State;
        PeerCount = d.Peers;
        Recovery = d.Recovery;
        Percent = d.Percent;
        PercentText = Format.Percent(d.Percent);
        SizeText = $"{Format.Size(d.BytesDone)} z {Format.Size(d.BytesTotal)}";
        Speed = IsRunning ? d.SpeedBytesPerSecond : 0;
        if (d.State == "Downloading") History.Add(_app.Now(), Speed);
        SpeedText = IsRunningWithSource ? Format.Speed(d.SpeedBytesPerSecond) : "";
        EtaText = IsRecovering ? RecoveryText(Recovery)
            : IsWaitingForSource ? "žádné PC se hrou není připojené"
            : IsRunning ? Format.Eta(d.EtaSeconds) : "";
        StatsText = IsFinished && d.DurationSeconds is not null
            ? string.Join(" · ", new[] { $"Staženo za {Format.Duration(d.DurationSeconds)}", Format.Speed(d.PeakSpeedBytesPerSecond ?? 0) is { Length: > 0 } peak ? $"špička {peak}" : "" }.Where(s => s.Length > 0))
            : "";
        Error = d.Error;

        // Only sources that actually send something are worth a line.
        var active = d.PeerDetails.Where(p => p.DownloadRate > 0).OrderByDescending(p => p.DownloadRate).ToList();
        Sources.Clear();
        foreach (var p in active) Sources.Add(new PeerSpeed(p.Name, Format.Speed(p.DownloadRate)));

        // The expanded table shows every peer, sending or not.
        PeerRows.Clear();
        foreach (var p in d.PeerDetails.OrderByDescending(p => p.DownloadRate))
            PeerRows.Add(new PeerDetailRow(p.Name, p.Address, Format.Speed(p.DownloadRate), Format.Speed(p.UploadRate), p.IsSeed ? "Seed" : "Peer"));
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task PauseAsync() => Run(() => _app.Client.PauseAsync(Id));

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task ResumeAsync() => Run(() => _app.Client.ResumeAsync(Id));

    /// <summary>Forgets the download. Files already written are kept, and a repair or update never deletes an installed game.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task CancelAsync() => Run(() => _app.Client.CancelAsync(Id, deleteFiles: false));

    /// <summary>For a failed or cancelled install: deletes its partial files too, so nothing is left to retry and this
    /// row does not linger forever. A repair or update is never offered this, they never delete an installed game.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task DiscardAsync() => Run(() => _app.Client.CancelAsync(Id, deleteFiles: true));

    private async Task Run(Func<Task> action)
    {
        IsBusy = true;
        Message = null;
        try { await TryAsync(action, m => Message = m).ConfigureAwait(true); }
        finally { IsBusy = false; }
        await _app.RefreshDownloadsAsync().ConfigureAwait(true);
    }

    partial void OnIsBusyChanged(bool value)
    {
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }
}
