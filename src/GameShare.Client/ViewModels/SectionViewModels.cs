using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameShare.Client.Services;
using GameShare.Protocol;

namespace GameShare.Client.ViewModels;

/// <summary>"Moje hry" and "Na LAN": the same games split by whether they are on this PC.</summary>
public sealed partial class LibraryViewModel : ViewModelBase
{
    private readonly AppModel _app;

    public LibraryViewModel(AppModel app)
    {
        _app = app;
        app.GamesChanged += (_, _) => Rebuild();
        app.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppModel.IsConnected)) UpdateHint(); };
        Rebuild();
    }

    /// <summary>Installed, damaged and being installed.</summary>
    public ObservableCollection<GameCardViewModel> MyGames { get; } = [];

    /// <summary>Offered by other PCs and not on this one, including newer versions of games that are.</summary>
    public ObservableCollection<GameCardViewModel> LanGames { get; } = [];

    [ObservableProperty] public partial bool IsEmpty { get; set; }

    /// <summary>"No games yet" is only true when the agent answered. Without an agent the banner says what is wrong, not this.</summary>
    [ObservableProperty] public partial bool ShowEmptyHint { get; set; }
    [ObservableProperty] public partial bool HasMyGames { get; set; }
    [ObservableProperty] public partial bool HasLanGames { get; set; }
    [ObservableProperty] public partial string ScanText { get; set; } = "";
    [ObservableProperty] public partial bool IsScanning { get; set; }

    private void Rebuild()
    {
        Replace(MyGames, _app.Games.Where(g => g.State is GameState.Installed or GameState.Damaged or GameState.Downloading));
        Replace(LanGames, _app.Games.Where(g => g.State == GameState.AvailableOnLan));
        HasMyGames = MyGames.Count > 0;
        HasLanGames = LanGames.Count > 0;
        IsEmpty = !HasMyGames && !HasLanGames;
        UpdateHint();
    }

    private void UpdateHint() => ShowEmptyHint = IsEmpty && _app.IsConnected;

    /// <summary>Only touches the list when it really differs, so a row being clicked is not rebuilt under the pointer.</summary>
    private static void Replace(ObservableCollection<GameCardViewModel> target, IEnumerable<GameCardViewModel> items)
    {
        var wanted = items.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Details).ToList();
        if (target.SequenceEqual(wanted)) return;
        target.Clear();
        foreach (var g in wanted) target.Add(g);
    }

    /// <summary>Looks through the game folders again, for example after copying a game there by hand.</summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        IsScanning = true;
        ScanText = "Prohledávám složky s hrami…";
        try
        {
            await TryAsync(async () =>
            {
                var r = await _app.Client.ScanAsync();
                ScanText = r.Damaged.Count > 0
                    ? $"Nalezeno nových her: {r.Added}. Poškozené hry: {r.Damaged.Count}."
                    : $"Nalezeno nových her: {r.Added}. Beze změny: {r.Unchanged}.";
                if (r.Errors.Count > 0) ScanText += $" Chyby: {string.Join("; ", r.Errors)}";
            }, m => ScanText = m).ConfigureAwait(true);
        }
        finally { IsScanning = false; }
        await _app.RefreshGamesAsync().ConfigureAwait(true);
    }
}

public sealed class DownloadsViewModel(AppModel app) : ViewModelBase
{
    public AppModel App { get; } = app;
    public ObservableCollection<DownloadViewModel> Downloads => App.Downloads;
}

public sealed class NetworkViewModel(AppModel app) : ViewModelBase
{
    public AppModel App { get; } = app;
    public ObservableCollection<PeerViewModel> Peers => App.Peers;
}

/// <summary>One game folder in the settings list, with its own remove button.</summary>
public sealed class RootItem
{
    public RootItem(string path, Action<RootItem> remove)
    {
        Path = path;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Path { get; }
    public IRelayCommand RemoveCommand { get; }
}

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AppModel _app;

    public SettingsViewModel(AppModel app) => _app = app;

    public ObservableCollection<RootItem> Roots { get; } = [];

    [ObservableProperty] public partial string NewRoot { get; set; } = "";
    [ObservableProperty] public partial bool SeedingEnabled { get; set; } = true;
    [ObservableProperty] public partial string MaxUploadText { get; set; } = "";
    [ObservableProperty] public partial string MaxDownloadText { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial bool IsLoaded { get; set; }

    /// <summary>Whether the administrator's list of verified games is followed, and whether it loaded. Read only, it is set on the PC itself.</summary>
    [ObservableProperty] public partial string TrustText { get; set; } = "";
    [ObservableProperty] public partial bool TrustEnabled { get; set; }

    internal static string DescribeTrust(TrustStatusDto t)
    {
        if (t.Mode == TrustMode.Off) return "Ověřování her seznamem správce je vypnuté.";
        var mode = t.Mode == TrustMode.Require ? "instalují se jen ověřené hry" : "neověřené hry se jen označí";
        var list = t.HasList
            ? $"Seznam č. {t.Sequence} z {(t.IssuedAt is { } issued ? Format.Date(issued) : "?")}, ověřených her: {t.VerifiedCount}, zrušených verzí: {t.RevokedCount}."
            : "Seznam zatím není k dispozici.";
        var error = t.LastError is null ? "" : $" Poslední načtení selhalo: {t.LastError}";
        return $"Režim: {mode}. {list}{error}";
    }

    /// <summary>Asks where the list comes from again, for the administrator who has just published a new one.</summary>
    [RelayCommand]
    private async Task RefreshTrustAsync()
    {
        await TryAsync(async () =>
        {
            var t = await _app.Client.RefreshTrustAsync();
            TrustEnabled = t.Mode != TrustMode.Off;
            TrustText = DescribeTrust(t);
            await _app.RefreshGamesAsync();
        }, m => Message = m).ConfigureAwait(true);
    }

    /// <summary>Reads the current settings from the agent. Called when the page is opened.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        await TryAsync(async () =>
        {
            var s = await _app.Client.GetSettingsAsync();
            SetRoots(s.GameRoots);
            SeedingEnabled = s.SeedingEnabled;
            MaxUploadText = s.MaxUploadMBps?.ToString() ?? "";
            MaxDownloadText = s.MaxDownloadMBps?.ToString() ?? "";
            var trust = await _app.Client.GetTrustAsync();
            TrustEnabled = trust.Mode != TrustMode.Off;
            TrustText = DescribeTrust(trust);
            IsLoaded = true;
            Message = "";
        }, m => Message = m).ConfigureAwait(true);
    }

    [RelayCommand]
    private void AddRoot()
    {
        var path = NewRoot.Trim();
        NewRoot = "";
        if (path.Length == 0) return;
        if (!Roots.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))) Roots.Add(new RootItem(path, r => Roots.Remove(r)));
        NewRoot = "";
    }

    private void SetRoots(IEnumerable<string> paths)
    {
        Roots.Clear();
        foreach (var p in paths) Roots.Add(new RootItem(p, r => Roots.Remove(r)));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!TryParseLimit(MaxUploadText, out var up) || !TryParseLimit(MaxDownloadText, out var down))
        {
            Message = "Limit rychlosti musí být celé číslo v MB/s, nebo prázdný pro bez omezení.";
            return;
        }

        await TryAsync(async () =>
        {
            var saved = await _app.Client.SaveSettingsAsync(new SettingsDto([.. Roots.Select(r => r.Path)], SeedingEnabled, up, down));
            SetRoots(saved.GameRoots); // the agent normalises paths, show what it kept
            Message = "Uloženo.";
        }, m => Message = m).ConfigureAwait(true);
        await _app.RefreshGamesAsync().ConfigureAwait(true);
    }

    private static bool TryParseLimit(string text, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!int.TryParse(text.Trim(), out var n) || n <= 0) return false;
        value = n;
        return true;
    }
}
