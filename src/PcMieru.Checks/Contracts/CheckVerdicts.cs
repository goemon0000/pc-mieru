namespace PcMieru.Checks;

/// <summary>The overall verdict of a result (SPEC-unify §4.0, DESIGN-A §3).</summary>
public static class CheckVerdicts
{
    /// <summary>
    /// The worst verdict of the judged items, check &gt; caution &gt; unavailable &gt; ok.
    /// <list type="bullet">
    /// <item>An observation-only item (verdict info) never counts, with or without a value.</item>
    /// <item>No judged item at all, or every judged item missing, gives unavailable.</item>
    /// <item>A judged item with reason partial counts as unavailable, unless it already found a caution or a check:
    /// a confirmed mismatch stays check after a stop, but a row that did not finish cannot say ok.</item>
    /// <item>An item that says ok while it has any reason is not a complete reading either; it counts as unavailable.</item>
    /// </list>
    /// </summary>
    public static CheckVerdict Overall(IEnumerable<CheckItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var worst = -1;
        foreach (var item in items)
        {
            if (item.Verdict == ItemVerdict.Info)
            {
                continue;
            }

            worst = Math.Max(worst, Rank(Effective(item)));
        }

        return worst < 0 ? CheckVerdict.Unavailable : FromRank(worst);
    }

    /// <summary>The verdict one judged item contributes to the overall verdict. Not defined for info.</summary>
    internal static CheckVerdict Effective(CheckItem item) => item.Verdict switch
    {
        ItemVerdict.Check => CheckVerdict.Check,
        ItemVerdict.Caution => CheckVerdict.Caution,
        ItemVerdict.Unavailable => CheckVerdict.Unavailable,
        ItemVerdict.Ok => item.Reason is null ? CheckVerdict.Ok : CheckVerdict.Unavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(item), item.Verdict, "An info item has no overall verdict."),
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
