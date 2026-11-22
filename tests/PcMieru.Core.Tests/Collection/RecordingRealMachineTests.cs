using System.Collections.Concurrent;
using System.Diagnostics;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Sensors;
using Xunit.Abstractions;

namespace PcMieru.Core.Tests.Collection;

/// <summary>
/// The recording lease on this PC with the real sampler, the real Windows counters and (when its build output is found) the real SensorHost.
/// It prints what comes out: CPU package power, GPU limit reasons, AC and battery, with the reason for every value that is missing. What it
/// asserts is only the contract: a value is a real number or missing with a reason, never a made-up zero, and the frames are numbered.
/// </summary>
[Trait("Category", "RealMachine")]
public sealed class RecordingRealMachineTests(ITestOutputHelper output)
{
    private static string? FindSensorHost()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcMieru.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return null;
        }

        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var path = Path.Combine(dir.FullName, "src", "PcMieru.SensorHost", "bin", configuration, "net10.0-windows", SystemSensorProcessFactory.ExeName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string Show<T>(Value<T> value) =>
        value.IsOk ? $"{value.Data} [{value.Source}]" : $"MISSING {value.Status}: {value.Detail} [{value.Source}]";

    [Fact]
    public async Task A_lease_on_this_pc_records_numbered_one_second_samples_with_cpu_power_gpu_limits_and_the_ac_state()
    {
        var exe = FindSensorHost();
        output.WriteLine("SensorHost: " + (exe ?? "NOT FOUND (vendor GPU values are not covered by this run)"));
        using var client = exe is null ? null : new SensorHostClient(exe);
        var control = new SamplerSettings();
        using var source = new WindowsSnapshotSource(static () => DateTime.UtcNow, control, () => client);
        using var loop = new SamplerLoop(source, control, () => client);
        var samples = new ConcurrentQueue<RecordingSample>();
        loop.RecordingSampled += samples.Enqueue;

        loop.Start();
        using (loop.AcquireRecordingLease())
        {
            Assert.True(loop.IsRecording);

            // Wait for CONDITIONS, never for a duration: enough samples, and (with a SensorHost) one that carries a vendor frame.
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(60) && !Ready(samples, exe is not null))
            {
                await Task.Delay(200);
            }
        }

        await loop.StopAsync();
        var all = samples.ToArray();
        output.WriteLine($"{all.Length} samples");
        Assert.True(all.Length >= 3, "the lease produced fewer than three samples");
        Assert.False(loop.IsRecording);

        foreach (var sample in all)
        {
            output.WriteLine(
                $"core={sample.Core.Frame} period={sample.Core.PeriodMs} interval={sample.IntervalMs} " +
                $"cpu={Show(sample.CpuUsagePercent)} | cpuPower={Show(sample.CpuPackageWatts)} | " +
                $"ac={Show(sample.Power.AcConnected)} | battery={Show(sample.Power.BatteryPercent)} {Show(sample.Power.BatteryState)}");
            foreach (var gpu in sample.Gpus)
            {
                output.WriteLine(
                    $"  gpu {gpu.Name}: util={Show(gpu.UtilizationPercent)} | temp={Show(gpu.TemperatureC)} | power={Show(gpu.PowerWatts)} | " +
                    $"limit={Show(gpu.LimitReasons)} | vendor frame={gpu.Vendor.Frame?.ToString() ?? "none"} gap={gpu.Vendor.Gap} period={gpu.Vendor.PeriodMs}");
            }
        }

        // The sampler's own pass is numbered, increasing, and stands for one second.
        Assert.All(all, s => Assert.Equal(1000, s.Core.PeriodMs));
        var sequences = all.Select(s => s.Core.Frame!.Value.Sequence).ToArray();
        Assert.Equal(sequences.OrderBy(n => n), sequences);
        Assert.Equal(sequences.Length, sequences.Distinct().Count());

        // A number or a reason, never a zero standing in for "no counter".
        foreach (var sample in all)
        {
            Assert.True(sample.CpuPackageWatts.IsOk ? sample.CpuPackageWatts.Data > 0 : !string.IsNullOrWhiteSpace(sample.CpuPackageWatts.Detail), Show(sample.CpuPackageWatts));
            Assert.True(sample.Power.AcConnected.IsOk || !string.IsNullOrWhiteSpace(sample.Power.AcConnected.Detail), Show(sample.Power.AcConnected));
            foreach (var gpu in sample.Gpus)
            {
                Assert.True(gpu.LimitReasons.IsOk || !string.IsNullOrWhiteSpace(gpu.LimitReasons.Detail) || gpu.LimitReasons.Status == ValueStatus.NotCollected, Show(gpu.LimitReasons));
            }
        }
    }

    private static bool Ready(ConcurrentQueue<RecordingSample> samples, bool withVendor)
    {
        var all = samples.ToArray();
        return all.Length >= 5 && (!withVendor || all.Any(s => s.Gpus.Any(g => g.Vendor.Frame is not null && g.LimitReasons.Status != ValueStatus.NotCollected)));
    }
}
