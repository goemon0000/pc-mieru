using System.Diagnostics;
using PcMieru.Core.Collection;

namespace PcMieru.Core.Tests.Collection;

/// <summary>
/// The recording lease of <see cref="SamplerLoop"/> (DESIGN-B 2 and 6). The loop is driven with a short recording period (20 ms unless a test says
/// otherwise) so the tests wait for CONDITIONS (a count reached, a state settled) with a generous timeout and never assert on how long something took.
/// </summary>
public sealed class RecordingLeaseTests
{
    private static readonly TimeSpan FastBeat = TimeSpan.FromMilliseconds(20);

    private static SamplerLoop NewLoop(ScriptedSource source, TestControl control, CountingCadence? cadence = null, int periodMs = 20, int queue = SamplerLoop.RecordingQueueLength) =>
        new(source, control, cadence is null ? null : () => cadence, TimeSpan.FromMilliseconds(periodMs), queue);

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

    /// <summary>Waits until the value stops changing (three looks in a row, 100 ms apart) and returns it. Only used to show that something STOPPED.</summary>
    private static async Task<int> SettledAsync(Func<int> value)
    {
        var last = value();
        var same = 0;
        var clock = Stopwatch.StartNew();
        while (same < 3)
        {
            Assert.True(clock.ElapsedMilliseconds < 15_000, "the count never settled: the loop is still sampling");
            await Task.Delay(100);
            var now = value();
            same = now == last ? same + 1 : 0;
            last = now;
        }

        return last;
    }

    private sealed class Recorder
    {
        private readonly object _lock = new();
        private readonly List<RecordingSample> _items = [];

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _items.Count;
                }
            }
        }

        public RecordingSample[] Items
        {
            get
            {
                lock (_lock)
                {
                    return [.. _items];
                }
            }
        }

        public void Add(RecordingSample sample)
        {
            lock (_lock)
            {
                _items.Add(sample);
            }
        }
    }

    [Fact]
    public void Two_holders_keep_the_loop_recording_until_the_last_one_lets_go()
    {
        var control = new TestControl();
        var cadence = new CountingCadence();
        using var loop = NewLoop(new ScriptedSource(), control, cadence);
        Assert.False(loop.IsRecording);

        var a = loop.AcquireRecordingLease();
        var b = loop.AcquireRecordingLease();
        Assert.True(loop.IsRecording);
        Assert.Equal(2, loop.LeaseCount);

        a.Dispose();
        Assert.True(loop.IsRecording);
        Assert.Equal(1, loop.LeaseCount);
        Assert.Equal(1, cadence.Active);
        Assert.Equal(1, control.ActiveGpuSubscriptions);

        b.Dispose();
        Assert.False(loop.IsRecording);
        Assert.Equal(0, loop.LeaseCount);
        Assert.Equal(0, cadence.Active);
        Assert.Equal(0, control.ActiveGpuSubscriptions);
    }

    [Fact]
    public void A_double_dispose_returns_one_count_only()
    {
        var control = new TestControl();
        var cadence = new CountingCadence();
        using var loop = NewLoop(new ScriptedSource(), control, cadence);
        var a = loop.AcquireRecordingLease();
        var b = loop.AcquireRecordingLease();

        a.Dispose();
        a.Dispose();
        a.Dispose();

        // b still holds its lease: the loop is still recording and nothing the first lease started was torn down.
        Assert.Equal(1, loop.LeaseCount);
        Assert.True(loop.IsRecording);
        Assert.Equal(1, cadence.Active);
        Assert.Equal(1, control.ActiveGpuSubscriptions);

        b.Dispose();
        b.Dispose();
        Assert.Equal(0, loop.LeaseCount);
        Assert.False(loop.IsRecording);
    }

    [Fact]
    public void The_loop_period_and_the_lease_count_are_public_and_read_only_so_a_window_can_show_what_a_recording_does_to_the_beat()
    {
        // A front end outside Core (the window, the measured run) reads them; nothing of it may be able to change them.
        foreach (var name in new[] { nameof(SamplerLoop.EffectivePeriod), nameof(SamplerLoop.LeaseCount) })
        {
            var property = typeof(SamplerLoop).GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(property);
            Assert.True(property.GetMethod is { IsPublic: true }, name + " must be public");
            Assert.True(property.SetMethod is null, name + " must be read only");
        }

        var control = new TestControl { Interval = TimeSpan.FromSeconds(5) };
        using var loop = NewLoop(new ScriptedSource(), control);
        Assert.Equal(0, loop.LeaseCount);
        Assert.Equal(TimeSpan.FromSeconds(5), loop.EffectivePeriod);
        using (loop.AcquireRecordingLease())
        {
            Assert.Equal(1, loop.LeaseCount);
            Assert.Equal(FastBeat, loop.EffectivePeriod);
        }

        Assert.Equal(0, loop.LeaseCount);
        Assert.Equal(TimeSpan.FromSeconds(5), loop.EffectivePeriod);
    }

    [Fact]
    public void The_last_release_goes_back_to_the_settings_as_they_are_now_not_as_they_were_at_the_first_lease()
    {
        var control = new TestControl { Interval = TimeSpan.FromSeconds(5), MinimizedInterval = TimeSpan.FromSeconds(7) };
        using var loop = NewLoop(new ScriptedSource(), control);
        var a = loop.AcquireRecordingLease();
        var b = loop.AcquireRecordingLease();
        Assert.Equal(FastBeat, loop.EffectivePeriod);

        // The user changes everything while the recording runs.
        control.Interval = TimeSpan.FromSeconds(3);
        control.Minimized = true;
        control.Paused = true;
        Assert.Equal(FastBeat, loop.EffectivePeriod); // the lease still rules the beat
        Assert.Equal(TimeSpan.FromSeconds(3), control.Interval); // and never wrote the user's settings

        a.Dispose();
        b.Dispose();

        Assert.False(loop.IsRecording);
        Assert.Equal(TimeSpan.FromSeconds(3), control.Interval);
        Assert.True(control.Minimized);
        Assert.True(control.Paused);
        Assert.Equal(TimeSpan.FromSeconds(7), loop.EffectivePeriod); // the minimized interval the user has NOW
    }

    [Fact]
    public async Task A_lease_overrides_a_pause_only_while_it_is_held()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromMilliseconds(20), Paused = true };
        using var loop = NewLoop(source, control);
        loop.Start();
        Assert.Equal(0, await SettledAsync(() => source.Calls)); // paused, no lease: nothing is sampled

        using (var lease = loop.AcquireRecordingLease())
        {
            Assert.True(await UntilAsync(() => source.Calls >= 4), "a paused loop must sample for a recording");
            Assert.True(control.Paused, "the lease must not touch the user's pause");
        }

        // Released: the user's pause rules again, so the loop stops.
        var parked = await SettledAsync(() => source.Calls);
        Assert.True(control.Paused);

        control.Paused = false;
        Assert.True(await UntilAsync(() => source.Calls > parked), "unpausing must resume the user's sampling");
    }

    [Fact]
    public void One_GPU_subscription_and_one_SensorHost_lease_for_the_first_lease_only()
    {
        var control = new TestControl();
        var cadence = new CountingCadence();
        using var loop = NewLoop(new ScriptedSource(), control, cadence);

        var a = loop.AcquireRecordingLease();
        var b = loop.AcquireRecordingLease();
        var c = loop.AcquireRecordingLease();

        Assert.Equal(1, control.GpuSubscriptions);
        Assert.Equal(1, cadence.Acquired);

        a.Dispose();
        b.Dispose();
        c.Dispose();
        Assert.Equal(0, control.ActiveGpuSubscriptions);

        // A new recording takes its own.
        loop.AcquireRecordingLease().Dispose();
        Assert.Equal(2, control.GpuSubscriptions);
        Assert.Equal(2, cadence.Acquired);
        Assert.Equal(0, cadence.Active);
    }

    [Fact]
    public void A_lease_works_without_a_sensor_client()
    {
        var control = new TestControl();
        using var loop = new SamplerLoop(new ScriptedSource(), control, () => null);

        loop.AcquireRecordingLease().Dispose();

        Assert.Equal(1, control.GpuSubscriptions);
        Assert.Equal(0, control.ActiveGpuSubscriptions);
    }

    [Fact]
    public void A_cadence_that_cannot_be_leased_does_not_leave_the_GPU_subscribed()
    {
        var control = new TestControl();
        using var loop = new SamplerLoop(new ScriptedSource(), control, () => new ThrowingCadence());

        _ = Assert.Throws<InvalidOperationException>(loop.AcquireRecordingLease);

        Assert.Equal(0, control.ActiveGpuSubscriptions);
        Assert.Equal(0, loop.LeaseCount);
        Assert.False(loop.IsRecording);
    }

    private sealed class ThrowingCadence : ISensorCadence
    {
        public IDisposable AcquireOneSecondLease() => throw new InvalidOperationException("no sensor host");
    }

    [Fact]
    public void The_recording_period_is_one_second_by_default_and_the_users_interval_otherwise()
    {
        var control = new TestControl { Interval = TimeSpan.FromMilliseconds(500), MinimizedInterval = TimeSpan.FromSeconds(5) };
        using var loop = new SamplerLoop(new ScriptedSource(), control);
        Assert.Equal(TimeSpan.FromSeconds(1), SamplerLoop.RecordingPeriod);
        Assert.Equal(TimeSpan.FromMilliseconds(500), loop.EffectivePeriod);

        control.Minimized = true;
        Assert.Equal(TimeSpan.FromSeconds(5), loop.EffectivePeriod);

        var lease = loop.AcquireRecordingLease();
        Assert.Equal(TimeSpan.FromSeconds(1), loop.EffectivePeriod); // even above the user's own 0.5 s and the minimized 5 s

        lease.Dispose();
        Assert.Equal(TimeSpan.FromSeconds(5), loop.EffectivePeriod);
        control.Minimized = false;
        Assert.Equal(TimeSpan.FromMilliseconds(500), loop.EffectivePeriod);
    }

    [Fact]
    public async Task A_lease_makes_the_loop_sample_at_the_recording_period_even_when_the_users_interval_is_long()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromSeconds(30), MinimizedInterval = TimeSpan.FromSeconds(60), Minimized = true };
        using var loop = NewLoop(source, control);
        loop.Start();
        Assert.True(await UntilAsync(() => source.Calls >= 1));

        // The loop is now waiting out a 60 s beat. The lease wakes it and the next beat is the recording one.
        using var lease = loop.AcquireRecordingLease();

        Assert.True(await UntilAsync(() => source.Calls >= 6), "the loop kept the user's slow beat while recording");
    }

    [Fact]
    public async Task Samples_reach_RecordingSampled_in_order_with_a_sequence_and_the_period()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromSeconds(30) };
        using var loop = NewLoop(source, control);
        var recorder = new Recorder();
        loop.RecordingSampled += recorder.Add;
        loop.Start();

        using var lease = loop.AcquireRecordingLease();
        Assert.True(await UntilAsync(() => recorder.Count >= 5));

        var items = recorder.Items;
        for (var i = 0; i < items.Length; i++)
        {
            var core = items[i].Core;
            Assert.Equal(0, core.Frame!.Value.Generation); // Core's own sampler
            Assert.Equal(20, core.PeriodMs);
            Assert.Equal(SourceGap.None, core.Gap);
            Assert.True(core.AcquiredUs > 0);
            if (i > 0)
            {
                Assert.Equal(items[i - 1].Core.Frame!.Value.Sequence + 1, core.Frame!.Value.Sequence); // nothing dropped while the handler keeps up
                Assert.True(core.AcquiredUs >= items[i - 1].Core.AcquiredUs);
            }
        }
    }

    [Fact]
    public async Task No_sample_reaches_the_recorder_without_a_lease()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromMilliseconds(20) };
        using var loop = NewLoop(source, control);
        var recorder = new Recorder();
        loop.RecordingSampled += recorder.Add;

        loop.Start();
        Assert.True(await UntilAsync(() => source.Calls >= 5));
        await loop.StopAsync();

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task A_lease_taken_before_Start_records_once_the_loop_starts()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromSeconds(30) };
        using var loop = NewLoop(source, control);
        var recorder = new Recorder();
        loop.RecordingSampled += recorder.Add;

        using var lease = loop.AcquireRecordingLease();
        Assert.True(loop.IsRecording);
        Assert.Equal(0, source.Calls);

        loop.Start();
        Assert.True(await UntilAsync(() => recorder.Count >= 3));
    }

    [Fact]
    public async Task A_slow_handler_never_holds_the_loop_and_the_oldest_waiting_samples_are_dropped()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromSeconds(30) };
        using var loop = NewLoop(source, control, periodMs: 10, queue: 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        var seen = new List<long>();
        loop.RecordingSampled += sample =>
        {
            lock (seen)
            {
                seen.Add(sample.Core.Frame!.Value.Sequence);
            }

            _ = entered.TrySetResult();
            release.Wait(); // a handler that blocks
        };

        using var lease = loop.AcquireRecordingLease();
        try
        {
            loop.Start();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The handler holds the first sample. The loop must go on sampling regardless: its calls keep growing.
            var blockedAt = source.Calls;
            Assert.True(await UntilAsync(() => source.Calls >= blockedAt + 8), "a blocked handler stopped the sampling loop");
        }
        finally
        {
            release.Set();
        }

        Assert.True(await UntilAsync(() => Count(seen) >= 4));
        long[] sequence;
        lock (seen)
        {
            sequence = [.. seen];
        }

        // At least eight samples were taken while the handler was stuck, but only the newest two could wait: the rest were dropped and the
        // gap is visible as a jump in the sequence (not as a repeated or reordered one).
        Assert.True(sequence[1] > sequence[0] + 1, $"expected a jump after the blocked sample, got {string.Join(",", sequence)}");
        for (var i = 1; i < sequence.Length; i++)
        {
            Assert.True(sequence[i] > sequence[i - 1], $"the sequence went backwards: {string.Join(",", sequence)}");
        }
    }

    private static int Count(List<long> list)
    {
        lock (list)
        {
            return list.Count;
        }
    }

    [Fact]
    public async Task A_handler_that_throws_does_not_stop_the_other_handlers_or_the_loop()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromSeconds(30) };
        using var loop = NewLoop(source, control);
        var recorder = new Recorder();
        loop.RecordingSampled += _ => throw new InvalidOperationException("recorder bug");
        loop.RecordingSampled += recorder.Add;
        loop.Start();

        using var lease = loop.AcquireRecordingLease();

        Assert.True(await UntilAsync(() => recorder.Count >= 4));
    }

    [Fact]
    public async Task A_handler_that_unsubscribes_gets_no_more_samples()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromSeconds(30) };
        using var loop = NewLoop(source, control);
        var stays = new Recorder();
        var leaves = new Recorder();
        loop.RecordingSampled += stays.Add;
        loop.RecordingSampled += leaves.Add;
        loop.Start();
        using var lease = loop.AcquireRecordingLease();
        Assert.True(await UntilAsync(() => leaves.Count >= 2));

        loop.RecordingSampled -= leaves.Add;
        var after = leaves.Count;
        var target = stays.Count + 4;

        Assert.True(await UntilAsync(() => stays.Count >= target));
        Assert.True(leaves.Count <= after + 2, "samples that were already being handed over may still arrive, no others");
    }

    [Fact]
    public async Task The_window_keeps_the_users_cadence_while_the_loop_records()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromSeconds(30) };
        using var loop = NewLoop(source, control, periodMs: 10);
        var shown = 0;
        loop.Sampled += _ => Interlocked.Increment(ref shown);
        var recorder = new Recorder();
        loop.RecordingSampled += recorder.Add;
        loop.Start();

        using var lease = loop.AcquireRecordingLease();
        Assert.True(await UntilAsync(() => recorder.Count >= 12));

        // The window got the sample the start gave it and the first recorded one; the 30 s interval keeps the rest away from it.
        Assert.InRange(Volatile.Read(ref shown), 1, 2);
        Assert.InRange(loop.History.Count, 1, 2);
    }

    [Fact]
    public async Task A_paused_window_gets_nothing_while_the_recording_goes_on()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Paused = true };
        using var loop = NewLoop(source, control, periodMs: 10);
        var shown = 0;
        loop.Sampled += _ => Interlocked.Increment(ref shown);
        var recorder = new Recorder();
        loop.RecordingSampled += recorder.Add;
        loop.Start();

        using var lease = loop.AcquireRecordingLease();
        Assert.True(await UntilAsync(() => recorder.Count >= 6));

        Assert.Equal(0, Volatile.Read(ref shown));
        Assert.Empty(loop.History);
        Assert.Null(loop.Latest);
    }

    [Fact]
    public async Task A_refresh_request_while_recording_shows_the_next_sample_on_the_window()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromSeconds(30) };
        using var loop = NewLoop(source, control, periodMs: 10);
        var shown = 0;
        loop.Sampled += _ => Interlocked.Increment(ref shown);
        var recorder = new Recorder();
        loop.RecordingSampled += recorder.Add;
        loop.Start();
        using var lease = loop.AcquireRecordingLease();
        Assert.True(await UntilAsync(() => recorder.Count >= 4 && Volatile.Read(ref shown) >= 1));
        var before = Volatile.Read(ref shown);

        loop.RequestSampleNow();

        Assert.True(await UntilAsync(() => Volatile.Read(ref shown) > before), "F5 must still show a fresh sample while recording");
    }

    [Fact]
    public void Disposing_the_loop_gives_back_every_lease_and_a_late_dispose_is_harmless()
    {
        var control = new TestControl();
        var cadence = new CountingCadence();
        var loop = NewLoop(new ScriptedSource(), control, cadence);
        var a = loop.AcquireRecordingLease();
        var b = loop.AcquireRecordingLease();

        loop.Dispose();

        Assert.Equal(0, cadence.Active);
        Assert.Equal(0, control.ActiveGpuSubscriptions);
        Assert.False(loop.IsRecording);
        a.Dispose();
        b.Dispose();
        Assert.Equal(0, cadence.Active);
        _ = Assert.Throws<ObjectDisposedException>(loop.AcquireRecordingLease);
    }

    [Fact]
    public async Task Without_a_lease_the_loop_is_what_it_was_the_users_interval_every_sample_shown_nothing_subscribed()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Interval = TimeSpan.FromMilliseconds(20), MinimizedInterval = TimeSpan.FromMilliseconds(400) };
        var cadence = new CountingCadence();
        using var loop = NewLoop(source, control, cadence);
        var shown = 0;
        loop.Sampled += _ => Interlocked.Increment(ref shown);
        var recorder = new Recorder();
        loop.RecordingSampled += recorder.Add;

        Assert.Equal(TimeSpan.FromMilliseconds(20), loop.EffectivePeriod);
        loop.Start();
        Assert.True(await UntilAsync(() => source.Calls >= 6));
        await loop.StopAsync();

        Assert.False(loop.IsRecording);
        Assert.Equal(source.Calls, Volatile.Read(ref shown)); // every sample goes to the window
        Assert.Equal(0, recorder.Count);
        Assert.Equal(0, control.GpuSubscriptions);
        Assert.Equal(0, cadence.Acquired);
        control.Minimized = true;
        Assert.Equal(TimeSpan.FromMilliseconds(400), loop.EffectivePeriod);
    }

    [Fact]
    public async Task A_tick_that_fails_while_recording_still_takes_its_sequence_number_so_the_gap_shows()
    {
        var source = new ScriptedSource { Fail = n => n == 3 ? new InvalidOperationException("one bad tick") : null };
        var control = new TestControl { Interval = TimeSpan.FromSeconds(30) };
        using var loop = NewLoop(source, control, periodMs: 10);
        var failures = 0;
        loop.Failed += _ => Interlocked.Increment(ref failures);
        var recorder = new Recorder();
        loop.RecordingSampled += recorder.Add;
        using var lease = loop.AcquireRecordingLease();

        loop.Start();
        Assert.True(await UntilAsync(() => recorder.Count >= 5), "the loop did not carry on after the failed tick");

        Assert.Equal(1, Volatile.Read(ref failures));
        var sequence = recorder.Items.Select(s => s.Core.Frame!.Value.Sequence).ToArray();
        // 1, 2, (the failed third tick), 4, 5: the recorder sees the failed tick as a jump of two, never as an unbroken run.
        Assert.Contains(2L, sequence.Zip(sequence.Skip(1), (a, b) => b - a));
    }

    [Fact]
    public void A_lease_taken_after_the_loop_was_disposed_throws_and_takes_nothing()
    {
        var control = new TestControl();
        var cadence = new CountingCadence();
        var loop = NewLoop(new ScriptedSource(), control, cadence);
        loop.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(loop.AcquireRecordingLease);

        Assert.Equal(0, control.GpuSubscriptions);
        Assert.Equal(0, cadence.Acquired);
        Assert.Equal(0, loop.LeaseCount);
    }

    private sealed class DisposingCadence(CountingCadence inner, Action onAcquire) : ISensorCadence
    {
        public IDisposable AcquireOneSecondLease()
        {
            var lease = inner.AcquireOneSecondLease();
            onAcquire(); // the loop is disposed by someone else while the first lease is being taken
            return lease;
        }
    }

    [Fact]
    public void A_loop_disposed_while_the_first_lease_is_being_taken_is_left_with_no_subscription_and_no_SensorHost_lease()
    {
        var control = new TestControl();
        var inner = new CountingCadence();
        SamplerLoop? loop = null;
        loop = new SamplerLoop(new ScriptedSource(), control, () => new DisposingCadence(inner, () => loop!.Dispose()), FastBeat, SamplerLoop.RecordingQueueLength);

        _ = Assert.Throws<ObjectDisposedException>(loop.AcquireRecordingLease);

        Assert.Equal(0, inner.Active); // not kept at one second for a loop that is gone
        Assert.Equal(0, control.ActiveGpuSubscriptions);
        Assert.False(loop.IsRecording);
        Assert.Equal(0, loop.LeaseCount);
    }

    [Fact]
    public void Showing_a_paused_window_is_refused_without_a_lease_and_accepted_with_one()
    {
        var control = new TestControl { Paused = true };
        using var loop = NewLoop(new ScriptedSource(), control);

        Assert.False(loop.TryRequestShowWhilePaused());

        using var lease = loop.AcquireRecordingLease();
        Assert.True(loop.TryRequestShowWhilePaused());
    }

    [Fact]
    public async Task A_paused_window_with_a_lease_shows_exactly_one_new_sample_when_it_asks()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Paused = true };
        using var loop = NewLoop(source, control, periodMs: 10);
        var shown = 0;
        loop.Sampled += _ => Interlocked.Increment(ref shown);
        var recorder = new Recorder();
        loop.RecordingSampled += recorder.Add;
        loop.Start();
        using var lease = loop.AcquireRecordingLease();
        Assert.True(await UntilAsync(() => recorder.Count >= 3));
        Assert.Equal(0, Volatile.Read(ref shown)); // paused: nothing for the window

        Assert.True(loop.TryRequestShowWhilePaused()); // F5 on the paused window

        Assert.True(await UntilAsync(() => Volatile.Read(ref shown) >= 1), "the paused window got nothing after F5");
        var after = recorder.Count;
        Assert.True(await UntilAsync(() => recorder.Count >= after + 6)); // the recording goes on...
        Assert.Equal(1, Volatile.Read(ref shown)); // ...and the window got that one sample and no more
        Assert.Single(loop.History);
        Assert.True(control.Paused);
    }

    [Fact]
    public async Task A_paused_one_shot_samples_without_a_lease_and_hands_the_window_to_the_loop_with_one()
    {
        var source = new ScriptedSource();
        var control = new TestControl { Paused = true };
        using var loop = NewLoop(source, control, periodMs: 10);

        var alone = await loop.TrySampleWhilePausedAsync(CancellationToken.None);

        Assert.NotNull(alone);
        Assert.Equal(1, source.Calls);

        using var lease = loop.AcquireRecordingLease();
        var recording = await loop.TrySampleWhilePausedAsync(CancellationToken.None);

        Assert.Null(recording); // the loop is sampling for the recording: a second sampler here would put an off-beat row into it
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task A_paused_one_shot_and_the_loop_never_sample_the_source_at_the_same_time()
    {
        // The race: the window reads "not recording", a recording starts, and the window's one-shot runs next to the loop's own sampling.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedSource { Hold = hold };
        var control = new TestControl { Paused = true };
        using var loop = NewLoop(source, control, periodMs: 10);
        var oneShot = loop.TrySampleWhilePausedAsync(CancellationToken.None);
        try
        {
            Assert.True(await UntilAsync(() => source.Held == 1), "the one-shot did not reach the source");

            using var lease = loop.AcquireRecordingLease(); // the recording starts while the one-shot is inside the source; held to the end, or the paused loop stops sampling
            loop.Start();

            var overlapped = await UntilAsync(() => source.Held >= 2, 1_000);
            _ = hold.TrySetResult();
            Assert.False(overlapped, "the loop sampled the source while the window's one-shot was still inside it");

            _ = await oneShot;
            Assert.True(await UntilAsync(() => source.Calls >= 4), "the loop did not carry on after the one-shot");
            Assert.Equal(1, source.PeakConcurrency);
        }
        finally
        {
            _ = hold.TrySetResult(); // a failed assert above must not leave the one-shot blocked
        }
    }

    private sealed class BeatSource : ISnapshotSource, IBeatAware
    {
        public Func<TimeSpan>? Beat { get; private set; }

        public void UseBeat(Func<TimeSpan> beat) => Beat = beat;

        public void ResetBaseline()
        {
        }

        public Task<Snapshot> SampleAsync(CancellationToken ct = default) => Task.FromResult(FakeSnapshots.Create(DateTime.UtcNow, tick: 1, intervalMs: 1));

        public void Dispose()
        {
        }
    }

    [Fact]
    public void A_source_that_states_a_nominal_interval_is_told_the_loops_beat_which_is_the_recording_period_under_a_lease()
    {
        var control = new TestControl { Interval = TimeSpan.FromSeconds(30) };
        var source = new BeatSource();
        using var loop = new SamplerLoop(source, control, null, FastBeat, SamplerLoop.RecordingQueueLength);

        Assert.NotNull(source.Beat);
        Assert.Equal(TimeSpan.FromSeconds(30), source.Beat());

        using var lease = loop.AcquireRecordingLease();

        Assert.Equal(FastBeat, source.Beat());
    }
}
