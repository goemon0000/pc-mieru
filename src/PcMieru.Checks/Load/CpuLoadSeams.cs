using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Collectors;

namespace PcMieru.Checks.Load;

/// <summary>
/// The pieces of one CPU run that touch the real machine (DESIGN-A §2: the CPU test-session factory). The load itself runs on top of them, so
/// a unit test drives it with no real load, no real threads, no real PDH and a clock it moves by hand.
/// </summary>
internal interface ICpuTestSession : IDisposable
{
    /// <summary>The logical processors, in order (group, then number). One load thread runs on each.</summary>
    IReadOnlyList<LogicalCpu> Cpus { get; }

    /// <summary>The computation of one chunk.</summary>
    IChunkWork Work { get; }

    /// <summary>Pins a load thread to its logical processor.</summary>
    ICpuPin Pin { get; }

    /// <summary>Starts a load thread.</summary>
    ILoadThreadStarter Threads { get; }

    /// <summary>The run's clock and its wait.</summary>
    ILoadClock Clock { get; }

    /// <summary>
    /// One collection of the check's own PDH query: the effective clock, the utilisation and the package power. Never 0 for a value that is missing.
    /// A counter call can hang and cannot be interrupted, so the load never makes this call on its control path (see <see cref="PdhSampler"/>): it
    /// runs on a thread of its own and a call that does not come back in time is a missing sample. For the same reason <see cref="IDisposable.Dispose"/>
    /// of a session never waits for a call that is still in flight: it closes the query when that call returns (or at once, if none is in flight).
    /// </summary>
    CpuPdhSample SamplePdh();
}

/// <summary>The computation of one chunk: the same input always gives the same answer, on a CPU that computes correctly.</summary>
internal interface IChunkWork
{
    ulong Chunk(ulong seed);
}

/// <summary>Pins the calling thread to one logical processor. True when Windows accepted it. An exception is an outcome that is not known.</summary>
internal interface ICpuPin
{
    bool PinCurrentThread(LogicalCpu cpu);
}

/// <summary>Whether the pin of a load thread took. <see cref="Unknown"/> is also what a thread that never got to pin leaves behind.</summary>
internal enum PinOutcome
{
    Unknown = 0,
    Pinned,
    Failed,
}

/// <summary>One load thread. It lives from <see cref="ILoadThreadStarter.Start"/> until its step says to stop or it faults; <see cref="Join"/> waits for that.</summary>
internal interface ILoadThread
{
    bool IsAlive { get; }

    /// <summary>Waits until the thread has ended. A thread that has to be joined never leaves load running behind it.</summary>
    void Join();
}

/// <summary>
/// Starts a load thread that runs <c>onStart()</c> once and then <c>step()</c> again and again while it returns true. A step is one chunk:
/// the thread can only stop between two chunks.
/// <para>
/// Nothing a step or <c>onStart</c> throws is swallowed: any exception that escapes them (an OutOfMemoryException too) ends the thread and is
/// handed to <paramref name="onFault"/> on that thread, once. The run then fails; a load thread that dies without a word would leave a CPU
/// unloaded and the result looking like a finding about it.
/// </para>
/// </summary>
internal interface ILoadThreadStarter
{
    ILoadThread Start(string name, Action onStart, Func<bool> step, Action<Exception> onFault);
}

/// <summary>The clock of the load run and its wait between two seconds.</summary>
internal interface ILoadClock
{
    long Timestamp { get; }

    TimeSpan Elapsed(long since);

    /// <summary>Waits for <paramref name="wait"/> or until <paramref name="ct"/> is cancelled. True when it was cancelled.</summary>
    bool WaitCancelled(TimeSpan wait, CancellationToken ct);
}
