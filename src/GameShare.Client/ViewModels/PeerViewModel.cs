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

    public PeerViewModel(PeerDto p, IAgentClient client)
    {
        _client = client;
        MachineId = p.MachineId;
        Apply(p);
    }

    public string MachineId { get; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Address { get; set; } = "";
    [ObservableProperty] public partial string GamesText { get; set; } = "";

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
