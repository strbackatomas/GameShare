using System.Text.RegularExpressions;

namespace GameShare.Agent;

/// <summary>
/// What a paired PC may ask of this one: looking at games, downloads and the network, and starting, pausing, resuming and cancelling
/// downloads, checks, repairs, updates and a scan. Nothing else of the control API is reachable remotely: no settings, so no other
/// folder to write to; no starting games or setup steps, which run programs and are for the person at this PC; no deleting games;
/// and nothing of remote management itself, so a paired PC cannot pair others or reach further through this one.
/// </summary>
public static class RemoteWhitelist
{
    private const string Hash = "[0-9a-f]{64}";
    private const string MachineId = "[A-Za-z0-9-]{1,64}";

    private static readonly (string Method, Regex Path)[] Allowed =
    [
        ("GET", Path("status")),
        ("GET", Path("games")),
        ("GET", Path("games/scan")),
        ("GET", Path($"games/{Hash}")),
        ("GET", Path($"games/{Hash}/icon")),
        ("GET", Path("peers")),
        ("GET", Path($"peers/{MachineId}/games")),
        ("GET", Path("downloads")),
        ("GET", Path(@"downloads/\d{1,18}")),
        ("GET", Path("uploads")),
        ("GET", Path("settings/roots")), // where an install may go, with free space; the settings themselves stay local
        ("GET", Path("trust")),
        ("GET", Path("app-update")),
        ("GET", Path("network/check")), // whether that PC's firewall and network are set up right, read only

        ("POST", Path("games/scan")),
        ("POST", Path($"games/{Hash}/(install|check|repair|update)")),
        ("POST", Path(@"downloads/\d{1,18}/(pause|resume)")),
        ("POST", Path("app-update/(check|apply)")),

        ("DELETE", Path(@"downloads/\d{1,18}")), // cancels a download, never an installed game
    ];

    /// <param name="path">The part of the path after /api/, as Kestrel decoded it.</param>
    public static bool Allows(string method, string path) =>
        Allowed.Any(a => a.Method.Equals(method, StringComparison.OrdinalIgnoreCase) && a.Path.IsMatch(path));

    /// <summary>Whether this request changes something, so it is worth a notice and a line in the log.</summary>
    public static bool IsAction(string method) => !HttpMethods.IsGet(method);

    private static Regex Path(string pattern) => new($"^{pattern}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
}
