using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameShare.Client.Services;
using GameShare.Protocol;

namespace GameShare.Client.ViewModels;

/// <summary>Good, worth a look, wrong, or not applicable. Drives the colour of a row.</summary>
public enum CheckLevel { Ok, Warn, Bad, Off }

/// <summary>One line of the check: what, its state in words, and how good that is.</summary>
public sealed class CheckRow(string title, string detail, CheckLevel level)
{
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public CheckLevel Level { get; } = level;
    public bool IsOk => Level == CheckLevel.Ok;
    public bool IsWarn => Level == CheckLevel.Warn;
    public bool IsBad => Level == CheckLevel.Bad;
    public bool IsOff => Level == CheckLevel.Off;
}

/// <summary>
/// The network check on the Síť page: which networks the PC is on and whether they are private, whether the firewall lets each of
/// GameShare's ports in, and whether the other PCs answer. The agent gathers the facts; what they mean and what to do is said here.
/// </summary>
public sealed partial class NetworkCheckViewModel(AppModel app) : ViewModelBase
{
    public ObservableCollection<CheckRow> Networks { get; } = [];
    public ObservableCollection<CheckRow> Ports { get; } = [];
    public ObservableCollection<CheckRow> Peers { get; } = [];

    /// <summary>What to do about what is wrong, one sentence each. Empty when all is well.</summary>
    public ObservableCollection<string> Advice { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial bool IsChecking { get; set; }

    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial CheckLevel Overall { get; set; }
    [ObservableProperty] public partial bool HasAdvice { get; set; }
    [ObservableProperty] public partial bool HasPeers { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "";

    /// <summary>Whether the rows and advice are shown. Folded when all is well, so the card stays small; open as soon as something is wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    public partial bool IsExpanded { get; set; }

    public string Chevron => IsExpanded ? "▾" : "▸";

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    public bool IsOverallOk => Overall == CheckLevel.Ok;
    public bool IsOverallWarn => Overall == CheckLevel.Warn;
    public bool IsOverallBad => Overall == CheckLevel.Bad;

    partial void OnOverallChanged(CheckLevel value)
    {
        OnPropertyChanged(nameof(IsOverallOk));
        OnPropertyChanged(nameof(IsOverallWarn));
        OnPropertyChanged(nameof(IsOverallBad));
    }

    private bool CanCheck => !IsChecking;

    /// <summary>Runs once when the page is first opened; after that only when asked.</summary>
    public Task CheckOnceAsync() => HasResult || IsChecking ? Task.CompletedTask : CheckAsync();

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        IsChecking = true;
        Message = "";
        try
        {
            NetworkCheckDto? result = null;
            if (await TryAsync(async () => result = await app.Client.CheckNetworkAsync(), m => Message = m).ConfigureAwait(true))
                Show(result!);
        }
        finally { IsChecking = false; }
    }

    internal void Show(NetworkCheckDto check)
    {
        Networks.Clear();
        Ports.Clear();
        Peers.Clear();
        Advice.Clear();

        // The networks GameShare really uses: a virtualiser's adapter does not count, GameShare leaves it alone.
        var used = check.Networks.Where(n => !n.IsVirtual).ToList();

        foreach (var n in check.Networks)
        {
            var adapters = n.Adapters.Count == 0 ? "" : " · " + string.Join(", ", n.Adapters);
            var level = n.IsVirtual ? CheckLevel.Off : n.Category == "Public" ? CheckLevel.Bad : CheckLevel.Ok;
            var firewall = !n.FirewallOn ? " · firewall vypnutý" : n.BlocksAllInbound ? " · firewall blokuje všechna příchozí spojení" : "";
            var note = n.IsVirtual ? " · virtuální, GameShare ji nepoužívá" : "";
            Networks.Add(new CheckRow(n.Name, $"{CategoryName(n.Category)}{adapters}{firewall}{note}", n.BlocksAllInbound && !n.IsVirtual ? CheckLevel.Bad : level));
        }
        foreach (var n in used.Where(n => n.Category == "Public"))
            Advice.Add($"Síť „{n.Name}“ je ve Windows nastavená jako veřejná a na té firewall GameShare nepustí. Přepni ji na soukromou: "
                + "Nastavení → Síť a internet → Ethernet (nebo Wi-Fi) → Typ síťového profilu → Soukromá síť.");
        foreach (var n in used.Where(n => n.BlocksAllInbound))
            Advice.Add($"Na síti „{n.Name}“ firewall blokuje všechna příchozí spojení. Vypni to v Zabezpečení Windows → Brána firewall a ochrana sítě.");
        if (check.FirewallProblem is { } problem)
            Advice.Add($"Firewall se nepodařilo přečíst, porty proto nejsou ověřené: {problem}");

        var closed = new List<string>();
        foreach (var p in check.Ports)
        {
            var (detail, level) = Judge(p, used);
            Ports.Add(new CheckRow($"{RoleName(p.Role)} · {p.Protocol} {p.Port}", detail, level));
            if (p.Expected && !p.Listening)
                Advice.Add($"Agent neposlouchá na {p.Protocol} {p.Port}. Port nejspíš drží jiný program; Protokol řekne víc.");
            else if (level is CheckLevel.Bad or CheckLevel.Warn && p.AllowedOn is { } allowed && !allowed.Contains("Private"))
                closed.Add($"{p.Protocol} {p.Port}");
        }
        if (closed.Count > 0)
            Advice.Add($"Firewall nepouští {string.Join(", ", closed)} na soukromé síti. Spusť znovu install-agent.ps1 jako správce, "
                + "ten pravidla doplní; nebo je povol ve firewallu ručně pro soukromou a doménovou síť.");

        foreach (var peer in check.Peers)
            Peers.Add(peer.Reachable
                ? new CheckRow(peer.Name, $"{peer.Address} · odpovídá ({peer.Milliseconds:0} ms)", CheckLevel.Ok)
                : new CheckRow(peer.Name, $"{peer.Address} · neodpovídá ({ReachError(peer.Error)})", CheckLevel.Bad));
        if (check.Peers.Any(p => !p.Reachable))
            Advice.Add("PC, které neodpovídá, má nejspíš veřejnou síť nebo chybějící pravidla ve firewallu. Spusť tuhle kontrolu i na něm.");

        HasPeers = Peers.Count > 0;
        HasAdvice = Advice.Count > 0;
        var all = Networks.Concat(Ports).Concat(Peers).ToList();
        Overall = all.Any(r => r.IsBad) || check.FirewallProblem is not null ? CheckLevel.Bad : all.Any(r => r.IsWarn) ? CheckLevel.Warn : CheckLevel.Ok;
        Summary = Overall switch
        {
            CheckLevel.Ok => "Všechno je v pořádku.",
            CheckLevel.Warn => "Funguje, ale něco stojí za pozornost.",
            _ => "Něco brání ostatním PC se sem dostat. Co s tím je napsané níž.",
        };
        IsExpanded = Overall != CheckLevel.Ok;
        HasResult = true;
    }

    /// <summary>Whether the firewall lets the port in on every network GameShare uses, on some of them, or on none.</summary>
    internal static (string Detail, CheckLevel Level) Judge(PortCheckDto p, IReadOnlyList<NetworkProfileDto> used)
    {
        if (!p.Expected && !p.Listening) return ("vypnuto", CheckLevel.Off);
        if (p.Expected && !p.Listening) return ("agent na něm neposlouchá", CheckLevel.Bad);
        if (!p.NeedsFirewall) return ("jen pro tento PC, firewall nepotřebuje", CheckLevel.Ok);
        if (p.AllowedOn is null) return ("poslouchá, firewall se nepodařilo ověřit", CheckLevel.Warn);

        bool Lets(NetworkProfileDto n) => !n.FirewallOn || (!n.BlocksAllInbound && p.AllowedOn.Contains(n.Category));
        var allowedText = p.AllowedOn.Count == 0 ? "nikde" : string.Join(", ", p.AllowedOn.Select(CategoryName));
        if (used.Count == 0)
            return p.AllowedOn.Count > 0 ? ($"firewall pouští: {allowedText}", CheckLevel.Ok) : ("firewall ho nepouští", CheckLevel.Bad);

        var blocked = used.Where(n => !Lets(n)).ToList();
        if (blocked.Count == 0) return ($"firewall pouští: {allowedText}", CheckLevel.Ok);
        var where = string.Join(", ", blocked.Select(n => $"„{n.Name}“ ({CategoryName(n.Category).ToLowerInvariant()})"));
        return blocked.Count == used.Count
            ? ($"firewall ho nepouští na síti {where}", CheckLevel.Bad)
            : ($"firewall ho nepouští na síti {where}, na ostatních ano", CheckLevel.Warn);
    }

    internal static string CategoryName(string category) => category switch
    {
        "Private" => "Soukromá",
        "Domain" => "Doménová",
        "Public" => "Veřejná",
        _ => category,
    };

    internal static string RoleName(string role) => role switch
    {
        PortRoles.Control => "Ovládání z tohoto PC",
        PortRoles.Peer => "Seznam her pro ostatní PC",
        PortRoles.Discovery => "Hledání ostatních PC",
        PortRoles.Transfer => "Přenos her",
        PortRoles.Remote => "Vzdálená správa",
        _ => role,
    };

    private static string ReachError(string? error) => error switch
    {
        "Timeout" => "nic se nevrátilo do 3 s, nejspíš firewall",
        "ConnectionRefused" => "spojení odmítnuto, agent tam možná neběží",
        "HostUnreachable" or "NetworkUnreachable" => "PC není v dosahu",
        null => "neznámá chyba",
        _ => error,
    };
}
