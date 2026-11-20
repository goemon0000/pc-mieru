using PcMieru.Checks.Load;
using PcMieru.Checks.Tests.SystemInfo;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Collectors;

namespace PcMieru.Checks.Tests.Load;

/// <summary>
/// The CPU load (DESIGN-A §6.2) over a session in memory: one thread per logical processor, the pin, the known answers, a stop at a chunk
/// boundary, the join before anything is reported, the readings once a second. A few tests use real threads, with a chunk that sleeps
/// instead of computing, to show that the real starter does what the fake one is trusted to.
/// </summary>
public sealed class CpuLoadTestTests
{
    private static CpuLoadResult Run(FakeCpuSession session, int seconds, Action<double, double>? onTick = null, CancellationToken ct = default) =>
        CpuLoadTest.Run(session, TimeSpan.FromSeconds(seconds), onTick, ct);

    // ---- the chunk (ports of the old CpuLoadTestTests) ----

    [Fact]
    public void Chunk_is_deterministic()
    {
        Assert.Equal(CpuLoadTest.Chunk(123), CpuLoadTest.Chunk(123));
        Assert.NotEqual(CpuLoadTest.Chunk(123), CpuLoadTest.Chunk(124));
    }

    [Fact]
    public void Chunk_gives_another_answer_for_another_number_of_iterations()
    {
        Assert.NotEqual(CpuLoadTest.Chunk(123, 100), CpuLoadTest.Chunk(123, 101));
        Assert.Equal(CpuLoadTest.Chunk(123, 100), CpuLoadTest.Chunk(123, 100));
    }

    [Fact]
    public void The_real_chunk_work_is_the_chunk_of_the_test()
    {
        Assert.Equal(CpuLoadTest.Chunk(CpuLoadTest.SeedOf(3)), new ComputeChunkWork().Chunk(CpuLoadTest.SeedOf(3)));
    }

    [Fact]
    public void The_16_seeds_are_different_and_the_old_ones()
    {
        var seeds = Enumerable.Range(0, CpuLoadTest.SeedCount).Select(CpuLoadTest.SeedOf).ToList();

        Assert.Equal(16, seeds.Distinct().Count());
        Assert.Equal(99UL, seeds[0]);
        Assert.Equal((5UL * 0x1234567UL) + 99, seeds[5]);
    }

    // ---- one thread per logical CPU, pinned by group and number ----

    [Fact]
    public void There_is_one_named_thread_per_logical_cpu_and_each_is_pinned_by_its_group_and_number()
    {
        IReadOnlyList<LogicalCpu> cpus = [new(0, 0, 0, 0, 0), new(1, 0, 1, 0, 0), new(2, 1, 0, 1, 0)];
        var session = new FakeCpuSession(cpus);

        var r = Run(session, 3);

        Assert.Equal(3, session.Threads.Count);
        Assert.Equal(["PcMieru.CpuLoad.0", "PcMieru.CpuLoad.1", "PcMieru.CpuLoad.2"], session.Threads.Select(static t => t.Name));
        Assert.Equal(cpus, session.PinCalls);
        Assert.Equal(cpus.Select(static c => (c.Group, c.Number)), session.PinCalls.Select(static c => (c.Group, c.Number)));
        Assert.All(r.Pins, static p => Assert.Equal(PinOutcome.Pinned, p));
        Assert.Equal(3, r.Pins.Count);
    }

    [Fact]
    public void A_pin_that_windows_refuses_is_kept_as_failed_and_the_load_still_runs_on_that_thread()
    {
        var session = new FakeCpuSession(Cpus.Uniform(3)) { PinAccepts = static c => c.Index != 1 };

        var r = Run(session, 3);

        Assert.Equal([PinOutcome.Pinned, PinOutcome.Failed, PinOutcome.Pinned], r.Pins);
        Assert.All(r.PerCpuAverage, static v => Assert.True(v > 0));
    }

    [Fact]
    public void A_pin_that_throws_is_an_unknown_outcome_and_does_not_end_the_run()
    {
        var session = new FakeCpuSession(Cpus.Uniform(3)) { PinThrows = static c => c.Index == 2 };

        var r = Run(session, 3);

        Assert.Equal([PinOutcome.Pinned, PinOutcome.Pinned, PinOutcome.Unknown], r.Pins);
        Assert.False(r.Stopped);
        Assert.Equal(3, r.Ticks.Count);
    }

    [Fact]
    public void A_thread_that_never_ran_its_start_action_leaves_the_pin_unknown()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2)) { ThreadNeverStarts = static i => i == 0 };

        var r = Run(session, 3);

        Assert.Equal([PinOutcome.Unknown, PinOutcome.Pinned], r.Pins);
        Assert.Single(session.PinCalls);
    }

    // ---- the known answers ----

    [Fact]
    public void The_known_answers_are_computed_first_on_the_calling_thread_before_any_thread_starts()
    {
        var session = new FakeCpuSession(Cpus.Uniform(3));

        Run(session, 3);

        Assert.Equal([16, 16, 16], session.ChunkCallsAtThreadStart);
        Assert.Equal(16, session.KnownAnswerThreads.Count);
        Assert.All(session.KnownAnswerThreads, static id => Assert.Equal(Environment.CurrentManagedThreadId, id));
        Assert.Equal(Enumerable.Range(0, 16).Select(CpuLoadTest.SeedOf), session.Seeds.Take(16));
    }

    [Fact]
    public void Every_thread_cycles_through_the_16_seeds_from_its_own_start()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2)) { ChunksPerWait = static (_, _) => 20 };

        Run(session, 1);

        var thread0 = session.Seeds.Skip(16).Take(20).ToList();
        var thread1 = session.Seeds.Skip(36).Take(20).ToList();
        Assert.Equal(Enumerable.Range(0, 20).Select(i => CpuLoadTest.SeedOf(i % 16)), thread0);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => CpuLoadTest.SeedOf((i + 1) % 16)), thread1);
    }

    [Fact]
    public void A_run_with_no_wrong_answer_has_no_compute_error_and_counts_every_chunk_that_was_compared()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2));

        var r = Run(session, 3);

        Assert.Equal(0, r.ComputeErrors);
        Assert.Equal(2 * 3 * 1002, r.Chunks);
        Assert.Equal(16 + (2 * 3 * 1002), session.ChunkCalls); // the 16 answers are not counted as load
    }

    [Fact]
    public void A_wrong_answer_is_a_compute_error_and_every_wrong_answer_is_counted()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2)) { WrongAnswer = static n => n is 20 or 25 or 3000 };

        var r = Run(session, 3);

        Assert.Equal(3, r.ComputeErrors);
        Assert.Equal(3, r.Ticks[^1].ComputeErrors);
        Assert.False(r.Stopped);
    }

    [Fact]
    public void A_wrong_answer_in_the_known_answers_would_make_every_later_chunk_a_mismatch_so_the_answers_are_not_judged_at_all()
    {
        // The first 16 calls are the truth. A call number that falls among them is the truth itself being a little different; every thread
        // then disagrees with it on the same seed. The load reports that as errors (it cannot know which side is right).
        var session = new FakeCpuSession(Cpus.Uniform(1)) { WrongAnswer = static n => n == 1 };

        var r = Run(session, 3);

        Assert.True(r.ComputeErrors > 0);
    }

    // ---- the seconds ----

    [Fact]
    public void Duration_ends_the_test_and_reports_each_second()
    {
        var reported = new List<(double Elapsed, double Total)>();
        var session = new FakeCpuSession(Cpus.Uniform(2));

        var r = Run(session, 3, (e, total) => reported.Add((e, total)));

        Assert.False(r.Stopped);
        Assert.Equal(3, r.Ticks.Count);
        Assert.Equal(3, reported.Count);
        Assert.All(r.Ticks, static t => Assert.Equal(2, t.PerCpu.Length));
        Assert.All(r.PerCpuAverage, static v => Assert.True(v > 0));
        Assert.Null(r.Steadiness); // 3 seconds are not judged
        Assert.Equal([1, 2, 3], r.Ticks.Select(static t => t.Second));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    public void A_run_has_exactly_as_many_readings_as_seconds_and_the_readings_are_more_than_a_second_apart(int seconds)
    {
        var elapsed = new List<double>();
        var session = new FakeCpuSession(Cpus.Uniform(1)) { ChunksPerWait = static (_, _) => 1 };

        var r = Run(session, seconds, (e, _) => elapsed.Add(e));

        Assert.Equal(seconds, r.Ticks.Count);
        Assert.Equal(seconds, elapsed.Count);
        Assert.Equal(seconds, session.Waits);
        Assert.All(session.WaitLengths, static w => Assert.Equal(CpuLoadTest.TickInterval, w));
        for (var i = 1; i < elapsed.Count; i++)
        {
            Assert.True(elapsed[i] - elapsed[i - 1] > 1.0, $"readings {i} and {i + 1} are {elapsed[i] - elapsed[i - 1]} s apart");
        }
    }

    [Fact]
    public void The_pace_of_a_cpu_is_its_chunks_over_the_time_between_two_readings()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2)) { ChunksPerWait = static (_, cpu) => cpu == 0 ? 1002 : 501 };

        var r = Run(session, 3);

        Assert.All(r.Ticks, t =>
        {
            Assert.Equal(1000, t.PerCpu[0], 6);
            Assert.Equal(500, t.PerCpu[1], 6);
            Assert.Equal(1500, t.Total, 6);
        });
    }

    [Fact]
    public void The_per_cpu_average_leaves_out_the_first_two_seconds_when_there_are_more_than_four()
    {
        static int Slow(int wait, int _) => wait <= 2 ? 501 : 1002;

        var four = Run(new FakeCpuSession(Cpus.Uniform(1)) { ChunksPerWait = Slow }, 4);
        var five = Run(new FakeCpuSession(Cpus.Uniform(1)) { ChunksPerWait = Slow }, 5);

        Assert.Equal(750, four.PerCpuAverage[0], 6); // 4 seconds: all of them count
        Assert.Equal(1000, five.PerCpuAverage[0], 6); // 5 seconds: the two slow ones are the ramp-up
    }

    [Fact]
    public void The_steadiness_is_over_the_totals_and_needs_eight_seconds()
    {
        var seven = Run(new FakeCpuSession(Cpus.Uniform(1)), 7);
        var eight = Run(new FakeCpuSession(Cpus.Uniform(1)), 8);

        Assert.Null(seven.Steadiness);
        Assert.NotNull(eight.Steadiness);
        Assert.Equal(100, eight.Steadiness!.Score, 6);
    }

    [Fact]
    public void The_clock_steadiness_is_computed_only_when_every_second_has_a_clock()
    {
        var all = Run(new FakeCpuSession(Cpus.Uniform(1)), 30);
        var oneMissing = Run(new FakeCpuSession(Cpus.Uniform(1)) { Sample = static n => n == 5 ? PdhSamples.Nothing() : PdhSamples.Healthy() }, 30);

        Assert.NotNull(all.ClockSteadiness);
        Assert.Null(oneMissing.ClockSteadiness);
        Assert.Equal(30, oneMissing.Ticks.Count);
        Assert.Null(oneMissing.Ticks[4].Sample.EffectiveMhz);
    }

    [Fact]
    public void A_pdh_that_throws_is_a_missing_sample_and_not_a_failed_second()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2)) { SampleThrows = static _ => true };

        var r = Run(session, 10);

        Assert.False(r.Stopped);
        Assert.Equal(10, r.Ticks.Count);
        Assert.All(r.Ticks, static t => Assert.Null(t.Sample.EffectiveMhz));
        Assert.All(r.Ticks, static t => Assert.Null(t.Sample.UtilizationPercent));
        Assert.Null(r.ClockSteadiness);
    }

    [Fact]
    public void The_progress_callback_gets_the_elapsed_seconds_and_the_total_pace_and_comes_before_the_next_wait()
    {
        var calls = new List<(double Elapsed, double Total, int WaitsSoFar)>();
        var session = new FakeCpuSession(Cpus.Uniform(2));

        Run(session, 3, (e, total) => calls.Add((e, total, session.Waits)));

        Assert.Equal([1, 2, 3], calls.Select(static c => c.WaitsSoFar));
        Assert.All(calls, static c => Assert.Equal(2000, c.Total, 6));
        Assert.Equal(1.002, calls[0].Elapsed, 3);
        Assert.Equal(3.006, calls[2].Elapsed, 3);
    }

    // ---- stop and join ----

    [Fact]
    public void Stop_works_at_once_and_every_thread_is_joined_before_the_result_exists()
    {
        using var cts = new CancellationTokenSource();
        var session = new FakeCpuSession(Cpus.Uniform(4)) { OnWait = n => { if (n == 3) { cts.Cancel(); } } };

        var r = Run(session, 300, ct: cts.Token);

        Assert.True(r.Stopped);
        Assert.Equal(2, r.Ticks.Count); // the cut wait gave no reading
        Assert.Equal(3, session.Waits);
        Assert.All(session.Threads, static t => Assert.Equal(1, t.Joins));
        Assert.All(session.Threads, static t => Assert.False(t.IsAlive));
        Assert.All(session.Threads, static t => Assert.Equal(0, t.StepsDuringJoin)); // the stop flag was set before the join: no chunk after it
        Assert.Equal(0, r.ComputeErrors);
        Assert.True(r.Chunks > 0);
    }

    [Fact]
    public void A_token_that_was_cancelled_before_the_run_gives_a_stopped_result_with_no_reading_and_no_thread_left()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var session = new FakeCpuSession(Cpus.Uniform(2));

        var r = Run(session, 300, ct: cts.Token);

        Assert.True(r.Stopped);
        Assert.Empty(r.Ticks);
        Assert.Equal(0, r.Chunks);
        Assert.All(session.Threads, static t => Assert.False(t.IsAlive));
        Assert.All(session.Threads, static t => Assert.Equal(1, t.Joins));
    }

    [Fact]
    public void A_stop_keeps_the_compute_error_that_was_found_before_it()
    {
        using var cts = new CancellationTokenSource();
        var session = new FakeCpuSession(Cpus.Uniform(2)) { WrongAnswer = static n => n == 400, OnWait = n => { if (n == 4) { cts.Cancel(); } } };

        var r = Run(session, 30, ct: cts.Token);

        Assert.True(r.Stopped);
        Assert.Equal(1, r.ComputeErrors);
        Assert.Equal(1, r.Ticks[^1].ComputeErrors);
    }

    [Fact]
    public void A_stop_between_the_wait_and_the_reading_is_a_stop_too()
    {
        using var cts = new CancellationTokenSource();
        var session = new FakeCpuSession(Cpus.Uniform(1)) { AfterAdvance = _ => cts.Cancel() };

        var r = Run(session, 30, ct: cts.Token);

        Assert.True(r.Stopped);
        Assert.Empty(r.Ticks);
        Assert.All(session.Threads, static t => Assert.False(t.IsAlive));
    }

    [Fact]
    public void A_chunk_that_throws_fails_the_run_at_the_next_reading_and_still_joins_every_thread()
    {
        var session = new FakeCpuSession(Cpus.Uniform(3)) { ChunkThrows = static n => n == 100 };

        var failure = Assert.Throws<CheckRunFailedException>(() => Run(session, 30));

        Assert.Equal(CheckRunFailureCodes.InternalError, failure.Code);
        Assert.Equal(1, session.Waits); // not a minute of load for a check that is already broken
        Assert.All(session.Threads, static t => Assert.Equal(1, t.Joins));
        Assert.All(session.Threads, static t => Assert.False(t.IsAlive));
    }

    [Fact]
    public void A_thread_that_cannot_be_started_stops_and_joins_the_ones_that_were()
    {
        var session = new FakeCpuSession(Cpus.Uniform(4)) { ThreadStartFails = static i => i == 2 };

        Assert.Throws<InvalidOperationException>(() => Run(session, 30));

        Assert.Equal(2, session.Threads.Count);
        Assert.All(session.Threads, static t => Assert.Equal(1, t.Joins));
        Assert.All(session.Threads, static t => Assert.False(t.IsAlive));
    }

    [Fact]
    public void A_machine_with_no_logical_processor_fails_the_run()
    {
        var session = new FakeCpuSession([]);

        var failure = Assert.Throws<CheckRunFailedException>(() => Run(session, 30));

        Assert.Equal(CheckRunFailureCodes.InternalError, failure.Code);
    }

    [Fact]
    public void The_session_is_required()
    {
        Assert.Throws<ArgumentNullException>(() => CpuLoadTest.Run(null!, TimeSpan.FromSeconds(30), null, CancellationToken.None));
    }

    // ---- real threads, a chunk that sleeps ----

    [Fact]
    public void The_real_starter_runs_a_background_thread_of_below_normal_priority_with_its_name_and_starts_it_on_itself()
    {
        var starter = new RealLoadThreadStarter();
        string? name = null;
        ThreadPriority? priority = null;
        bool? background = null;
        int? startThread = null;
        var steps = 0;

        var thread = starter.Start(
            "PcMieru.CpuLoad.test",
            () =>
            {
                name = Thread.CurrentThread.Name;
                priority = Thread.CurrentThread.Priority;
                background = Thread.CurrentThread.IsBackground;
                startThread = Environment.CurrentManagedThreadId;
            },
            () => ++steps < 3,
            static _ => { });
        thread.Join();

        Assert.Equal("PcMieru.CpuLoad.test", name);
        Assert.Equal(ThreadPriority.BelowNormal, priority);
        Assert.True(background);
        Assert.NotEqual(Environment.CurrentManagedThreadId, startThread);
        Assert.Equal(3, steps);
        Assert.False(thread.IsAlive);
    }

    [Fact]
    public void A_real_stop_waits_for_the_chunk_in_progress_and_leaves_no_thread_alive()
    {
        using var cts = new CancellationTokenSource();
        // The stop comes when the ninth chunk has finished, not after a fixed time: a busy machine (the whole suite in parallel) may start
        // the BelowNormal threads late, and a time-based stop then found too few chunks.
        var session = new RealThreadSession(Cpus.Uniform(3)) { StopAfterChunks = 9, Stop = cts.Cancel };

        var r = CpuLoadTest.Run(session, TimeSpan.FromSeconds(300), null, cts.Token);

        Assert.True(r.Stopped);
        Assert.Equal(3, session.Handles.Count);
        Assert.All(session.Handles, static h => Assert.False(h.IsAlive));
        Assert.Equal(session.ChunksStarted, session.ChunksFinished); // no chunk was abandoned half way
        Assert.True(session.ChunksFinished >= 9, $"{session.ChunksFinished} chunks finished");
        Assert.Equal(0, r.ComputeErrors);
    }

    /// <summary>Real threads, the real clock and wait, a chunk that sleeps a little (no load), a pin that takes and no PDH.</summary>
    private sealed class RealThreadSession(IReadOnlyList<LogicalCpu> cpus) : ICpuTestSession
    {
        private readonly List<ILoadThread> _handles = [];
        private long _started;
        private long _finished;

        public IReadOnlyList<LogicalCpu> Cpus { get; } = cpus;

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

        public long StopAfterChunks { get; init; } = long.MaxValue;

        public Action? Stop { get; init; }

        public IChunkWork Work => new SleepyWork(this);

        public ICpuPin Pin { get; } = new AlwaysPin();

        public ILoadThreadStarter Threads => new RecordingStarter(this);

        public ILoadClock Clock { get; } = new RealLoadClock();

        public CpuPdhSample SamplePdh() => PdhSamples.Healthy();

        public void Dispose()
        {
        }

        private sealed class SleepyWork(RealThreadSession owner) : IChunkWork
        {
            public ulong Chunk(ulong seed)
            {
                Interlocked.Increment(ref owner._started);
                Thread.Sleep(2);
                if (Interlocked.Increment(ref owner._finished) == owner.StopAfterChunks)
                {
                    owner.Stop?.Invoke();
                }
                return FakeCpuSession.Answer(seed);
            }
        }

        private sealed class AlwaysPin : ICpuPin
        {
            public bool PinCurrentThread(LogicalCpu cpu) => true;
        }

        private sealed class RecordingStarter(RealThreadSession owner) : ILoadThreadStarter
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
