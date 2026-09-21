using CommunityToolkit.Mvvm.ComponentModel;
using GameShare.Client.Services;
using GameShare.Protocol;

namespace GameShare.Client.ViewModels;

public sealed partial class PeerViewModel : ViewModelBase
{
    public PeerViewModel(PeerDto p)
    {
        MachineId = p.MachineId;
        Apply(p);
    }

    public string MachineId { get; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Address { get; set; } = "";
    [ObservableProperty] public partial string GamesText { get; set; } = "";

    public void Apply(PeerDto p)
    {
        Name = p.MachineName;
        Address = p.Address;
        GamesText = p.GameCount switch { 0 => "nenabízí žádnou hru", 1 => "nabízí 1 hru", >= 2 and <= 4 => $"nabízí {p.GameCount} hry", _ => $"nabízí {p.GameCount} her" };
    }
}
