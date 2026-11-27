using System.Diagnostics;
using PcMieru.Party.Hardware;

namespace PcMieru.Party;

/// <summary>
/// One sample of the feed. <see cref="SampledUtc"/> is for people (a wall-clock time that a clock correction can move backwards);
/// <see cref="Stamp"/> is a <see cref="Stopwatch"/> tick count taken when the sample ARRIVED, and it is the only thing the window orders samples by,
/// takes dt from and judges staleness with (a backward clock correction must not make fresh samples look old, DESIGN-C 2.2).
/// </summary>
internal sealed record BoardSample(BoardFrame Frame, DateTime SampledUtc, long Stamp)
{
    /// <summary>Seconds on the monotonic clock between two stamps.</summary>
    public static double SecondsBetween(long earlier, long later) => (later - earlier) / (double)Stopwatch.Frequency;

    /// <summary>Seconds since this sample arrived.</summary>
    public double AgeSeconds => SecondsBetween(Stamp, Stopwatch.GetTimestamp());
}

/// <summary>
/// Where the window gets its numbers (DESIGN-C 8 step 4). The window polls <see cref="Latest"/> once a second, builds the slots with
/// <see cref="Game.PartyComposition"/> and ticks the Director. <see cref="FakeBoardFeed"/> is for --smoke; <see cref="SamplerFeed"/> is the real one.
/// </summary>
internal interface IBoardFeed
{
    /// <summary>The newest sample; null until the first one has arrived.</summary>
    BoardSample? Latest { get; }

    /// <summary>How often the feed delivers a sample right now: 1 s, or 2 s while the window is folded (DESIGN-C 2.2).</summary>
    TimeSpan Period { get; }

    /// <summary>The window tells the feed whether it is folded (the sampling slows down; it never stops).</summary>
    void SetFolded(bool folded);

    /// <summary>
    /// A sample older than this is "old": the window shows its values as old. <c>max(3 x period, 6 s)</c> on the monotonic clock.
    /// </summary>
    static TimeSpan StaleAfter(TimeSpan period) => period * 3 > TimeSpan.FromSeconds(6) ? period * 3 : TimeSpan.FromSeconds(6);
}
