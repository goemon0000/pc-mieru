using PcMieru.Party.Hardware;

namespace PcMieru.Party;

/// <summary>
/// Where the window gets its numbers (DESIGN-C 8 step 4). The window polls <see cref="Latest"/> once a second, builds the slots with
/// <see cref="Game.PartyComposition"/> and ticks the Director. Step 4a has only <see cref="FakeBoardFeed"/>; the real one (PCMieru.exe's
/// snapshot over the Wire) comes with step 4b.
/// </summary>
internal interface IBoardFeed
{
    /// <summary>The newest frame and the time it was sampled; null until the first one has arrived.</summary>
    (BoardFrame Frame, DateTime SampledUtc)? Latest { get; }
}
