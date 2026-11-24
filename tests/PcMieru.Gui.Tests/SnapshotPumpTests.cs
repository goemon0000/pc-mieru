using System.Diagnostics;
using PcMieru.Core.Collection;

namespace PcMieru.Gui.Tests;

/// <summary>The pump is the window's view of Core's one sampler: it follows the shared settings, delivers on the UI thread, and never samples a second time.</summary>
public class SnapshotPumpTests
{
    private sealed class CountingSource : ISnapshotSource
    {
        private readonly FakeSnapshotSource _inner = new();
        private int _calls;
        private int _resets;

        public int Calls => Volatile.Read(ref _calls);

        public int Resets => Volatile.Read(ref _resets);

        public void ResetBaseline()
        {
            _ = Interlocked.Increment(ref _resets);
            _inner.ResetBaseline();
        }

        public Task<Snapshot> SampleAsync(CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return _inner.SampleAsync(ct);
        }

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>Remembers which threads called it, so a test can tell the loop's thread from the window's.</summary>
    private sealed class ThreadTrackingSource : ISnapshotSource
    {
        private readonly FakeSnapshotSource _inner = new();
        private readonly HashSet<int> _threads = [];
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool WasCalledOn(int managedThreadId)
        {
            lock (_threads)
            {
                return _threads.Contains(managedThreadId);
            }
        }

        public void ResetBaseline() => _inner.ResetBaseline();

        public Task<Snapshot> SampleAsync(CancellationToken ct = default)
        {
            lock (_threads)
            {
                _ = _threads.Add(Environment.CurrentManagedThreadId);
            }

            _ = Interlocked.Increment(ref _calls);
            return _inner.SampleAsync(ct);
        }

        public void Dispose() => _inner.Dispose();
    }

    // These tests stay on one thread (the pump's deliveries are posted to the thread that made it), so they cannot await.
    private static void Finish(Task task) => task.GetAwaiter().GetResult();

    /// <summary>Lets the pump's posted deliveries run: the test thread stands in for the window's message loop.</summary>
    private static bool Pumping(Func<bool> condition, int timeoutMs = 10_000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            Application.DoEvents();
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    [Fact]
    public void The_pump_writes_the_users_choices_into_the_loops_shared_settings()
    {
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
        var settings = services.Get<ISamplerControl>();
        using var pump = new SnapshotPump(services, 2000);

        Assert.Equal(TimeSpan.FromSeconds(2), settings.Interval);
        pump.SetInterval(500);
        Assert.Equal(TimeSpan.FromMilliseconds(500), settings.Interval);
        pump.SetPaused(true);
        Assert.True(settings.Paused);
        pump.SetPaused(false);
        Assert.False(settings.Paused);
        pump.SetSlowMode(true);
        Assert.True(settings.Minimized);
        Assert.True(pump.SlowMode);
        Assert.Equal(SnapshotPump.SlowIntervalMs, pump.EffectiveIntervalMs);
    }

    [Fact]
    public void The_slow_interval_the_pump_names_is_the_one_the_loop_uses()
    {
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
        Assert.Equal(TimeSpan.FromMilliseconds(SnapshotPump.SlowIntervalMs), ((SamplerSettings)services.Get<ISamplerControl>()).MinimizedInterval);
        Assert.Equal(SamplerLoop.HistoryLength, SnapshotPump.HistoryLength);
    }

    [Fact]
    public void Snapshots_arrive_on_the_threads_that_made_the_pump_and_the_history_grows()
    {
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
        using var pump = new SnapshotPump(services, 500);
        var uiThread = Environment.CurrentManagedThreadId;
        var threads = new List<int>();
        pump.Sampled += _ => threads.Add(Environment.CurrentManagedThreadId);

        pump.Start();
        Assert.True(Pumping(() => pump.History.Count >= 1), "no snapshot was delivered");
        Assert.NotNull(pump.Latest);
        Assert.True(pump.Running);

        var tick = pump.TickAsync();
        Assert.True(Pumping(() => tick.IsCompleted), "a refresh did not return");
        Finish(tick);

        Assert.True(pump.History.Count >= 2);
        Assert.Same(pump.History[^1], pump.Latest);
        Assert.All(threads, id => Assert.Equal(uiThread, id));
    }

    [Fact]
    public void The_pump_never_samples_the_source_itself_while_the_loop_is_running()
    {
        var source = new CountingSource();
        var settings = new SamplerSettings();
        using var loop = new SamplerLoop(source, settings);
        var taken = 0;
        loop.Sampled += _ => Interlocked.Increment(ref taken);
        using var pump = new SnapshotPump(loop, source, 500);

        pump.Start();
        Assert.True(Pumping(() => pump.History.Count >= 1));
        for (var i = 0; i < 3; i++)
        {
            var tick = pump.TickAsync();
            Assert.True(Pumping(() => tick.IsCompleted));
            Finish(tick);
        }

        Finish(loop.StopAsync());
        Assert.Equal(taken, source.Calls); // every call on the source was the loop's
    }

    [Fact]
    public void While_paused_a_refresh_takes_one_sample_here_and_the_loop_takes_none()
    {
        var source = new CountingSource();
        using var loop = new SamplerLoop(source, new SamplerSettings());
        using var pump = new SnapshotPump(loop, source, 500);
        pump.SetPaused(true);
        pump.Start();
        Pumping(() => false, 400); // a paused loop would have sampled within this if it ignored the pause
        Assert.Equal(0, source.Calls);
        Assert.False(pump.Running);

        var tick = pump.TickAsync();
        Assert.True(Pumping(() => tick.IsCompleted));
        Finish(tick);

        Assert.Equal(1, source.Calls);
        Assert.NotNull(pump.Latest);
        Assert.Empty(loop.History);
    }

    [Fact]
    public void While_paused_and_recording_a_refresh_shows_exactly_one_new_snapshot_and_takes_none_on_the_window_thread()
    {
        // A recording keeps the loop sampling (1 s) under the user's pause. F5 on the paused window must still show something: the pump asks
        // the loop to show the NEXT sample of its own beat (a second sample taken here would put an off-beat row into the recording), and it
        // shows that one and no more.
        var source = new ThreadTrackingSource();
        using var loop = new SamplerLoop(source, new SamplerSettings());
        using var pump = new SnapshotPump(loop, source, 500);
        pump.SetPaused(true);
        pump.Start();
        using var lease = loop.AcquireRecordingLease();
        Assert.True(Pumping(() => source.Calls >= 1), "the loop did not sample for the recording under the pause");
        Assert.True(loop.IsRecording);
        Assert.Empty(pump.History); // paused: the window got nothing so far

        var tick = pump.TickAsync();

        Assert.True(Pumping(() => tick.IsCompleted), "the refresh of a paused, recording window did not come back");
        Finish(tick);
        Assert.False(source.WasCalledOn(Environment.CurrentManagedThreadId), "the refresh sampled on the window's thread");
        Assert.NotNull(pump.Latest);
        Assert.Single(pump.History);
        Assert.True(pump.Paused);
        var calls = source.Calls;
        Assert.True(Pumping(() => source.Calls >= calls + 2), "the recording stopped sampling"); // more recording samples arrive...
        Assert.Single(pump.History); // ...and the paused window shows none of them
        Assert.Single(loop.History);
    }

    [Fact]
    public void Once_the_recording_ends_a_paused_refresh_samples_here_again()
    {
        var source = new ThreadTrackingSource();
        using var loop = new SamplerLoop(source, new SamplerSettings());
        using var pump = new SnapshotPump(loop, source, 500);
        pump.SetPaused(true);
        pump.Start();
        var lease = loop.AcquireRecordingLease();
        Assert.True(Pumping(() => source.Calls >= 1));
        lease.Dispose();
        Assert.False(loop.IsRecording);

        var tick = pump.TickAsync();
        Assert.True(Pumping(() => tick.IsCompleted));
        Finish(tick);

        Assert.True(source.WasCalledOn(Environment.CurrentManagedThreadId), "the paused refresh did not sample again after the lease ended");
        Assert.NotNull(pump.Latest);
    }

    [Fact]
    public void Resuming_after_a_pause_shows_a_sample_at_once_and_the_baseline_was_dropped()
    {
        var source = new CountingSource();
        using var loop = new SamplerLoop(source, new SamplerSettings { Interval = TimeSpan.FromSeconds(30) });
        using var pump = new SnapshotPump(loop, source, 5000);
        pump.Start();
        Assert.True(Pumping(() => pump.History.Count >= 1));
        pump.SetPaused(true);
        Pumping(() => false, 400);
        var resets = source.Resets;
        var count = pump.History.Count;

        pump.SetPaused(false);

        Assert.True(Pumping(() => pump.History.Count > count, 5000), "no sample after the resume (the beat is 5 s, so the resume must ask for one)");
        Assert.True(source.Resets > resets, "the baseline was not dropped on resume");
    }

    [Fact]
    public void A_stopped_pump_delivers_nothing_more_and_a_refresh_does_nothing()
    {
        var source = new CountingSource();
        using var loop = new SamplerLoop(source, new SamplerSettings { Interval = TimeSpan.FromMilliseconds(250) });
        using var pump = new SnapshotPump(loop, source, 500);
        pump.Start();
        Assert.True(Pumping(() => pump.History.Count >= 1));

        pump.Stop();
        var count = pump.History.Count;
        Pumping(() => false, 600);
        Finish(pump.TickAsync());

        Assert.Equal(count, pump.History.Count);
        Assert.False(pump.Running);
    }

    [Fact]
    public void A_refresh_before_start_has_nothing_to_refresh()
    {
        var source = new CountingSource();
        using var loop = new SamplerLoop(source, new SamplerSettings());
        using var pump = new SnapshotPump(loop, source);

        Finish(pump.TickAsync());

        Assert.Equal(0, source.Calls);
        Assert.Null(pump.Latest);
    }
}
