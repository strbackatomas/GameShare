using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
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

/// <summary>One piece of a log line: plain text, the leading timestamp (dimmed), or a value worth catching the eye
/// (a number, byte count, id, address or path) so a wall of INF lines is not one flat colour to wade through.</summary>
public sealed class LogSegment(string text, bool isValue = false, bool isTimestamp = false)
{
    public string Text { get; } = text;
    public bool IsValue { get; } = isValue;
    public bool IsTimestamp { get; } = isTimestamp;
}

/// <summary>One line of the agent's log, coloured by its level so a warning or error stands out from the rest.</summary>
public sealed class LogLineViewModel
{
    // AgentHost's Serilog template starts every line with "yyyy-MM-dd HH:mm:ss.fff", then "{Level:u3}" right after it.
    private static readonly Regex TimestampPattern = new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}", RegexOptions.Compiled);

    // Hex ids/hashes, IPv4 with an optional port, Windows/UNC paths, quoted text, and plain numbers (sizes, percentages,
    // counts with thousands separators). Longest/most specific alternatives first, so e.g. an IP is not cut at its first dot.
    private static readonly Regex ValuePattern = new(
        """\b[0-9a-fA-F]{8,}\b|\b\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?\b|[A-Za-z]:\\\S+|\\\\\S+|"[^"]*"|\b\d[\d,]*(?:\.\d+)?%?\b""",
        RegexOptions.Compiled);

    public LogLineViewModel(string text)
    {
        Text = text;
        IsError = HasLevel(text, "ERR") || HasLevel(text, "FTL");
        IsWarn = HasLevel(text, "WRN");
        IsDebug = HasLevel(text, "DBG") || HasLevel(text, "VRB");
        // A line already coloured whole by its level needs no further highlighting inside it.
        Segments = IsError || IsWarn || IsDebug ? [new LogSegment(text)] : BuildSegments(text);
    }

    public string Text { get; }
    public bool IsError { get; }
    public bool IsWarn { get; }
    public bool IsDebug { get; }
    public IReadOnlyList<LogSegment> Segments { get; }

    // AgentHost's Serilog template puts "{Level:u3}" right after the timestamp, so the 3-letter code always sits
    // between two single spaces: "...123 ERR message". A continuation line of a stack trace has none and stays plain.
    private static bool HasLevel(string text, string level) => text.Contains(' ' + level + ' ', StringComparison.Ordinal);

    private static IReadOnlyList<LogSegment> BuildSegments(string text)
    {
        var segments = new List<LogSegment>();
        int pos = 0;

        var ts = TimestampPattern.Match(text);
        if (ts.Success)
        {
            segments.Add(new LogSegment(ts.Value, isTimestamp: true));
            pos = ts.Length;
        }

        foreach (Match m in ValuePattern.Matches(text, pos))
        {
            if (m.Index > pos) segments.Add(new LogSegment(text[pos..m.Index]));
            segments.Add(new LogSegment(m.Value, isValue: true));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) segments.Add(new LogSegment(text[pos..]));

        return segments;
    }
}

/// <summary>The tail of the agent's own log file, read on demand. No trip to the data folder needed to see what it is doing.</summary>
public sealed partial class LogViewModel : ViewModelBase
{
    private readonly AppModel _app;

    public LogViewModel(AppModel app) => _app = app;

    public ObservableCollection<LogLineViewModel> Lines { get; } = [];

    [ObservableProperty] public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string Message { get; set; } = "";

    [ObservableProperty] public partial bool IsLoading { get; set; }

    public bool HasMessage => Message.Length > 0;

    /// <summary>Reads the log again. Called when the page is opened, and by its own Refresh button.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await TryAsync(async () =>
            {
                var lines = await _app.Client.GetLogTailAsync();
                Lines.Clear();
                foreach (var line in lines) Lines.Add(new LogLineViewModel(line));
                IsEmpty = Lines.Count == 0;
                Message = "";
            }, m => Message = m).ConfigureAwait(true);
        }
        finally { IsLoading = false; }
    }
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

    /// <summary>For the "O aplikaci" section: client and agent version.</summary>
    public AppModel App => _app;

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
        if (path.Length == 0) { Message = "Napiš cestu ke složce, nebo použij Procházet."; return; }
        AddRootPath(path);
    }

    /// <summary>Opens the Windows folder dialog. Adds what was picked the same way a typed path would be.</summary>
    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _app.FolderPicker.PickFolderAsync().ConfigureAwait(true);
        if (path is not null) AddRootPath(path);
    }

    private void AddRootPath(string path)
    {
        Message = "";
        if (!Roots.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))) Roots.Add(new RootItem(path, r => Roots.Remove(r)));
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
