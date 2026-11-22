using PcMieru.Checks.Load;
using PcMieru.Checks.Tests.Run;
using PcMieru.Checks.Tests.SystemInfo;

namespace PcMieru.Checks.Tests.Load;

/// <summary>
/// The CPU check end to end (DESIGN-A §3.2, §4.0, §6.2, §14.2): a fake session in place of the machine (no thread, no load, no counter), the run
/// framework in between, and the history store on a fresh temp folder. The clock of the load is moved by hand, so a run of 300 seconds takes
/// no real time. Nothing here loads the machine, reads a counter or touches the user's folders.
/// </summary>
public sealed class CpuCheckProcedureTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly CheckRequest Default = new(CheckKind.Cpu);

    private static Task<CheckRun> Run(RunRig rig, CheckRequest? request = null, IProgress<CheckProgress>? progress = null, CancellationToken ct = default) =>
        rig.RunAsync(request ?? Default, progress, ct).WaitAsync(Patience);

    private static CheckItem Row(CheckRun run, string id, string? subject = null) =>
        run.Result.Items.Single(i => i.Id == id && i.Subject?.Id == subject);

    // ---- a healthy machine ----

    [Fact]
    public async Task A_healthy_run_gives_a_valid_completed_cpu_result_whose_verdict_is_ok()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2));
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Equal(CheckKind.Cpu, run.Result.Kind);
        Assert.Equal(Kit.Id(1), run.Result.RunId);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal(CheckVerdict.Ok, run.Result.Verdict); // §4.0: every judged row is ok, and there is no temperature row to be missing
        Assert.Equal(0, run.Result.Counts.Caution);
        Assert.Equal(0, run.Result.Counts.Check);
        Assert.Equal(0, run.Result.Counts.Unavailable);
        Assert.Equal(run.Result.Items.Count, run.Result.Counts.Total);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task A_healthy_run_has_the_rows_of_the_cpu_check_in_order()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2));
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(
            [
                "cpu.duration", "cpu.chunks", "cpu.compute_errors", "cpu.throughput", "cpu.steadiness", "cpu.variation",
                "cpu.logical_throughput", "cpu.logical_throughput", "cpu.peer_ratio", "cpu.peer_ratio",
                "cpu.effective_clock", "cpu.utilization", "cpu.package_power",
            ],
            run.Result.Items.Select(static i => i.Id));
        Assert.Equal(["cpu-1", "cpu-2"], run.Result.Items.Where(static i => i.Id == "cpu.peer_ratio").Select(static i => i.Subject!.Id));
    }

    [Fact]
    public async Task A_healthy_run_measures_what_the_fake_world_did()
    {
        // Two CPUs, 1002 chunks per wait of 1.002 seconds: a thousand chunks a second each, for the 30 seconds that were asked.
        var session = new FakeCpuSession(Cpus.Uniform(2));
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(30.06, Rows.Number(Row(run, "cpu.duration")), 3);
        Assert.Equal(2 * 30 * 1002, Rows.One(run.Result.Items, "cpu.chunks").Value!.AsInt);
        Assert.Equal(CheckValue.Int(0), Row(run, "cpu.compute_errors").Value);
        Assert.Equal(ItemVerdict.Ok, Row(run, "cpu.compute_errors").Verdict);
        Assert.Equal(2000, Rows.Number(Row(run, "cpu.throughput")), 3);
        Assert.Equal(100, Rows.Number(Row(run, "cpu.steadiness")), 3);
        Assert.Equal(0, Rows.Number(Row(run, "cpu.variation")), 3);
        Assert.Equal(1000, Rows.Number(Row(run, "cpu.logical_throughput", "cpu-1")), 3);
        Assert.Equal(100, Rows.Number(Row(run, "cpu.peer_ratio", "cpu-2")), 3);
        Assert.Equal(3500, Rows.Number(Row(run, "cpu.effective_clock")), 3);
        Assert.Equal(ItemVerdict.Ok, Row(run, "cpu.effective_clock").Verdict);
        Assert.Equal(100, Rows.Number(Row(run, "cpu.utilization")), 3);
        Assert.Equal(65, Rows.Number(Row(run, "cpu.package_power")), 3);
        Assert.Equal(ItemVerdict.Info, Row(run, "cpu.package_power").Verdict);
        Assert.Equal(30, session.Waits);
        Assert.Equal(30, session.Samples);
    }

    [Fact]
    public async Task The_temperature_is_not_a_row_and_the_note_says_so()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2));
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await Run(rig);

        Assert.DoesNotContain(run.Result.Items, static i => i.Id.Contains("temperature", StringComparison.Ordinal));
        Assert.Equal(CheckNotes.CpuTemperatureNotMeasured, Assert.Single(run.Result.Notes));
    }

    [Fact]
    public async Task The_result_is_saved_and_reads_back_byte_for_byte()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2));
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(CheckSaveStatus.Saved, run.Save.Status);
        Assert.Equal([Kit.NameOf(run.Result)], Kit.Names(rig.HistoryRoot));
        Assert.Equal([run.Result.RunId], rig.History.Saved.Select(static r => r.RunId));
        var back = await rig.ReadBackAsync(run.Result.RunId);
        Assert.NotNull(back);
        Assert.Equal(Kit.Bytes(run.Result), Kit.Bytes(back!));
        Assert.Equal(CheckKind.Cpu, back!.Kind);
        Assert.Equal(CheckNotes.CpuTemperatureNotMeasured, Assert.Single(back.Notes));
        Assert.Empty(CheckResultValidator.Validate(back));
    }

    [Fact]
    public async Task One_logical_processor_has_no_peer_rows_and_still_passes_the_validator()
    {
        var session = new FakeCpuSession(Cpus.Uniform(1));
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await Run(rig);

        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.DoesNotContain(run.Result.Items, static i => i.Id == "cpu.peer_ratio");
        Assert.Single(run.Result.Items, static i => i.Id == "cpu.logical_throughput");
        Assert.Equal(CheckVerdict.Ok, run.Result.Verdict);
    }

    // ---- the duration ----

    [Fact]
    public async Task A_request_without_a_duration_is_thirty_seconds_of_load()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2));
        using var rig = new RunRig(cpuSessionFactory: () => session);
        var log = new ProgressLog();

        await Run(rig, new CheckRequest(CheckKind.Cpu), log);

        Assert.Equal(30, session.Waits);
        Assert.Equal(30, Assert.Single(log.Items, static p => p.Stage == CheckStage.Loading && p.RecentRate is null).TargetSeconds);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(300)]
    public async Task The_requested_duration_is_that_many_seconds_of_load_and_the_target_of_the_progress(int seconds)
    {
        var session = new FakeCpuSession(Cpus.Uniform(2));
        using var rig = new RunRig(cpuSessionFactory: () => session);
        var log = new ProgressLog();

        var run = await Run(rig, new CheckRequest(CheckKind.Cpu, TimeSpan.FromSeconds(seconds)), log);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Equal(seconds, session.Waits);
        Assert.Equal(seconds * 1.002, Rows.Number(Row(run, "cpu.duration")), 3);
        Assert.Equal((long)2 * seconds * 1002, Row(run, "cpu.chunks").Value!.AsInt);
        Assert.All(log.Items.Where(static p => p.Stage == CheckStage.Loading), p => Assert.Equal(seconds, p.TargetSeconds));
        Assert.Empty(CheckResultValidator.Validate(run.Result));
    }

    [Fact]
    public async Task A_duration_that_is_not_allowed_is_refused_before_anything_starts()
    {
        var made = 0;
        using var rig = new RunRig(cpuSessionFactory: () =>
        {
            Interlocked.Increment(ref made);
            return new FakeCpuSession(Cpus.Uniform(2));
        });

        await Assert.ThrowsAnyAsync<ArgumentException>(() => rig.RunAsync(new CheckRequest(CheckKind.Cpu, TimeSpan.FromSeconds(45))));

        Assert.Equal(0, made);
    }

    // ---- a stop ----

    [Fact]
    public async Task A_stop_in_the_middle_gives_a_stopped_partial_result_that_is_saved()
    {
        using var cts = new CancellationTokenSource();
        var session = new FakeCpuSession(Cpus.Uniform(2)) { OnWait = n => { if (n == 3) { cts.Cancel(); } } };
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await Run(rig, ct: cts.Token);

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal(CheckNotes.CpuTemperatureNotMeasured, Assert.Single(run.Result.Notes));
        Assert.Equal([run.Result.RunId], rig.History.Saved.Select(static r => r.RunId));
        Assert.Equal(CheckOutcome.Stopped, rig.History.Saved.Single().Outcome);

        // The two seconds that were done are kept; nothing that rests on the whole run is ok.
        Assert.Equal(2 * 2 * 1002, Row(run, "cpu.chunks").Value!.AsInt);
        Assert.Equal(CheckReason.Partial, Row(run, "cpu.chunks").Reason);
        Assert.DoesNotContain(run.Result.Items, static i => i.Verdict == ItemVerdict.Ok);
        Assert.NotEqual(CheckVerdict.Ok, run.Result.Verdict);
        Assert.Equal(CheckReason.Partial, Row(run, "cpu.steadiness").Reason); // two seconds are too short to say anything
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task A_stop_joins_every_load_thread_before_the_run_ends_and_none_is_left()
    {
        using var cts = new CancellationTokenSource();
        var session = new FakeCpuSession(Cpus.Uniform(4)) { OnWait = n => { if (n == 2) { cts.Cancel(); } } };
        using var rig = new RunRig(cpuSessionFactory: () => session);

        await Run(rig, ct: cts.Token);

        Assert.Equal(4, session.Threads.Count);
        Assert.All(session.Threads, static t =>
        {
            Assert.True(t.Ended);
            Assert.Equal(1, t.Joins);
            Assert.Equal(0, t.StepsDuringJoin); // the stop flag was set before the join: no chunk was started after it
        });
    }

    [Fact]
    public async Task A_compute_error_that_was_found_before_the_stop_stays_a_finding()
    {
        using var cts = new CancellationTokenSource();
        var session = new FakeCpuSession(Cpus.Uniform(2))
        {
            WrongAnswer = static n => n == 100, // one wrong answer, long after the 16 known ones
            OnWait = n => { if (n == 3) { cts.Cancel(); } },
        };
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await Run(rig, ct: cts.Token);

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        var errors = Row(run, "cpu.compute_errors");
        Assert.Equal(CheckValue.Int(1), errors.Value);
        Assert.Equal(ItemVerdict.Check, errors.Verdict);
        Assert.Equal(CheckEvidenceCode.ComputeMismatch, errors.Evidence!.Code);
        Assert.Equal(CheckVerdict.Check, run.Result.Verdict);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.True(run.IsSaved);
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_never_starts_a_run_and_makes_no_session()
    {
        // The framework's rule (before a run exists there is nothing to save): the call is cancelled, no session is made, no file is written.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var made = 0;
        using var rig = new RunRig(cpuSessionFactory: () =>
        {
            Interlocked.Increment(ref made);
            return new FakeCpuSession(Cpus.Uniform(2));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.RunAsync(Default, null, cts.Token));

        Assert.Equal(0, made);
        Assert.Empty(rig.History.Saved);
    }

    [Fact]
    public async Task A_stop_at_the_very_first_wait_is_a_stopped_result_with_no_second_of_load_and_every_thread_joined()
    {
        using var cts = new CancellationTokenSource();
        var session = new FakeCpuSession(Cpus.Uniform(2)) { OnWait = n => { if (n == 1) { cts.Cancel(); } } };
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await Run(rig, ct: cts.Token);

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.True(run.IsSaved);
        Assert.Equal(CheckNotes.CpuTemperatureNotMeasured, Assert.Single(run.Result.Notes));
        Assert.Equal(0, Row(run, "cpu.chunks").Value!.AsInt);
        Assert.Equal(CheckVerdict.Unavailable, run.Result.Verdict);
        Assert.All(session.Threads, static t => Assert.True(t.Ended));
    }

    // ---- a failure ----

    [Fact]
    public async Task A_chunk_that_throws_fails_the_run_with_an_internal_error_and_the_failed_run_is_saved()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2)) { ChunkThrows = static n => n == 40 };
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.InternalError, run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal(CheckNotes.CpuTemperatureNotMeasured, Assert.Single(run.Result.Notes)); // a failed run keeps the note
        Assert.All(session.Threads, static t => Assert.True(t.Ended));
        Assert.True(session.Disposed);
        Assert.Equal(CheckOutcome.Failed, rig.History.Saved.Single().Outcome);
    }

    [Fact]
    public async Task A_session_that_cannot_be_made_fails_the_run_the_same_way_and_the_failed_run_is_saved()
    {
        using var rig = new RunRig(cpuSessionFactory: static () => throw new InvalidOperationException("no session"));

        var run = await Run(rig);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.InternalError, run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal(CheckNotes.CpuTemperatureNotMeasured, Assert.Single(run.Result.Notes));
    }

    [Fact]
    public async Task A_computer_with_no_logical_processor_fails_the_run_and_does_not_hang()
    {
        var session = new FakeCpuSession([]);
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.InternalError, run.FailureCode);
        Assert.True(session.Disposed);
    }

    // ---- a compute error without a stop ----

    [Fact]
    public async Task A_wrong_answer_in_a_whole_run_is_the_heaviest_finding_and_the_verdict_is_check()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2)) { WrongAnswer = static n => n % 5000 == 0 };
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        var errors = Row(run, "cpu.compute_errors");
        Assert.Equal(ItemVerdict.Check, errors.Verdict);
        Assert.Equal(CheckEvidenceCode.ComputeMismatch, errors.Evidence!.Code);
        Assert.True(errors.Value!.AsInt >= 1);
        Assert.Null(errors.Reason);
        Assert.Equal(CheckVerdict.Check, run.Result.Verdict);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
    }

    [Fact]
    public async Task A_throttled_run_is_judged_by_the_steadiness_and_the_clock_through_the_whole_framework()
    {
        // From the 13th second every CPU does 60 % of the work and the clock sags the same way.
        var session = new FakeCpuSession(Cpus.Uniform(2))
        {
            ChunksPerWait = static (wait, _) => wait <= 12 ? 1002 : 601,
            Sample = static n => PdhSamples.Healthy(mhz: n <= 12 ? 3500 : 2100),
        };
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(ItemVerdict.Check, Row(run, "cpu.steadiness").Verdict);
        Assert.Equal(ItemVerdict.Caution, Row(run, "cpu.effective_clock").Verdict);
        Assert.Equal(CheckVerdict.Check, run.Result.Verdict);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
    }

    // ---- the progress ----

    [Fact]
    public async Task The_stages_come_in_order_and_the_loading_stage_names_the_target()
    {
        var session = new FakeCpuSession(Cpus.Uniform(2));
        using var rig = new RunRig(cpuSessionFactory: () => session);
        var log = new ProgressLog();

        await Run(rig, progress: log);

        Assert.Equal(
            [CheckStage.Starting, CheckStage.Collecting, CheckStage.Loading, CheckStage.Judging, CheckStage.Saving, CheckStage.Done],
            log.Stages.Distinct());
        Assert.Equal(CheckStage.Done, log.Stages[^1]);
    }

    [Fact]
    public async Task Progress_is_sent_at_most_once_a_second_and_carries_the_chunks_per_second()
    {
        // The service clock (the one the rate limit reads) moves 0.4 s for every second of the load: the readings come at 0.4, 0.8, 1.2 ...
        // and one goes out when a whole second has passed since the last one that did: seconds 1, 4, 7 ... 28 of the load, ten in all.
        var serviceClock = new ManualTimeProvider();
        var session = new FakeCpuSession(Cpus.Uniform(2)) { AfterAdvance = _ => serviceClock.Advance(TimeSpan.FromMilliseconds(400)) };
        using var rig = new RunRig(clock: serviceClock, cpuSessionFactory: () => session);
        var log = new ProgressLog();

        var run = await Run(rig, progress: log);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        var readings = log.Items.Where(static p => p.RecentRate is not null).ToList();
        Assert.Equal(10, readings.Count);
        Assert.All(readings, p =>
        {
            Assert.Equal(CheckStage.Loading, p.Stage);
            Assert.Equal("chunks/s", p.RateUnit);
            Assert.Equal(30, p.TargetSeconds);
            Assert.Equal(2000, p.RecentRate!.Value, 3);
        });
        Assert.Equal(readings.OrderBy(static p => p.ElapsedSeconds).Select(static p => p.ElapsedSeconds), readings.Select(static p => p.ElapsedSeconds));
    }

    [Fact]
    public async Task The_stage_notifications_are_not_load_readings()
    {
        var serviceClock = new ManualTimeProvider();
        var session = new FakeCpuSession(Cpus.Uniform(2)) { AfterAdvance = _ => serviceClock.Advance(TimeSpan.FromMilliseconds(400)) };
        using var rig = new RunRig(clock: serviceClock, cpuSessionFactory: () => session);
        var log = new ProgressLog();

        await Run(rig, progress: log);

        var stages = log.Items.Where(static p => p.RecentRate is null).ToList();
        Assert.All(stages, static p => Assert.Null(p.RateUnit));
        Assert.Equal(
            [CheckStage.Starting, CheckStage.Collecting, CheckStage.Loading, CheckStage.Judging, CheckStage.Saving, CheckStage.Done],
            stages.Select(static p => p.Stage));
    }

    [Fact]
    public async Task A_progress_handler_that_blocks_does_not_hold_up_the_stop_or_the_seal()
    {
        using var release = new ManualResetEventSlim();
        var session = new FakeCpuSession(Cpus.Uniform(2));
        using var rig = new RunRig(cpuSessionFactory: () => session);
        var log = new ProgressLog { OnReport = _ => release.Wait(TimeSpan.FromSeconds(20)) };

        try
        {
            var run = await rig.RunAsync(Default, log).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
            Assert.True(run.IsSaved);
            Assert.Empty(CheckResultValidator.Validate(run.Result));
            Assert.Equal(30, session.Waits);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task A_progress_handler_that_blocks_does_not_hold_up_a_stop()
    {
        using var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var session = new FakeCpuSession(Cpus.Uniform(2)) { OnWait = n => { if (n == 4) { cts.Cancel(); } } };
        using var rig = new RunRig(cpuSessionFactory: () => session);
        var log = new ProgressLog { OnReport = _ => release.Wait(TimeSpan.FromSeconds(20)) };

        try
        {
            var run = await rig.RunAsync(Default, log, cts.Token).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
            Assert.True(run.IsSaved);
            Assert.All(session.Threads, static t => Assert.True(t.Ended));
        }
        finally
        {
            release.Set();
        }
    }

    // ---- the caller's thread ----

    [Fact]
    public async Task Nothing_that_blocks_runs_on_the_callers_thread_before_the_first_await()
    {
        // The session's recipe blocks (stands for the PDH query opening). If the procedure ran it on the caller's thread, RunAsync itself would
        // not return until the gate opened; it must return at once with a task that is not done.
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var factoryThread = -1;
        var session = new FakeCpuSession(Cpus.Uniform(2));
        using var rig = new RunRig(cpuSessionFactory: () =>
        {
            factoryThread = Environment.CurrentManagedThreadId;
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(20));
            return session;
        });
        var callerThread = Environment.CurrentManagedThreadId;

        var started = System.Diagnostics.Stopwatch.StartNew();
        var task = rig.RunAsync(Default);
        var returnedAfter = started.Elapsed;

        try
        {
            Assert.True(returnedAfter < TimeSpan.FromSeconds(5), $"RunAsync waited {returnedAfter.TotalSeconds:0.0} s for the session");
            Assert.False(task.IsCompleted);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the session recipe was never asked");
            Assert.NotEqual(callerThread, factoryThread);
        }
        finally
        {
            gate.Set();
        }

        var run = await task.WaitAsync(Patience);
        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
    }

    [Fact]
    public async Task The_load_does_not_take_a_thread_from_the_pool()
    {
        // The recipe runs on a long-running thread of its own: a thread-pool thread is never held for the minutes of the load.
        var onPool = true;
        var session = new FakeCpuSession(Cpus.Uniform(2));
        using var rig = new RunRig(cpuSessionFactory: () =>
        {
            onPool = Thread.CurrentThread.IsThreadPoolThread;
            return session;
        });

        await Run(rig);

        Assert.False(onPool);
    }

    // ---- the framework around it ----

    [Fact]
    public async Task Two_runs_in_a_row_make_two_sessions_and_two_files()
    {
        var made = new List<FakeCpuSession>();
        using var rig = new RunRig(cpuSessionFactory: () =>
        {
            var session = new FakeCpuSession(Cpus.Uniform(2));
            made.Add(session);
            return session;
        });

        var first = await Run(rig);
        var second = await Run(rig);

        Assert.Equal(2, made.Count);
        Assert.All(made, static s => Assert.True(s.Disposed));
        Assert.Equal(2, Kit.Names(rig.HistoryRoot).Length);
        Assert.NotEqual(first.Result.RunId, second.Result.RunId);
    }
}
