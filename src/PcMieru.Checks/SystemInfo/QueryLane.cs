namespace PcMieru.Checks.SystemInfo;

/// <summary>Starts the thread a lane job runs on. A seam: a test counts the starts, the real one makes a background thread.</summary>
internal delegate void LaneThreadStarter(string name, Action body);

/// <summary>What the work of a lane job can see of its caller: whether the caller has given up (deadline passed, or the run was stopped).</summary>
internal class LaneJob
{
    private int _abandoned;

    /// <summary>True once the caller has stopped waiting. The work should wind down at its next safe point; whatever it returns is dropped.</summary>
    public bool IsAbandoned => Volatile.Read(ref _abandoned) != 0;

    private protected void MarkAbandoned() => Volatile.Write(ref _abandoned, 1);
}

/// <summary>
/// A bounded background lane for calls that may not come back in time (DESIGN-A §6.1, §12): the event log and WMI each have one. A call
/// runs on a thread of its own; the caller waits for it only until its deadline, on the monotonic clock, so a call that does not return
/// cannot hold the check. What the lane guarantees:
/// <list type="bullet">
/// <item>It has a small fixed number of slots. A slot is taken before the thread starts and given back only when the work has
/// actually returned, also when the caller gave up long before.</item>
/// <item>When no slot is free (every one is held, by a stuck call or by a live one) the request fails at once as
/// <see cref="CheckReason.Timeout"/>: no thread is started and nothing waits in a queue, so pressing the button again cannot pile
/// up blocked workers. A caller that wants more than <see cref="Slots"/> queries at the same time has to ask for them in turn.</item>
/// <item>The slot is freed before the caller is told, so the next request of a caller that just got its answer finds it free.</item>
/// <item>A value that comes back after the caller gave up is dropped: the caller's give-up and the worker's finish race for one state
/// (running to completed, or running to abandoned), and only the winner counts. A dropped value is never given to anybody, so it cannot
/// end up in a result that was already sealed.</item>
/// </list>
/// </summary>
internal sealed class QueryLane
{
    public const int DefaultSlots = 2;

    private readonly string _name;
    private readonly LaneThreadStarter _start;
    private int _held;

    /// <param name="name">Names the worker threads ("PCMieru.Checks.&lt;name&gt;").</param>
    /// <param name="slots">How many calls may be in flight (running, or abandoned and not yet returned) at the same time.</param>
    /// <param name="start">Starts a worker thread. Tests give their own; the default makes a background thread.</param>
    public QueryLane(string name, int slots = DefaultSlots, LaneThreadStarter? start = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        _name = "PCMieru.Checks." + name;
        Slots = slots;
        _start = start ?? StartBackgroundThread;
    }

    public int Slots { get; }

    /// <summary>The slots taken right now: calls running, and calls the caller gave up on that have not returned yet.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>
    /// Runs <paramref name="work"/> on a worker thread and waits for it until <paramref name="deadline"/> or <paramref name="ct"/>.
    /// The work returns its own reading (it checks the deadline itself, between the steps of its call); the lane only adds what it
    /// alone can say: no free slot or deadline passed (timeout), stopped (partial), the work threw or no thread could be started (error).
    /// </summary>
    public Task<CheckReading<T>> RunAsync<T>(Func<LaneJob, CheckReading<T>> work, QueryDeadline deadline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(deadline);

        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(CheckReading<T>.Stopped());
        }

        if (deadline.IsExpired || !TryTakeSlot())
        {
            return Task.FromResult(CheckReading<T>.TimedOut());
        }

        var call = new LaneCall<T>();
        try
        {
            _start(_name, () => Execute(call, work));
        }
        catch (Exception)
        {
            // No thread could be made, so nothing will ever give the slot back: do it here.
            ReleaseSlot();
            return Task.FromResult(CheckReading<T>.Failed());
        }

        return WaitAsync(call, deadline, ct);
    }

    private static async Task<CheckReading<T>> WaitAsync<T>(LaneCall<T> call, QueryDeadline deadline, CancellationToken ct)
    {
        try
        {
            await call.Done.WaitAsync(deadline.Remaining, deadline.Time, ct).ConfigureAwait(false);
            return call.Result;
        }
        catch (TimeoutException)
        {
            // The caller gives up: from now on the worker's value is dropped. If the worker finished first, its value stands.
            return call.TryAbandon() ? CheckReading<T>.TimedOut() : call.Result;
        }
        catch (OperationCanceledException)
        {
            return call.TryAbandon() ? CheckReading<T>.Stopped() : call.Result;
        }
    }

    // On the worker thread.
    private void Execute<T>(LaneCall<T> call, Func<LaneJob, CheckReading<T>> work)
    {
        CheckReading<T> reading;
        try
        {
            reading = work(call);
        }
        catch (Exception)
        {
            reading = CheckReading<T>.Failed(); // nothing of the exception is kept: a Windows message could name a path or a machine
        }

        // The Windows call has returned (or never started): only now is the slot free. Before the caller is told, so that the caller's next
        // request does not find the slot of the call it just waited for still taken.
        ReleaseSlot();
        call.TryComplete(reading);
    }

    private bool TryTakeSlot()
    {
        while (true)
        {
            var held = Volatile.Read(ref _held);
            if (held >= Slots)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _held, held + 1, held) == held)
            {
                return true;
            }
        }
    }

    private void ReleaseSlot() => Interlocked.Decrement(ref _held);

    private static void StartBackgroundThread(string name, Action body)
    {
        var thread = new Thread(() => body()) { IsBackground = true, Name = name };
        thread.Start();
    }
}

/// <summary>
/// One call of a lane: the state the caller and the worker race for. Running goes to Completed (the worker finished first) or to Abandoned
/// (the caller gave up first), once and for all, by compare-and-swap; only the winner counts. A value that arrives after Abandoned is never
/// looked at again, so a late value cannot end up in a result the caller already sealed.
/// </summary>
internal sealed class LaneCall<T> : LaneJob
{
    private const int Running = 0;
    private const int Completed = 1;
    private const int Abandoned = 2;

    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;
    private CheckReading<T>? _result;

    public Task Done => _done.Task;

    /// <summary>Only meaningful once the call completed (<see cref="Done"/> is done, or <see cref="TryAbandon"/> returned false).</summary>
    public CheckReading<T> Result => _result!;

    /// <summary>The worker finished. False when the caller gave up first: the value is then never looked at.</summary>
    public bool TryComplete(CheckReading<T> reading)
    {
        _result = reading; // written before the state changes, so a caller that sees Completed sees it
        if (Interlocked.CompareExchange(ref _state, Completed, Running) != Running)
        {
            return false;
        }

        _done.SetResult();
        return true;
    }

    /// <summary>The caller gives up. False when the worker had finished already: that value counts.</summary>
    public bool TryAbandon()
    {
        if (Interlocked.CompareExchange(ref _state, Abandoned, Running) != Running)
        {
            return false;
        }

        MarkAbandoned();
        return true;
    }
}

/// <summary>The two lanes of the process (DESIGN-A §6.1). A slot lasts until the Windows call returns, so these outlive any one check.</summary>
internal static class QueryLanes
{
    /// <summary>The event log (wevtapi).</summary>
    public static QueryLane EventLog { get; } = new("EventLog");

    /// <summary>WMI (System.Management).</summary>
    public static QueryLane Wmi { get; } = new("Wmi");
}
