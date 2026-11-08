using PcMieru.Core.Collection;

namespace PcMieru.Core.Tests.Collection;

public class ProcessDeltaEngineTests
{
    private const long Created = 133_000_000_000_000_000;

    private static RawProcess Raw(uint pid, long cpu = 0, ulong read = 0, ulong write = 0, long created = Created) =>
        new(pid, 4, created, cpu, 0, 1, 1, 8, 1, 0, 0, 0, read, write, "p.exe", false, 1);

    private static ProcessRates[] Run(ProcessDeltaEngine engine, double elapsed, int cpus, params RawProcess[] processes)
    {
        var rates = new ProcessRates[processes.Length];
        engine.Compute(processes, elapsed, cpus, rates);
        return rates;
    }

    [Fact]
    public void The_first_sample_has_no_baseline_so_nothing_is_a_number()
    {
        var engine = new ProcessDeltaEngine();
        var first = Run(engine, 0, 8, Raw(10, cpu: 5_000_000))[0];
        Assert.Equal(ValueStatus.NotCollected, first.Cpu.Status);
        Assert.Equal(ValueStatus.NotCollected, first.ReadPerSec.Status);
        Assert.Equal(ValueStatus.NotCollected, first.WritePerSec.Status);
        Assert.False(first.Cpu.IsOk);
        Assert.NotNull(first.Cpu.Detail);
    }

    [Fact]
    public void Cpu_percent_is_delta_over_elapsed_over_logical_processors()
    {
        var engine = new ProcessDeltaEngine();
        Run(engine, 0, 4, Raw(10, cpu: 1_000_000));

        // 2 s elapsed, 4 CPUs, the process used exactly 1 CPU-second = 12.5 % of the machine.
        var r = Run(engine, 2.0, 4, Raw(10, cpu: 1_000_000 + 10_000_000))[0];

        Assert.True(r.Cpu.TryGet(out var cpu));
        Assert.Equal(12.5, cpu, 6);
    }

    [Fact]
    public void Io_rates_are_bytes_per_elapsed_second()
    {
        var engine = new ProcessDeltaEngine();
        Run(engine, 0, 4, Raw(10, read: 100, write: 1000));
        var r = Run(engine, 0.5, 4, Raw(10, read: 100 + 50, write: 1000 + 2000))[0];
        Assert.Equal(100.0, r.ReadPerSec.Data, 6);
        Assert.Equal(4000.0, r.WritePerSec.Data, 6);
        Assert.Equal(ValueStatus.Ok, r.ReadPerSec.Status);
    }

    [Fact]
    public void An_idle_process_is_zero_percent_not_missing()
    {
        var engine = new ProcessDeltaEngine();
        Run(engine, 0, 4, Raw(10, cpu: 777));
        var r = Run(engine, 1.0, 4, Raw(10, cpu: 777))[0];
        Assert.Equal(ValueStatus.Ok, r.Cpu.Status);
        Assert.Equal(0.0, r.Cpu.Data);
    }

    [Fact]
    public void Timer_jitter_cannot_push_a_process_past_100_percent()
    {
        var engine = new ProcessDeltaEngine();
        Run(engine, 0, 2, Raw(10, cpu: 0));
        var r = Run(engine, 1.0, 2, Raw(10, cpu: 25_000_000))[0]; // 2.5 CPU-seconds in 1 s on 2 CPUs
        Assert.Equal(100.0, r.Cpu.Data);
    }

    [Fact]
    public void A_reused_pid_is_a_new_process_and_never_inherits_the_old_baseline()
    {
        var engine = new ProcessDeltaEngine();
        Run(engine, 0, 4, Raw(10, cpu: 900_000_000));

        // Same PID, different creation time, and a small counter: without the key this would look like "went backwards".
        var r = Run(engine, 1.0, 4, Raw(10, cpu: 1_000, created: Created + 12345))[0];

        Assert.Equal(ValueStatus.NotCollected, r.Cpu.Status);
        Assert.Equal(ProcessDeltaEngine.DetailNewProcess, r.Cpu.Detail);
    }

    [Fact]
    public void A_process_new_since_the_last_tick_is_not_collected_for_one_tick_then_collected()
    {
        var engine = new ProcessDeltaEngine();
        Run(engine, 0, 4, Raw(10, cpu: 0));
        var second = Run(engine, 1.0, 4, Raw(10, cpu: 0), Raw(11, cpu: 5_000_000));
        Assert.Equal(ValueStatus.Ok, second[0].Cpu.Status);
        Assert.Equal(ValueStatus.NotCollected, second[1].Cpu.Status);

        var third = Run(engine, 1.0, 4, Raw(10, cpu: 0), Raw(11, cpu: 15_000_000));
        Assert.Equal(ValueStatus.Ok, third[1].Cpu.Status);
        Assert.Equal(25.0, third[1].Cpu.Data, 6);
    }

    [Fact]
    public void A_counter_that_went_backwards_is_not_collected_not_negative_and_not_zero()
    {
        var engine = new ProcessDeltaEngine();
        Run(engine, 0, 4, Raw(10, cpu: 5_000_000, read: 1000, write: 1000));
        var r = Run(engine, 1.0, 4, Raw(10, cpu: 4_000_000, read: 10, write: 2000))[0];

        Assert.Equal(ValueStatus.NotCollected, r.Cpu.Status);
        Assert.Equal(ProcessDeltaEngine.DetailBackwards, r.Cpu.Detail);
        Assert.Equal(ValueStatus.NotCollected, r.ReadPerSec.Status);
        Assert.Equal(ValueStatus.Ok, r.WritePerSec.Status); // only the counter that regressed is dropped
    }

    [Fact]
    public void Reset_drops_the_baseline_like_the_first_sample()
    {
        var engine = new ProcessDeltaEngine();
        Run(engine, 0, 4, Raw(10, cpu: 0));
        Run(engine, 1.0, 4, Raw(10, cpu: 1));
        Assert.True(engine.HasBaseline);

        engine.Reset();

        Assert.False(engine.HasBaseline);
        var r = Run(engine, 1.0, 4, Raw(10, cpu: 2))[0];
        Assert.Equal(ValueStatus.NotCollected, r.Cpu.Status);
        Assert.Equal(ProcessDeltaEngine.DetailNoBaseline, r.Cpu.Detail);
        Assert.Equal(ValueStatus.Ok, Run(engine, 1.0, 4, Raw(10, cpu: 3))[0].Cpu.Status);
    }

    [Fact]
    public void A_process_that_disappears_and_returns_with_the_same_key_starts_over()
    {
        // The baseline only holds the previous tick, so a gap (e.g. the process was invisible for a tick) is not bridged.
        var engine = new ProcessDeltaEngine();
        Run(engine, 0, 4, Raw(10, cpu: 0));
        Run(engine, 1.0, 4, Raw(99, cpu: 0));
        var r = Run(engine, 1.0, 4, Raw(10, cpu: 1_000_000))[0];
        Assert.Equal(ValueStatus.NotCollected, r.Cpu.Status);
    }

    [Fact]
    public void Zero_elapsed_time_is_not_collected_instead_of_a_division_by_zero()
    {
        var engine = new ProcessDeltaEngine();
        Run(engine, 0, 4, Raw(10, cpu: 0));
        var r = Run(engine, 0, 4, Raw(10, cpu: 5))[0];
        Assert.Equal(ValueStatus.NotCollected, r.Cpu.Status);
        Assert.Equal(ProcessDeltaEngine.DetailNoElapsed, r.Cpu.Detail);
    }

    [Fact]
    public void The_baseline_does_not_grow_with_processes_that_came_and_went()
    {
        var engine = new ProcessDeltaEngine();
        Run(engine, 0, 4, Raw(1));
        for (uint pid = 100; pid < 5000; pid++)
        {
            Run(engine, 1.0, 4, Raw(1), Raw(pid));
        }

        // Observable consequence: pid 100 (long gone) is "new" again if it reappears.
        var r = Run(engine, 1.0, 4, Raw(100))[0];
        Assert.Equal(ValueStatus.NotCollected, r.Cpu.Status);
    }
}

public class SystemCpuSamplerTests
{
    private static ProcessorTimes Cpu(long idle, long kernelIncludingIdle, long user) =>
        new() { IdleTime = idle, KernelTime = kernelIncludingIdle, UserTime = user };

    [Fact]
    public void The_first_reading_is_not_collected_everywhere()
    {
        var r = SystemCpuSampler.Compute([], [Cpu(0, 0, 0), Cpu(0, 0, 0)], 2, hasBaseline: false);
        Assert.Equal(2, r.PerLogical.Count);
        Assert.All(r.PerLogical, v => Assert.Equal(ValueStatus.NotCollected, v.Status));
        Assert.Equal(ValueStatus.NotCollected, r.Total.Status);
    }

    [Fact]
    public void Kernel_time_includes_idle_so_busy_is_user_plus_kernel_minus_idle()
    {
        // CPU 0: over the window 100 units elapsed: idle 60, kernel(incl. idle) 70, user 30 => busy 40 => 40 %.
        // CPU 1: idle 100, kernel(incl. idle) 100, user 0 => 0 %.
        var before = new[] { Cpu(1000, 1100, 500), Cpu(1000, 1100, 500) };
        var now = new[] { Cpu(1060, 1170, 530), Cpu(1100, 1200, 500) };

        var r = SystemCpuSampler.Compute(before, now, 2, hasBaseline: true);

        Assert.Equal(40.0, r.PerLogical[0].Data, 6);
        Assert.Equal(0.0, r.PerLogical[1].Data, 6);
        Assert.Equal(20.0, r.Total.Data, 6); // (40 + 0) / (100 + 100)
        Assert.Null(r.Total.Detail);
    }

    [Fact]
    public void Total_is_weighted_by_elapsed_cpu_time_not_a_plain_mean()
    {
        var before = new[] { Cpu(0, 0, 0), Cpu(0, 0, 0) };
        var now = new[] { Cpu(0, 100, 0), Cpu(300, 300, 0) }; // CPU 0 fully busy for 100, CPU 1 fully idle for 300
        var r = SystemCpuSampler.Compute(before, now, 2, hasBaseline: true);
        Assert.Equal(100.0, r.PerLogical[0].Data, 6);
        Assert.Equal(0.0, r.PerLogical[1].Data, 6);
        Assert.Equal(25.0, r.Total.Data, 6);
    }

    [Fact]
    public void A_processor_the_system_did_not_report_is_marked_and_the_total_says_how_much_it_covers()
    {
        var before = new[] { Cpu(0, 0, 0) };
        var now = new[] { Cpu(50, 100, 0) };

        var r = SystemCpuSampler.Compute(before, now, 3, hasBaseline: true);

        Assert.Equal(3, r.PerLogical.Count);
        Assert.Equal(50.0, r.PerLogical[0].Data, 6);
        Assert.Equal(ValueStatus.NotCollected, r.PerLogical[1].Status);
        Assert.Equal(ValueStatus.NotCollected, r.PerLogical[2].Status);
        Assert.Equal(ValueStatus.Ok, r.Total.Status);
        Assert.Contains("1 of 3", r.Total.Detail);
    }

    [Fact]
    public void A_counter_that_did_not_advance_or_went_backwards_is_not_collected_per_cpu()
    {
        var before = new[] { Cpu(100, 200, 100), Cpu(500, 600, 100), Cpu(10, 20, 10) };
        var now = new[] { Cpu(100, 200, 100), Cpu(400, 500, 100), Cpu(20, 40, 20) };

        var r = SystemCpuSampler.Compute(before, now, 3, hasBaseline: true);

        Assert.Equal(ValueStatus.NotCollected, r.PerLogical[0].Status); // no time passed
        Assert.Equal(ValueStatus.NotCollected, r.PerLogical[1].Status); // went backwards
        Assert.Equal(ValueStatus.Ok, r.PerLogical[2].Status);
    }

    [Fact]
    public void Idle_larger_than_total_is_rejected_instead_of_a_negative_percent()
    {
        var before = new[] { Cpu(0, 0, 0) };
        var now = new[] { Cpu(500, 100, 0) };
        var r = SystemCpuSampler.Compute(before, now, 1, hasBaseline: true);
        Assert.Equal(ValueStatus.NotCollected, r.PerLogical[0].Status);
        Assert.Equal(ValueStatus.NotCollected, r.Total.Status);
    }

    [Fact]
    public void The_entry_struct_is_the_48_bytes_the_kernel_writes()
    {
        Assert.Equal(48, System.Runtime.InteropServices.Marshal.SizeOf<ProcessorTimes>());
    }
}

public class MemorySamplerTests
{
    [Fact]
    public void Page_counts_are_multiplied_by_the_page_size_exactly_once()
    {
        var perf = new Win32.PerformanceInformation
        {
            PageSize = 4096,
            CommitTotal = 1000,
            CommitLimit = 2000,
            SystemCache = 300,
            KernelPaged = 40,
            KernelNonpaged = 5,
        };
        var status = new Win32.MemoryStatusEx { TotalPhys = 64UL << 30, AvailPhys = 25UL << 30 };

        var m = MemorySampler.Build(status, perf);

        Assert.Equal(64L << 30, m.TotalPhysicalBytes);
        Assert.Equal(25L << 30, m.AvailablePhysicalBytes);
        Assert.Equal(1000L * 4096, m.CommitTotalBytes.Data);
        Assert.Equal(2000L * 4096, m.CommitLimitBytes.Data);
        Assert.Equal(300L * 4096, m.CacheBytes.Data);
        Assert.Equal(40L * 4096, m.PagedPoolBytes.Data);
        Assert.Equal(5L * 4096, m.NonPagedPoolBytes.Data);
    }

    [Fact]
    public void A_failed_GetPerformanceInfo_is_an_error_value_and_not_zero_bytes()
    {
        var m = MemorySampler.Build(new Win32.MemoryStatusEx { TotalPhys = 100, AvailPhys = 50 }, null);
        Assert.Equal(100, m.TotalPhysicalBytes);
        Assert.Equal(ValueStatus.Error, m.CommitTotalBytes.Status);
        Assert.Equal(ValueStatus.Error, m.CacheBytes.Status);
    }

    [Fact]
    public void The_real_machine_reports_sane_memory()
    {
        var m = MemorySampler.Sample();
        Assert.True(m.TotalPhysicalBytes > 1L << 30);
        Assert.InRange(m.AvailablePhysicalBytes, 1, m.TotalPhysicalBytes);
        Assert.True(m.CommitTotalBytes.IsOk && m.CommitTotalBytes.Data > 0);
        Assert.True(m.CommitLimitBytes.Data >= m.CommitTotalBytes.Data / 2);
    }
}
