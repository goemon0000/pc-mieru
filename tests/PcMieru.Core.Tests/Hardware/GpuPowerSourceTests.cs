using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Sensors;

namespace PcMieru.Core.Tests.Hardware;

/// <summary>The AMD driver's "GPU power" is only a report. Front ends find it by its source, so the source must survive every hop.</summary>
public class GpuPowerSourceTests
{
    private static Value<double> Power(string source, ValueStatus status = ValueStatus.Ok) =>
        status == ValueStatus.Ok ? Value.Ok(17.3, source) : Value.Missing<double>(status, source, "x");

    [Theory]
    [InlineData("adlx:GPUPower", ValueStatus.Ok, true)]
    [InlineData("adlx:GPUTotalBoardPower", ValueStatus.Ok, false)]
    [InlineData("nvml:nvmlDeviceGetPowerUsage", ValueStatus.Ok, false)]
    [InlineData("fake:nvml", ValueStatus.Ok, false)]
    [InlineData("ADLX:GPUPOWER", ValueStatus.Ok, false)]
    [InlineData("adlx:GPUPower", ValueStatus.Unsupported, false)]
    [InlineData("adlx:GPUPower", ValueStatus.NotCollected, false)]
    public void Only_a_number_that_was_read_from_the_drivers_gpu_power_is_a_driver_report(string source, ValueStatus status, bool expected)
    {
        Assert.Equal(expected, GpuPowerSource.IsDriverReport(Power(source, status)));
    }

    [Fact]
    public void The_reading_keeps_its_source_through_the_sensor_merge_and_the_json_round_trip()
    {
        var reading = new GpuSensorReading(
            "luid_1",
            "AMD Radeon(TM) Graphics",
            Value.Ok(40.0, "adlx:GPUTemperature"),
            Power(GpuPowerSource.DriverReport),
            Value.Unsupported<double>("adlx", "no limit"),
            Value.Unsupported<double>("adlx", "rpm"),
            Value.Ok(800.0, "adlx:GPUClockSpeed"));

        var gpu = new GpuInfo(
            "luid_1",
            "AMD Radeon(TM) Graphics",
            Value.Ok(1.0, "t"),
            [],
            Value.Ok(1L, "t"),
            Value.Ok(2L, "t"),
            Value.Ok(3L, "t"),
            Value.NotCollected<double>("t"),
            Value.NotCollected<double>("t"),
            Value.NotCollected<double>("t"),
            Value.NotCollected<double>("t"));

        var merged = PcMieru.Core.Collection.GpuSensorMerge.Apply([gpu], Value.Ok<IReadOnlyList<GpuSensorReading>>([reading], "SensorHost")).Single();

        Assert.True(GpuPowerSource.IsDriverReport(merged.PowerWatts));

        var json = System.Text.Json.JsonSerializer.Serialize(merged.PowerWatts);
        var back = System.Text.Json.JsonSerializer.Deserialize<Value<double>>(json);
        Assert.True(GpuPowerSource.IsDriverReport(back));
    }

    [Fact]
    public void The_source_text_has_one_home_so_the_sensor_host_and_the_front_ends_cannot_drift_apart()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcMieru.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var src = Path.Combine(dir.FullName, "src");
        var sep = Path.DirectorySeparatorChar;
        var holders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !p.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p).Contains("\"adlx:GPUPower\"", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["GpuPowerSource.cs"], holders);
        var sampler = File.ReadAllText(Path.Combine(src, "PcMieru.SensorHost", "GpuSampler.cs"));
        Assert.Contains("GpuPowerSource.DriverReport", sampler, StringComparison.Ordinal);
    }
}
