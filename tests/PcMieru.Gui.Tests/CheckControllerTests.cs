using PcMieru.Checks;

namespace PcMieru.Gui.Tests;

/// <summary>
/// The window's one check run (DESIGN-A §7): every state it can be in, a stop while it runs, a run that fails, a result that could not be
/// saved, and the rule of one run at a time. The runner is a scripted stand-in (no load, no WMI, no disk, no prompt), and the "UI thread" is a
/// queue the test pumps, so what the controller posts to the UI can be looked at. No test asserts on how long anything takes.
/// </summary>
public class CheckControllerTests
{
    private static CheckRequest Request(CheckKind kind, int? seconds = null) =>
        new(kind, seconds is { } s ? TimeSpan.FromSeconds(s) : null);

    [Fact]
    public void A_new_controller_has_no_run_and_makes_no_runner_until_a_check_starts()
    {
        var queue = new RunnerQueue(new ScriptedRunner());
        using var controller = queue.Controller();

        Assert.Equal(CheckRunState.NotRun, controller.State);
        Assert.Null(controller.Active);
        Assert.Null(controller.ActiveRequest);
        Assert.False(controller.IsBusy);
        Assert.Null(controller.Progress);
        foreach (var kind in Enum.GetValues<CheckKind>())
        {
            Assert.Equal(CheckRunState.NotRun, controller.StateOf(kind).State);
            Assert.Null(controller.StateOf(kind).Run);
        }

        Assert.False(controller.Stop());
        Assert.True(controller.StopAndWaitAsync().IsCompleted);
        Assert.True(controller.WhenIdleAsync().IsCompleted);
        Assert.Equal(0, queue.Made);
    }

    [Fact]
    public void A_check_goes_from_not_run_to_running_to_completed_and_keeps_its_result()
    {
        Ui.Run(ui =>
        {
            var runner = new ScriptedRunner();
            var queue = new RunnerQueue(runner);
            using var controller = queue.Controller();
            var events = new List<string>();
            var threads = new List<int>();
            controller.Changed += () =>
            {
                events.Add("changed");
                threads.Add(Environment.CurrentManagedThreadId);
            };
            controller.RunFinished += kind =>
            {
                events.Add("finished:" + kind);
                threads.Add(Environment.CurrentManagedThreadId);
            };

            Assert.True(controller.TryStart(Request(CheckKind.System)));
            Assert.Equal(CheckRunState.Running, controller.State);
            Assert.Equal(CheckKind.System, controller.Active);
            Assert.True(controller.IsBusy);
            Assert.Equal(CheckRunState.Running, controller.StateOf(CheckKind.System).State);
            Assert.Equal(CheckRunState.NotRun, controller.StateOf(CheckKind.Storage).State);

            // What the controller tells the screen is posted to the UI thread: nothing is delivered until the UI pumps.
            Assert.Empty(events);
            Assert.True(ui.Until(() => runner.Started.IsCompleted));
            Assert.Equal(1, queue.Made);

            runner.Release();
            Assert.True(ui.Until(() => events.Contains("finished:System")));
            Assert.Equal(CheckRunState.Completed, controller.State);
            var state = controller.StateOf(CheckKind.System);
            Assert.Equal(CheckRunState.Completed, state.State);
            Assert.NotNull(state.Run);
            Assert.Equal(CheckOutcome.Completed, state.Run.Result.Outcome);
            Assert.True(state.Run.IsSaved);
            Assert.False(state.SaveFailed);
            Assert.Null(state.Error);
            Assert.Null(controller.Active);
            Assert.Null(controller.Progress);
            Assert.Equal("finished:System", events[^1]);
            Assert.Equal("changed", events[^2]);
            Assert.All(threads, id => Assert.Equal(ui.OwnerThread, id));
            Assert.True(controller.WhenIdleAsync().IsCompleted);
        });
    }

    [Fact]
    public void A_stop_while_the_check_runs_is_stopping_then_stopped_with_the_saved_partial_result()
    {
        Ui.Run(ui =>
        {
            var runner = new ScriptedRunner();
            using var controller = new RunnerQueue(runner).Controller();
            Assert.True(controller.TryStart(Request(CheckKind.Memory, 30)));
            Assert.True(ui.Until(() => runner.Started.IsCompleted));
            Assert.Equal(CheckRunState.Running, controller.State);

            Assert.True(controller.Stop());
            Assert.Equal(CheckRunState.Stopping, controller.State);
            Assert.Equal(CheckRunState.Stopping, controller.StateOf(CheckKind.Memory).State);
            Assert.True(controller.IsBusy);
            Assert.False(controller.Stop()); // a second request does not stop twice

            Assert.True(ui.Until(() => !controller.IsBusy));
            Assert.True(runner.WasStopped);
            Assert.Equal(CheckRunState.Stopped, controller.State);
            var run = controller.StateOf(CheckKind.Memory).Run;
            Assert.NotNull(run);
            Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
            Assert.True(run.IsSaved);
            Assert.Equal(1, runner.Runs);
        });
    }

    [Fact]
    public void A_runner_that_throws_makes_the_run_failed_with_the_reason_and_the_next_check_can_start()
    {
        Ui.Run(ui =>
        {
            using var _ = Harness.Locale();
            var broken = new ScriptedRunner { Hold = false, Failure = new InvalidOperationException("the helper is gone") };
            var fine = new ScriptedRunner { Hold = false };
            using var controller = new RunnerQueue(broken, fine).Controller();

            Assert.True(controller.TryStart(Request(CheckKind.Storage)));
            Assert.True(ui.Until(() => !controller.IsBusy));
            var state = controller.StateOf(CheckKind.Storage);
            Assert.Equal(CheckRunState.Failed, state.State);
            Assert.Equal("the helper is gone", state.Error);
            Assert.Null(state.Run);
            Assert.Equal(CheckRunState.Failed, controller.State);
            Assert.Contains("the helper is gone", CheckFormat.StateText(state), StringComparison.Ordinal);

            Assert.True(controller.TryStart(Request(CheckKind.Storage)));
            Assert.True(ui.Until(() => !controller.IsBusy));
            Assert.Equal(CheckRunState.Completed, controller.StateOf(CheckKind.Storage).State);
            Assert.Null(controller.StateOf(CheckKind.Storage).Error);
        });
    }

    [Fact]
    public void A_run_the_runner_calls_failed_is_failed_and_says_why_in_words()
    {
        Ui.Run(ui =>
        {
            using var _ = Harness.Locale();
            var runner = new ScriptedRunner { Hold = false, Outcome = CheckOutcome.Failed };
            using var controller = new RunnerQueue(runner).Controller();

            Assert.True(controller.TryStart(Request(CheckKind.System)));
            Assert.True(ui.Until(() => !controller.IsBusy));
            var state = controller.StateOf(CheckKind.System);
            Assert.Equal(CheckRunState.Failed, state.State);
            Assert.NotNull(state.Run);
            Assert.Equal(CheckRunFailureCodes.InternalError, state.Run.FailureCode);
            Assert.Contains(CheckFormat.FailureText(CheckRunFailureCodes.InternalError), CheckFormat.StateText(state), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_result_that_could_not_be_saved_is_completed_with_the_save_failure_appended_and_the_result_kept()
    {
        Ui.Run(ui =>
        {
            using var _ = Harness.Locale();
            var runner = new ScriptedRunner { Hold = false, Save = CheckSaveState.Failed(CheckSaveFailureCodes.DiskFull, "scripted") };
            using var controller = new RunnerQueue(runner).Controller();

            Assert.True(controller.TryStart(Request(CheckKind.System)));
            Assert.True(ui.Until(() => !controller.IsBusy));
            var state = controller.StateOf(CheckKind.System);
            Assert.Equal(CheckRunState.Completed, state.State); // the measurement is done; the save is a separate fact
            Assert.True(state.SaveFailed);
            Assert.NotNull(state.Run);
            Assert.False(state.Run.IsSaved);
            Assert.Equal(CheckOutcome.Completed, state.Run.Result.Outcome);
            Assert.NotEmpty(state.Run.Result.Items); // the result stays on the screen

            var text = CheckFormat.StateText(state);
            var completed = Strings.Get("gui.chk.state.completed");
            var failure = CheckFormat.SaveFailureText(CheckSaveFailureCodes.DiskFull);
            Assert.StartsWith(completed, text, StringComparison.Ordinal);
            Assert.Contains(failure, text, StringComparison.Ordinal);
            Assert.True(text.IndexOf(failure, StringComparison.Ordinal) > text.IndexOf(completed, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void A_save_that_went_well_adds_nothing_to_the_state_text()
    {
        Ui.Run(ui =>
        {
            using var _ = Harness.Locale();
            var runner = new ScriptedRunner { Hold = false };
            using var controller = new RunnerQueue(runner).Controller();

            Assert.True(controller.TryStart(Request(CheckKind.System)));
            Assert.True(ui.Until(() => !controller.IsBusy));
            Assert.Equal(Strings.Get("gui.chk.state.completed"), CheckFormat.StateText(controller.StateOf(CheckKind.System)));
        });
    }

    [Fact]
    public void Every_state_has_its_own_words_in_both_languages()
    {
        var run = new CheckRun(FakeCheckData.SystemCheck(FakeCheckData.SystemRun, FakeCheckData.Start), CheckSaveState.Saved("x.json"));
        CheckKindState[] states =
        [
            CheckKindState.NotRun,
            new(CheckRunState.Running, null, null),
            new(CheckRunState.Stopping, null, null),
            new(CheckRunState.Completed, run, null),
            new(CheckRunState.Stopped, run, null),
            new(CheckRunState.Failed, null, "reason"),
        ];
        foreach (var language in new[] { Language.Ja, Language.En })
        {
            using var _ = Harness.Locale(language);
            var texts = states.Select(CheckFormat.StateText).ToArray();
            Assert.Equal(states.Length, texts.Distinct().Count());
            Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text) || text.StartsWith('[')));
        }
    }

    [Fact]
    public void Only_one_run_at_a_time_a_start_while_one_runs_or_stops_is_refused_and_makes_no_second_runner()
    {
        Ui.Run(ui =>
        {
            var first = new ScriptedRunner();
            var second = new ScriptedRunner { Hold = false };
            var queue = new RunnerQueue(first, second);
            using var controller = queue.Controller();

            Assert.True(controller.TryStart(Request(CheckKind.Cpu, 30)));
            Assert.False(controller.TryStart(Request(CheckKind.System)));
            Assert.False(controller.TryStart(Request(CheckKind.Cpu, 30)));
            Assert.True(ui.Until(() => first.Started.IsCompleted));
            Assert.False(controller.TryStart(Request(CheckKind.Memory, 120)));

            Assert.True(controller.Stop());
            Assert.False(controller.TryStart(Request(CheckKind.System))); // stopping still counts as a run
            Assert.Equal(1, queue.Made);

            Assert.True(ui.Until(() => !controller.IsBusy));
            Assert.Equal(CheckKind.Cpu, Assert.Single(first.Requests).Kind);
            Assert.Equal(CheckRunState.NotRun, controller.StateOf(CheckKind.System).State);

            Assert.True(controller.TryStart(Request(CheckKind.System)));
            Assert.True(ui.Until(() => !controller.IsBusy));
            Assert.Equal(2, queue.Made);
            Assert.Equal(CheckRunState.Completed, controller.StateOf(CheckKind.System).State);
            Assert.Equal(CheckRunState.Stopped, controller.StateOf(CheckKind.Cpu).State);
        });
    }

    [Fact]
    public void A_request_that_breaks_the_rules_is_refused_before_anything_starts()
    {
        var queue = new RunnerQueue(new ScriptedRunner());
        using var controller = queue.Controller();

        Assert.False(controller.TryStart(new CheckRequest(CheckKind.System, TimeSpan.FromSeconds(30))));
        Assert.False(controller.TryStart(new CheckRequest(CheckKind.Cpu, TimeSpan.FromSeconds(45))));
        Assert.False(controller.TryStart(new CheckRequest(CheckKind.Cpu, AllowElevation: true)));
        Assert.False(controller.TryStart(new CheckRequest((CheckKind)99)));
        Assert.False(controller.IsBusy);
        Assert.Equal(0, queue.Made);
        Assert.Throws<ArgumentNullException>(() => controller.TryStart(null!));
    }

    [Fact]
    public void A_runner_that_is_stopped_before_it_made_a_run_leaves_the_state_stopped_without_a_result()
    {
        Ui.Run(ui =>
        {
            var runner = new ScriptedRunner { Hold = false, Failure = new OperationCanceledException() };
            using var controller = new RunnerQueue(runner).Controller();

            Assert.True(controller.TryStart(Request(CheckKind.System)));
            Assert.True(ui.Until(() => !controller.IsBusy));
            var state = controller.StateOf(CheckKind.System);
            Assert.Equal(CheckRunState.Stopped, state.State);
            Assert.Null(state.Run);
            Assert.Null(state.Error);
        });
    }

    [Fact]
    public void Each_kind_keeps_the_last_result_it_had_while_another_kind_runs()
    {
        Ui.Run(ui =>
        {
            var system = new ScriptedRunner { Hold = false };
            var load = new ScriptedRunner();
            using var controller = new RunnerQueue(system, load).Controller();

            Assert.True(controller.TryStart(Request(CheckKind.System)));
            Assert.True(ui.Until(() => !controller.IsBusy));
            var systemRun = controller.StateOf(CheckKind.System).Run;
            Assert.NotNull(systemRun);

            Assert.True(controller.TryStart(Request(CheckKind.Cpu, 30)));
            Assert.True(ui.Until(() => load.Started.IsCompleted));
            Assert.Equal(CheckRunState.Running, controller.StateOf(CheckKind.Cpu).State);
            Assert.Equal(CheckRunState.Completed, controller.StateOf(CheckKind.System).State);
            Assert.Same(systemRun, controller.StateOf(CheckKind.System).Run);

            // The state of the latest run of any kind is the one that is going.
            Assert.Equal(CheckRunState.Running, controller.State);
            load.Release();
            Assert.True(ui.Until(() => !controller.IsBusy));
            Assert.Same(systemRun, controller.StateOf(CheckKind.System).Run);
            Assert.Equal(CheckRunState.Completed, controller.StateOf(CheckKind.Cpu).State);
        });
    }

    [Fact]
    public void The_last_progress_is_kept_while_the_run_goes_and_a_late_report_after_the_run_is_dropped()
    {
        Ui.Run(ui =>
        {
            var runner = new ScriptedRunner();
            using var controller = new RunnerQueue(runner).Controller();
            var changed = 0;
            controller.Changed += () => changed++;

            Assert.True(controller.TryStart(Request(CheckKind.Cpu, 30)));
            Assert.True(ui.Until(() => runner.Started.IsCompleted));
            Assert.Null(controller.Progress);

            var first = new CheckProgress(runner.RunId, CheckStage.Loading, 5, 30, 40, "chunks/s");
            var second = new CheckProgress(runner.RunId, CheckStage.Loading, 6, 30, 41, "chunks/s");
            runner.Reporter!.Report(first);
            Assert.Same(first, controller.Progress);
            runner.Reporter.Report(second);
            Assert.Same(second, controller.Progress);

            runner.Release();
            Assert.True(ui.Until(() => !controller.IsBusy));
            Assert.Null(controller.Progress);

            ui.Pump();
            var after = changed;
            runner.Reporter.Report(new CheckProgress(runner.RunId, CheckStage.Loading, 29, 30, 99, "chunks/s"));
            ui.Pump();
            Assert.Null(controller.Progress);
            Assert.Equal(after, changed);
        });
    }

    [Fact]
    public void Progress_and_state_changes_reach_the_ui_context_only_when_it_pumps_and_never_inline_on_the_worker()
    {
        Ui.Run(ui =>
        {
            var runner = new ScriptedRunner();
            using var controller = new RunnerQueue(runner).Controller();
            var delivered = new List<int>();
            controller.Changed += () => delivered.Add(Environment.CurrentManagedThreadId);

            Assert.True(controller.TryStart(Request(CheckKind.Memory, 30)));
            Assert.True(ui.Until(() => runner.Started.IsCompleted));
            ui.Pump();
            delivered.Clear();

            runner.Reporter!.Report(new CheckProgress(runner.RunId, CheckStage.Loading, 1, 30, 5000, "MiB/s"));
            Assert.Empty(delivered); // posted, not delivered
            Assert.True(ui.Queued > 0);
            ui.Pump();
            Assert.NotEmpty(delivered);
            Assert.All(delivered, id => Assert.Equal(ui.OwnerThread, id));

            runner.Release();
            Assert.True(ui.Until(() => !controller.IsBusy));
        });
    }

    [Fact]
    public void Stopping_and_waiting_completes_only_after_the_result_is_saved()
    {
        Ui.Run(ui =>
        {
            var saveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new ScriptedRunner { SaveGate = saveGate };
            using var controller = new RunnerQueue(runner).Controller();
            Assert.True(controller.TryStart(Request(CheckKind.Storage)));
            Assert.True(ui.Until(() => runner.Started.IsCompleted));

            var wait = controller.StopAndWaitAsync();
            Assert.Equal(CheckRunState.Stopping, controller.State);
            Assert.True(ui.Until(() => runner.WasStopped));
            ui.Pump();
            Assert.False(wait.IsCompleted); // the save is still going

            saveGate.SetResult();
            Assert.True(ui.Until(() => wait.IsCompleted));
            Assert.Equal(CheckRunState.Stopped, controller.State);
            Assert.True(controller.StateOf(CheckKind.Storage).Run!.IsSaved);
        });
    }

    [Fact]
    public void Disposing_the_controller_stops_a_run_that_is_going_and_refuses_new_starts()
    {
        Ui.Run(ui =>
        {
            var runner = new ScriptedRunner();
            var queue = new RunnerQueue(runner, new ScriptedRunner());
            var controller = queue.Controller();
            Assert.True(controller.TryStart(Request(CheckKind.Cpu, 30)));
            Assert.True(ui.Until(() => runner.Started.IsCompleted));

            controller.Dispose();
            Assert.True(ui.Until(() => runner.WasStopped));
            Assert.True(ui.Until(() => !controller.IsBusy));
            Assert.False(controller.TryStart(Request(CheckKind.System)));
            Assert.Equal(1, queue.Made);
            controller.Dispose(); // a second dispose is harmless
        });
    }
}
