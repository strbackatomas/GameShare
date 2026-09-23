namespace GameShare.Discovery.Tests;

/// <summary>Which adapters discovery skips by default: virtualisers, whose address flaps against a real adapter's and stalls transfers.</summary>
public class UdpDatagramTransportTests
{
    [Theory]
    [InlineData("VirtualBox Host-Only Ethernet Adapter", "Ethernet 2")]
    [InlineData("Hyper-V Virtual Ethernet Adapter", "vEthernet (Default Switch)")]
    [InlineData("VMware Virtual Ethernet Adapter for VMnet8", "VMware Network Adapter VMnet8")]
    [InlineData("Windows Subsystem for Linux (WSL)", "vEthernet (WSL)")]
    [InlineData(null, "Npcap Loopback Adapter")]
    public void Virtualiser_adapters_are_flagged(string? description, string name) =>
        Assert.True(UdpDatagramTransport.IsLikelyVirtual(description, name));

    [Theory]
    [InlineData("Intel(R) Ethernet Connection", "Ethernet")]
    [InlineData("Realtek PCIe GbE Family Controller", "Ethernet")]
    [InlineData("Intel(R) Wi-Fi 6 AX201 160MHz", "Wi-Fi")]
    public void Real_adapters_are_not_flagged(string? description, string name) =>
        Assert.False(UdpDatagramTransport.IsLikelyVirtual(description, name));
}
