namespace PcMieru.Gui.Tests;

public sealed class ReportPreviewTests
{
    private const string Sample =
        "# Diagnostic report\n"
        + "- Generated: 2026-11-08\n"
        + "> Names are data read from this PC.\n"
        + "\n"
        + "## Observations\n"
        + "- [notice / candidate] CPU is busy. Top: `chrome.exe`\n"
        + "  - Evidence: cpu.total=12.5 %\n"
        + "\n"
        + "## Processes\n"
        + "| Name | PID | CPU % |\n"
        + "|---|---:|---:|\n"
        + "| `a\\|b.exe` | 12 | 3.5 |\n"
        + "| `chrome.exe` | 7200 | 1.0 |\n";

    private static IReadOnlyList<PreviewBlock> Parsed() => ReportPreview.Parse(Sample);

    [Fact]
    public void Lines_of_the_report_become_a_title_headings_bullets_a_quote_and_a_table()
    {
        var kinds = Parsed().Select(b => b.Kind).ToArray();
        Assert.Equal(
            [
                PreviewKind.Title,
                PreviewKind.Bullet,
                PreviewKind.Quote,
                PreviewKind.Blank,
                PreviewKind.Heading,
                PreviewKind.Bullet,
                PreviewKind.SubBullet,
                PreviewKind.Blank,
                PreviewKind.Heading,
                PreviewKind.TableHeader,
                PreviewKind.TableRow,
                PreviewKind.TableRow,
            ],
            kinds);
        Assert.Equal("Diagnostic report", PreviewBlock.Plain(Parsed()[0].Runs));
    }

    [Fact]
    public void The_marks_of_Markdown_are_gone_but_every_word_stays()
    {
        var text = ReportPreview.PlainText(Parsed());
        Assert.DoesNotContain("`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("##", text, StringComparison.Ordinal);
        Assert.DoesNotContain("|---", text, StringComparison.Ordinal);
        Assert.DoesNotContain("> ", text, StringComparison.Ordinal);
        Assert.Contains("CPU is busy. Top: chrome.exe", text, StringComparison.Ordinal);
        Assert.Contains("Evidence: cpu.total=12.5 %", text, StringComparison.Ordinal);
        Assert.Contains("Names are data read from this PC.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_from_the_PC_is_a_code_run_and_the_level_tag_is_cut_off_the_observation()
    {
        var observation = Parsed()[5];
        Assert.Equal(
            [
                new PreviewRun("[notice / candidate]", PreviewStyle.Tag),
                new PreviewRun(" CPU is busy. Top: ", PreviewStyle.Plain),
                new PreviewRun("chrome.exe", PreviewStyle.Code),
            ],
            observation.Runs);
    }

    [Fact]
    public void A_table_has_one_cell_per_column_and_an_escaped_bar_stays_inside_its_cell()
    {
        var table = Parsed().Where(b => b.Kind is PreviewKind.TableHeader or PreviewKind.TableRow).ToList();
        Assert.All(table, row => Assert.Equal(3, row.Cells.Count));
        Assert.Equal("Name", PreviewBlock.Plain(table[0].Cells[0]));
        Assert.Equal("a|b.exe", PreviewBlock.Plain(table[1].Cells[0]));
        Assert.Equal(PreviewStyle.Code, table[1].Cells[0][0].Style);
        Assert.Equal("3.5", PreviewBlock.Plain(table[1].Cells[2]));
    }

    [Fact]
    public void A_bracket_that_is_not_a_level_tag_and_a_lone_backtick_stay_as_text()
    {
        var blocks = ReportPreview.Parse("- [just a note] and a ` alone\n- plain\n");
        Assert.Equal("[just a note] and a ` alone", PreviewBlock.Plain(blocks[0].Runs));
        Assert.All(blocks[0].Runs, run => Assert.Equal(PreviewStyle.Plain, run.Style));
    }

    [Fact]
    public void Blank_lines_do_not_pile_up_or_trail_and_an_empty_text_is_an_empty_preview()
    {
        Assert.Empty(ReportPreview.Parse(string.Empty));
        var blocks = ReportPreview.Parse("\n\n# T\n\n\n\n- a\n\n");
        Assert.Equal([PreviewKind.Title, PreviewKind.Blank, PreviewKind.Bullet], blocks.Select(b => b.Kind).ToArray());
    }

    [Fact]
    public void Windows_line_endings_read_the_same_as_plain_ones()
    {
        var crlf = ReportPreview.Parse(Sample.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(ReportPreview.PlainText(Parsed()), ReportPreview.PlainText(crlf));
    }
}
