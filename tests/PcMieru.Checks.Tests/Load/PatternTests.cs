using PcMieru.Checks.Load;

namespace PcMieru.Checks.Tests.Load;

/// <summary>
/// The four patterns of the memory test (DESIGN-A §6.2): the port of the old tests, and the values pinned so that a change of a constant or of
/// the order of an operation is a failing test and not a memory test that quietly writes something else.
/// </summary>
public sealed class PatternTests
{
    // The kinds go through the theories as their numbers: the type is internal and a public test class cannot have it in its signature.
    public static TheoryData<int> AllKinds => new() { (int)PatternKind.Address, (int)PatternKind.AddressInverted, (int)PatternKind.Checker, (int)PatternKind.Random };

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Fill_then_verify_finds_nothing_for_every_kind(int number)
    {
        var kind = (PatternKind)number;
        var buffer = new ulong[10_000];

        Patterns.Fill(buffer, kind, 0xDEADBEEF, baseIndex: 1_000_000);
        var result = Patterns.Verify(buffer, kind, 0xDEADBEEF, baseIndex: 1_000_000);

        Assert.Equal(0, result.Mismatches);
        Assert.Empty(result.First);
    }

    [Theory]
    [InlineData((int)PatternKind.Address)]
    [InlineData((int)PatternKind.Random)]
    public void A_single_flipped_bit_is_found_with_its_place_and_the_bit(int number)
    {
        var kind = (PatternKind)number;
        var buffer = new ulong[4096];
        Patterns.Fill(buffer, kind, 42, baseIndex: 8192);
        buffer[1234] ^= 1UL << 17;

        var result = Patterns.Verify(buffer, kind, 42, baseIndex: 8192);

        Assert.Equal(1, result.Mismatches);
        var mismatch = Assert.Single(result.First);
        Assert.Equal(8192 + 1234, mismatch.Index);
        Assert.Equal(1, mismatch.FlippedBits);
        Assert.Equal(1UL << 17, mismatch.Expected ^ mismatch.Actual);
    }

    [Fact]
    public void Every_mismatch_is_counted_and_only_the_first_few_are_kept()
    {
        var buffer = new ulong[1000];
        Patterns.Fill(buffer, PatternKind.Checker, 0, baseIndex: 0);
        Array.Clear(buffer, 0, 100);

        var result = Patterns.Verify(buffer, PatternKind.Checker, 0, baseIndex: 0, keep: 8);

        Assert.Equal(100, result.Mismatches);
        Assert.Equal(8, result.First.Count);
        Assert.Equal(Enumerable.Range(0, 8).Select(static i => (long)i), result.First.Select(static m => m.Index));
    }

    [Fact]
    public void Keeping_none_still_counts_every_mismatch()
    {
        // The memory test keeps nothing that identifies a place; what it says about a mismatch is how many there were.
        var buffer = new ulong[1000];
        Patterns.Fill(buffer, PatternKind.Random, 9, baseIndex: 0);
        for (var i = 0; i < 1000; i += 10)
        {
            buffer[i] ^= 1;
        }

        var result = Patterns.Verify(buffer, PatternKind.Random, 9, baseIndex: 0, keep: 0);

        Assert.Equal(100, result.Mismatches);
        Assert.Empty(result.First);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void A_wrong_seed_is_found_everywhere(int number)
    {
        var kind = (PatternKind)number;
        var buffer = new ulong[512];
        Patterns.Fill(buffer, kind, 1, baseIndex: 0);

        // The Checker pattern depends on the seed by its lowest bit only: seeds 1 and 2 differ there; and so does every other kind everywhere.
        var result = Patterns.Verify(buffer, kind, 2, baseIndex: 0, keep: 0);

        Assert.Equal(buffer.Length, result.Mismatches);
    }

    [Fact]
    public void A_value_depends_on_the_index_and_the_seed_alone_not_on_how_the_work_was_split()
    {
        var whole = new ulong[100];
        var tail = new ulong[50];
        Patterns.Fill(whole, PatternKind.Random, 7, baseIndex: 0);
        Patterns.Fill(tail, PatternKind.Random, 7, baseIndex: 50);

        Assert.Equal(whole[50..], tail);
        Assert.Equal(whole[73], Patterns.ValueAt(PatternKind.Random, 7, 73));
    }

    [Fact]
    public void The_slices_of_one_range_fill_what_one_fill_of_the_range_does()
    {
        // Threads fill slices of a block, each with its own base index; the verify of the whole block must agree with them.
        var whole = new ulong[8192];
        var sliced = new ulong[8192];
        Patterns.Fill(whole, PatternKind.AddressInverted, 0xABCDEF, baseIndex: 16384);
        for (var first = 0; first < sliced.Length; first += 1000)
        {
            var count = Math.Min(1000, sliced.Length - first);
            Patterns.Fill(sliced.AsSpan(first, count), PatternKind.AddressInverted, 0xABCDEF, baseIndex: 16384 + first);
        }

        Assert.Equal(whole, sliced);
        Assert.Equal(0, Patterns.Verify(sliced, PatternKind.AddressInverted, 0xABCDEF, baseIndex: 16384).Mismatches);
    }

    [Fact]
    public void The_values_are_the_old_ones()
    {
        // Pinned (SplitMix64 reference values, computed outside the code): an edit that changes a constant breaks this and not the memory.
        Assert.Equal(0xE220A8397B1DCDAFUL, Patterns.ValueAt(PatternKind.Random, 0, 0));
        Assert.Equal(0x5E08003BE40567CFUL, Patterns.ValueAt(PatternKind.Random, 0x1234, 77));
        Assert.Equal(0x721D695BEB14A577UL, Patterns.ValueAt(PatternKind.Random, 0xDEADBEEF, 1_000_000));
        Assert.Equal(0xC695580F0B4DC2BFUL, Patterns.ValueAt(PatternKind.Random, 42, 8192 + 1234));

        Assert.Equal(5UL ^ 0xFFUL, Patterns.ValueAt(PatternKind.Address, 0xFF, 5));
        Assert.Equal(~(5UL ^ 0xFFUL), Patterns.ValueAt(PatternKind.AddressInverted, 0xFF, 5));

        Assert.Equal(0x5555555555555555UL, Patterns.ValueAt(PatternKind.Checker, 0, 0));
        Assert.Equal(0xAAAAAAAAAAAAAAAAUL, Patterns.ValueAt(PatternKind.Checker, 0, 1));
        Assert.Equal(0xAAAAAAAAAAAAAAAAUL, Patterns.ValueAt(PatternKind.Checker, 1, 0)); // an odd seed turns every bit
        Assert.Equal(0x5555555555555555UL, Patterns.ValueAt(PatternKind.Checker, 1, 1));
        Assert.Equal(0x5555555555555555UL, Patterns.ValueAt(PatternKind.Checker, 2, 0)); // an even seed does not
    }

    [Fact]
    public void The_kinds_are_the_four_of_the_old_test_in_the_old_order()
    {
        Assert.Equal([PatternKind.Address, PatternKind.AddressInverted, PatternKind.Checker, PatternKind.Random], Enum.GetValues<PatternKind>());
    }

    [Fact]
    public void A_mismatch_counts_the_bits_that_differ()
    {
        var mismatch = new Mismatch(5, 0b1010, 0b0110);

        Assert.Equal(2, mismatch.FlippedBits);
    }
}
