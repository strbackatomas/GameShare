using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GameShare.Discovery;
using GameShare.Protocol;

namespace GameShare.Agent;

/// <summary>
/// "Is my network set up right?" in one answer: which networks this PC is on and whether Windows treats them as private or public,
/// whether the firewall lets each of GameShare's ports in on them, whether the agent really listens there, and whether the other PCs
/// answer. Read only: nothing is changed, and reading the firewall and the network list needs no administrator.
/// </summary>
public sealed class NetworkCheck(AgentOptions options, DiscoveryService discovery, RemoteAccessService remote, ILogger<NetworkCheck> log)
{
    private const int ProfileDomain = 1, ProfilePrivate = 2, ProfilePublic = 4;
    private const int ProtocolTcp = 6, ProtocolUdp = 17, ProtocolAny = 256;

    public async Task<NetworkCheckDto> RunAsync(CancellationToken ct = default)
    {
        string? problem = null;
        IReadOnlyList<NetworkProfileDto> networks = [];
        List<Rule>? rules = null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The firewall can only be read on Windows.");
            networks = ReadNetworks();
            rules = ReadFirewallRules();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Network check could not read the firewall or the network list: {Message}", ex.Message);
            problem = ex.Message;
        }

        var ports = Ports().Select(p => Check(p, rules)).ToList();
        var peers = await Task.WhenAll(discovery.Peers.Select(p => ReachAsync(p, ct))).ConfigureAwait(false);
        return new NetworkCheckDto(networks, ports, [.. peers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)], problem);
    }

    // ---- ports ----

    private sealed record Port(string Role, string Protocol, int Number, bool NeedsFirewall, bool Expected);

    private IEnumerable<Port> Ports()
    {
        yield return new(PortRoles.Control, "TCP", options.LocalApiPort, NeedsFirewall: false, Expected: true);
        yield return new(PortRoles.Peer, "TCP", options.PeerApiPort, NeedsFirewall: true, Expected: true);
        yield return new(PortRoles.Discovery, "UDP", options.DiscoveryPort, NeedsFirewall: true, Expected: true);
        yield return new(PortRoles.Transfer, "TCP", options.TorrentPort, NeedsFirewall: true, Expected: true);
        yield return new(PortRoles.Transfer, "UDP", options.TorrentPort, NeedsFirewall: true, Expected: true);
        yield return new(PortRoles.Remote, "TCP", options.RemoteApiPort, NeedsFirewall: true, Expected: remote.ListeningPort is not null);
    }

    private static PortCheckDto Check(Port p, List<Rule>? rules)
    {
        var listening = p.Protocol == "TCP"
            ? IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == p.Number)
            : IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(e => e.Port == p.Number);

        IReadOnlyList<string>? allowed = null;
        if (rules is not null && p.NeedsFirewall)
        {
            int protocol = p.Protocol == "TCP" ? ProtocolTcp : ProtocolUdp;
            var matching = rules.Where(r => (r.Protocol == protocol || r.Protocol == ProtocolAny) && r.CoversPort(p.Number)).ToList();
            int allow = matching.Where(r => r.Allow).Aggregate(0, (m, r) => m | r.Profiles);
            int block = matching.Where(r => !r.Allow).Aggregate(0, (m, r) => m | r.Profiles); // a block rule wins over an allow rule
            allowed = Profiles(allow & ~block);
        }
        return new PortCheckDto(p.Role, p.Protocol, p.Number, listening, p.Expected, p.NeedsFirewall, allowed);
    }

    // ---- firewall ----

    private sealed record Rule(bool Allow, int Protocol, string LocalPorts, int Profiles)
    {
        public bool CoversPort(int port)
        {
            foreach (var part in LocalPorts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == "*") return true;
                var range = part.Split('-');
                if (range.Length == 2 && int.TryParse(range[0], out var from) && int.TryParse(range[1], out var to) && port >= from && port <= to) return true;
                if (int.TryParse(part, out var single) && single == port) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The enabled inbound rules that could apply to this program: no program named, or this one. Through the firewall's own COM
    /// interface, which anyone may read, with names in English whatever the language of Windows.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<Rule> ReadFirewallRules()
    {
        var self = Environment.ProcessPath ?? "";
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
        var result = new List<Rule>();
        foreach (dynamic r in policy.Rules)
        {
            if (!(bool)r.Enabled || (int)r.Direction != 1) continue; // 1 is inbound
            string? app = r.ApplicationName;
            if (!string.IsNullOrEmpty(app) && !string.Equals(Path.GetFullPath(app), self, StringComparison.OrdinalIgnoreCase)) continue;
            string? service = r.ServiceName;
            if (!string.IsNullOrEmpty(service) && service != "*" && !string.Equals(service, AgentHost.ServiceName, StringComparison.OrdinalIgnoreCase)) continue;
            int protocol = r.Protocol;
            if (protocol is not (ProtocolTcp or ProtocolUdp or ProtocolAny)) continue;
            string ports = protocol == ProtocolAny ? "*" : (string?)r.LocalPorts ?? "*";
            result.Add(new Rule((int)r.Action == 1, protocol, ports, (int)r.Profiles));
        }
        return result;
    }

    // ---- networks ----

    /// <summary>The networks this PC is connected to, their category, their adapters, and whether the firewall is on for them.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<NetworkProfileDto> ReadNetworks()
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
        dynamic manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), throwOnError: true)!)!;
        var adapters = NetworkInterface.GetAllNetworkInterfaces().ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        var result = new List<NetworkProfileDto>();
        foreach (dynamic network in manager.GetNetworks(1)) // 1: connected ones
        {
            int category = network.GetCategory(); // 0 public, 1 private, 2 domain
            int profile = category switch { 1 => ProfilePrivate, 2 => ProfileDomain, _ => ProfilePublic };
            var names = new List<string>();
            bool allVirtual = true;
            foreach (dynamic connection in network.GetNetworkConnections())
            {
                string id = ((INetworkConnection)connection).GetAdapterId().ToString("B"); // a GUID does not pass through late binding
                if (!adapters.TryGetValue(id, out var ni)) continue;
                var ipv4 = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                names.Add(ipv4 is null ? ni.Name : $"{ni.Name} ({ipv4})");
                allVirtual &= UdpDatagramTransport.IsLikelyVirtual(ni);
            }
            result.Add(new NetworkProfileDto(
                network.GetName(), Profiles(profile)[0], names, (bool)policy.FirewallEnabled[profile], (bool)policy.BlockAllInboundTraffic[profile],
                IsVirtual: names.Count > 0 && allVirtual));
        }
        return result;
    }

    /// <summary>The start of Windows' INetworkConnection, up to the adapter id. Only that is needed, the rest goes through late binding.</summary>
    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.IDispatch)]
        object GetNetwork();
        bool IsConnectedToInternet { [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    private static IReadOnlyList<string> Profiles(int mask)
    {
        var list = new List<string>();
        if ((mask & ProfilePrivate) != 0) list.Add("Private");
        if ((mask & ProfileDomain) != 0) list.Add("Domain");
        if ((mask & ProfilePublic) != 0) list.Add("Public");
        return list;
    }

    // ---- the other PCs ----

    /// <summary>Whether this PC gets through to another's peer API. That also tells whether that PC's firewall lets it in.</summary>
    private static async Task<PeerReachDto> ReachAsync(PeerInfo peer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(peer.Address, peer.AgentPort), timeout.Token).ConfigureAwait(false);
            return new PeerReachDto(peer.MachineName, $"{peer.Address}:{peer.AgentPort}", true, watch.Elapsed.TotalMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PeerReachDto(peer.MachineName, $"{peer.Address}:{peer.AgentPort}", false, null, "Timeout");
        }
        catch (SocketException ex)
        {
            return new PeerReachDto(peer.MachineName, $"{peer.Address}:{peer.AgentPort}", false, null, ex.SocketErrorCode.ToString());
        }
    }
}
