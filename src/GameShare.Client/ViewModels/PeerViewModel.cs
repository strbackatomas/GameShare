using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameShare.Client.Services;
using GameShare.Protocol;

namespace GameShare.Client.ViewModels;

/// <summary>One game a peer offers, as a row under its expanded entry in the network view.</summary>
public sealed class PeerGameRow(OfferedGameDto g)
{
    public string Name { get; } = g.Name;
    public string StatusText { get; } = g.IsComplete ? "kompletní" : Format.Percent(g.PercentIntact);
}

public sealed partial class PeerViewModel : ViewModelBase
{
    private readonly IAgentClient _client;
    private readonly string _localVersion;

    public PeerViewModel(PeerDto p, IAgentClient client, string localVersion = "")
    {
        _client = client;
        _localVersion = localVersion;
        MachineId = p.MachineId;
        Apply(p);
    }

    public string MachineId { get; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Address { get; set; } = "";
    [ObservableProperty] public partial string GamesText { get; set; } = "";

    /// <summary>"v0.1.0", or empty for a peer old enough not to send its version.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVersionPlain))]
    public partial string VersionText { get; set; } = "";

    /// <summary>True when the peer's version is known and differs from this PC's. Informational only, nothing is blocked by it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVersionPlain))]
    public partial bool IsVersionMismatch { get; set; }

    /// <summary>The version shown as plain muted text. A mismatch is shown as a chip instead, see <see cref="IsVersionMismatch"/>.</summary>
    public bool ShowVersionPlain => VersionText.Length > 0 && !IsVersionMismatch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron), nameof(ShowNoGames))]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoGames))]
    public partial bool IsLoadingGames { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGamesError), nameof(ShowNoGames))]
    public partial string? GamesError { get; set; }

    public ObservableCollection<PeerGameRow> Games { get; } = [];

    public string Chevron => IsExpanded ? "▾" : "▸";
    public bool HasGamesError => !string.IsNullOrEmpty(GamesError);
    public bool ShowNoGames => IsExpanded && !IsLoadingGames && !HasGamesError && Games.Count == 0;

    public void Apply(PeerDto p)
    {
        Name = p.MachineName;
        Address = p.Address;
        GamesText = p.GameCount switch { 0 => "nenabízí žádnou hru", 1 => "nabízí 1 hru", >= 2 and <= 4 => $"nabízí {p.GameCount} hry", _ => $"nabízí {p.GameCount} her" };
        VersionText = string.IsNullOrEmpty(p.AppVersion) ? "" : $"v{p.AppVersion}";
        IsVersionMismatch = !string.IsNullOrEmpty(p.AppVersion) && !string.IsNullOrEmpty(_localVersion) && p.AppVersion != _localVersion;
    }

    [RelayCommand]
    private async Task ToggleExpandAsync()
    {
        if (IsExpanded) { IsExpanded = false; return; }
        IsExpanded = true;
        await LoadGamesAsync().ConfigureAwait(true);
    }

    private async Task LoadGamesAsync()
    {
        IsLoadingGames = true;
        GamesError = null;
        try
        {
            var games = await _client.GetPeerGamesAsync(MachineId).ConfigureAwait(true);
            Games.Clear();
            foreach (var g in games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)) Games.Add(new PeerGameRow(g));
        }
        catch (AgentException ex) { GamesError = ex.Message; }
        finally
        {
            IsLoadingGames = false;
            OnPropertyChanged(nameof(ShowNoGames));
        }
    }
}
