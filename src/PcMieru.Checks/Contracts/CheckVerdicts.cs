namespace PcMieru.Checks;

/// <summary>The overall verdict of a result (SPEC-unify §4.0, DESIGN-A §3).</summary>
public static class CheckVerdicts
{
    /// <summary>
    /// The worst verdict of the judged items, check &gt; caution &gt; unavailable &gt; ok.
    /// <list type="bullet">
    /// <item>An observation-only item (verdict info) never counts, with or without a value.</item>
    /// <item>No judged item at all, or every judged item missing, gives unavailable.</item>
    /// <item>A judged item with reason partial is written as caution, check or unavailable, never ok (DESIGN-A §3.5-2):
    /// a confirmed mismatch stays check after a stop, and a row that did not finish says unavailable.</item>
    /// <item>An ok item has no reason; ok with a reason is not a valid item (the validator rejects it), so the verdict is
    /// never reinterpreted here.</item>
    /// </list>
    /// </summary>
    public static CheckVerdict Overall(IEnumerable<CheckItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Overall(items.Select(static item => item.Verdict));
    }

    /// <summary>
    /// The same arithmetic over verdicts alone. A reader that skips the rows it does not know still reads every row's
    /// verdict, so it can check the overall verdict and the counts against all of them (DESIGN-A §3.5-11).
    /// </summary>
    public static CheckVerdict Overall(IEnumerable<ItemVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        var worst = -1;
        foreach (var verdict in verdicts)
        {
            if (verdict == ItemVerdict.Info)
            {
                continue;
            }

            worst = Math.Max(worst, Rank(Effective(verdict)));
        }

        return worst < 0 ? CheckVerdict.Unavailable : FromRank(worst);
    }

    /// <summary>The verdict one judged item contributes to the overall verdict. Not defined for info.</summary>
    internal static CheckVerdict Effective(CheckItem item) => Effective(item.Verdict);

    /// <summary>An identity: ok is ok, whatever else the item says (an ok item with a reason never gets here from a valid result).</summary>
    internal static CheckVerdict Effective(ItemVerdict verdict) => verdict switch
    {
        ItemVerdict.Check => CheckVerdict.Check,
        ItemVerdict.Caution => CheckVerdict.Caution,
        ItemVerdict.Unavailable => CheckVerdict.Unavailable,
        ItemVerdict.Ok => CheckVerdict.Ok,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "An info item has no overall verdict."),
    };

    // Worst first: check 3, caution 2, unavailable 1, ok 0.
    private static int Rank(CheckVerdict verdict) => verdict switch
    {
        CheckVerdict.Check => 3,
        CheckVerdict.Caution => 2,
        CheckVerdict.Unavailable => 1,
        CheckVerdict.Ok => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
    };

    private static CheckVerdict FromRank(int rank) => rank switch
    {
        3 => CheckVerdict.Check,
        2 => CheckVerdict.Caution,
        1 => CheckVerdict.Unavailable,
        _ => CheckVerdict.Ok,
    };
}
