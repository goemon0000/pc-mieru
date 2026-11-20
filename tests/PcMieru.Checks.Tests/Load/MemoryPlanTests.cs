using PcMieru.Checks.Load;

namespace PcMieru.Checks.Tests.Load;

/// <summary>How much memory a run borrows (DESIGN-A §6.2): a pure function of the free and the total memory. The port of the old plan tests.</summary>
public sealed class MemoryPlanTests
{
    private const long Gib = 1L << 30;
    private const long Block = 64L << 20;

    [Fact]
    public void The_default_is_a_quarter_of_the_free_memory()
    {
        Assert.Equal(0.25, MemoryTest.DefaultFraction);
        Assert.Equal(10 * Gib, MemoryTest.PlanBytes(40 * Gib, 64 * Gib, MemoryTest.DefaultFraction, null));
    }

    [Fact]
    public void The_fraction_is_never_more_than_a_half()
    {
        Assert.Equal(0.5, MemoryTest.MaxFraction);
        Assert.Equal(20 * Gib, MemoryTest.PlanBytes(40 * Gib, 64 * Gib, 0.9, null));
        Assert.Equal(20 * Gib, MemoryTest.PlanBytes(40 * Gib, 64 * Gib, 5, null));
    }

    [Fact]
    public void A_fraction_under_zero_borrows_nothing()
    {
        Assert.Equal(0, MemoryTest.PlanBytes(40 * Gib, 64 * Gib, -1, null));
        Assert.Equal(0, MemoryTest.PlanBytes(40 * Gib, 64 * Gib, 0, null));
    }

    [Fact]
    public void An_explicit_size_is_held_to_the_same_cap()
    {
        Assert.Equal(20 * Gib, MemoryTest.PlanBytes(40 * Gib, 64 * Gib, 0.25, 30 * Gib));
        Assert.Equal(2 * Gib, MemoryTest.PlanBytes(40 * Gib, 64 * Gib, 0.25, 2 * Gib));
    }

    [Fact]
    public void The_free_memory_that_stays_is_never_under_the_floor()
    {
        var plan = MemoryTest.PlanBytes(8 * Gib, 64 * Gib, 0.5, null);

        Assert.True(8 * Gib - plan >= MemoryTest.FloorBytes(64 * Gib), "the plan went into the floor");
        Assert.True(plan > 0);
    }

    [Fact]
    public void A_machine_that_has_no_more_than_the_floor_free_borrows_nothing()
    {
        Assert.Equal(0, MemoryTest.PlanBytes(1 * Gib, 16 * Gib, 0.25, null)); // the floor of 16 GiB is 1.6 GiB
        Assert.Equal(0, MemoryTest.PlanBytes(1 * Gib, 8 * Gib, 0.25, null)); // exactly the floor of 8 GiB: nothing above it
        Assert.Equal(0, MemoryTest.PlanBytes(0, 8 * Gib, 0.25, null));
    }

    [Fact]
    public void The_floor_is_one_gib_or_a_tenth_of_the_machine_whichever_is_more()
    {
        Assert.Equal(1 * Gib, MemoryTest.FloorBytes(8 * Gib));
        Assert.Equal(1 * Gib, MemoryTest.FloorBytes(10 * Gib));
        Assert.Equal(64 * Gib / 10, MemoryTest.FloorBytes(64 * Gib));
    }

    [Theory]
    [InlineData(33L * Gib + 12345)]
    [InlineData(40L * Gib + 1)]
    [InlineData(9L * Gib + 63L * (1 << 20))]
    public void The_plan_is_whole_blocks_of_64_mib_rounded_down(long available)
    {
        var plan = MemoryTest.PlanBytes(available, 64 * Gib, 0.25, null);

        Assert.True(plan > 0);
        Assert.Equal(0, plan % Block);
        Assert.True(plan <= available * 0.25);
        Assert.True(available * 0.25 - plan < Block); // rounded down: not more than a block short of the share
    }

    [Fact]
    public void A_share_that_is_under_one_block_is_nothing()
    {
        // One byte short of a block above the floor of an 8 GiB machine: the floor leaves less than a block to borrow.
        var plan = MemoryTest.PlanBytes(1 * Gib + Block - 1, 8 * Gib, 0.25, null);

        Assert.Equal(0, plan);
    }

    [Fact]
    public void The_block_size_of_the_real_session_is_64_mib_and_a_slice_is_4_mib()
    {
        Assert.Equal(Block, RealMemoryTestSession.RealBlockBytes);
        Assert.Equal(4 << 20, MemoryTest.DefaultSliceWords * sizeof(ulong));
        Assert.Equal(TimeSpan.FromMilliseconds(250), MemoryTest.ReadInterval);
    }

    [Fact]
    public void A_smaller_block_gives_a_finer_plan()
    {
        // The test seam: 64 KiB blocks. Free memory of the floor + 5.5 blocks of 64 KiB, on an 8 GiB machine: five blocks.
        var plan = MemoryTest.PlanBytes(Gib + 5 * (64 << 10) + (32 << 10), 8 * Gib, 0.25, null, 64 << 10);

        Assert.Equal(5 * (64 << 10), plan);
    }
}
