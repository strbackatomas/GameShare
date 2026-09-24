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

    /// <summary>The game's picture as the agent serves it (.ico or .png), turned into an image by the view. Null until loaded, or when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIconImage))]
    public partial byte[]? IconData { get; set; }

    public bool HasIconImage => IconData is not null;
    private bool _iconRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(IsDamaged), nameof(IsDownloading), nameof(IsAvailable), nameof(CanInstall), nameof(CanUpdate), nameof(ShowInstallButton), nameof(StateText), nameof(CanPlay), nameof(NeedsExecutable), nameof(ShowUninstallButton), nameof(HasOtherLaunchOptions))]
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
    [NotifyPropertyChangedFor(nameof(CanPlay), nameof(NeedsExecutable), nameof(HasOtherLaunchOptions))]
    public partial LaunchState Launch { get; set; }

    /// <summary>A program of the game is running on this PC. Its files are then not rewritten, so repairing and updating wait.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay), nameof(CanModify), nameof(HasOtherLaunchOptions))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand), nameof(RepairCommand), nameof(RegisterCommand), nameof(UninstallPromptCommand), nameof(ConfirmUninstallCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>The game's other programs (an editor, a server, a launcher with options), offered next to the play button.</summary>
    public ObservableCollection<LaunchOptionViewModel> OtherLaunchOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherLaunchOptions))]
    public partial int OtherLaunchOptionCount { get; set; }

    public bool HasOtherLaunchOptions => CanPlay && OtherLaunchOptionCount > 0;

    /// <summary>
    /// Everything but the main action, in the menu next to it, grouped: the game's other programs, preparing it again, looking after
    /// its files, removing it. A null <see cref="CardMenuEntry.Command"/> is a separator line.
    /// </summary>
    public ObservableCollection<CardMenuEntry> MenuEntries { get; } = [];

    [ObservableProperty] public partial bool HasMenu { get; set; }

    private void RebuildMenu()
    {
        var entries = new List<CardMenuEntry>();
        void Group(IEnumerable<CardMenuEntry> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            if (entries.Count > 0) entries.Add(CardMenuEntry.Separator);
            entries.AddRange(list);
        }
        var here = IsInstalled || IsDamaged;
        Group(CanPlay ? OtherLaunchOptions.Select(o => new CardMenuEntry(o.Text, o.PlayCommand)) : []);
        Group(here && HasSetup ? [new CardMenuEntry("Znovu připravit hru…", PrepareAgainCommand)] : []);
        Group(here ? [new CardMenuEntry("Zkontrolovat soubory", CheckCommand),
                      .. IsDamaged ? new[] { new CardMenuEntry("Opravit ze sítě", RepairCommand), new CardMenuEntry("Registrovat jako novou verzi", RegisterCommand) } : []]
                   : []);
        Group(ShowUninstallButton ? [new CardMenuEntry("Odinstalovat…", UninstallPromptCommand)] : []);

        if (entries.Select(e => (e.Header, e.Command)).SequenceEqual(MenuEntries.Select(e => (e.Header, e.Command)))) return;
        MenuEntries.Clear();
        foreach (var e in entries) MenuEntries.Add(e);
        HasMenu = MenuEntries.Count > 0;
    }

    // ---- preparing the PC before the game is first played ----

    /// <summary>The game's definition has setup steps (redistributables, registry, compatibility, profile).</summary>
    [ObservableProperty] public partial bool HasSetup { get; set; }

    /// <summary>The setup did not run on this PC for this version and folder yet. Play shows the preparation first.</summary>
    [ObservableProperty] public partial bool NeedsSetup { get; set; }

    /// <summary>The preparation is shown for the player to confirm.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunSetup))]
    public partial bool IsShowingSetup { get; set; }

    public ObservableCollection<SetupStepViewModel> SetupSteps { get; } = [];

    /// <summary>Why the preparation must not run, as the agent put it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunSetup), nameof(HasSetupBlocked))]
    public partial string? SetupBlocked { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSetupWarning))]
    public partial string? SetupWarning { get; set; }

    /// <summary>"Pokračovat" asks for administrator rights, so the player knows a Windows prompt comes.</summary>
    [ObservableProperty] public partial bool SetupNeedsAdmin { get; set; }

    /// <summary>The shared redistributables package the game needs, offered on the LAN, not installed here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingRedist))]
    public partial string? MissingRedistContentHash { get; set; }

    public bool HasSetupBlocked => SetupBlocked is not null;
    public bool HasSetupWarning => SetupWarning is not null;
    public bool HasMissingRedist => MissingRedistContentHash is not null;
    public bool CanRunSetup => IsShowingSetup && SetupBlocked is null && !IsBusy;

    private SetupPlanDto? _setupPlan;
    private int _entryAfterSetup;

    /// <summary>The play button starts the game with administrator rights, so the player will see a UAC prompt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayTip))]
    public partial bool PlayNeedsAdmin { get; set; }

    public string? PlayTip => PlayNeedsAdmin ? "Spustí se jako správce, Windows se zeptá na potvrzení." : null;

    /// <summary>The programs of the game to choose from, once the player asked to choose.</summary>
    public ObservableCollection<string> Executables { get; } = [];

    [ObservableProperty] public partial string? SelectedExecutable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay), nameof(HasOtherLaunchOptions))]
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
    [NotifyPropertyChangedFor(nameof(HasTrust), nameof(TrustText), nameof(TrustTip), nameof(IsTrustVerified), nameof(IsTrustUnknown), nameof(IsTrustRevoked))]
    public partial TrustVerdict Trust { get; set; }

    /// <summary>For a revoked version, the administrator's reason.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrustTip))]
    public partial string? TrustNote { get; set; }

    /// <summary>0 to 100, only meaningful while the game is being downloaded.</summary>
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; } = "";

    /// <summary>An operation on this game is running, so its buttons are off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct), nameof(CanModify), nameof(CanRunSetup))]
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

    /// <summary>What the shield means, in a sentence, for its tooltip.</summary>
    public string TrustTip => Trust switch
    {
        TrustVerdict.Verified => "Ověřeno správcem: přesně tahle verze je na jeho podepsaném seznamu. Soubory jsou takové, jaké schválil.",
        TrustVerdict.Unknown => "Neověřeno: tahle verze není na seznamu správce. Může být v pořádku, ale nikdo za ni neručí.",
        TrustVerdict.Revoked => "Zrušeno správcem: tuhle verzi stáhl" + (string.IsNullOrEmpty(TrustNote) ? "." : $" ({TrustNote}).") + " Neinstaluj ji.",
        _ => "",
    };

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
        ApplyLaunchOptions(g.LaunchOptions);
        HasSetup = g.Definition?.Setup is { IsEmpty: false } && g.Definition.Kind == GameKind.Game;
        NeedsSetup = g.NeedsSetup;
        if (g.HasIcon && !_iconRequested)
        {
            _iconRequested = true;
            _ = LoadIconAsync();
        }
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
        RebuildMenu();
    }

    /// <summary>When the player last started one of the game's programs, to tell a game that quit right away from one that was played.</summary>
    private DateTime? _launchedAt;

    /// <summary>A game that stops within this long after it was started most likely did not start properly.</summary>
    internal static readonly TimeSpan QuickExit = TimeSpan.FromSeconds(20);

    /// <summary>"Hra se spouští…" is done with once the game runs. A game that stops right after it was started is worth saying.</summary>
    partial void OnIsRunningChanged(bool value)
    {
        if (Message is { } m && m.StartsWith("Hra se spouští", StringComparison.Ordinal)) Message = null;
        if (!value && _launchedAt is { } at && DateTime.UtcNow - at < QuickExit)
            Message = "Hra se ukončila hned po spuštění. Zkus ji spustit znovu. Když to nepomůže, podívej se do jejího logu ve složce hry.";
        if (!value) _launchedAt = null;
    }

    private async Task ForgetLaunchMessageAsync(string message)
    {
        await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(true);
        if (Message == message) Message = null;
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

    private async Task LoadIconAsync()
    {
        try { IconData = await _app.Client.GetIconAsync(ContentHash).ConfigureAwait(true); }
        catch (Exception) { _iconRequested = false; } // tried again with the next refresh
    }

    private void ApplyLaunchOptions(IReadOnlyList<LaunchOptionDto> options)
    {
        PlayNeedsAdmin = options.FirstOrDefault(o => o.Index == 0)?.RunAsAdmin == true;
        var others = options.Where(o => o.Index != 0).ToList();
        if (others.SequenceEqual(OtherLaunchOptions.Select(o => o.Option))) return;
        OtherLaunchOptions.Clear();
        foreach (var o in others) OtherLaunchOptions.Add(new LaunchOptionViewModel(o, StartAsync));
        OtherLaunchOptionCount = OtherLaunchOptions.Count;
        RebuildMenu();
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
    private Task PlayAsync() => StartAsync(0);

    /// <summary>Starts one of the game's programs, 0 being the game itself. A game not prepared on this PC yet is prepared first.</summary>
    private async Task StartAsync(int entry)
    {
        if (IsBusy) return;
        if (NeedsSetup)
        {
            await OpenSetupAsync(entry).ConfigureAwait(true);
            return;
        }
        await LaunchAsync(entry).ConfigureAwait(true);
    }

    /// <summary>Asks the agent what preparing this PC does and shows it. Nothing runs until the player agrees.</summary>
    private async Task OpenSetupAsync(int entry)
    {
        IsBusy = true;
        Message = "Zjišťuji, co hra potřebuje…";
        _entryAfterSetup = entry;
        try
        {
            if (!await TryAsync(async () => _setupPlan = await _app.Client.GetSetupPlanAsync(ContentHash), m => Message = m).ConfigureAwait(true)) return;
            Message = null;
            var plan = _setupPlan!;
            SetupSteps.Clear();
            foreach (var step in plan.Steps) SetupSteps.Add(new SetupStepViewModel(step));
            SetupBlocked = plan.Blocked;
            SetupWarning = plan.Warning;
            SetupNeedsAdmin = plan.NeedsAdmin;
            MissingRedistContentHash = plan.MissingRedistContentHash;
            IsShowingSetup = true;
        }
        finally { IsBusy = false; }

        // Nothing left to do here (every redistributable is on the PC already, say): note it and play without asking.
        if (IsShowingSetup && _setupPlan is { Blocked: null } p && p.Steps.All(s => s.AlreadyDone))
            await FinishSetupAsync(p).ConfigureAwait(true);
    }

    /// <summary>The player agreed: one UAC prompt for the machine's steps, the player's own steps here, then the game starts.</summary>
    [RelayCommand(CanExecute = nameof(CanRunSetup))]
    private async Task RunSetupAsync()
    {
        if (_setupPlan is not { } plan) return;
        IsBusy = true;
        Message = plan.NeedsAdmin ? "Připravuji hru, potvrď dotaz Windows na oprávnění…" : "Připravuji hru…";
        IReadOnlyList<SetupStepResultDto> results;
        try { results = await _app.SetupRunner.RunAsync(plan).ConfigureAwait(true); }
        finally { IsBusy = false; }

        foreach (var result in results)
            SetupSteps.FirstOrDefault(s => s.Title == result.Title)?.Report(result);
        var failed = results.Where(r => !r.Ok).ToList();
        if (failed.Count > 0)
        {
            Message = failed.Count == 1 && failed[0].Title == "Oprávnění správce"
                ? failed[0].Message
                : $"Příprava se nepovedla: {string.Join("; ", failed.Select(f => $"{f.Title}: {f.Message}"))}";
            return;
        }
        await FinishSetupAsync(plan).ConfigureAwait(true);
    }

    private async Task FinishSetupAsync(SetupPlanDto plan)
    {
        if (!await TryAsync(() => _app.Client.SetupDoneAsync(ContentHash, plan.SetupHash), m => Message = m).ConfigureAwait(true)) return;
        NeedsSetup = false;
        IsShowingSetup = false;
        await LaunchAsync(_entryAfterSetup).ConfigureAwait(true);
    }

    /// <summary>Plays without preparing, for a player who knows the game runs without it. Asked again next time.</summary>
    [RelayCommand]
    private async Task SkipSetupAsync()
    {
        IsShowingSetup = false;
        await LaunchAsync(_entryAfterSetup).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelSetup()
    {
        IsShowingSetup = false;
        Message = null;
    }

    /// <summary>Installs the shared redistributables package the game needs. Play prepares the game once it is there.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task InstallRedistAsync()
    {
        if (MissingRedistContentHash is not { } hash) return;
        if (await TryAsync(() => _app.Client.InstallAsync(hash), m => Message = m).ConfigureAwait(true))
        {
            IsShowingSetup = false;
            Message = "Instaluje se balíček knihoven. Až bude hotový, klikni znovu na Hrát.";
        }
        await _app.RefreshDownloadsAsync().ConfigureAwait(true);
    }

    /// <summary>Runs the preparation again: for another player on this PC, or when the game still does not start.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task PrepareAgainAsync()
    {
        if (!await TryAsync(() => _app.Client.ResetSetupAsync(ContentHash), m => Message = m).ConfigureAwait(true)) return;
        NeedsSetup = true;
        await OpenSetupAsync(0).ConfigureAwait(true);
    }

    private async Task LaunchAsync(int entry)
    {
        IsBusy = true;
        Message = null;
        try
        {
            await TryAsync(async () =>
            {
                var info = await _app.Client.LaunchAsync(ContentHash, entry);
                _app.Starter.Start(info);
                Message = info.RunAsAdmin ? "Hra se spouští jako správce, potvrď dotaz Windows…" : "Hra se spouští…";
                _launchedAt = DateTime.UtcNow;
                _ = ForgetLaunchMessageAsync(Message);
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

    partial void OnIsShowingSetupChanged(bool value) => RunSetupCommand.NotifyCanExecuteChanged();
    partial void OnSetupBlockedChanged(string? value) => RunSetupCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        RunSetupCommand.NotifyCanExecuteChanged();
        InstallRedistCommand.NotifyCanExecuteChanged();
        PrepareAgainCommand.NotifyCanExecuteChanged();
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

/// <summary>One line of the menu next to a game's main button, or a separator when there is no command.</summary>
public sealed record CardMenuEntry(string Header, System.Windows.Input.ICommand? Command)
{
    public static readonly CardMenuEntry Separator = new("-", null); // "-" is what Avalonia draws as a separator line
    public bool IsSeparator => Command is null;
}

/// <summary>One step of a game's preparation as the player sees it before agreeing, and how it went afterwards.</summary>
public sealed partial class SetupStepViewModel(SetupStepDto step) : ObservableObject
{
    public string Title => step.Title;
    public string Details => string.Join(Environment.NewLine, step.Details);
    public bool HasDetails => step.Details.Count > 0;
    public bool NeedsAdmin => step.NeedsAdmin && !step.AlreadyDone;
    public bool AlreadyDone => step.AlreadyDone;

    /// <summary>Empty until the step ran: then "hotovo" or what went wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutcome))]
    public partial string? Outcome { get; set; }

    [ObservableProperty] public partial bool Failed { get; set; }

    /// <summary>The player opened what the step writes: registry keys and values, the installer and its arguments.</summary>
    [ObservableProperty] public partial bool ShowDetails { get; set; }

    public bool HasOutcome => Outcome is not null;

    public void Report(SetupStepResultDto result)
    {
        Failed = !result.Ok;
        Outcome = result.Ok ? result.Message ?? "Hotovo." : $"Nepovedlo se: {result.Message}";
    }
}

/// <summary>One of a game's other programs, as an item in the menu next to the play button.</summary>
public sealed partial class LaunchOptionViewModel(LaunchOptionDto option, Func<int, Task> start)
{
    public LaunchOptionDto Option { get; } = option;

    public string Text => (string.IsNullOrWhiteSpace(Option.Name) ? Option.Executable : Option.Name) + (Option.RunAsAdmin ? "  (jako správce)" : "");

    [RelayCommand]
    private Task PlayAsync() => start(Option.Index);
}

/// <summary>A configured game folder as an item to pick from, with free space folded into the text shown.</summary>
public sealed record InstallRootOption(string Path, string Text);
