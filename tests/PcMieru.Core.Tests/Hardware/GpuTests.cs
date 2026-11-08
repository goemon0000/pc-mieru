using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Tests.Hardware;

public class GpuTests
{
    [Fact]
    public void LuidTextMatchesThePdhCounterSpelling()
    {
        var a = new DxgiAdapter("x", 0x10DE, 0x2F04, 0, 0, 0, 0, 0, 0x0001A2B3, 0, 0);
        Assert.Equal("luid_0x00000000_0x0001a2b3", a.LuidText);
        var negative = a with { LuidHigh = -1 };
        Assert.Equal("luid_0xffffffff_0x0001a2b3", negative.LuidText);
    }

    [Fact]
    public void SoftwareAdaptersAreRecognizedByFlag()
    {
        Assert.True(new DxgiAdapter("Microsoft Basic Render Driver", 0x1414, 0x8C, 0, 0, 0, 0, 0, 1, 0, DxgiAdapter.SoftwareFlag).IsSoftware);
        Assert.False(new DxgiAdapter("GPU", 0x10DE, 1, 0, 0, 0, 0, 0, 1, 0, 0).IsSoftware);
    }

    [Fact]
    public void AllOnesAddressMeansNotAPhysicalPciFunction()
    {
        Assert.True(new KmtAdapterInfo(1, 0, 0, null, null).HasPciAddress);
        Assert.False(new KmtAdapterInfo(uint.MaxValue, ushort.MaxValue, ushort.MaxValue, null, null).HasPciAddress);
    }

    [Theory]
    [InlineData(0x10DE, "NVIDIA")]
    [InlineData(0x1002, "AMD")]
    [InlineData(0x8086, "Intel")]
    [InlineData(0x1414, "Microsoft")]
    public void KnownVendorsGetNames(uint id, string name) => Assert.Equal(name, GpuCollector.VendorName(id));

    [Fact]
    public void UnknownVendorKeepsTheRawId() => Assert.Equal("PCI vendor 0x1234", GpuCollector.VendorName(0x1234));
}
