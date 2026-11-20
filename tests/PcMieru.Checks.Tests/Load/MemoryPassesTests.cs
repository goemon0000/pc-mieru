using PcMieru.Checks.Load;

namespace PcMieru.Checks.Tests.Load;

/// <summary>
/// The passes of the memory test over the blocks (DESIGN-A §6.2): which slice a thread takes, when a phase ends, when a pass counts, what a phase
/// that was cut short keeps. Called step by step on one thread over the blocks of a fake session, and once by four real threads over arrays.
/// </summary>
public sealed class MemoryPassesTests
{
    private const long Block = FakeMemorySession.SmallBlock;

    // A fake session, its blocks, a stop flag and the passes over them. A slice of 8192 words is a whole block (one slice per block).
    private sealed class Setup
    {
        public Setup(int blocks, int sliceWords = 8192, TimeSpan? duration = null)
        {
            Session = FakeMemorySession.Planned(blocks);
            IMemoryTestSession seam = Session;
            for (var i = 0; i < blocks; i++)
            {
                Blocks.Add(seam.Allocate());
            }

            Passes = new MemoryPasses(Blocks, seam, Stop, seam.Clock.Timestamp, duration ?? TimeSpan.FromSeconds(30), sliceWords);
        }

        public FakeMemorySession Session { get; }

        public List<IMemoryBlock> Blocks { get; } = [];

        public MemoryStop Stop { get; } = new();

        public MemoryPasses Passes { get; }

        public void Steps(int n)
        {
            for (var i = 0; i < n; i++)
            {
                Assert.True(Passes.Step(), $"step {i + 1} of {n} said stop");
            }
        }
    }

    [Fact]
    public void A_pass_counts_only_when_the_write_and_the_verify_of_the_whole_range_both_finished()
    {
        var t = new Setup(3);

        t.Steps(3); // the write phase
        Assert.Equal(0, t.Passes.Passes);
        Assert.Equal(3 * Block, t.Passes.WriteBytes);
        Assert.Equal(0, t.Passes.VerifiedBytes);

        t.Steps(2); // two of the three blocks verified
        Assert.Equal(0, t.Passes.Passes);

        t.Steps(1);
        Assert.Equal(1, t.Passes.Passes);
        Assert.Equal(3 * Block, t.Passes.VerifiedBytes);
        Assert.Equal(0, t.Passes.Mismatches);
    }

    [Fact]
    public void A_verify_that_was_cut_short_keeps_its_bytes_and_its_time_and_makes_no_pass()
    {
        var t = new Setup(3);
        t.Steps(5); // write 3 blocks (3 ms of the fake clock), verify 2 (2 ms)

        t.Stop.Request();
        Assert.False(t.Passes.Step());
        t.Passes.Settle();

        Assert.Equal(0, t.Passes.Passes);
        Assert.Equal(3 * Block, t.Passes.WriteBytes);
        Assert.Equal(2 * Block, t.Passes.VerifiedBytes);
        Assert.Equal(5 * Block, t.Passes.ProcessedBytes);
        Assert.Equal(0.003, t.Passes.WriteSeconds, 6);
        Assert.Equal(0.002, t.Passes.VerifySeconds, 6);
    }

    [Fact]
    public void A_write_that_was_cut_short_keeps_its_bytes_and_nothing_was_verified()
    {
        var t = new Setup(3);
        t.Steps(2);

        t.Stop.Request();
        t.Passes.Settle();

        Assert.Equal(2 * Block, t.Passes.WriteBytes);
        Assert.Equal(0, t.Passes.VerifiedBytes);
        Assert.Equal(0, t.Passes.Passes);
        Assert.Equal(0.002, t.Passes.WriteSeconds, 6);
        Assert.Equal(0, t.Passes.VerifySeconds);
    }

    [Fact]
    public void Settle_does_not_count_a_phase_twice_and_changes_nothing_after_a_whole_phase()
    {
        var t = new Setup(2);
        t.Steps(4); // one pass

        t.Passes.Settle();
        t.Passes.Settle();

        Assert.Equal(1, t.Passes.Passes);
        Assert.Equal(2 * Block, t.Passes.WriteBytes);
        Assert.Equal(2 * Block, t.Passes.VerifiedBytes);
    }

    [Fact]
    public void The_kind_and_the_seed_change_from_one_pass_to_the_next_and_the_kinds_come_round_again()
    {
        var t = new Setup(2);
        var kinds = Enum.GetValues<PatternKind>();

        for (var pass = 0; pass < 9; pass++)
        {
            var kind = kinds[pass % kinds.Length];
            var seed = 0x5EED_0000UL + (ulong)(pass + 1); // the fake session's seed of the pass that starts as number pass + 1

            t.Steps(2); // the write phase of this pass: what is in the blocks is this pass's pattern
            Assert.Equal(Patterns.ValueAt(kind, seed, 0), ((FakeMemorySession.FakeBlock)t.Blocks[0]).Cells[0]);
            Assert.Equal(Patterns.ValueAt(kind, seed, 8192 + 5), ((FakeMemorySession.FakeBlock)t.Blocks[1]).Cells[5]); // the second block carries on from the first

            t.Steps(2); // its verify phase
            Assert.Equal(pass + 1, t.Passes.Passes);
        }

        Assert.Equal(0, t.Passes.Mismatches);
        Assert.Equal(10, t.Session.Seeds.Count); // the first one, and one for every pass that ended
        Assert.Equal(10, t.Session.Seeds.Distinct().Count());
    }

    [Fact]
    public void A_block_is_cut_into_slices_and_the_last_one_is_the_short_one()
    {
        var t = new Setup(2, sliceWords: 1000);
        var accesses = new List<MemoryAccess>();
        t.Session.OnAccess = accesses.Add;

        t.Steps(18); // 9 slices a block, 2 blocks: the write phase

        Assert.Equal(2 * Block, t.Passes.WriteBytes);
        Assert.Equal(18, accesses.Count);
        Assert.All(accesses.Take(9), static a => Assert.Equal(0, a.BlockIndex));
        Assert.All(accesses.Skip(9), static a => Assert.Equal(1, a.BlockIndex));
        Assert.Equal([0, 1000, 2000, 3000, 4000, 5000, 6000, 7000, 8000], accesses.Take(9).Select(static a => a.FirstWord));
        Assert.Equal([1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 192], accesses.Take(9).Select(static a => a.Count));
        Assert.Equal(accesses.Take(9).Select(static a => a.Count), accesses.Skip(9).Select(static a => a.Count));
    }

    [Fact]
    public void A_slice_larger_than_a_block_is_a_block()
    {
        var t = new Setup(2, sliceWords: MemoryTest.DefaultSliceWords);
        var accesses = new List<MemoryAccess>();
        t.Session.OnAccess = accesses.Add;

        t.Steps(2);

        Assert.Equal([8192, 8192], accesses.Select(static a => a.Count));
        Assert.Equal(2 * Block, t.Passes.WriteBytes);
    }

    [Fact]
    public void The_stop_flag_ends_the_step_before_any_memory_is_touched()
    {
        var t = new Setup(2);
        t.Steps(1);
        var touched = t.Session.Accesses;

        t.Stop.Request();

        Assert.False(t.Passes.Step());
        Assert.Equal(touched, t.Session.Accesses);
    }

    [Fact]
    public void The_absolute_deadline_ends_the_step_and_sets_the_stop_flag_for_the_others()
    {
        var t = new Setup(2, duration: TimeSpan.FromSeconds(1));
        t.Steps(1);
        Assert.False(t.Stop.IsSet);
        var touched = t.Session.Accesses;

        t.Session.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(t.Passes.Step());
        Assert.True(t.Stop.IsSet);
        Assert.Equal(touched, t.Session.Accesses);
    }

    [Fact]
    public void Every_word_that_reads_back_wrong_is_one_mismatch_and_the_pass_still_counts()
    {
        var t = new Setup(3);
        t.Steps(3);
        ((FakeMemorySession.FakeBlock)t.Blocks[0]).Flip(0, 1);
        ((FakeMemorySession.FakeBlock)t.Blocks[1]).Flip(5, 0);
        ((FakeMemorySession.FakeBlock)t.Blocks[1]).Flip(5, 1); // two bits of one word: one place that reads back wrong
        ((FakeMemorySession.FakeBlock)t.Blocks[2]).Flip(8191, 63);

        t.Steps(3);

        Assert.Equal(3, t.Passes.Mismatches);
        Assert.Equal(1, t.Passes.Passes);
        Assert.Equal(3 * Block, t.Passes.VerifiedBytes);
    }

    [Fact]
    public void A_thread_that_finds_every_slice_taken_does_not_touch_memory_and_waits_for_the_phase_to_end()
    {
        // One block, one slice. While a thread is inside the only slice of the write phase, a second thread (here: the same thread, called from
        // inside the access) asks for work: there is none left to take, and it must not touch the memory, and it must not end.
        var t = new Setup(1);
        bool? nested = null;
        var accessesInside = -1;
        t.Session.OnAccess = a =>
        {
            if (a.Number == 1)
            {
                nested = t.Passes.Step();
                accessesInside = t.Session.Accesses;
            }
        };

        t.Steps(1);

        Assert.True(nested);
        Assert.Equal(1, accessesInside);
        Assert.Equal(Block, t.Passes.WriteBytes);
        t.Session.OnAccess = null;
        t.Steps(1); // and the phase that follows is the verify
        Assert.Equal(1, t.Passes.Passes);
        Assert.Equal(0, t.Passes.Mismatches);
    }

    // ---- real threads ----

    // A session of nothing but a number of seeds and a clock that stands still: only what MemoryPasses reads.
    private sealed class StillSession : IMemoryTestSession
    {
        private long _seeds;

        public long BlockBytes => Block;

        public int LogicalProcessorCount => 4;

        public ILoadThreadStarter Threads => throw new NotSupportedException();

        public ILoadClock Clock { get; } = new StillClock();

        public MemoryStatus? ReadStatus() => throw new NotSupportedException();

        public IMemoryBlock Allocate() => throw new NotSupportedException();

        public ulong NextSeed() => (ulong)Interlocked.Increment(ref _seeds) * 0x9E3779B97F4A7C15UL;

        public void Dispose()
        {
        }
    }

    private sealed class StillClock : ILoadClock
    {
        public long Timestamp => 0;

        public TimeSpan Elapsed(long since) => TimeSpan.Zero;

        public bool WaitCancelled(TimeSpan wait, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ArrayBlock : IMemoryBlock
    {
        private readonly ulong[] _cells = new ulong[Block / sizeof(ulong)];

        public Span<ulong> Words(int firstWord, int count) => _cells.AsSpan(firstWord, count);

        public void Free()
        {
        }
    }

    [Fact]
    public void Four_real_threads_on_one_set_of_blocks_never_see_a_false_mismatch_over_many_passes()
    {
        // The threads share the slices of a phase, and a phase may only change when every slice of it is done: a thread that verifies a slice
        // while another still writes the same range would read a mismatch that is not in the memory. Ended by a count of passes, not by a time
        // (the only clock here is the safety limit of a hung run).
        const int wanted = 300;
        var session = new StillSession();
        var blocks = Enumerable.Range(0, 6).Select(static _ => (IMemoryBlock)new ArrayBlock()).ToList();
        var stop = new MemoryStop();
        var passes = new MemoryPasses(blocks, session, stop, 0, TimeSpan.FromHours(1), sliceWords: 1000);
        Exception? fault = null;
        var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            try
            {
                while (passes.Step())
                {
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref fault, ex, null);
                stop.Request();
            }
        })
        { IsBackground = true }).ToList();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        var limit = DateTime.UtcNow.AddSeconds(60);
        while (passes.Passes < wanted && Volatile.Read(ref fault) is null && DateTime.UtcNow < limit)
        {
            Thread.Sleep(1);
        }

        stop.Request();
        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "a thread did not end after the stop");
        }

        passes.Settle();

        Assert.Null(fault);
        Assert.True(passes.Passes >= wanted, $"only {passes.Passes} passes in 60 s");
        Assert.Equal(0, passes.Mismatches);
        var oneRange = 6 * Block;
        Assert.InRange(passes.WriteBytes - (passes.Passes * oneRange), 0, oneRange);
        Assert.InRange(passes.VerifiedBytes - (passes.Passes * oneRange), 0, oneRange);
    }
}
