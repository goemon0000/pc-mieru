using System.Collections.Concurrent;
using System.Diagnostics;
using PcMieru.Checks;

namespace PcMieru.Gui.Tests;

/// <summary>
/// Stands in for the UI thread of a test: what is posted to it waits in a queue and runs only when the test pumps, on the test's own
/// thread. So "the controller posts to the UI" is a thing a test can look at (nothing arrives until it pumps), and a handler runs where a
/// control could be drawn. Tests that use it are synchronous on purpose: an async test loses the context it installed.
/// </summary>
internal sealed class QueueContext : SynchronizationContext
{
    private readonly object _gate = new();
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly int _owner = Environment.CurrentManagedThreadId;

    public int OwnerThread => _owner;

    public int Queued
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_gate)
        {
            _queue.Enqueue((d, state));
        }

        _signal.Set();
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId != _owner)
        {
            throw new NotSupportedException("A test context takes posts from other threads, not sends.");
        }

        d(state);
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs everything that is queued now (and what that queues), on the calling thread.</summary>
    public int Pump()
    {
        var ran = 0;
        while (true)
        {
            (SendOrPostCallback Callback, object? State) next;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    return ran;
                }

                next = _queue.Dequeue();
            }

            next.Callback(next.State);
            ran++;
        }
    }

    /// <summary>
    /// Pumps until the condition holds. The bound is only there so a broken test ends instead of hanging; no test asserts on how long
    /// anything took, and the result is the condition, never the clock.
    /// </summary>
    public bool Until(Func<bool> condition, int hangGuardMilliseconds = 30_000)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Pump();
            if (condition())
            {
                return true;
            }

            if (watch.ElapsedMilliseconds > hangGuardMilliseconds)
            {
                return false;
            }

            _signal.WaitOne(10);
        }
    }
}

internal static class Ui
{
    /// <summary>Runs the body with a queue context installed as the thread's UI context; the old one is put back afterwards.</summary>
    public static void Run(Action<QueueContext> body)
    {
        var previous = SynchronizationContext.Current;
        var context = new QueueContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            body(context);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>A temporary folder that is removed (and its removal confirmed) when the test is done.</summary>
    public sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcm-gui-checks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        /// <summary>Everything under the folder (files and folders), to prove that nothing was made there.</summary>
        public string[] Entries() => Directory.GetFileSystemEntries(Path, "*", SearchOption.AllDirectories);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A temp folder that cannot be removed now is left to the system's temp cleaning; it never holds anything of value.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// A runner that does what a test tells it to and nothing else: no load, no WMI, no disk, no prompt. By default it holds the run open until
/// <see cref="Release"/> or a stop, so a test can look at the state "running"; a stop is answered with a stopped result (saved), as the
/// real runner does.
/// </summary>
internal sealed class ScriptedRunner : ICheckRunner
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<CheckRequest> _requests = new();
    private int _runs;

    /// <summary>True: the run waits for <see cref="Release"/> or a stop. False: it ends at once.</summary>
    public bool Hold { get; init; } = true;

    /// <summary>How an ended run says it ended (a stop overrides it with stopped).</summary>
    public CheckOutcome Outcome { get; init; } = CheckOutcome.Completed;

    /// <summary>How the save of the result went.</summary>
    public CheckSaveState Save { get; init; } = CheckSaveState.Saved("scripted.json");

    /// <summary>Thrown by the run instead of an answer (after it was released or stopped).</summary>
    public Exception? Failure { get; init; }

    /// <summary>When set, the run does not hand its answer back until this is opened: "the save is still going".</summary>
    public TaskCompletionSource? SaveGate { get; init; }

    public Task Started => _started.Task;

    public bool WasStopped => _stopSeen.Task.IsCompleted;

    public int Runs => Volatile.Read(ref _runs);

    public IReadOnlyCollection<CheckRequest> Requests => _requests;

    /// <summary>The progress channel the controller gave the run (a test reports through it).</summary>
    public IProgress<CheckProgress>? Reporter { get; private set; }

    public Guid RunId { get; private set; }

    public void Release() => _release.TrySetResult();

    public async Task<CheckRun> RunAsync(CheckRequest request, IProgress<CheckProgress>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _runs);
        _requests.Enqueue(request);
        Reporter = progress;
        RunId = Guid.NewGuid();
        _started.TrySetResult();

        var stopped = false;
        if (Hold)
        {
            using var registration = ct.Register(() => _stopSeen.TrySetResult());
            await Task.WhenAny(_release.Task, _stopSeen.Task).ConfigureAwait(false);
            stopped = _stopSeen.Task.IsCompleted && !_release.Task.IsCompleted;
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        if (SaveGate is not null)
        {
            await SaveGate.Task.ConfigureAwait(false);
        }

        var outcome = stopped ? CheckOutcome.Stopped : Outcome;
        var start = FakeCheckData.Start;
        var result = outcome switch
        {
            CheckOutcome.Completed => FakeCheckData.For(request, RunId, start, request.Duration?.TotalSeconds ?? 30),
            CheckOutcome.Stopped => FakeCheckData.Stopped(request.Kind, RunId, start, 1),
            _ => CheckResult.Create(FakeCheckData.App, RunId, request.Kind, start, start.AddSeconds(1), outcome, []),
        };
        return outcome == CheckOutcome.Failed
            ? new CheckRun(result, Save, CheckRunFailureCodes.InternalError, "scripted")
            : new CheckRun(result, Save);
    }
}

/// <summary>Hands out the scripted runners one per run, in order, and counts how many were asked for (a window that never starts a check asks for none).</summary>
internal sealed class RunnerQueue(params ScriptedRunner[] runners)
{
    private readonly Queue<ScriptedRunner> _queue = new(runners);
    private int _made;

    public int Made => Volatile.Read(ref _made);

    public ICheckRunner Make()
    {
        Interlocked.Increment(ref _made);
        lock (_queue)
        {
            return _queue.Dequeue();
        }
    }

    public CheckController Controller(IReadOnlyCheckHistory? history = null) => new CheckController(Make, history ?? new InMemoryCheckHistory([])).Watched();
}

/// <summary>
/// Tells when a run has really ended for the UI. <see cref="CheckController.IsBusy"/> goes false the moment the worker clears the active run,
/// and only then are "changed" and "run finished" posted to the UI context, so a wait on the flag alone can end before the page has been
/// told (the flake: a state line that still said "Running"). The posts are first in, first out, so once the delivery of "run finished" has been
/// counted on the context, the "changed" before it has been delivered too. A controller is watched from its creation on (<see cref="Watched"/>);
/// the count is of deliveries, never of time.
/// </summary>
internal static class RunEnd
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CheckController, Counter> Counters = new();

    /// <summary>Starts counting the "run finished" events the UI context delivers for this controller. Call it before the first run; a second call changes nothing.</summary>
    public static CheckController Watched(this CheckController controller)
    {
        Counters.GetValue(
            controller,
            static watched =>
            {
                var counter = new Counter();
                watched.RunFinished += _ => Interlocked.Increment(ref counter.Delivered);
                return counter;
            });
        return controller;
    }

    /// <summary>How many "run finished" events the UI context has delivered so far.</summary>
    public static int FinishedDelivered(this CheckController controller) =>
        Counters.TryGetValue(controller, out var counter)
            ? Volatile.Read(ref counter.Delivered)
            : throw new InvalidOperationException("The controller was not created watched: build it with RunnerQueue.Controller() or call Watched() before the first run.");

    /// <summary>True when no run is active and the end of at least <paramref name="expected"/> runs (in all, since the controller was made) has been delivered to the UI.</summary>
    public static bool RunEnded(this CheckController controller, int expected = 1) => !controller.IsBusy && controller.FinishedDelivered() >= expected;

    /// <summary>Pumps until <paramref name="expected"/> runs of this controller have ended and been told to the UI.</summary>
    public static bool UntilRunEnded(this QueueContext ui, CheckController controller, int expected = 1) => ui.Until(() => controller.RunEnded(expected));

    private sealed class Counter
    {
        public int Delivered;
    }
}
