using PcMieru.Checks;

namespace PcMieru.Cli.Tests;

/// <summary>
/// Ctrl+C and the exit code (DESIGN-A 8): the handler only cancels; Main owns the code; the watchdog leaves only when Main has not returned
/// by the end of the grace; a second Ctrl+C waits while the shutdown is held; a completed run keeps its code on a late cancel. Everything is
/// driven by gates (the grace is a task the test completes, "held" is a flag the test sets); no test waits for time to pass. The 30 second
/// bounds below only stop a broken run from hanging the suite; no assertion is about time.
/// </summary>
public class ShutdownCoordinatorTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private sealed class Rig : IDisposable
    {
        private readonly object _lock = new();
        private readonly List<int> _exits = [];

        public Rig(Func<bool>? isHeld = null)
        {
            Coordinator = new ShutdownCoordinator(
                Cancel,
                code =>
                {
                    lock (_lock)
                    {
                        _exits.Add(code);
                    }
                },
                () => Leave.Task,
                isHeld ?? (() => Volatile.Read(ref _held)));
        }

        public CancellationTokenSource Cancel { get; } = new();

        /// <summary>Completes the grace: what the watchdog waits for.</summary>
        public TaskCompletionSource Leave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ShutdownCoordinator Coordinator { get; }

        private bool _held;

        public bool Held
        {
            set => Volatile.Write(ref _held, value);
        }

        public IReadOnlyList<int> Exits
        {
            get
            {
                lock (_lock)
                {
                    return [.. _exits];
                }
            }
        }

        /// <summary>The grace is over; waits until the watchdog has done what it does with that.</summary>
        public async Task EndGraceAsync()
        {
            Leave.SetResult();
            await Coordinator.Watchdog!.WaitAsync(Bound);
        }

        public void Dispose() => Cancel.Dispose();
    }

    // ---- the first Ctrl+C --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_Ctrl_C_only_cancels_and_the_watchdog_leaves_with_1_when_Main_has_not_returned_by_the_end_of_the_grace()
    {
        using var rig = new Rig();
        Assert.False(rig.Coordinator.Token.IsCancellationRequested);
        Assert.Null(rig.Coordinator.Watchdog);

        rig.Coordinator.CtrlC();

        Assert.True(rig.Coordinator.Token.IsCancellationRequested);
        Assert.Empty(rig.Exits);
        var watchdog = rig.Coordinator.Watchdog;
        Assert.NotNull(watchdog);
        Assert.False(watchdog.IsCompleted, "the process must not leave before the grace is over");

        await rig.EndGraceAsync();

        Assert.Equal([1], rig.Exits);

        // The process is leaving with 1; a Main that finishes meanwhile reports the same.
        Assert.Equal(1, rig.Coordinator.Complete(0));
    }

    [Fact]
    public async Task The_watchdog_leaves_a_Main_alone_that_returned_before_the_end_of_the_grace()
    {
        using var rig = new Rig();
        rig.Coordinator.CtrlC();

        Assert.Equal(0, rig.Coordinator.Complete(0));
        await rig.EndGraceAsync();

        Assert.Empty(rig.Exits);
    }

    // ---- the second Ctrl+C -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_second_Ctrl_C_leaves_at_once_when_nothing_holds_the_shutdown()
    {
        using var rig = new Rig();

        rig.Coordinator.CtrlC();
        Assert.Empty(rig.Exits);
        rig.Coordinator.CtrlC();

        Assert.Equal([1], rig.Exits);

        // The watchdog of the first press finds the process already leaving: one exit, not two.
        await rig.EndGraceAsync();
        Assert.Equal([1], rig.Exits);
    }

    [Fact]
    public async Task A_second_Ctrl_C_does_not_leave_while_the_shutdown_is_held_and_waits_for_the_same_grace()
    {
        using var rig = new Rig();
        rig.Held = true;

        rig.Coordinator.CtrlC();
        rig.Coordinator.CtrlC();
        rig.Coordinator.CtrlC();

        Assert.Empty(rig.Exits);
        Assert.True(rig.Coordinator.Token.IsCancellationRequested);

        await rig.EndGraceAsync();

        Assert.Equal([1], rig.Exits);
    }

    [Fact]
    public async Task A_held_run_that_finishes_before_the_grace_keeps_its_code_through_a_second_Ctrl_C()
    {
        using var rig = new Rig();
        rig.Held = true;
        rig.Coordinator.CtrlC();
        rig.Coordinator.CtrlC();

        Assert.Equal(1, rig.Coordinator.Complete(1));
        await rig.EndGraceAsync();

        Assert.Empty(rig.Exits);
    }

    [Fact]
    public void A_Ctrl_C_after_Main_has_returned_changes_nothing()
    {
        using var rig = new Rig();
        Assert.Equal(0, rig.Coordinator.Complete(0));

        rig.Coordinator.CtrlC();
        rig.Coordinator.CtrlC();

        Assert.False(rig.Coordinator.Token.IsCancellationRequested);
        Assert.Null(rig.Coordinator.Watchdog);
        Assert.Empty(rig.Exits);
    }

    [Fact]
    public void A_Ctrl_C_that_comes_after_the_token_source_was_disposed_does_not_throw()
    {
        var rig = new Rig();
        rig.Dispose();

        rig.Coordinator.CtrlC();

        Assert.Empty(rig.Exits);
    }

    // ---- the exit code is Main's --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task A_run_that_completed_keeps_its_code_when_the_cancel_comes_late(int code)
    {
        using var rig = new Rig();
        var flushed = false;

        var exit = await rig.Coordinator.RunAsync(
            _ =>
            {
                // The run is over; the cancel lands before Main has returned its code.
                rig.Coordinator.CtrlC();
                return Task.FromResult(code);
            },
            () =>
            {
                flushed = true;
                return Task.CompletedTask;
            });

        Assert.Equal(code, exit);
        Assert.True(flushed);
        Assert.True(rig.Coordinator.Token.IsCancellationRequested);
        await rig.EndGraceAsync();
        Assert.Empty(rig.Exits);
    }

    [Fact]
    public async Task The_flush_runs_when_the_body_throws_and_the_exception_is_not_swallowed()
    {
        using var rig = new Rig();
        var flushed = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Coordinator.RunAsync(
            _ => throw new InvalidOperationException("boom"),
            () =>
            {
                flushed = true;
                return Task.CompletedTask;
            }));

        Assert.True(flushed);
    }

    // ---- through a check ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ctrl_C_during_a_check_gives_the_stopped_saved_result_and_exit_1_and_two_presses_do_not_cut_the_held_save_short()
    {
        using var data = new TempFolder();

        // The real hold of CliShutdown, as Program uses it: the check holds the shutdown from its start to its last byte.
        using var rig = new Rig(() => CliShutdown.IsHeld);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeCheckRunner(async (_, _, ct) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // The runner finishes its chunk, saves the stopped result and returns it (here: when the test lets it).
            }

            await saved.Task;
            return FakeCheckRunner.Saved(CheckSamples.SystemStopped());
        });
        CliResult? result = null;

        var running = rig.Coordinator.RunAsync(async ct =>
        {
            result = await CliRun.CheckAsync(["check", "system", "--json"], data, runner, ct: ct);
            return result.ExitCode;
        });
        await started.Task.WaitAsync(Bound);
        Assert.True(CliShutdown.IsHeld);

        rig.Coordinator.CtrlC();
        rig.Coordinator.CtrlC();
        rig.Coordinator.CtrlC();

        Assert.True(rig.Coordinator.Token.IsCancellationRequested);
        Assert.Empty(rig.Exits);
        saved.SetResult();
        var exit = await running.WaitAsync(Bound);

        Assert.Equal(1, exit);
        Assert.NotNull(result);
        Assert.Equal(CheckJson.Serialize(CheckSamples.SystemStopped()) + "\n", result.Out);
        Assert.Equal("pcmi check: " + CliWords.En("cli.check.stopped") + "\n", result.Err);
        Assert.False(CliShutdown.IsHeld);
        await rig.EndGraceAsync();
        Assert.Empty(rig.Exits);
    }

    [Fact]
    public async Task A_check_that_completed_just_before_the_Ctrl_C_is_exit_0_with_its_result()
    {
        using var data = new TempFolder();
        using var rig = new Rig();
        var runner = new FakeCheckRunner((_, _, _) =>
        {
            // The run completed and was saved; the cancel arrives as the runner hands it back.
            rig.Coordinator.CtrlC();
            return Task.FromResult(FakeCheckRunner.Saved(CheckSamples.SystemOk()));
        });
        CliResult? result = null;

        var exit = await rig.Coordinator.RunAsync(async ct =>
        {
            result = await CliRun.CheckAsync(["check", "system", "--json"], data, runner, ct: ct);
            return result.ExitCode;
        });

        Assert.True(rig.Coordinator.Token.IsCancellationRequested);
        Assert.Equal(0, exit);
        Assert.NotNull(result);
        Assert.Equal(CheckJson.Serialize(CheckSamples.SystemOk()) + "\n", result.Out);
        Assert.Equal("", result.Err);
        await rig.EndGraceAsync();
        Assert.Empty(rig.Exits);
    }
}
