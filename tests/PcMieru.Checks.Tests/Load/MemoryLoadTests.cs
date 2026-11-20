using PcMieru.Checks.Load;

namespace PcMieru.Checks.Tests.Load;

/// <summary>
/// The memory load (DESIGN-A §6.2) in a fake world: blocks are managed arrays, the clock is moved by hand, the work threads run when the control
/// loop waits, and the free memory is a number the test scripts. Nothing real is borrowed and nothing is timed: every stop is "at access number
/// N" or "at wait number N", never "after so many milliseconds".
/// <para>
/// The arithmetic of the world: <c>Planned(4)</c> borrows four blocks of 64 KiB and starts four threads (eight logical processors). In every wait
/// of 0.25 s each thread does three slices, so twelve slices are done, one slice is one block, and a pass (a write phase and a verify phase of
/// four slices each) takes eight of them. A run of 5 s has twenty waits, 240 slices and 30 passes. The accesses to the blocks are numbered one
/// after the other (thread 0's steps first): 1 to 4 write the blocks, 5 to 8 verify them, 9 to 12 write them again.
/// </para>
/// </summary>
public sealed class MemoryLoadTests
{
    private const long Block = FakeMemorySession.SmallBlock;

    private static readonly TimeSpan Five = TimeSpan.FromSeconds(5);

    private static MemoryLoadResult Go(FakeMemorySession session, CancellationToken ct = default, Action<double, double?>? onTick = null, TimeSpan? duration = null) =>
        MemoryTest.Run(session, duration ?? Five, onTick, ct);

    private static CheckRunFailedException Fails(FakeMemorySession session, CancellationToken ct = default) =>
        Assert.Throws<CheckRunFailedException>(() => { _ = Go(session, ct); });

    private static List<string> Kinds(FakeMemorySession session) => [.. session.Events.Select(static e => e.Kind)];

    // ---- a healthy run ----

    [Fact]
    public void A_healthy_run_borrows_the_plan_works_the_whole_time_and_finds_nothing()
    {
        var s = FakeMemorySession.Planned(4);

        var r = Go(s);

        Assert.True(r.StatusKnown);
        Assert.Equal(8L << 30, r.TotalBytes);
        Assert.Equal((1L << 30) + (4 * Block) + (Block / 2), r.AvailableAtStartBytes);
        Assert.Equal(Block, r.BlockBytes);
        Assert.Equal(4, r.Blocks);
        Assert.Equal(4 * Block, r.BorrowedBytes);
        Assert.Equal(4, r.Workers);
        Assert.False(r.Stopped);
        Assert.False(r.Pressure);
        Assert.Equal(20, s.Waits);
        Assert.Equal(30, r.Passes);
        Assert.Equal(30 * 4 * Block, r.WriteBytes);
        Assert.Equal(30 * 4 * Block, r.VerifiedBytes);
        Assert.Equal(0, r.Mismatches);
        Assert.Equal(5.012, r.Seconds, 3);
        Assert.True(r.WriteRate > 0);
        Assert.True(r.VerifyRate > 0);
        Assert.Equal(r.Passes + 1, s.Seeds.Count); // the first pass's seed, and one for every pass that ended
    }

    [Fact]
    public void The_rates_are_the_mib_per_second_over_the_time_in_the_phases()
    {
        var s = FakeMemorySession.Planned(4);

        var r = Go(s);

        // The 1 ms of a slice: a phase of four slices is 4 ms but the phases that straddle a wait include the wait; the rate is bytes over that time.
        Assert.Equal(r.WriteBytes / 1048576.0 / r.WriteSeconds, r.WriteRate!.Value, 9);
        Assert.Equal(r.VerifiedBytes / 1048576.0 / r.VerifySeconds, r.VerifyRate!.Value, 9);
        Assert.True(r.WriteSeconds > 0 && r.WriteSeconds < r.Seconds);
    }

    [Fact]
    public void The_blocks_are_borrowed_one_by_one_every_thread_is_joined_and_only_then_the_blocks_are_freed_once_each()
    {
        var s = FakeMemorySession.Planned(4);

        _ = Go(s);

        Assert.Equal(
            ["alloc", "alloc", "alloc", "alloc", "join", "join", "join", "join", "free", "free", "free", "free"],
            Kinds(s));
        MemoryEvents.AssertJoinedThenFreed(s);
        Assert.All(s.WorkThreads, static t =>
        {
            Assert.Equal(1, t.Joins);
            Assert.Equal(0, t.StepsDuringJoin); // the stop flag was set before the join: no slice was done after it
        });
    }

    [Fact]
    public void The_work_threads_are_named()
    {
        var s = FakeMemorySession.Planned(4);

        _ = Go(s);

        Assert.Equal(["PcMieru.MemoryLoad.0", "PcMieru.MemoryLoad.1", "PcMieru.MemoryLoad.2", "PcMieru.MemoryLoad.3"], s.WorkThreads.Select(static t => t.Name));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 2)]
    [InlineData(16, 8)]
    [InlineData(64, 32)]
    public void The_work_threads_are_half_of_the_logical_processors_and_never_fewer_than_one(int logical, int workers)
    {
        var s = FakeMemorySession.Planned(4, logicalProcessors: logical);

        var r = Go(s);

        Assert.Equal(workers, r.Workers);
        Assert.Equal(workers, s.WorkThreads.Count);
        Assert.False(r.Stopped);
        Assert.Equal(0, r.Mismatches);
        Assert.True(r.Passes > 0);
        MemoryEvents.AssertJoinedThenFreed(s);
    }

    [Fact]
    public void The_free_memory_is_read_in_one_place_every_quarter_of_a_second()
    {
        var s = FakeMemorySession.Planned(4);
        var stamps = new List<long>();
        s.AvailableAt = _ =>
        {
            stamps.Add(s.Time.GetTimestamp());
            return null;
        };

        _ = Go(s);

        // One reading at the start, one before each of the four blocks, then one for every reading of the loop but the last (the time is up).
        Assert.Equal(1 + 4 + 19, s.StatusReads);
        Assert.Equal(s.StatusReads, stamps.Count);
        var loopReads = stamps.Skip(5).ToList();
        Assert.Equal(19, loopReads.Count);
        for (var i = 1; i < loopReads.Count; i++)
        {
            Assert.Equal(0.25, s.Time.GetElapsedTime(loopReads[i - 1], loopReads[i]).TotalSeconds, 6);
        }
    }

    [Fact]
    public void The_progress_is_reported_once_a_second_with_the_mib_per_second_of_the_last_second()
    {
        var s = FakeMemorySession.Planned(4);
        var ticks = new List<(double Seconds, double? Rate)>();

        _ = Go(s, onTick: (seconds, rate) => ticks.Add((seconds, rate)));

        // Every fourth reading, but not the last one (it is the end): at 1, 2, 3 and 4 s. 48 slices of 64 KiB (3 MiB) in every second.
        Assert.Equal(4, ticks.Count);
        Assert.Equal([1.012, 2.012, 3.012, 4.012], ticks.Select(static t => Math.Round(t.Seconds, 3)));
        Assert.Equal(3.0 / 1.012, ticks[0].Rate!.Value, 6);
        Assert.All(ticks.Skip(1), static t => Assert.Equal(3.0, t.Rate!.Value, 6));
    }

    [Fact]
    public void The_late_readings_that_come_at_once_after_a_long_allocation_do_not_make_a_progress_rate_out_of_a_tiny_window()
    {
        // The loop starts at 1.6 s (four blocks of 0.4 s), so its first six readings are late and come at once. The reading of the progress that
        // is one of them has no time behind it: it is skipped, and the first one that goes out covers at least the shortest window.
        var s = FakeMemorySession.Planned(4);
        s.AllocateCost = TimeSpan.FromMilliseconds(400);
        var ticks = new List<(double Seconds, double? Rate)>();

        _ = Go(s, onTick: (seconds, rate) => ticks.Add((seconds, rate)));

        var window = MemoryTest.MinReportWindow.TotalSeconds;
        Assert.NotEmpty(ticks);
        Assert.All(ticks, static t => Assert.True(t.Rate > 0, "a progress rate that is missing or not positive"));
        Assert.True(ticks[0].Seconds - 1.6 >= window, $"the first progress reading covers {ticks[0].Seconds - 1.6} s");
        for (var i = 1; i < ticks.Count; i++)
        {
            Assert.True(ticks[i].Seconds - ticks[i - 1].Seconds >= window, $"readings {i - 1} and {i} are {ticks[i].Seconds - ticks[i - 1].Seconds} s apart");
        }
    }

    // ---- a bit that changes ----

    [Fact]
    public void One_bit_that_flips_between_the_write_and_the_verify_is_exactly_one_mismatch()
    {
        var s = FakeMemorySession.Planned(4);
        s.OnAccess = a =>
        {
            if (a.Number == 5)
            {
                s.Blocks[0].Flip(10, 3); // after block 0 was written, before it is read back
            }
        };

        var r = Go(s);

        Assert.Equal(1, r.Mismatches);
        Assert.False(r.Stopped);
        Assert.Equal(30, r.Passes); // a mismatch does not end the run
        Assert.Equal(30 * 4 * Block, r.VerifiedBytes);
    }

    [Theory]
    [InlineData(1, 1)] // one word, one bit
    [InlineData(2, 1)] // two bits of one word: one place that reads back wrong
    public void A_place_that_reads_back_wrong_is_counted_once_however_many_of_its_bits_are_wrong(int bits, int expected)
    {
        var s = FakeMemorySession.Planned(4);
        s.OnAccess = a =>
        {
            if (a.Number == 6)
            {
                for (var bit = 0; bit < bits; bit++)
                {
                    s.Blocks[1].Flip(77, 40 + bit);
                }
            }
        };

        var r = Go(s);

        Assert.Equal(expected, r.Mismatches);
    }

    [Fact]
    public void Bits_that_flip_in_different_words_and_different_passes_are_all_counted_with_no_cap()
    {
        var s = FakeMemorySession.Planned(4);
        var flips = 0;
        s.OnAccess = a =>
        {
            // The first verify access of each of the first twenty passes (access 5, 13, 21 ...): one flip in block 0.
            if (a.Number % 8 == 5 && a.Number <= 20 * 8)
            {
                s.Blocks[0].Flip(flips++, 5);
            }
        };

        var r = Go(s);

        Assert.Equal(20, flips);
        Assert.Equal(20, r.Mismatches);
    }

    [Fact]
    public void A_stop_in_the_middle_of_the_verify_after_a_mismatch_is_stopped_and_keeps_the_mismatch()
    {
        using var cts = new CancellationTokenSource();
        var s = FakeMemorySession.Planned(4);
        s.OnAccess = a =>
        {
            if (a.Number == 5)
            {
                s.Blocks[0].Flip(10, 3);
            }

            if (a.Number == 6)
            {
                cts.Cancel(); // while block 1 is being verified: that slice is finished, the next is not begun
            }
        };

        var r = Go(s, cts.Token);

        Assert.True(r.Stopped);
        Assert.False(r.Pressure);
        Assert.Equal(1, r.Mismatches);
        Assert.Equal(0, r.Passes);
        Assert.Equal(4 * Block, r.WriteBytes);
        Assert.Equal(2 * Block, r.VerifiedBytes); // the two blocks that were read back
        Assert.Equal(6, s.Accesses);
        MemoryEvents.AssertJoinedThenFreed(s);
    }

    [Fact]
    public void A_stop_in_the_middle_of_the_verify_with_nothing_found_is_stopped_with_no_mismatch()
    {
        using var cts = new CancellationTokenSource();
        var s = FakeMemorySession.Planned(4);
        s.OnAccess = a =>
        {
            if (a.Number == 6)
            {
                cts.Cancel();
            }
        };

        var r = Go(s, cts.Token);

        Assert.True(r.Stopped);
        Assert.Equal(0, r.Mismatches);
        Assert.Equal(2 * Block, r.VerifiedBytes);
        Assert.Equal(0, r.Passes);
        MemoryEvents.AssertJoinedThenFreed(s);
    }

    [Fact]
    public void A_stop_before_anything_was_read_back_has_no_verified_byte_and_no_verify_rate()
    {
        using var cts = new CancellationTokenSource();
        var s = FakeMemorySession.Planned(4);
        s.OnAccess = a =>
        {
            if (a.Number == 2)
            {
                cts.Cancel();
            }
        };

        var r = Go(s, cts.Token);

        Assert.True(r.Stopped);
        Assert.Equal(2 * Block, r.WriteBytes);
        Assert.Equal(0, r.VerifiedBytes);
        Assert.Equal(0, r.Mismatches);
        Assert.NotNull(r.WriteRate);
        Assert.Null(r.VerifyRate); // no bytes: missing, never 0
        MemoryEvents.AssertJoinedThenFreed(s);
    }

    [Fact]
    public void A_stop_at_a_wait_ends_the_run_with_what_was_done_before_it()
    {
        using var cts = new CancellationTokenSource();
        var s = FakeMemorySession.Planned(4);
        s.OnWait = n =>
        {
            if (n == 4)
            {
                cts.Cancel();
            }
        };

        var r = Go(s, cts.Token);

        Assert.True(r.Stopped);
        Assert.Equal(4, s.Waits);
        Assert.Equal(4, r.Passes); // three waits of twelve slices: 36 slices, four whole passes and four slices of the fifth
        Assert.Equal(0, r.Mismatches);
        Assert.Equal(0.762, r.Seconds, 3);
        MemoryEvents.AssertJoinedThenFreed(s);
    }

    // ---- the free memory ----

    [Fact]
    public void A_fall_of_the_free_memory_under_half_the_floor_stops_the_run_with_pressure()
    {
        var s = FakeMemorySession.Planned(4);
        s.AvailableAt = n => n == 8 ? s.Floor / 2 - 1 : null; // reading 8 is the third of the loop (1 at the start, 4 for the blocks)

        var r = Go(s);

        Assert.True(r.Stopped);
        Assert.True(r.Pressure);
        Assert.Equal(4, r.Passes); // three waits were done before the reading
        Assert.Equal(3, s.Waits);
        Assert.Equal(0.762, r.Seconds, 3);
        MemoryEvents.AssertJoinedThenFreed(s);
    }

    [Fact]
    public void Exactly_half_the_floor_is_not_pressure()
    {
        var s = FakeMemorySession.Planned(4);
        s.AvailableAt = n => n == 8 ? s.Floor / 2 : null;

        var r = Go(s);

        Assert.False(r.Stopped);
        Assert.False(r.Pressure);
        Assert.Equal(20, s.Waits);
    }

    [Fact]
    public void A_free_memory_that_cannot_be_read_during_the_run_is_taken_as_pressure()
    {
        var s = FakeMemorySession.Planned(4);
        s.StatusUnreadable = static n => n == 8;

        var r = Go(s);

        Assert.True(r.Stopped);
        Assert.True(r.Pressure);
        MemoryEvents.AssertJoinedThenFreed(s);
    }

    [Fact]
    public void Growth_stops_before_the_block_that_would_take_the_machine_under_the_floor()
    {
        var s = FakeMemorySession.Planned(4);
        // Before block 3 (reading 4) the machine says it has one byte less than it takes to leave the floor free after that block.
        s.AvailableAt = n => n == 4 ? s.Floor + Block - 1 : null;

        var r = Go(s);

        Assert.Equal(2, r.Blocks);
        Assert.Equal(2, s.AllocateCalls);
        Assert.False(r.Stopped);
        Assert.False(r.Pressure); // some memory was borrowed: the run is whole, only smaller
        Assert.True(r.Passes > 0);
    }

    [Fact]
    public void The_block_that_just_fits_above_the_floor_is_borrowed()
    {
        var s = FakeMemorySession.Planned(4);
        s.AvailableAt = n => n == 4 ? s.Floor + Block : null; // after the block exactly the floor is left

        var r = Go(s);

        Assert.Equal(4, r.Blocks);
    }

    [Fact]
    public void The_allocator_that_says_out_of_memory_ends_the_growth_and_the_run_goes_on_with_what_it_has()
    {
        var s = FakeMemorySession.Planned(4);
        s.AllocateFails = static n => n == 3;

        var r = Go(s);

        Assert.Equal(2, r.Blocks);
        Assert.Equal(3, s.AllocateCalls); // not asked again after the refusal
        Assert.False(r.Stopped);
        Assert.False(r.Pressure);
        Assert.Equal(4, r.Workers);
        Assert.Equal(60, r.Passes); // two blocks: four slices to a pass, twelve a wait, twenty waits
        Assert.Equal(0, r.Mismatches);
        MemoryEvents.AssertJoinedThenFreed(s);
    }

    [Fact]
    public void No_block_at_all_when_the_plan_is_under_one_block_is_a_completed_run_with_pressure()
    {
        // Half a block above the floor: the plan is nothing.
        var s = new FakeMemorySession(8 * FakeMemorySession.Gib, FakeMemorySession.Gib + (ulong)(Block / 2));

        var r = Go(s);

        Assert.True(r.StatusKnown);
        Assert.Equal(0, r.Blocks);
        Assert.Equal(0, r.BorrowedBytes);
        Assert.Equal(0, r.Workers);
        Assert.False(r.Stopped);
        Assert.True(r.Pressure);
        Assert.Equal(0, r.Passes);
        Assert.Equal(0, r.WriteBytes);
        Assert.Equal(0, r.VerifiedBytes);
        Assert.Null(r.WriteRate);
        Assert.Null(r.VerifyRate);
        Assert.Equal(0, s.AllocateCalls);
        Assert.Empty(s.WorkThreads);
    }

    [Fact]
    public void No_block_at_all_when_the_floor_is_reached_on_the_first_block_is_the_same()
    {
        var s = FakeMemorySession.Planned(3);
        s.AvailableAt = n => n == 2 ? s.Floor + Block - 1 : null; // the machine lost memory since the start: not one block fits

        var r = Go(s);

        Assert.Equal(0, r.Blocks);
        Assert.False(r.Stopped);
        Assert.True(r.Pressure);
        Assert.Equal(0, s.AllocateCalls);
        Assert.Empty(s.WorkThreads);
    }

    [Fact]
    public void Windows_that_cannot_say_how_much_memory_there_is_at_the_start_means_no_plan_and_no_pressure()
    {
        var s = FakeMemorySession.Planned(4);
        s.StatusUnreadable = static n => n == 1;

        var r = Go(s);

        Assert.False(r.StatusKnown);
        Assert.Equal(0, r.Blocks);
        Assert.False(r.Stopped);
        Assert.False(r.Pressure);
        Assert.Equal(1, s.StatusReads);
        Assert.Equal(0, s.AllocateCalls);
    }

    // ---- the time ----

    [Fact]
    public void The_deadline_counts_from_the_start_and_the_allocation_is_in_it()
    {
        // 0.4 s for every block: the four blocks take 1.6 s of the 5, and the loop's first six readings (due at 0.25 to 1.5 s) are already late.
        var s = FakeMemorySession.Planned(4);
        s.AllocateCost = TimeSpan.FromMilliseconds(400);

        var r = Go(s);

        Assert.Equal(4, r.Blocks);
        Assert.Equal(14, s.Waits); // the readings from 1.75 s to 5 s
        Assert.Equal(21, r.Passes);
        Assert.InRange(r.Seconds, 5.0, 5.1);
        Assert.False(r.Stopped);
    }

    [Fact]
    public void An_allocation_that_takes_the_whole_time_ends_the_run_at_the_deadline_with_no_work_and_no_pressure()
    {
        var s = FakeMemorySession.Planned(10);
        s.AllocateCost = TimeSpan.FromSeconds(3);

        var r = Go(s);

        Assert.Equal(2, r.Blocks); // 0 s: yes, 3 s: yes, 6 s: the time is up
        Assert.Equal(0, r.Workers);
        Assert.Equal(0, r.Passes);
        Assert.False(r.Stopped);
        Assert.False(r.Pressure); // blocks were borrowed, and the time ran out in the allocation: nothing the machine needed
        Assert.Equal(6.0, r.Seconds, 3);
        Assert.Equal(["alloc", "alloc", "free", "free"], Kinds(s));
        MemoryEvents.AssertJoinedThenFreed(s);
    }

    [Fact]
    public void An_allocation_of_one_second_a_block_gets_five_blocks_in_five_seconds_and_no_more()
    {
        var s = FakeMemorySession.Planned(10);
        s.AllocateCost = TimeSpan.FromSeconds(1);

        var r = Go(s);

        Assert.Equal(5, r.Blocks);
        Assert.Equal(5.0, r.Seconds, 3);
        Assert.Equal(0, r.Workers);
    }

    [Fact]
    public void A_clock_that_oversleeps_does_not_push_the_end_later_than_one_oversleep()
    {
        // Every wait sleeps 0.4 s too long. The readings stay on their schedule (the next wait is the shorter), so the run ends within one
        // oversleep of the 5 s, with fewer readings than the twenty of a clock that does not.
        var s = FakeMemorySession.Planned(4);
        s.Oversleep = TimeSpan.FromMilliseconds(400);

        var r = Go(s);

        Assert.False(r.Stopped);
        Assert.InRange(r.Seconds, 5.0, 5.5);
        Assert.InRange(s.Waits, 1, 19);
        Assert.Equal(0, r.Mismatches);
    }

    [Fact]
    public void A_stop_during_the_allocation_ends_it_at_once_and_starts_no_thread()
    {
        using var cts = new CancellationTokenSource();
        var s = FakeMemorySession.Planned(10);
        s.OnAllocate = n =>
        {
            if (n == 3)
            {
                cts.Cancel();
            }
        };

        var r = Go(s, cts.Token);

        Assert.True(r.Stopped);
        Assert.Equal(3, r.Blocks); // the third was being allocated; the fourth was not asked for
        Assert.Equal(3, s.AllocateCalls);
        Assert.Equal(0, r.Workers);
        Assert.Equal(["alloc", "alloc", "alloc", "free", "free", "free"], Kinds(s));
        MemoryEvents.AssertJoinedThenFreed(s);
    }

    [Fact]
    public void A_stop_that_was_already_there_borrows_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var s = FakeMemorySession.Planned(4);

        var r = Go(s, cts.Token);

        Assert.True(r.StatusKnown);
        Assert.True(r.Stopped);
        Assert.False(r.Pressure);
        Assert.Equal(0, r.Blocks);
        Assert.Equal(0, s.AllocateCalls);
        Assert.Empty(s.Events);
    }

    // ---- a fault ----

    [Fact]
    public void A_work_thread_that_throws_fails_the_run_after_every_thread_was_joined_and_every_block_freed()
    {
        var s = FakeMemorySession.Planned(4);
        s.AccessFault = static n => n == 7 ? new InvalidOperationException("secret detail at 0x7ffe00001000") : null;

        var failure = Fails(s);

        Assert.Equal(CheckRunFailureCodes.InternalError, failure.Code);
        Assert.Contains("InvalidOperationException", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal); // the type, never the text of the thing that threw
        MemoryEvents.AssertJoinedThenFreed(s);
        Assert.Equal(4, s.WorkThreads.Count);
    }

    [Fact]
    public void An_out_of_memory_in_a_work_thread_is_a_failure_of_the_run_and_not_an_exception_that_leaves_the_blocks_held()
    {
        var s = FakeMemorySession.Planned(4);
        s.AccessFault = static n => n == 9 ? new OutOfMemoryException() : null;

        var failure = Fails(s);

        Assert.Equal(CheckRunFailureCodes.InternalError, failure.Code);
        Assert.Contains("OutOfMemoryException", failure.Message, StringComparison.Ordinal);
        MemoryEvents.AssertJoinedThenFreed(s);
    }

    [Fact]
    public void A_work_thread_that_throws_stops_the_others_at_their_next_slice()
    {
        var s = FakeMemorySession.Planned(4);
        s.AccessFault = static n => n == 7 ? new InvalidOperationException() : null;

        _ = Assert.Throws<CheckRunFailedException>(() => { _ = Go(s); });

        // Accesses 1 to 6 were done by threads 0 and 1; thread 2 threw at the seventh; thread 3 did nothing after that.
        Assert.Equal(7, s.Accesses);
    }

    [Fact]
    public void A_block_that_cannot_be_freed_fails_the_run_and_the_others_are_still_freed()
    {
        var s = FakeMemorySession.Planned(4);
        s.FreeFails = static i => i == 1;

        var failure = Fails(s);

        Assert.Equal(CheckRunFailureCodes.InternalError, failure.Code);
        MemoryEvents.AssertJoinedThenFreed(s); // every block, the failing one too, was asked once
        Assert.Equal(["free", "free", "free", "free"], s.Events.Where(static e => e.Kind == "free").Select(static e => e.Kind));
        Assert.Equal([0, 1, 2, 3], s.Events.Where(static e => e.Kind == "free").Select(static e => e.Index));
    }

    [Fact]
    public void A_fault_during_the_allocation_that_is_not_an_out_of_memory_leaves_the_run_to_fail_and_frees_what_was_borrowed()
    {
        var s = FakeMemorySession.Planned(4);
        s.OnAllocate = static n =>
        {
            if (n == 3)
            {
                throw new InvalidOperationException("not an out of memory");
            }
        };

        _ = Assert.Throws<InvalidOperationException>(() => { _ = Go(s); }); // not caught by the allocation's out of memory rule

        Assert.Equal(2, s.Blocks.Count);
        Assert.All(s.Blocks, static b => Assert.Equal(1, b.FreeCalls)); // freed in the finally
        Assert.Empty(s.WorkThreads);
    }
}
