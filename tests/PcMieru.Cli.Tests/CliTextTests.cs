namespace PcMieru.Cli.Tests;

public class CliTextTests
{
    [Theory]
    [InlineData("abc", 3)]
    [InlineData("", 0)]
    [InlineData("日本語", 6)]
    [InlineData("ＡＢＣ", 6)]
    [InlineData("ｱｲ", 2)]
    [InlineData("a日b", 4)]
    [InlineData("a\u0007b", 2)]
    public void Width_counts_terminal_cells(string text, int expected) => Assert.Equal(expected, CliText.Width(text));

    [Fact]
    public void Truncate_keeps_short_text_and_cuts_long_text_within_the_width()
    {
        Assert.Equal("abc", CliText.Truncate("abc", 3));
        var cut = CliText.Truncate("abcdefghij", 5);
        Assert.Equal(5, CliText.Width(cut));
        Assert.EndsWith("…", cut, StringComparison.Ordinal);

        var wide = CliText.Truncate("日本語テキスト", 7);
        Assert.True(CliText.Width(wide) <= 7, wide);
        Assert.EndsWith("…", wide, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncate_never_splits_a_surrogate_pair()
    {
        var pair = char.ConvertFromUtf32(0x20BB7);
        var cut = CliText.Truncate("a" + pair + pair + pair + pair, 4);
        Assert.True(CliText.Width(cut) <= 4);
        Assert.DoesNotContain((char)0xFFFD, cut);
        Assert.All(cut.Where(char.IsSurrogate).Chunk(2), chunk => Assert.True(chunk.Length == 2 && char.IsHighSurrogate(chunk[0]) && char.IsLowSurrogate(chunk[1])));
    }

    [Fact]
    public void Printable_replaces_control_characters_so_a_name_cannot_move_the_cursor()
    {
        Assert.Equal("abc", CliText.Printable("abc"));
        var cleaned = CliText.Printable("a\nb\r\u001b[31mred\tz");
        Assert.DoesNotContain(cleaned, char.IsControl);
        Assert.StartsWith("a?b?", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Padding_uses_display_width_not_length()
    {
        Assert.Equal("日本  ", CliText.PadRight("日本", 6));
        Assert.Equal("  日本", CliText.PadLeft("日本", 6));
        Assert.Equal("abcdef", CliText.PadRight("abcdef", 3));
    }

    [Fact]
    public void Num_ignores_the_machine_culture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.5", CliText.Num(1.5));
            Assert.Equal("1234.50", CliText.Num(1234.5, "0.00"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }
}
