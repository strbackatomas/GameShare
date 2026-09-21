using System.Net;
using System.Net.Sockets;

namespace GameShare.Discovery;

/// <summary>Decides whether an address is on a local network. Used to keep game data and control traffic off the internet.</summary>
public static class LanAddress
{
    /// <summary>Loopback, RFC 1918 private ranges and link-local. IPv4-mapped IPv6 addresses are unwrapped first.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        // IPv6 unique local (fc00::/7) and link-local (fe80::/10).
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
        }
        return false;
    }
}
