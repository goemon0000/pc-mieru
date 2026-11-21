namespace PcMieru.Gui;

/// <summary>
/// What the window does when it is asked to close while a check runs (DESIGN-A §7). The first request is cancelled and the user is
/// asked; on yes the stop is requested, the stop and the save are awaited for at most <see cref="StopWait"/> (never blocking the UI
/// thread), and then the window is closed again whether or not the run has ended. Meanwhile any further close request is cancelled
/// without asking again and without a second stop: the guard holds the re-entry to one. On no, nothing changed. With no check running
/// the close goes straight through. Windows ending the session or the task manager closing the window is never asked about and never
/// cancelled, in any phase: the stop is requested and the close goes on.
/// </summary>
/// <param name="controller">The run owner of the window.</param>
/// <param name="confirm">The question; true is yes.</param>
/// <param name="close">Closes the window (called once, after the stop wait).</param>
/// <param name="announceStopping">Told once when yes was answered and a run is being stopped (the status line says it will close when the run ends).</param>
/// <param name="delay">The bound of the stop wait: completes after the given time, or is cancelled. A test drives it by hand; the default is <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
internal sealed class CheckCloseGuard(
    CheckController controller,
    Func<bool> confirm,
    Action close,
    Action? announceStopping = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>The longest the window waits for a stopped run to end and save after yes (DESIGN-A §7). A run that does not stop must not keep the window from closing.</summary>
    internal static readonly TimeSpan StopWait = TimeSpan.FromSeconds(15);

    private enum Phase
    {
        /// <summary>No question is open and no stop is under way.</summary>
        Idle,

        /// <summary>The question is on the screen (its message loop can deliver another close request).</summary>
        Asking,

        /// <summary>Yes was answered: the stop is requested and the save is awaited (for at most <see cref="StopWait"/>).</summary>
        Stopping,

        /// <summary>
        /// The close that follows is let through. Never reset, on purpose: the only way out of this phase is the window being disposed (nothing else in
        /// the window cancels a close and nothing reopens it), so a reset would guard a window that no longer exists.
        /// </summary>
        Approved,
    }

    private Phase _phase;

    /// <summary>The stop-and-close that yes started (null before). A test awaits it; the window never does.</summary>
    internal Task? Pending { get; private set; }

    /// <summary>True when the window may go on closing now. False: the request was cancelled (asked, answered no, or held).</summary>
    /// <param name="systemEnding">Windows is shutting down or the task manager closes the window: nobody can answer and a cancelled close is what Windows reports as "this app is stopping the shutdown", so in every phase the stop is requested and the close goes on.</param>
    public bool OnCloseRequested(bool systemEnding = false)
    {
        if (systemEnding)
        {
            // Before the phase switch: a question that is open, a stop that is awaited and an approved close must not turn this into a cancelled close.
            // The save of the stopped run is a best effort after the message loop (Program.cs waits a few seconds); nothing is waited for here.
            _ = controller.Stop();
            return true;
        }

        switch (_phase)
        {
            case Phase.Approved:
                return true;
            case Phase.Asking:
            case Phase.Stopping:
                return false;
        }

        if (!controller.IsBusy)
        {
            return true;
        }

        _phase = Phase.Asking;
        bool yes;
        try
        {
            yes = confirm();
        }
        catch
        {
            _phase = Phase.Idle;
            throw;
        }

        if (!yes)
        {
            _phase = Phase.Idle;
            return false;
        }

        _phase = Phase.Stopping;
        Pending = StopThenCloseAsync();
        return false;
    }

    private async Task StopThenCloseAsync()
    {
        try
        {
            announceStopping?.Invoke();
        }
        catch
        {
            // A status line that cannot be written is no reason to keep the window open.
        }

        try
        {
            var stopped = controller.StopAndWaitAsync();
            using var bound = new CancellationTokenSource();
            var timeout = (delay ?? Task.Delay)(StopWait, bound.Token);

            // Whichever comes first: the run is over and saved, or the bound is up (a run that does not stop must not keep the window open).
            await Task.WhenAny(stopped, timeout).ConfigureAwait(true);
            bound.Cancel();
            if (!stopped.IsCompleted)
            {
                // The run is still going and the window closes anyway: nobody is left to read the stopped task, so its fault (if any) is read here.
                _ = stopped.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
        catch
        {
            // A run that cannot be waited for must not keep the window open: the user asked to close it.
        }

        // Never close from inside the call that asked: a run that ended while the question was open has nothing to wait for, and the close
        // must still come after that request has returned (it would otherwise be a close inside a close).
        await Task.Yield();
        _phase = Phase.Approved;
        close();
    }
}
