using System.Globalization;
using PcMieru.Checks;

namespace PcMieru.Gui.Tests;

/// <summary>
/// What the person can read in the tables of the checks: no item label and no verdict word is cut by its column (a cell that is cut for a
/// long evidence line or a part's name has a tooltip with the whole text), the item column follows the longest label of the language on
/// show, and the overall line under a table does not write counts of zero. The grid answers for itself (<see cref="GridView.IsCut"/>): the
/// tests never keep a copy of its arithmetic.
/// </summary>
public class GridReadabilityTests
{
    private static readonly Guid Run = Guid.Parse("5b3f7a40-6a0e-4c5f-9a4f-3f6c1d8e9b21");

    /// <summary>The item column of the result grid (the grid keeps the number to itself).</summary>
    private const int ItemColumn = 0;

    /// <summary>A grid in a form of the size the checks page gives it (not shown: the grid measures with the font and the columns it has).</summary>
    private sealed class GridRig : IDisposable
    {
        private readonly IDisposable _locale;
        private readonly Form _form;

        public GridRig(Language language)
        {
            _locale = Harness.Locale(language);
            _form = new Form { Size = new Size(1100, 700) };
            Grid = new CheckResultGrid { Dock = DockStyle.Fill };
            _form.Controls.Add(Grid);
            Grid.PrepareColumns(value => Grid.LogicalToDeviceUnits(value), "t");
            _form.PerformLayout();
            Grid.FitLastColumn();
        }

        public CheckResultGrid Grid { get; }

        public void Dispose()
        {
            _form.Dispose();
            _locale.Dispose();
        }
    }

    private static IEnumerable<CheckResult> Results()
    {
        yield return FakeCheckData.SystemCheck(Run, FakeCheckData.Start);
        yield return FakeCheckData.Cpu(Run, FakeCheckData.Start, 30);
        yield return FakeCheckData.Memory(Run, FakeCheckData.Start, 30);
        yield return FakeCheckData.Storage(Run, FakeCheckData.Start, elevated: true);
        yield return FakeCheckData.Storage(Run, FakeCheckData.Start, elevated: false);
        yield return FakeCheckData.StoppedMemory(Run, FakeCheckData.Start);
    }

    [Theory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void No_item_label_and_no_verdict_word_of_any_table_is_cut_and_every_other_cut_has_a_tooltip(Language language)
    {
        using var rig = new GridRig(language);

        var shown = 0;
        foreach (var result in Results())
        {
            rig.Grid.Show(result);
            shown += result.Items.Count;
            Assert.Empty(rig.Grid.UnreadableCuts());
        }

        Assert.True(shown > 40, "the tables held rows to look at");
    }

    [Theory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void The_item_column_is_as_wide_as_the_longest_label_of_the_language_and_follows_a_change_of_language(Language language)
    {
        using var rig = new GridRig(language);
        var grid = rig.Grid;
        var other = language == Language.Ja ? Language.En : Language.Ja;
        var widest = CheckItemRegistry.All.Max(spec => TextRenderer.MeasureText(CheckFormat.ItemLabel(spec.Id), grid.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width);

        // The longest label of the language on show fits in the column with the grid's room around it (the padding on both sides).
        Assert.True(grid.Columns[ItemColumn].Width >= widest + (grid.LogicalToDeviceUnits(8) * 2), $"the item column is {grid.Columns[ItemColumn].Width} px, the longest label {widest} px");

        // The language changes: the labels are written again, and the column is made for the new longest one.
        Strings.Current = other;
        grid.ApplyLanguage();
        var widestOther = CheckItemRegistry.All.Max(spec => TextRenderer.MeasureText(CheckFormat.ItemLabel(spec.Id), grid.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width);
        Assert.True(grid.Columns[ItemColumn].Width >= widestOther + (grid.LogicalToDeviceUnits(8) * 2), $"after the change the item column is {grid.Columns[ItemColumn].Width} px, the longest label {widestOther} px");
        foreach (var result in Results())
        {
            grid.Show(result);
            Assert.Empty(grid.UnreadableCuts());
        }
    }

    [Fact]
    public void The_check_is_able_to_fail_a_column_that_is_too_narrow_is_found()
    {
        using var rig = new GridRig(Language.En);
        var grid = rig.Grid;
        grid.Show(FakeCheckData.SystemCheck(Run, FakeCheckData.Start));
        Assert.Empty(grid.UnreadableCuts());

        // A third of the width: the labels are cut and nothing holds them in a tooltip, so the grid says so.
        grid.Columns[ItemColumn].Width /= 3;
        var cuts = grid.UnreadableCuts();
        Assert.NotEmpty(cuts);
        Assert.Contains(cuts, static cut => cut.Contains(":" + ItemColumn.ToString(CultureInfo.InvariantCulture) + " ", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the overall line

    private static CheckResult Only(Func<CheckItem, bool> keep)
    {
        var items = FakeCheckData.SystemCheck(Run, FakeCheckData.Start).Items.Where(keep).ToList();
        return CheckResult.Create(FakeCheckData.App, Run, CheckKind.System, FakeCheckData.Start, FakeCheckData.Start.AddSeconds(1), CheckOutcome.Completed, items);
    }

    private static string Word(string key, Language language)
    {
        var entry = GuiStrings.All.Single(e => e.Key == key);
        return language == Language.Ja ? entry.Ja : entry.En;
    }

    [Theory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void The_overall_line_names_only_the_verdicts_that_have_rows(Language language)
    {
        using var _ = Harness.Locale(language);
        string Verdict(string name) => Word("gui.chk.verdict." + name, language);

        // Every verdict has rows: all five are written, in the order of the table's own words.
        var all = FakeCheckData.SystemCheck(Run, FakeCheckData.Start);
        var counts = all.Items.GroupBy(static i => i.Verdict).ToDictionary(static g => g.Key, static g => g.Count());
        Assert.Equal(5, counts.Count);
        var line = CheckFormat.SummaryText(all);
        Assert.Contains($"{Verdict("check")} {counts[ItemVerdict.Check]} / {Verdict("caution")} {counts[ItemVerdict.Caution]} / {Verdict("ok")} {counts[ItemVerdict.Ok]} / {Verdict("unavailable")} {counts[ItemVerdict.Unavailable]} / {Verdict("info")} {counts[ItemVerdict.Info]}", line, StringComparison.Ordinal);

        // Only OK rows: no zero and no word of another verdict.
        var okOnly = Only(static i => i.Verdict == ItemVerdict.Ok);
        var okCount = okOnly.Items.Count;
        Assert.True(okCount > 0);
        var okLine = CheckFormat.SummaryText(okOnly);
        Assert.Equal(Word("gui.chk.summary", language).Replace("{verdict}", Verdict("ok"), StringComparison.Ordinal).Replace("{counts}", Verdict("ok") + " " + okCount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), okLine);
        Assert.DoesNotContain(" 0", okLine, StringComparison.Ordinal);
        foreach (var other in new[] { "check", "caution", "unavailable", "info" })
        {
            Assert.DoesNotContain(Verdict(other), okLine, StringComparison.Ordinal);
        }

        // Ok and info rows: the two that have rows, joined, and nothing for the others.
        var two = Only(static i => i.Verdict is ItemVerdict.Ok or ItemVerdict.Info);
        var twoLine = CheckFormat.SummaryText(two);
        Assert.Contains($"{Verdict("ok")} {two.Items.Count(static i => i.Verdict == ItemVerdict.Ok)} / {Verdict("info")} {two.Items.Count(static i => i.Verdict == ItemVerdict.Info)}", twoLine, StringComparison.Ordinal);
        Assert.DoesNotContain(" 0", twoLine, StringComparison.Ordinal);
        Assert.DoesNotContain(Verdict("caution"), twoLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void A_result_with_no_row_says_the_overall_verdict_alone_without_an_empty_bracket(Language language)
    {
        using var _ = Harness.Locale(language);
        var empty = CheckResult.Create(FakeCheckData.App, Run, CheckKind.Cpu, FakeCheckData.Start, FakeCheckData.Start.AddSeconds(1), CheckOutcome.Stopped, []);

        var line = CheckFormat.SummaryText(empty);

        Assert.Equal(Word("gui.chk.summary.none", language).Replace("{verdict}", CheckFormat.VerdictWord(empty.Verdict), StringComparison.Ordinal), line);
        Assert.DoesNotContain("(", line, StringComparison.Ordinal);
        Assert.DoesNotContain("（", line, StringComparison.Ordinal);
        Assert.DoesNotContain("0", line, StringComparison.Ordinal);
        // The line under a table, the history's detail and the run tab share it: the result line carries it and says there is nothing to show.
        Assert.Contains(line, CheckFormat.ResultLine(empty), StringComparison.Ordinal);
    }
}
