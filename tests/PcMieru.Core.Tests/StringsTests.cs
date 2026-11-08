namespace PcMieru.Core.Tests;

public class StringsTests
{
    private static PcMieruServices Services() => PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });

    [Fact]
    public void Every_registered_string_has_both_languages_with_the_same_placeholders()
    {
        using var _ = Services();
        var all = Strings.All;
        Assert.True(all.Count > 10);
        foreach (var entry in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Ja), entry.Key + " ja");
            Assert.False(string.IsNullOrWhiteSpace(entry.En), entry.Key + " en");
            Assert.Equal(Strings.Placeholders(entry.Ja), Strings.Placeholders(entry.En));
        }
    }

    [Fact]
    public void Keys_carry_their_owner_prefix()
    {
        using var _ = Services();
        string[] prefixes = ["core.", "collection.", "gui.", "hw.", "mgmt.", "cli.", "report.", "obs.", "mcp."];
        Assert.All(Strings.All, e => Assert.Contains(prefixes, p => e.Key.StartsWith(p, StringComparison.Ordinal)));
    }

    [Fact]
    public void Format_fills_placeholders_and_leaves_unknown_ones_visible()
    {
        Strings.Register([new("core.test.format", "{a} と {b}", "{a} and {b}")]);
        Assert.Equal("1 and {b}", Strings.Format("core.test.format", new Dictionary<string, string> { ["a"] = "1" }, Language.En));
        Assert.Equal("1 と 2", Strings.Format("core.test.format", new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }, Language.Ja));
    }

    [Fact]
    public void Missing_key_is_shown_in_brackets_not_hidden()
    {
        Assert.Equal("[core.nope]", Strings.Get("core.nope", Language.En));
    }

    [Fact]
    public void Registering_the_same_key_twice_is_fine_but_a_different_text_throws()
    {
        var entry = new StringEntry("core.test.dup", "あ", "a");
        Strings.Register([entry]);
        Strings.Register([entry]);
        Assert.Throws<InvalidOperationException>(() => Strings.Register([entry with { En = "b" }]));
    }

    [Theory]
    [InlineData(ValueStatus.Denied)]
    [InlineData(ValueStatus.NotCollected)]
    [InlineData(ValueStatus.Unsupported)]
    [InlineData(ValueStatus.Gone)]
    public void Every_value_status_has_a_display_text(ValueStatus status)
    {
        Assert.DoesNotContain("[", Strings.Get("core.status." + char.ToLowerInvariant(status.ToString()[0]) + status.ToString()[1..], Language.En), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(Strings.Status(status)));
    }
}
