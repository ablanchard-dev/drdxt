using DrDxt.Detection.Network;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Network;

// Revue F-43 : les adaptateurs que Windows type « Ethernet » sans câble derrière.
public sealed class CartesVersInternetTests
{
    [Theory]
    [InlineData("TAP-Windows Adapter V9", true)]
    [InlineData("WireGuard Tunnel", true)]
    [InlineData("Remote NDIS based Internet Sharing Device", true)]
    [InlineData("Hyper-V Virtual Ethernet Adapter", true)]
    [InlineData("Realtek Gaming 2.5GbE Family Controller", false)]
    [InlineData("Intel(R) Wi-Fi 6E AX211 160MHz", false)]
    public void Un_adaptateur_virtuel_se_reconnait_a_son_nom(string description, bool virtuelle)
        => CartesVersInternet.Virtuelle(description).Should().Be(virtuelle);
}
