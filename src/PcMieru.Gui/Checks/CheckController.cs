using PcMieru.Checks;

namespace PcMieru.Gui;

/// <summary>Where one check stands. "Completed" can carry a save failure (see <see cref="CheckKindState.SaveFailed"/>), it is not a state of its own.</summary>
internal enum CheckRunState
{
    NotRun,
    Running,
    Stopping,
    Completed,
    Stopped,
    Failed,
}

/// <summary>
/// The state of one kind of check: how it stands, the run it ended with (null: none, or it never began), for a failed run that threw what it
/// was (<paramref name="Error"/> is the type and the HRESULT, never the exception's message, which can carry a path or a user name; the
/// words come from <paramref name="FailureCode"/>), and the request that run was started with (the storage tab asks whether it was already
/// the elevated one).
/// </summary>
internal sealed record CheckKindState(CheckRunState State, CheckRun? Run, string? Error, CheckRequest? Request = null, string? FailureCode = null)
{
    public static CheckKindState NotRun { get; } = new(CheckRunState.NotRun, null, null);

    /// <summary>The run was done and its result could not be saved (DESIGN-A §7: appended after "completed").</summary>
    public bool SaveFailed => Run is { Save.Status: CheckSaveStatus.Failed };
}

/// <summary>
/// The one check that is running (or was the last to run) in a window: its state, its last progress and the last result of each kind.
/// It outlives every page, tab and nav change, because the window owns it (<see cref="GuiContext.Checks"/>). One run at a time: a
/// second start is refused while one is going or stopping.
/// <para>
/// The work is on the thread pool: the runner is made there (a live runner builds its services on first use, so a window that never
/// starts a check touches nothing), and it runs there. Nothing here blocks the thread that asked, and the events are posted to the
/// context that started the run (the UI thread), so a handler may draw. A test that has no context gets them inline.
/// </para>
/// </summary>
internal sealed class CheckController : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<ICheckRunner> _runnerFactory;
    private readonly Dictionary<CheckKind, CheckKindState> _last = [];
    private ActiveRun? _active;
    private SynchronizationContext? _context;
    private CheckProgress? _progress;
    private CheckKind? _lastKind;
    private int _generation;
    private bool _disposed;

    /// <param name="runnerFactory">Makes the runner of one run. It is called on a worker when a run starts, never earlier.</param>
    /// <param name="history">What the history tab reads. Reading it never creates a folder.</param>
    public CheckController(Func<ICheckRunner> runnerFactory, IReadOnlyCheckHistory history)
    {
        ArgumentNullException.ThrowIfNull(runnerFactory);
        ArgumentNullException.ThrowIfNull(history);
        _runnerFactory = runnerFactory;
        History = history;
    }

    /// <summary>What the history tab lists and opens.</summary>
    public IReadOnlyCheckHistory History { get; }

    /// <summary>The state or the progress changed (posted to the UI).</summary>
    public event Action? Changed;

    /// <summary>A run ended, whatever way, and its result is saved or not (posted to the UI, after <see cref="Changed"/>).</summary>
    public event Action<CheckKind>? RunFinished;

    /// <summary>The kind that is running or stopping, or null.</summary>
    public CheckKind? Active
    {
        get
        {
            lock (_gate)
            {
                return _active?.Request.Kind;
            }
        }
    }

    public CheckRequest? ActiveRequest
    {
        get
        {
            lock (_gate)
            {
                return _active?.Request;
            }
        }
    }

    public bool IsBusy => Active is not null;

    /// <summary>The state of the latest run of any kind (not run when there was none).</summary>
    public CheckRunState State
    {
        get
        {
            lock (_gate)
            {
                return _active is { } active ? (active.Stopping ? CheckRunState.Stopping : CheckRunState.Running)
                    : _lastKind is { } kind ? _last[kind].State : CheckRunState.NotRun;
            }
        }
    }

    /// <summary>The last progress of the run that is going (null between runs and before its first report).</summary>
    public CheckProgress? Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    public CheckKindState StateOf(CheckKind kind)
    {
        lock (_gate)
        {
            if (_active is { } active && active.Request.Kind == kind)
            {
                return new CheckKindState(active.Stopping ? CheckRunState.Stopping : CheckRunState.Running, _last.GetValueOrDefault(kind)?.Run, null, active.Request);
            }

            return _last.GetValueOrDefault(kind) ?? CheckKindState.NotRun;
        }
    }

    /// <summary>
    /// Starts a check. False (and nothing started) when one is going, when the request is not valid, or when the controller is disposed.
    /// The runner is made and run on a worker; the caller is not blocked.
    /// </summary>
    public bool TryStart(CheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Problem is not null)
        {
            return false;
        }

        ActiveRun run;
        lock (_gate)
        {
            if (_disposed || _active is not null)
            {
                return false;
            }

            run = new ActiveRun(request, ++_generation);
            _active = run;
            _progress = null;
            _context = SynchronizationContext.Current;
        }

        Post(Changed);
        _ = Task.Run(() => ExecuteAsync(run));
        return true;
    }

    /// <summary>
    /// Asks the running check to stop. It returns at once: the runner ends its run (a stopped result, saved) on its own thread. False when
    /// nothing runs or a stop was already asked (a second request does not stop twice).
    /// </summary>
    public bool Stop()
    {
        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (_active is not { Stopping: false } active)
            {
                return false;
            }

            active.Stopping = true;
            cancel = active.Cancel;
        }

        try
        {
            cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended between the lock and here: there is nothing left to stop.
        }

        Post(Changed);
        return true;
    }

    /// <summary>Asks the running check to stop and completes when its run is over and its result is saved (or the save failed). Completes at once when nothing runs.</summary>
    public Task StopAndWaitAsync()
    {
        Task? done;
        lock (_gate)
        {
            done = _active?.Done.Task;
        }

        if (done is null)
        {
            return Task.CompletedTask;
        }

        Stop();
        return done;
    }

    /// <summary>Completes when the run that is going ends (at once when none is).</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _active?.Done.Task ?? Task.CompletedTask;
        }
    }

    /// <summary>How long the program waits, after the message loop ended, for a run that was told to stop to save its result (DESIGN-A §7).</summary>
    internal static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Blocks until no run is going, or until <paramref name="limit"/> has passed; true when it is idle. For the end of the program only, after
    /// the message loop is gone: nothing of the UI is left to block, and the posts a finishing run makes are dropped (see <see cref="TryPost"/>).
    /// </summary>
    internal bool WaitUntilIdle(TimeSpan limit) => WhenIdleAsync().Wait(limit);

    /// <summary>The window is closing for good: a run that is still going is told to stop. Nothing waits here.</summary>
    public void Dispose()
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_active is { } active)
            {
                active.Stopping = true;
                cancel = active.Cancel;
            }
        }

        try
        {
            cancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ExecuteAsync(ActiveRun run)
    {
        CheckRun? result = null;
        string? error = null;
        try
        {
            try
            {
                var runner = _runnerFactory();
                result = await runner.RunAsync(run.Request, new Reporter(this, run), run.Cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (run.Cancel.IsCancellationRequested)
            {
                // Our own stop reached the runner before it made a run of it: no result, the state is "stopped". A cancellation this run did not ask
                // for (a timeout inside the runner, a token of its own) is a failure like any other and goes to the next catch.
            }
            catch (Exception ex)
            {
                // The type and the HRESULT only: the message of an exception can carry a path, a user name or the OS language's sentence.
                error = CheckFormat.ExceptionDetail(ex);
            }

            Finish(run, result, error);
        }
        finally
        {
            run.Done.TrySetResult();
        }
    }

    private void Finish(ActiveRun run, CheckRun? result, string? error)
    {
        var state = error is not null
            ? CheckRunState.Failed
            : result is null
                ? CheckRunState.Stopped
                : result.Result.Outcome switch
                {
                    CheckOutcome.Completed => CheckRunState.Completed,
                    CheckOutcome.Stopped => CheckRunState.Stopped,
                    _ => CheckRunState.Failed,
                };
        var kind = run.Request.Kind;
        lock (_gate)
        {
            _last[kind] = new CheckKindState(state, result, error, run.Request, error is null ? null : CheckRunFailureCodes.InternalError);
            _lastKind = kind;
            _active = null;
            _progress = null;
        }

        run.Cancel.Dispose();
        Post(Changed);
        Post(RunFinished, kind);
    }

    private void Report(ActiveRun run, CheckProgress progress)
    {
        lock (_gate)
        {
            // A report that arrives after its run was ended (or replaced) is a late one: it must not paint over the next run.
            if (_active?.Generation != run.Generation)
            {
                return;
            }

            _progress = progress;
        }

        Post(Changed);
    }

    private void Post(Action? handler)
    {
        if (handler is null)
        {
            return;
        }

        var context = _context;
        if (context is null)
        {
            handler();
        }
        else
        {
            TryPost(context, _ => handler());
        }
    }

    private void Post<T>(Action<T>? handler, T argument)
    {
        if (handler is null)
        {
            return;
        }

        var context = _context;
        if (context is null)
        {
            handler(argument);
        }
        else
        {
            TryPost(context, _ => handler(argument));
        }
    }

    /// <summary>
    /// A UI context that is gone (the window closed while a run ended, or a control without a handle) refuses the post. That must neither
    /// skip the posts that come after it (the run's end is told twice) nor fault the run's task: nobody is left to draw, so the post is dropped.
    /// </summary>
    private static void TryPost(SynchronizationContext context, SendOrPostCallback callback)
    {
        try
        {
            context.Post(callback, null);
        }
        catch (Exception)
        {
            // Dropped on purpose (see above).
        }
    }

    private sealed class ActiveRun(CheckRequest request, int generation)
    {
        public CheckRequest Request { get; } = request;

        public int Generation { get; } = generation;

        public CancellationTokenSource Cancel { get; } = new();

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Stopping { get; set; }
    }

    private sealed class Reporter(CheckController owner, ActiveRun run) : IProgress<CheckProgress>
    {
        public void Report(CheckProgress value) => owner.Report(run, value);
    }
}
