namespace PcMieru.Gui;

/// <summary>
/// What the window does when it is asked to close while a check runs (DESIGN-A §7). The first request is cancelled and the user is
/// asked; on yes the stop is requested, the stop and the save are awaited (never blocking the UI thread), and then the window is closed
/// again. Meanwhile any further close request is cancelled without asking again and without a second stop: the guard holds the
/// re-entry to one. On no, nothing changed. With no check running the close goes straight through.
/// </summary>
internal sealed class CheckCloseGuard(CheckController controller, Func<bool> confirm, Action close)
{
    private enum Phase
    {
        /// <summary>No question is open and no stop is under way.</summary>
        Idle,

        /// <summary>The question is on the screen (its message loop can deliver another close request).</summary>
        Asking,

        /// <summary>Yes was answered: the stop is requested and the save is awaited.</summary>
        Stopping,

        /// <summary>The run is over: the close that follows is let through.</summary>
        Approved,
    }

    private Phase _phase;

    /// <summary>The stop-and-close that yes started (null before). A test awaits it; the window never does.</summary>
    internal Task? Pending { get; private set; }

    /// <summary>True when the window may go on closing now. False: the request was cancelled (asked, answered no, or held).</summary>
    /// <param name="systemEnding">Windows is shutting down or the task manager closes the window: nobody can answer, so the stop is requested and the close goes on.</param>
    public bool OnCloseRequested(bool systemEnding = false)
    {
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

        if (systemEnding)
        {
            _ = controller.Stop();
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
            await controller.StopAndWaitAsync().ConfigureAwait(true);
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
