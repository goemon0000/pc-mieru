using PcMieru.Checks.SystemInfo;

namespace PcMieru.Checks.Tests.SystemInfo;

/// <summary>
/// A monotonic clock a test moves by hand, with working timers (Task.WaitAsync needs them). The wall clock is a separate value that
/// <see cref="Advance"/> does not touch and a test can set to anything, to show that a deadline does not depend on it.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly long _frequency;
    private readonly List<ManualTimer> _timers = [];
    private long _now;

    public ManualTimeProvider(long frequency = 1000, long start = 1_000_000)
    {
        _frequency = frequency;
        _now = start;
    }

    public DateTimeOffset Wall { get; set; } = new(2026, 11, 21, 3, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => _frequency;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override DateTimeOffset GetUtcNow() => Wall;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the monotonic clock and runs, on this thread, the callbacks of the timers that came due.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += Ticks(by);
            due = [.. _timers.Where(t => t.DueAt is { } at && at <= _now).OrderBy(t => t.DueAt)];
            foreach (var timer in due)
            {
                timer.DueAt = null;
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    // Whole clock ticks in a span, rounded up, in integer arithmetic (a double would turn 4.999 s * 1000 into 4999.000000000001).
    private long Ticks(TimeSpan span) => (span.Ticks * _frequency + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (!owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }

                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    DueAt = null;
                    return true;
                }

                if (dueTime <= TimeSpan.Zero)
                {
                    DueAt = null;
                    ThreadPool.QueueUserWorkItem(_ => Fire());
                    return true;
                }

                DueAt = owner._now + owner.Ticks(dueTime);
                return true;
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                DueAt = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Small helpers shared by the tests of this folder.</summary>
internal static class QueryTest
{
    /// <summary>Longest a test waits, in real time, for something the code under test must do by itself.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public static QueryDeadline Deadline(ManualTimeProvider clock, double seconds = 5) =>
        QueryDeadline.After(clock, TimeSpan.FromSeconds(seconds));

    /// <summary>Waits for a task without ever hanging the test run.</summary>
    public static async Task<T> Done<T>(Task<T> task) => await task.WaitAsync(Patience);

    public static bool Eventually(Func<bool> condition) => SpinWait.SpinUntil(condition, Patience);

    /// <summary>Waits for an event the worker raises.</summary>
    public static void Reached(ManualResetEventSlim gate) => Assert.True(gate.Wait(Patience), "the worker did not get there");
}

/// <summary>One answer of the fake EvtNext.</summary>
internal readonly record struct NextResult(int Status, int Returned)
{
    /// <summary>A batch of <paramref name="count"/> events with status 0.</summary>
    public static NextResult Events(int count) => new(0, count);

    /// <summary>ERROR_NO_MORE_ITEMS.</summary>
    public static NextResult End => new(EventLogFlags.ErrorNoMoreItems, 0);

    /// <summary>ERROR_TIMEOUT.</summary>
    public static NextResult Timeout => new(EventLogFlags.ErrorTimeout, 0);

    public static NextResult Fail(int win32Error) => new(win32Error, 0);
}

/// <summary>
/// A scripted wevtapi that never calls Windows. It hands out handles, and records every call with the thread it was made on, so a
/// test can show that each handle was closed exactly once and on the thread that owns it.
/// </summary>
internal sealed class FakeEventLogApi(Func<int, NextResult> next) : IEventLogApi
{
    public const nint ResultSetHandle = 7;

    private readonly object _gate = new();
    private readonly List<nint> _opened = [];
    private readonly Dictionary<nint, int> _closes = [];
    private readonly List<int> _callThreads = [];
    private readonly List<int> _nextTimeouts = [];
    private readonly List<(string Channel, string XPath, int Flags)> _queries = [];
    private nint _handle = 1000;
    private int _nextCalls;

    /// <summary>The status EvtQuery answers with; 0 is a result set.</summary>
    public int QueryStatus { get; set; }

    /// <summary>Makes EvtQuery throw (a bug in the layer below, or a thread abort).</summary>
    public Exception? QueryThrows { get; set; }

    public int NextCalls => Volatile.Read(ref _nextCalls);

    public IReadOnlyList<int> NextTimeoutsMs => Snapshot(_nextTimeouts);

    public IReadOnlyList<int> CallThreads => Snapshot(_callThreads);

    public IReadOnlyList<(string Channel, string XPath, int Flags)> Queries => Snapshot(_queries);

    public IReadOnlyList<nint> Opened => Snapshot(_opened);

    /// <summary>Every handle that was given out was closed, each exactly once, and nothing else was closed.</summary>
    public bool EveryHandleClosedExactlyOnce
    {
        get
        {
            lock (_gate)
            {
                return _opened.All(h => _closes.GetValueOrDefault(h) == 1) && _closes.Keys.All(_opened.Contains);
            }
        }
    }

    public int Query(string channel, string xpath, int flags, out nint resultSet)
    {
        Record(_callThreads, Environment.CurrentManagedThreadId);
        lock (_gate)
        {
            _queries.Add((channel, xpath, flags));
        }

        if (QueryThrows is { } ex)
        {
            throw ex;
        }

        if (QueryStatus != 0)
        {
            resultSet = 0;
            return QueryStatus;
        }

        resultSet = ResultSetHandle;
        lock (_gate)
        {
            _opened.Add(ResultSetHandle);
        }

        return 0;
    }

    public int Next(nint resultSet, int timeoutMs, Span<nint> events, out int returned)
    {
        Record(_callThreads, Environment.CurrentManagedThreadId);
        Record(_nextTimeouts, timeoutMs);
        var index = Interlocked.Increment(ref _nextCalls) - 1;
        var result = next(index);
        Assert.InRange(result.Returned, 0, events.Length);
        lock (_gate)
        {
            for (var i = 0; i < result.Returned; i++)
            {
                events[i] = ++_handle;
                _opened.Add(events[i]);
            }
        }

        returned = result.Returned;
        return result.Status;
    }

    public void Close(nint handle)
    {
        Record(_callThreads, Environment.CurrentManagedThreadId);
        lock (_gate)
        {
            _closes[handle] = _closes.GetValueOrDefault(handle) + 1;
        }
    }

    private void Record<T>(List<T> list, T value)
    {
        lock (_gate)
        {
            list.Add(value);
        }
    }

    private List<T> Snapshot<T>(List<T> list)
    {
        lock (_gate)
        {
            return [.. list];
        }
    }
}

/// <summary>A WMI row made of a dictionary.</summary>
internal sealed class FakeRow(IReadOnlyDictionary<string, object?> values) : IWmiRow
{
    public object? this[string name] => values.GetValueOrDefault(name);

    public static FakeRow Of(object? value) => new(new Dictionary<string, object?> { ["V"] = value });
}

/// <summary>
/// A WMI that never calls Windows: what it does is a delegate of the test (it may block, move the clock, hand out rows, throw). It
/// records the calls and the threads they ran on.
/// </summary>
internal sealed class FakeWmiExecutor(Action<FakeWmiExecutor, Func<IWmiRow, bool>> run) : IWmiExecutor
{
    private readonly object _gate = new();
    private readonly List<(string Scope, string Query, TimeSpan Timeout)> _calls = [];
    private readonly List<int> _threads = [];

    public IReadOnlyList<(string Scope, string Query, TimeSpan Timeout)> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<int> Threads
    {
        get
        {
            lock (_gate)
            {
                return [.. _threads];
            }
        }
    }

    public void Run(string scope, string query, TimeSpan timeout, Func<IWmiRow, bool> visit)
    {
        lock (_gate)
        {
            _calls.Add((scope, query, timeout));
            _threads.Add(Environment.CurrentManagedThreadId);
        }

        run(this, visit);
    }

    /// <summary>Hands the rows to the visitor one by one until it says stop, the way a real enumeration does.</summary>
    public static Action<FakeWmiExecutor, Func<IWmiRow, bool>> Rows(params object?[] values) => (_, visit) =>
    {
        foreach (var value in values)
        {
            if (!visit(FakeRow.Of(value)))
            {
                return;
            }
        }
    };
}

/// <summary>A lane whose thread starts are counted, so a test can show that a refused request started nothing.</summary>
internal sealed class CountingLane
{
    private int _started;

    public CountingLane(int slots = 2)
    {
        Lane = new QueryLane("Test", slots, Start);
    }

    public QueryLane Lane { get; }

    /// <summary>How many worker threads were started.</summary>
    public int Started => Volatile.Read(ref _started);

    private void Start(string name, Action body)
    {
        Interlocked.Increment(ref _started);
        var thread = new Thread(() => body()) { IsBackground = true, Name = name };
        thread.Start();
    }
}
