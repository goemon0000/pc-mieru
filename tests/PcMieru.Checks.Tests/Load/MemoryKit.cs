using PcMieru.Checks.Load;
using PcMieru.Checks.Tests.SystemInfo;

namespace PcMieru.Checks.Tests.Load;

/// <summary>One call of <see cref="IMemoryBlock.Words"/> in the fake world: which access it is (1 is the first), which block, which words.</summary>
internal readonly record struct MemoryAccess(int Number, int BlockIndex, int FirstWord, int Count);

/// <summary>
/// A whole memory test session in memory: blocks are managed arrays, the clock is moved by hand, the work threads run when the world runs, the
/// free memory is a number the test scripts, and an allocation that fails is an exception the test throws. Nothing real is touched: no thread,
/// no native memory, no GlobalMemoryStatusEx.
/// <para>
/// The world is simple. Every wait of the control loop is one "second step": while the clock is moved by the length of the wait, every live
/// fake thread does <see cref="StepsPerWait"/> slices, thread by thread (wait number, then thread index), so the accesses to the blocks are
/// numbered one after the other. A cancellation during the wait cuts it short and nothing more happens. Every access to a block costs
/// <see cref="SliceCost"/> of fake time, so the phases have a duration and the rates are positive; a block that is allocated costs
/// <see cref="AllocateCost"/>.
/// </para>
/// </summary>
internal sealed class FakeMemorySession : IMemoryTestSession
{
    /// <summary>The size of a block of the fake world: 64 KiB, 8192 words.</summary>
    public const long SmallBlock = 64 << 10;

    public const ulong Gib = 1UL << 30;

    private readonly List<FakeLoadThread> _threads = [];
    private readonly List<FakeBlock> _blocks = [];
    private readonly object _gate = new();
    private ulong _available;
    private int _reads;
    private int _allocCalls;
    private int _accesses;
    private int _waits;
    private int _seedCalls;

    public FakeMemorySession(ulong total, ulong available, long blockBytes = SmallBlock, int logicalProcessors = 8, ManualTimeProvider? time = null)
    {
        Total = total;
        _available = available;
        BlockBytes = blockBytes;
        LogicalProcessorCount = logicalProcessors;
        Time = time ?? new ManualTimeProvider();
    }

    /// <summary>
    /// A machine of 8 GiB (the floor is 1 GiB) whose free memory lets the plan borrow exactly <paramref name="blocks"/> blocks: the floor, that
    /// many blocks and half a block over.
    /// </summary>
    public static FakeMemorySession Planned(int blocks, long blockBytes = SmallBlock, int logicalProcessors = 8, ManualTimeProvider? time = null) =>
        new(8 * Gib, Gib + (ulong)(blocks * blockBytes) + (ulong)(blockBytes / 2), blockBytes, logicalProcessors, time);

    /// <summary>The floor of this machine (what the test leaves free).</summary>
    public ulong Floor => (ulong)MemoryTest.FloorBytes((long)Total);

    public ulong Total { get; }

    public long BlockBytes { get; }

    public int LogicalProcessorCount { get; }

    /// <summary>The clock of the run. The run never sees another one.</summary>
    public ManualTimeProvider Time { get; }

    /// <summary>How many slices the thread with this index does in wait number <c>wait</c> (1 is the first). Default: three.</summary>
    public Func<int, int, int> StepsPerWait { get; set; } = static (_, _) => 3;

    /// <summary>Fake time for every access to a block (a slice).</summary>
    public TimeSpan SliceCost { get; set; } = TimeSpan.FromMilliseconds(1);

    /// <summary>Fake time for every block that is allocated.</summary>
    public TimeSpan AllocateCost { get; set; }

    /// <summary>Added to every wait of the load clock: a clock that oversleeps (the real one sleeps a little longer than asked).</summary>
    public TimeSpan Oversleep { get; set; }

    /// <summary>
    /// The free memory for reading number <c>n</c> of the status (1 is the first: the start). Null (the default, or a null answer) is the natural
    /// number: what was free at the start minus the blocks that were allocated.
    /// </summary>
    public Func<int, ulong?>? AvailableAt { get; set; }

    /// <summary>Windows cannot say for reading number <c>n</c>.</summary>
    public Func<int, bool> StatusUnreadable { get; set; } = static _ => false;

    /// <summary>The allocation call number <c>n</c> (1 is the first) throws <see cref="OutOfMemoryException"/>, as <c>NativeMemory.AlignedAlloc</c> does.</summary>
    public Func<int, bool> AllocateFails { get; set; } = static _ => false;

    /// <summary>The seed of the pass that starts as number <c>n</c> (1 is the first).</summary>
    public Func<int, ulong> SeedOf { get; set; } = static n => 0x5EED_0000UL + (ulong)n;

    /// <summary>The access with this number throws this exception (null: it does not). A worker fault; an out of memory too.</summary>
    public Func<int, Exception?> AccessFault { get; set; } = static _ => null;

    /// <summary>The block with this index throws when it is freed.</summary>
    public Func<int, bool> FreeFails { get; set; } = static _ => false;

    /// <summary>Called at the start of every access to a block, before the words are given out: a test flips a bit here, or cancels the run.</summary>
    public Action<MemoryAccess>? OnAccess { get; set; }

    /// <summary>Called at the start of every wait with its number, before anything happens in it. A test cancels the run from here.</summary>
    public Action<int>? OnWait { get; set; }

    /// <summary>Called after the clock of the run was moved, with the length of the wait: a test moves a second clock from here.</summary>
    public Action<TimeSpan>? AfterAdvance { get; set; }

    /// <summary>Called when an allocation is made, with its number (before the block exists). A test cancels the run from here.</summary>
    public Action<int>? OnAllocate { get; set; }

    /// <summary>Called when a work thread is joined, with its index, after the join was logged: a test cancels the run while it is ending.</summary>
    public Action<int>? OnJoin { get; set; }

    /// <summary>What happened to the machine's memory and threads, in order: <c>alloc</c>, <c>join</c>, <c>free</c>, with the index of the block or of the thread.</summary>
    public List<(string Kind, int Index)> Events { get; } = [];

    public IReadOnlyList<FakeLoadThread> WorkThreads => _threads;

    public IReadOnlyList<FakeBlock> Blocks => _blocks;

    public int Waits => _waits;

    public int Accesses => _accesses;

    public int AllocateCalls => _allocCalls;

    public int StatusReads => _reads;

    /// <summary>The seeds that were handed out, in order.</summary>
    public List<ulong> Seeds { get; } = [];

    /// <summary>The lengths of the waits the run asked for.</summary>
    public List<TimeSpan> WaitLengths { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>
    /// The real-time backstops the run armed through the clock (DESIGN-A §6.2), in order. None of them fires on wall time: a test calls
    /// <see cref="FakeBackstop.Fire"/> to say "the real time has run out".
    /// </summary>
    public List<FakeBackstop> Backstops { get; } = [];

    ILoadThreadStarter IMemoryTestSession.Threads => new FakeThreadStarter(this);

    ILoadClock IMemoryTestSession.Clock => new FakeLoadClock(this);

    public MemoryStatus? ReadStatus()
    {
        var n = ++_reads;
        if (StatusUnreadable(n))
        {
            return null;
        }

        return new MemoryStatus(Total, AvailableAt?.Invoke(n) ?? _available);
    }

    public IMemoryBlock Allocate()
    {
        var n = ++_allocCalls;
        OnAllocate?.Invoke(n);
        if (AllocateFails(n))
        {
            throw new OutOfMemoryException("the fake allocator has no more memory");
        }

        Time.Advance(AllocateCost);
        _available -= (ulong)BlockBytes;
        var block = new FakeBlock(this, _blocks.Count);
        _blocks.Add(block);
        Events.Add(("alloc", block.Index));
        return block;
    }

    public ulong NextSeed()
    {
        lock (_gate)
        {
            var seed = SeedOf(++_seedCalls);
            Seeds.Add(seed);
            return seed;
        }
    }

    public void Dispose() => Disposed = true;

    private FakeBackstop Arm(TimeSpan delay, Action callback)
    {
        var backstop = new FakeBackstop(delay, callback);
        Backstops.Add(backstop);
        return backstop;
    }

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
            _threads[i].Run(StepsPerWait(n, i));
        }

        Time.Advance(wait + Oversleep);
        AfterAdvance?.Invoke(wait);
        return ct.IsCancellationRequested;
    }

    private Span<ulong> Touch(FakeBlock block, int firstWord, int count)
    {
        var n = ++_accesses;
        if (block.Freed)
        {
            throw new ObjectDisposedException(nameof(FakeBlock), "a thread touched a block after it was freed");
        }

        OnAccess?.Invoke(new MemoryAccess(n, block.Index, firstWord, count));
        if (AccessFault(n) is { } fault)
        {
            throw fault;
        }

        Time.Advance(SliceCost);
        return new Span<ulong>(block.Cells, firstWord, count);
    }

    internal sealed class FakeBlock : IMemoryBlock
    {
        private readonly FakeMemorySession _owner;

        public FakeBlock(FakeMemorySession owner, int index)
        {
            _owner = owner;
            Index = index;
            Cells = new ulong[owner.BlockBytes / sizeof(ulong)];
        }

        public int Index { get; }

        public ulong[] Cells { get; }

        public bool Freed { get; private set; }

        public int FreeCalls { get; private set; }

        public Span<ulong> Words(int firstWord, int count) => _owner.Touch(this, firstWord, count);

        public void Free()
        {
            FreeCalls++;
            _owner.Events.Add(("free", Index));
            Freed = true;
            _owner._available += (ulong)_owner.BlockBytes;
            if (_owner.FreeFails(Index))
            {
                throw new InvalidOperationException("the fake block could not be freed");
            }
        }

        /// <summary>Flips one bit of one word: a cell of the memory that does not hold what was written.</summary>
        public void Flip(int word, int bit) => Cells[word] ^= 1UL << bit;
    }

    private sealed class FakeThreadStarter(FakeMemorySession owner) : ILoadThreadStarter
    {
        public ILoadThread Start(string name, Action onStart, Func<bool> step, Action<Exception> onFault)
        {
            var index = owner._threads.Count;
            var thread = new FakeLoadThread(name, step, onFault);
            owner._threads.Add(thread);
            onStart();
            return new LoggedThread(owner, index, thread);
        }
    }

    // The thread of the fake world, with its join in the order of events.
    private sealed class LoggedThread(FakeMemorySession owner, int index, FakeLoadThread inner) : ILoadThread
    {
        public bool IsAlive => inner.IsAlive;

        public void Join()
        {
            owner.Events.Add(("join", index));
            owner.OnJoin?.Invoke(index);
            inner.Join();
        }
    }

    private sealed class FakeLoadClock(FakeMemorySession owner) : ILoadClock
    {
        public long Timestamp => owner.Time.GetTimestamp();

        public TimeSpan Elapsed(long since) => owner.Time.GetElapsedTime(since);

        public bool WaitCancelled(TimeSpan wait, CancellationToken ct) => owner.Wait(wait, ct);

        public IDisposable CallAfter(TimeSpan delay, Action callback) => owner.Arm(delay, callback);
    }
}

/// <summary>A real-time callback of the fake clock: it records what it was asked for and fires only when a test says so, never on wall time.</summary>
internal sealed class FakeBackstop(TimeSpan delay, Action callback) : IDisposable
{
    public TimeSpan Delay => delay;

    public bool Disposed { get; private set; }

    public int Fired { get; private set; }

    /// <summary>The real time ran out: the callback runs now, on the calling thread (unless the backstop was disposed first, like a real timer).</summary>
    public void Fire()
    {
        if (!Disposed)
        {
            Fired++;
            callback();
        }
    }

    public void Dispose() => Disposed = true;
}

/// <summary>The backstop of a fake clock that has none to give: it never fires, and disposing it does nothing.</summary>
internal sealed class NeverFires : IDisposable
{
    public static readonly NeverFires Instance = new();

    public void Dispose()
    {
    }
}

/// <summary>Reads of what a fake world did to the machine's memory and threads.</summary>
internal static class MemoryEvents
{
    /// <summary>
    /// Every join of a work thread came before the first free of a block, every thread that was started was joined once, and every block that was
    /// allocated was freed exactly once.
    /// </summary>
    public static void AssertJoinedThenFreed(FakeMemorySession s)
    {
        var joins = s.Events.Select(static (e, i) => (e, i)).Where(static x => x.e.Kind == "join").Select(static x => x.i).ToList();
        var frees = s.Events.Select(static (e, i) => (e, i)).Where(static x => x.e.Kind == "free").Select(static x => x.i).ToList();
        Assert.Equal(s.WorkThreads.Count, joins.Count);
        Assert.Equal(s.Events.Where(static e => e.Kind == "join").Select(static e => e.Index).Order(), Enumerable.Range(0, s.WorkThreads.Count));
        if (joins.Count > 0 && frees.Count > 0)
        {
            Assert.True(joins.Max() < frees.Min(), "a block was freed before every work thread was joined");
        }

        Assert.Equal(s.Blocks.Count, frees.Count);
        Assert.All(s.Blocks, static b =>
        {
            Assert.True(b.Freed);
            Assert.Equal(1, b.FreeCalls);
        });
        Assert.All(s.WorkThreads, static t => Assert.False(t.IsAlive));
    }
}
