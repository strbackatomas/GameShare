using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameShare.Client.Services;
using GameShare.Protocol;

namespace GameShare.Client.ViewModels;

/// <summary>One game version as a row in the library: what it is, where it stands, and what can be done with it.</summary>
public sealed partial class GameCardViewModel : ViewModelBase
{
    private readonly AppModel _app;
    private GameChangesDto? _lastCheck;

    /// <summary>What the agent noticed the game changing while it was played, before anyone asked for a check.</summary>
    private IReadOnlyList<string> _noticedPatterns = [];

    /// <summary>The patterns to offer: the result of a check the user ran wins over what the agent noticed by itself.</summary>
    private IReadOnlyList<string> SuggestedPatterns => _lastCheck?.SuggestedPatterns ?? _noticedPatterns;

    public GameCardViewModel(GameDto game, AppModel app)
    {
        _app = app;
        ContentHash = game.ContentHash;
        Apply(game);
    }

    /// <summary>The identity of this version, and its id in the agent's API.</summary>
    public string ContentHash { get; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Details { get; set; } = "";
    [ObservableProperty] public partial string PeersText { get; set; } = "";
    [ObservableProperty] public partial string? InstallPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(IsDamaged), nameof(IsDownloading), nameof(IsAvailable), nameof(CanInstall), nameof(CanUpdate), nameof(ShowInstallButton), nameof(StateText), nameof(CanPlay), nameof(NeedsExecutable), nameof(ShowUninstallButton))]
    public partial GameState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(CanUpdate), nameof(ShowInstallButton))]
    public partial string? UpdatesContentHash { get; set; }

    /// <summary>False when the PCs that are online do not have all of the game between them, so installing would stall.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(CanUpdate), nameof(ShowInstallButton), nameof(IsWaitingForParts), nameof(StateText))]
    public partial bool FullyAvailable { get; set; } = true;

    [ObservableProperty] public partial string CoverageText { get; set; } = "";

    /// <summary>Ready: it can be started. NeedsExecutable: the player picks which program starts it. Only for a game installed here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay), nameof(NeedsExecutable))]
    public partial LaunchState Launch { get; set; }

    /// <summary>A program of the game is running on this PC. Its files are then not rewritten, so repairing and updating wait.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay), nameof(CanModify))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand), nameof(RepairCommand), nameof(RegisterCommand), nameof(UninstallPromptCommand), nameof(ConfirmUninstallCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>The programs of the game to choose from, once the player asked to choose.</summary>
    public ObservableCollection<string> Executables { get; } = [];

    [ObservableProperty] public partial string? SelectedExecutable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    public partial bool IsChoosingExecutable { get; set; }

    /// <summary>The configured game folders to install into, once there is more than one and the player is asked to pick.</summary>
    public ObservableCollection<InstallRootOption> InstallRoots { get; } = [];

    [ObservableProperty] public partial InstallRootOption? SelectedInstallRoot { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallButton))]
    public partial bool IsChoosingInstallFolder { get; set; }

    /// <summary>The player is asked to confirm before the game's files are deleted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUninstallButton))]
    public partial bool IsConfirmingUninstall { get; set; }

    /// <summary>What the administrator's signed list says about this version. Nothing is shown when checking is off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrust), nameof(TrustText), nameof(IsTrustVerified), nameof(IsTrustUnknown), nameof(IsTrustRevoked))]
    public partial TrustVerdict Trust { get; set; }

    /// <summary>For a revoked version, the administrator's reason.</summary>
    [ObservableProperty] public partial string? TrustNote { get; set; }

    /// <summary>0 to 100, only meaningful while the game is being downloaded.</summary>
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; } = "";

    /// <summary>An operation on this game is running, so its buttons are off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct), nameof(CanModify))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(ChooseExecutableCommand), nameof(SaveExecutableCommand), nameof(InstallCommand), nameof(ConfirmInstallCommand),
        nameof(UpdateCommand), nameof(RepairCommand), nameof(CheckCommand), nameof(MarkVolatileCommand), nameof(RegisterCommand), nameof(UninstallPromptCommand), nameof(ConfirmUninstallCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestion))]
    public partial string? Suggestion { get; set; }

    public bool IsInstalled => State == GameState.Installed;
    public bool IsDamaged => State == GameState.Damaged;
    public bool IsDownloading => State == GameState.Downloading;
    public bool IsAvailable => State == GameState.AvailableOnLan;
    public bool CanInstall => IsAvailable && FullyAvailable && UpdatesContentHash is null;
    public bool CanUpdate => IsAvailable && FullyAvailable && UpdatesContentHash is not null;

    /// <summary>Hidden while the player is picking which folder to install into.</summary>
    public bool ShowInstallButton => CanInstall && !IsChoosingInstallFolder;

    /// <summary>Hidden while the player is confirming the uninstall.</summary>
    public bool ShowUninstallButton => (IsInstalled || IsDamaged) && !IsConfirmingUninstall;

    /// <summary>Offered by PCs that were used to play it, and the missing parts are not among the PCs that are online.</summary>
    public bool IsWaitingForParts => IsAvailable && !FullyAvailable;
    public bool HasTrust => Trust != TrustVerdict.NotChecked;
    public bool IsTrustVerified => Trust == TrustVerdict.Verified;
    public bool IsTrustUnknown => Trust == TrustVerdict.Unknown;
    public bool IsTrustRevoked => Trust == TrustVerdict.Revoked;

    public string TrustText => Trust switch
    {
        TrustVerdict.Verified => "Ověřeno správcem",
        TrustVerdict.Unknown => "Není v seznamu správce",
        TrustVerdict.Revoked => "Zrušeno správcem",
        _ => "",
    };

    /// <summary>A damaged game can be played too: playing changes files, which is what marks it. Whether the program itself is intact is the agent's call.</summary>
    public bool CanPlay => (IsInstalled || IsDamaged) && Launch == LaunchState.Ready && !IsRunning && !IsChoosingExecutable;
    public bool NeedsExecutable => (IsInstalled || IsDamaged) && Launch == LaunchState.NeedsExecutable && !IsChoosingExecutable;

    /// <summary>Repairing, updating and registering rewrite the files, which a running game holds.</summary>
    public bool CanModify => !IsBusy && !IsRunning;

    public bool CanAct => !IsBusy;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasSuggestion => !string.IsNullOrEmpty(Suggestion);

    public string StateText => State switch
    {
        GameState.Installed => "Nainstalováno",
        GameState.Damaged => "Soubory se změnily",
        GameState.Downloading => "Stahuje se",
        GameState.AvailableOnLan when !FullyAvailable => "Zatím nekompletní",
        GameState.AvailableOnLan => UpdatesContentHash is null ? "Dostupné na LAN" : "Nová verze na LAN",
        _ => "Nedostupné",
    };

    public void Apply(GameDto g)
    {
        Name = g.Name;
        State = g.State;
        InstallPath = g.InstallPath;
        UpdatesContentHash = g.UpdatesContentHash;
        Details = string.IsNullOrEmpty(g.Version) ? Format.Size(g.TotalSize) : $"{g.Version} · {Format.Size(g.TotalSize)}";
        PeersText = g.State == GameState.AvailableOnLan && g.PeerNames.Count > 0 ? DescribePeers(g) : "";
        Launch = g.Launch;
        IsRunning = g.IsRunning;
        Trust = g.Trust;
        TrustNote = g.TrustNote;
        FullyAvailable = g.FullyAvailable;
        CoverageText = g.State == GameState.AvailableOnLan && !g.FullyAvailable
            ? $"Dohromady je k dispozici jen {Format.Percent(g.CoveragePercent ?? 0)} dat hry. Instalace půjde, až se objeví PC s chybějícími částmi."
            : "";
        if (g.State != GameState.Downloading) { Percent = 0; ProgressText = ""; }
        // The message and the suggestion of a check the user ran must survive a refresh of the game.
        // What the agent noticed by itself follows the game: it appears while the game is played and goes when the game is intact again.
        _noticedPatterns = g.State == GameState.Damaged ? g.SuggestedPatterns : [];
        if (_lastCheck is null)
            Suggestion = _noticedPatterns.Count == 0 ? null
                : $"Hra při hraní změnila {Format.FileCount(g.ChangedFileCount)}. Pokud jsou to nastavení nebo savy, označ je jako proměnné: {string.Join(", ", _noticedPatterns)}";
    }

    /// <summary>"Nabízí 3 PC: PC-01, PC-04, PC-08 (jen část: PC-04)". A PC that was used to play the game has only part of it.</summary>
    private static string DescribePeers(GameDto g)
    {
        var text = $"Nabízí {Format.PcCount(g.PeerNames.Count)}: {string.Join(", ", g.PeerNames)}";
        if (g.PartialPeerNames.Count == 0) return text;
        return g.PartialPeerNames.Count == g.PeerNames.Count
            ? text + " (každé jen část hry)"
            : text + $" (jen část: {string.Join(", ", g.PartialPeerNames)})";
    }

    public void ApplyProgress(DownloadDto d)
    {
        Percent = d.Percent;
        var speed = Format.Speed(d.SpeedBytesPerSecond);
        var eta = Format.Eta(d.EtaSeconds);
        ProgressText = string.Join(" · ", new[] { Format.Percent(d.Percent), speed, eta }.Where(s => s.Length > 0));
    }

    /// <summary>The agent checks the game and says what to start, the client starts it. A refusal is shown as the agent worded it.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task PlayAsync()
    {
        IsBusy = true;
        Message = null;
        try
        {
            await TryAsync(async () =>
            {
                var info = await _app.Client.LaunchAsync(ContentHash);
                _app.Starter.Start(info);
                Message = "Hra se spouští…";
            }, m => Message = m).ConfigureAwait(true);
        }
        finally { IsBusy = false; }
    }

    /// <summary>For a game that does not say which program starts it: lists the programs of the game to pick from.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ChooseExecutableAsync()
    {
        IsBusy = true;
        Message = null;
        try
        {
            await TryAsync(async () =>
            {
                var programs = await _app.Client.GetExecutablesAsync(ContentHash);
                Executables.Clear();
                foreach (var p in programs) Executables.Add(p);
                SelectedExecutable = Executables.FirstOrDefault();
                IsChoosingExecutable = Executables.Count > 0;
                if (Executables.Count == 0) Message = "Hra neobsahuje žádný program, který by šel spustit.";
            }, m => Message = m).ConfigureAwait(true);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SaveExecutableAsync()
    {
        if (string.IsNullOrEmpty(SelectedExecutable)) return;
        IsBusy = true;
        try
        {
            await TryAsync(async () =>
            {
                await _app.Client.ChooseExecutableAsync(ContentHash, SelectedExecutable, null);
                IsChoosingExecutable = false;
                Message = "Uloženo. Hru teď spustíš tlačítkem Hrát.";
            }, m => Message = m).ConfigureAwait(true);
        }
        finally { IsBusy = false; }
        await _app.RefreshGamesAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelChoosingExecutable() => IsChoosingExecutable = false;

    /// <summary>With one configured folder, installs straight into it. With several, asks which one first.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task InstallAsync()
    {
        IsBusy = true;
        Message = null;
        try
        {
            await TryAsync(async () =>
            {
                var roots = await _app.Client.GetGameRootsAsync();
                if (roots.Count <= 1) { await _app.Client.InstallAsync(ContentHash); return; }

                InstallRoots.Clear();
                foreach (var r in roots) InstallRoots.Add(new InstallRootOption(r.Path, DescribeRoot(r)));
                SelectedInstallRoot = InstallRoots.FirstOrDefault();
                IsChoosingInstallFolder = true;
            }, m => Message = m).ConfigureAwait(true);
        }
        finally { IsBusy = false; }

        if (IsChoosingInstallFolder) return;
        Message ??= "Instalace začala.";
        await _app.RefreshDownloadsAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ConfirmInstallAsync()
    {
        if (SelectedInstallRoot is null) return;
        IsBusy = true;
        try
        {
            if (await TryAsync(() => _app.Client.InstallAsync(ContentHash, SelectedInstallRoot.Path), m => Message = m).ConfigureAwait(true))
            {
                Message = "Instalace začala.";
                IsChoosingInstallFolder = false;
            }
        }
        finally { IsBusy = false; }
        await _app.RefreshDownloadsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelChoosingInstallFolder() => IsChoosingInstallFolder = false;

    private static string DescribeRoot(GameRootDto root) =>
        root.FreeBytes is null ? root.Path : $"{root.Path}  ({Format.Size(root.FreeBytes.Value)} volno)";

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task UpdateAsync() => Run(() => _app.Client.UpdateAsync(ContentHash), "Aktualizace začala.");

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task RepairAsync() => Run(() => _app.Client.RepairAsync(ContentHash), "Oprava začala.");

    /// <summary>Verifies every file, which takes a while on a large game, and says what differs.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task CheckAsync()
    {
        IsBusy = true;
        Message = "Kontroluji soubory…";
        Suggestion = null;
        try
        {
            await TryAsync(async () =>
            {
                _lastCheck = await _app.Client.CheckAsync(ContentHash);
                if (_lastCheck.IsIntact) { Message = "Vše je v pořádku."; return; }

                Message = $"Změněno souborů: {_lastCheck.Modified.Count}, chybí: {_lastCheck.Missing.Count}. Nezměněné části se dál nabízejí ostatním.";
                if (_lastCheck.SuggestedPatterns.Count > 0)
                    Suggestion = "Pokud je hra mění při hraní (nastavení, savy), označ je jako proměnné: " + string.Join(", ", _lastCheck.SuggestedPatterns);
            }, m => Message = m).ConfigureAwait(true);
        }
        finally { IsBusy = false; }
        await _app.RefreshGamesAsync().ConfigureAwait(true);
    }

    /// <summary>The changed files are what the game writes itself. They stop counting as game content and are never shared or overwritten.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task MarkVolatileAsync()
    {
        var patterns = SuggestedPatterns;
        if (patterns.Count == 0) return;
        IsBusy = true;
        try
        {
            await TryAsync(async () =>
            {
                await _app.Client.AddVolatileAsync(ContentHash, patterns);
                Message = "Označeno. Tyto soubory se už nepočítají do hry.";
                Suggestion = null;
                _lastCheck = null;
                _noticedPatterns = [];
            }, m => Message = m).ConfigureAwait(true);
        }
        finally { IsBusy = false; }
        await _app.RefreshGamesAsync().ConfigureAwait(true);
    }

    /// <summary>After deliberately patching a game: the files as they are now become its new version.</summary>
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RegisterAsync()
    {
        IsBusy = true;
        Message = "Registruji aktuální soubory jako novou verzi…";
        try
        {
            await TryAsync(async () =>
            {
                await _app.Client.RegisterAsync(ContentHash);
                Message = "Zaregistrováno jako nová verze.";
            }, m => Message = m).ConfigureAwait(true);
        }
        finally { IsBusy = false; }
        await _app.RefreshGamesAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private void UninstallPrompt() => IsConfirmingUninstall = true;

    [RelayCommand]
    private void CancelUninstall() => IsConfirmingUninstall = false;

    /// <summary>Stops offering the game, deletes its files and forgets it was installed. Confirmed first, files are gone for good.</summary>
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ConfirmUninstallAsync()
    {
        IsBusy = true;
        Message = null;
        try
        {
            if (await TryAsync(() => _app.Client.UninstallAsync(ContentHash), m => Message = m).ConfigureAwait(true))
                IsConfirmingUninstall = false;
        }
        finally { IsBusy = false; }
        await _app.RefreshGamesAsync().ConfigureAwait(true);
    }

    private async Task Run(Func<Task> action, string doneMessage)
    {
        IsBusy = true;
        Message = null;
        try
        {
            if (await TryAsync(action, m => Message = m).ConfigureAwait(true)) Message = doneMessage;
        }
        finally { IsBusy = false; }
        await _app.RefreshDownloadsAsync().ConfigureAwait(true);
    }

    partial void OnIsBusyChanged(bool value)
    {
        InstallCommand.NotifyCanExecuteChanged();
        ConfirmInstallCommand.NotifyCanExecuteChanged();
        UpdateCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        CheckCommand.NotifyCanExecuteChanged();
        MarkVolatileCommand.NotifyCanExecuteChanged();
        RegisterCommand.NotifyCanExecuteChanged();
        UninstallPromptCommand.NotifyCanExecuteChanged();
        ConfirmUninstallCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>A configured game folder as an item to pick from, with free space folded into the text shown.</summary>
public sealed record InstallRootOption(string Path, string Text);
