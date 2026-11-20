using PcMieru.Checks.Tests.Storage;

namespace PcMieru.Checks.Tests.Run;

/// <summary>
/// The run framework on its own, with fake procedures (kind=cpu here: a kind that has no real procedure yet, which is exactly what the
/// framework must stay generic for). Whatever a procedure does, the run ends in one result that is saved, and the end of it is decided at one point.
/// </summary>
public sealed class CheckRunnerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static CheckItem Duration(double seconds = 30) =>
        CheckItem.Create("cpu.duration", CheckValue.Number(seconds), ItemVerdict.Info, CheckSource.Load);

    private static CheckItem Steady(double percent = 97.5) =>
        CheckItem.Create("cpu.steadiness", CheckValue.Number(percent), ItemVerdict.Ok, CheckSource.Load);

    private static CheckRunner With(RunRig rig, Func<CheckContext, CancellationToken, Task> body, CheckKind kind = CheckKind.Cpu) =>
        new(rig.Services, [new FakeProcedure(kind, body)]);

    private static Task<CheckRun> Run(CheckRunner runner, IProgress<CheckProgress>? progress = null, CancellationToken ct = default) =>
        runner.RunAsync(new CheckRequest(CheckKind.Cpu), progress, ct).WaitAsync(Patience);

    private static void AssertValid(CheckRun run) =>
        Assert.True(CheckResultValidator.Validate(run.Result).Count == 0, string.Join(" | ", CheckResultValidator.Validate(run.Result)));

    // ---- completed ----

    [Fact]
    public async Task A_procedure_that_returns_gives_a_completed_saved_result()
    {
        using var rig = new RunRig();
        var runner = With(rig, (c, _) =>
        {
            c.Collector.Add(Duration());
            c.Collector.Add(Steady());
            c.Collector.Note(CheckNotes.CpuTemperatureNotMeasured);
            c.Collector.Note(CheckNotes.CpuTemperatureNotMeasured); // a note is written once
            return Task.CompletedTask;
        });

        var run = await Run(runner);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Equal(CheckKind.Cpu, run.Result.Kind);
        Assert.Equal(Kit.Id(1), run.Result.RunId);
        Assert.Equal(Samples.App, run.Result.App);
        Assert.Equal(CheckVerdict.Ok, run.Result.Verdict);
        Assert.Equal(1, run.Result.Counts.Ok);
        Assert.Equal(1, run.Result.Counts.Info);
        Assert.Equal(2, run.Result.Counts.Total);
        Assert.Equal([CheckNotes.CpuTemperatureNotMeasured], run.Result.Notes);
        Assert.Null(run.FailureCode);
        Assert.Null(run.FailureDetail);
        AssertValid(run);

        Assert.True(run.IsSaved);
        Assert.Same(run.Result, Assert.Single(rig.History.Saved));
        var back = await rig.ReadBackAsync(run.Result.RunId);
        Assert.NotNull(back);
        Assert.Equal(Kit.Bytes(run.Result), Kit.Bytes(back));
    }

    [Fact]
    public async Task A_check_row_makes_the_overall_verdict_check()
    {
        using var rig = new RunRig();
        var runner = With(rig, (c, _) =>
        {
            c.Collector.Add(Steady());
            c.Collector.Add(CheckItem.Create("cpu.compute_errors", CheckValue.Int(3), ItemVerdict.Check, CheckSource.Load));
            return Task.CompletedTask;
        });

        var run = await Run(runner);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Equal(CheckVerdict.Check, run.Result.Verdict);
        Assert.Equal(1, run.Result.Counts.Check);
        Assert.True(run.IsSaved);
    }

    [Fact]
    public async Task The_procedure_gets_the_request_the_run_id_the_start_time_and_the_callers_token()
    {
        using var rig = new RunRig();
        using var cts = new CancellationTokenSource();
        CheckContext? seen = null;
        CancellationToken seenToken = default;
        var runner = With(rig, (c, ct) =>
        {
            seen = c;
            seenToken = ct;
            return Task.CompletedTask;
        });

        var run = await Run(runner, ct: cts.Token);

        Assert.NotNull(seen);
        Assert.Equal(CheckKind.Cpu, seen.Request.Kind);
        Assert.Equal(run.Result.RunId, seen.RunId);
        Assert.Equal(run.Result.StartedAtUtc, seen.StartedUtc);
        Assert.Same(rig.Services, seen.Services);
        Assert.Equal(cts.Token, seenToken);
    }

    [Fact]
    public async Task The_times_are_the_clock_at_the_start_and_at_the_end_truncated_to_milliseconds()
    {
        using var rig = new RunRig();
        var runner = With(rig, (_, _) =>
        {
            rig.Time.Now = Drives.T0.AddMilliseconds(5500).AddTicks(4321);
            return Task.CompletedTask;
        });

        var run = await Run(runner);

        Assert.Equal(Drives.T0, run.Result.StartedAtUtc);
        Assert.Equal(Drives.T0.AddMilliseconds(5500), run.Result.EndedAtUtc);
        AssertValid(run);
    }

    [Fact]
    public async Task A_clock_that_went_back_during_the_run_does_not_make_the_run_end_before_it_started()
    {
        using var rig = new RunRig();
        var runner = With(rig, (_, _) =>
        {
            rig.Time.Now = Drives.T0.AddHours(-1);
            return Task.CompletedTask;
        });

        var run = await Run(runner);

        Assert.Equal(run.Result.StartedAtUtc, run.Result.EndedAtUtc);
        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        AssertValid(run);
        Assert.True(run.IsSaved);
    }

    [Fact]
    public async Task Each_kind_goes_to_its_own_procedure()
    {
        using var rig = new RunRig();
        var asked = new List<CheckKind>();
        var runner = new CheckRunner(
            rig.Services,
            [
                new FakeProcedure(CheckKind.Cpu, (_, _) =>
                {
                    asked.Add(CheckKind.Cpu);
                    return Task.CompletedTask;
                }),
                new FakeProcedure(CheckKind.Memory, (_, _) =>
                {
                    asked.Add(CheckKind.Memory);
                    return Task.CompletedTask;
                }),
            ]);

        var cpu = await runner.RunAsync(new CheckRequest(CheckKind.Cpu), null, CancellationToken.None).WaitAsync(Patience);
        var memory = await runner.RunAsync(new CheckRequest(CheckKind.Memory), null, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal([CheckKind.Cpu, CheckKind.Memory], asked);
        Assert.Equal(CheckKind.Cpu, cpu.Result.Kind);
        Assert.Equal(CheckKind.Memory, memory.Result.Kind);
        Assert.Equal([Kit.Id(1), Kit.Id(2)], rig.History.Saved.Select(static r => r.RunId));
        Assert.Equal([CheckKind.Cpu, CheckKind.Memory], [.. runner.SupportedKinds.OrderBy(static k => k)]);
    }

    [Fact]
    public void The_runner_of_the_services_knows_the_system_the_cpu_and_the_storage_checks_and_nothing_else_yet()
    {
        using var rig = new RunRig();

        Assert.Equal([CheckKind.System, CheckKind.Cpu, CheckKind.Storage], [.. new CheckRunner(rig.Services).SupportedKinds.OrderBy(static k => k)]);
    }

    // ---- stopped ----

    [Fact]
    public async Task A_cancel_in_the_middle_gives_a_stopped_result_that_is_saved_with_what_was_collected()
    {
        using var rig = new RunRig();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = With(rig, async (c, ct) =>
        {
            c.Collector.Add(Duration());
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });

        var task = Run(runner, ct: cts.Token);
        await started.Task.WaitAsync(Patience);
        await cts.CancelAsync();
        var run = await task;

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.Equal("cpu.duration", Assert.Single(run.Result.Items).Id);
        AssertValid(run);
        Assert.True(run.IsSaved); // the stop does not reach the write
        Assert.Same(run.Result, Assert.Single(rig.History.Saved));
    }

    [Fact]
    public async Task A_procedure_that_marks_itself_stopped_gives_a_stopped_result()
    {
        using var rig = new RunRig();
        var runner = With(rig, (c, _) =>
        {
            c.Collector.Add(Duration(12));
            c.Collector.MarkStopped(); // for example a memory run that stopped for the lack of free memory
            return Task.CompletedTask;
        });

        var run = await Run(runner);

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.Single(run.Result.Items);
        Assert.True(run.IsSaved);
    }

    [Fact]
    public async Task A_cancel_that_comes_after_the_procedure_but_before_the_end_is_a_stop()
    {
        using var rig = new RunRig();
        using var cts = new CancellationTokenSource();
        var runner = With(rig, async (c, _) =>
        {
            c.Collector.Add(Duration());
            await cts.CancelAsync(); // the user's stop arrives just as the procedure finishes: one finalisation point decides
        });

        var run = await Run(runner, ct: cts.Token);

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
    }

    [Fact]
    public async Task A_cancellation_that_is_not_the_callers_is_a_failure_and_not_a_stop()
    {
        using var rig = new RunRig();
        var runner = With(rig, (_, _) => throw new TaskCanceledException("a timeout inside the procedure"));

        var run = await Run(runner);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.InternalError, run.FailureCode);
        Assert.True(run.IsSaved);
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_throws_and_makes_no_run()
    {
        using var rig = new RunRig();
        var ran = false;
        var runner = With(rig, (_, _) =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(runner, ct: cts.Token));

        Assert.False(ran);
        Assert.Empty(rig.History.Saved);
        Assert.Equal(0, rig.Source.Calls);
    }

    // ---- failed ----

    [Fact]
    public async Task A_failure_with_a_code_gives_a_failed_saved_result_with_the_code_and_what_was_collected()
    {
        using var rig = new RunRig();
        var runner = With(rig, (c, _) =>
        {
            c.Collector.Add(Duration());
            throw new CheckRunFailedException(CheckRunFailureCodes.InspectionFailed, "the sample could not be taken");
        });

        var run = await Run(runner);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.InspectionFailed, run.FailureCode);
        Assert.Equal("the sample could not be taken", run.FailureDetail);
        Assert.Single(run.Result.Items);
        AssertValid(run);
        Assert.True(run.IsSaved);
        Assert.Same(run.Result, Assert.Single(rig.History.Saved));
    }

    [Fact]
    public async Task Any_other_exception_is_an_internal_error_and_the_run_is_still_saved()
    {
        using var rig = new RunRig();
        var runner = With(rig, (c, _) =>
        {
            c.Collector.Add(Duration());
            throw new InvalidOperationException("something nobody planned for");
        });

        var run = await Run(runner);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.InternalError, run.FailureCode);
        Assert.Contains("InvalidOperationException", run.FailureDetail, StringComparison.Ordinal);
        Assert.Contains("something nobody planned for", run.FailureDetail, StringComparison.Ordinal);
        Assert.Single(run.Result.Items);
        Assert.True(run.IsSaved);
    }

    [Fact]
    public async Task An_exception_thrown_before_the_procedure_returns_a_task_is_a_failure_too()
    {
        using var rig = new RunRig();
        var runner = new CheckRunner(rig.Services, [new ThrowsAtOnce()]);

        var run = await runner.RunAsync(new CheckRequest(CheckKind.Cpu), null, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.InternalError, run.FailureCode);
        Assert.True(run.IsSaved);
    }

    [Fact]
    public async Task A_very_long_failure_message_is_cut_so_the_detail_stays_short()
    {
        using var rig = new RunRig();
        var runner = With(rig, (_, _) => throw new InvalidOperationException(new string('x', 5000)));

        var run = await Run(runner);

        Assert.True(run.FailureDetail!.Length <= 300);
        Assert.True(run.IsSaved);
    }

    [Fact]
    public void The_failure_codes_are_the_three_the_framework_uses()
    {
        Assert.Equal(["inspection_failed", "result_invalid", "internal_error"], CheckRunFailureCodes.All);
    }

    // ---- a result that does not pass ----

    [Fact]
    public async Task A_result_that_breaks_a_rule_is_a_bug_the_run_fails_and_nothing_invalid_is_saved()
    {
        using var rig = new RunRig();
        var runner = With(rig, (c, _) =>
        {
            c.Collector.Add(Duration());
            c.Collector.Add(Duration(60)); // the same (id, subject) twice
            return Task.CompletedTask;
        });

        var run = await Run(runner);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.ResultInvalid, run.FailureCode);
        Assert.False(string.IsNullOrWhiteSpace(run.FailureDetail));
        Assert.Empty(run.Result.Items);
        AssertValid(run);

        // What was saved is the empty failed result, never the broken one.
        var saved = Assert.Single(rig.History.Saved);
        Assert.Same(run.Result, saved);
        Assert.Empty(CheckResultValidator.Validate(saved));
        Assert.True(run.IsSaved);
        var back = await rig.ReadBackAsync(run.Result.RunId);
        Assert.Empty(back!.Items);

        // What the check built is not lost (DESIGN-A §2): it rides on the run, unsaved and invalid, for the screen, with the first problem as
        // the diagnosis. The procedure itself did not fail, so there is no earlier failure code.
        var unsaved = run.UnsavedResult!;
        Assert.NotNull(run.UnsavedResult);
        Assert.NotSame(run.Result, unsaved);
        Assert.Equal(run.Result.RunId, unsaved.RunId);
        Assert.Equal(CheckOutcome.Completed, unsaved.Outcome);
        Assert.Equal(2, unsaved.Items.Count);
        Assert.NotEmpty(CheckResultValidator.Validate(unsaved));
        Assert.DoesNotContain(rig.History.Saved, r => ReferenceEquals(r, unsaved));
        Assert.Null(run.OriginalFailureCode);
        Assert.False(string.IsNullOrWhiteSpace(run.FirstProblem));
        Assert.Equal(run.FirstProblem, run.FailureDetail);
    }

    [Fact]
    public async Task The_failure_code_the_run_had_before_the_replacement_is_kept_beside_the_new_one()
    {
        using var rig = new RunRig();
        var runner = With(rig, (c, _) =>
        {
            c.Collector.Add(Duration());
            c.Collector.Add(Duration(60)); // the same (id, subject) twice
            throw new InvalidOperationException("the drive list broke");
        });

        var run = await Run(runner);

        Assert.Equal(CheckRunFailureCodes.ResultInvalid, run.FailureCode);
        Assert.Equal(CheckRunFailureCodes.InternalError, run.OriginalFailureCode);
        var unsaved = run.UnsavedResult!;
        Assert.NotNull(run.UnsavedResult);
        Assert.Equal(CheckOutcome.Failed, unsaved.Outcome);
        Assert.Equal(2, unsaved.Items.Count);
        Assert.False(string.IsNullOrWhiteSpace(run.FirstProblem));
        AssertValid(run);
        Assert.Empty(run.Result.Items);
        Assert.Single(rig.History.Saved);
    }

    [Fact]
    public async Task A_run_whose_result_passes_has_no_unsaved_copy_and_no_diagnosis()
    {
        using var rig = new RunRig();
        var completed = await Run(With(rig, (c, _) =>
        {
            c.Collector.Add(Duration());
            return Task.CompletedTask;
        }));
        var failed = await Run(With(rig, (c, _) =>
        {
            c.Collector.Add(Duration());
            throw new InvalidOperationException("something nobody planned for");
        }));

        foreach (var run in new[] { completed, failed })
        {
            Assert.Null(run.UnsavedResult);
            Assert.Null(run.OriginalFailureCode);
            Assert.Null(run.FirstProblem);
        }

        Assert.Equal(CheckRunFailureCodes.InternalError, failed.FailureCode);
    }

    [Fact]
    public async Task A_note_the_registry_does_not_know_makes_the_result_invalid()
    {
        using var rig = new RunRig();
        var runner = With(rig, (c, _) =>
        {
            c.Collector.Add(Duration());
            c.Collector.Note("made.up_note");
            return Task.CompletedTask;
        });

        var run = await Run(runner);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.ResultInvalid, run.FailureCode);
        Assert.Empty(run.Result.Items);
        Assert.True(run.Result.Notes is null or { Count: 0 });
        AssertValid(run);

        // The note the registry refused is on the unsaved original, not in what was saved.
        Assert.NotNull(run.UnsavedResult);
        Assert.Contains("made.up_note", run.UnsavedResult!.Notes!);
        Assert.False(string.IsNullOrWhiteSpace(run.FirstProblem));
    }

    [Fact]
    public async Task An_invalid_result_of_a_stopped_run_is_also_replaced()
    {
        using var rig = new RunRig();
        using var cts = new CancellationTokenSource();
        var runner = With(rig, async (c, _) =>
        {
            c.Collector.Add(Duration());
            c.Collector.Add(Duration());
            await cts.CancelAsync();
        });

        var run = await Run(runner, ct: cts.Token);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.ResultInvalid, run.FailureCode);
        AssertValid(run);

        // The original was a stopped run: that is what the screen is told (a stop is not a failure code).
        Assert.NotNull(run.UnsavedResult);
        Assert.Equal(CheckOutcome.Stopped, run.UnsavedResult!.Outcome);
        Assert.Null(run.OriginalFailureCode);
    }

    // ---- the save ----

    [Fact]
    public async Task A_save_that_fails_is_reported_and_the_result_is_kept()
    {
        using var rig = new RunRig();
        rig.History.Override = static _ => Task.FromResult(CheckSaveState.Failed(CheckSaveFailureCodes.AccessDenied, "denied"));
        var runner = With(rig, (c, _) =>
        {
            c.Collector.Add(Duration());
            return Task.CompletedTask;
        });

        var run = await Run(runner);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.False(run.IsSaved);
        Assert.Equal(CheckSaveStatus.Failed, run.Save.Status);
        Assert.Equal(CheckSaveFailureCodes.AccessDenied, run.Save.FailureCode);
        Assert.Single(run.Result.Items);
        AssertValid(run);
    }

    [Fact]
    public async Task A_history_that_throws_is_a_failed_save_and_not_a_lost_result()
    {
        using var rig = new RunRig();
        rig.History.Override = static _ => throw new IOException("a store that was not supposed to throw");
        var runner = With(rig, (c, _) =>
        {
            c.Collector.Add(Duration());
            return Task.CompletedTask;
        });

        var run = await Run(runner);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Equal(CheckSaveStatus.Failed, run.Save.Status);
        Assert.Equal(CheckSaveFailureCodes.IoError, run.Save.FailureCode);
        Assert.Single(run.Result.Items);
    }

    [Fact]
    public async Task A_failed_run_whose_save_also_fails_reports_both()
    {
        using var rig = new RunRig();
        rig.History.Override = static _ => Task.FromResult(CheckSaveState.Failed(CheckSaveFailureCodes.DiskFull, "full"));
        var runner = With(rig, (_, _) => throw new CheckRunFailedException(CheckRunFailureCodes.InspectionFailed, "x"));

        var run = await Run(runner);

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.InspectionFailed, run.FailureCode);
        Assert.Equal(CheckSaveFailureCodes.DiskFull, run.Save.FailureCode);
    }

    // ---- the request ----

    [Fact]
    public async Task A_request_that_breaks_its_own_rules_throws_before_a_run_exists()
    {
        using var rig = new RunRig();
        var runner = With(rig, (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(new CheckRequest(CheckKind.Cpu, TimeSpan.FromSeconds(45)), null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(new CheckRequest(CheckKind.Cpu, AllowElevation: true), null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.RunAsync(null!, null, CancellationToken.None));

        Assert.Empty(rig.History.Saved);
    }

    [Fact]
    public async Task A_kind_with_no_procedure_in_this_build_is_not_supported_and_makes_no_run()
    {
        using var rig = new RunRig();
        var runner = With(rig, (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<NotSupportedException>(() => runner.RunAsync(new CheckRequest(CheckKind.System), null, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => runner.RunAsync(new CheckRequest(CheckKind.Memory, TimeSpan.FromSeconds(30)), null, CancellationToken.None));

        Assert.Empty(rig.History.Saved);
    }

    [Fact]
    public void A_runner_needs_its_services_and_its_procedures()
    {
        using var rig = new RunRig();

        Assert.Throws<ArgumentNullException>(() => new CheckRunner(null!));
        Assert.Throws<ArgumentNullException>(() => new CheckRunner(rig.Services, null!));
    }

    // ---- progress ----

    [Fact]
    public async Task The_runner_reports_start_saving_and_done_with_the_run_id()
    {
        using var rig = new RunRig();
        var log = new ProgressLog();
        var runner = With(rig, (_, _) => Task.CompletedTask);

        var run = await Run(runner, log);

        Assert.Equal([CheckStage.Starting, CheckStage.Saving, CheckStage.Done], log.Stages);
        Assert.All(log.Items, p => Assert.Equal(run.Result.RunId, p.RunId));
    }

    [Fact]
    public async Task A_stage_the_procedure_reports_comes_between_start_and_saving()
    {
        using var rig = new RunRig();
        var log = new ProgressLog();
        var runner = With(rig, (c, _) =>
        {
            c.Stage(CheckStage.Collecting, targetSeconds: 30);
            c.Stage(CheckStage.Judging);
            return Task.CompletedTask;
        });

        await Run(runner, log);

        Assert.Equal([CheckStage.Starting, CheckStage.Collecting, CheckStage.Judging, CheckStage.Saving, CheckStage.Done], log.Stages);
        Assert.Equal(30, log.Items[1].TargetSeconds);
    }

    [Fact]
    public async Task A_handler_that_throws_does_not_fail_the_run()
    {
        using var rig = new RunRig();
        var log = new ProgressLog { OnReport = static _ => throw new InvalidOperationException("a broken progress bar") };
        var runner = With(rig, (c, _) =>
        {
            c.Collector.Add(Duration());
            return Task.CompletedTask;
        });

        var run = await Run(runner, log);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Equal(CheckStage.Done, log.Stages[^1]); // every report still reached it
    }

    [Fact]
    public async Task Load_reports_are_at_most_one_a_second_and_the_first_always_goes()
    {
        using var rig = new RunRig();
        var time = new TickTime(Drives.T0);
        var services = new CheckServices(time, rig.Source, rig.History, rig.Baseline.Engine, Samples.App, rig.NextRunId);
        var log = new ProgressLog();
        var runner = new CheckRunner(
            services,
            [
                new FakeProcedure(CheckKind.Cpu, (c, _) =>
                {
                    foreach (var milliseconds in new[] { 0, 100, 500, 999, 1000, 1400, 1999, 2000, 2001, 3500 })
                    {
                        time.Milliseconds = milliseconds;
                        c.Load(milliseconds / 1000.0, 30, recentRate: milliseconds, rateUnit: "chunks/s");
                    }

                    return Task.CompletedTask;
                }),
            ]);

        await Run(runner, log);

        var loads = log.Items.Where(static p => p.Stage == CheckStage.Loading).ToList();
        Assert.Equal([0.0, 1.0, 2.0, 3.5], loads.Select(static p => p.ElapsedSeconds));
        Assert.All(loads, static p => Assert.Equal(30, p.TargetSeconds));
        Assert.Equal([0.0, 1000.0, 2000.0, 3500.0], loads.Select(static p => p.RecentRate!.Value));
        Assert.All(loads, static p => Assert.Equal("chunks/s", p.RateUnit));
    }

    [Fact]
    public async Task A_report_without_a_handler_is_harmless()
    {
        using var rig = new RunRig();
        var runner = With(rig, (c, _) =>
        {
            c.Stage(CheckStage.Collecting);
            c.Load(1, 30, null, null);
            return Task.CompletedTask;
        });

        var run = await Run(runner);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
    }

    // ---- fakes ----

    private sealed class ThrowsAtOnce : ICheckProcedure
    {
        public CheckKind Kind => CheckKind.Cpu;

        public Task ExecuteAsync(CheckContext context, CancellationToken ct) => throw new InvalidOperationException("thrown before any task exists");
    }

    /// <summary>A clock whose stopwatch is moved by hand (the progress throttle reads the stopwatch, not the date).</summary>
    private sealed class TickTime(DateTimeOffset now) : TimeProvider
    {
        public long Milliseconds { get; set; }

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Milliseconds;

        public override DateTimeOffset GetUtcNow() => now.AddMilliseconds(Milliseconds);
    }
}
