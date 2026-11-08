using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Sensors;

namespace PcMieru.Core.Tests.Hardware;

public class SensorFrameParserTests
{
    [Fact]
    public void AFrameRoundTripsWithEveryStatusIntact()
    {
        var extras = SensorFixtures.Extras(pciBusId: "0000:01:00", limitMin: null);
        var line = SensorFixtures.FrameLine(
            SensorFixtures.Reading("luid_0x00000000_0x0001a2b3", extras: extras),
            SensorFixtures.Reading("adlx:0", "AMD Radeon(TM) Graphics", limit: null));
        Assert.DoesNotContain('\n', line);

        Assert.True(SensorFrameParser.TryParse(line, out var frame, out var error), error);
        Assert.Equal(SensorFrame.CurrentSchemaVersion, frame!.SchemaVersion);
        Assert.True(frame.Gpus.IsOk);
        var gpus = frame.Gpus.Data!;
        Assert.Equal(2, gpus.Count);
        Assert.Equal("luid_0x00000000_0x0001a2b3", gpus[0].Id);
        Assert.Equal(40, gpus[0].TemperatureC.Data);
        Assert.Equal("0000:01:00", gpus[0].Extras!.PciBusId);
        Assert.Equal("98.05.28.00.9b", gpus[0].Extras!.Vbios.Data);
        Assert.Equal(ValueStatus.Unsupported, gpus[0].Extras!.PowerLimitMinWatts.Status);
        Assert.Equal(ValueStatus.Unsupported, gpus[1].PowerLimitWatts.Status);
        Assert.Null(gpus[1].Extras);
    }

    [Fact]
    public void AFrameWithoutGpusKeepsTheReasonInsteadOfAnEmptyList()
    {
        var line = PcMieruJson.Serialize(new SensorFrame(
            SensorFrame.CurrentSchemaVersion,
            DateTime.UtcNow,
            Value.Unsupported<IReadOnlyList<GpuSensorReading>>("sensorhost", "no NVIDIA or AMD driver")));
        Assert.True(SensorFrameParser.TryParse(line, out var frame, out _));
        Assert.Equal(ValueStatus.Unsupported, frame!.Gpus.Status);
        Assert.Equal("no NVIDIA or AMD driver", frame.Gpus.Detail);
        Assert.Null(frame.Gpus.Data);
    }

    [Fact]
    public void ADifferentSchemaVersionIsRefused()
    {
        var line = PcMieruJson.Serialize(new SensorFrame("2", DateTime.UtcNow, SensorFixtures.Readings()));
        Assert.False(SensorFrameParser.TryParse(line, out var frame, out var error));
        Assert.Null(frame);
        Assert.Contains("schema 2", error);
        Assert.Contains("expects 1", error);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"schemaVersion\":")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"schemaVersion\":\"1\",\"takenAtUtc\":\"yesterday\",\"gpus\":{}}")]
    public void GarbageIsRefusedWithoutThrowing(string line)
    {
        Assert.False(SensorFrameParser.TryParse(line, out var frame, out var error));
        Assert.Null(frame);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TheLiteralNullIsNotAFrame()
    {
        Assert.False(SensorFrameParser.TryParse("null", out var frame, out var error));
        Assert.Null(frame);
        Assert.Contains("empty", error);
    }

    [Fact]
    public void AnAbsurdlyLongLineIsRefusedBeforeItIsParsed()
    {
        var line = new string('x', SensorFrameParser.MaxLineChars + 1);
        Assert.False(SensorFrameParser.TryParse(line, out _, out var error));
        Assert.Contains("longer", error);
    }

    [Fact]
    public void ARealisticFrameIsFarBelowTheLineCap()
    {
        var line = SensorFixtures.FrameLine(
            SensorFixtures.Reading("luid_0x00000000_0x0001a2b3", extras: SensorFixtures.Extras(pciBusId: "0000:01:00")),
            SensorFixtures.Reading("luid_0x00000000_0x00017dda", "AMD Radeon(TM) Graphics"));
        Assert.True(line.Length < 8_000, $"a two-GPU frame is {line.Length} characters");
    }
}
