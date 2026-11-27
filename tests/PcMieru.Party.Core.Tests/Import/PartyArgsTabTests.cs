using PcMieru.Party.Ui;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>
/// <c>--tab</c> of PCMieru.Party.exe (PartyArgs.cs is compiled into this project). The Startup tab is not shown in Party by design
/// (the sign-in setting lives in PCMieru's settings, DESIGN-C section 3 item 5), so a shot of it would only show an empty panel: it is refused.
/// </summary>
public class PartyArgsTabTests
{
    private static readonly string Shot = Path.Combine(Path.GetTempPath(), "party-args-tab-never-written.png");

    [Theory]
    [InlineData("view", SettingsTab.View)]
    [InlineData("checks", SettingsTab.Checks)]
    [InlineData("data", SettingsTab.Data)]
    [InlineData("DATA", SettingsTab.Data)]
    public void The_tabs_that_Party_shows_are_accepted(string value, SettingsTab expected)
    {
        Assert.True(PartyArgs.TryParse(["--smoke", "--shot", Shot, "--tab", value], out var options, out var problem), problem);
        Assert.Equal(expected, options.ShotTab);
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("Startup")]
    [InlineData("STARTUP")]
    public void The_startup_tab_is_refused_and_the_message_lists_the_ones_that_work(string value)
    {
        Assert.False(PartyArgs.TryParse(["--smoke", "--shot", Shot, "--tab", value], out var options, out var problem));
        Assert.Null(options.ShotTab);
        Assert.Contains("view", problem);
        Assert.Contains("checks", problem);
        Assert.Contains("data", problem);
        Assert.Contains("not shown in Party", problem);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    public void An_unknown_tab_is_refused_with_the_same_list(string value)
    {
        Assert.False(PartyArgs.TryParse(["--smoke", "--shot", Shot, "--tab", value], out _, out var problem));
        Assert.Contains("view, checks or data", problem);
    }

    [Fact]
    public void A_missing_value_is_refused()
    {
        Assert.False(PartyArgs.TryParse(["--smoke", "--shot", Shot, "--tab"], out _, out var problem));
        Assert.Contains("view, checks or data", problem);
    }

    [Fact]
    public void A_tab_still_needs_a_shot()
    {
        Assert.False(PartyArgs.TryParse(["--smoke", "--tab", "data"], out _, out var problem));
        Assert.Contains("--shot", problem);
    }
}
