namespace PcMieru.Checks.Tests;

/// <summary>The overall verdict (SPEC-unify §4.0, DESIGN-A §3): the worst of the judged items, check > caution > unavailable > ok.</summary>
public class VerdictTests
{
    // One token per item. A trailing "~" is reason partial (a value that is there but did not finish); "ok~" does not exist.
    //   ok caution check unavail  a judged item          info  an observation-only item
    private static CheckItem Make(string token)
    {
        var partial = token.EndsWith('~');
        var name = token.TrimEnd('~');

        return name switch
        {
            "ok" when partial => throw new ArgumentException("An ok item has no reason (DESIGN-A §3.5-2): " + token),
            "ok" => Judged(ItemVerdict.Ok, partial),
            "caution" => Judged(ItemVerdict.Caution, partial),
            "check" => Judged(ItemVerdict.Check, partial),
            "unavail" => new CheckItem("cpu.compute_errors", null, partial ? CheckValue.Int(0) : null, "count", ItemVerdict.Unavailable, CheckSource.Load, partial ? CheckReason.Partial : CheckReason.Error),
            "info" => new CheckItem("cpu.duration", null, partial ? CheckValue.Number(1) : null, "s", ItemVerdict.Info, CheckSource.Load, partial ? CheckReason.Partial : CheckReason.Unsupported),
            _ => throw new ArgumentException(token),
        };

        static CheckItem Judged(ItemVerdict verdict, bool partial) =>
            new("cpu.compute_errors", null, CheckValue.Int(1), "count", verdict, CheckSource.Load, partial ? CheckReason.Partial : null);
    }

    private static CheckVerdict Overall(string tokens) =>
        CheckVerdicts.Overall(tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Make).ToList());

    [Theory]
    // plain
    [InlineData("ok", CheckVerdict.Ok)]
    [InlineData("ok ok ok", CheckVerdict.Ok)]
    [InlineData("ok caution", CheckVerdict.Caution)]
    [InlineData("ok check", CheckVerdict.Check)]
    [InlineData("caution check ok", CheckVerdict.Check)]
    [InlineData("check caution unavail ok", CheckVerdict.Check)]
    [InlineData("caution unavail ok", CheckVerdict.Caution)]
    [InlineData("ok unavail", CheckVerdict.Unavailable)]
    [InlineData("unavail", CheckVerdict.Unavailable)]
    [InlineData("unavail unavail", CheckVerdict.Unavailable)]
    // unavailable ranks above ok but below caution
    [InlineData("ok ok ok unavail", CheckVerdict.Unavailable)]
    [InlineData("unavail caution", CheckVerdict.Caution)]
    // an item that did not finish (partial) says unavailable (an "ok" with a reason does not exist: DESIGN-A §3.5-2, see the validator tests)...
    [InlineData("unavail~", CheckVerdict.Unavailable)]
    [InlineData("unavail~ ok", CheckVerdict.Unavailable)]
    // ...unless it already found a caution or a check, which stays
    [InlineData("caution~", CheckVerdict.Caution)]
    [InlineData("caution~ ok", CheckVerdict.Caution)]
    [InlineData("check~", CheckVerdict.Check)]
    [InlineData("check~ unavail~ unavail", CheckVerdict.Check)]
    [InlineData("unavail~ caution", CheckVerdict.Caution)]
    [InlineData("unavail~ check", CheckVerdict.Check)]
    // observation-only items never count, with or without a value, partial or not
    [InlineData("info", CheckVerdict.Unavailable)]
    [InlineData("info info info", CheckVerdict.Unavailable)]
    [InlineData("info~", CheckVerdict.Unavailable)]
    [InlineData("info ok", CheckVerdict.Ok)]
    [InlineData("info~ ok", CheckVerdict.Ok)]
    [InlineData("info caution info", CheckVerdict.Caution)]
    // no items at all: nothing was judged
    [InlineData("", CheckVerdict.Unavailable)]
    public void Overall_is_the_worst_judged_item(string tokens, CheckVerdict expected)
    {
        Assert.Equal(expected, Overall(tokens));

        // The order of the items does not matter.
        var reversed = string.Join(' ', tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries).Reverse());
        Assert.Equal(expected, Overall(reversed));
    }

    [Fact]
    public void An_ok_item_with_a_reason_is_rejected_not_repaired_by_Overall()
    {
        // DESIGN-A §3.5-2: an ok item has no reason. Overall is a plain worst-of and does not rescue such an item (it used
        // to turn it into unavailable); the validator refuses it, so neither the writer nor the reader lets it through.
        var item = new CheckItem("cpu.compute_errors", null, CheckValue.Int(0), "count", ItemVerdict.Ok, CheckSource.Load, CheckReason.Partial);
        Assert.Equal(CheckVerdict.Ok, CheckVerdicts.Overall([item]));

        var result = CheckResult.Create(Samples.App, Samples.CpuRun, CheckKind.Cpu, Samples.Start, Samples.Start.AddSeconds(1), CheckOutcome.Completed, [item]);
        Assert.Contains(CheckResultValidator.Validate(result), p => p.Contains("ok cannot carry a reason", StringComparison.Ordinal));
        Assert.Throws<CheckFormatException>(() => CheckJson.Serialize(result));
    }

    [Fact]
    public void Design_example_3_4_is_check_with_eight_infos()
    {
        var result = Samples.MemoryCheck();
        Assert.Equal(CheckVerdict.Check, result.Verdict);
        Assert.Equal(new CheckCounts(Ok: 0, Caution: 0, Check: 1, Unavailable: 0, Info: 8), result.Counts);
    }

    [Fact]
    public void A_stopped_run_with_no_mismatch_found_is_unavailable()
    {
        // DESIGN-A §3.4: "unavailable, reason partial, value 0" and the overall is unavailable.
        var mismatches = CheckItem.Create("memory.mismatches", CheckValue.Int(0), ItemVerdict.Unavailable, CheckSource.Load, CheckReason.Partial);
        var duration = CheckItem.Create("memory.duration", CheckValue.Number(4.2), ItemVerdict.Info, CheckSource.Load);
        var result = CheckResult.Create(
            Samples.App,
            Samples.MemoryRun,
            CheckKind.Memory,
            Samples.Start,
            Samples.Start.AddMilliseconds(4200),
            CheckOutcome.Stopped,
            [duration, mismatches],
            [CheckNotes.MemoryBorrowedRegionOnly]);

        Assert.Equal(CheckVerdict.Unavailable, result.Verdict);
        Assert.Equal(new CheckCounts(0, 0, 0, 1, 1), result.Counts);
        Assert.Empty(CheckResultValidator.Validate(result));
    }

    [Fact]
    public void The_four_samples_have_the_verdict_their_items_give()
    {
        Assert.Equal(CheckVerdict.Caution, Samples.SystemCheck().Verdict);
        Assert.Equal(CheckVerdict.Caution, Samples.CpuCheck().Verdict);
        Assert.Equal(CheckVerdict.Check, Samples.MemoryCheck().Verdict);
        Assert.Equal(CheckVerdict.Caution, Samples.StorageCheck().Verdict);
    }

    [Fact]
    public void Counts_have_five_keys_and_add_up()
    {
        foreach (var (_, result) in Samples.All)
        {
            Assert.Equal(result.Items.Count, result.Counts.Total);
            Assert.Equal(CheckCounts.From(result.Items), result.Counts);
        }
    }
}
