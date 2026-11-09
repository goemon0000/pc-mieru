using System.Diagnostics;
using System.Runtime.InteropServices;
using PcMieru.Core.Collection;

namespace PcMieru.Core.Tests.Collection;

#pragma warning disable SYSLIB1054 // test-only P/Invoke; the test project does not enable unsafe code, which LibraryImport needs

/// <summary>These run against the real machine, but only ever look at (and only ever touch) the test's own process and its own children.</summary>
[Trait("Category", "RealMachine")]
public sealed class WindowsSnapshotSourceTests : IDisposable
{
    private readonly List<Process> _children = [];

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(nint handle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(nint handle);

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

    private static WindowsSnapshotSource NewSource() => new(static () => DateTime.UtcNow);

    [Fact]
    public async Task The_test_process_is_in_the_snapshot_with_sane_fields_and_its_real_start_time()
    {
        using var source = NewSource();
        var snap = await source.SampleAsync();
        using var me = Process.GetCurrentProcess();

        var mine = snap.Processes.Single(p => p.Key.Pid == (uint)me.Id);

        Assert.Equal(me.StartTime.ToFileTimeUtc() / 10_000, mine.Key.CreateTime / 10_000); // same millisecond
        Assert.StartsWith("testhost", mine.Name, StringComparison.OrdinalIgnoreCase);
        Assert.True(mine.ThreadCount > 1);
        Assert.True(mine.HandleCount > 10);
        Assert.True(mine.WorkingSetBytes > 5_000_000);
        Assert.InRange(mine.PrivateWorkingSetBytes, 1, mine.WorkingSetBytes);
        Assert.True(mine.CommitBytes > 5_000_000);
        Assert.Equal(Environment.ProcessId, (int)mine.Key.Pid);
        Assert.Equal(ProcessState.Running, mine.State.Data);
        Assert.NotNull(mine.ParentKey); // the test runner (dotnet / the host) is still alive
        Assert.Equal(snap.Processes.Count, snap.Processes.Select(p => p.Key).Distinct().Count());
        Assert.True(snap.Processes.Count > 20);
    }

    [Fact]
    public async Task The_cpu_temperature_is_unsupported_with_a_reason_never_a_number()
    {
        using var source = NewSource();
        var snap = await source.SampleAsync();

        Assert.Equal(ValueStatus.Unsupported, snap.System.Cpu.TemperatureC.Status);
        Assert.Contains("kernel driver", snap.System.Cpu.TemperatureC.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_sample_has_no_rates_and_the_second_has_them()
    {
        using var source = NewSource();
        var first = await source.SampleAsync();
        Assert.All(first.Processes, p => Assert.Equal(ValueStatus.NotCollected, p.CpuPercent.Status));
        Assert.Equal(ValueStatus.NotCollected, first.System.Cpu.TotalPercent.Status);
        Assert.Equal(Environment.ProcessorCount, first.System.Cpu.LogicalProcessors);
        Assert.Equal(first.System.Cpu.LogicalProcessors, first.System.Cpu.PerLogicalPercent.Count);
        Assert.Equal(Snapshot.CurrentSchemaVersion, first.SchemaVersion);
        Assert.Equal(DateTimeKind.Utc, first.TakenAtUtc.Kind);
        Assert.Equal(first.IntervalMs, (int)(first.TakenAtUtc - first.WindowStartUtc).TotalMilliseconds);

        // Memory is a level, not a rate: the first sample has it.
        Assert.True(first.System.Memory.TotalPhysicalBytes > 0);

        await Task.Delay(400);
        var second = await source.SampleAsync();

        Assert.InRange(second.IntervalMs, 300, 3000);
        Assert.True(second.System.Cpu.TotalPercent.IsOk);
        Assert.InRange(second.System.Cpu.TotalPercent.Data, 0, 100);
        Assert.All(second.System.Cpu.PerLogicalPercent, v => Assert.InRange(v.GetOr(0), 0, 100));
        var collected = second.Processes.Count(p => p.CpuPercent.IsOk);
        Assert.True(collected > second.Processes.Count / 2, $"only {collected} of {second.Processes.Count} processes have a CPU value");
        Assert.All(second.Processes.Where(p => p.CpuPercent.IsOk), p => Assert.InRange(p.CpuPercent.Data, 0, 100));
    }

    [Fact]
    public async Task ResetBaseline_makes_the_next_sample_look_like_a_first_sample_again()
    {
        using var source = NewSource();
        await source.SampleAsync();
        await Task.Delay(200);
        Assert.True((await source.SampleAsync()).System.Cpu.TotalPercent.IsOk);

        source.ResetBaseline();
        var afterReset = await source.SampleAsync();
        Assert.Equal(ValueStatus.NotCollected, afterReset.System.Cpu.TotalPercent.Status);
        Assert.All(afterReset.Processes, p => Assert.Equal(ValueStatus.NotCollected, p.CpuPercent.Status));

        await Task.Delay(200);
        Assert.True((await source.SampleAsync()).System.Cpu.TotalPercent.IsOk);
    }

    [Fact]
    public async Task A_child_process_is_found_with_its_parent_and_disappears_when_it_exits()
    {
        using var source = NewSource();
        var child = StartPing();
        await Task.Delay(300);

        var snap = await source.SampleAsync();
        var info = snap.Processes.Single(p => p.Key.Pid == (uint)child.Id);

        Assert.Equal("PING.EXE", info.Name, ignoreCase: true);
        Assert.Equal((uint)Environment.ProcessId, info.ParentPid);
        Assert.NotNull(info.ParentKey);
        Assert.Equal((uint)Environment.ProcessId, info.ParentKey!.Value.Pid);
        Assert.Equal(child.StartTime.ToFileTimeUtc() / 10_000, info.Key.CreateTime / 10_000);

        child.Kill(true);
        await child.WaitForExitAsync();
        var after = await source.SampleAsync();
        Assert.DoesNotContain(after.Processes, p => p.Key == info.Key);
    }

    [Fact]
    public async Task A_suspended_child_reads_as_Suspended_and_resumes_to_Running()
    {
        using var source = NewSource();
        var child = StartPing();
        await Task.Delay(500);
        Assert.Equal(ProcessState.Running, State(await source.SampleAsync(), child));

        Assert.Equal(0, NtSuspendProcess(child.Handle));
        try
        {
            // Thread wait reasons need a moment to settle after the suspend.
            var seen = ProcessState.Running;
            for (var i = 0; i < 20 && seen != ProcessState.Suspended; i++)
            {
                await Task.Delay(100);
                seen = State(await source.SampleAsync(), child);
            }

            Assert.Equal(ProcessState.Suspended, seen);
        }
        finally
        {
            NtResumeProcess(child.Handle);
        }

        var resumed = ProcessState.Suspended;
        for (var i = 0; i < 20 && resumed != ProcessState.Running; i++)
        {
            await Task.Delay(100);
            resumed = State(await source.SampleAsync(), child);
        }

        Assert.Equal(ProcessState.Running, resumed);
    }

    private static ProcessState State(Snapshot snapshot, Process child) =>
        snapshot.Processes.Single(p => p.Key.Pid == (uint)child.Id).State.Data;

    [Fact]
    public async Task A_busy_child_reads_about_one_core_and_an_idle_child_reads_about_zero()
    {
        using var source = NewSource();
        var idle = StartPing();
        var busy = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"$e=[Diagnostics.Stopwatch]::StartNew(); while($e.Elapsed.TotalSeconds -lt 20){}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        _children.Add(busy);
        await Task.Delay(2500); // let PowerShell start and settle into the loop

        await source.SampleAsync();
        await Task.Delay(1500);
        var snap = await source.SampleAsync();

        var expectedOneCore = 100.0 / Environment.ProcessorCount;
        var b = snap.Processes.Single(p => p.Key.Pid == (uint)busy.Id).CpuPercent;
        var i = snap.Processes.Single(p => p.Key.Pid == (uint)idle.Id).CpuPercent;
        Assert.True(b.IsOk);
        Assert.InRange(b.Data, expectedOneCore * 0.6, expectedOneCore * 1.4);
        Assert.True(i.IsOk);
        Assert.InRange(i.Data, 0, 1.0);
    }

    [Fact]
    public async Task Cancellation_is_honoured_before_any_work()
    {
        using var source = NewSource();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.SampleAsync(cts.Token));

        // A cancelled call must not wedge the source.
        var snap = await source.SampleAsync();
        Assert.NotEmpty(snap.Processes);
    }

    [Fact]
    public async Task Concurrent_callers_are_serialized_and_each_gets_a_snapshot()
    {
        using var source = NewSource();
        var snaps = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => source.SampleAsync()));
        Assert.All(snaps, s => Assert.NotEmpty(s.Processes));
        Assert.Equal(6, snaps.Select(s => s.TakenAtUtc).Distinct().Count());
    }

    [Fact]
    public async Task After_Dispose_sampling_throws_ObjectDisposed_and_Dispose_twice_is_fine()
    {
        var source = NewSource();
        await source.SampleAsync();
        source.Dispose();
        source.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.SampleAsync());
    }

    [Fact]
    public async Task The_injected_clock_stamps_the_snapshot()
    {
        var fixedTime = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        using var source = new WindowsSnapshotSource(() => fixedTime);
        var snap = await source.SampleAsync();
        Assert.Equal(fixedTime, snap.TakenAtUtc);
    }

    [Fact]
    public async Task Every_list_of_a_real_snapshot_says_whether_it_could_be_read_and_a_list_with_entries_is_ok()
    {
        using var source = new WindowsSnapshotSource(static () => DateTime.UtcNow, new SamplerSettings { GpuDefaultOn = true });
        _ = await source.SampleAsync();
        await Task.Delay(300);
        var system = (await source.SampleAsync()).System;

        (int Count, Value<int> State, string Name)[] lists =
        [
            (system.Disks.Count, system.DisksState, "disks"),
            (system.Nics.Count, system.NicsState, "nics"),
            (system.Gpus.Count, system.GpusState, "gpus"),
        ];
        Assert.All(lists, l =>
        {
            Assert.False(ListStatus.IsUnset(l.State), l.Name + " has no state");
            if (l.Count > 0)
            {
                Assert.True(l.State.IsOk, l.Name + ": " + ListStatus.Describe(l.State));
                Assert.Equal(l.Count, l.State.Data);
            }
        });
    }

    [Fact]
    public async Task Uptime_matches_the_tick_count_within_a_few_seconds()
    {
        using var source = NewSource();
        var snap = await source.SampleAsync();
        Assert.InRange(snap.System.UptimeSec, (Environment.TickCount64 / 1000) - 3, (Environment.TickCount64 / 1000) + 3);
    }
}
