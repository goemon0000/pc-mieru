namespace PcMieru.Cli;

/// <summary>
/// Ctrl+C and the exit code, in one place (DESIGN-A 8). The handler only cancels the token; <c>Main</c> owns the exit code and returns it.
/// <list type="bullet">
/// <item>The first Ctrl+C cancels the token and starts a watchdog. The watchdog leaves the process (exit 1) only when <c>Main</c> has not
/// returned by the end of the grace (<see cref="CliShutdown.WaitForLeaveAsync()"/>: 2 seconds, or longer while a command holds the shutdown).
/// It is for a read a token cannot interrupt (<c>pcmi mcp</c> blocked on stdin in a terminal).</item>
/// <item>A second Ctrl+C leaves at once, unless a command holds the shutdown (a stopped check is joining its workers and saving the stopped
/// result): then it waits for the same watchdog, up to the same grace.</item>
/// <item>A run that has completed keeps its exit code when the cancel comes late: the code is decided under one lock, and the first of "Main
/// returned" and "the process was forced out" wins. A cancel after the run's end changes nothing.</item>
/// </list>
/// </summary>
internal sealed class ShutdownCoordinator
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancel;
    private readonly Action<int> _exit;
    private readonly Func<Task> _waitForLeave;
    private readonly Func<bool> _isHeld;
    private int _presses;
    private bool _mainReturned;
    private bool _forced;
    private Task? _watchdog;

    /// <summary>The real thing: the process leaves with <see cref="Environment.Exit(int)"/>, after the grace of <see cref="CliShutdown"/>.</summary>
    public ShutdownCoordinator(CancellationTokenSource cancel)
        : this(cancel, Environment.Exit, CliShutdown.WaitForLeaveAsync, () => CliShutdown.IsHeld)
    {
    }

    /// <summary>The seam: how the process leaves, what the watchdog waits for, and whether a command holds the shutdown.</summary>
    public ShutdownCoordinator(CancellationTokenSource cancel, Action<int> exit, Func<Task> waitForLeave, Func<bool> isHeld)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentNullException.ThrowIfNull(waitForLeave);
        ArgumentNullException.ThrowIfNull(isHeld);
        _cancel = cancel;
        _exit = exit;
        _waitForLeave = waitForLeave;
        _isHeld = isHeld;
    }

    /// <summary>The token the run is given.</summary>
    public CancellationToken Token => _cancel.Token;

    /// <summary>The watchdog of the first Ctrl+C (null before it). It ends once it has either left the process or found that <c>Main</c> had returned.</summary>
    internal Task? Watchdog
    {
        get
        {
            lock (_gate)
            {
                return _watchdog;
            }
        }
    }

    /// <summary>A Ctrl+C. Never throws, never exits on the first press, never decides the exit code.</summary>
    public void CtrlC()
    {
        int press;
        lock (_gate)
        {
            if (_mainReturned || _forced)
            {
                return;
            }

            press = ++_presses;
        }

        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Main is already past the run.
        }

        if (press == 1)
        {
            lock (_gate)
            {
                _watchdog ??= Task.Run(WatchAsync);
            }
        }
        else if (!_isHeld())
        {
            Force();
        }

        // A second press while a command holds the shutdown does nothing more: the watchdog of the first press ends the wait at the grace.
    }

    /// <summary>
    /// Runs the command, lets go of what it wrote (the flush), and decides the exit code: the command's own, unless the process was already
    /// forced out (then 1). A cancel that came after the command had finished does not change a completed run's code.
    /// </summary>
    public async Task<int> RunAsync(Func<CancellationToken, Task<int>> body, Func<Task>? flush = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        int code;
        try
        {
            code = await body(_cancel.Token).ConfigureAwait(false);
        }
        finally
        {
            if (flush is not null)
            {
                await flush().ConfigureAwait(false);
            }
        }

        return Complete(code);
    }

    /// <summary>
    /// <c>Main</c> has its code. From here the process is no longer forced out; if it was forced out before this call, the code is 1 (the
    /// process is leaving with 1 anyway).
    /// </summary>
    public int Complete(int code)
    {
        lock (_gate)
        {
            if (_forced)
            {
                return 1;
            }

            _mainReturned = true;
            return code;
        }
    }

    private async Task WatchAsync()
    {
        try
        {
            await _waitForLeave().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A wait that failed ends the wait; the check in Force still spares a Main that has returned.
        }

        Force();
    }

    // Leaves with 1, unless Main has already returned its code (or the process is already leaving).
    private void Force()
    {
        lock (_gate)
        {
            if (_mainReturned || _forced)
            {
                return;
            }

            _forced = true;
        }

        _exit(1);
    }
}
