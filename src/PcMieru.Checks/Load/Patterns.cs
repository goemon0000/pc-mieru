// Provenance: PartsLife.Checks.Load.Patterns (pl-cloud 25e4571, src/PartsLife.Checks/Load/Patterns.cs), MIT, same owner. The four patterns, their
// values, SplitMix and the fill and verify loops are the old ones; the types are internal and the comments are in English.
namespace PcMieru.Checks.Load;

/// <summary>The four patterns the memory test writes and reads back. They rotate from one pass to the next.</summary>
internal enum PatternKind
{
    /// <summary>The address itself (scrambled by the seed). Strong against a broken address line and against a write that lands in another place.</summary>
    Address,

    /// <summary>The inverse of the above: every bit sees both a 0 and a 1.</summary>
    AddressInverted,

    /// <summary>0101... and 1010... alternating. Strong against interference between neighbouring bits.</summary>
    Checker,

    /// <summary>A random number made from the address: it can be predicted at any address, so it does not depend on the order of the writes.</summary>
    Random,
}

/// <summary>One place where the memory read back differently from what was written.</summary>
internal sealed record Mismatch(long Index, ulong Expected, ulong Actual)
{
    /// <summary>The number of bits that differ. A single bit is the typical flip of a memory cell.</summary>
    public int FlippedBits => System.Numerics.BitOperations.PopCount(Expected ^ Actual);
}

internal sealed record VerifyResult(long Mismatches, IReadOnlyList<Mismatch> First);

/// <summary>
/// The writes and the checks of the memory test. Pure functions: every value is decided by the index (which 8-byte word it is) and the seed
/// alone, so it does not depend on the order of the writes or on how the work is shared between threads, and the check can make the expected
/// value again.
/// </summary>
internal static class Patterns
{
    public static ulong ValueAt(PatternKind kind, ulong seed, long index) => kind switch
    {
        PatternKind.Address => (ulong)index ^ seed,
        PatternKind.AddressInverted => ~((ulong)index ^ seed),
        PatternKind.Checker => ((index & 1) == 0 ? 0x5555555555555555UL : 0xAAAAAAAAAAAAAAAAUL) ^ ((seed & 1) == 0 ? 0 : ulong.MaxValue),
        _ => SplitMix((ulong)index + seed),
    };

    private static ulong SplitMix(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }

    /// <summary><paramref name="baseIndex"/> is which word of the whole the first word of the span is.</summary>
    public static void Fill(Span<ulong> span, PatternKind kind, ulong seed, long baseIndex)
    {
        switch (kind)
        {
            case PatternKind.Address:
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = (ulong)(baseIndex + i) ^ seed;
                }

                break;
            case PatternKind.AddressInverted:
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = ~((ulong)(baseIndex + i) ^ seed);
                }

                break;
            default:
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = ValueAt(kind, seed, baseIndex + i);
                }

                break;
        }
    }

    /// <summary>The check. Every mismatch is counted; only the first <paramref name="keep"/> are remembered.</summary>
    public static VerifyResult Verify(ReadOnlySpan<ulong> span, PatternKind kind, ulong seed, long baseIndex, int keep = 8)
    {
        long bad = 0;
        List<Mismatch>? first = null;
        for (var i = 0; i < span.Length; i++)
        {
            var want = kind switch
            {
                PatternKind.Address => (ulong)(baseIndex + i) ^ seed,
                PatternKind.AddressInverted => ~((ulong)(baseIndex + i) ^ seed),
                _ => ValueAt(kind, seed, baseIndex + i),
            };
            var got = span[i];
            if (got != want)
            {
                bad++;
                if ((first ??= []).Count < keep)
                {
                    first.Add(new Mismatch(baseIndex + i, want, got));
                }
            }
        }

        return new VerifyResult(bad, (IReadOnlyList<Mismatch>?)first ?? []);
    }
}
