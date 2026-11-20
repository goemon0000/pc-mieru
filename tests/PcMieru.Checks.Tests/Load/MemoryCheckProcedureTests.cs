using System.Text.RegularExpressions;
using PcMieru.Checks.Load;
using PcMieru.Checks.Tests.Run;
using PcMieru.Checks.Tests.SystemInfo;

namespace PcMieru.Checks.Tests.Load;

/// <summary>
/// The memory check end to end (DESIGN-A §3.2, §4.0, §6.2, §14.3): a fake session in place of the machine (no thread, no real memory, no
/// GlobalMemoryStatusEx), the run framework in between, and the history store on a fresh temp folder. The clock of the load is moved by hand, so a
/// run of 300 seconds takes no real time and nothing here is timed. Nothing here borrows a byte of the machine's memory: only the RealMachine
/// class does.
/// <para>The world: <c>Planned(4)</c> borrows four blocks of 64 KiB and has four work threads; see <see cref="MemoryLoadTests"/>. A run of 30 s
/// is 120 waits, 180 passes and 30.012 s.</para>
/// </summary>
public sealed class MemoryCheckProcedureTests
{
    private const long Block = FakeMemorySession.SmallBlock;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly CheckRequest Default = new(CheckKind.Memory);

    private static Task<CheckRun> Run(RunRig rig, CheckRequest? request = null, IProgress<CheckProgress>? progress = null, CancellationToken ct = default) =>
        rig.RunAsync(request ?? Default, progress, ct).WaitAsync(Patience);

    private static CheckItem Row(CheckRun run, string id) => run.Result.Items.Single(i => i.Id == id);

    private static readonly string[] RowIds =
    [
        "memory.duration", "memory.total", "memory.available_at_start", "memory.borrowed", "memory.passes",
        "memory.verified", "memory.mismatches", "memory.write_rate", "memory.verify_rate",
    ];

    // ---- a healthy machine ----

    [Fact]
    public async Task A_healthy_run_gives_a_valid_completed_memory_result_whose_verdict_is_ok()
    {
        var session = FakeMemorySession.Planned(4);
        using var rig = new RunRig(memorySessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Equal(CheckKind.Memory, run.Result.Kind);
        Assert.Equal(Kit.Id(1), run.Result.RunId);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal(CheckVerdict.Ok, run.Result.Verdict);
        Assert.Equal(1, run.Result.Counts.Ok);
        Assert.Equal(8, run.Result.Counts.Info);
        Assert.Equal(0, run.Result.Counts.Caution);
        Assert.Equal(0, run.Result.Counts.Check);
        Assert.Equal(0, run.Result.Counts.Unavailable);
        Assert.Equal(run.Result.Items.Count, run.Result.Counts.Total);
        Assert.True(session.Disposed);
        MemoryEvents.AssertJoinedThenFreed(session);
    }

    [Fact]
    public async Task A_healthy_run_has_the_rows_of_the_memory_check_in_order_and_the_note_that_it_tested_only_what_it_borrowed()
    {
        var session = FakeMemorySession.Planned(4);
        using var rig = new RunRig(memorySessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(RowIds, run.Result.Items.Select(static i => i.Id));
        Assert.Equal([CheckNotes.MemoryBorrowedRegionOnly], run.Result.Notes);
    }

    [Fact]
    public async Task A_healthy_run_measures_what_the_fake_world_did()
    {
        var session = FakeMemorySession.Planned(4);
        using var rig = new RunRig(memorySessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(30.012, Rows.Number(Row(run, "memory.duration")), 3);
        Assert.Equal(CheckValue.Dec(8UL << 30), Row(run, "memory.total").Value);
        Assert.Equal(CheckValue.Dec((1UL << 30) + (4UL * Block) + (ulong)(Block / 2)), Row(run, "memory.available_at_start").Value);
        Assert.Equal(CheckValue.Dec((ulong)(4 * Block)), Row(run, "memory.borrowed").Value);
        Assert.Equal(180, Row(run, "memory.passes").Value!.AsInt);
        Assert.Equal(CheckValue.Dec((ulong)(180 * 4 * Block)), Row(run, "memory.verified").Value);
        Assert.Equal(CheckValue.Int(0), Row(run, "memory.mismatches").Value);
        Assert.Equal(ItemVerdict.Ok, Row(run, "memory.mismatches").Verdict);
        Assert.True(Rows.Number(Row(run, "memory.write_rate")) > 0);
        Assert.True(Rows.Number(Row(run, "memory.verify_rate")) > 0);
        Assert.Equal(CheckSource.Win32, Row(run, "memory.total").Source);
        Assert.Equal(CheckSource.Load, Row(run, "memory.verified").Source);
        Assert.Equal(120, session.Waits);
    }

    [Fact]
    public async Task The_result_is_saved_and_reads_back_byte_for_byte()
    {
        var session = FakeMemorySession.Planned(4);
        using var rig = new RunRig(memorySessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(CheckSaveStatus.Saved, run.Save.Status);
        Assert.Equal([Kit.NameOf(run.Result)], Kit.Names(rig.HistoryRoot));
        Assert.Equal([run.Result.RunId], rig.History.Saved.Select(static r => r.RunId));
        var back = await rig.ReadBackAsync(run.Result.RunId);
        Assert.NotNull(back);
        Assert.Equal(Kit.Bytes(run.Result), Kit.Bytes(back!));
        Assert.Equal(CheckKind.Memory, back!.Kind);
        Assert.Equal([CheckNotes.MemoryBorrowedRegionOnly], back.Notes);
        Assert.Empty(CheckResultValidator.Validate(back));
    }

    // ---- the duration ----

    [Fact]
    public async Task A_request_without_a_duration_is_thirty_seconds_of_load()
    {
        var session = FakeMemorySession.Planned(4);
        using var rig = new RunRig(memorySessionFactory: () => session);
        var log = new ProgressLog();

        await Run(rig, new CheckRequest(CheckKind.Memory), log);

        Assert.Equal(120, session.Waits);
        Assert.Equal(30, Assert.Single(log.Items, static p => p.Stage == CheckStage.Loading && p.RecentRate is null).TargetSeconds);
        Assert.Equal(30, MemoryCheckProcedure.DefaultDuration.TotalSeconds);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(300)]
    public async Task The_requested_duration_is_that_many_seconds_of_load_and_the_target_of_the_progress(int seconds)
    {
        var session = FakeMemorySession.Planned(4);
        using var rig = new RunRig(memorySessionFactory: () => session);
        var log = new ProgressLog();

        var run = await Run(rig, new CheckRequest(CheckKind.Memory, TimeSpan.FromSeconds(seconds)), log);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Equal(seconds * 4, session.Waits);
        Assert.Equal(seconds + 0.012, Rows.Number(Row(run, "memory.duration")), 3);
        Assert.All(log.Items.Where(static p => p.Stage == CheckStage.Loading), p => Assert.Equal(seconds, p.TargetSeconds));
        Assert.Empty(CheckResultValidator.Validate(run.Result));
    }

    [Fact]
    public async Task A_duration_that_is_not_allowed_is_refused_before_anything_starts()
    {
        var made = 0;
        using var rig = new RunRig(memorySessionFactory: () =>
        {
            Interlocked.Increment(ref made);
            return FakeMemorySession.Planned(4);
        });

        await Assert.ThrowsAnyAsync<ArgumentException>(() => rig.RunAsync(new CheckRequest(CheckKind.Memory, TimeSpan.FromSeconds(45))));

        Assert.Equal(0, made);
    }

    // ---- a stop ----

    [Fact]
    public async Task A_stop_in_the_middle_gives_a_stopped_partial_result_that_is_saved_and_has_no_ok_row()
    {
        using var cts = new CancellationTokenSource();
        var session = FakeMemorySession.Planned(4);
        session.OnWait = n =>
        {
            if (n == 7)
            {
                cts.Cancel();
            }
        };
        using var rig = new RunRig(memorySessionFactory: () => session);

        var run = await Run(rig, ct: cts.Token);

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal([CheckNotes.MemoryBorrowedRegionOnly], run.Result.Notes);
        Assert.Equal(CheckOutcome.Stopped, rig.History.Saved.Single().Outcome);

        // Six waits were done: 72 slices, nine whole passes. Everything that has a value says partial; nothing is ok.
        Assert.Equal(9, Row(run, "memory.passes").Value!.AsInt);
        Assert.Equal(CheckValue.Dec((ulong)(9 * 4 * Block)), Row(run, "memory.verified").Value);
        Assert.All(run.Result.Items, static i => Assert.Equal(CheckReason.Partial, i.Reason));
        Assert.DoesNotContain(run.Result.Items, static i => i.Verdict == ItemVerdict.Ok);
        Assert.NotEqual(CheckVerdict.Ok, run.Result.Verdict);
        Assert.Equal(CheckVerdict.Unavailable, run.Result.Verdict);
        Assert.True(session.Disposed);
        MemoryEvents.AssertJoinedThenFreed(session);
    }

    [Fact]
    public async Task A_stop_joins_every_thread_before_it_frees_a_block_and_frees_each_once()
    {
        using var cts = new CancellationTokenSource();
        var session = FakeMemorySession.Planned(4);
        session.OnWait = n =>
        {
            if (n == 3)
            {
                cts.Cancel();
            }
        };
        using var rig = new RunRig(memorySessionFactory: () => session);

        await Run(rig, ct: cts.Token);

        Assert.Equal(4, session.WorkThreads.Count);
        MemoryEvents.AssertJoinedThenFreed(session);
        Assert.All(session.WorkThreads, static t => Assert.Equal(0, t.StepsDuringJoin));
    }

    [Fact]
    public async Task A_mismatch_found_before_the_stop_stays_a_finding_and_the_verdict_is_check()
    {
        using var cts = new CancellationTokenSource();
        var session = FakeMemorySession.Planned(4);
        session.OnAccess = a =>
        {
            if (a.Number == 5)
            {
                session.Blocks[0].Flip(10, 3);
            }

            if (a.Number == 6)
            {
                cts.Cancel();
            }
        };
        using var rig = new RunRig(memorySessionFactory: () => session);

        var run = await Run(rig, ct: cts.Token);

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        var row = Row(run, "memory.mismatches");
        Assert.Equal(CheckValue.Int(1), row.Value);
        Assert.Equal(ItemVerdict.Check, row.Verdict);
        Assert.Equal(CheckReason.Partial, row.Reason);
        Assert.Equal(CheckEvidenceCode.PatternMismatch, row.Evidence!.Code);
        Assert.Equal(CheckVerdict.Check, run.Result.Verdict);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.True(run.IsSaved);
    }

    [Fact]
    public async Task A_stop_that_comes_while_the_run_is_ending_is_a_stop_with_partial_rows_and_never_a_stop_with_whole_ones()
    {
        // The load went to its deadline; the cancel comes while the first work thread is being joined. The procedure sees the token when the load
        // returns: the rows are partial and the run is stopped (before the seal point).
        using var cts = new CancellationTokenSource();
        var session = FakeMemorySession.Planned(4);
        session.OnJoin = _ => cts.Cancel();
        using var rig = new RunRig(memorySessionFactory: () => session);

        var run = await Run(rig, ct: cts.Token);

        Assert.Equal(120, session.Waits); // every second was done
        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.DoesNotContain(run.Result.Items, static i => i.Verdict == ItemVerdict.Ok);
        Assert.Equal(CheckReason.Partial, Row(run, "memory.mismatches").Reason);
        Assert.Equal(CheckReason.Partial, Row(run, "memory.verified").Reason);
        MemoryEvents.AssertJoinedThenFreed(session);
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_never_starts_a_run_and_makes_no_session()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var made = 0;
        using var rig = new RunRig(memorySessionFactory: () =>
        {
            Interlocked.Increment(ref made);
            return FakeMemorySession.Planned(4);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.RunAsync(Default, null, cts.Token));

        Assert.Equal(0, made);
        Assert.Empty(rig.History.Saved);
    }

    [Fact]
    public async Task A_stop_at_the_very_first_wait_is_a_stopped_result_with_nothing_verified_and_every_block_freed()
    {
        using var cts = new CancellationTokenSource();
        var session = FakeMemorySession.Planned(4);
        session.OnWait = n =>
        {
            if (n == 1)
            {
                cts.Cancel();
            }
        };
        using var rig = new RunRig(memorySessionFactory: () => session);

        var run = await Run(rig, ct: cts.Token);

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.True(run.IsSaved);
        Assert.Equal(CheckValue.Dec(0), Row(run, "memory.verified").Value);
        Assert.Null(Row(run, "memory.mismatches").Value);
        Assert.Equal(CheckVerdict.Unavailable, run.Result.Verdict);
        MemoryEvents.AssertJoinedThenFreed(session);
    }

    // ---- the free memory ----

    [Fact]
    public async Task A_fall_of_the_free_memory_stops_the_run_and_adds_the_pressure_note_after_the_region_note()
    {
        var session = FakeMemorySession.Planned(4);
        session.AvailableAt = n => n == 8 ? session.Floor / 2 - 1 : null;
        using var rig = new RunRig(memorySessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Equal([CheckNotes.MemoryBorrowedRegionOnly, CheckNotes.MemoryPressure], run.Result.Notes);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.DoesNotContain(run.Result.Items, static i => i.Verdict == ItemVerdict.Ok);
        Assert.NotEqual(CheckVerdict.Ok, run.Result.Verdict);
        Assert.Equal(CheckReason.Partial, Row(run, "memory.verified").Reason);
        MemoryEvents.AssertJoinedThenFreed(session);
    }

    [Fact]
    public async Task A_run_that_could_not_borrow_one_block_is_completed_with_the_pressure_note_and_an_unavailable_verdict()
    {
        var session = new FakeMemorySession(8 * FakeMemorySession.Gib, FakeMemorySession.Gib + (ulong)(Block / 2));
        using var rig = new RunRig(memorySessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal([CheckNotes.MemoryBorrowedRegionOnly, CheckNotes.MemoryPressure], run.Result.Notes);
        Assert.Equal(CheckValue.Dec(0), Row(run, "memory.borrowed").Value);
        Assert.Null(Row(run, "memory.passes").Value);
        var mismatches = Row(run, "memory.mismatches");
        Assert.Equal(ItemVerdict.Unavailable, mismatches.Verdict);
        Assert.Null(mismatches.Value);
        Assert.Equal(CheckReason.Partial, mismatches.Reason);
        Assert.Null(Row(run, "memory.write_rate").Value); // not 0
        Assert.Null(Row(run, "memory.verify_rate").Value);
        Assert.DoesNotContain(run.Result.Items, static i => i.Verdict == ItemVerdict.Ok);
        Assert.Equal(CheckVerdict.Unavailable, run.Result.Verdict);
        Assert.Empty(session.WorkThreads);
        Assert.Equal(0, session.AllocateCalls);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task An_allocation_that_runs_out_of_memory_at_the_third_block_borrows_two_and_the_run_is_whole()
    {
        var session = FakeMemorySession.Planned(4);
        session.AllocateFails = static n => n == 3;
        using var rig = new RunRig(memorySessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal([CheckNotes.MemoryBorrowedRegionOnly], run.Result.Notes);
        Assert.Equal(CheckValue.Dec((ulong)(2 * Block)), Row(run, "memory.borrowed").Value);
        Assert.Equal(CheckValue.Int(0), Row(run, "memory.mismatches").Value);
        Assert.Equal(CheckVerdict.Ok, run.Result.Verdict);
        Assert.Equal(3, session.AllocateCalls);
        MemoryEvents.AssertJoinedThenFreed(session);
    }

    // ---- a failure ----

    [Fact]
    public async Task A_work_thread_that_runs_out_of_memory_fails_the_run_with_an_internal_error_and_every_block_is_freed()
    {
        var session = FakeMemorySession.Planned(4);
        session.AccessFault = static n => n == 9 ? new OutOfMemoryException() : null;
        using var rig = new RunRig(memorySessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.InternalError, run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal([CheckNotes.MemoryBorrowedRegionOnly], run.Result.Notes); // a failed run keeps the note
        Assert.Equal(CheckOutcome.Failed, rig.History.Saved.Single().Outcome);
        Assert.True(session.Disposed);
        MemoryEvents.AssertJoinedThenFreed(session);
    }

    [Fact]
    public async Task A_work_thread_that_throws_fails_the_run_the_same_way()
    {
        var session = FakeMemorySession.Planned(4);
        session.AccessFault = static n => n == 40 ? new InvalidOperationException("boom") : null;
        using var rig = new RunRig(memorySessionFactory: () => session);

        var run = await Run(rig);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.InternalError, run.FailureCode);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        MemoryEvents.AssertJoinedThenFreed(session);
    }

    [Fact]
    public async Task A_session_that_cannot_be_made_fails_the_run_the_same_way_and_the_failed_run_is_saved()
    {
        using var rig = new RunRig(memorySessionFactory: static () => throw new InvalidOperationException("no session"));

        var run = await Run(rig);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.InternalError, run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal([CheckNotes.MemoryBorrowedRegionOnly], run.Result.Notes);
    }

    // ---- the progress ----

    [Fact]
    public async Task The_stages_come_in_order_and_the_loading_stage_names_the_target()
    {
        var session = FakeMemorySession.Planned(4);
        using var rig = new RunRig(memorySessionFactory: () => session);
        var log = new ProgressLog();

        await Run(rig, progress: log);

        Assert.Equal(
            [CheckStage.Starting, CheckStage.Collecting, CheckStage.Loading, CheckStage.Judging, CheckStage.Saving, CheckStage.Done],
            log.Stages.Distinct());
        Assert.Equal(CheckStage.Done, log.Stages[^1]);
    }

    [Fact]
    public async Task Progress_goes_through_the_gate_once_a_second_with_the_mib_per_second()
    {
        // The service clock (the one the rate limit reads) moves 0.25 s with every wait of the load, a reading is offered every fourth wait (once
        // a second of the load) and every one of them is let through: 29 in 30 seconds (the last one is the end of the run).
        var serviceClock = new ManualTimeProvider();
        var session = FakeMemorySession.Planned(4);
        session.AfterAdvance = _ => serviceClock.Advance(TimeSpan.FromMilliseconds(250));
        using var rig = new RunRig(clock: serviceClock, memorySessionFactory: () => session);
        var log = new ProgressLog();

        var run = await Run(rig, progress: log);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        var readings = log.Items.Where(static p => p.RecentRate is not null).ToList();
        Assert.Equal(29, readings.Count);
        Assert.All(readings, static p =>
        {
            Assert.Equal(CheckStage.Loading, p.Stage);
            Assert.Equal("MiB/s", p.RateUnit);
            Assert.Equal(30, p.TargetSeconds);
            Assert.True(p.RecentRate!.Value > 0);
        });
        Assert.Equal(readings.OrderBy(static p => p.ElapsedSeconds).Select(static p => p.ElapsedSeconds), readings.Select(static p => p.ElapsedSeconds));
    }

    [Fact]
    public async Task Progress_is_never_sent_twice_within_the_gate_even_when_the_readings_come_faster()
    {
        // 0.1 s of service time with every wait: a reading is offered every 0.4 s and one goes out when 0.9 s have passed since the last that
        // did: waits 4, 16, 28 ... 112, ten in all.
        var serviceClock = new ManualTimeProvider();
        var session = FakeMemorySession.Planned(4);
        session.AfterAdvance = _ => serviceClock.Advance(TimeSpan.FromMilliseconds(100));
        using var rig = new RunRig(clock: serviceClock, memorySessionFactory: () => session);
        var log = new ProgressLog();

        await Run(rig, progress: log);

        Assert.Equal(10, log.Items.Count(static p => p.RecentRate is not null));
    }

    [Fact]
    public async Task The_stage_notifications_are_not_load_readings()
    {
        var serviceClock = new ManualTimeProvider();
        var session = FakeMemorySession.Planned(4);
        session.AfterAdvance = _ => serviceClock.Advance(TimeSpan.FromMilliseconds(250));
        using var rig = new RunRig(clock: serviceClock, memorySessionFactory: () => session);
        var log = new ProgressLog();

        await Run(rig, progress: log);

        var stages = log.Items.Where(static p => p.RecentRate is null).ToList();
        Assert.All(stages, static p => Assert.Null(p.RateUnit));
        Assert.Equal(
            [CheckStage.Starting, CheckStage.Collecting, CheckStage.Loading, CheckStage.Judging, CheckStage.Saving, CheckStage.Done],
            stages.Select(static p => p.Stage));
    }

    [Fact]
    public async Task A_progress_handler_that_blocks_does_not_hold_up_the_run()
    {
        using var release = new ManualResetEventSlim();
        var session = FakeMemorySession.Planned(4);
        using var rig = new RunRig(memorySessionFactory: () => session);
        var log = new ProgressLog { OnReport = _ => release.Wait(TimeSpan.FromSeconds(20)) };

        try
        {
            var run = await rig.RunAsync(Default, log).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
            Assert.True(run.IsSaved);
            Assert.Empty(CheckResultValidator.Validate(run.Result));
            Assert.Equal(120, session.Waits);
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
        // The session's recipe blocks (stands for an allocation that takes a long time). If the procedure ran it on the caller's thread, RunAsync
        // itself would not return until the gate opened. It must return while the recipe is still blocked, with a task that is not done, and
        // the recipe must be running on another thread. No time is measured: the gate is only the way to stop the recipe.
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var factoryThread = -1;
        var session = FakeMemorySession.Planned(4);
        using var rig = new RunRig(memorySessionFactory: () =>
        {
            factoryThread = Environment.CurrentManagedThreadId;
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(20));
            return session;
        });
        var callerThread = Environment.CurrentManagedThreadId;

        var task = rig.RunAsync(Default);

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the session recipe was never asked");
            Assert.False(task.IsCompleted);
            Assert.False(gate.IsSet); // RunAsync came back while the recipe was still blocked
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
        var onPool = true;
        var session = FakeMemorySession.Planned(4);
        using var rig = new RunRig(memorySessionFactory: () =>
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
        var made = new List<FakeMemorySession>();
        using var rig = new RunRig(memorySessionFactory: () =>
        {
            var session = FakeMemorySession.Planned(4);
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

    [Fact]
    public async Task The_memory_procedure_is_one_of_the_default_procedures_of_the_runner()
    {
        // A runner that is given no procedure list takes a memory request: the memory procedure is registered in the default list.
        var session = FakeMemorySession.Planned(4);
        using var rig = new RunRig(memorySessionFactory: () => session);
        var runner = new CheckRunner(rig.Services);

        var run = await runner.RunAsync(Default, null, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(CheckKind.Memory, run.Result.Kind);
        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Equal(120, session.Waits);
        Assert.Equal(CheckKind.Memory, new MemoryCheckProcedure().Kind);
    }

    // ---- the real memory is kept out of the tests ----

    [Fact]
    public async Task A_rig_that_is_given_no_memory_session_does_not_borrow_the_machines_memory_and_fails_the_run()
    {
        // The default of the test rig is a session that throws. If the default were the real session, this run would borrow memory on the machine
        // that runs the tests (and the cancel after two seconds would only stop it, not fail it).
        using var rig = new RunRig();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var run = await rig.RunAsync(Default, null, cts.Token).WaitAsync(Patience);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.InternalError, run.FailureCode);
    }

    [Fact]
    public void No_test_but_the_real_machine_class_makes_a_real_memory_session()
    {
        // Static guard: nothing but MemoryRealMachineTests may write "new" and the name of the real session, with or without a namespace in front
        // of it. The name is built here so that this file does not contain it.
        var pattern = new Regex(@"new\s+(global::)?([A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*" + "Real" + "MemoryTestSession" + @"\b");
        var root = TestsRoot();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(static f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(static f => !Path.GetFileName(f).Equals("MemoryRealMachineTests.cs", StringComparison.Ordinal))
            .Where(f => pattern.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(offenders.Count == 0, "a test makes a real memory session: " + string.Join(", ", offenders));
    }

    private static string TestsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcMieru.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests");
    }
}
