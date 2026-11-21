using PcMieru.Checks;

namespace PcMieru.Gui.Tests;

/// <summary>
/// What the window does when it is asked to close while a check runs (DESIGN-A §7): the first request is cancelled and asked about; on yes the
/// stop is requested, the stop and the save are awaited, and only then is the window closed; a second request while that goes on neither asks again nor
/// stops again; on no nothing changes. The first half tests the guard alone, the second half the real <see cref="MainForm"/> (a handle, never shown).
/// The runner is scripted: no load, no WMI, no disk, no prompt. Whether something has or has not happened yet is decided by gates the test holds
/// (the save is "still going" until the test opens it), never by the clock.
/// </summary>
public class CheckClosingTests
{
    private static CheckRequest Request(CheckKind kind = CheckKind.System) => new(kind);

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- the guard alone ----

    private sealed class GuardRig : IDisposable
    {
        public GuardRig(ScriptedRunner runner, bool answer)
        {
            Runner = runner;
            Answer = answer;
            Controller = new RunnerQueue(runner).Controller();
            Guard = new CheckCloseGuard(
                Controller,
                () =>
                {
                    Asked++;
                    DuringAsk?.Invoke();
                    return Answer;
                },
                () =>
                {
                    Closed++;
                    AtClose = Controller.StateOf(CheckKind.System);
                    BusyAtClose = Controller.IsBusy;
                });
        }

        public ScriptedRunner Runner { get; }

        public CheckController Controller { get; }

        public CheckCloseGuard Guard { get; }

        public bool Answer { get; set; }

        public Action? DuringAsk { get; set; }

        public int Asked { get; private set; }

        public int Closed { get; private set; }

        public CheckKindState? AtClose { get; private set; }

        public bool? BusyAtClose { get; private set; }

        public void Dispose() => Controller.Dispose();
    }

    private static void StartAndWait(GuardRig rig, QueueContext ui)
    {
        Assert.True(rig.Controller.TryStart(Request()));
        Assert.True(ui.Until(() => rig.Runner.Started.IsCompleted));
    }

    [Fact]
    public void With_no_check_running_the_close_goes_straight_through_without_a_question()
    {
        Ui.Run(ui =>
        {
            using var rig = new GuardRig(new ScriptedRunner(), answer: true);

            Assert.True(rig.Guard.OnCloseRequested());
            Assert.Equal(0, rig.Asked);
            Assert.Equal(0, rig.Closed);
            Assert.Null(rig.Guard.Pending);

            // A check that is over is no reason to ask either.
            StartAndWait(rig, ui);
            rig.Runner.Release();
            Assert.True(ui.Until(() => !rig.Controller.IsBusy));
            Assert.True(rig.Guard.OnCloseRequested());
            Assert.Equal(0, rig.Asked);
            Assert.Null(rig.Guard.Pending);
        });
    }

    [Fact]
    public void With_a_check_running_the_first_close_is_cancelled_and_the_user_is_asked_once()
    {
        Ui.Run(ui =>
        {
            using var rig = new GuardRig(new ScriptedRunner(), answer: false);
            StartAndWait(rig, ui);

            Assert.False(rig.Guard.OnCloseRequested());
            Assert.Equal(1, rig.Asked);
        });
    }

    [Fact]
    public void No_changes_nothing_the_run_goes_on_and_the_next_close_asks_again()
    {
        Ui.Run(ui =>
        {
            using var rig = new GuardRig(new ScriptedRunner(), answer: false);
            StartAndWait(rig, ui);

            Assert.False(rig.Guard.OnCloseRequested());
            Assert.Equal(1, rig.Asked);
            Assert.Equal(0, rig.Closed);
            Assert.Null(rig.Guard.Pending);
            Assert.True(rig.Controller.IsBusy);
            Assert.Equal(CheckRunState.Running, rig.Controller.State);
            Assert.False(rig.Runner.WasStopped);

            // Pumping the UI changes nothing: no stop is on its way.
            ui.Pump();
            Assert.Equal(CheckRunState.Running, rig.Controller.State);
            Assert.Equal(0, rig.Closed);

            Assert.False(rig.Guard.OnCloseRequested());
            Assert.Equal(2, rig.Asked);
            Assert.Null(rig.Guard.Pending);

            // When the check ends by itself the close is let through, and nobody is asked again.
            rig.Runner.Release();
            Assert.True(ui.Until(() => !rig.Controller.IsBusy));
            Assert.True(rig.Guard.OnCloseRequested());
            Assert.Equal(2, rig.Asked);
            Assert.Equal(0, rig.Closed);
        });
    }

    [Fact]
    public void Yes_stops_the_run_waits_for_the_save_and_only_then_closes_once()
    {
        Ui.Run(ui =>
        {
            var save = Gate();
            using var rig = new GuardRig(new ScriptedRunner { SaveGate = save }, answer: true);
            StartAndWait(rig, ui);

            Assert.False(rig.Guard.OnCloseRequested());
            Assert.Equal(1, rig.Asked);
            var pending = rig.Guard.Pending;
            Assert.NotNull(pending);

            // The stop was asked for, and it is being waited for: the save is still open, so nothing closed.
            Assert.True(ui.Until(() => rig.Runner.WasStopped));
            Assert.Equal(CheckRunState.Stopping, rig.Controller.State);
            ui.Pump();
            Assert.Equal(0, rig.Closed);
            Assert.False(pending.IsCompleted);

            save.SetResult();
            Assert.True(ui.Until(() => rig.Closed == 1));
            Assert.True(ui.Until(() => pending.IsCompleted));
            Assert.Equal(1, rig.Closed);

            // At the moment of the close the run was over and its (stopped) result was saved.
            Assert.False(rig.BusyAtClose);
            Assert.NotNull(rig.AtClose);
            Assert.Equal(CheckRunState.Stopped, rig.AtClose.State);
            Assert.NotNull(rig.AtClose.Run);
            Assert.True(rig.AtClose.Run.IsSaved);
            Assert.Equal(CheckOutcome.Stopped, rig.AtClose.Run.Result.Outcome);

            // The close that follows is let through, without a second question and without a second close.
            Assert.True(rig.Guard.OnCloseRequested());
            Assert.Equal(1, rig.Asked);
            Assert.Equal(1, rig.Closed);
            Assert.Equal(1, rig.Runner.Runs);
            Assert.Same(pending, rig.Guard.Pending);
        });
    }

    [Fact]
    public void The_close_is_never_made_inside_the_request_that_asked_even_when_the_run_ended_while_the_question_was_open()
    {
        Ui.Run(ui =>
        {
            using var rig = new GuardRig(new ScriptedRunner(), answer: true);
            StartAndWait(rig, ui);

            // The question is open (it has its own message loop) and the check finishes meanwhile: nothing is left to wait for.
            rig.DuringAsk = () =>
            {
                rig.Runner.Release();
                Assert.True(ui.Until(() => !rig.Controller.IsBusy));
            };

            Assert.False(rig.Guard.OnCloseRequested());
            Assert.Equal(0, rig.Closed);
            Assert.NotNull(rig.Guard.Pending);

            Assert.True(ui.Until(() => rig.Closed == 1));
            Assert.Equal(1, rig.Asked);
            Assert.Equal(1, rig.Closed);
            Assert.True(rig.Guard.OnCloseRequested());
        });
    }

    [Fact]
    public void A_second_close_while_the_question_is_open_or_the_stop_goes_on_neither_asks_nor_stops_again()
    {
        Ui.Run(ui =>
        {
            var save = Gate();
            using var rig = new GuardRig(new ScriptedRunner { SaveGate = save }, answer: true);
            StartAndWait(rig, ui);

            // A close request that arrives from inside the open question (its message loop delivers it).
            var inside = new List<bool>();
            rig.DuringAsk = () => inside.Add(rig.Guard.OnCloseRequested());

            Assert.False(rig.Guard.OnCloseRequested());
            Assert.Equal(new[] { false }, inside);
            Assert.Equal(1, rig.Asked);
            var pending = rig.Guard.Pending;
            Assert.NotNull(pending);

            // And more, while the stop and the save are going on.
            Assert.True(ui.Until(() => rig.Runner.WasStopped));
            for (var i = 0; i < 3; i++)
            {
                Assert.False(rig.Guard.OnCloseRequested());
                Assert.Same(pending, rig.Guard.Pending);
            }

            Assert.Equal(1, rig.Asked);
            Assert.Equal(0, rig.Closed);
            Assert.Equal(1, rig.Runner.Runs);

            save.SetResult();
            Assert.True(ui.Until(() => rig.Closed == 1));
            Assert.True(ui.Until(() => pending.IsCompleted));

            // Nothing else is queued behind it: there was one stop-and-close, so there is one close.
            ui.Pump();
            Assert.Equal(1, rig.Closed);
            Assert.Equal(1, rig.Asked);
            Assert.Same(pending, rig.Guard.Pending);
        });
    }

    [Fact]
    public void When_windows_ends_the_session_the_stop_is_requested_and_the_close_goes_on_without_a_question()
    {
        Ui.Run(ui =>
        {
            using var rig = new GuardRig(new ScriptedRunner(), answer: false);
            StartAndWait(rig, ui);

            Assert.True(rig.Guard.OnCloseRequested(systemEnding: true));
            Assert.Equal(0, rig.Asked);
            Assert.Null(rig.Guard.Pending);
            Assert.True(ui.Until(() => rig.Runner.WasStopped));
            Assert.True(ui.Until(() => !rig.Controller.IsBusy));
        });
    }

    [Fact]
    public void A_question_that_fails_leaves_nothing_stuck_and_the_next_close_asks_again()
    {
        Ui.Run(ui =>
        {
            using var rig = new GuardRig(new ScriptedRunner(), answer: false);
            StartAndWait(rig, ui);

            var broken = true;
            rig.DuringAsk = () =>
            {
                if (broken)
                {
                    throw new InvalidOperationException("the dialog could not open");
                }
            };

            Assert.Throws<InvalidOperationException>(() => rig.Guard.OnCloseRequested());
            Assert.Null(rig.Guard.Pending);
            Assert.True(rig.Controller.IsBusy);

            broken = false;
            Assert.False(rig.Guard.OnCloseRequested());
            Assert.Equal(2, rig.Asked);
        });
    }

    // ---- the real window ----

    /// <summary>A real main window with a handle that is never shown (nothing is drawn or put in front), on a fake-data service set and a scripted controller.</summary>
    private sealed class FormRig : IDisposable
    {
        private readonly IDisposable _locale = Harness.Locale();
        private readonly ThemeService _theme = new(ThemeMode.Light);

        public FormRig(params ScriptedRunner[] runners)
        {
            Queue = new RunnerQueue(runners);
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Checks = Queue.Controller();
            var context = new GuiContext(Services, Pump, _theme, new GuiSettings(), checks: Checks);
            Form = new MainForm(context);
            _ = Form.Handle;
            Form.FormClosing += (_, _) =>
            {
                ClosingBusy.Add(Checks.IsBusy);
                AtClosing = Checks.StateOf(CheckKind.System);
            };
            Form.FormClosed += (_, _) => Closed++;
            Dialogs.AutoAnswer = null;
            Dialogs.Log.Clear();
        }

        public RunnerQueue Queue { get; }

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public CheckController Checks { get; }

        public MainForm Form { get; }

        public int Closed { get; private set; }

        /// <summary>One entry per time the window really went on closing (a cancelled request does not raise the event): was a check still going then?</summary>
        public List<bool> ClosingBusy { get; } = [];

        public CheckKindState? AtClosing { get; private set; }

        public void Dispose()
        {
            Dialogs.AutoAnswer = null;
            Dialogs.Log.Clear();
            Form.Dispose();
            Checks.Dispose();
            Pump.Dispose();
            Services.Dispose();
            _theme.Dispose();
            _locale.Dispose();
        }
    }

    [Fact]
    public void The_window_with_no_check_closes_at_once_and_asks_nothing()
    {
        Ui.Run(_ =>
        {
            using var rig = new FormRig();

            rig.Form.Close();

            Assert.Equal(1, rig.Closed);
            Assert.True(rig.Form.IsDisposed);
            Assert.Empty(Dialogs.Log);
            Assert.Equal(new[] { false }, rig.ClosingBusy);
            Assert.Equal(0, rig.Queue.Made);
        });
    }

    [Fact]
    public void The_window_with_a_check_running_cancels_the_close_and_on_no_nothing_changes()
    {
        Ui.Run(ui =>
        {
            var runner = new ScriptedRunner();
            using var rig = new FormRig(runner);
            Assert.True(rig.Checks.TryStart(Request()));
            Assert.True(ui.Until(() => runner.Started.IsCompleted));
            Dialogs.AutoAnswer = false;

            rig.Form.Close();

            Assert.Equal(0, rig.Closed);
            Assert.False(rig.Form.IsDisposed);
            Assert.Empty(rig.ClosingBusy);
            var asked = Assert.Single(Dialogs.Log);
            Assert.Contains("A check is running. Stop it and close?", asked, StringComparison.Ordinal);
            Assert.Null(rig.Form.ClosePending);
            Assert.True(rig.Checks.IsBusy);
            Assert.False(runner.WasStopped);
            Assert.Equal(CheckRunState.Running, rig.Checks.State);

            // The window is still there and still asks the next time.
            rig.Form.Close();
            Assert.Equal(2, Dialogs.Log.Count);
            Assert.Equal(0, rig.Closed);

            // Once the check is over the window closes without a question.
            runner.Release();
            Assert.True(ui.Until(() => !rig.Checks.IsBusy));
            rig.Form.Close();
            Assert.Equal(1, rig.Closed);
            Assert.Equal(2, Dialogs.Log.Count);
            Assert.Equal(new[] { false }, rig.ClosingBusy);
        });
    }

    [Fact]
    public void The_window_with_a_check_running_stops_it_waits_for_the_save_and_only_then_closes_and_a_second_close_meanwhile_does_nothing()
    {
        Ui.Run(ui =>
        {
            var save = Gate();
            var runner = new ScriptedRunner { SaveGate = save };
            using var rig = new FormRig(runner);
            Assert.True(rig.Checks.TryStart(Request()));
            Assert.True(ui.Until(() => runner.Started.IsCompleted));
            Dialogs.AutoAnswer = true;

            rig.Form.Close();

            // The first request was cancelled; the stop is under way and the window is still open.
            Assert.Equal(0, rig.Closed);
            Assert.False(rig.Form.IsDisposed);
            Assert.Single(Dialogs.Log);
            var pending = rig.Form.ClosePending;
            Assert.NotNull(pending);
            Assert.False(pending.IsCompleted);

            // A second close while it stops: no second question, no second stop, no close.
            rig.Form.Close();
            rig.Form.Close();
            Assert.Single(Dialogs.Log);
            Assert.Equal(0, rig.Closed);
            Assert.Same(pending, rig.Form.ClosePending);

            Assert.True(ui.Until(() => runner.WasStopped));
            ui.Pump();
            Assert.Equal(0, rig.Closed);
            Assert.False(rig.Form.IsDisposed);
            Assert.Equal(CheckRunState.Stopping, rig.Checks.State);

            save.SetResult();
            Assert.True(ui.Until(() => rig.Closed == 1));
            Assert.True(rig.Form.IsDisposed);

            // It went on closing exactly once, after the run was over and its stopped result was saved.
            Assert.Equal(new[] { false }, rig.ClosingBusy);
            Assert.NotNull(rig.AtClosing);
            Assert.Equal(CheckRunState.Stopped, rig.AtClosing.State);
            Assert.NotNull(rig.AtClosing.Run);
            Assert.True(rig.AtClosing.Run.IsSaved);
            Assert.Single(Dialogs.Log);
            Assert.Equal(1, runner.Runs);
        });
    }
}
