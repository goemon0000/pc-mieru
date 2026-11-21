namespace PcMieru.Cli.Tests;

public class CliStringsTests
{
    private static IEnumerable<StringEntry> AllCliEntries() =>
        CliStrings.All.Concat(CliApp.Commands.SelectMany(c => c.Texts));

    [Fact]
    public void Commands_are_discovered_and_have_unique_lowercase_names()
    {
        var names = CliApp.Commands.Select(c => c.Name).ToArray();
        Assert.True(names.Length >= 15);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, n => Assert.Matches("^[a-z][a-z0-9-]*$", n));
        Assert.Equal(names.Order(StringComparer.Ordinal).ToArray(), names);
    }

    [Fact]
    public void Every_command_has_a_help_line_in_both_languages()
    {
        foreach (var command in CliApp.Commands)
        {
            var entry = command.Texts.SingleOrDefault(t => t.Key == "cli.help." + command.Name);
            Assert.False(string.IsNullOrWhiteSpace(entry.Ja), command.Name + " ja");
            Assert.False(string.IsNullOrWhiteSpace(entry.En), command.Name + " en");
            Assert.DoesNotContain('[', Strings.Get("cli.help." + command.Name, Language.En));
        }
    }

    [Fact]
    public void Command_strings_use_the_cli_prefix_and_both_languages_agree_on_placeholders()
    {
        foreach (var entry in AllCliEntries())
        {
            Assert.StartsWith("cli.", entry.Key, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(entry.Ja), entry.Key + " ja");
            Assert.False(string.IsNullOrWhiteSpace(entry.En), entry.Key + " en");
            Assert.Equal(Strings.Placeholders(entry.Ja), Strings.Placeholders(entry.En));
        }
    }

    [Fact]
    public void The_help_summary_is_in_the_text_of_the_language_asked_for()
    {
        Assert.Contains("プロセス", CliApp.Help(Language.Ja), StringComparison.Ordinal);
        Assert.Contains("List processes", CliApp.Help(Language.En), StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_starts_with_pcmi_and_the_command_name_and_options_do_not_clash_with_globals()
    {
        foreach (var command in CliApp.Commands)
        {
            Assert.StartsWith("pcmi " + command.Name, command.Usage, StringComparison.Ordinal);
            var names = command.Options.Select(o => o.Name).ToArray();
            Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain("help", names);
            Assert.DoesNotContain("lang", names);
            Assert.DoesNotContain("version", names);
            var shorts = command.Options.Where(o => o.Short != '\0').Select(o => o.Short).ToArray();
            Assert.Equal(shorts.Length, shorts.Distinct().Count());
            Assert.DoesNotContain('h', shorts);
        }
    }

    [Fact]
    public void Every_option_named_in_a_usage_line_exists()
    {
        foreach (var command in CliApp.Commands)
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(command.Usage, @"--([a-z][a-z-]*)"))
            {
                Assert.Contains(command.Options, o => o.Name == m.Groups[1].Value);
            }
        }
    }

    // ---- the checks and the configuration code (step 11): one word for every member of every list the library can grow ----

    private static Dictionary<string, StringEntry> Table() =>
        AllCliEntries().ToDictionary(e => e.Key, e => e, StringComparer.Ordinal);

    private static void AssertHasText(Dictionary<string, StringEntry> table, string key)
    {
        Assert.True(table.TryGetValue(key, out var entry), "no text for " + key);
        Assert.False(string.IsNullOrWhiteSpace(entry.Ja), key + " ja");
        Assert.False(string.IsNullOrWhiteSpace(entry.En), key + " en");
    }

    private static string Lower<T>(T value)
        where T : Enum => value.ToString()!.ToLowerInvariant();

    [Fact]
    public void Every_member_of_the_check_enums_has_a_word_in_both_languages()
    {
        var table = Table();
        foreach (var verdict in Enum.GetValues<PcMieru.Checks.ItemVerdict>())
        {
            AssertHasText(table, "cli.check.verdict." + Lower(verdict));
        }

        foreach (var verdict in Enum.GetValues<PcMieru.Checks.CheckVerdict>())
        {
            AssertHasText(table, "cli.check.verdict." + Lower(verdict));
        }

        foreach (var kind in Enum.GetValues<PcMieru.Checks.CheckKind>())
        {
            AssertHasText(table, "cli.check.kind." + Lower(kind));
        }

        foreach (var outcome in Enum.GetValues<PcMieru.Checks.CheckOutcome>())
        {
            AssertHasText(table, "cli.check.outcome." + Lower(outcome));
        }

        foreach (var reason in Enum.GetValues<PcMieru.Checks.CheckReason>())
        {
            AssertHasText(table, "cli.check.reason." + Lower(reason));
        }

        foreach (var stage in Enum.GetValues<PcMieru.Checks.CheckStage>())
        {
            AssertHasText(table, "cli.check.stage." + Lower(stage));
        }
    }

    [Fact]
    public void Every_known_note_failure_save_failure_and_history_warning_has_a_text()
    {
        var table = Table();
        foreach (var note in PcMieru.Checks.CheckNotes.Known)
        {
            AssertHasText(table, "cli.check.note." + note);
        }

        foreach (var code in PcMieru.Checks.CheckRunFailureCodes.All)
        {
            AssertHasText(table, "cli.check.fail." + code);
        }

        foreach (var code in PcMieru.Checks.CheckSaveFailureCodes.All)
        {
            AssertHasText(table, "cli.check.savefail." + code);
        }

        foreach (var code in PcMieru.Checks.CheckHistoryWarningCodes.All)
        {
            AssertHasText(table, "cli.check.warn." + code);
        }

        // The words for a code this version has not met are there too, and every one carries the code it says.
        AssertHasText(table, "cli.check.note.unknown");
        AssertHasText(table, "cli.check.fail.unknown");
        AssertHasText(table, "cli.check.savefail.unknown");
        AssertHasText(table, "cli.check.warn.unknown");
        AssertHasText(table, "cli.check.savefail.notSaved");
    }

    [Fact]
    public void Every_note_kind_unread_reason_and_category_of_the_configuration_code_has_a_text()
    {
        var table = Table();
        foreach (var kind in Enum.GetValues<PcMieru.Checks.Build.BuildNoteKind>())
        {
            var suffix = Commands.PartsCommand.NoteKey(kind);
            Assert.NotNull(suffix);
            AssertHasText(table, "cli.parts.note." + suffix);
        }

        AssertHasText(table, "cli.parts.note.one_part_left_out_for_length_many");
        AssertHasText(table, "cli.parts.note.unknown");
        foreach (var reason in Enum.GetValues<PcMieru.Checks.CheckReason>())
        {
            AssertHasText(table, "cli.parts.reason." + Lower(reason));
        }

        foreach (var category in PcMieru.Checks.Build.BuildConfig.Categories)
        {
            AssertHasText(table, "cli.parts.category." + category.Replace('-', '_'));
        }
    }

    [Fact]
    public void The_five_verdict_words_are_the_ones_the_spec_names_in_both_languages()
    {
        var table = Table();
        string[] keys = ["ok", "caution", "check", "unavailable", "info"];
        Assert.Equal(["OK", "注意", "要確認", "測れず", "参考"], keys.Select(k => table["cli.check.verdict." + k].Ja).ToArray());
        Assert.Equal(["OK", "Caution", "Needs review", "Not measured", "Info"], keys.Select(k => table["cli.check.verdict." + k].En).ToArray());
    }

    [Theory]
    [InlineData(Language.En, "System and storage checks", "pcmi check", "--elevate", "--code")]
    [InlineData(Language.Ja, "システム・ストレージの確認", "pcmi check", "--elevate", "--code")]
    public async Task Help_and_usage_name_the_check_verbs_and_the_code_option(Language language, string checkLine, string usage, string elevate, string code)
    {
        var help = await CliRun.RunAsync(["help"], language: language);
        Assert.Equal(0, help.ExitCode);
        Assert.Contains(checkLine, help.Out, StringComparison.Ordinal);
        Assert.Contains("check", help.Out, StringComparison.Ordinal);

        var check = Assert.Single(CliApp.Commands, c => c.Name == "check");
        Assert.StartsWith(usage, check.Usage, StringComparison.Ordinal);
        foreach (var verb in new[] { "system", "storage", "history" })
        {
            Assert.Contains(verb, check.Usage, StringComparison.Ordinal);
        }

        Assert.Contains(elevate, check.Usage, StringComparison.Ordinal);
        Assert.Equal(["json", "elevate"], check.Options.Select(o => o.Name).ToArray());

        var parts = Assert.Single(CliApp.Commands, c => c.Name == "parts");
        Assert.Contains(code, parts.Usage, StringComparison.Ordinal);
        Assert.Contains(parts.Options, o => o.Name == "code");

        var shown = await CliRun.RunAsync(["check", "--help"], language: language);
        Assert.Equal(0, shown.ExitCode);
        Assert.Contains("pcmi check system", shown.Out, StringComparison.Ordinal);
        var shownParts = await CliRun.RunAsync(["parts", "--help"], language: language);
        Assert.Contains("--code", shownParts.Out, StringComparison.Ordinal);
    }
}
