using System.Text;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Tests;

/// <summary>
/// Which copy of PCMieru.Swap.exe may do a swap (DESIGN-B 5 and 13 item 3): ONLY <c>I\update\recover.exe</c>. The installed <c>I\PCMieru.Swap.exe</c> is the file a swap replaces, and a running exe cannot be replaced,
/// so it may only hand over. These tests pin that rule (the fifth mutation of step 4 lets <see cref="RunnerRules.Decide"/> accept the installed Swap.exe as the runner, and this class must fail).
/// </summary>
public sealed class RunnerRulesTests
{
    private static readonly string Install = Path.Combine(Path.GetTempPath(), "pcm-rules", "PC Mieru");

    private static string Recover => Path.Combine(Install, "update", "recover.exe");

    private static string Swap => Path.Combine(Install, "PCMieru.Swap.exe");

    [Fact]
    public void The_recover_exe_in_the_update_folder_is_the_runner()
    {
        Assert.Equal(RunnerRole.Runner, RunnerRules.Decide(Recover, Install));
    }

    [Fact]
    public void The_installed_swap_exe_is_never_the_runner_it_is_the_launcher()
    {
        // The rule the whole design stands on: a swap replaces I\PCMieru.Swap.exe, and Windows will not replace an exe that runs.
        Assert.Equal(RunnerRole.Launcher, RunnerRules.Decide(Swap, Install));
        Assert.NotEqual(RunnerRole.Runner, RunnerRules.Decide(Swap, Install));
    }

    [Theory]
    [InlineData("PCMieru.exe")]
    [InlineData("other.exe")]
    [InlineData("update\\PCMieru.Swap.exe")]
    [InlineData("update\\other.exe")]
    [InlineData("recover.exe")]
    [InlineData("sub\\PCMieru.Swap.exe")]
    [InlineData("update\\sub\\recover.exe")]
    public void Any_other_file_is_refused(string relative)
    {
        Assert.Equal(RunnerRole.Refused, RunnerRules.Decide(Path.Combine(Install, relative), Install));
    }

    [Fact]
    public void The_file_names_are_compared_without_regard_to_case_and_a_dotted_path_is_resolved_first()
    {
        Assert.Equal(RunnerRole.Runner, RunnerRules.Decide(Path.Combine(Install, "UPDATE", "RECOVER.EXE"), Install));
        Assert.Equal(RunnerRole.Launcher, RunnerRules.Decide(Path.Combine(Install, "pcmieru.swap.EXE"), Install));
        Assert.Equal(RunnerRole.Runner, RunnerRules.Decide(Path.Combine(Install, "update", "x", "..", "recover.exe"), Install));
    }

    [Fact]
    public void Locate_finds_the_install_folder_from_the_path_of_the_exe()
    {
        Assert.Equal(new RunnerSite(RunnerRole.Runner, Install), RunnerRules.Locate(Recover));
        Assert.Equal(new RunnerSite(RunnerRole.Launcher, Install), RunnerRules.Locate(Swap));
        Assert.Equal(new RunnerSite(RunnerRole.Runner, Install), RunnerRules.Locate(Path.Combine(Install, "UPDATE", "Recover.EXE")));
    }

    [Theory]
    [InlineData("recover.exe")]
    [InlineData("other.exe")]
    [InlineData("PCMieru.exe")]
    [InlineData("update\\other.exe")]
    [InlineData("update\\sub\\recover.exe")]
    public void Locate_refuses_a_file_that_is_neither(string relative)
    {
        Assert.Equal(RunnerRole.Refused, RunnerRules.Locate(Path.Combine(Install, relative)).Role);
    }

    [Fact]
    public void A_recover_exe_that_is_not_in_a_folder_called_update_is_not_a_runner()
    {
        Assert.Equal(RunnerRole.Refused, RunnerRules.Locate(Path.Combine(Install, "elsewhere", "recover.exe")).Role);
    }

    [Fact]
    public void The_rule_is_the_one_file_that_the_source_of_the_decision_names()
    {
        // The test above passes for a Decide that returns Runner for recover.exe only. This one reads the source and fails for a Decide that names the installed Swap.exe in a Runner branch.
        var source = File.ReadAllText(FindSource("RunnerPlacement.cs"), Encoding.UTF8);
        var start = source.IndexOf("public static RunnerRole Decide", StringComparison.Ordinal);
        var end = source.IndexOf("public static RunnerSite Locate", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "RunnerRules.Decide not found in the source");
        var decide = source[start..end];
        Assert.Equal(1, CountOf(decide, "RunnerRole.Runner"));
        Assert.Contains("paths.Recover", decide, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string what)
    {
        var count = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string FindSource(string name)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "src", "PcMieru.Update", "Transaction", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("the source file " + name + " was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_update_folder_name_is_the_one_the_gate_uses()
    {
        Assert.Equal(GateFile.UpdateFolderName, Path.GetFileName(Path.GetDirectoryName(Recover)));
        Assert.Equal(RunnerRules.RecoverFileName, Path.GetFileName(Recover));
    }
}
