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
            Value.Unsupported<IReadOnlyList<GpuSensorReading>>("sensorhost", "no NVIDIA or AMD driver"),
            GenerationId: 1,
            Sequence: 1,
            PeriodMs: 2000,
            MonotonicUs: 5_000_000));
        Assert.True(SensorFrameParser.TryParse(line, out var frame, out _));
        Assert.Equal(ValueStatus.Unsupported, frame!.Gpus.Status);
        Assert.Equal("no NVIDIA or AMD driver", frame.Gpus.Detail);
        Assert.Null(frame.Gpus.Data);
    }

    [Fact]
    public void ADifferentSchemaVersionIsRefused()
    {
        // The schema the previous child wrote: a parent of this build must refuse it, in words, and say it is a mismatch rather than noise.
        var line = PcMieruJson.Serialize(new SensorFrame("1", DateTime.UtcNow, SensorFixtures.Readings()));
        Assert.False(SensorFrameParser.TryParse(line, out var frame, out var error, out var incompatible));
        Assert.Null(frame);
        Assert.True(incompatible);
        Assert.Contains("schema 1", error);
        Assert.Contains("expects 2", error);
    }

    [Fact]
    public void TheCurrentSchemaIsTwoSoAChildOfTheOldBuildCannotBeMistakenForIt()
    {
        Assert.Equal("2", SensorFrame.CurrentSchemaVersion);
    }

    [Fact]
    public void ASchemaTwoFrameWithoutItsAcquisitionFieldsIsUnreadableNotIncompatible()
    {
        foreach (var frame in new[]
        {
            SensorFixtures.Frame(1, 0, 2000),
            SensorFixtures.Frame(1, 5, 0),
            SensorFixtures.Frame(1, 5, 2000) with { MonotonicUs = 0 },
        })
        {
            var line = SensorFixtures.Serialize(frame);
            Assert.False(SensorFrameParser.TryParse(line, out var parsed, out var error, out var incompatible));
            Assert.Null(parsed);
            Assert.False(incompatible);
            Assert.Contains("without its sequence", error);
        }
    }

    [Fact]
    public void AFrameKeepsItsGenerationSequencePeriodAndMonotonicTime()
    {
        var line = SensorFixtures.Serialize(SensorFixtures.Frame(3, 41, 1000, SensorFixtures.Reading("luid_a")));

        Assert.True(SensorFrameParser.TryParse(line, out var frame, out var error, out var incompatible), error);
        Assert.False(incompatible);
        Assert.Equal(3, frame!.GenerationId);
        Assert.Equal(41, frame.Sequence);
        Assert.Equal(1000, frame.PeriodMs);
        Assert.Equal(1_000_000 + (41 * 1_000_000L), frame.MonotonicUs);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(0x4UL)]
    [InlineData(0x1FFUL)]
    [InlineData(ulong.MaxValue)]
    public void TheLimitReasonMaskTravelsAsAnUnsignedNumber(ulong mask)
    {
        var line = SensorFixtures.FrameLine(SensorFixtures.Reading("luid_a", extras: SensorFixtures.Extras(reasons: mask)));

        Assert.True(SensorFrameParser.TryParse(line, out var frame, out var error), error);
        var reasons = frame!.Gpus.Data![0].Extras!.ClocksEventReasons;
        Assert.True(reasons.IsOk);
        Assert.Equal(mask, reasons.Data);
    }

    [Fact]
    public void AnUnsupportedLimitReasonStaysMissingWithItsReason()
    {
        var line = SensorFixtures.FrameLine(SensorFixtures.Reading("luid_a", extras: SensorFixtures.Extras(reasons: null)));

        Assert.True(SensorFrameParser.TryParse(line, out var frame, out var error), error);
        var reasons = frame!.Gpus.Data![0].Extras!.ClocksEventReasons;
        Assert.Equal(ValueStatus.Unsupported, reasons.Status);
        Assert.False(reasons.IsOk);
        Assert.Equal(0UL, reasons.Data);
    }

    [Fact]
    public void AFrameFromAHostThatPredatesTheLimitReasonsLeavesThemNotCollected()
    {
        var line = SensorFixtures.FrameLine(SensorFixtures.Reading("luid_a", extras: SensorFixtures.Extras(reasons: 4)));
        const string key = ",\"clocksEventReasons\":";
        var start = line.IndexOf(key, StringComparison.Ordinal);
        Assert.True(start > 0, "the field is in the frame");
        var end = line.IndexOf('}', start) + 1;
        var old = line.Remove(start, end - start);

        Assert.True(SensorFrameParser.TryParse(old, out var frame, out var error), error);
        Assert.Equal(ValueStatus.NotCollected, frame!.Gpus.Data![0].Extras!.ClocksEventReasons.Status);
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
    public void The_temperature_limit_travels_in_the_frame_with_its_status()
    {
        var line = SensorFixtures.FrameLine(
            SensorFixtures.Reading("luid_a", extras: SensorFixtures.Extras(slowdownC: 88)),
            SensorFixtures.Reading("luid_b", extras: SensorFixtures.Extras(slowdownC: null)));

        Assert.True(SensorFrameParser.TryParse(line, out var frame, out var error), error);
        var gpus = frame!.Gpus.Data!;
        Assert.Equal(88, gpus[0].Extras!.TemperatureLimitC.Data);
        Assert.Equal(ValueStatus.Unsupported, gpus[1].Extras!.TemperatureLimitC.Status);
    }

    [Fact]
    public void A_frame_from_a_host_that_predates_the_limit_leaves_it_not_collected()
    {
        var line = SensorFixtures.FrameLine(SensorFixtures.Reading("luid_a", extras: SensorFixtures.Extras()));
        const string key = ",\"temperatureLimitC\":";
        var start = line.IndexOf(key, StringComparison.Ordinal);
        Assert.True(start > 0, "the field is in the frame");
        var end = line.IndexOf('}', start) + 1;
        var old = line.Remove(start, end - start);

        Assert.True(SensorFrameParser.TryParse(old, out var frame, out var error), error);
        Assert.Equal(ValueStatus.NotCollected, frame!.Gpus.Data![0].Extras!.TemperatureLimitC.Status);
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
