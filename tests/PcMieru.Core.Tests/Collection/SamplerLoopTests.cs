using System.Diagnostics;
using PcMieru.Core.Collection;

namespace PcMieru.Core.Tests.Collection;

/// <summary>A source that records what the loop asks of it. Each snapshot's IntervalMs carries its sequence number (1, 2, 3...).</summary>
internal sealed class ScriptedSource : ISnapshotSource
{
    private readonly object _lock = new();
    private readonly List<string> _log = [];
    private readonly List<long> _starts = [];
    private int _calls;
    private int _running;
    private int _peak;
    private int _held;

    public TimeSpan Duration { get; set; }

    /// <summary>While set, a call blocks until it is completed or the call is cancelled (a sampler that is stuck).</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Calls that are blocked on <see cref="Hold"/> right now.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>Returns the exception to throw for the n-th call (1-based), or null.</summary>
    public Func<int, Exception?>? Fail { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public int PeakConcurrency => Volatile.Read(ref _peak);

    public int Resets
    {
        get
        {
            lock (_lock)
            {
                return _log.Count(l => l == "reset");
            }
        }
    }

    public string[] Log
    {
        get
        {
            lock (_lock)
            {
                return [.. _log];
            }
        }
    }

    /// <summary>Gaps between the starts of consecutive calls, in milliseconds.</summary>
    public double[] StartGapsMs
    {
        get
        {
            lock (_lock)
            {
                return [.. _starts.Zip(_starts.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a, b).TotalMilliseconds)];
            }
        }
    }

    public void ResetBaseline()
    {
        lock (_lock)
        {
            _log.Add("reset");
        }
    }

    public async Task<Snapshot> SampleAsync(CancellationToken ct = default)
    {
        int n;
        lock (_lock)
        {
            n = ++_calls;
            _log.Add("sample");
            _starts.Add(Stopwatch.GetTimestamp());
        }

        var now = Interlocked.Increment(ref _running);
        int seen;
        do
        {
            seen = Volatile.Read(ref _peak);
        }
        while (now > seen && Interlocked.CompareExchange(ref _peak, now, seen) != seen);

        try
        {
            if (Hold is { } hold)
            {
                _ = Interlocked.Increment(ref _held);
                try
                {
                    await hold.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    _ = Interlocked.Decrement(ref _held);
                }
            }

            if (Duration > TimeSpan.Zero)
            {
                await Task.Delay(Duration, ct).ConfigureAwait(false);
            }

            if (Fail?.Invoke(n) is { } error)
            {
                throw error;
            }

            return FakeSnapshots.Create(DateTime.UtcNow, tick: n, intervalMs: n);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _running);
        }
    }

    public void Dispose()
    {
    }
}

internal sealed class TestControl : ISamplerControl
{
    private long _interval = TimeSpan.FromMilliseconds(40).Ticks;
    private long _minimizedInterval = TimeSpan.FromMilliseconds(400).Ticks;
    private int _minimized;
    private int _paused;

    public TimeSpan Interval
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref _interval));
        set => Volatile.Write(ref _interval, value.Ticks);
    }

    public TimeSpan MinimizedInterval
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref _minimizedInterval));
        set => Volatile.Write(ref _minimizedInterval, value.Ticks);
    }

    public bool Minimized
    {
        get => Volatile.Read(ref _minimized) != 0;
        set => Volatile.Write(ref _minimized, value ? 1 : 0);
    }

    public bool Paused
    {
        get => Volatile.Read(ref _paused) != 0;
        set => Volatile.Write(ref _paused, value ? 1 : 0);
    }

    public TimeSpan EffectiveInterval => Minimized ? MinimizedInterval : Interval;

    public bool GpuActive => false;

    public IDisposable SubscribeGpu() => throw new NotSupportedException();
}

public sealed class HistoryRingTests
{
    [Fact]
    public void It_keeps_the_newest_entries_oldest_first_once_it_wraps()
    {
        var ring = new HistoryRing<int>(4);
        for (var i = 1; i <= 3; i++)
        {
            ring.Add(i);
        }

        Assert.Equal([1, 2, 3], ring.ToArray());

        for (var i = 4; i <= 10; i++)
        {
            ring.Add(i);
        }

        Assert.Equal(4, ring.Count);
        Assert.Equal([7, 8, 9, 10], ring.ToArray());
    }

    [Fact]
    public void A_copy_taken_earlier_does_not_change_when_more_arrive()
    {
        var ring = new HistoryRing<int>(3);
        ring.Add(1);
        var copy = ring.ToArray();

        ring.Add(2);

        Assert.Equal([1], copy);
    }

    [Fact]
    public void Clear_empties_it_and_it_can_be_filled_again()
    {
        var ring = new HistoryRing<string>(2);
        ring.Add("a");
        ring.Add("b");
        ring.Add("c");

        ring.Clear();
        Assert.Empty(ring.ToArray());
        ring.Add("d");

        Assert.Equal(["d"], ring.ToArray());
    }

    [Fact]
    public void A_capacity_below_one_is_refused()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryRing<int>(0));
    }

    [Fact]
    public async Task Writers_on_several_threads_never_lose_the_cap_or_corrupt_it()
    {
        var ring = new HistoryRing<int>(50);
        var writers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 5000; i++)
            {
                ring.Add((t * 100_000) + i);
                if (i % 500 == 0)
                {
                    Assert.True(ring.ToArray().Length <= 50);
                }
            }
        }));

        await Task.WhenAll(writers);

        Assert.Equal(50, ring.Count);
        Assert.Equal(50, ring.ToArray().Distinct().Count());
    }
}

public sealed class SamplerLoopTests
{
    private static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5);
        }

        return condition();
    }

    [Fact]
    public async Task It_resets_the_baseline_before_the_first_sample_and_then_samples_on_a_beat_never_faster_than_the_interval()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromMilliseconds(60) };
        using var loop = new SamplerLoop(source, control);

        loop.Start();
        loop.Start(); // a second Start must not run a second loop
        Assert.True(await UntilAsync(() => source.Calls >= 8));
        await loop.StopAsync();

        Assert.Equal("reset", source.Log[0]);
        Assert.Equal("sample", source.Log[1]);
        Assert.Equal(1, source.Resets);
        Assert.Equal(1, source.PeakConcurrency);
        Assert.All(source.StartGapsMs, gap => Assert.True(gap >= 45, $"a sample started {gap:F1} ms after the previous one with a 60 ms interval"));
        Assert.False(loop.Running);
    }

    [Fact]
    public async Task The_next_sample_is_one_interval_after_the_start_of_the_last_not_after_its_end()
    {
        var source = new ScriptedSource { Duration = TimeSpan.FromMilliseconds(80) };
        var control = new TestControl { Interval = TimeSpan.FromMilliseconds(200) };
        using var loop = new SamplerLoop(source, control);

        loop.Start();
        Assert.True(await UntilAsync(() => source.Calls >= 4));
        await loop.StopAsync();

        // A beat of 200 ms with 80 ms of work is a 200 ms gap, not a 280 ms one.
        Assert.All(source.StartGapsMs.Take(2), gap => Assert.InRange(gap, 180, 260));
    }

    [Fact]
    public async Task A_sample_that_overruns_the_beat_is_not_followed_by_another_at_once_and_never_overlaps()
    {
        var source = new ScriptedSource { Duration = TimeSpan.FromMilliseconds(120) };
        var control = new TestControl { Interval = TimeSpan.FromMilliseconds(40) };
        using var loop = new SamplerLoop(source, control);

        loop.Start();
        Assert.True(await UntilAsync(() => source.Calls >= 4));
        await loop.StopAsync();

        Assert.Equal(1, source.PeakConcurrency);
        // 120 ms of work plus a quarter interval (10 ms) of rest.
        Assert.All(source.StartGapsMs, gap => Assert.True(gap >= 125, $"gap {gap:F1} ms: the loop did not rest after an overrun"));
    }

    [Fact]
    public async Task Pausing_stops_sampling_and_resuming_drops_the_baseline_first()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromMilliseconds(30) };
        using var loop = new SamplerLoop(source, control);
        loop.Start();
        Assert.True(await UntilAsync(() => source.Calls >= 3));

        control.Paused = true;
        await Task.Delay(150); // let a sample that was already running finish
        var atPause = source.Calls;
        await Task.Delay(500);
        Assert.Equal(atPause, source.Calls);
        Assert.Equal(1, source.Resets);

        control.Paused = false;
        Assert.True(await UntilAsync(() => source.Calls > atPause));
        await loop.StopAsync();

        Assert.Equal(2, source.Resets);
        var log = source.Log;
        var resume = Array.LastIndexOf(log, "reset");
        Assert.Equal("sample", log[resume + 1]); // the first sample after the pause is the one without a baseline
        Assert.Equal(atPause, log[..resume].Count(l => l == "sample"));
    }

    [Fact]
    public async Task A_refresh_request_is_ignored_while_paused()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromSeconds(30) };
        using var loop = new SamplerLoop(source, control);
        loop.Start();
        Assert.True(await UntilAsync(() => source.Calls >= 1));

        control.Paused = true;
        await Task.Delay(100);
        loop.RequestSampleNow();
        await Task.Delay(600);

        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task A_refresh_request_samples_now_instead_of_waiting_for_the_beat()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromSeconds(30) };
        using var loop = new SamplerLoop(source, control);
        loop.Start();
        Assert.True(await UntilAsync(() => source.Calls >= 1));

        loop.RequestSampleNow();

        Assert.True(await UntilAsync(() => source.Calls >= 2, 3000));
        await Task.Delay(300);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task A_burst_of_refresh_requests_during_a_sample_costs_one_more_sample_not_one_each()
    {
        var source = new ScriptedSource { Duration = TimeSpan.FromMilliseconds(200) };
        var control = new TestControl { Interval = TimeSpan.FromSeconds(30) };
        using var loop = new SamplerLoop(source, control);
        loop.Start();
        Assert.True(await UntilAsync(() => source.Calls >= 1)); // the first sample is now running for 200 ms

        for (var i = 0; i < 5; i++)
        {
            loop.RequestSampleNow();
        }

        await Task.Delay(1200);

        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Minimizing_slows_the_beat_within_a_slice_and_restoring_speeds_it_up()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromMilliseconds(40), MinimizedInterval = TimeSpan.FromMilliseconds(500) };
        using var loop = new SamplerLoop(source, control);
        loop.Start();
        Assert.True(await UntilAsync(() => source.Calls >= 3));

        control.Minimized = true;
        await Task.Delay(100); // a sample or two may still be due from the old beat
        var before = source.Calls;
        await Task.Delay(1000);
        var slow = source.Calls - before;

        control.Minimized = false;
        var fastFrom = source.Calls;
        await Task.Delay(800); // the loop re-reads the setting at least every 250 ms, so the speed-up starts within that
        var fast = source.Calls - fastFrom;
        await loop.StopAsync();

        Assert.InRange(slow, 1, 3); // 500 ms beat over one second
        Assert.True(fast >= 8, $"only {fast} samples in 0.8 s after restoring a 40 ms interval");
    }

    [Fact]
    public async Task History_holds_the_last_sixty_snapshots_oldest_first_and_Latest_is_the_newest()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromMilliseconds(1) };
        using var loop = new SamplerLoop(source, control);
        loop.Start();
        Assert.True(await UntilAsync(() => source.Calls >= 75));
        await loop.StopAsync();

        var history = loop.History;
        Assert.Equal(SamplerLoop.HistoryLength, history.Count);
        var sequence = history.Select(s => s.IntervalMs).ToArray();
        Assert.Equal(sequence.OrderBy(n => n), sequence);
        Assert.Equal(Enumerable.Range(sequence[0], 60), sequence); // consecutive: nothing dropped from the middle
        Assert.Same(history[^1], loop.Latest);
        Assert.True(sequence[0] > 1, "the first samples should have been forgotten");
    }

    [Fact]
    public async Task A_failed_sample_is_reported_and_the_loop_carries_on_without_it_in_the_history()
    {
        var source = new ScriptedSource { Fail = n => n == 2 ? new InvalidOperationException("counter exploded") : null };
        var control = new TestControl { Interval = TimeSpan.FromMilliseconds(20) };
        using var loop = new SamplerLoop(source, control);
        var failures = new List<Exception>();
        loop.Failed += ex =>
        {
            lock (failures)
            {
                failures.Add(ex);
            }
        };

        loop.Start();
        Assert.True(await UntilAsync(() => source.Calls >= 5));
        await loop.StopAsync();

        lock (failures)
        {
            var failure = Assert.Single(failures);
            Assert.Equal("counter exploded", failure.Message);
        }

        var sequence = loop.History.Select(s => s.IntervalMs).ToArray();
        Assert.DoesNotContain(2, sequence);
        Assert.Contains(1, sequence);
        Assert.Contains(3, sequence);
    }

    [Fact]
    public async Task A_listener_that_throws_does_not_stop_the_loop_or_the_other_listeners()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromMilliseconds(20) };
        using var loop = new SamplerLoop(source, control);
        var good = 0;
        loop.Sampled += _ => throw new InvalidOperationException("listener bug");
        loop.Sampled += _ => Interlocked.Increment(ref good);

        loop.Start();
        Assert.True(await UntilAsync(() => Volatile.Read(ref good) >= 4));
        await loop.StopAsync();

        Assert.True(source.Calls >= 4);
        Assert.Equal(Volatile.Read(ref good), loop.History.Count);
    }

    [Fact]
    public async Task Stopping_cancels_a_sample_that_is_in_flight_and_ends_the_loop()
    {
        var source = new ScriptedSource { Duration = TimeSpan.FromSeconds(30) };
        var control = new TestControl();
        using var loop = new SamplerLoop(source, control);
        loop.Start();
        Assert.True(await UntilAsync(() => source.Calls >= 1));
        Assert.True(loop.Running);

        var clock = Stopwatch.StartNew();
        await loop.StopAsync();

        Assert.True(clock.ElapsedMilliseconds < 3000, "StopAsync waited for the sample instead of cancelling it");
        Assert.False(loop.Running);
        Assert.Empty(loop.History);
    }

    [Fact]
    public async Task Dispose_returns_promptly_is_repeatable_and_a_disposed_loop_refuses_to_start()
    {
        var source = new ScriptedSource { Duration = TimeSpan.FromSeconds(30) };
        var loop = new SamplerLoop(source, new TestControl());
        loop.Start();
        Assert.True(await UntilAsync(() => source.Calls >= 1));

        var clock = Stopwatch.StartNew();
        loop.Dispose();
        loop.Dispose();

        Assert.True(clock.ElapsedMilliseconds < 4000);
        _ = Assert.Throws<ObjectDisposedException>(loop.Start);
        loop.RequestSampleNow(); // must not throw after Dispose
    }

    [Fact]
    public void A_loop_that_was_never_started_has_nothing_and_is_not_running()
    {
        using var loop = new SamplerLoop(new ScriptedSource(), new TestControl());

        Assert.False(loop.Running);
        Assert.Null(loop.Latest);
        Assert.Empty(loop.History);
    }

    [Trait("Category", "RealMachine")]
    [Fact]
    public async Task The_real_source_drives_the_loop_and_the_second_snapshot_has_rates()
    {
        using var source = new WindowsSnapshotSource(static () => DateTime.UtcNow, new SamplerSettings { GpuDefaultOn = false });
        var control = new TestControl { Interval = TimeSpan.FromMilliseconds(300) };
        using var loop = new SamplerLoop(source, control);

        loop.Start();
        Assert.True(await UntilAsync(() => loop.History.Count >= 3, 30_000));
        await loop.StopAsync();

        var history = loop.History;
        Assert.Equal(ValueStatus.NotCollected, history[0].System.Cpu.TotalPercent.Status);
        Assert.True(history[1].System.Cpu.TotalPercent.IsOk, history[1].System.Cpu.TotalPercent.Detail);
        Assert.True(history[2].TakenAtUtc > history[1].TakenAtUtc);
    }
}
