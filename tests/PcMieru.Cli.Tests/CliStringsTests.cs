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
}
