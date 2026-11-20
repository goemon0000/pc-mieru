// Provenance: PartsLife.Checks.Load.MemoryTest (pl-cloud 25e4571, src/PartsLife.Checks/Load/MemoryTest.cs), MIT, same owner. PlanBytes (the
// fraction, the cap, the floor, the 64 MiB rounding), the block-by-block growth with a free-memory check before each block, the rotation of the
// pattern kinds, the 4 MiB slices and the pull-back when the free memory falls are the old ones. What is new: the machine behind a seam, dedicated
// threads instead of Parallel.For, one reading of the free memory every 0.25 s in one place, the absolute deadline from the start of the run,
// the join of every thread before any block is freed, and an allocation that fails as an exception.
namespace PcMieru.Checks.Load;

/// <summary>What a memory run measured. It says nothing about verdicts: <see cref="MemoryJudge"/> does that.</summary>
internal sealed class MemoryLoadResult
{
    /// <summary>False when Windows could not say how much memory there is: nothing was planned and nothing was borrowed.</summary>
    public bool StatusKnown { get; set; }

    public long TotalBytes { get; set; }

    public long AvailableAtStartBytes { get; set; }

    public long BlockBytes { get; set; }

    /// <summary>How many blocks were borrowed. May be fewer than planned: the floor, a refusal of the allocator, the stop or the deadline ended the growth.</summary>
    public int Blocks { get; set; }

    public long BorrowedBytes => Blocks * BlockBytes;

    /// <summary>The number of work threads that were started.</summary>
    public int Workers { get; set; }

    /// <summary>Wall time from the start of the run (the allocation included) to the last work thread ended.</summary>
    public double Seconds { get; set; }

    /// <summary>
    /// The run was cut short: by the caller, or by a fall of the free memory. Everything below is then what was found by then. The deadline is
    /// the normal end of a run and does not set it.
    /// </summary>
    public bool Stopped { get; set; }

    /// <summary>
    /// The machine needed its memory: either no block could be borrowed (the plan was under one block, or the free memory was already at the
    /// floor) or the free memory fell under half the floor during the run (<see cref="Stopped"/> too then).
    /// </summary>
    public bool Pressure { get; set; }

    public long Passes { get; set; }

    /// <summary>Bytes written, in the write phases that finished and in the one that was cut short.</summary>
    public long WriteBytes { get; set; }

    /// <summary>The wall time of those phases (the time in the phases, not in the allocation or the waits between readings).</summary>
    public double WriteSeconds { get; set; }

    /// <summary>Bytes read back and compared, in the verify phases that finished and in the one that was cut short.</summary>
    public long VerifiedBytes { get; set; }

    public double VerifySeconds { get; set; }

    /// <summary>Every place where the read-back differed from what was written, in all verified bytes. Nothing that identifies one is kept.</summary>
    public long Mismatches { get; set; }

    /// <summary>MiB/s over the time in the write phases. Null (missing, never 0) when nothing was written or no time was measured.</summary>
    public double? WriteRate => Rate(WriteBytes, WriteSeconds);

    public double? VerifyRate => Rate(VerifiedBytes, VerifySeconds);

    private static double? Rate(long bytes, double seconds) => bytes > 0 && seconds > 0 ? bytes / 1048576.0 / seconds : null;
}

/// <summary>
/// The memory load (DESIGN-A §6.2): memory is borrowed from Windows block by block, a pattern is written over all of it and read back, again
/// and again with the next pattern, until the time is up. A place that reads back differently from what was written is the finding of the check.
/// It tests only the memory Windows lent to this process; it is not a test of all of the memory.
/// <list type="bullet">
/// <item>Modest on purpose: a quarter of the free memory by default, half at most, and never into the last 1 GiB or tenth of the machine's
/// memory. A machine that starts paging has its disk under test, and other applications stall.</item>
/// <item>The free memory is watched all through the run, in one place (the control loop, every 0.25 s): when it falls under half that floor the
/// test pulls back and the run is stopped.</item>
/// <item>The work threads are dedicated threads (below normal), never the thread pool. Every one of them is joined before any block is freed,
/// and every block is freed once, whatever happened (<c>finally</c>).</item>
/// </list>
/// </summary>
internal static class MemoryTest
{
    /// <summary>The share of the free memory that is borrowed (the request has no say in it).</summary>
    public const double DefaultFraction = 0.25;

    /// <summary>The most that can be asked for: half of the free memory.</summary>
    public const double MaxFraction = 0.5;

    /// <summary>The work is cut into slices of this many words (4 MiB); a thread looks at the stop flag between two.</summary>
    public const int DefaultSliceWords = (4 << 20) / sizeof(ulong);

    /// <summary>The free memory is read, and the stop looked at, every quarter of a second (an absolute schedule from the start of the run).</summary>
    internal static readonly TimeSpan ReadInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>A reading of the progress goes out with every this many readings of the free memory (once a second; the context's gate takes no more).</summary>
    internal const int ReportEveryReadings = 4;

    /// <summary>
    /// A reading of the progress needs at least this much time since the last one. The readings are on an absolute schedule from the start of the
    /// run, so after a long allocation several of them are late and come at once: a rate over the few milliseconds between them is noise, so
    /// that reading is skipped and the next one reports over the longer window.
    /// </summary>
    internal static readonly TimeSpan MinReportWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long after the deadline the stop flag is set whatever the control loop is doing (real time): the backstop for a loop that is stuck, so
    /// that no thread keeps writing into the machine's memory longer than it was asked to.
    /// </summary>
    internal static readonly TimeSpan DeadlineGrace = TimeSpan.FromSeconds(1);

    /// <summary>The free memory that stays: the larger of 1 GiB and a tenth of the machine's memory.</summary>
    public static long FloorBytes(long totalPhys) => Math.Max(1L << 30, totalPhys / 10);

    /// <summary>
    /// How much is borrowed on a machine with <paramref name="availPhys"/> free out of <paramref name="totalPhys"/>. A pure function: the fraction
    /// of the free memory (never above <see cref="MaxFraction"/>), at most half of the free memory, never below the floor
    /// (<see cref="FloorBytes"/>), rounded down to whole blocks. 0 when there is not one block to borrow.
    /// </summary>
    public static long PlanBytes(long availPhys, long totalPhys, double fraction, long? explicitBytes, long blockBytes = RealMemoryTestSession.RealBlockBytes)
    {
        fraction = Math.Clamp(fraction, 0, MaxFraction);
        var floor = FloorBytes(totalPhys);
        var want = explicitBytes ?? (long)(availPhys * fraction);
        want = Math.Min(want, (long)(availPhys * MaxFraction));
        want = Math.Min(want, availPhys - floor);
        return want <= 0 ? 0 : want - (want % blockBytes);
    }

    /// <summary>
    /// Runs the test for <paramref name="duration"/> from now (the allocation counts) or until <paramref name="ct"/> is cancelled, then stops every
    /// thread at its slice boundary, joins them all and only then frees the blocks. Blocking: call it on a worker.
    /// </summary>
    /// <param name="onTick">Called about once a second with the elapsed seconds and the MiB/s since the last call (never over a window shorter than <see cref="MinReportWindow"/>). It must not block.</param>
    /// <param name="sliceWords">The size of a slice in words (a test gives a small one to get several slices in a small block).</param>
    /// <exception cref="CheckRunFailedException">A work thread faulted (a write, a read or the pattern threw; out of memory too), or a block could not be freed. That is the check's own failure, not a finding about the memory.</exception>
    public static MemoryLoadResult Run(IMemoryTestSession session, TimeSpan duration, Action<double, double?>? onTick, CancellationToken ct, int sliceWords = DefaultSliceWords)
    {
        ArgumentNullException.ThrowIfNull(session);
        var clock = session.Clock;
        var blockBytes = session.BlockBytes;
        var startStamp = clock.Timestamp;
        var result = new MemoryLoadResult { BlockBytes = blockBytes };
        var stop = new MemoryStop();

        // Two things set the stop flag besides the end of the control loop, and neither waits for the loop: the cancel, and the deadline.
        using var stopOnCancel = ct.Register(stop.Request);
        using var stopAtDeadline = new Timer(_ => stop.Request(), null, duration + DeadlineGrace, Timeout.InfiniteTimeSpan);

        if (session.ReadStatus() is not { } start)
        {
            // Windows could not say how much memory there is: nothing can be planned, nothing is borrowed, and that is not a finding about the memory.
            result.Seconds = clock.Elapsed(startStamp).TotalSeconds;
            result.Stopped = ct.IsCancellationRequested;
            return result;
        }

        result.StatusKnown = true;
        result.TotalBytes = ToLong(start.TotalBytes);
        result.AvailableAtStartBytes = ToLong(start.AvailableBytes);
        var floor = FloorBytes(result.TotalBytes);
        var wantedBlocks = PlanBytes(result.AvailableAtStartBytes, result.TotalBytes, DefaultFraction, null, blockBytes) / blockBytes;

        var blocks = new List<IMemoryBlock>();
        var workers = new List<ILoadThread>();
        MemoryPasses? passes = null;
        Exception? firstFault = null;
        long faults = 0;
        var stopped = false;
        var pressure = false;
        var deadlineInAllocation = false;
        var freeFailed = false;
        TimeSpan elapsedAtEnd;
        try
        {
            // Grow block by block. A block is touched when it is allocated, so the free memory has really fallen before it is read again.
            for (long i = 0; i < wantedBlocks; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }

                if (clock.Elapsed(startStamp) >= duration)
                {
                    deadlineInAllocation = true;
                    break;
                }

                if (session.ReadStatus() is not { } now || ToLong(now.AvailableBytes) - blockBytes < floor)
                {
                    break; // the next block would take the machine under the floor (or Windows cannot say): stop growing here
                }

                IMemoryBlock block;
                try
                {
                    block = session.Allocate();
                }
                catch (OutOfMemoryException)
                {
                    break; // the allocator has no more to give: that is the end of the growth, not a failure
                }

                blocks.Add(block);
            }

            if (!stopped && !deadlineInAllocation && blocks.Count > 0)
            {
                passes = new MemoryPasses(blocks, session, stop, startStamp, duration, sliceWords);
                var logical = Math.Max(1, session.LogicalProcessorCount);
                var threads = Math.Clamp(logical / 2, 1, logical);
                for (var t = 0; t < threads; t++)
                {
                    workers.Add(session.Threads.Start(
                        $"PcMieru.MemoryLoad.{t}",
                        static () => { },
                        passes.Step,
                        ex =>
                        {
                            Interlocked.CompareExchange(ref firstFault, ex, null);
                            Interlocked.Increment(ref faults);
                            stop.Request(); // the others stop at their next slice: no point in loading the machine for the rest of the time
                        }));
                }

                (stopped, pressure) = ControlLoop(session, passes, duration, floor, startStamp, onTick, ct, () => Interlocked.Read(ref faults) > 0);
            }
        }
        finally
        {
            // Stop, then wait for every thread that was started, and only then give the memory back: no thread can write into a freed block.
            stop.Request();
            foreach (var worker in workers)
            {
                worker.Join();
            }

            elapsedAtEnd = clock.Elapsed(startStamp);
            passes?.Settle();
            foreach (var block in blocks)
            {
                try
                {
                    block.Free();
                }
                catch (Exception)
                {
                    freeFailed = true; // the others are still freed; the run fails below
                }
            }
        }

        if (Interlocked.Read(ref faults) > 0)
        {
            // The type only: a message of the code under test may carry an address, and the detail must not.
            throw new CheckRunFailedException(CheckRunFailureCodes.InternalError, $"a memory thread faulted: {Volatile.Read(ref firstFault)?.GetType().Name}");
        }

        if (freeFailed)
        {
            throw new CheckRunFailedException(CheckRunFailureCodes.InternalError, "a block could not be freed");
        }

        result.Blocks = blocks.Count;
        result.Workers = workers.Count;
        result.Seconds = elapsedAtEnd.TotalSeconds;
        result.Stopped = stopped;
        result.Pressure = pressure || (blocks.Count == 0 && !stopped && !deadlineInAllocation);
        if (passes is not null)
        {
            result.Passes = passes.Passes;
            result.WriteBytes = passes.WriteBytes;
            result.WriteSeconds = passes.WriteSeconds;
            result.VerifiedBytes = passes.VerifiedBytes;
            result.VerifySeconds = passes.VerifySeconds;
            result.Mismatches = passes.Mismatches;
        }

        return result;
    }

    // Every 0.25 s, on an absolute schedule: the stop, a fault, the end of the time, the free memory (read here and nowhere else) and the progress.
    private static (bool Stopped, bool Pressure) ControlLoop(
        IMemoryTestSession session,
        MemoryPasses passes,
        TimeSpan duration,
        long floor,
        long startStamp,
        Action<double, double?>? onTick,
        CancellationToken ct,
        Func<bool> faulted)
    {
        var clock = session.Clock;
        var lastReport = clock.Elapsed(startStamp);
        var lastBytes = passes.ProcessedBytes;
        for (var tick = 1; ; tick++)
        {
            // Reading k is due k intervals after the start (but never after the end of the time): a wait that oversleeps does not push the
            // readings after it later, it only shortens the next wait.
            var due = TimeSpan.FromTicks(Math.Min(ReadInterval.Ticks * tick, duration.Ticks));
            var wait = due - clock.Elapsed(startStamp);
            if (wait > TimeSpan.Zero && clock.WaitCancelled(wait, ct))
            {
                return (true, false);
            }

            if (ct.IsCancellationRequested)
            {
                return (true, false);
            }

            if (faulted())
            {
                return (false, false); // the check itself is broken: the run fails after the join
            }

            var now = clock.Elapsed(startStamp);
            if (now >= duration)
            {
                return (false, false); // the time is up: the normal end
            }

            // Under half the floor (or no answer at all): the machine needs its memory, and the test pulls back.
            if (session.ReadStatus() is not { } status || ToLong(status.AvailableBytes) < floor / 2)
            {
                return (true, true);
            }

            if (tick % ReportEveryReadings == 0 && now - lastReport >= MinReportWindow)
            {
                var bytes = passes.ProcessedBytes;
                onTick?.Invoke(now.TotalSeconds, (bytes - lastBytes) / 1048576.0 / (now - lastReport).TotalSeconds);
                lastReport = now;
                lastBytes = bytes;
            }
        }
    }

    private static long ToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
