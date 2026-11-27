namespace PcMieru.Party.Core.Tests.Import;

/// <summary>The command line of PCMieru.Party.exe (PartyArgs.cs is compiled into this project): <c>--logon</c>, the argument of the Run value of the sign-in (DESIGN-C 3 item 5).</summary>
public class PartyArgsLogonTests
{
    [Fact]
    public void Logon_alone_is_accepted_and_is_the_only_thing_it_sets()
    {
        Assert.True(PartyArgs.TryParse(["--logon"], out var options, out var problem), problem);
        Assert.True(options.Logon);
        Assert.False(options.Smoke);
        Assert.Null(options.Language);
        Assert.Null(options.ShotPath);
        Assert.Null(options.ImportPreview);
    }

    [Fact]
    public void A_normal_command_line_is_not_a_logon_start()
    {
        Assert.True(PartyArgs.TryParse([], out var plain, out _));
        Assert.False(plain.Logon);
        Assert.True(PartyArgs.TryParse(["--lang", "en"], out var withLanguage, out _));
        Assert.False(withLanguage.Logon);
    }

    [Fact]
    public void Logon_can_come_with_a_language_in_either_order()
    {
        Assert.True(PartyArgs.TryParse(["--logon", "--lang", "ja"], out var first, out var problem1), problem1);
        Assert.True(first.Logon);
        Assert.Equal(PartyLanguage.Ja, first.Language);
        Assert.True(PartyArgs.TryParse(["--lang", "en", "--logon"], out var second, out var problem2), problem2);
        Assert.True(second.Logon);
        Assert.Equal(PartyLanguage.En, second.Language);
    }

    [Fact]
    public void Logon_together_with_smoke_is_refused_in_either_order()
    {
        Assert.False(PartyArgs.TryParse(["--logon", "--smoke"], out _, out var problem1));
        Assert.Contains("--logon", problem1);
        Assert.Contains("--smoke", problem1);
        Assert.False(PartyArgs.TryParse(["--smoke", "--logon"], out _, out var problem2));
        Assert.Contains("--logon", problem2);
    }

    [Fact]
    public void Logon_with_smoke_and_a_shot_or_a_preview_is_refused_too()
    {
        var shot = Path.Combine(Path.GetTempPath(), "party-args-logon-never-written.png");
        Assert.False(PartyArgs.TryParse(["--smoke", "--shot", shot, "--logon"], out _, out _));
        Assert.False(PartyArgs.TryParse(["--smoke", "--import-preview", "ready", "--logon"], out _, out _));
    }

    [Fact]
    public void The_argument_the_setting_writes_is_the_one_the_parser_takes()
    {
        // PartyLogon.Argument is what goes into the Run value (Core); the Party exe must parse exactly that.
        Assert.Equal("--logon", PcMieru.Core.Siblings.PartyLogon.Argument);
        Assert.True(PartyArgs.TryParse([PcMieru.Core.Siblings.PartyLogon.Argument], out var options, out var problem), problem);
        Assert.True(options.Logon);
    }
}
