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

    /// <summary>One second between two readings, plus a little: the next reading is scheduled from when the last one really happened, so that two never fall closer than the progress rate limit of one a second.</summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(2);

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
    /// </summary>
    /// <param name="onTick">Called at the start of every second's reading with the elapsed seconds and the total chunks per second. It must not block.</param>
    /// <exception cref="CheckRunFailedException">A load thread faulted: the chunk itself threw. That is the check's own failure, not a finding about the CPU.</exception>
    public static CpuLoadResult Run(ICpuTestSession session, TimeSpan duration, Action<double, double>? onTick, CancellationToken ct)
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
        var stop = 0;

        var started = new List<ILoadThread>(n);
        var ticks = new List<CpuLoadTick>();
        var stopped = false;
        var startStamp = clock.Timestamp;
        TimeSpan elapsedAtEnd;
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

                        ulong got;
                        try
                        {
                            got = work.Chunk(SeedOf(k));
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            Interlocked.Increment(ref faults);
                            return false;
                        }

                        if (got != expected[k])
                        {
                            Interlocked.Increment(ref errors);
                        }

                        k = (k + 1) % SeedCount;
                        Volatile.Write(ref counters[me * Stride], ++done);
                        return true;
                    }));
            }

            var previous = new long[n];
            var lastSample = TimeSpan.Zero;
            var next = TickInterval;
            for (var tick = 1; tick <= seconds;)
            {
                var wait = next - clock.Elapsed(startStamp);
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
                next = now + TickInterval;

                var perCpu = new double[n];
                for (var i = 0; i < n; i++)
                {
                    var count = Volatile.Read(ref counters[i * Stride]);
                    perCpu[i] = (count - previous[i]) / span;
                    previous[i] = count;
                }

                var total = perCpu.Sum();
                onTick?.Invoke(now.TotalSeconds, total);

                ticks.Add(new CpuLoadTick(tick, perCpu, total, SafeSample(session), Interlocked.Read(ref errors)));
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
            throw new CheckRunFailedException(CheckRunFailureCodes.InternalError, "a load thread faulted");
        }

        var outcomes = new PinOutcome[n];
        for (var i = 0; i < n; i++)
        {
            outcomes[i] = (PinOutcome)Volatile.Read(ref pins[i]);
        }

        var final = new CpuLoadResult { Cpus = cpus, Pins = outcomes, Seconds = elapsedAtEnd.TotalSeconds, Stopped = stopped };
        final.Ticks.AddRange(ticks);
        final.ComputeErrors = Interlocked.Read(ref errors);
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

    // What the PDH query could not give is a missing sample, never a failed second of the load.
    private static CpuPdhSample SafeSample(ICpuTestSession session)
    {
        try
        {
            return session.SamplePdh();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CpuPdhSample.Missing();
        }
    }
}
