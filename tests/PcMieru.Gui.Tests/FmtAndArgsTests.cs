namespace PcMieru.Gui.Tests;

public class FmtAndArgsTests
{
    [Fact]
    public void A_value_that_was_not_collected_is_a_dash_never_a_zero()
    {
        using var _ = Harness.Locale();
        Assert.Equal("-", Fmt.Percent(Value.NotCollected<double>("t")));
        Assert.Equal("-", Fmt.Mhz(Value.Denied<double>("t")));
        Assert.Equal("-", Fmt.Bytes(Value.Unsupported<long>("t")));
        Assert.Equal("-", Fmt.Rate(Value.Failed<double>("t")));
        Assert.Equal("-", Fmt.Rate(Value.NotCollected<double>("t"), Value.Gone<double>("t")));
        Assert.Equal("0.0 %", Fmt.Percent(Value.Ok(0.0, "t")));
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(104_857_600L, "100 MB")]
    [InlineData(4_617_089_843L, "4.3 GB")]
    [InlineData(long.MaxValue, "8388608 TB")]
    public void Bytes_scale_to_the_largest_unit_that_keeps_the_number_small(long bytes, string expected)
    {
        using var _ = Harness.Locale();
        Assert.Equal(expected, Fmt.Bytes(bytes));
    }

    [Fact]
    public void Rate_adds_read_and_write_and_survives_one_side_missing_or_negative()
    {
        using var _ = Harness.Locale();
        Assert.Equal("3.0 KB/s", Fmt.Rate(Value.Ok(1024.0, "t"), Value.Ok(2048.0, "t")));
        Assert.Equal("1.0 KB/s", Fmt.Rate(Value.Ok(1024.0, "t"), Value.NotCollected<double>("t")));
        Assert.Equal("0 B/s", Fmt.Rate(Value.Ok(-5.0, "t")));
    }

    [Theory]
    [InlineData(Language.En, 93_785L, "1 d 02:03:05")]
    [InlineData(Language.Ja, 93_785L, "1 日 02:03:05")]
    [InlineData(Language.En, -3L, "0 d 00:00:00")]
    public void Uptime_reads_in_the_current_language(Language language, long seconds, string expected)
    {
        using var _ = Harness.Locale(language);
        Assert.Equal(expected, Fmt.Uptime(seconds));
    }

    [Fact]
    public void No_arguments_means_a_normal_start()
    {
        Assert.True(GuiArgs.TryParse([], out var options, out _));
        Assert.Equal(new GuiOptions(null, null, null, null, null, false), options);
    }

    [Fact]
    public void Every_switch_is_read()
    {
        Assert.True(GuiArgs.TryParse(["--lang", "EN", "--theme", "dark", "--page", "hardware", "--smoke", "o.json", "--shots", "shots", "--fakes"], out var o, out var problem));
        Assert.Equal(string.Empty, problem);
        Assert.Equal(new GuiOptions(Language.En, ThemeMode.Dark, "hardware", "o.json", "shots", true), o);
    }

    [Theory]
    [InlineData("--lang")]
    [InlineData("--lang", "fr")]
    [InlineData("--theme", "blue")]
    [InlineData("--page")]
    [InlineData("--smoke")]
    [InlineData("--shots")]
    [InlineData("--nope")]
    [InlineData("stray")]
    public void A_bad_command_line_is_refused_with_a_reason(params string[] args)
    {
        Assert.False(GuiArgs.TryParse(args, out _, out var problem));
        Assert.NotEmpty(problem);
    }

    [Fact]
    public void Settings_round_trip_and_a_broken_file_falls_back_to_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcmieru-gui-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "gui.ini");
        try
        {
            new GuiSettings(path) { Language = Language.Ja, Theme = ThemeMode.Dark }.Save();
            var loaded = GuiSettings.Load(path);
            Assert.Equal(Language.Ja, loaded.Language);
            Assert.Equal(ThemeMode.Dark, loaded.Theme);

            File.WriteAllText(path, "language=klingon\ntheme=99\n=\ngarbage");
            var broken = GuiSettings.Load(path);
            Assert.Equal(ThemeMode.System, broken.Theme);
            Assert.True(Enum.IsDefined(broken.Language));

            Assert.Equal(ThemeMode.System, GuiSettings.Load(Path.Combine(dir, "missing.ini")).Theme);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Settings_without_a_path_never_touch_the_disk()
    {
        var settings = new GuiSettings { Theme = ThemeMode.Light };
        settings.Save();
        Assert.Equal(ThemeMode.Light, settings.Theme);
    }
}
