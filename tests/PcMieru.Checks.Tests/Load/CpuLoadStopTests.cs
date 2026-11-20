using System.Diagnostics;
using PcMieru.Checks.Load;
using PcMieru.Checks.Tests.Run;
using PcMieru.Checks.Tests.SystemInfo;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Collectors;

namespace PcMieru.Checks.Tests.Load;

/// <summary>
/// The stop of the CPU load (DESIGN-A 6.2) when the things around it misbehave: a counter call that never returns, a control loop that is
/// stuck, a load thread that dies, a clock that oversleeps. The load must still end when it was asked to, with every thread joined; a late
/// sample is a missing one, never a 0; a thread that faults fails the run. Most tests run in the fake world (a clock moved by hand); the ones
/// about a stuck control loop need real threads and a chunk that sleeps a little, and take a few seconds at most.
/// </summary>
public sealed class CpuLoadStopTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // The load blocks, so it runs on a worker, as the procedure runs it.
    private static Task<CpuLoadResult> RunOnWorker(ICpuTestSession session, int seconds, CancellationToken ct = default, TimeSpan? sampleWait = null) =>
        Task.Factory.StartNew(
            () => CpuLoadTest.Run(session, TimeSpan.FromSeconds(seconds), null, ct, sampleWait),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    // ---- a PDH sample that never returns (the fake world) ----

    [Fact]
    public async Task A_sample_that_never_returns_is_missing_every_second_and_the_end_of_the_duration_still_stops_and_joins_every_thread()
    {
        var gate = new ManualResetEventSlim(false);
        var session = new FakeCpuSession(Cpus.Uniform(3)) { SampleBlock = gate };
        CpuLoadResult r;
        try
        {
            r = await RunOnWorker(session, 5, sampleWait: Short).WaitAsync(Bound);
        }
        finally
        {
            gate.Set();
        }

        Assert.False(r.Stopped);
        Assert.Equal(5, r.Ticks.Count); // every second was read, with or without its sample
        Assert.All(r.Ticks, static t => Assert.Null(t.Sample.EffectiveMhz));
        Assert.All(r.Ticks, static t => Assert.Null(t.Sample.UtilizationPercent)); // missing, never 0
        Assert.Equal(1, session.Samples); // one call is stuck out there: no second one was made behind it
        Assert.All(session.Threads, static t => Assert.Equal(1, t.Joins));
        Assert.All(session.Threads, static t => Assert.False(t.IsAlive));
        Assert.All(session.Threads, static t => Assert.Equal(0, t.StepsDuringJoin)); // the stop flag was set before the join
        Assert.True(r.Chunks > 0);
        Assert.Equal(5 * 1.002, r.Seconds, 3);
    }

    [Fact]
    public async Task A_cancel_while_the_sample_is_stuck_ends_the_wait_sets_the_stop_flag_and_joins_every_thread()
    {
        var gate = new ManualResetEventSlim(false);
        var session = new FakeCpuSession(Cpus.Uniform(3)) { SampleBlock = gate };
        using var cts = new CancellationTokenSource();
        var running = RunOnWorker(session, 300, cts.Token, Timeout.InfiniteTimeSpan); // a wait for the sample with no end of its own
        CpuLoadResult r;
        try
        {
            Assert.True(session.SampleEntered.Wait(Bound));

            await cts.CancelAsync();
            r = await running.WaitAsync(Bound);
        }
        finally
        {
            gate.Set();
        }

        Assert.True(r.Stopped);
        Assert.Single(r.Ticks);
        Assert.Null(r.Ticks[0].Sample.EffectiveMhz);
        Assert.Equal(1, session.Samples);
        Assert.All(session.Threads, static t => Assert.Equal(1, t.Joins));
        Assert.All(session.Threads, static t => Assert.False(t.IsAlive));
        Assert.All(session.Threads, static t => Assert.Equal(0, t.StepsDuringJoin));
    }

    // ---- a PDH sample that never returns (real threads) ----

    [Fact]
    public async Task With_real_threads_a_sample_that_never_returns_delays_the_end_by_the_bounded_wait_and_not_more()
    {
        var gate = new ManualResetEventSlim(false);
        var world = new RealWorld(Cpus.Uniform(2), new RealLoadClock()) { SampleBlock = gate };
        var clock = Stopwatch.StartNew();
        CpuLoadResult r;
        try
        {
            r = await RunOnWorker(world, 1).WaitAsync(Bound);
        }
        finally
        {
            gate.Set();
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"the run took {clock.Elapsed}");
        Assert.False(r.Stopped);
        Assert.Single(r.Ticks);
        Assert.Null(r.Ticks[0].Sample.EffectiveMhz);
        Assert.Equal(2, world.Handles.Count);
        Assert.All(world.Handles, static h => Assert.False(h.IsAlive));
        Assert.Equal(world.ChunksStarted, world.ChunksFinished);
    }

    // ---- a control loop that is stuck: the stop flag does not depend on it ----

    [Fact]
    public async Task A_cancel_sets_the_stop_flag_by_itself_even_when_the_control_loop_is_stuck()
    {
        var stuck = new StuckClock();
        var world = new RealWorld(Cpus.Uniform(3), stuck);
        using var cts = new CancellationTokenSource();
        var running = RunOnWorker(world, 300, cts.Token);
        try
        {
            Assert.True(stuck.Entered.Wait(Bound));
            Assert.Equal(3, world.Handles.Count);
            Assert.All(world.Handles, static h => Assert.True(h.IsAlive));

            await cts.CancelAsync();

            Assert.True(
                SpinWait.SpinUntil(() => world.Handles.All(static h => !h.IsAlive), TimeSpan.FromSeconds(5)),
                "the load threads were still running 5 seconds after the cancel");
            Assert.False(running.IsCompleted); // the control loop is still stuck in its wait: the threads stopped without it
        }
        finally
        {
            stuck.Release.Set();
        }

        var r = await running.WaitAsync(Bound);

        Assert.True(r.Stopped);
        Assert.Equal(world.ChunksStarted, world.ChunksFinished);
    }

    [Fact]
    public async Task The_end_of_the_duration_sets_the_stop_flag_by_itself_even_when_the_control_loop_is_stuck()
    {
        var stuck = new StuckClock();
        var world = new RealWorld(Cpus.Uniform(2), stuck);
        var clock = Stopwatch.StartNew();
        var running = RunOnWorker(world, 1);
        try
        {
            Assert.True(stuck.Entered.Wait(Bound));

            var limit = CpuLoadTest.TickInterval + CpuLoadTest.DeadlineGrace + TimeSpan.FromSeconds(5);
            Assert.True(
                SpinWait.SpinUntil(() => world.Handles.All(static h => !h.IsAlive), limit),
                $"the load threads were still running {limit} after the start");
            Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1), $"the load stopped after {clock.Elapsed}, before its second was over");
            Assert.False(running.IsCompleted);
        }
        finally
        {
            stuck.Release.Set();
        }

        var r = await running.WaitAsync(Bound);

        Assert.False(r.Stopped);
        Assert.Equal(world.ChunksStarted, world.ChunksFinished);
    }

    // ---- a load thread that dies ----

    [Fact]
    public void An_out_of_memory_in_a_load_thread_is_a_fault_that_fails_the_run_and_still_joins_every_thread()
    {
        var session = new FakeCpuSession(Cpus.Uniform(3)) { ChunkFault = static n => n == 100 ? new OutOfMemoryException() : null };

        var failure = Assert.Throws<CheckRunFailedException>(() => CpuLoadTest.Run(session, TimeSpan.FromSeconds(30), null, CancellationToken.None));

        Assert.Equal(CheckRunFailureCodes.InternalError, failure.Code);
        Assert.Contains("OutOfMemoryException", failure.Detail, StringComparison.Ordinal);
        Assert.Equal(1, session.Waits); // not a minute of load for a check that is already broken
        Assert.All(session.Threads, static t => Assert.Equal(1, t.Joins));
        Assert.All(session.Threads, static t => Assert.False(t.IsAlive));
    }

    [Fact]
    public async Task An_out_of_memory_in_a_load_thread_is_a_failed_run_with_an_internal_error_and_it_is_saved()
    {
        var session = new FakeCpuSession(Cpus.Uniform(3)) { ChunkFault = static n => n == 100 ? new OutOfMemoryException() : null };
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await rig.RunAsync(new CheckRequest(CheckKind.Cpu), null, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.InternalError, run.FailureCode);
        Assert.Contains("OutOfMemoryException", run.FailureDetail, StringComparison.Ordinal);
        Assert.True(run.IsSaved);
        Assert.True(session.Disposed);
        Assert.All(session.Threads, static t => Assert.False(t.IsAlive));
    }

    [Fact]
    public void The_real_starter_reports_an_out_of_memory_of_a_step_as_a_fault_and_ends_the_thread()
    {
        Exception? seen = null;

        var thread = new RealLoadThreadStarter().Start("PcMieru.CpuLoad.test", static () => { }, static () => throw new OutOfMemoryException(), ex => seen = ex);
        thread.Join();

        Assert.IsType<OutOfMemoryException>(seen);
        Assert.False(thread.IsAlive);
    }

    [Fact]
    public void The_real_starter_reports_a_start_action_that_throws_as_a_fault_and_never_runs_a_step()
    {
        Exception? seen = null;
        var steps = 0;

        var thread = new RealLoadThreadStarter().Start(
            "PcMieru.CpuLoad.test",
            static () => throw new InvalidOperationException("the start action failed"),
            () =>
            {
                steps++;
                return true;
            },
            ex => seen = ex);
        thread.Join();

        Assert.IsType<InvalidOperationException>(seen);
        Assert.Equal(0, steps);
    }

    [Fact]
    public void A_fault_handler_that_throws_cannot_end_the_process_from_a_load_thread()
    {
        var thread = new RealLoadThreadStarter().Start(
            "PcMieru.CpuLoad.test",
            static () => { },
            static () => throw new InvalidOperationException("the step failed"),
            static _ => throw new InvalidOperationException("the handler failed"));

        thread.Join();

        Assert.False(thread.IsAlive);
    }

    // ---- ticks on an absolute schedule ----

    [Fact]
    public void A_clock_that_oversleeps_every_wait_does_not_push_the_run_late_300_readings_end_on_the_requested_time()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2)) { Oversleep = TimeSpan.FromMilliseconds(10) };

        var r = CpuLoadTest.Run(session, TimeSpan.FromSeconds(300), null, CancellationToken.None);

        var due = 300 * 1.002;
        Assert.False(r.Stopped);
        Assert.Equal(300, r.Ticks.Count);
        Assert.Equal(300, session.Waits);
        Assert.InRange(r.Seconds, due, due + 1.002); // within one tick of the time asked for; the old schedule ended at 303.6
        Assert.All(session.WaitLengths.Skip(1), static w => Assert.Equal(0.992, w.TotalSeconds, 3)); // each wait is the time left to the due reading, not a fresh 1.002 s
    }

    [Fact]
    public async Task A_late_reading_followed_by_an_early_one_still_gives_at_most_one_progress_a_second()
    {
        var time = new ManualTimeProvider();
        FakeCpuSession session = null!;
        session = new FakeCpuSession(Cpus.Uniform(2), time)
        {
            AfterAdvance = _ =>
            {
                if (session.Waits == 5)
                {
                    time.Advance(TimeSpan.FromMilliseconds(400)); // the fifth wait comes back late: the sixth reading is then only 0.6 s after it
                }
            },
        };
        using var rig = new RunRig(clock: time, cpuSessionFactory: () => session);
        var log = new ProgressLog();

        var run = await rig.RunAsync(new CheckRequest(CheckKind.Cpu), log, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Equal(30.06, Rows.Number(Rows.One(run.Result.Items, "cpu.duration")), 3); // the late reading did not push the end
        Assert.Equal(30, session.Waits);
        var reports = log.Items.Where(static p => p.Stage == CheckStage.Loading && p.RecentRate is not null).Select(static p => p.ElapsedSeconds).ToList();
        Assert.Equal(29, reports.Count); // the early reading's notification was dropped by the gate
        Assert.DoesNotContain(reports, static e => Math.Abs(e - (6 * 1.002)) < 0.001);
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i] - reports[i - 1] >= 1.0, $"reports {i - 1} and {i} are {reports[i] - reports[i - 1]} s apart");
        }
    }

    // ---- the real world: real threads, a chunk that sleeps a little, a clock of the test's choice ----

    /// <summary>A clock whose wait ignores the token and lasts until the test lets it go: a control loop that is stuck.</summary>
    private sealed class StuckClock : ILoadClock
    {
        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public long Timestamp => Stopwatch.GetTimestamp();

        public TimeSpan Elapsed(long since) => Stopwatch.GetElapsedTime(since);

        public bool WaitCancelled(TimeSpan wait, CancellationToken ct)
        {
            Entered.Set();
            Release.Wait();
            return false;
        }
    }

    private sealed class RealWorld(IReadOnlyList<LogicalCpu> cpus, ILoadClock clock) : ICpuTestSession
    {
        private readonly List<ILoadThread> _handles = [];
        private long _started;
        private long _finished;

        public IReadOnlyList<LogicalCpu> Cpus { get; } = cpus;

        /// <summary>When set, the PDH sample blocks on it (a counter call that hangs).</summary>
        public ManualResetEventSlim? SampleBlock { get; init; }

        public IReadOnlyList<ILoadThread> Handles
        {
            get
            {
                lock (_handles)
                {
                    return [.. _handles];
                }
            }
        }

        public long ChunksStarted => Interlocked.Read(ref _started);

        public long ChunksFinished => Interlocked.Read(ref _finished);

        public IChunkWork Work => new SleepyWork(this);

        public ICpuPin Pin { get; } = new AlwaysPin();

        public ILoadThreadStarter Threads => new RecordingStarter(this);

        public ILoadClock Clock { get; } = clock;

        public CpuPdhSample SamplePdh()
        {
            SampleBlock?.Wait();
            return PdhSamples.Healthy();
        }

        public void Dispose()
        {
        }

        private sealed class SleepyWork(RealWorld owner) : IChunkWork
        {
            public ulong Chunk(ulong seed)
            {
                Interlocked.Increment(ref owner._started);
                Thread.Sleep(2);
                Interlocked.Increment(ref owner._finished);
                return FakeCpuSession.Answer(seed);
            }
        }

        private sealed class AlwaysPin : ICpuPin
        {
            public bool PinCurrentThread(LogicalCpu cpu) => true;
        }

        private sealed class RecordingStarter(RealWorld owner) : ILoadThreadStarter
        {
            public ILoadThread Start(string name, Action onStart, Func<bool> step, Action<Exception> onFault)
            {
                var handle = new RealLoadThreadStarter().Start(name, onStart, step, onFault);
                lock (owner._handles)
                {
                    owner._handles.Add(handle);
                }

                return handle;
            }
        }
    }
}
