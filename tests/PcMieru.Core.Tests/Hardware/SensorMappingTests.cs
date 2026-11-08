using PcMieru.Core.Hardware.Sensors;

namespace PcMieru.Core.Tests.Hardware;

public class SensorMappingTests
{
    [Theory]
    [InlineData(0, ValueStatus.Ok)]
    [InlineData(SensorMapping.FunctionMissing, ValueStatus.Unsupported)]
    [InlineData(3, ValueStatus.Unsupported)]
    [InlineData(6, ValueStatus.Unsupported)]
    [InlineData(13, ValueStatus.Unsupported)]
    [InlineData(24, ValueStatus.Unsupported)]
    [InlineData(4, ValueStatus.Denied)]
    [InlineData(10, ValueStatus.Timeout)]
    [InlineData(15, ValueStatus.Gone)]
    [InlineData(999, ValueStatus.Error)]
    public void NvmlResultCodesBecomeStatuses(int code, ValueStatus expected) =>
        Assert.Equal(expected, SensorMapping.StatusOf(VendorApi.Nvml, code));

    [Theory]
    [InlineData(0, ValueStatus.Ok)]
    [InlineData(SensorMapping.FunctionMissing, ValueStatus.Unsupported)]
    [InlineData(9, ValueStatus.Unsupported)]
    [InlineData(12, ValueStatus.Unsupported)]
    [InlineData(14, ValueStatus.Unsupported)]
    [InlineData(4, ValueStatus.Error)]
    [InlineData(15, ValueStatus.Error)]
    public void AdlxResultCodesBecomeStatuses(int code, ValueStatus expected) =>
        Assert.Equal(expected, SensorMapping.StatusOf(VendorApi.Adlx, code));

    [Fact]
    public void DescriptionsNameTheVendorAndTheCode()
    {
        Assert.Equal("NVML result 3 (not supported by this GPU)", SensorMapping.Describe(VendorApi.Nvml, 3));
        Assert.Equal("ADLX result 12 (not supported by this GPU)", SensorMapping.Describe(VendorApi.Adlx, 12));
        Assert.Equal("NVML result 77", SensorMapping.Describe(VendorApi.Nvml, 77));
        Assert.Equal("this driver does not provide the function", SensorMapping.Describe(VendorApi.Nvml, SensorMapping.FunctionMissing));
    }

    [Fact]
    public void ANumberThatWasReadIsOkAndKeepsItsSource()
    {
        var v = SensorMapping.Number(VendorApi.Nvml, 39, 0, "nvml", 0, 150);
        Assert.True(v.IsOk);
        Assert.Equal(39, v.Data);
        Assert.Equal("nvml", v.Source);
    }

    [Fact]
    public void AFailedReadNeverCarriesTheNumber()
    {
        // NVML leaves 0 in the out-parameter when it fails; that 0 must not reach the screen.
        var v = SensorMapping.Number(VendorApi.Nvml, 0, 3, "nvml", 0, 150);
        Assert.Equal(ValueStatus.Unsupported, v.Status);
        Assert.Contains("not supported", v.Detail);
        Assert.False(v.IsOk);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(151)]
    public void ImpossibleNumbersAreErrorsNotValues(double number)
    {
        var v = SensorMapping.Number(VendorApi.Nvml, number, 0, "nvml", 0, 150);
        Assert.Equal(ValueStatus.Error, v.Status);
        Assert.False(v.IsOk);
    }

    [Fact]
    public void RangeEdgesAreAccepted()
    {
        Assert.True(SensorMapping.Number(VendorApi.Nvml, 0, 0, "n", 0, 150).IsOk);
        Assert.True(SensorMapping.Number(VendorApi.Nvml, 150, 0, "n", 0, 150).IsOk);
    }

    [Fact]
    public void ANoteIsAddedOnlyToAGoodReading()
    {
        var good = SensorMapping.NumberNote(VendorApi.Nvml, 262.5, 0, "nvml", 0, 2000, "limit in force now");
        Assert.True(good.IsOk);
        Assert.Equal("limit in force now", good.Detail);
        var bad = SensorMapping.NumberNote(VendorApi.Nvml, 0, 4, "nvml", 0, 2000, "limit in force now");
        Assert.Equal(ValueStatus.Denied, bad.Status);
        Assert.DoesNotContain("limit in force now", bad.Detail);
    }

    [Fact]
    public void IntegersRoundAndBytesTruncateToWholeNumbers()
    {
        Assert.Equal(5, SensorMapping.Integer(VendorApi.Nvml, 4.6, 0, "n", 0, 10).Data);
        Assert.Equal(ValueStatus.Error, SensorMapping.Integer(VendorApi.Nvml, 11, 0, "n", 0, 10).Status);
        Assert.Equal(12_820_938_752L, SensorMapping.Bytes(VendorApi.Nvml, 12_820_938_752d, 0, "n").Data);
        Assert.Equal(ValueStatus.Error, SensorMapping.Bytes(VendorApi.Nvml, -1, 0, "n").Status);
        Assert.Equal(ValueStatus.Denied, SensorMapping.Integer(VendorApi.Nvml, 0, 4, "n", 0, 10).Status);
    }

    [Fact]
    public void TextIsTrimmedAndAnEmptyTextIsNotAValue()
    {
        Assert.Equal("98.05.28.00.9b", SensorMapping.Text(VendorApi.Nvml, "  98.05.28.00.9b ", 0, "nvml").Data);
        Assert.Equal(ValueStatus.Unsupported, SensorMapping.Text(VendorApi.Nvml, "   ", 0, "nvml").Status);
        Assert.Equal(ValueStatus.Unsupported, SensorMapping.Text(VendorApi.Nvml, null, 0, "nvml").Status);
        Assert.Equal(ValueStatus.Timeout, SensorMapping.Text(VendorApi.Nvml, "x", 10, "nvml").Status);
    }

    [Fact]
    public void AbsentReadingsCarryTheReasonOnEveryField()
    {
        var v = SensorMapping.Absent<double>(ValueStatus.Unsupported, "sensorhost", "no NVIDIA driver");
        Assert.Equal(ValueStatus.Unsupported, v.Status);
        Assert.Equal("no NVIDIA driver", v.Detail);
    }

    [Theory]
    [InlineData("AMD Radeon(TM) Graphics", "radeongraphics")]
    [InlineData("AMD Radeon (TM) Graphics", "radeongraphics")]
    [InlineData("Radeon Graphics", "radeongraphics")]
    [InlineData("NVIDIA GeForce RTX 5070", "nvidiageforcertx5070")]
    public void NamesAreNormalisedForPairing(string name, string expected) => Assert.Equal(expected, SensorMapping.NormalizeName(name));

    [Fact]
    public void OneAdapterAndOneVendorGpuPairEvenWhenTheNamesDiffer()
    {
        var pairs = SensorMapping.PairByName(["AMD Radeon(TM) Graphics"], ["Some other marketing name"], out var ambiguous);
        Assert.False(ambiguous);
        Assert.Equal(0, pairs[0]);
    }

    [Fact]
    public void SameNameOnBothSidesPairs()
    {
        var pairs = SensorMapping.PairByName(["AMD Radeon(TM) Graphics"], ["Radeon Graphics"], out var ambiguous);
        Assert.False(ambiguous);
        Assert.Single(pairs);
    }

    [Fact]
    public void PairingIsByNameAcrossSeveralGpus()
    {
        var pairs = SensorMapping.PairByName(["AMD Radeon RX 7800 XT", "AMD Radeon(TM) Graphics"], ["Radeon Graphics", "Radeon RX 7800 XT"], out var ambiguous);
        Assert.False(ambiguous);
        Assert.Equal(1, pairs[0]);
        Assert.Equal(0, pairs[1]);
    }

    [Fact]
    public void TwoIdenticalCardsAreNeverPairedBecauseTheyCannotBeToldApart()
    {
        var pairs = SensorMapping.PairByName(["AMD Radeon RX 7800 XT", "AMD Radeon RX 7800 XT"], ["Radeon RX 7800 XT", "Radeon RX 7800 XT"], out var ambiguous);
        Assert.True(ambiguous);
        Assert.Empty(pairs);
    }

    [Fact]
    public void NoNameMatchWithSeveralGpusPairsNothing()
    {
        var pairs = SensorMapping.PairByName(["AMD Radeon RX 7800 XT", "AMD Radeon(TM) Graphics"], ["Unknown A", "Unknown B"], out var ambiguous);
        Assert.False(ambiguous);
        Assert.Empty(pairs);
    }

    [Fact]
    public void AdaptersWithTheSameNameAreNeverPairedToOneVendorGpu()
    {
        // Two adapters called the same, one vendor GPU: either adapter could be the one, so neither gets its numbers.
        var pairs = SensorMapping.PairByName(["Radeon A", "Radeon A"], ["Radeon A"], out var ambiguous);
        Assert.True(ambiguous);
        Assert.Empty(pairs);
    }

    [Fact]
    public void PciBusIdsRoundTrip()
    {
        var text = PciBusIds.Format(0, 18, 0);
        Assert.Equal("0000:12:00", text);
        Assert.True(PciBusIds.TryParse(text, out var bus, out var device));
        Assert.Equal(18u, bus);
        Assert.Equal(0u, device);
        Assert.Equal("0000:01:00", PciBusIds.Format(0, 1, 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01:00")]
    [InlineData("0000:01:00.0")]
    [InlineData("0000:zz:00")]
    [InlineData("bus 1, device 0")]
    public void MalformedBusIdsAreRejected(string? text) => Assert.False(PciBusIds.TryParse(text, out _, out _));

    [Theory]
    [InlineData("bus 1, device 0, function 0", 1u, 0u)]
    [InlineData("bus 18, device 0", 18u, 0u)]
    [InlineData("bus 255, device 31, function 7", 255u, 31u)]
    public void TheGpuListLocationTextIsParsed(string text, uint bus, uint device)
    {
        Assert.True(PciBusIds.TryParseLocation(text, out var b, out var d));
        Assert.Equal(bus, b);
        Assert.Equal(device, d);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0000:01:00")]
    [InlineData("bus x, device 0")]
    [InlineData("bus 99999999999, device 0")]
    public void MalformedLocationsAreRejected(string? text) => Assert.False(PciBusIds.TryParseLocation(text, out _, out _));
}
