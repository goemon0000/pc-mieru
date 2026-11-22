namespace PcMieru.Checks.Load;

/// <summary>The physical memory of the machine now: all of it, and what is free (not in use, not on a standby list that must be given back).</summary>
internal readonly record struct MemoryStatus(ulong TotalBytes, ulong AvailableBytes);

/// <summary>
/// The pieces of one memory run that touch the real machine (DESIGN-A §2: the memory test-session factory, the same seam as
/// <see cref="ICpuTestSession"/>). The test itself runs on top of them, so a unit test drives it with managed arrays for memory, no real
/// thread, a clock it moves by hand and a free-memory figure it scripts: it never borrows a gigabyte from the machine it runs on.
/// <para>
/// A session reuses the seams of the CPU run for the threads (<see cref="ILoadThreadStarter"/>) and the clock (<see cref="ILoadClock"/>).
/// </para>
/// </summary>
internal interface IMemoryTestSession : IDisposable
{
    /// <summary>The size of one block, in bytes: 64 MiB in the real session. A multiple of 8.</summary>
    long BlockBytes { get; }

    /// <summary>How many logical processors the machine has (the test uses half of them for its threads).</summary>
    int LogicalProcessorCount { get; }

    /// <summary>Starts a work thread.</summary>
    ILoadThreadStarter Threads { get; }

    /// <summary>The run's clock and its wait between two readings of the free memory.</summary>
    ILoadClock Clock { get; }

    /// <summary>The physical memory now, or null when Windows could not say. Read at the start and then every 0.25 s, by the control loop only.</summary>
    MemoryStatus? ReadStatus();

    /// <summary>
    /// Allocates one block, aligned to a page, and touches every one of its pages so that Windows really gives it the memory (until then the
    /// free memory does not fall, and a check of it would mean nothing).
    /// </summary>
    /// <exception cref="OutOfMemoryException">There is no memory for another block. The run stops growing there; it is not a failure.</exception>
    IMemoryBlock Allocate();

    /// <summary>A new seed for the patterns of the next pass.</summary>
    ulong NextSeed();
}

/// <summary>One block of memory that was borrowed. It is freed once, after every thread that works on it has ended.</summary>
internal interface IMemoryBlock
{
    /// <summary>
    /// Words <paramref name="firstWord"/> up to <paramref name="firstWord"/> + <paramref name="count"/> of the block. Several threads may ask
    /// for different parts of the same block at the same time.
    /// </summary>
    Span<ulong> Words(int firstWord, int count);

    /// <summary>Gives the block back. The run calls it exactly once, and only when no thread can touch the block any more.</summary>
    void Free();
}
