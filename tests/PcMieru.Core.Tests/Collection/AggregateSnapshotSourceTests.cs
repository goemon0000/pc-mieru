using PcMieru.Core.Collection;

namespace PcMieru.Core.Tests.Collection;

/// <summary>
/// The aggregate-only source (Parts Life, DESIGN-C 2.1): machine-wide numbers and nothing per process. The pure part (the GPU aggregator without
/// a per-process table) runs everywhere; the part that opens real counters is a real-machine test.
/// </summary>
public sealed class AggregateGpuAggregatorTests
{
    private const uint Valid = Pdh.CStatusValidData;
    private const ulong Gpu0 = 0x166CA;

    private static GpuAggregator Fill(bool trackProcesses)
    {
        var a = new GpuAggregator(trackProcesses);
        a.Begin();
        a.AddEngine(GpuNames.Engine(10, Gpu0, 0, 0, "3D"), 30, Valid);
        a.AddEngine(GpuNames.Engine(11, Gpu0, 0, 0, "3D"), 40, Valid);
        a.AddEngine(GpuNames.Engine(10, Gpu0, 0, 1, "Copy"), 5, Valid);
        a.AddAdapterMemory(GpuNames.Adapter(Gpu0), 2048, Valid, dedicated: true);
        a.Finish();
        return a;
    }

    [Fact]
    public void Without_per_process_tracking_no_process_entry_is_ever_made()
    {
        var a = Fill(trackProcesses: false);

        Assert.Empty(a.ProcessBusiest);
        Assert.Equal(0, a.PerProcessEntries);
    }

    [Fact]
    public void The_default_still_tracks_processes_as_before()
    {
        var a = Fill(trackProcesses: true);

        Assert.Equal(2, a.ProcessBusiest.Count);
        Assert.Equal(30, a.ProcessBusiest[10]);
        Assert.Equal(3, a.PerProcessEntries);
    }

    [Fact]
    public void The_adapter_numbers_are_the_same_with_or_without_the_per_process_table()
    {
        var tracked = Fill(trackProcesses: true).Adapters[Gpu0];
        var aggregate = Fill(trackProcesses: false).Adapters[Gpu0];

        Assert.Equal(70, aggregate.Busiest);
        Assert.Equal(tracked.Busiest, aggregate.Busiest);
        Assert.Equal(tracked.EngineInstances, aggregate.EngineInstances);
        Assert.Equal(tracked.ByType.OrderBy(t => t.Key), aggregate.ByType.OrderBy(t => t.Key));
        Assert.Equal(tracked.DedicatedUsed, aggregate.DedicatedUsed);
    }

    [Fact]
    public void A_gpu_sampler_is_aggregate_only_only_when_asked()
    {
        using var normal = new GpuSampler();
        using var aggregate = new GpuSampler(aggregateOnly: true);

        Assert.False(normal.AggregateOnly);
        Assert.True(aggregate.AggregateOnly);
    }
}

/// <summary>These open the real PDH, DXGI and network counters, but never look at a process.</summary>
[Trait("Category", "RealMachine")]
public sealed class AggregateSnapshotSourceRealTests
{
    [Fact]
    public async Task A_sample_has_no_processes_and_the_second_one_has_machine_wide_numbers()
    {
        using var source = new AggregateSnapshotSource(static () => DateTime.UtcNow);

        var first = await source.SampleAsync();
        await Task.Delay(400);
        var second = await source.SampleAsync();

        Assert.Empty(first.Processes);
        Assert.Empty(second.Processes);
        Assert.Equal(Snapshot.CurrentSchemaVersion, second.SchemaVersion);
        Assert.True(second.System.Cpu.LogicalProcessors > 0);
        Assert.True(second.System.Cpu.TotalPercent.IsOk, second.System.Cpu.TotalPercent.Detail);
        Assert.True(second.System.Memory.TotalPhysicalBytes > 0);
        Assert.False(ListStatus.IsUnset(second.System.DisksState));
        Assert.False(ListStatus.IsUnset(second.System.NicsState));
        Assert.False(ListStatus.IsUnset(second.System.GpusState));
    }

    [Fact]
    public async Task The_gpu_sensor_fields_stay_not_collected_and_the_gpu_counters_run_without_any_subscription()
    {
        // SamplerSettings.GpuDefaultOn is false here on purpose: this source collects the GPU totals whatever the control says.
        using var source = new AggregateSnapshotSource(static () => DateTime.UtcNow, new SamplerSettings { GpuDefaultOn = false });

        _ = await source.SampleAsync();
        await Task.Delay(400);
        var gpus = (await source.SampleAsync()).System.Gpus;

        Assert.All(gpus, g =>
        {
            Assert.Equal(ValueStatus.NotCollected, g.TemperatureC.Status);
            Assert.Equal(ValueStatus.NotCollected, g.PowerWatts.Status);
            Assert.Equal(ValueStatus.NotCollected, g.FanPercent.Status);
            Assert.Equal(ValueStatus.NotCollected, g.ClockMhz.Status);
            Assert.Null(g.VendorOrigin);
        });
        Assert.NotEqual(GpuSampler.OffDetail, (await source.SampleAsync()).System.GpusState.Detail);
    }

    [Fact]
    public async Task A_gpu_sampler_in_aggregate_mode_keeps_no_per_process_entries()
    {
        using var gpu = new GpuSampler(aggregateOnly: true);

        _ = gpu.Sample(active: true);
        await Task.Delay(400);
        var reading = gpu.Sample(active: true);

        Assert.Empty(reading.ProcessBusiest);
        Assert.Equal(0, gpu.PerProcessEntries);
    }

    [Fact]
    public async Task ResetBaseline_makes_the_next_sample_a_first_sample_again_and_Dispose_twice_is_fine()
    {
        var source = new AggregateSnapshotSource(static () => DateTime.UtcNow);
        _ = await source.SampleAsync();
        await Task.Delay(400);
        Assert.True((await source.SampleAsync()).System.Cpu.TotalPercent.IsOk);

        source.ResetBaseline();
        var afterReset = await source.SampleAsync();

        Assert.False(afterReset.System.Cpu.TotalPercent.IsOk);
        source.Dispose();
        source.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.SampleAsync());
    }
}
