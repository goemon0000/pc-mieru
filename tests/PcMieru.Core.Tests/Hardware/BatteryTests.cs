using System.Buffers.Binary;
using System.Text;
using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Tests.Hardware;

public class BatteryTests
{
    private static RawPowerStatus Power(byte ac = 1, byte flag = 0, byte percent = 80, uint life = uint.MaxValue) => new(ac, flag, percent, life);

    private static RawBattery Laptop(uint design = 50_000, uint full = 45_000, uint cycles = 120, string chemistry = "LION", uint? state = 4, uint? voltage = 12_300, uint caps = BatteryNative.CapSystemBattery) =>
        new(caps, chemistry, design, full, cycles, state, voltage);

    private static BatteryHardware Ok(Value<BatteryHardware> v)
    {
        Assert.Equal(ValueStatus.Ok, v.Status);
        return v.Data!;
    }

    [Theory]
    [InlineData(128)]
    [InlineData(255)]
    public void DesktopWithNoBatteryIsOneUnsupportedValueNotZeros(byte flag)
    {
        var v = BatteryBuilder.Build(Power(flag: flag, percent: 255), []);
        Assert.Equal(ValueStatus.Unsupported, v.Status);
        Assert.Contains("no battery", v.Detail);
        Assert.Null(v.Data);
    }

    [Fact]
    public void UpsIsNotASystemBattery()
    {
        var ups = Laptop(caps: 0);
        Assert.Equal(ValueStatus.Unsupported, BatteryBuilder.Build(Power(flag: 128), [ups]).Status);
    }

    [Fact]
    public void LaptopBatteryReportsChargeCapacityWearAndCycles()
    {
        var b = Ok(BatteryBuilder.Build(Power(ac: 0, flag: 1, percent: 63, life: 5400), [Laptop(state: 2)]));
        Assert.Equal(63, b.ChargePercent.Data);
        Assert.False(b.OnAc.Data);
        Assert.Equal(50_000, b.DesignCapacityMilliwattHours.Data);
        Assert.Equal(45_000, b.FullChargeCapacityMilliwattHours.Data);
        Assert.Equal(10.0, b.WearPercent.Data);
        Assert.Equal(120, b.CycleCount.Data);
        Assert.Equal("Lithium-ion", b.Details!.Chemistry.Data);
        Assert.Equal("discharging", b.Details.Status.Data);
        Assert.Equal(12_300, b.Details.VoltageMillivolts.Data);
        Assert.Equal(90, b.Details.RemainingMinutes.Data);
    }

    [Fact]
    public void ChargingOnAcShowsNoRemainingTime()
    {
        var b = Ok(BatteryBuilder.Build(Power(ac: 1, flag: 8, percent: 40), [Laptop(state: 5)]));
        Assert.True(b.OnAc.Data);
        Assert.Equal("charging", b.Details!.Status.Data);
        Assert.Equal(ValueStatus.Unsupported, b.Details.RemainingMinutes.Status);
    }

    [Fact]
    public void WearIsNeverNegative()
    {
        var b = Ok(BatteryBuilder.Build(Power(), [Laptop(design: 40_000, full: 42_000)]));
        Assert.Equal(0.0, b.WearPercent.Data);
        Assert.Contains("at or above", b.WearPercent.Detail);
    }

    [Fact]
    public void WearIsRoundedToOneDecimal()
    {
        var b = Ok(BatteryBuilder.Build(Power(), [Laptop(design: 53_000, full: 47_123)]));
        Assert.Equal(11.1, b.WearPercent.Data);
    }

    [Fact]
    public void ZeroCycleCountMeansNotReportedNotBrandNew()
    {
        var b = Ok(BatteryBuilder.Build(Power(), [Laptop(cycles: 0)]));
        Assert.Equal(ValueStatus.Unsupported, b.CycleCount.Status);
    }

    [Fact]
    public void RelativeCapacityBatteryDoesNotInventMilliwattHours()
    {
        var b = Ok(BatteryBuilder.Build(Power(), [Laptop(caps: BatteryNative.CapSystemBattery | BatteryNative.CapRelative)]));
        Assert.Equal(ValueStatus.Unsupported, b.DesignCapacityMilliwattHours.Status);
        Assert.Equal(ValueStatus.Unsupported, b.FullChargeCapacityMilliwattHours.Status);
        Assert.Equal(ValueStatus.Unsupported, b.WearPercent.Status);
    }

    [Fact]
    public void UnknownCapacityAndVoltageAreUnsupported()
    {
        var b = Ok(BatteryBuilder.Build(Power(), [Laptop(design: BatteryNative.Unknown, full: 0, voltage: BatteryNative.Unknown)]));
        Assert.Equal(ValueStatus.Unsupported, b.DesignCapacityMilliwattHours.Status);
        Assert.Equal(ValueStatus.Unsupported, b.FullChargeCapacityMilliwattHours.Status);
        Assert.Equal(ValueStatus.Unsupported, b.Details!.VoltageMillivolts.Status);
    }

    [Fact]
    public void BatteryPresentButDriverGivesNoDetailsStillShowsChargeAndSaysWhy()
    {
        var b = Ok(BatteryBuilder.Build(Power(ac: 0, flag: 1, percent: 55), []));
        Assert.Equal(55, b.ChargePercent.Data);
        Assert.Equal(ValueStatus.Unsupported, b.DesignCapacityMilliwattHours.Status);
        Assert.Equal(ValueStatus.Unsupported, b.WearPercent.Status);
        Assert.Equal(ValueStatus.Unsupported, b.Details!.Chemistry.Status);
        Assert.Equal("on battery", b.Details.Status.Data);
    }

    [Fact]
    public void ChargeLevelOutOfRangeIsUnsupported()
    {
        var b = Ok(BatteryBuilder.Build(Power(percent: 255), [Laptop()]));
        Assert.Equal(ValueStatus.Unsupported, b.ChargePercent.Status);
    }

    [Fact]
    public void TwoBatteriesAreSaidSo()
    {
        var b = Ok(BatteryBuilder.Build(Power(), [Laptop(), Laptop(design: 10_000, full: 9_000)]));
        Assert.Contains("2 batteries", b.DesignCapacityMilliwattHours.Detail);
        Assert.Equal(50_000, b.DesignCapacityMilliwattHours.Data);
    }

    [Theory]
    [InlineData("LION", "Lithium-ion")]
    [InlineData("LiP", "Lithium polymer")]
    [InlineData("NiMH", "Nickel metal hydride")]
    [InlineData("PbAc", "Lead acid")]
    public void ChemistryCodesGetPlainNames(string code, string expected) => Assert.Equal(expected, BatteryBuilder.ChemistryName(code));

    [Fact]
    public void UnknownChemistryCodeIsShownAsReported()
    {
        var b = Ok(BatteryBuilder.Build(Power(), [Laptop(chemistry: "XYZ1")]));
        Assert.Equal("XYZ1", b.Details!.Chemistry.Data);
        var none = Ok(BatteryBuilder.Build(Power(), [Laptop(chemistry: "")]));
        Assert.Equal(ValueStatus.Unsupported, none.Details!.Chemistry.Status);
    }

    [Theory]
    [InlineData(4u, "charging")]
    [InlineData(5u, "charging")]
    [InlineData(2u, "discharging")]
    [InlineData(1u, "plugged in, not charging")]
    [InlineData(0u, "idle")]
    [InlineData(10u, "discharging (critical)")]
    public void PowerStateFlagsAreWords(uint state, string expected) => Assert.Equal(expected, BatteryBuilder.StateName(state));

    [Fact]
    public void BatteryInformationIsParsedFromTheIoctlLayout()
    {
        var b = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(b, BatteryNative.CapSystemBattery);
        Encoding.ASCII.GetBytes("LION").CopyTo(b, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 57_000);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 51_000);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), 301);
        var info = BatteryNative.ParseInformation(b)!;
        Assert.Equal(BatteryNative.CapSystemBattery, info.Capabilities);
        Assert.Equal("LION", info.Chemistry);
        Assert.Equal(57_000u, info.DesignedCapacity);
        Assert.Equal(51_000u, info.FullChargedCapacity);
        Assert.Equal(301u, info.CycleCount);
    }

    [Fact]
    public void ShortIoctlAnswersAreNotParsed()
    {
        Assert.Null(BatteryNative.ParseInformation(new byte[35]));
        Assert.Equal((null, null), BatteryNative.ParseStatus(new byte[15]));
    }

    [Fact]
    public void BatteryStatusIsParsedFromTheIoctlLayout()
    {
        var b = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 12_450);
        Assert.Equal((4u, 12_450u), BatteryNative.ParseStatus(b));
    }

    [Fact]
    public void IoctlRequestsHaveTheDocumentedSizes()
    {
        Assert.Equal(12, BatteryNative.QueryInformation(7, 0).Length);
        Assert.Equal(20, BatteryNative.WaitStatus(7).Length);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(BatteryNative.WaitStatus(7)));
    }

    [Fact]
    public void RealCollectionRunsOnThisMachineWithoutThrowing()
    {
        var v = BatteryCollector.Collect();
        Assert.True(v.Status is ValueStatus.Ok or ValueStatus.Unsupported or ValueStatus.Error, v.Detail);
        if (v.Status == ValueStatus.Ok)
        {
            Assert.InRange(v.Data!.ChargePercent.Data, 0, 100);
        }
    }
}
