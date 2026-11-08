using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Sensors;

namespace PcMieru.Core.Tests.Hardware;

public class GpuSensorMergeTests
{
    private const string LuidA = "luid_0x00000000_0x0001a2b3";
    private const string LuidB = "luid_0x00000000_0x0001a2c4";

    [Fact]
    public void AGpuIsJoinedToItsReadingByLuidIgnoringCase()
    {
        var gpus = SensorFixtures.Gpus(SensorFixtures.Gpu(LuidA));
        var sensors = SensorFixtures.Readings(SensorFixtures.Reading(LuidA.ToUpperInvariant(), extras: SensorFixtures.Extras()));

        var merged = GpuSensorMerge.Apply(gpus, sensors);

        var gpu = Assert.Single(merged.Data!);
        Assert.Equal("98.05.28.00.9b", gpu.Vbios.Data);
        Assert.Equal(262.5, gpu.PowerLimitWatts.Data);
    }

    [Fact]
    public void ThePowerLimitSaysItIsTheLimitInForceAndShowsTheDriversRange()
    {
        var merged = GpuSensorMerge.Apply(
            SensorFixtures.Gpus(SensorFixtures.Gpu(LuidA)),
            SensorFixtures.Readings(SensorFixtures.Reading(LuidA, extras: SensorFixtures.Extras())));

        var limit = merged.Data![0].PowerLimitWatts;
        Assert.True(limit.IsOk);
        Assert.Contains("limit in force now", limit.Detail);
        Assert.Contains("175 to 300 W", limit.Detail);
        Assert.Contains("board default 250 W", limit.Detail);
    }

    [Fact]
    public void WhenTheLuidDoesNotMatchTheGpuIsJoinedByItsPciPosition()
    {
        // SensorHost could not name the adapter by LUID ("nvml:0") but knows the bus position NVML reported.
        var gpus = SensorFixtures.Gpus(SensorFixtures.Gpu(LuidA, pciLocation: "bus 1, device 0, function 0"));
        var sensors = SensorFixtures.Readings(SensorFixtures.Reading("nvml:0", extras: SensorFixtures.Extras(pciBusId: "0000:01:00")));

        var merged = GpuSensorMerge.Apply(gpus, sensors);

        Assert.Equal("98.05.28.00.9b", merged.Data![0].Vbios.Data);
    }

    [Fact]
    public void ADifferentPciPositionIsNotAMatch()
    {
        var gpus = SensorFixtures.Gpus(SensorFixtures.Gpu(LuidA, pciLocation: "bus 1, device 0, function 0"));
        var sensors = SensorFixtures.Readings(SensorFixtures.Reading("nvml:0", extras: SensorFixtures.Extras(pciBusId: "0000:12:00")));

        var gpu = GpuSensorMerge.Apply(gpus, sensors).Data![0];

        Assert.Equal(ValueStatus.Unsupported, gpu.Vbios.Status);
        Assert.Contains("did not list this GPU", gpu.Vbios.Detail);
    }

    [Fact]
    public void TheNameAloneNeverJoinsAReadingToAGpu()
    {
        // Same model name on both sides, but neither the LUID nor the bus position agrees.
        var gpus = SensorFixtures.Gpus(SensorFixtures.Gpu(LuidA));
        var sensors = SensorFixtures.Readings(SensorFixtures.Reading(LuidB, extras: SensorFixtures.Extras()));

        var gpu = GpuSensorMerge.Apply(gpus, sensors).Data![0];

        Assert.Equal(ValueStatus.Unsupported, gpu.Vbios.Status);
        Assert.Equal(ValueStatus.Unsupported, gpu.PowerLimitWatts.Status);
    }

    [Fact]
    public void TwoCardsOfTheSameModelEachGetTheirOwnReading()
    {
        var gpus = SensorFixtures.Gpus(SensorFixtures.Gpu(LuidA), SensorFixtures.Gpu(LuidB));
        var sensors = SensorFixtures.Readings(
            SensorFixtures.Reading(LuidB, limit: 200, extras: SensorFixtures.Extras(vbios: "B.B.B")),
            SensorFixtures.Reading(LuidA, limit: 300, extras: SensorFixtures.Extras(vbios: "A.A.A")));

        var merged = GpuSensorMerge.Apply(gpus, sensors).Data!;

        Assert.Equal("A.A.A", merged[0].Vbios.Data);
        Assert.Equal(300, merged[0].PowerLimitWatts.Data);
        Assert.Equal("B.B.B", merged[1].Vbios.Data);
        Assert.Equal(200, merged[1].PowerLimitWatts.Data);
    }

    [Fact]
    public void TwoReadingsClaimingOneLuidAreAmbiguousAndNeitherIsUsed()
    {
        var gpus = SensorFixtures.Gpus(SensorFixtures.Gpu(LuidA));
        var sensors = SensorFixtures.Readings(
            SensorFixtures.Reading(LuidA, extras: SensorFixtures.Extras(vbios: "one")),
            SensorFixtures.Reading(LuidA, extras: SensorFixtures.Extras(vbios: "two")));

        var gpu = GpuSensorMerge.Apply(gpus, sensors).Data![0];

        Assert.Equal(ValueStatus.Unsupported, gpu.Vbios.Status);
    }

    [Fact]
    public void ANonVendorGpuSaysThatOnlyNvidiaAndAmdAreReadThroughAVendorLibrary()
    {
        var gpus = SensorFixtures.Gpus(SensorFixtures.Gpu(LuidA, vendor: "Intel", name: "Intel(R) UHD Graphics"));

        var gpu = GpuSensorMerge.Apply(gpus, SensorFixtures.Readings()).Data![0];

        Assert.Equal(ValueStatus.Unsupported, gpu.Vbios.Status);
        Assert.Contains("only NVIDIA and AMD", gpu.Vbios.Detail);
        Assert.Equal(ValueStatus.Unsupported, gpu.PowerLimitWatts.Status);
    }

    [Theory]
    [InlineData(ValueStatus.Timeout)]
    [InlineData(ValueStatus.Unsupported)]
    [InlineData(ValueStatus.Gone)]
    [InlineData(ValueStatus.Error)]
    public void WhenTheSensorReadingFailedTheRealReasonReplacesThePlaceholder(ValueStatus status)
    {
        var gpus = SensorFixtures.Gpus(SensorFixtures.Gpu(LuidA));
        var sensors = Value.Missing<IReadOnlyList<GpuSensorReading>>(status, "sensorhost", "the reason");

        var gpu = GpuSensorMerge.Apply(gpus, sensors).Data![0];

        Assert.Equal(status, gpu.Vbios.Status);
        Assert.Equal(status, gpu.PowerLimitWatts.Status);
        Assert.Equal("the reason", gpu.Vbios.Detail);
        Assert.Equal("the reason", gpu.PowerLimitWatts.Detail);
    }

    [Fact]
    public void AnOkStatusWithNoDataIsAnErrorNotACrash()
    {
        var gpus = SensorFixtures.Gpus(SensorFixtures.Gpu(LuidA));
        var broken = new Value<IReadOnlyList<GpuSensorReading>>(null, ValueStatus.Ok, "sensorhost");

        var gpu = GpuSensorMerge.Apply(gpus, broken).Data![0];

        Assert.Equal(ValueStatus.Error, gpu.Vbios.Status);
        Assert.Equal(ValueStatus.Error, gpu.PowerLimitWatts.Status);
    }

    [Fact]
    public void AGpuListThatIsNotOkIsReturnedUntouched()
    {
        var failed = Value.Denied<IReadOnlyList<GpuHardware>>("dxgi", "no access");

        var merged = GpuSensorMerge.Apply(failed, SensorFixtures.Readings(SensorFixtures.Reading(LuidA)));

        Assert.Equal(ValueStatus.Denied, merged.Status);
        Assert.Equal("no access", merged.Detail);
    }

    [Fact]
    public void AReadingWithoutExtrasGivesTheLimitButNoVbios()
    {
        var gpus = SensorFixtures.Gpus(SensorFixtures.Gpu(LuidA));
        var sensors = SensorFixtures.Readings(SensorFixtures.Reading(LuidA, extras: null));

        var gpu = GpuSensorMerge.Apply(gpus, sensors).Data![0];

        Assert.Equal(262.5, gpu.PowerLimitWatts.Data);
        Assert.Equal(ValueStatus.Unsupported, gpu.Vbios.Status);
        Assert.Contains("no VBIOS", gpu.Vbios.Detail);
    }

    [Fact]
    public void TheVendorDriverVersionIsAddedAsANoteNotInPlaceOfWindowsOwn()
    {
        var gpus = SensorFixtures.Gpus(SensorFixtures.Gpu(LuidA));
        var sensors = SensorFixtures.Readings(SensorFixtures.Reading(LuidA, extras: SensorFixtures.Extras(driver: "610.88")));

        var gpu = GpuSensorMerge.Apply(gpus, sensors).Data![0];

        Assert.Equal("32.0.15.8088", gpu.DriverVersion.Data);
        Assert.Contains("NVIDIA driver version 610.88", gpu.DriverVersion.Detail);
    }

    [Fact]
    public void WindowsPcieNumbersWinAndTheVendorOnlyFillsGaps()
    {
        var withWindowsGen = SensorFixtures.Gpu(LuidA) with { PcieGeneration = Value.Ok(4, "dxgi") };
        var sensors = SensorFixtures.Readings(SensorFixtures.Reading(LuidA, extras: SensorFixtures.Extras(gen: 5, width: 16)));

        var gpu = GpuSensorMerge.Apply(SensorFixtures.Gpus(withWindowsGen), sensors).Data![0];

        Assert.Equal(4, gpu.PcieGeneration.Data);
        Assert.Equal(16, gpu.PcieWidth.Data);
        Assert.Contains("current link", gpu.PcieWidth.Detail);
    }

    [Fact]
    public void MergingIsRepeatableAndDoesNotChangeTheInput()
    {
        var gpus = SensorFixtures.Gpus(SensorFixtures.Gpu(LuidA));
        var first = GpuSensorMerge.Apply(gpus, SensorFixtures.Readings(SensorFixtures.Reading(LuidA, extras: SensorFixtures.Extras(vbios: "v1"))));
        var second = GpuSensorMerge.Apply(gpus, SensorFixtures.Readings(SensorFixtures.Reading(LuidA, extras: SensorFixtures.Extras(vbios: "v2"))));

        Assert.Equal("v1", first.Data![0].Vbios.Data);
        Assert.Equal("v2", second.Data![0].Vbios.Data);
        Assert.Equal(ValueStatus.NotCollected, gpus.Data![0].Vbios.Status);
    }
}
