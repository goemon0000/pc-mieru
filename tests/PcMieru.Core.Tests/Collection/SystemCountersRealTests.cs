using System.Diagnostics;
using PcMieru.Core.Collection;

namespace PcMieru.Core.Tests.Collection;

/// <summary>
/// The system-wide counters on the real machine. They never touch another process: only the sampler's own reads, plus a
/// <c>ping</c> this test starts and kills. Written to hold on a machine with no GPU or with unusual adapters, so what is
/// asserted is "never a wrong number, always a reason", with the exact values left to the synthetic tests.
/// </summary>
[Trait("Category", "RealMachine")]
public sealed class SystemCountersRealTests : IDisposable
{
    private readonly List<Process> _children = [];

    public void Dispose()
    {
        foreach (var child in _children)
        {
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                child.Dispose();
            }
        }
    }

    private Process StartPing()
    {
        var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        _children.Add(child);
        return child;
    }

    private static WindowsSnapshotSource NewSource(SamplerSettings? settings = null) =>
        new(static () => DateTime.UtcNow, settings ?? new SamplerSettings { GpuDefaultOn = false });

    [Fact]
    public async Task Disks_have_no_rates_in_the_first_sample_and_real_rates_after_the_second()
    {
        using var source = NewSource();

        var first = await source.SampleAsync();
        await Task.Delay(500);
        var second = await source.SampleAsync();

        Assert.All(first.System.Disks, d =>
        {
            Assert.Equal(ValueStatus.NotCollected, d.ActivePercent.Status);
            Assert.Equal(ValueStatus.NotCollected, d.ReadBytesPerSec.Status);
        });
        Assert.NotEmpty(second.System.Disks);
        Assert.Equal(second.System.Disks.Count, second.System.Disks.Select(d => d.Id).Distinct().Count());
        Assert.All(second.System.Disks, d =>
        {
            Assert.StartsWith("PhysicalDrive", d.Id);
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
            Assert.True(d.ActivePercent.IsOk, $"{d.Name}: {d.ActivePercent.Status} {d.ActivePercent.Detail}");
            Assert.InRange(d.ActivePercent.Data, 0, 100);
            Assert.True(d.ReadBytesPerSec.IsOk && d.ReadBytesPerSec.Data >= 0);
            Assert.True(d.WriteBytesPerSec.IsOk && d.WriteBytesPerSec.Data >= 0);
            Assert.True(d.ResponseTimeMs.IsOk && d.ResponseTimeMs.Data >= 0);
        });
    }

    [Fact]
    public async Task Network_adapters_are_listed_with_a_link_state_and_rates_from_the_second_sample()
    {
        using var source = NewSource();

        var first = await source.SampleAsync();
        await Task.Delay(500);
        var second = await source.SampleAsync();

        Assert.All(first.System.Nics, n => Assert.Equal(ValueStatus.NotCollected, n.SendBytesPerSec.Status));
        Assert.Equal(second.System.Nics.Count, second.System.Nics.Select(n => n.Id).Distinct().Count());
        Assert.All(second.System.Nics, n =>
        {
            Assert.False(string.IsNullOrWhiteSpace(n.Name));
            var fresh = n.SendBytesPerSec.Detail?.Contains("new since", StringComparison.Ordinal) == true;
            Assert.True(fresh || (n.SendBytesPerSec.IsOk && n.SendBytesPerSec.Data >= 0), $"{n.Name}: {n.SendBytesPerSec.Status} {n.SendBytesPerSec.Detail}");
            Assert.True(fresh || (n.ReceiveBytesPerSec.IsOk && n.ReceiveBytesPerSec.Data >= 0));
            if (n.LinkSpeedBitsPerSec.IsOk)
            {
                Assert.True(n.IsUp);
                Assert.True(n.LinkSpeedBitsPerSec.Data > 0);
            }
        });
    }

    [Fact]
    public async Task The_clock_speed_is_a_plausible_value_after_the_second_sample_or_says_why_not()
    {
        using var source = NewSource();

        _ = await source.SampleAsync();
        await Task.Delay(500);
        var mhz = (await source.SampleAsync()).System.Cpu.CurrentMhz;

        if (mhz.IsOk)
        {
            Assert.InRange(mhz.Data, 100, 10_000);
        }
        else
        {
            Assert.NotNull(mhz.Detail);
        }
    }

    [Fact]
    public async Task With_nobody_looking_at_the_GPU_nothing_is_collected_and_the_snapshot_says_so()
    {
        var settings = new SamplerSettings { GpuDefaultOn = false };
        using var source = NewSource(settings);
        Assert.False(settings.GpuActive);

        _ = await source.SampleAsync();
        await Task.Delay(300);
        var snap = await source.SampleAsync();

        Assert.Empty(snap.System.Gpus);
        Assert.All(snap.Processes, p =>
        {
            Assert.Equal(ValueStatus.NotCollected, p.GpuPercent.Status);
            Assert.Contains("off", p.GpuPercent.Detail, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Subscribing_turns_the_GPU_on_and_disposing_the_subscription_turns_it_off_again()
    {
        var settings = new SamplerSettings { GpuDefaultOn = false };
        using var source = NewSource(settings);
        _ = await source.SampleAsync();

        using (settings.SubscribeGpu())
        {
            Assert.True(settings.GpuActive);
            _ = await source.SampleAsync(); // PDH's first window: not collected, with a reason
            await Task.Delay(500);
            var on = await source.SampleAsync();

            // Whatever this machine's GPUs are, no adapter may claim a number it did not measure.
            foreach (var gpu in on.System.Gpus)
            {
                Assert.False(string.IsNullOrWhiteSpace(gpu.Name));
                Assert.True(gpu.UtilizationPercent.IsOk || gpu.UtilizationPercent.Detail is not null);
                if (gpu.UtilizationPercent.IsOk)
                {
                    Assert.InRange(gpu.UtilizationPercent.Data, 0, 100);
                    Assert.All(gpu.Engines, e => Assert.InRange(e.Percent.GetOr(0), 0, 100));
                    Assert.All(gpu.Engines, e => Assert.True(gpu.UtilizationPercent.Data >= e.Percent.GetOr(0) - 1e-9));
                }
            }

            Assert.All(on.Processes.Where(p => p.GpuPercent.IsOk), p => Assert.InRange(p.GpuPercent.Data, 0, 100));
        }

        Assert.False(settings.GpuActive);
        _ = await source.SampleAsync();
        var off = await source.SampleAsync();
        Assert.All(off.Processes, p => Assert.Equal(ValueStatus.NotCollected, p.GpuPercent.Status));
        Assert.All(off.System.Gpus, g => Assert.Equal(ValueStatus.NotCollected, g.UtilizationPercent.Status));
    }

    [Fact]
    public async Task A_process_started_between_two_samples_is_not_collected_for_GPU_until_a_sample_has_seen_it_twice()
    {
        var settings = new SamplerSettings { GpuDefaultOn = true };
        using var source = NewSource(settings);
        _ = await source.SampleAsync();
        await Task.Delay(400);
        var warm = await source.SampleAsync();
        var gpuWorks = warm.System.Gpus.Any(g => g.UtilizationPercent.IsOk);

        var ping = StartPing();
        await Task.Delay(300);
        var withNew = await source.SampleAsync();
        await Task.Delay(300);
        var seenTwice = await source.SampleAsync();

        var fresh = withNew.Processes.Single(p => p.Key.Pid == (uint)ping.Id).GpuPercent;
        var settled = seenTwice.Processes.Single(p => p.Key.Pid == (uint)ping.Id).GpuPercent;
        var story = $"fresh = {fresh.Status} '{fresh.Detail}'; settled = {settled.Status} '{settled.Detail}' {settled.Data}";
        Assert.True(fresh.Status != ValueStatus.Ok, story); // a process with no baseline never gets a zero it did not measure
        if (gpuWorks)
        {
            Assert.True(fresh.Status == ValueStatus.NotCollected && fresh.Detail is not null && fresh.Detail.Contains("new since", StringComparison.Ordinal), story);
            Assert.True(settled.IsOk && settled.Data == 0, story); // ping draws nothing
        }
    }

    [Fact]
    public async Task ResetBaseline_also_restarts_the_disk_network_and_GPU_windows()
    {
        using var source = NewSource(new SamplerSettings { GpuDefaultOn = true });
        _ = await source.SampleAsync();
        await Task.Delay(400);
        var running = await source.SampleAsync();
        Assert.NotEmpty(running.System.Disks);
        Assert.True(running.System.Disks[0].ActivePercent.IsOk);

        source.ResetBaseline();
        var afterReset = await source.SampleAsync();

        Assert.All(afterReset.System.Disks, d => Assert.Equal(ValueStatus.NotCollected, d.ActivePercent.Status));
        Assert.All(afterReset.System.Nics, n => Assert.Equal(ValueStatus.NotCollected, n.SendBytesPerSec.Status));
        Assert.All(afterReset.System.Gpus, g => Assert.Equal(ValueStatus.NotCollected, g.UtilizationPercent.Status));
        Assert.All(afterReset.Processes, p => Assert.Equal(ValueStatus.NotCollected, p.GpuPercent.Status));
    }

    [Fact]
    public async Task A_sensor_client_that_is_never_asked_for_GPU_is_never_called()
    {
        var calls = 0;
        using var source = new WindowsSnapshotSource(
            static () => DateTime.UtcNow,
            new SamplerSettings { GpuDefaultOn = false },
            () =>
            {
                Interlocked.Increment(ref calls);
                return null;
            });

        _ = await source.SampleAsync();
        await Task.Delay(300);
        _ = await source.SampleAsync();
        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Without_a_sensor_client_the_GPU_sensor_fields_are_unsupported_with_a_reason_when_GPU_is_on()
    {
        using var source = NewSource(new SamplerSettings { GpuDefaultOn = true });
        _ = await source.SampleAsync();
        await Task.Delay(400);
        _ = await source.SampleAsync();
        await Task.Delay(400);
        var snap = await source.SampleAsync();

        Assert.All(snap.System.Gpus, g =>
        {
            Assert.False(g.TemperatureC.IsOk);
            Assert.NotNull(g.TemperatureC.Detail);
        });
    }
}
