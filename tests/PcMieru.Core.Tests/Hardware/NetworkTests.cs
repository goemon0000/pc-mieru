using System.Net;
using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Tests.Hardware;

public class NetworkTests
{
    private const string Guid1 = "{3DC90928-AAAA-BBBB-CCCC-000000000001}";

    private static RawAdapter Raw(int ifType = 6, int oper = 1, ulong tx = 1_000_000_000, ulong rx = 1_000_000_000, byte[]? mac = null, params string[] ips) =>
        new(Guid1, "Ethernet", "Test Adapter", ifType, oper, 1500, mac ?? [0x02, 0x00, 0x5E, 0x10, 0x20, 0x30], 0x4, tx, rx,
            ips.Select(IPAddress.Parse).ToList(), [IPAddress.Parse("192.0.2.1")]);

    private static NetworkPnp Pci(string? driver = "1.2.3.4") => new(@"PCI\VEN_8086&DEV_10FB\1", "Test Card #2", driver);

    [Theory]
    [InlineData(6, @"PCI\VEN_8086&DEV_10FB\1", NetworkKind.Ethernet)]
    [InlineData(6, @"USB\VID_0BDA&PID_8153\1", NetworkKind.Ethernet)]
    [InlineData(117, @"PCI\VEN_8086&DEV_15F3\1", NetworkKind.Ethernet)]
    [InlineData(6, @"ROOT\NET\0002", NetworkKind.Virtual)]
    [InlineData(6, @"SWD\MSRRAS\MS_NDISWANIP", NetworkKind.Virtual)]
    [InlineData(6, @"ROOT\KDNIC\0000", NetworkKind.Virtual)]
    [InlineData(6, null, NetworkKind.Virtual)]
    [InlineData(6, @"BTH\MS_BTHPAN\a&377ebe2d&1&2", NetworkKind.Other)]
    [InlineData(71, @"PCI\VEN_14C3&DEV_0616\1", NetworkKind.Wifi)]
    [InlineData(71, null, NetworkKind.Wifi)]
    [InlineData(53, null, NetworkKind.Virtual)]
    [InlineData(24, null, NetworkKind.Loopback)]
    [InlineData(144, @"PCI\VEN_1\1", NetworkKind.Other)]
    public void KindComesFromTypeAndDeviceBus(int ifType, string? instanceId, NetworkKind expected)
    {
        var kind = NetworkBuilder.Classify(ifType, instanceId);
        Assert.Equal(ValueStatus.Ok, kind.Status);
        Assert.Equal(expected, kind.Data);
    }

    [Fact]
    public void WiredAdapterReportsNegotiatedSpeedAndAddresses()
    {
        var a = NetworkBuilder.Build(Raw(tx: 10_000_000_000, rx: 10_000_000_000, ips: ["192.0.2.10", "fe80::1"]), Pci(), null, 0);
        Assert.Equal(NetworkKind.Ethernet, a.Kind.Data);
        Assert.Equal("Test Card #2", a.Name.Data);
        Assert.Equal(10_000_000_000L, a.LinkSpeedBitsPerSec.Data);
        Assert.Contains("negotiated", a.LinkSpeedBitsPerSec.Detail);
        Assert.Equal("02-00-5E-10-20-30", a.MacAddress.Data);
        Assert.Equal(["192.0.2.10", "fe80::1"], a.IpAddresses.Data);
        Assert.Equal(["192.0.2.1"], a.DnsServers.Data);
        Assert.Equal("1.2.3.4", a.DriverVersion.Data);
        Assert.Equal("Ethernet", a.Details!.Description.Data);
        Assert.True(a.Details.IsUp.Data);
        Assert.True(a.Details.Dhcp.Data);
        Assert.Equal(1500, a.Details.Mtu.Data);
    }

    [Fact]
    public void WiredAdapterIsNotAWifiAdapter()
    {
        var a = NetworkBuilder.Build(Raw(ips: "192.0.2.10"), Pci(), null, 0);
        Assert.Equal(ValueStatus.Unsupported, a.Ssid.Status);
        Assert.Equal(ValueStatus.Unsupported, a.WifiBand.Status);
        Assert.Equal(ValueStatus.Unsupported, a.SignalPercent.Status);
        Assert.Equal(ValueStatus.Unsupported, a.Details!.WifiStandard.Status);
    }

    [Fact]
    public void LinkDownGivesNoSpeedAndNoAddressesInsteadOfZero()
    {
        var a = NetworkBuilder.Build(Raw(oper: 2, tx: 0, rx: 0, ips: "169.254.1.2"), Pci(), null, 0);
        Assert.Equal(ValueStatus.Unsupported, a.LinkSpeedBitsPerSec.Status);
        Assert.Contains("down", a.LinkSpeedBitsPerSec.Detail);
        Assert.Equal(ValueStatus.Unsupported, a.IpAddresses.Status);
        Assert.Equal(ValueStatus.Unsupported, a.DnsServers.Status);
        Assert.False(a.Details!.IsUp.Data);
        Assert.Equal(ValueStatus.Ok, a.MacAddress.Status);
    }

    [Fact]
    public void UnknownSpeedIsNotShownAsANumber()
    {
        var a = NetworkBuilder.Build(Raw(tx: ulong.MaxValue, rx: ulong.MaxValue, ips: "192.0.2.10"), Pci(), null, 0);
        Assert.Equal(ValueStatus.Unsupported, a.LinkSpeedBitsPerSec.Status);
    }

    [Fact]
    public void MissingHardwareAddressIsNotAFakeZeroMac()
    {
        var none = NetworkBuilder.Build(Raw(mac: [], ips: "192.0.2.10"), Pci(), null, 0);
        Assert.Equal(ValueStatus.Unsupported, none.MacAddress.Status);
        var zeros = NetworkBuilder.Build(Raw(mac: new byte[6], ips: "192.0.2.10"), Pci(), null, 0);
        Assert.Equal(ValueStatus.Unsupported, zeros.MacAddress.Status);
    }

    [Fact]
    public void PnpNameIsPreferredAndAdapterNameIsTheFallback()
    {
        var withPnp = NetworkBuilder.Build(Raw(ips: "192.0.2.10"), Pci(), null, 0);
        Assert.Equal("Test Card #2", withPnp.Name.Data);
        var withoutPnp = NetworkBuilder.Build(Raw(ips: "192.0.2.10"), null, null, 0, @"PCI\VEN_8086&DEV_10FB\1");
        Assert.Equal("Test Adapter", withoutPnp.Name.Data);
        Assert.Equal(NetworkKind.Ethernet, withoutPnp.Kind.Data);
        Assert.Equal(ValueStatus.Unsupported, withoutPnp.DriverVersion.Status);
    }

    private static Dictionary<string, WlanConnection> WlanMap(WlanConnection c) =>
        new() { [NetworkBuilder.NormalizeGuid(Guid1)] = c };

    [Fact]
    public void ConnectedWifiShowsNameBandSignalAndRates()
    {
        var c = new WlanConnection(Wlan.StateConnected, "HomeNet", 10, 87, 36, null, 1_200_000, 960_000);
        var a = NetworkBuilder.Build(Raw(ifType: 71, tx: 1, rx: 1, ips: "192.0.2.20"), Pci(), WlanMap(c), 0);
        Assert.Equal(NetworkKind.Wifi, a.Kind.Data);
        Assert.Equal("HomeNet", a.Ssid.Data);
        Assert.Equal("5 GHz", a.WifiBand.Data);
        Assert.Equal(87, a.SignalPercent.Data);
        Assert.Contains("not dBm", a.SignalPercent.Detail);
        Assert.Equal("Wi-Fi 6 (802.11ax)", a.Details!.WifiStandard.Data);
        Assert.Equal(960_000_000L, a.Details.TransmitRateBitsPerSec.Data);
        Assert.Equal(1_200_000_000L, a.Details.ReceiveRateBitsPerSec.Data);
        Assert.Equal(960_000_000L, a.LinkSpeedBitsPerSec.Data);
    }

    [Fact]
    public void DisconnectedWifiIsUnsupportedNotEmpty()
    {
        var c = new WlanConnection(4, null, null, null, null, null);
        var a = NetworkBuilder.Build(Raw(ifType: 71, oper: 2, ips: "169.254.1.2"), Pci(), WlanMap(c), 0);
        Assert.Equal(ValueStatus.Unsupported, a.Ssid.Status);
        Assert.Contains("not connected", a.Ssid.Detail);
        Assert.Equal(ValueStatus.Unsupported, a.WifiBand.Status);
        Assert.Equal(ValueStatus.Unsupported, a.SignalPercent.Status);
        Assert.Equal(ValueStatus.Unsupported, a.LinkSpeedBitsPerSec.Status);
    }

    [Fact]
    public void WifiDetailsHiddenByLocationPermissionAreReportedAsDenied()
    {
        var c = new WlanConnection(Wlan.StateConnected, null, null, null, null, Probe.ErrorAccessDenied);
        var a = NetworkBuilder.Build(Raw(ifType: 71, ips: "192.0.2.20"), Pci(), WlanMap(c), 0);
        Assert.Equal(ValueStatus.Denied, a.Ssid.Status);
        Assert.Contains("location", a.Ssid.Detail);
        Assert.Equal(ValueStatus.Denied, a.WifiBand.Status);
        Assert.Equal(ValueStatus.Denied, a.SignalPercent.Status);
        Assert.Equal(ValueStatus.Denied, a.Details!.WifiStandard.Status);
    }

    [Fact]
    public void WlanServiceNotRunningIsUnsupported()
    {
        var a = NetworkBuilder.Build(Raw(ifType: 71, ips: "192.0.2.20"), Pci(), null, 1062);
        Assert.Equal(ValueStatus.Unsupported, a.Ssid.Status);
        Assert.Contains("not running", a.Ssid.Detail);
    }

    [Theory]
    [InlineData(4, 36, "5 GHz")]
    [InlineData(5, 6, "2.4 GHz")]
    [InlineData(6, 11, "2.4 GHz")]
    [InlineData(8, 100, "5 GHz")]
    [InlineData(7, 6, "2.4 GHz")]
    [InlineData(7, 44, "5 GHz")]
    [InlineData(10, 36, "5 GHz")]
    [InlineData(10, 100, "5 GHz")]
    [InlineData(10, 11, "2.4 GHz")]
    [InlineData(10, 2, "2.4 GHz")]
    [InlineData(10, 189, "6 GHz")]
    [InlineData(11, 233, "6 GHz")]
    public void BandIsNamedWhenTheChannelLeavesNoDoubt(int phy, int channel, string expected) =>
        Assert.Equal(expected, NetworkBuilder.WifiBandOf(phy, channel));

    [Theory]
    [InlineData(10, 1)]
    [InlineData(10, 5)]
    [InlineData(10, 149)]
    [InlineData(11, 165)]
    [InlineData(10, 37)]
    [InlineData(10, null)]
    [InlineData(10, 0)]
    public void AmbiguousChannelNumbersGiveNoBandInsteadOfAGuess(int? phy, int? channel) =>
        Assert.Null(NetworkBuilder.WifiBandOf(phy, channel));

    [Theory]
    [InlineData(7, "Wi-Fi 4 (802.11n)")]
    [InlineData(8, "Wi-Fi 5 (802.11ac)")]
    [InlineData(10, "Wi-Fi 6 (802.11ax)")]
    [InlineData(11, "Wi-Fi 7 (802.11be)")]
    [InlineData(4, "802.11a")]
    public void WifiStandardUsesTheNamesPeopleKnow(int phy, string expected) =>
        Assert.Equal(expected, NetworkBuilder.WifiStandardName(phy));

    [Fact]
    public void UnknownPhyTypeHasNoName() => Assert.Null(NetworkBuilder.WifiStandardName(0));

    [Fact]
    public void ListOrdersRealCardsBeforeVirtualOnes()
    {
        Assert.True(NetworkBuilder.SortRank(NetworkKind.Ethernet) < NetworkBuilder.SortRank(NetworkKind.Wifi));
        Assert.True(NetworkBuilder.SortRank(NetworkKind.Wifi) < NetworkBuilder.SortRank(NetworkKind.Other));
        Assert.True(NetworkBuilder.SortRank(NetworkKind.Other) < NetworkBuilder.SortRank(NetworkKind.Virtual));
    }

    [Fact]
    public void GuidsAreComparedInOneSpelling()
    {
        Assert.Equal(NetworkBuilder.NormalizeGuid("{3DC90928-AAAA-BBBB-CCCC-000000000001}"), NetworkBuilder.NormalizeGuid("3dc90928-aaaa-bbbb-cccc-000000000001"));
        Assert.Equal(string.Empty, NetworkBuilder.NormalizeGuid("not a guid"));
        Assert.Equal(string.Empty, NetworkBuilder.NormalizeGuid(null));
    }

    [Trait("Category", "RealMachine")]
    [Fact]
    public void RealCollectionRunsOnThisMachineWithoutThrowing()
    {
        var result = NetworkCollector.Collect();
        Assert.True(result.Status is ValueStatus.Ok or ValueStatus.Unsupported or ValueStatus.Error, result.Detail);
        if (result.Status == ValueStatus.Ok)
        {
            Assert.All(result.Data!, a =>
            {
                Assert.NotEqual(NetworkKind.Loopback, a.Kind.Data);
                Assert.True(a.Name.Status is ValueStatus.Ok or ValueStatus.Unsupported);
            });
        }
    }
}
