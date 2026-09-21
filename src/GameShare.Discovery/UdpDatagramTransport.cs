using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameShare.Discovery;

/// <summary>
/// Sends every datagram to the multicast group and to the directed broadcast address of each active IPv4 network
/// adapter, and listens on both. Two delivery paths because switches and Wi-Fi treat multicast and broadcast
/// differently, and the receiver treats duplicates as harmless heartbeats.
/// Stays on the local link: TTL 1, never a limited-scope route to the internet.
/// </summary>
public sealed class UdpDatagramTransport : IDatagramTransport
{
    public static readonly IPAddress DefaultGroup = IPAddress.Parse("239.255.77.77");

    private readonly int _port;
    private readonly IPAddress _group;
    private readonly ILogger _log;
    private readonly Socket _receiver;
    private readonly Socket _sender;
    private readonly HashSet<IPAddress> _joined = [];
    private readonly HashSet<string> _warned = [];

    public UdpDatagramTransport(int port, IPAddress? group = null, ILogger? logger = null)
    {
        _port = port;
        _group = group ?? DefaultGroup;
        _log = logger ?? NullLogger.Instance;

        _receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _receiver.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); // several agents or tests per machine
        DisableConnectionReset(_receiver);
        _receiver.Bind(new IPEndPoint(IPAddress.Any, port));

        _sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _sender.EnableBroadcast = true;
        _sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        _sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        DisableConnectionReset(_sender);

        JoinNewInterfaces(GetInterfaces());
    }

    private sealed record Nic(string Name, IPAddress Address, IPAddress Broadcast);

    /// <summary>Active IPv4 adapters. Read on every send, so a changed or newly plugged adapter is picked up.</summary>
    private static List<Nic> GetInterfaces()
    {
        var result = new List<Nic>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (!ni.SupportsMulticast) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bcast = new byte[4];
                for (int i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
                result.Add(new Nic(ni.Name, ua.Address, new IPAddress(bcast)));
            }
        }
        return result;
    }

    private void JoinNewInterfaces(List<Nic> nics)
    {
        foreach (var nic in nics)
        {
            if (_joined.Contains(nic.Address)) continue;
            try
            {
                _receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(_group, nic.Address));
                _joined.Add(nic.Address);
                _log.LogDebug("Joined discovery multicast group {Group} on {Nic} {Address}", _group, nic.Name, nic.Address);
            }
            catch (SocketException ex)
            {
                WarnOnce($"join:{nic.Address}", "Cannot join discovery multicast group on {Nic} {Address}: {Error}", nic.Name, nic.Address, ex.Message);
            }
        }
    }

    public async ValueTask SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var nics = GetInterfaces();
        JoinNewInterfaces(nics);
        if (nics.Count == 0) WarnOnce("no-nic", "No active IPv4 network adapter, discovery cannot send.");

        foreach (var nic in nics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, nic.Address.GetAddressBytes());
                await _sender.SendToAsync(payload, SocketFlags.None, new IPEndPoint(_group, _port), cancellationToken).ConfigureAwait(false);
                await _sender.SendToAsync(payload, SocketFlags.None, new IPEndPoint(nic.Broadcast, _port), cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                WarnOnce($"send:{nic.Address}", "Discovery send failed on {Nic} {Address}: {Error}", nic.Name, nic.Address, ex.Message);
            }
        }
    }

    public async IAsyncEnumerable<Datagram> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[DiscoveryMessage.MaxSize + 1]; // one spare byte so oversized datagrams are detectable
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await _receiver.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { yield break; }
            catch (ObjectDisposedException) { yield break; }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted) { yield break; }

            if (r.RemoteEndPoint is IPEndPoint remote)
                yield return new Datagram(buffer.AsSpan(0, r.ReceivedBytes).ToArray(), remote.Address);
        }
    }

    private void WarnOnce(string key, string template, params object[] args)
    {
        if (_warned.Add(key)) _log.LogWarning(template, args);
    }

    /// <summary>Windows reports an ICMP "port unreachable" as a receive error on UDP sockets. We never want that.</summary>
    private static void DisableConnectionReset(Socket s)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { s.IOControl((IOControlCode)(-1744830452), [0, 0, 0, 0], null); } // SIO_UDP_CONNRESET
        catch (SocketException) { /* best effort */ }
    }

    public void Dispose()
    {
        _receiver.Dispose();
        _sender.Dispose();
    }
}
