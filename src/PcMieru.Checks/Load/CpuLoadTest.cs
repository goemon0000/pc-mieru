// Provenance: PartsLife.Checks.Load.CpuLoadTest (pl-cloud 25e4571, src/PartsLife.Checks/Load/CpuLoadTest.cs), MIT, same owner. The chunk, its
// 16 seeds and their answers, the counters 128 bytes apart, the one-second tick loop and the join at the end are the old ones; the clock, the
// threads, the pin and the chunk are behind seams, and the result keeps what DESIGN-A 14.2 needs (pins, per-second clock samples).
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Collectors;

namespace PcMieru.Checks.Load;

/// <summary>One second of the load: the pace of every logical CPU, the total, what the PDH query saw, and the compute errors so far.</summary>
/// <param name="Second">1 for the first second.</param>
/// <param name="PerCpu">Chunks per second of each logical CPU, in the order of the session's list.</param>
internal sealed record CpuLoadTick(int Second, double[] PerCpu, double Total, CpuPdhSample Sample, long ComputeErrors);

/// <summary>What a load run measured. It says nothing about verdicts: <see cref="CpuJudge"/> does that.</summary>
internal sealed class CpuLoadResult
{
    public IReadOnlyList<LogicalCpu> Cpus { get; init; } = [];

    /// <summary>Whether the pin of each load thread took, in the order of <see cref="Cpus"/>.</summary>
    public IReadOnlyList<PinOutcome> Pins { get; init; } = [];

    public List<CpuLoadTick> Ticks { get; } = [];

    /// <summary>Wall time of the load, from the start to the last thread ended.</summary>
    public double Seconds { get; set; }

    /// <summary>The run was cut short by the caller. Everything below is then what was found by then.</summary>
    public bool Stopped { get; set; }

    public long ComputeErrors { get; set; }

    /// <summary>Chunks computed and compared with their known answer, all threads together.</summary>
    public long Chunks { get; set; }

    /// <summary>The average chunks per second of each logical CPU, without the first two seconds (the ramp-up) when there are more than four.</summary>
    public double[] PerCpuAverage { get; set; } = [];

    public SteadinessResult? Steadiness { get; set; }

    /// <summary>Only when every second has a clock sample.</summary>
    public SteadinessResult? ClockSteadiness { get; set; }
}

/// <summary>
/// The CPU load (DESIGN-A §6.2): one dedicated thread per logical CPU, below-normal priority, pinned by group and CPU number, each computing
/// a known chunk again and again and comparing the answer with the one it must give. A wrong answer under load is a CPU that miscalculates
/// (an overclock too high, too little voltage, heat): the heaviest finding of the check.
/// <list type="bullet">
/// <item>A thread stops only between two chunks, and every thread is joined before the run reports anything: no load outlives the report.</item>
/// <item>The expected answers are computed once, on the calling thread, before the load starts.</item>
/// <item>Every second the per-CPU counters are read and the check's PDH query is collected once.</item>
/// </list>
/// </summary>
internal static class CpuLoadTest
{
    public const int ChunkIterations = 1 << 16;

    /// <summary>The number of different inputs the threads cycle through.</summary>
    internal const int SeedCount = 16;

    // The counters sit 128 bytes apart (16 longs): a core does not fight its neighbour for a cache line.
    private const int Stride = 16;

    /// <summary>
    /// One second between two readings, plus a little, so that two readings never fall closer than the progress rate limit of one a second. Reading
    /// k is due k intervals after the start (an absolute schedule): a wait that oversleeps does not push the readings after it later, it only
    /// shortens the next wait, and the run ends when it was asked to (a reading that comes too early may be dropped by the progress gate).
    /// </summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// How long a reading waits for its PDH sample before it goes on without it (the sample is then missing, never 0). Real time. The PDH call is
    /// normally a few milliseconds; this only bounds a call that hangs.
    /// </summary>
    internal static readonly TimeSpan SampleWait = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long after the last reading is due the stop flag is set whatever the control loop is doing (real time). In a healthy run the loop is
    /// done long before; this is the backstop for a loop that is stuck, so that no load outlives the time that was asked for. It is armed through
    /// <see cref="ILoadClock.CallAfter"/>: the real clock fires it on wall time, a test's clock only when the test says so.
    /// </summary>
    internal static readonly TimeSpan DeadlineGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// One chunk of computation. The same seed always gives the same answer. Integer multiplication and shifts are mixed with floating-point
    /// multiplication and addition.
    /// </summary>
    public static ulong Chunk(ulong seed, int iterations = ChunkIterations)
    {
        var x = seed | 1;
        var d = ((seed & 0xFFFF) * 1e-3) + 1.0;
        for (var i = 0; i < iterations; i++)
        {
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            x = unchecked((x * 0x9E3779B97F4A7C15UL) + (ulong)i);
            d = (d * 1.0000001) + 0.5;
            if (d > 1e6)
            {
                d -= 999_999.0;
            }
        }

        return x ^ BitConverter.DoubleToUInt64Bits(d);
    }

    /// <summary>The input of the k-th seed.</summary>
    internal static ulong SeedOf(int k) => ((ulong)k * 0x1234567UL) + 99;

    /// <summary>
    /// Runs the load for <paramref name="duration"/> (a whole number of seconds: that many readings) or until <paramref name="ct"/> is
    /// cancelled, then stops every thread at its chunk boundary and joins them all. Blocking: call it on a worker.
    /// <para>
    /// The stop flag does not wait for the control loop: it is set by the cancel itself (<see cref="CancellationToken.Register(Action)"/>) and by an
    /// absolute deadline (<see cref="DeadlineGrace"/> after the last reading is due), as well as when the loop ends. And the loop never waits
    /// for the counters: the PDH sample is taken by <see cref="PdhSampler"/> and waited for at most <paramref name="sampleWait"/>.
    /// </para>
    /// </summary>
    /// <param name="onTick">Called at the start of every second's reading with the elapsed seconds and the total chunks per second. It must not block.</param>
    /// <param name="sampleWait">How long a reading waits for its PDH sample (null: <see cref="SampleWait"/>). A test gives a short one for a sampler that hangs, or an infinite one to make a control loop that is stuck.</param>
    /// <exception cref="CheckRunFailedException">A load thread faulted: the chunk (or its set-up) threw. That is the check's own failure, not a finding about the CPU.</exception>
    public static CpuLoadResult Run(ICpuTestSession session, TimeSpan duration, Action<double, double>? onTick, CancellationToken ct, TimeSpan? sampleWait = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var cpus = session.Cpus;
        var n = cpus.Count;
        if (n == 0)
        {
            throw new CheckRunFailedException(CheckRunFailureCodes.InternalError, "no logical processor");
        }

        var work = session.Work;
        var clock = session.Clock;
        var seconds = Math.Max(1, (int)Math.Round(duration.TotalSeconds));

        // The answers first, before any load, on this one thread.
        var expected = new ulong[SeedCount];
        for (var k = 0; k < SeedCount; k++)
        {
            expected[k] = work.Chunk(SeedOf(k));
        }

        var counters = new long[n * Stride];
        var pins = new int[n];
        long errors = 0;
        long faults = 0;
        Exception? firstFault = null;
        var stop = 0;

        var started = new List<ILoadThread>(n);
        var ticks = new List<CpuLoadTick>();
        var stopped = false;
        var startStamp = clock.Timestamp;
        TimeSpan elapsedAtEnd;

        // Two things set the stop flag besides the end of the loop, and neither waits for the loop: the cancel, and the deadline.
        using var stopOnCancel = ct.Register(() => Volatile.Write(ref stop, 1));
        using var stopAtDeadline = clock.CallAfter((TickInterval * seconds) + DeadlineGrace, () => Volatile.Write(ref stop, 1));

        // The PDH sample is taken on a thread of its own: a counter call that hangs delays nothing here.
        using var sampler = new PdhSampler(session.SamplePdh, ct);
        var sampleBound = sampleWait ?? SampleWait;
        try
        {
            for (var t = 0; t < n; t++)
            {
                var me = t;
                var cpu = cpus[me];
                var k = me % SeedCount;
                long done = 0;

                started.Add(session.Threads.Start(
                    $"PcMieru.CpuLoad.{me}",
                    () => Volatile.Write(ref pins[me], (int)TryPin(session.Pin, cpu)),
                    () =>
                    {
                        // The stop flag is looked at between two chunks, never inside one.
                        if (Volatile.Read(ref stop) != 0)
                        {
                            return false;
                        }

                        // Nothing is caught here: whatever the chunk throws ends the thread and reaches the fault handler below.
                        var got = work.Chunk(SeedOf(k));
                        if (got != expected[k])
                        {
                            Interlocked.Increment(ref errors);
                        }

                        k = (k + 1) % SeedCount;
                        Volatile.Write(ref counters[me * Stride], ++done);
                        return true;
                    },
                    ex =>
                    {
                        Interlocked.CompareExchange(ref firstFault, ex, null);
                        Interlocked.Increment(ref faults);
                    }));
            }

            var previous = new long[n];
            var lastSample = TimeSpan.Zero;
            for (var tick = 1; tick <= seconds;)
            {
                // Reading k is due k intervals after the start, however late the one before it was.
                var wait = (TickInterval * tick) - clock.Elapsed(startStamp);
                if (wait > TimeSpan.Zero && clock.WaitCancelled(wait, ct))
                {
                    stopped = true;
                    break;
                }

                if (ct.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }

                if (Interlocked.Read(ref faults) > 0)
                {
                    break; // the check itself is broken: no point in loading the machine for the rest of the time, the run fails below
                }

                var now = clock.Elapsed(startStamp);
                var span = Math.Max(0.001, (now - lastSample).TotalSeconds);
                lastSample = now;

                var perCpu = new double[n];
                for (var i = 0; i < n; i++)
                {
                    var count = Volatile.Read(ref counters[i * Stride]);
                    perCpu[i] = (count - previous[i]) / span;
                    previous[i] = count;
                }

                var total = perCpu.Sum();
                onTick?.Invoke(now.TotalSeconds, total);

                ticks.Add(new CpuLoadTick(tick, perCpu, total, sampler.Take(sampleBound), Interlocked.Read(ref errors)));
                tick++;
            }
        }
        finally
        {
            // Stop, then wait for every thread that was started: nothing is reported while load is still running.
            Volatile.Write(ref stop, 1);
            foreach (var thread in started)
            {
                thread.Join();
            }

            elapsedAtEnd = clock.Elapsed(startStamp);
        }

        if (Interlocked.Read(ref faults) > 0)
        {
            // The type only: a message of the code under test may carry a path or an ID, and the detail must not.
            throw new CheckRunFailedException(CheckRunFailureCodes.InternalError, $"a load thread faulted: {Volatile.Read(ref firstFault)?.GetType().Name}");
        }

        var outcomes = new PinOutcome[n];
        for (var i = 0; i < n; i++)
        {
            outcomes[i] = (PinOutcome)Volatile.Read(ref pins[i]);
        }

        var final = new CpuLoadResult { Cpus = cpus, Pins = outcomes, Seconds = elapsedAtEnd.TotalSeconds, Stopped = stopped };
        final.Ticks.AddRange(ticks);
        final.ComputeErrors = Interlocked.Read(ref errors);

        // The counters are read here, after the join, not at the last reading: Chunks includes the chunks that finished after the last tick (and,
        // on a stop, in the stretch the stop cut short), so Chunks is not the sum of the ticks' rates. That is by design (DESIGN-A 3.2: cpu.chunks
        // is what was computed and compared, cpu.throughput is what the steady seconds show).
        for (var i = 0; i < n; i++)
        {
            final.Chunks += Volatile.Read(ref counters[i * Stride]);
        }

        Summarise(final);
        return final;
    }

    /// <summary>The averages without the ramp-up, the steadiness of the total and the steadiness of the clock (only when every second has a clock).</summary>
    internal static void Summarise(CpuLoadResult r)
    {
        var n = r.Cpus.Count;
        var steady = r.Ticks.Skip(r.Ticks.Count > 4 ? Steadiness.DefaultSkip : 0).ToList();
        r.PerCpuAverage = new double[n];
        if (steady.Count > 0)
        {
            for (var i = 0; i < n; i++)
            {
                r.PerCpuAverage[i] = steady.Average(t => t.PerCpu[i]);
            }
        }

        r.Steadiness = Steadiness.Compute(r.Ticks.Select(static t => t.Total).ToList());
        var clocks = r.Ticks.Where(static t => t.Sample.EffectiveMhz is not null).Select(static t => t.Sample.EffectiveMhz!.Value).ToList();
        if (r.Ticks.Count > 0 && clocks.Count == r.Ticks.Count)
        {
            r.ClockSteadiness = Steadiness.Compute(clocks);
        }
    }

    // A pin that throws, or that is not asked at all, is an outcome that is not known; one Windows refuses is a failed pin.
    private static PinOutcome TryPin(ICpuPin pin, LogicalCpu cpu)
    {
        try
        {
            return pin.PinCurrentThread(cpu) ? PinOutcome.Pinned : PinOutcome.Failed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return PinOutcome.Unknown;
        }
    }
}
