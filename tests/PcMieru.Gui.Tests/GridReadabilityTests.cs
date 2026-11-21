using System.Globalization;
using PcMieru.Checks;

namespace PcMieru.Gui.Tests;

/// <summary>
/// What the person can read in the tables of the checks: no item label and no verdict word is cut by its column (a cell that is cut for a
/// long evidence line or a part's name has a tooltip with the whole text), the item, verdict and value columns are as wide as their content
/// and the evidence takes the rest, each of the five verdicts has a colour of its own that reads in both themes (also on the selected row), and
/// the overall line under a table does not write counts of zero. The grid answers for itself (<see cref="GridView.IsCut"/>): the
/// tests never keep a copy of its arithmetic.
/// </summary>
public class GridReadabilityTests
{
    private static readonly Guid Run = Guid.Parse("5b3f7a40-6a0e-4c5f-9a4f-3f6c1d8e9b21");

    /// <summary>The item column of the result grid (the grid keeps the number to itself).</summary>
    private const int ItemColumn = 0;
    private const int VerdictColumn = 1;
    private const int ValueColumn = 2;
    private const int EvidenceColumn = 3;

    /// <summary>A grid in a form of the size the checks page gives it (not shown: the grid measures with the font and the columns it has).</summary>
    private sealed class GridRig : IDisposable
    {
        private readonly IDisposable _locale;
        private readonly Form _form;
        private Font? _grown;

        public GridRig(Language language)
        {
            _locale = Harness.Locale(language);
            _form = new Form { Size = new Size(1100, 700) };
            Grid = new CheckResultGrid { Dock = DockStyle.Fill };
            _form.Controls.Add(Grid);

            // The column that takes the rest is set only for a grid that has a handle (the page's grid has one as soon as it is shown).
            _ = _form.Handle;
            _ = Grid.Handle;
            Grid.PrepareColumns(value => Grid.LogicalToDeviceUnits(value), "t");
            _form.PerformLayout();
            Grid.FitLastColumn();
        }

        public CheckResultGrid Grid { get; }

        /// <summary>The form's font at another size, as a denser screen has it (the grid scales its columns and measures again).</summary>
        public void Grow(float factor)
        {
            var basis = _form.Font;
            _grown?.Dispose();
            _grown = new Font(basis.FontFamily, basis.SizeInPoints * factor);
            _form.Font = _grown;
            _form.PerformLayout();
            Grid.FitLastColumn();
        }

        public void Dispose()
        {
            _form.Dispose();
            _grown?.Dispose();
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
    [InlineData(Language.Ja, 1.0f)]
    [InlineData(Language.En, 1.0f)]
    [InlineData(Language.Ja, 1.5f)]
    [InlineData(Language.En, 1.5f)]
    public void The_item_verdict_and_value_columns_are_as_wide_as_their_content_and_the_evidence_takes_the_rest(Language language, float factor)
    {
        using var rig = new GridRig(language);
        var grid = rig.Grid;
        if (factor != 1.0f)
        {
            rig.Grow(factor);
        }

        // The air a column keeps beyond what its text needs (the grid's own, a few pixels), and a pixel for the rounding of a scaled width.
        var slack = grid.Scaled(4) + 1;
        var widthBefore = grid.ClientSize.Width;
        foreach (var result in Results())
        {
            grid.Show(result);
            Assert.Empty(grid.UnreadableCuts());

            // The table is a vertical bar narrower once it holds more rows than it shows: the third is of the width it had when the columns were made.
            var widthNow = grid.ClientSize.Width;
            var ceiling = Math.Max(Math.Max(widthBefore, widthNow) * 33 / 100, grid.Scaled(120));
            var ceilingLeast = Math.Max(Math.Min(widthBefore, widthNow) * 33 / 100, grid.Scaled(120));

            // Item and verdict: never narrower than the header and every label or word, never wider than that and the grid's least.
            foreach (var (column, least) in new[] { (ItemColumn, 120), (VerdictColumn, 70) })
            {
                var need = grid.ContentNeed(column);
                var width = grid.Columns[column].Width;
                Assert.True(width <= Math.Max(need, grid.Scaled(least)) + slack, $"column {column} is {width} px for a content of {need} px ({language}, x{factor})");
                Assert.True(width >= grid.HeaderNeed(column), $"column {column} is {width} px, its header needs {grid.HeaderNeed(column)} px");
            }

            // Value: the same, and a very long value is held to a third of the table (it has a tooltip).
            var valueNeed = grid.ContentNeed(ValueColumn);
            var valueWidth = grid.Columns[ValueColumn].Width;
            Assert.True(valueWidth <= Math.Max(Math.Min(valueNeed, ceiling), grid.Scaled(90)) + slack, $"the value column is {valueWidth} px for a content of {valueNeed} px ({language}, x{factor})");
            Assert.True(valueWidth >= Math.Min(valueNeed, ceilingLeast), $"the value column is {valueWidth} px, its content needs {valueNeed} px");

            // Evidence: everything that is left, and the columns together never run past the table.
            var others = grid.Columns[ItemColumn].Width + grid.Columns[VerdictColumn].Width + grid.Columns[ValueColumn].Width;
            var evidence = grid.Columns[EvidenceColumn].Width;
            Assert.True(evidence >= grid.ClientSize.Width - others - grid.Scaled(40), $"evidence is {evidence} px, {grid.ClientSize.Width - others} px were left");
            Assert.True(others + evidence <= grid.ClientSize.Width + 1 || evidence <= grid.Scaled(140) + 1, $"the columns are {others + evidence} px in a table of {grid.ClientSize.Width} px");
        }
    }

    [Fact]
    public void The_columns_follow_a_change_of_language()
    {
        using var rig = new GridRig(Language.En);
        var grid = rig.Grid;
        grid.Show(FakeCheckData.SystemCheck(Run, FakeCheckData.Start));
        var before = grid.Columns[ItemColumn].Width;

        Strings.Current = Language.Ja;
        grid.ApplyLanguage();

        // The labels are written again, and the column is made for what it holds now: the grid's own measure of it, not the width it had.
        Assert.True(grid.Columns[ItemColumn].Width >= grid.ContentNeed(ItemColumn), $"{grid.Columns[ItemColumn].Width} px for {grid.ContentNeed(ItemColumn)} px (it was {before} px)");
        Assert.Empty(grid.UnreadableCuts());
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

    // ---------------------------------------------------------------- the colours of the verdicts

    private const double Readable = 4.5;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_five_verdicts_are_five_colours_and_each_reads_on_a_plain_row_and_on_the_selected_one(bool dark)
    {
        var mode = dark ? ThemeMode.Dark : ThemeMode.Light;
        using var theme = new ThemeService(mode);
        var palette = theme.Palette;
        var verdicts = Enum.GetValues<ItemVerdict>();
        Assert.Equal(5, verdicts.Length);

        var plain = verdicts.ToDictionary(static v => v, v => GridView.ToneColor(palette, CheckFormat.Tone(v), selected: false));
        var chosen = verdicts.ToDictionary(static v => v, v => GridView.ToneColor(palette, CheckFormat.Tone(v), selected: true));

        // Five words, five colours: caution and needs review in particular are not the same colour (they were both bluish).
        Assert.Equal(5, plain.Values.Select(static c => c.ToArgb()).Distinct().Count());
        Assert.Equal(5, chosen.Values.Select(static c => c.ToArgb()).Distinct().Count());
        Assert.NotEqual(plain[ItemVerdict.Caution], plain[ItemVerdict.Check]);

        foreach (var verdict in verdicts)
        {
            // On the table's own background and on the page's.
            Assert.True(BuildCodeUiTests.ContrastRatio(plain[verdict], palette.Surface) >= Readable, $"{verdict} on the table: {BuildCodeUiTests.ContrastRatio(plain[verdict], palette.Surface):F2}:1 ({mode})");
            Assert.True(BuildCodeUiTests.ContrastRatio(plain[verdict], palette.Window) >= Readable, $"{verdict} on the window: {BuildCodeUiTests.ContrastRatio(plain[verdict], palette.Window):F2}:1 ({mode})");

            // On the selected row (the tone blended 0.7 toward its own share, the rest the selection's text colour).
            Assert.True(BuildCodeUiTests.ContrastRatio(chosen[verdict], palette.Selection) >= Readable, $"{verdict} on the selected row: {BuildCodeUiTests.ContrastRatio(chosen[verdict], palette.Selection):F2}:1 ({mode})");
        }
    }

    [Fact]
    public void The_check_is_able_to_fail_two_verdicts_on_one_colour_are_found()
    {
        // A test that nothing can fail proves nothing: with caution and needs review on one tone, the five colours are four.
        using var theme = new ThemeService(ThemeMode.Light);
        var palette = theme.Palette;
        Color Mapped(ItemVerdict verdict) => GridView.ToneColor(palette, verdict == ItemVerdict.Check ? CheckFormat.Tone(ItemVerdict.Caution) : CheckFormat.Tone(verdict), selected: false);

        Assert.Equal(4, Enum.GetValues<ItemVerdict>().Select(Mapped).Select(static c => c.ToArgb()).Distinct().Count());
    }

    [Fact]
    public void The_verdict_word_of_a_row_that_needs_review_is_bold_and_the_other_four_are_not()
    {
        using var rig = new GridRig(Language.En);
        var grid = rig.Grid;
        grid.Show(FakeCheckData.SystemCheck(Run, FakeCheckData.Start));

        for (var row = 0; row < grid.RowCount; row++)
        {
            if (grid.ItemAt(row) is not { } item)
            {
                continue;
            }

            Assert.Equal(item.Verdict == ItemVerdict.Check, grid.Cells!(row, VerdictColumn).Bold);
            Assert.Equal(CheckFormat.Tone(item.Verdict), grid.ToneAt(row, VerdictColumn));
        }
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
