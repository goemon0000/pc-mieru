using PcMieru.Party.Game;
using PcMieru.Party.Import;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>The measuring start of PCMieru.Party.exe (PartyArgs.cs and PartyMeasure.cs are compiled into this project): <c>--measure &lt;dir&gt;</c>, DESIGN-C 7 and 8 step 7.</summary>
public class PartyArgsMeasureTests
{
    private static string Dir => Path.Combine(Path.GetTempPath(), "party-args-measure-never-created");

    [Fact]
    public void Measure_with_an_absolute_folder_is_accepted_and_sets_only_the_folder()
    {
        Assert.True(PartyArgs.TryParse(["--measure", Dir], out var options, out var problem), problem);
        Assert.Equal(Dir, options.MeasureDir);
        Assert.False(options.Smoke);
        Assert.False(options.Logon);
        Assert.Null(options.ShotPath);
        Assert.Null(options.ImportPreview);
        Assert.True(PartyArgs.TryParse(["--lang", "en", "--measure", Dir], out var withLanguage, out _));
        Assert.Equal(PartyLanguage.En, withLanguage.Language);
        Assert.True(PartyArgs.TryParse([], out var plain, out _));
        Assert.Null(plain.MeasureDir);
    }

    [Theory]
    [InlineData("relative\\folder")]
    [InlineData("folder")]
    [InlineData("C:folder")]
    [InlineData("\\rooted-without-drive")]
    [InlineData("")]
    public void Measure_needs_a_fully_qualified_folder(string folder)
    {
        Assert.False(PartyArgs.TryParse(["--measure", folder], out _, out var problem));
        Assert.Contains("--measure", problem);
    }

    [Fact]
    public void Measure_without_a_value_is_refused()
    {
        Assert.False(PartyArgs.TryParse(["--measure"], out _, out var problem));
        Assert.Contains("--measure", problem);
    }

    [Fact]
    public void Measure_together_with_smoke_is_refused_in_either_order()
    {
        Assert.False(PartyArgs.TryParse(["--measure", Dir, "--smoke"], out _, out var p1));
        Assert.Contains("--smoke", p1);
        Assert.False(PartyArgs.TryParse(["--smoke", "--measure", Dir], out _, out var p2));
        Assert.Contains("--measure", p2);
    }

    [Fact]
    public void Measure_together_with_logon_is_refused_in_either_order()
    {
        Assert.False(PartyArgs.TryParse(["--measure", Dir, "--logon"], out _, out var p1));
        Assert.Contains("--logon", p1);
        Assert.False(PartyArgs.TryParse(["--logon", "--measure", Dir], out _, out var p2));
        Assert.Contains("--measure", p2);
    }

    [Fact]
    public void Measure_together_with_a_shot_is_refused_with_or_without_smoke()
    {
        var shot = Path.Combine(Path.GetTempPath(), "party-args-measure-never-written.png");
        Assert.False(PartyArgs.TryParse(["--measure", Dir, "--shot", shot], out _, out var p1));
        Assert.Contains("--measure", p1);
        Assert.False(PartyArgs.TryParse(["--smoke", "--shot", shot, "--measure", Dir], out _, out var p2));
        Assert.Contains("--measure", p2);
    }

    [Fact]
    public void Measure_together_with_a_tab_is_refused()
    {
        Assert.False(PartyArgs.TryParse(["--measure", Dir, "--tab", "view"], out _, out var p1));
        Assert.Contains("--tab", p1);
        Assert.False(PartyArgs.TryParse(["--tab", "data", "--measure", Dir], out _, out var p2));
        Assert.Contains("--measure", p2);
    }

    [Fact]
    public void Measure_together_with_an_import_preview_is_refused()
    {
        Assert.False(PartyArgs.TryParse(["--measure", Dir, "--import-preview", "ready"], out _, out var p1));
        Assert.Contains("--import-preview", p1);
        Assert.False(PartyArgs.TryParse(["--import-preview", "hold", "--measure", Dir], out _, out var p2));
        Assert.Contains("--measure", p2);
    }

    [Fact]
    public void The_measuring_resident_mutex_is_not_the_real_one_for_any_user()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        Assert.NotEqual(PartyMutexNames.ResidentNameFor(sid), PartyMeasure.ResidentNameFor(sid));
        Assert.StartsWith(PartyMutexNames.ResidentNameFor(sid), PartyMeasure.ResidentNameFor(sid), StringComparison.Ordinal);
        Assert.EndsWith(PartyMeasure.MutexSuffix, PartyMeasure.ResidentNameFor(sid), StringComparison.Ordinal);
        Assert.NotEqual(PartyMutexNames.CurrentResidentName(), PartyMeasure.ResidentName());
    }

    [Fact]
    public void The_state_of_a_measuring_start_is_only_inside_the_given_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "party-measure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(Path.Combine(dir, "state.json"), PartyMeasure.StatePath(dir));
            var state = PartyState.Load(dir);   // what Program.RunMeasure does: a folder with no state.json gives a new state
            Assert.Equal(PartyMeasure.StatePath(dir), state.SavePath);
            Assert.False(state.SaveBlocked);
            Assert.Equal(dir, Path.GetDirectoryName(state.SavePath));
            Assert.Empty(Directory.GetFileSystemEntries(dir));   // loading a missing state wrote nothing
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_first_value_file_is_one_line_of_whole_milliseconds_inside_the_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "party-measure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            PartyMeasure.WriteFirstValue(dir, 1234.56);
            var file = PartyMeasure.FirstValuePath(dir);
            Assert.Equal(dir, Path.GetDirectoryName(file));
            Assert.Equal("first-value.txt", Path.GetFileName(file));
            Assert.Equal("1235\n", File.ReadAllText(file));
            Assert.Single(Directory.GetFileSystemEntries(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
