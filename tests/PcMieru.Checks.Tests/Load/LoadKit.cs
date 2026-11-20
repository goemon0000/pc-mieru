using PcMieru.Checks.Load;
using PcMieru.Checks.Tests.SystemInfo;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Collectors;

namespace PcMieru.Checks.Tests.Load;

/// <summary>Lists of logical processors for the tests: nothing here reads the machine.</summary>
internal static class Cpus
{
    /// <summary>One kind of core, one core per logical processor, all in group 0.</summary>
    public static IReadOnlyList<LogicalCpu> Uniform(int count, byte efficiencyClass = 0) =>
        [.. Enumerable.Range(0, count).Select(i => new LogicalCpu(i, 0, (byte)i, i, efficiencyClass))];

    /// <summary>One logical processor for each class given, in order.</summary>
    public static IReadOnlyList<LogicalCpu> OfClasses(params byte[] classes) =>
        [.. classes.Select((c, i) => new LogicalCpu(i, 0, (byte)i, i, c))];
}

/// <summary>The PDH samples the fake session gives.</summary>
internal static class PdhSamples
{
    public static CpuPdhSample Healthy(double mhz = 3500, double load = 100, double watts = 65) =>
        new(mhz, load, new PackagePowerReading(PackagePowerState.Valid, watts));

    /// <summary>A second in which nothing could be read (the Energy Meter state as given).</summary>
    public static CpuPdhSample Nothing(PackagePowerState power = PackagePowerState.NoValidSample) => CpuPdhSample.Missing(power);
}

/// <summary>
/// A whole CPU test session in memory: a clock moved by hand, threads that run when the world runs, a chunk whose answers the test decides, a
/// pin the test decides and a PDH the test scripts. Nothing real is touched: no thread, no load, no counter.
/// <para>
/// The world is simple. Every wait of the load loop is one "second": while the clock is moved by the length of the wait, every live fake thread
/// does <see cref="ChunksPerWait"/> chunks (wait number, then CPU index). A cancellation during the wait cuts it short and nothing more happens.
/// </para>
/// </summary>
internal sealed class FakeCpuSession : ICpuTestSession
{
    private readonly List<FakeLoadThread> _threads = [];
    private readonly List<LogicalCpu> _pinCalls = [];
    private int _waits;
    private int _samples;
    private int _chunkCalls;

    public FakeCpuSession(IReadOnlyList<LogicalCpu> cpus, ManualTimeProvider? time = null)
    {
        Cpus = cpus;
        Time = time ?? new ManualTimeProvider();
    }

    public IReadOnlyList<LogicalCpu> Cpus { get; }

    /// <summary>The clock of the load. The load never sees another one.</summary>
    public ManualTimeProvider Time { get; }

    /// <summary>The chunks the thread of CPU <c>cpu</c> completes during wait number <c>wait</c> (1 is the first second). Default: 1002, a thousand a second.</summary>
    public Func<int, int, int> ChunksPerWait { get; set; } = static (_, _) => 1002;

    /// <summary>What the PDH gives for sample number <c>n</c> (1 is the first). Default: a healthy 3500 MHz, fully loaded, 65 W.</summary>
    public Func<int, CpuPdhSample> Sample { get; set; } = static _ => PdhSamples.Healthy();

    /// <summary>Whether Windows accepts the pin of a CPU (default: always).</summary>
    public Func<LogicalCpu, bool> PinAccepts { get; set; } = static _ => true;

    /// <summary>A pin call that throws (an outcome that is not known).</summary>
    public Func<LogicalCpu, bool> PinThrows { get; set; } = static _ => false;

    /// <summary>Wrong answer for chunk call number <c>n</c> (1 is the first call, the first 16 are the known answers). Default: never.</summary>
    public Func<int, bool> WrongAnswer { get; set; } = static _ => false;

    /// <summary>The chunk throws on call number <c>n</c>.</summary>
    public Func<int, bool> ChunkThrows { get; set; } = static _ => false;

    /// <summary>The thread with this index never gets to run its start action (so it never pins).</summary>
    public Func<int, bool> ThreadNeverStarts { get; set; } = static _ => false;

    /// <summary>The start of the thread with this index fails (the OS refuses a new thread).</summary>
    public Func<int, bool> ThreadStartFails { get; set; } = static _ => false;

    /// <summary>Called at the start of every wait with its number, before anything happens in it. A test cancels the run from here.</summary>
    public Action<int>? OnWait { get; set; }

    /// <summary>Called after the clock of the load was moved, with the length of the wait: a test moves a second clock from here.</summary>
    public Action<TimeSpan>? AfterAdvance { get; set; }

    /// <summary>Throws when the PDH is asked for sample number <c>n</c>.</summary>
    public Func<int, bool> SampleThrows { get; set; } = static _ => false;

    public IReadOnlyList<FakeLoadThread> Threads => _threads;

    public IReadOnlyList<LogicalCpu> PinCalls => _pinCalls;

    public int Waits => _waits;

    public int Samples => _samples;

    public int ChunkCalls => _chunkCalls;

    /// <summary>The seed of every chunk call, in order (the first 16 are the known answers).</summary>
    public List<ulong> Seeds { get; } = [];

    /// <summary>The managed thread of every chunk call that was made before the first thread started (the known answers).</summary>
    public List<int> KnownAnswerThreads { get; } = [];

    /// <summary>How many chunk calls had been made when each thread was started.</summary>
    public List<int> ChunkCallsAtThreadStart { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>The lengths of the waits the load asked for.</summary>
    public List<TimeSpan> WaitLengths { get; } = [];

    IChunkWork ICpuTestSession.Work => new FakeChunkWork(this);

    ICpuPin ICpuTestSession.Pin => new FakePin(this);

    ILoadThreadStarter ICpuTestSession.Threads => new FakeThreadStarter(this);

    ILoadClock ICpuTestSession.Clock => new FakeLoadClock(this);

    public CpuPdhSample SamplePdh()
    {
        var n = Interlocked.Increment(ref _samples);
        return SampleThrows(n) ? throw new InvalidOperationException("the fake PDH failed") : Sample(n);
    }

    public void Dispose() => Disposed = true;

    /// <summary>What a chunk must answer for a seed. The load takes the first 16 answers as the truth.</summary>
    public static ulong Answer(ulong seed) => (seed * 0x9E3779B97F4A7C15UL) ^ 0xABCDEF;

    private bool Wait(TimeSpan wait, CancellationToken ct)
    {
        var n = ++_waits;
        WaitLengths.Add(wait);
        OnWait?.Invoke(n);
        if (ct.IsCancellationRequested)
        {
            return true;
        }

        for (var i = 0; i < _threads.Count; i++)
        {
            _threads[i].Run(ChunksPerWait(n, i));
        }

        Time.Advance(wait);
        AfterAdvance?.Invoke(wait);
        return ct.IsCancellationRequested;
    }

    private sealed class FakeChunkWork(FakeCpuSession owner) : IChunkWork
    {
        public ulong Chunk(ulong seed)
        {
            var n = Interlocked.Increment(ref owner._chunkCalls);
            lock (owner.Seeds)
            {
                owner.Seeds.Add(seed);
                if (owner._threads.Count == 0)
                {
                    owner.KnownAnswerThreads.Add(Environment.CurrentManagedThreadId);
                }
            }

            if (owner.ChunkThrows(n))
            {
                throw new InvalidOperationException("the fake chunk failed");
            }

            var answer = Answer(seed);
            return owner.WrongAnswer(n) ? answer ^ 1UL : answer;
        }
    }

    private sealed class FakePin(FakeCpuSession owner) : ICpuPin
    {
        public bool PinCurrentThread(LogicalCpu cpu)
        {
            lock (owner._pinCalls)
            {
                owner._pinCalls.Add(cpu);
            }

            return owner.PinThrows(cpu) ? throw new InvalidOperationException("the fake pin failed") : owner.PinAccepts(cpu);
        }
    }

    private sealed class FakeThreadStarter(FakeCpuSession owner) : ILoadThreadStarter
    {
        public ILoadThread Start(string name, Action onStart, Func<bool> step)
        {
            var index = owner._threads.Count;
            if (owner.ThreadStartFails(index))
            {
                throw new InvalidOperationException("the fake OS refused a thread");
            }

            owner.ChunkCallsAtThreadStart.Add(owner._chunkCalls);
            var thread = new FakeLoadThread(name, step);
            owner._threads.Add(thread);
            if (!owner.ThreadNeverStarts(index))
            {
                onStart();
            }

            return thread;
        }
    }

    private sealed class FakeLoadClock(FakeCpuSession owner) : ILoadClock
    {
        public long Timestamp => owner.Time.GetTimestamp();

        public TimeSpan Elapsed(long since) => owner.Time.GetElapsedTime(since);

        public bool WaitCancelled(TimeSpan wait, CancellationToken ct) => owner.Wait(wait, ct);
    }
}

/// <summary>
/// A load thread that works only when the world says so. <see cref="Join"/> runs the steps that are left and demands that the first of them
/// says "stop": a thread that is joined without the stop flag set would run forever, and the test says so instead of hanging.
/// </summary>
internal sealed class FakeLoadThread(string name, Func<bool> step) : ILoadThread
{
    private const int JoinGuard = 100_000;

    public string Name { get; } = name;

    public bool Ended { get; private set; }

    /// <summary>How many times <see cref="Join"/> was called.</summary>
    public int Joins { get; private set; }

    /// <summary>Chunks that were completed after the join started: none, if the stop flag was set first.</summary>
    public int StepsDuringJoin { get; private set; }

    public bool IsAlive => !Ended;

    public void Run(int chunks)
    {
        for (var i = 0; i < chunks && !Ended; i++)
        {
            if (!step())
            {
                Ended = true;
            }
        }
    }

    public void Join()
    {
        Joins++;
        for (var i = 0; !Ended; i++)
        {
            if (i >= JoinGuard)
            {
                throw new InvalidOperationException("Join would never return: the stop flag was not set before the join");
            }

            if (step())
            {
                StepsDuringJoin++;
            }
            else
            {
                Ended = true;
            }
        }
    }
}

/// <summary>Builds results of a load directly, for the tests of the judge: ticks of a constant pace, summarised by the load's own arithmetic.</summary>
internal static class Loads
{
    /// <param name="rates">Chunks per second of each logical CPU, every second.</param>
    /// <param name="pins">The outcome of each pin (default: all pinned).</param>
    /// <param name="sample">The PDH sample of second <c>n</c> (default: healthy).</param>
    public static CpuLoadResult Make(
        IReadOnlyList<LogicalCpu> cpus,
        double[] rates,
        int seconds = 30,
        PinOutcome[]? pins = null,
        Func<int, CpuPdhSample>? sample = null,
        long errors = 0,
        bool stopped = false)
    {
        var result = new CpuLoadResult
        {
            Cpus = cpus,
            Pins = pins ?? [.. cpus.Select(static _ => PinOutcome.Pinned)],
            Seconds = seconds * 1.002,
            Stopped = stopped,
            ComputeErrors = errors,
            Chunks = (long)(rates.Sum() * seconds),
        };
        for (var s = 1; s <= seconds; s++)
        {
            result.Ticks.Add(new CpuLoadTick(s, (double[])rates.Clone(), rates.Sum(), (sample ?? (static _ => PdhSamples.Healthy()))(s), errors));
        }

        CpuLoadTest.Summarise(result);
        return result;
    }

    /// <summary>The same, but every second's pace of every CPU is given (a series that is not flat).</summary>
    public static CpuLoadResult FromSeries(IReadOnlyList<LogicalCpu> cpus, Func<int, double[]> ratesOfSecond, int seconds, Func<int, CpuPdhSample>? sample = null)
    {
        var result = new CpuLoadResult { Cpus = cpus, Pins = [.. cpus.Select(static _ => PinOutcome.Pinned)], Seconds = seconds * 1.002 };
        for (var s = 1; s <= seconds; s++)
        {
            var rates = ratesOfSecond(s);
            result.Ticks.Add(new CpuLoadTick(s, rates, rates.Sum(), (sample ?? (static _ => PdhSamples.Healthy()))(s), 0));
            result.Chunks += (long)rates.Sum();
        }

        CpuLoadTest.Summarise(result);
        return result;
    }
}

/// <summary>Reads of the rows of a result.</summary>
internal static class Rows
{
    public static CheckItem One(IEnumerable<CheckItem> items, string id) => items.Single(i => i.Id == id);

    public static IReadOnlyList<CheckItem> All(IEnumerable<CheckItem> items, string id) => [.. items.Where(i => i.Id == id)];

    public static double Number(CheckItem item) => item.Value!.AsNumber;
}
