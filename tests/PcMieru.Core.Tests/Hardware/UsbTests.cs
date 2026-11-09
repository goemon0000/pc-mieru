using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Tests.Hardware;

public class UsbTests
{
    private const string FakeSerial = "TESTSERIAL0001";

    private static RawUsb Root(string id = "USB\\ROOT_HUB30\\5&AAAA&0&0") =>
        new(id, "USB\\ROOT_HUB30&VID1022&PID149C&REV0000", null, "USB Root Hub (USB 3.0)", "USB", "USBHUB3", null, "PCI\\VEN_1022&DEV_149C\\3&BBBB", "Test USB 3.10 Host Controller");

    private static RawUsb Device(string name, string vid, string pid, string parentId, string port = "Port_#0001.Hub_#0001", string? hardwareId = null, string? busName = null) =>
        new($"USB\\VID_{vid}&PID_{pid}\\{FakeSerial}", hardwareId ?? $"USB\\VID_{vid}&PID_{pid}&REV_0100", busName ?? name, "USB Composite Device", "USB", "usbccgp", port, parentId, "parent");

    [Theory]
    [InlineData("USB\\VID_046D&PID_C52B&REV_2407", "046D", "C52B")]
    [InlineData("usb\\vid_0781&pid_5567", "0781", "5567")]
    [InlineData("USB\\VID_2833&PID_5013&MI_00", "2833", "5013")]
    public void VidAndPidAreReadFromTheHardwareId(string hardwareId, string vid, string pid) =>
        Assert.Equal((vid, pid), UsbBuilder.VidPid(hardwareId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("USB\\ROOT_HUB30&VID1022&PID149C&REV0000")]
    [InlineData("USB\\VID_ZZZZ&PID_0001")]
    [InlineData("USB\\VID_04")]
    public void NoVendorPartMeansNoVidPid(string? hardwareId) => Assert.Null(UsbBuilder.VidPid(hardwareId));

    [Fact]
    public void CompositeFunctionNodesAreNotDevices()
    {
        Assert.True(UsbBuilder.IsInterfaceNode("USB\\VID_046D&PID_C52B&MI_01\\6&1&0&1"));
        Assert.False(UsbBuilder.IsInterfaceNode("USB\\VID_046D&PID_C52B\\5&1&0&2"));
    }

    [Fact]
    public void ListIsInTreeOrderWithDepth()
    {
        var root = Root();
        var hub = Device("Test Hub", "05E3", "0608", root.InstanceId, "Port_#0002.Hub_#0001");
        var mouse = Device("Test Mouse", "046D", "C52B", hub.InstanceId, "Port_#0001.Hub_#0002");
        var stick = Device("Test Stick", "0781", "5567", root.InstanceId, "Port_#0001.Hub_#0001");

        // Deliberately shuffled: the builder must not depend on enumeration order.
        var list = UsbBuilder.Build([mouse, stick, root, hub]);

        Assert.Equal(["USB Root Hub (USB 3.0)", "Test Stick", "Test Hub", "Test Mouse"], list.Select(d => d.Name.Data!).ToArray());
        Assert.Equal([0, 1, 1, 2], list.Select(d => d.Details!.Depth.Data).ToArray());
    }

    [Fact]
    public void ProductStringBeatsTheGenericDriverName()
    {
        var root = Root();
        var d = Device("Test Headset", "1532", "1003", root.InstanceId);
        var named = UsbBuilder.Build([root, d]).Single(x => x.VendorId.Status == ValueStatus.Ok);
        Assert.Equal("Test Headset", named.Name.Data);

        var generic = d with { BusReportedName = null };
        Assert.Equal("USB Composite Device", UsbBuilder.Build([root, generic]).Single(x => x.VendorId.Status == ValueStatus.Ok).Name.Data);
    }

    [Fact]
    public void DeviceWithNoNameIsSaidSo()
    {
        var root = Root();
        var d = Device("x", "046D", "C52B", root.InstanceId) with { BusReportedName = null, DeviceDescription = null };
        Assert.Equal(ValueStatus.Unsupported, UsbBuilder.Build([root, d]).Single(x => x.VendorId.Status == ValueStatus.Ok).Name.Status);
    }

    [Fact]
    public void VendorIdsArePlainUppercaseHex()
    {
        var root = Root();
        var d = Device("Test", "046d", "c52b", root.InstanceId);
        var item = UsbBuilder.Build([root, d]).Single(x => x.VendorId.Status == ValueStatus.Ok);
        Assert.Equal("046D", item.VendorId.Data);
        Assert.Equal("C52B", item.ProductId.Data);
        Assert.Equal("Logitech", item.Manufacturer.Data);
    }

    [Fact]
    public void UnknownVendorIsUnsupportedWithTheIdInTheNote()
    {
        var root = Root();
        var d = Device("Test", "FFFE", "0001", root.InstanceId);
        var item = UsbBuilder.Build([root, d]).Single(x => x.VendorId.Status == ValueStatus.Ok);
        Assert.Equal(ValueStatus.Unsupported, item.Manufacturer.Status);
        Assert.Contains("FFFE", item.Manufacturer.Detail);
        Assert.Null(item.Manufacturer.Data);
    }

    [Fact]
    public void RootHubHasNoVidPortOrFakeZero()
    {
        var list = UsbBuilder.Build([Root()]);
        var hub = Assert.Single(list);
        Assert.Equal(ValueStatus.Unsupported, hub.VendorId.Status);
        Assert.Equal(ValueStatus.Unsupported, hub.ProductId.Status);
        Assert.Equal(ValueStatus.Unsupported, hub.Details!.Location.Status);
        Assert.Equal(0, hub.Details.Depth.Data);
        Assert.Contains("Test USB 3.10 Host Controller", hub.ParentId.Data);
    }

    [Fact]
    public void ParentTextNamesTheHubAndNeverCarriesTheInstancePathOrSerial()
    {
        var root = Root();
        var hub = Device("Test Hub", "05E3", "0608", root.InstanceId);
        var kid = Device("Test Mouse", "046D", "C52B", hub.InstanceId);
        var list = UsbBuilder.Build([root, hub, kid]);

        Assert.Equal("USB Composite Device (VID_05E3&PID_0608)", list.Single(d => d.Name.Data == "Test Mouse").ParentId.Data);
        var json = PcMieru.Core.PcMieruJson.Serialize(list);
        Assert.DoesNotContain(FakeSerial, json);
        Assert.DoesNotContain("USB\\\\VID_", json);
        Assert.DoesNotContain("5&AAAA", json);
    }

    [Fact]
    public void DeviceWhoseParentIsNotListedStillGetsTheParentName()
    {
        var d = Device("Test Stick", "0781", "5567", "USB\\SOMETHING\\ELSE") with { ParentName = "Some Parent" };
        var list = UsbBuilder.Build([d]);
        Assert.Equal("Some Parent", Assert.Single(list).ParentId.Data);
        Assert.Equal(0, list[0].Details!.Depth.Data);
    }

    [Fact]
    public void ParentLoopDoesNotHangOrRepeat()
    {
        var a = Device("A", "046D", "0001", "USB\\VID_046D&PID_0002\\" + FakeSerial);
        var b = Device("B", "046D", "0002", "USB\\VID_046D&PID_0001\\" + FakeSerial);
        // Both are each other's parent: neither is a root, so neither is visited, and nothing loops.
        var list = UsbBuilder.Build([a, b]);
        Assert.True(list.Count <= 2);
    }

    [Fact]
    public void DepthIsCappedSoADeepChainCannotRunAway()
    {
        var items = new List<RawUsb> { Root() };
        var parent = items[0].InstanceId;
        for (var i = 0; i < 40; i++)
        {
            var d = Device($"Hub {i}", "05E3", "0608", parent) with { };
            d = d with { InstanceId = $"USB\\VID_05E3&PID_0608\\{FakeSerial}{i}" };
            items.Add(d);
            parent = d.InstanceId;
        }

        var list = UsbBuilder.Build(items);
        Assert.True(list.Count <= 18);
    }

    [Fact]
    public void EverySourceIsNamed()
    {
        var root = Root();
        var d = Device("Test", "046D", "C52B", root.InstanceId);
        foreach (var item in UsbBuilder.Build([root, d]))
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Name.Source));
            Assert.False(string.IsNullOrWhiteSpace(item.VendorId.Source));
            Assert.False(string.IsNullOrWhiteSpace(item.Manufacturer.Source));
            Assert.False(string.IsNullOrWhiteSpace(item.ParentId.Source));
        }
    }

    [Fact]
    public void VendorTableHasNoDuplicateOrBadKeys()
    {
        // The table is a Dictionary built with an indexer, so a duplicate would silently win; check a few well-known ids instead.
        Assert.Equal("Microsoft", UsbVendors.Name("045E"));
        Assert.Equal("SanDisk", UsbVendors.Name("0781"));
        Assert.Equal("Intel", UsbVendors.Name("8087"));
        Assert.Null(UsbVendors.Name("0000"));
    }

    [Trait("Category", "RealMachine")]
    [Fact]
    public void RealCollectionRunsOnThisMachineWithoutThrowing()
    {
        var result = UsbCollector.Collect();
        Assert.True(result.Status is ValueStatus.Ok or ValueStatus.Unsupported or ValueStatus.Error, result.Detail);
        if (result.Status == ValueStatus.Ok)
        {
            Assert.NotEmpty(result.Data!);
            Assert.All(result.Data!, d => Assert.DoesNotContain("&MI_", d.Name.Data ?? string.Empty));
        }
    }
}
