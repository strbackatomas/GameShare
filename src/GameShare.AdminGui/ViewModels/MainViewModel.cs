using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameShare.AdminGui.Services;
using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.AdminGui.ViewModels;

/// <summary>
/// Everything the graphical version of the admin tool does: make or load a key, load or start a list, scan a game folder
/// and vouch for it, withdraw or forget a version. Every change that touches the list signs and writes it at once,
/// there is no separate save step, so a list on disk is always the one that was last shown.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IFolderPicker _folders;
    private readonly IFilePicker _files;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _settingsPath;
    private TrustPayload _list = TrustPayload.Empty(DateTimeOffset.UtcNow);
    private TrustedGame? _scanned;

    /// <param name="settingsPath">Where the remembered paths are kept. Tests give their own, so they never touch the real profile.</param>
    public MainViewModel(IFolderPicker folders, IFilePicker files, Func<DateTimeOffset>? now = null, string? settingsPath = null)
    {
        _folders = folders;
        _files = files;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _settingsPath = settingsPath ?? LocalSettings.DefaultPath;

        var saved = LocalSettings.Load(_settingsPath);
        PrivateKeyPath = saved.PrivateKeyPath ?? "";
        ListPath = saved.ListPath ?? "";
    }

    public string Version => AppVersion.Current;

    public ObservableCollection<TrustedGameRow> VerifiedGames { get; } = [];
    public ObservableCollection<RevokedGameRow> RevokedGames { get; } = [];

    // ---- key ----

    [ObservableProperty] public partial string PrivateKeyPath { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKey))]
    public partial string? KeyId { get; set; }

    public string? PublicKey { get; private set; }
    public bool HasKey => KeyId is not null;

    [RelayCommand]
    private async Task BrowsePrivateKeyAsync()
    {
        var path = await _files.PickExistingFileAsync("Vyber soukromý klíč (trust-private.key)").ConfigureAwait(true);
        if (path is not null) PrivateKeyPath = path;
    }

    [RelayCommand]
    private async Task GenerateKeysAsync()
    {
        var folder = await _folders.PickFolderAsync("Kam uložit nové klíče").ConfigureAwait(true);
        if (folder is null) return;

        await RunAsync(async () =>
        {
            var info = await Task.Run(() => TrustWorkflow.GenerateKeys(folder, Pwd())).ConfigureAwait(true);
            ApplyKey(info);
            Message = $"Nové klíče vytvořeny v {folder}. Otisk klíče: {info.KeyId}. " +
                      $"Do nastavení každého agenta patří Agent:TrustPublicKey = {info.PublicKey}";
            RememberPaths();
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task LoadKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(PrivateKeyPath)) { Message = "Vyber soubor se soukromým klíčem, nebo nejdřív nějaký vytvoř."; return; }

        await RunAsync(async () =>
        {
            var info = await Task.Run(() => TrustWorkflow.LoadKey(PrivateKeyPath, Pwd())).ConfigureAwait(true);
            ApplyKey(info);
            Message = $"Klíč načten. Otisk: {info.KeyId}";
            RememberPaths();
        }).ConfigureAwait(true);
    }

    private void ApplyKey(TrustKeyInfo info)
    {
        PrivateKeyPath = info.PrivateKeyPath;
        PublicKey = info.PublicKey;
        KeyId = info.KeyId;
    }

    // ---- list ----

    [ObservableProperty] public partial string ListPath { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasList))]
    public partial long? Sequence { get; set; }

    public bool HasList => Sequence is not null;

    [ObservableProperty] public partial string ListSummary { get; set; } = "Seznam zatím není načtený.";

    [RelayCommand]
    private async Task BrowseListAsync()
    {
        var path = await _files.PickFileToSaveAsync("Vyber nebo vytvoř soubor se seznamem", "trust.json").ConfigureAwait(true);
        if (path is not null) ListPath = path;
    }

    [RelayCommand]
    private async Task LoadListAsync()
    {
        if (!HasKey) { Message = "Nejdřív načti nebo vytvoř klíč, seznam se ověřuje proti němu."; return; }
        if (string.IsNullOrWhiteSpace(ListPath)) { Message = "Vyber cestu k souboru se seznamem."; return; }

        await RunAsync(async () =>
        {
            var existed = File.Exists(ListPath);
            _list = await Task.Run(() => TrustWorkflow.LoadOrStartList(ListPath, PublicKey!, _now())).ConfigureAwait(true);
            RefreshListDisplay();
            RememberPaths();
            Message = existed ? "Seznam načten, podpis sedí." : "Nový, zatím prázdný seznam. Přidej hru, tím se poprvé podepíše a uloží.";
        }).ConfigureAwait(true);
    }

    private void RefreshListDisplay()
    {
        Sequence = _list.Sequence;
        ListSummary = $"Verze seznamu {_list.Sequence}, vydáno {_list.IssuedAt:d. M. yyyy}" +
            (_list.ValidUntil is { } until ? $", platí do {until:d. M. yyyy}" : ", bez omezení platnosti") +
            $". Ověřených her: {_list.Games.Count}, zrušených verzí: {_list.Revoked.Count}.";

        VerifiedGames.Clear();
        foreach (var g in _list.Games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            VerifiedGames.Add(new TrustedGameRow(g, RevokeAsync, RemoveAsync));

        RevokedGames.Clear();
        foreach (var r in _list.Revoked) RevokedGames.Add(new RevokedGameRow(r, ForgetAsync));
    }

    // ---- adding a game ----

    [ObservableProperty] public partial string GameFolder { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanned))]
    public partial string? ScannedName { get; set; }

    [ObservableProperty] public partial string? ScannedVersion { get; set; }
    [ObservableProperty] public partial string? ScannedHash { get; set; }
    public bool HasScanned => _scanned is not null;

    [RelayCommand]
    private async Task BrowseGameFolderAsync()
    {
        var path = await _folders.PickFolderAsync("Vyber složku hry (čistá instalace, bez uložených pozic)").ConfigureAwait(true);
        if (path is not null) GameFolder = path;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(GameFolder)) { Message = "Vyber složku hry."; return; }

        _scanned = null;
        ScannedName = ScannedVersion = ScannedHash = null;
        await RunAsync(async () =>
        {
            _scanned = await TrustWorkflow.ScanAsync(GameFolder).ConfigureAwait(true);
            ScannedName = _scanned.Name;
            ScannedVersion = _scanned.Version;
            ScannedHash = _scanned.ContentHash;
            Message = "Naskenováno. Zkontroluj název a verzi, pak přidej do seznamu.";
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddScannedGameAsync()
    {
        if (_scanned is null) { Message = "Nejdřív naskenuj složku hry."; return; }
        if (!HasKey) { Message = "Nejdřív načti nebo vytvoř klíč."; return; }
        if (!HasList) { Message = "Nejdřív načti nebo založ seznam."; return; }

        var game = _scanned;
        await RunAsync(async () =>
        {
            _list = TrustListEditor.Add(_list, [game], _now());
            await PublishAsync().ConfigureAwait(true);
            Message = $"{game.Name} {game.Version} přidáno a seznam podepsán.";
            _scanned = null;
            GameFolder = "";
            ScannedName = ScannedVersion = ScannedHash = null;
        }).ConfigureAwait(true);
    }

    // ---- per-row actions ----

    private Task RevokeAsync(TrustedGame game, string reason) => RunAsync(async () =>
    {
        _list = TrustListEditor.Revoke(_list, game.ContentHash, reason, _now());
        await PublishAsync().ConfigureAwait(true);
        Message = $"{game.Name} zrušeno: {reason}";
    });

    private Task RemoveAsync(TrustedGame game) => RunAsync(async () =>
    {
        _list = TrustListEditor.Remove(_list, game.ContentHash, _now());
        await PublishAsync().ConfigureAwait(true);
        Message = $"{game.Name} odebráno ze seznamu.";
    });

    private Task ForgetAsync(RevokedGame revoked) => RunAsync(async () =>
    {
        _list = TrustListEditor.Remove(_list, revoked.ContentHash, _now());
        await PublishAsync().ConfigureAwait(true);
        Message = "Odebráno ze seznamu zrušených verzí.";
    });

    private async Task PublishAsync()
    {
        await Task.Run(() => TrustWorkflow.Publish(_list, PrivateKeyPath, Pwd(), ListPath)).ConfigureAwait(true);
        RefreshListDisplay();
    }

    // ---- plumbing ----

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "";

    private async Task RunAsync(Func<Task> work)
    {
        IsBusy = true;
        try { await work().ConfigureAwait(true); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Message = ex.Message; }
        finally { IsBusy = false; }
    }

    private string? Pwd() => string.IsNullOrEmpty(Password) ? null : Password;

    private void RememberPaths() => LocalSettings.Save(_settingsPath, new GuiSettings(PrivateKeyPath, ListPath));
}
