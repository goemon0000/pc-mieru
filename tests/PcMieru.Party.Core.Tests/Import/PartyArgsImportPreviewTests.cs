namespace PcMieru.Party.Core.Tests.Import;

/// <summary>The command line of PCMieru.Party.exe (PartyArgs.cs is compiled into this project): <c>--import-preview</c> and what it needs.</summary>
public class PartyArgsImportPreviewTests
{
    private static readonly string Shot = Path.Combine(Path.GetTempPath(), "party-args-test-never-written.png");

    [Theory]
    [InlineData("ready", "Ready")]
    [InlineData("repair", "Repair")]
    [InlineData("oldapp", "OldApp")]
    [InlineData("hold", "Hold")]
    [InlineData("HOLD", "Hold")]
    public void Each_case_is_accepted_together_with_smoke(string value, string expected)
    {
        Assert.True(PartyArgs.TryParse(["--smoke", "--import-preview", value], out var options, out var problem), problem);
        Assert.Equal(expected, options.ImportPreview?.ToString());
        Assert.True(options.Smoke);
    }

    [Fact]
    public void It_can_be_given_before_smoke_and_with_a_shot_and_a_language()
    {
        Assert.True(PartyArgs.TryParse(["--import-preview", "repair", "--smoke", "--lang", "en", "--shot", Shot], out var options, out var problem), problem);
        Assert.Equal(PartyImportPreview.Repair, options.ImportPreview);
        Assert.Equal(PartyLanguage.En, options.Language);
        Assert.Equal(Shot, options.ShotPath);
    }

    [Fact]
    public void Without_smoke_it_is_refused()
    {
        Assert.False(PartyArgs.TryParse(["--import-preview", "ready"], out _, out var problem));
        Assert.Contains("--smoke", problem);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData("ready ")]
    public void An_unknown_value_is_refused(string value)
    {
        Assert.False(PartyArgs.TryParse(["--smoke", "--import-preview", value], out _, out var problem));
        Assert.Contains("--import-preview", problem);
    }

    [Fact]
    public void A_missing_value_is_refused()
    {
        Assert.False(PartyArgs.TryParse(["--smoke", "--import-preview"], out _, out var problem));
        Assert.Contains("--import-preview", problem);
    }

    [Fact]
    public void A_normal_command_line_has_no_preview()
    {
        Assert.True(PartyArgs.TryParse(["--smoke"], out var options, out _));
        Assert.Null(options.ImportPreview);
        Assert.True(PartyArgs.TryParse([], out var plain, out _));
        Assert.Null(plain.ImportPreview);
    }
}
