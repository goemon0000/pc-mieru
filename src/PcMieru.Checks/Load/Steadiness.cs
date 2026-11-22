// Provenance: PartsLife.Checks.Load.Steadiness (pl-cloud 25e4571, src/PartsLife.Checks/Load/Steadiness.cs), MIT, same owner. The computation and
// the verdict boundaries are the old ones, unchanged: drop the first 2, need 6, first-third against last-third, < 70, < 90 or cv > 0.10.
namespace PcMieru.Checks.Load;

/// <summary>
/// Steadiness: the ratio of the average of the end of a run to the average of its start, and the spread of the whole run.
/// </summary>
/// <param name="Score">End divided by start times 100, capped at 100. Lower means the pace was pulled down.</param>
/// <param name="Cv">The standard deviation divided by the mean (a ratio: 0.10 is 10 %). <c>cpu.variation</c> and the evidence <c>cv</c> are this times 100 (DESIGN-A 14.0).</param>
internal sealed record SteadinessResult(double Score, double Cv, double FirstAverage, double LastAverage, int Samples);

internal static class Steadiness
{
    /// <summary>The samples of the warm-up that are dropped.</summary>
    public const int DefaultSkip = 2;

    /// <summary>Fewer samples than this after the skip cannot be judged: saying "steady" about a run that short would be a lie.</summary>
    public const int MinimumSamples = 6;

    /// <summary>The caution line of the clock's steadiness (DESIGN-A 14.2).</summary>
    public const double ClockCautionBelow = 85;

    /// <summary>
    /// The steadiness of a per-second series. Pure. The first <paramref name="skip"/> samples are dropped (the ramp-up); with fewer than
    /// <see cref="MinimumSamples"/> left the result is null.
    /// </summary>
    public static SteadinessResult? Compute(IReadOnlyList<double> series, int skip = DefaultSkip)
    {
        ArgumentNullException.ThrowIfNull(series);
        var v = series.Skip(skip).ToArray();
        if (v.Length < MinimumSamples)
        {
            return null;
        }

        var third = Math.Max(2, v.Length / 3);
        var first = v.Take(third).Average();
        var last = v.Skip(v.Length - third).Average();
        var mean = v.Average();
        var sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Length);
        var score = first <= 0 ? 0 : Math.Clamp(last / first, 0, 1) * 100;
        return new SteadinessResult(score, mean <= 0 ? 0 : sd / mean, first, last, v.Length);
    }

    /// <summary>The one verdict of the CPU's steadiness (DESIGN-A 14.2): below 70 is check; below 90 or a cv over 0.10 is caution; else ok.</summary>
    public static ItemVerdict Judge(SteadinessResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r.Score < 70 ? ItemVerdict.Check : r.Score < 90 || r.Cv > 0.10 ? ItemVerdict.Caution : ItemVerdict.Ok;
    }

    /// <summary>The verdict of the effective clock's steadiness (DESIGN-A 14.2): a score below 85 is caution, else ok.</summary>
    public static ItemVerdict JudgeClock(SteadinessResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r.Score < ClockCautionBelow ? ItemVerdict.Caution : ItemVerdict.Ok;
    }
}
