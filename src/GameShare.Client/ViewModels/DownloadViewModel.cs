using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameShare.Client.Services;
using GameShare.Protocol;

namespace GameShare.Client.ViewModels;

/// <summary>One line of a download's source list: which PC the data comes from and how fast.</summary>
public sealed record PeerSpeed(string Name, string Speed);

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
    [ObservableProperty] public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsRunning), nameof(IsPaused), nameof(IsFinished), nameof(IsFailed), nameof(HasProgress))]
    public partial string State { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    /// <summary>Where the data comes from right now, with each source's speed.</summary>
    public ObservableCollection<PeerSpeed> Sources { get; } = [];

    public bool IsRunning => State is "Downloading" or "Queued" or "Verifying";
    public bool IsPaused => State == "Paused";
    public bool IsFinished => State == "Completed";
    public bool IsFailed => State == "Failed";
    public bool HasProgress => IsRunning || IsPaused;
    public bool CanAct => !IsBusy;
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public string StateText => State switch
    {
        "Downloading" => "Stahuje se",
        "Queued" => "Ve frontě",
        "Verifying" => "Ověřuji soubory",
        "Paused" => "Pozastaveno",
        "Completed" => "Hotovo",
        "Failed" => "Selhalo",
        _ => State,
    };

    public void Apply(DownloadDto d)
    {
        Name = d.GameName;
        KindText = d.Kind switch { "Update" => "Aktualizace", "Repair" => "Oprava", _ => "Instalace" };
        State = d.State;
        Percent = d.Percent;
        PercentText = Format.Percent(d.Percent);
        SizeText = $"{Format.Size(d.BytesDone)} z {Format.Size(d.BytesTotal)}";
        SpeedText = IsRunning ? Format.Speed(d.SpeedBytesPerSecond) : "";
        EtaText = IsRunning ? Format.Eta(d.EtaSeconds) : "";
        Error = d.Error;

        // Only sources that actually send something are worth a line.
        var active = d.PeerDetails.Where(p => p.DownloadRate > 0).OrderByDescending(p => p.DownloadRate).ToList();
        Sources.Clear();
        foreach (var p in active) Sources.Add(new PeerSpeed(p.Name, Format.Speed(p.DownloadRate)));
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task PauseAsync() => Run(() => _app.Client.PauseAsync(Id));

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task ResumeAsync() => Run(() => _app.Client.ResumeAsync(Id));

    /// <summary>Forgets the download. Files already written are kept, and a repair or update never deletes an installed game.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task CancelAsync() => Run(() => _app.Client.CancelAsync(Id, deleteFiles: false));

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
    }
}
