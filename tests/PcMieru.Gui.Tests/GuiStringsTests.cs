using System.Text.RegularExpressions;

namespace PcMieru.Gui.Tests;

public partial class GuiStringsTests
{
    [GeneratedRegex("\"(gui\\.[a-z0-9_]+(?:\\.[a-z0-9_]+)*\\.?)\"")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex("\\{([a-zA-Z0-9_]+)\\}")]
    private static partial Regex Placeholder();

    private static readonly HashSet<string> Registered = GuiStrings.All.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Keys_are_unique_and_owned_by_gui()
    {
        Assert.Equal(GuiStrings.All.Length, Registered.Count);
        Assert.All(GuiStrings.All, e => Assert.StartsWith("gui.", e.Key, StringComparison.Ordinal));
    }

    [Fact]
    public void Both_languages_have_text_and_use_the_same_placeholders()
    {
        foreach (var entry in GuiStrings.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Ja), entry.Key + " ja");
            Assert.False(string.IsNullOrWhiteSpace(entry.En), entry.Key + " en");
            Assert.Equal(PlaceholdersOf(entry.Ja), PlaceholdersOf(entry.En));
        }
    }

    [Fact]
    public void Registering_twice_is_harmless()
    {
        Strings.Register(GuiStrings.All);
        Strings.Register(GuiStrings.All);
        Assert.Equal("Processes", Strings.Get("gui.nav.processes", Language.En));
    }

    [Fact]
    public void Every_gui_key_written_in_the_source_is_registered()
    {
        var files = Directory.GetFiles(Harness.GuiSource, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && Path.GetFileName(f) != "GuiStrings.cs")
            .ToArray();
        Assert.NotEmpty(files);

        var problems = new List<string>();
        var seen = 0;
        foreach (var file in files)
        {
            foreach (Match match in KeyLiteral().Matches(File.ReadAllText(file)))
            {
                seen++;
                var key = match.Groups[1].Value;
                if (key == "gui.ini")
                {
                    continue; // the settings file name, not a string key
                }

                var known = key.EndsWith('.')
                    ? Registered.Any(r => r.StartsWith(key, StringComparison.Ordinal))
                    : Registered.Contains(key);
                if (!known)
                {
                    problems.Add(Path.GetFileName(file) + ": " + key);
                }
            }
        }

        Assert.True(seen > 20, "the scan found only " + seen + " keys; the pattern is probably broken");
        Assert.Empty(problems);
    }

    [Fact]
    public void Texts_built_from_an_enum_exist_for_every_member()
    {
        using var _ = Harness.Locale();
        Check("gui.proc.state.", Enum.GetValues<ProcessState>());
        Check("gui.svc.state.", Enum.GetValues<ServiceState>());
        Check("gui.svc.start.", Enum.GetValues<ServiceStartType>());
        Check("gui.startup.state.", Enum.GetValues<StartupState>());
        Check("gui.overview.certainty.", Enum.GetValues<ObservationCertainty>());
    }

    [Fact]
    public void A_missing_key_shows_as_a_bracketed_placeholder_which_the_smoke_run_looks_for()
    {
        Assert.True(SmokeText.IsPlaceholder(Strings.Get("gui.no.such.key")));
        Assert.False(SmokeText.IsPlaceholder(Strings.Get("gui.nav.processes", Language.En)));
    }

    private static void Check<T>(string prefix, T[] members)
        where T : struct, Enum
    {
        foreach (var member in members)
        {
            var key = prefix + member.ToString().ToLowerInvariant();
            foreach (var language in new[] { Language.Ja, Language.En })
            {
                var text = Strings.Get(key, language);
                Assert.NotEqual("[" + key + "]", text);
            }
        }
    }

    private static string[] PlaceholdersOf(string text) =>
        [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal)];
}
