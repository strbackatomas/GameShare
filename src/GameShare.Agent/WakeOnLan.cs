using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GameShare.Discovery;

namespace GameShare.Agent;

/// <summary>Waking a managed PC that is off. A seam, so tests neither depend on this PC's adapters nor send broadcasts.</summary>
public interface IWakeOnLan
{
    /// <summary>The MAC addresses another PC would wake this one with: real wired and wireless adapters on a private network.</summary>
    IReadOnlyList<string> OwnMacAddresses();

    /// <summary>Sends the magic packet for each address to every network this PC is on.</summary>
    Task SendAsync(IReadOnlyList<string> macAddresses, CancellationToken ct = default);
}

/// <summary>
/// The standard magic packet: six 0xFF bytes and the MAC sixteen times, as a UDP broadcast on ports 9 and 7. Whether the PC wakes
/// is up to it: Wake-on-LAN has to be on in its BIOS and for its network adapter, and over Wi-Fi it rarely works.
/// </summary>
public sealed class WakeOnLan(ILogger<WakeOnLan> log) : IWakeOnLan
{
    private static readonly int[] Ports = [9, 7];

    public IReadOnlyList<string> OwnMacAddresses()
    {
        var result = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211))
                continue;
            if (UdpDatagramTransport.IsLikelyVirtual(ni)) continue;
            if (!ni.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork && LanAddress.IsPrivate(a.Address)))
                continue;
            var bytes = ni.GetPhysicalAddress().GetAddressBytes();
            if (bytes.Length == 6) result.Add(Format(bytes));
        }
        return result.Distinct().ToList();
    }

    public async Task SendAsync(IReadOnlyList<string> macAddresses, CancellationToken ct = default)
    {
        var packets = macAddresses.Select(Packet).ToList();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        foreach (var target in Broadcasts())
            foreach (var port in Ports)
                foreach (var packet in packets)
                {
                    try { await socket.SendToAsync(packet, SocketFlags.None, new IPEndPoint(target, port), ct).ConfigureAwait(false); }
                    catch (SocketException ex) { log.LogDebug("Wake-on-LAN to {Target}:{Port} failed: {Message}", target, port, ex.Message); }
                }
        log.LogInformation("Wake-on-LAN sent for {Macs}", string.Join(", ", macAddresses));
    }

    /// <summary>The broadcast address of each private network this PC is on, and the limited broadcast for good measure.</summary>
    private static IEnumerable<IPAddress> Broadcasts()
    {
        var result = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null || !LanAddress.IsPrivate(ua.Address)) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bcast = new byte[4];
                for (int i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
                result.Add(new IPAddress(bcast));
            }
        }
        return result;
    }

    /// <summary>"AA-BB-CC-DD-EE-FF".</summary>
    public static string Format(byte[] mac) => string.Join('-', mac.Select(b => b.ToString("X2")));

    /// <exception cref="ArgumentException">Not six bytes in hex, with or without '-' or ':' between them.</exception>
    public static byte[] Packet(string mac)
    {
        var hex = mac.Replace("-", "").Replace(":", "");
        if (hex.Length != 12) throw new ArgumentException($"'{mac}' is not a MAC address.");
        byte[] address;
        try { address = Convert.FromHexString(hex); }
        catch (FormatException) { throw new ArgumentException($"'{mac}' is not a MAC address."); }

        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 0; i < 16; i++) Buffer.BlockCopy(address, 0, packet, 6 + i * 6, 6);
        return packet;
    }
}
