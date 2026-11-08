using PcMieru.Wire;

namespace PcMieru.Cli.Tests;

public sealed class CliTableTests
{
    private static IReadOnlyList<string> Row(params string[] cells) => cells;

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The display column at which the given text starts on a line.</summary>
    private static int ColumnOf(string line, string needle) => CliText.Width(line[..line.IndexOf(needle, StringComparison.Ordinal)]);

    [Fact]
    public void Columns_line_up_by_display_width_when_the_text_has_full_width_characters()
    {
        var text = CliTable.Render(
            ["名前", "PID"],
            [Row("メモ帳", "100"), Row("abc", "2000"), Row("日本語のプロセス", "3")],
            new HashSet<int> { 1 });

        var lines = Lines(text);
        Assert.Equal(4, lines.Length);
        // Every number ends in the same display column, so the right edge is straight.
        var ends = lines.Select(CliText.Width).Distinct().ToArray();
        Assert.Single(ends);
    }

    [Fact]
    public void A_left_aligned_last_column_is_not_padded()
    {
        var text = CliTable.Render(["a", "note"], [Row("1", "x"), Row("22", "a longer note")]);

        Assert.All(Lines(text), l => Assert.Equal(l.TrimEnd(), l));
        Assert.Equal("1   x", Lines(text)[1]);
    }

    [Fact]
    public void An_empty_last_cell_leaves_no_trailing_spaces()
    {
        var text = CliTable.Render(["id", "state", "note"], [Row("a", "on", string.Empty), Row("bb", "off", "careful")]);

        Assert.All(Lines(text), l => Assert.Equal(l.TrimEnd(), l));
        Assert.Equal("a   on", Lines(text)[1]);
    }

    [Fact]
    public void Right_aligned_columns_are_padded_on_the_left_and_keep_their_last_position()
    {
        var text = CliTable.Render(["name", "n"], [Row("a", "5"), Row("b", "1234")], new HashSet<int> { 1 });

        var lines = Lines(text);
        Assert.Equal("name     n", lines[0]);
        Assert.Equal("a        5", lines[1]);
        Assert.Equal("b     1234", lines[2]);
    }

    [Fact]
    public void The_flex_column_is_cut_with_an_ellipsis_so_no_line_is_wider_than_the_terminal()
    {
        var long1 = new string('x', 120);
        var text = CliTable.Render(
            ["PID", "Name", "Mem"],
            [Row("1", long1, "10"), Row("22", "short", "2000")],
            new HashSet<int> { 0, 2 },
            flexColumn: 1,
            consoleWidth: 40);

        var lines = Lines(text);
        Assert.All(lines, l => Assert.True(CliText.Width(l) <= 40, l));
        Assert.Contains("…", lines[1], StringComparison.Ordinal);
        Assert.Contains("short", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_width_flex_cell_is_cut_without_splitting_a_character()
    {
        var text = CliTable.Render(
            ["PID", "Name", "Mem"],
            [Row("1", string.Concat(Enumerable.Repeat("日本語", 30)), "10")],
            new HashSet<int> { 0, 2 },
            flexColumn: 1,
            consoleWidth: 41);

        Assert.All(Lines(text), l => Assert.True(CliText.Width(l) <= 41, l));
    }

    [Fact]
    public void A_console_width_of_zero_means_not_a_terminal_and_nothing_is_cut()
    {
        var long1 = new string('x', 300);

        var text = CliTable.Render(["a", "b"], [Row("1", long1)], flexColumn: 1, consoleWidth: 0);

        Assert.Contains(long1, text, StringComparison.Ordinal);
        Assert.DoesNotContain("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Control_characters_in_a_cell_cannot_move_the_cursor()
    {
        var text = CliTable.Render(["name", "n"], [Row("evil\u001b[31m\r\nname\ttab", "1")], new HashSet<int> { 1 });

        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\t', text);
        Assert.Equal(2, Lines(text).Length);
    }

    [Fact]
    public void Every_body_row_keeps_the_second_column_at_the_header_position()
    {
        var text = CliTable.Render(["Name", "State"], [Row("メモ帳", "on"), Row("Discord.exe", "off")]);

        var lines = Lines(text);
        var column = ColumnOf(lines[0], "State");
        Assert.Equal(column, ColumnOf(lines[1], "on"));
        Assert.Equal(column, ColumnOf(lines[2], "off"));
    }

    [Fact]
    public void A_table_without_rows_is_just_the_header()
    {
        var text = CliTable.Render(["a", "b"], []);

        Assert.Equal("a  b\n", text);
    }
}

public sealed class OperationTextTests
{
    private static CliContext Context(Language language = Language.En)
    {
        // Touching the command list registers every command's strings.
        Assert.NotEmpty(CliApp.Commands);
        return new CliContext(
            new CliEnvironment(new StringReader(string.Empty), new StringWriter(), new StringWriter(), o => CliRun.Services(o), 120, language),
            language,
            CancellationToken.None);
    }

    [Fact]
    public void Combine_of_nothing_or_only_successes_is_0()
    {
        Assert.Equal(ExitCode.Success, OperationText.Combine([]));
        Assert.Equal(ExitCode.Success, OperationText.Combine([OperationCode.Ok, OperationCode.Ok]));
    }

    [Fact]
    public void Combine_of_the_same_failure_is_that_failure()
    {
        Assert.Equal(ExitCode.Protected, OperationText.Combine([OperationCode.Protected, OperationCode.Protected]));
        Assert.Equal(ExitCode.Denied, OperationText.Combine([OperationCode.Denied]));
    }

    [Fact]
    public void Combine_of_a_mix_is_the_general_failure()
    {
        Assert.Equal(ExitCode.Failure, OperationText.Combine([OperationCode.Ok, OperationCode.Denied]));
        Assert.Equal(ExitCode.Failure, OperationText.Combine([OperationCode.Protected, OperationCode.Denied]));
    }

    [Theory]
    [InlineData(OperationCode.Ok, 0)]
    [InlineData(OperationCode.PartialSuccess, 1)]
    [InlineData(OperationCode.Error, 1)]
    [InlineData(OperationCode.Cancelled, 1)]
    [InlineData(OperationCode.InvalidArguments, 2)]
    [InlineData(OperationCode.NotFound, 3)]
    [InlineData(OperationCode.Gone, 3)]
    [InlineData(OperationCode.Denied, 4)]
    [InlineData(OperationCode.Protected, 5)]
    [InlineData(OperationCode.MultipleMatches, 6)]
    public void Operation_codes_map_to_the_documented_exit_codes(OperationCode code, int expected) =>
        Assert.Equal(expected, (int)ExitCodes.From(code));

    [Fact]
    public void Every_operation_code_has_a_text_in_both_languages()
    {
        foreach (var language in new[] { Language.En, Language.Ja })
        {
            using var context = Context(language);
            foreach (var code in Enum.GetValues<OperationCode>())
            {
                var text = OperationText.Describe(context, code);
                Assert.False(string.IsNullOrWhiteSpace(text), code.ToString());
                Assert.DoesNotContain("cli.result.", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void A_line_adds_the_providers_words_but_only_for_failures_and_without_control_characters()
    {
        using var context = Context();

        Assert.Equal("Done", OperationText.Line(context, OperationResult.Success("something")));
        Assert.Equal("Permission denied (no way)", OperationText.Line(context, OperationResult.Fail(OperationCode.Denied, " no way ")));
        Assert.Equal("Failed", OperationText.Line(context, OperationResult.Fail(OperationCode.Error, string.Empty)));
        Assert.DoesNotContain('\u001b', OperationText.Line(context, OperationResult.Fail(OperationCode.Error, "a\u001b[2Jb")));
    }
}

public sealed class StartedAtTests
{
    private static ProcessKey Key(DateTime utc) => new(1234, utc.ToFileTimeUtc());

    [Fact]
    public void The_file_time_number_must_match_exactly()
    {
        var key = Key(new DateTime(2026, 11, 8, 12, 0, 5, DateTimeKind.Utc));
        var parsed = StartedAt.Parse(key.CreateTime.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(parsed.Matches(key));
        Assert.False(parsed.Matches(new ProcessKey(1234, key.CreateTime + 1)));
    }

    [Fact]
    public void A_date_names_a_one_second_window()
    {
        var utc = new DateTime(2026, 11, 8, 12, 0, 5, DateTimeKind.Utc);
        var parsed = StartedAt.Parse("2026-11-08T12:00:05Z");

        Assert.True(parsed.Matches(Key(utc.AddMilliseconds(400))));
        Assert.True(parsed.Matches(Key(utc.AddMilliseconds(-400))));
        Assert.False(parsed.Matches(Key(utc.AddSeconds(2))));
    }

    [Fact]
    public void A_date_never_matches_a_process_whose_start_time_is_unknown()
    {
        Assert.False(StartedAt.Parse("1601-01-01T00:00:00Z").Matches(new ProcessKey(4, 0)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("0")]
    [InlineData("-5")]
    public void Anything_else_is_a_usage_error(string text) =>
        Assert.Throws<ArgException>(() => StartedAt.Parse(text));
}
