using System.Net;

namespace GameShare.Discovery.Tests;

/// <summary>The peer API and every outgoing call rely on this to keep traffic on the local network.</summary>
public class LanAddressTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.30.104")]
    [InlineData("169.254.10.20")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("::ffff:192.168.1.5")] // IPv4 address as seen by a dual-stack socket
    public void Private_addresses_are_local(string address) => Assert.True(LanAddress.IsPrivate(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]  // just below the private 172.16/12 block
    [InlineData("172.32.0.1")]      // just above it
    [InlineData("192.169.0.1")]
    [InlineData("100.64.0.1")]      // carrier-grade NAT and many VPNs, not a home or office LAN
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_are_not_local(string address) => Assert.False(LanAddress.IsPrivate(IPAddress.Parse(address)));
}
