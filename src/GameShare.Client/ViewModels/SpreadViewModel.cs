using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameShare.Client.Services;
using GameShare.Protocol;

namespace GameShare.Client.ViewModels;

/// <summary>What installing one game on a paired PC would do there.</summary>
public enum SpreadAction { None, Install, Update }

/// <summary>One paired PC in the list of where to install a game: whether it can take it, and how it went.</summary>
public sealed partial class SpreadTargetRow(string machineId, string name) : ObservableObject
{
    public string MachineId { get; } = machineId;
    public string Name { get; } = name;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    public partial SpreadAction Action { get; set; }

    public bool CanSelect => Action != SpreadAction.None;

    [ObservableProperty] public partial string StatusText { get; set; } = "zjišťuji…";
}

/// <summary>
/// Installs one game on several paired PCs with one click: each PC is asked what it has, and those that can take the game get an
/// install, or an update when they have an older version. Each PC downloads from the network on its own, this one only asks.
/// </summary>
/// <param name="close">Closes the panel, under the game's card.</param>
public sealed partial class SpreadViewModel(AppModel app, string contentHash, string gameName, Action? close = null) : ViewModelBase
{
    [RelayCommand]
    private void Close() => close?.Invoke();

    public string GameName { get; } = gameName;
    public ObservableCollection<SpreadTargetRow> Targets { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial string Message { get; set; } = "";

    public async Task LoadAsync()
    {
        IsBusy = true;
        Message = "";
        try
        {
            RemoteStatusDto? status = null;
            if (!await TryAsync(async () => status = await app.Client.GetRemoteAsync(), m => Message = m).ConfigureAwait(true)) return;
            Targets.Clear();
            foreach (var t in status!.Targets.OrderBy(t => t.MachineName, StringComparer.OrdinalIgnoreCase))
            {
                var row = new SpreadTargetRow(t.MachineId, t.MachineName);
                row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SpreadTargetRow.IsSelected)) InstallCommand.NotifyCanExecuteChanged(); };
                if (!t.Online) row.StatusText = "teď není na síti";
                Targets.Add(row);
            }
            if (Targets.Count == 0) Message = "Zatím nespravuješ žádné další PC. Spáruj je na stránce Vzdálená správa.";
            await Task.WhenAll(Targets.Where(r => r.StatusText != "teď není na síti").Select(AskAsync)).ConfigureAwait(true);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Looks at what the PC has of this game.</summary>
    private async Task AskAsync(SpreadTargetRow row)
    {
        try
        {
            var games = await app.Client.ForTarget(row.MachineId).GetGamesAsync().ConfigureAwait(true);
            var game = games.FirstOrDefault(g => g.ContentHash == contentHash);
            (row.Action, row.StatusText) = game switch
            {
                null => (SpreadAction.None, "hru v síti nevidí"),
                { State: GameState.Installed or GameState.Damaged } => (SpreadAction.None, "už ji má"),
                { State: GameState.Downloading } => (SpreadAction.None, "už ji stahuje"),
                { FullyAvailable: false } => (SpreadAction.None, "v síti teď nejsou všechny její části"),
                { UpdatesContentHash: not null } => (SpreadAction.Update, "má starší verzi, aktualizuje se"),
                _ => (SpreadAction.Install, "nainstaluje se"),
            };
            row.IsSelected = row.CanSelect;
        }
        catch (AgentException ex) { row.StatusText = ex.Message; }
    }

    private bool CanInstall => !IsBusy && Targets.Any(t => t.IsSelected && t.CanSelect);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        IsBusy = true;
        try
        {
            var chosen = Targets.Where(t => t.IsSelected && t.CanSelect).ToList();
            int started = (await Task.WhenAll(chosen.Select(StartAsync)).ConfigureAwait(true)).Count(ok => ok);
            Message = started == chosen.Count
                ? $"Začalo na {Format.PcCount(started)}. Průběh je v okně každého PC."
                : $"Začalo na {Format.PcCount(started)} z {chosen.Count}, u ostatních je napsáno proč ne.";
        }
        finally { IsBusy = false; }
    }

    private async Task<bool> StartAsync(SpreadTargetRow row)
    {
        var target = app.Client.ForTarget(row.MachineId);
        try
        {
            if (row.Action == SpreadAction.Update) await target.UpdateAsync(contentHash).ConfigureAwait(true);
            else await target.InstallAsync(contentHash).ConfigureAwait(true);
            row.StatusText = row.Action == SpreadAction.Update ? "začala aktualizace" : "začala instalace";
            row.Action = SpreadAction.None; // done with this one
            row.IsSelected = false;
            return true;
        }
        catch (AgentException ex)
        {
            row.StatusText = ex.Message;
            return false;
        }
    }
}
