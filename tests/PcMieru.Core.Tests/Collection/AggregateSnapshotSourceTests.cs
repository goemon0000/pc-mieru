using System.Collections.Concurrent;
using PcMieru.Core.Collection;
using Xunit.Abstractions;

namespace PcMieru.Core.Tests.Collection;

/// <summary>
/// The aggregate-only source (Parts Life, DESIGN-C 2.1): machine-wide numbers and nothing per process. The pure part (the GPU aggregator without
/// a per-process table) runs everywhere; the part that opens real counters is a real-machine test.
/// </summary>
public sealed class AggregateGpuAggregatorTests
{
    private const uint Valid = Pdh.CStatusValidData;
    private const ulong Gpu0 = 0x166CA;

    private static GpuAggregator Fill(GpuAggregator a)
    {
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
        var a = Fill(new GpuAggregator(trackProcesses: false));

        Assert.Empty(a.ProcessBusiest);
        Assert.Equal(0, a.PerProcessEntries);
    }

    [Fact]
    public void The_default_still_tracks_processes_as_before()
    {
        var a = Fill(new GpuAggregator());

        Assert.Equal(2, a.ProcessBusiest.Count);
        Assert.Equal(30, a.ProcessBusiest[10]);
        Assert.Equal(3, a.PerProcessEntries);
    }

    [Fact]
    public void The_adapter_numbers_are_the_same_with_or_without_the_per_process_table()
    {
        var tracked = Fill(new GpuAggregator()).Adapters[Gpu0];
        var aggregate = Fill(new GpuAggregator(trackProcesses: false)).Adapters[Gpu0];

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

/// <summary>"Missing must stay missing": an adapter whose engine entries were all unreadable is not an idle adapter (it would earn on zero load).</summary>
public sealed class AggregateGpuUnreadableLoadTests
{
    private const uint Valid = Pdh.CStatusValidData;
    private const uint InvalidData = 0xC0000BBA;
    private const ulong Gpu0 = 0x166CA;
    private static readonly GpuAdapter Adapter = new(Gpu0, "NVIDIA GeForce RTX 5070", 12L << 30, 0x10DE, false);

    private static GpuAggregator AllEnginesInvalid(bool aggregate, bool withMemory = true)
    {
        var a = new GpuAggregator(trackProcesses: !aggregate, flagUnreadableEngines: aggregate);
        a.Begin();
        a.AddEngine(GpuNames.Engine(10, Gpu0, 0, 0, "3D"), 0, InvalidData);
        a.AddEngine(GpuNames.Engine(10, Gpu0, 0, 1, "Copy"), 0, InvalidData);
        if (withMemory)
        {
            a.AddAdapterMemory(GpuNames.Adapter(Gpu0), 2048, Valid, dedicated: true);
        }

        a.Finish();
        return a;
    }

    private static GpuInfo Describe(GpuAggregator data) =>
        Assert.Single(GpuSampler.Describe([Adapter], data, primed: true, null, ValueStatus.NotCollected));

    [Fact]
    public void In_aggregate_mode_all_invalid_engines_with_valid_memory_make_the_load_missing_not_zero()
    {
        var gpu = Describe(AllEnginesInvalid(aggregate: true));

        Assert.False(gpu.UtilizationPercent.IsOk);
        Assert.Equal(ValueStatus.Error, gpu.UtilizationPercent.Status);
        Assert.NotNull(gpu.UtilizationPercent.Detail);
        Assert.Empty(gpu.Engines);
        Assert.Equal(2048, gpu.DedicatedUsedBytes.Data);
    }

    [Fact]
    public void In_aggregate_mode_an_adapter_with_only_invalid_engines_is_still_listed_with_a_missing_load()
    {
        var gpu = Describe(AllEnginesInvalid(aggregate: true, withMemory: false));

        Assert.Equal(ValueStatus.Error, gpu.UtilizationPercent.Status);
    }

    [Fact]
    public void One_usable_engine_is_enough_for_a_load()
    {
        var a = new GpuAggregator(trackProcesses: false, flagUnreadableEngines: true);
        a.Begin();
        a.AddEngine(GpuNames.Engine(10, Gpu0, 0, 0, "3D"), 0, InvalidData);
        a.AddEngine(GpuNames.Engine(10, Gpu0, 0, 1, "Copy"), 7, Valid);
        a.AddAdapterMemory(GpuNames.Adapter(Gpu0), 2048, Valid, dedicated: true);
        a.Finish();

        var gpu = Describe(a);

        Assert.True(gpu.UtilizationPercent.IsOk);
        Assert.Equal(7.0, gpu.UtilizationPercent.Data);
    }

    [Fact]
    public void A_valid_adapter_with_no_engine_entries_at_all_is_still_idle_in_aggregate_mode()
    {
        var a = new GpuAggregator(trackProcesses: false, flagUnreadableEngines: true);
        a.Begin();
        a.AddAdapterMemory(GpuNames.Adapter(Gpu0), 2048, Valid, dedicated: true);
        a.Finish();

        var gpu = Describe(a);

        Assert.True(gpu.UtilizationPercent.IsOk);
        Assert.Equal(0.0, gpu.UtilizationPercent.Data);
    }

    [Fact]
    public void The_default_mode_output_is_unchanged_by_the_unreadable_tracking()
    {
        var gpu = Describe(AllEnginesInvalid(aggregate: false));

        Assert.True(gpu.UtilizationPercent.IsOk);
        Assert.Equal(0.0, gpu.UtilizationPercent.Data);
        Assert.Empty(GpuSampler.Describe([Adapter], AllEnginesInvalid(aggregate: false, withMemory: false), primed: true, null, ValueStatus.NotCollected));
    }
}

public sealed class DeferredReleaseTests
{
    [Fact]
    public void A_request_with_nobody_inside_releases_at_once_and_only_once()
    {
        var released = 0;
        var life = new DeferredRelease(() => released++);

        life.Request();
        life.Request();

        Assert.Equal(1, released);
        Assert.False(life.TryEnter());
    }

    [Fact]
    public void A_request_while_somebody_is_inside_waits_for_the_last_one_to_leave()
    {
        var released = 0;
        var life = new DeferredRelease(() => released++);
        Assert.True(life.TryEnter());
        Assert.True(life.TryEnter());

        life.Request();
        Assert.Equal(0, released);
        Assert.False(life.TryEnter());

        life.Exit();
        Assert.Equal(0, released);
        life.Exit();
        Assert.Equal(1, released);
        life.Request();
        Assert.Equal(1, released);
    }

    [Fact]
    public void Without_a_request_nothing_is_ever_released()
    {
        var released = 0;
        var life = new DeferredRelease(() => released++);

        Assert.True(life.TryEnter());
        life.Exit();

        Assert.Equal(0, released);
        Assert.False(life.IsRequested);
    }
}

/// <summary>The source's stage handling, with fake PDH and GPU samplers (the CPU, memory and network readings are the real ones; they are quick).</summary>
public sealed class AggregateSnapshotSourceStageTests
{
    private static readonly PdhSystemReading PdhOk = new([], Value.Ok(3000.0, "fake"), Value.Ok(0, "fake"));

    private static AggregateSnapshotSource Make(IGpuSampling gpu, Func<IPdhSampling> pdh, Func<long>? now = null, double timeoutSeconds = 5) =>
        new(static () => DateTime.UtcNow, null, pdh, gpu, now ?? (static () => 0L), TimeSpan.FromSeconds(timeoutSeconds));

    [Fact]
    public async Task Dispose_while_a_stage_is_running_frees_nothing_until_that_stage_and_the_sample_have_finished()
    {
        var hold = new ManualResetEventSlim();
        var pdh = new FakePdh { Hold = hold };
        var gpu = new FakeGpu();
        var source = Make(gpu, () => pdh, timeoutSeconds: 20);

        var sample = source.SampleAsync();
        Assert.True(pdh.Entered.Wait(TimeSpan.FromSeconds(10)));
        source.Dispose();

        Assert.Equal(0, pdh.Disposed);
        Assert.Equal(0, gpu.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.SampleAsync());

        hold.Set();
        _ = await sample;

        Assert.Equal(1, pdh.Disposed);
        Assert.Equal(1, gpu.Disposed);
        source.Dispose();
        Assert.Equal(1, pdh.Disposed);
        Assert.Equal(1, gpu.Disposed);
    }

    [Fact]
    public void Dispose_with_nothing_running_frees_the_samplers_at_once()
    {
        var gpu = new FakeGpu();
        var source = Make(gpu, () => new FakePdh());

        source.Dispose();

        Assert.Equal(1, gpu.Disposed);
    }

    [Fact]
    public async Task A_gpu_call_that_timed_out_has_its_baseline_thrown_away_before_the_next_gpu_sample()
    {
        var hold = new ManualResetEventSlim();
        var gpu = new FakeGpu { HoldFirstCall = hold };
        using var source = Make(gpu, () => new FakePdh(), timeoutSeconds: 0.5);

        var first = await source.SampleAsync();
        Assert.Equal(ValueStatus.Timeout, first.System.GpusState.Status);

        hold.Set();
        Assert.True(SpinWait.SpinUntil(() => gpu.Log.Contains("end 1"), TimeSpan.FromSeconds(10)));
        for (var i = 0; i < 100 && !gpu.Log.Contains("start 2"); i++)
        {
            _ = await source.SampleAsync();
            await Task.Delay(20);
        }

        Assert.Equal(["start 1", "end 1", "reset", "start 2"], gpu.Log.Take(4));
    }

    [Fact]
    public async Task A_failed_pdh_open_is_closed_and_tried_again_after_the_backoff_not_given_up_for_good()
    {
        var now = 1_000_000L;
        var attempts = 0;
        var made = new List<FakePdh>();
        IPdhSampling Open()
        {
            attempts++;
            if (attempts == 2)
            {
                throw new InvalidOperationException("the constructor threw");
            }

            var sampler = new FakePdh(open: attempts != 1);
            made.Add(sampler);
            return sampler;
        }

        using var source = Make(new FakeGpu(), Open, () => Interlocked.Read(ref now));

        var first = await source.SampleAsync();
        Assert.Equal(1, attempts);
        Assert.False(first.System.DisksState.IsOk);
        Assert.Equal(1, made[0].Disposed);

        now += 5_000;
        Assert.False((await source.SampleAsync()).System.DisksState.IsOk);
        Assert.Equal(1, attempts);

        now += AggregateSnapshotSource.PdhRetryAfterMs;
        Assert.False((await source.SampleAsync()).System.DisksState.IsOk);
        Assert.Equal(2, attempts);

        now += 5_000;
        Assert.False((await source.SampleAsync()).System.DisksState.IsOk);
        Assert.Equal(2, attempts);

        now += AggregateSnapshotSource.PdhRetryAfterMs;
        var recovered = await source.SampleAsync();
        Assert.Equal(3, attempts);
        Assert.True(recovered.System.DisksState.IsOk, recovered.System.DisksState.Detail);
        Assert.Equal(0, made[1].Disposed);
    }

    [Fact]
    public async Task Every_gpu_result_has_its_sensor_fields_not_collected_even_when_the_sampler_stamped_them_as_failed()
    {
        var known = new GpuInfo("GPU-1", "Test GPU", Value.Ok(10.0, "fake"), [], Value.Ok(1L, "fake"), Value.Ok(2L, "fake"), Value.Ok(3L, "fake"),
            Value.NotCollected<double>("SensorHost"), Value.NotCollected<double>("SensorHost"), Value.NotCollected<double>("SensorHost"), Value.NotCollected<double>("SensorHost"));
        var stamped = GpuSampler.TimedOut([known], ValueStatus.Error, "PdhCollectQueryData 0x800007D1", 1000);
        Assert.Equal(ValueStatus.Error, stamped[0].TemperatureC.Status);
        var gpu = new FakeGpu { Result = new GpuReading(stamped, new Dictionary<uint, double>(), false, "d", Value.Ok(1, "fake")) };
        using var source = Make(gpu, () => new FakePdh());

        var g = Assert.Single((await source.SampleAsync()).System.Gpus);

        Assert.Equal(ValueStatus.NotCollected, g.TemperatureC.Status);
        Assert.Equal(ValueStatus.NotCollected, g.PowerWatts.Status);
        Assert.Equal(ValueStatus.NotCollected, g.FanPercent.Status);
        Assert.Equal(ValueStatus.NotCollected, g.ClockMhz.Status);
        Assert.Equal(ValueStatus.NotCollected, g.TemperatureLimitC.Status);
        Assert.Equal(ValueStatus.NotCollected, g.LimitReasons.Status);
        Assert.Null(g.VendorOrigin);

        // What the sampler said about the counters themselves is not touched.
        Assert.Equal(ValueStatus.Error, g.UtilizationPercent.Status);
    }

    private sealed class FakePdh(bool open = true) : IPdhSampling
    {
        private int _disposed;

        public ManualResetEventSlim? Hold { get; init; }

        public ManualResetEventSlim Entered { get; } = new();

        public int Disposed => Volatile.Read(ref _disposed);

        public bool IsOpen => open;

        public void Reset()
        {
        }

        public PdhSystemReading Sample()
        {
            Entered.Set();
            _ = Hold?.Wait(TimeSpan.FromSeconds(30));
            return PdhOk;
        }

        public void Dispose() => Interlocked.Increment(ref _disposed);
    }

    private sealed class FakeGpu : IGpuSampling
    {
        private int _calls;
        private int _disposed;

        public ManualResetEventSlim? HoldFirstCall { get; init; }

        public GpuReading? Result { get; init; }

        public ConcurrentQueue<string> Log { get; } = new();

        public int Disposed => Volatile.Read(ref _disposed);

        public void Reset() => Log.Enqueue("reset");

        public GpuReading Sample(bool active)
        {
            var n = Interlocked.Increment(ref _calls);
            Log.Enqueue($"start {n}");
            if (n == 1)
            {
                _ = HoldFirstCall?.Wait(TimeSpan.FromSeconds(30));
            }

            Log.Enqueue($"end {n}");
            return Result ?? new GpuReading([], new Dictionary<uint, double>(), true, null, Value.Ok(0, "fake"));
        }

        public void Dispose() => Interlocked.Increment(ref _disposed);
    }
}

public sealed class PdhDiskTickTests
{
    private static PdhSystemSampler.Reading Valid(double value) => new(true, value, Pdh.CStatusValidData);

    [Fact]
    public void A_disk_that_one_counter_array_leaves_out_has_that_reading_missing_not_the_last_ticks()
    {
        var disk = new PdhSystemSampler.DiskAccumulator { Seen = true, Idle = Valid(80), Read = Valid(1000), Write = Valid(2000), Latency = Valid(0.004) };

        // Next tick: the write array does not list this disk.
        disk.BeginTick();
        disk.Idle = Valid(70);
        disk.Read = Valid(1500);
        disk.Latency = Valid(0.005);
        disk.Seen = true;
        var info = PdhSystemSampler.Build("1 D:", disk, primed: true, model: null);

        Assert.Equal(1500, info.ReadBytesPerSec.Data);
        Assert.False(info.WriteBytesPerSec.IsOk);
    }
}

/// <summary>These open the real PDH, DXGI and network counters, but never look at a process.</summary>
[Trait("Category", "RealMachine")]
public sealed class AggregateSnapshotSourceRealTests(ITestOutputHelper output)
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

        // Prime the GPU counters until one adapter has a real engine reading; without one the assertions below would say nothing.
        IReadOnlyList<GpuInfo> gpus = [];
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(400);
            gpus = (await source.SampleAsync()).System.Gpus;
            if (gpus.Any(static g => g.UtilizationPercent.IsOk && g.Engines.Count > 0))
            {
                break;
            }
        }

        if (!gpus.Any(static g => g.UtilizationPercent.IsOk && g.Engines.Count > 0))
        {
            // xunit 2 cannot skip at run time and the repo has no other skip mechanism: say so in the output, and do not claim a pass for the GPU claims.
            output.WriteLine("SKIPPED the GPU assertions: this machine gave no primed GPU reading with a valid engine counter (no usable GPU Engine counters).");
            return;
        }

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
        GpuReading reading = null!;
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(400);
            reading = gpu.Sample(active: true);
            if (reading.Gpus.Any(static g => g.UtilizationPercent.IsOk && g.Engines.Count > 0))
            {
                break;
            }
        }

        if (!reading.Gpus.Any(static g => g.UtilizationPercent.IsOk && g.Engines.Count > 0))
        {
            output.WriteLine("SKIPPED: this machine gave no primed GPU reading with a valid engine counter, so no per-process cell could have been filled either way.");
            return;
        }

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
