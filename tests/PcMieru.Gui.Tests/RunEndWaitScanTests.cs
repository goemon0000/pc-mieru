using System.Text.RegularExpressions;

namespace PcMieru.Gui.Tests;

/// <summary>
/// A wait that ends on <c>!controller.IsBusy</c> alone can end before the UI has been told the run is over (the controller clears the active
/// run first and posts "changed" and "run finished" after), which was the flake of the state line that said "Running". Every wait of the
/// tests ends on the delivered end of the run instead (<see cref="RunEnd"/>). This scan keeps it so: a wait on the flag, bare or inside a
/// longer condition, fails the build of the tests, with the file and the line.
/// </summary>
public class RunEndWaitScanTests
{
    // [U]ntil, not Until: the pattern's own text in this file must not read as a wait when the folder is scanned. The condition may run over
    // several lines (it stops at the end of the statement), and the flag is taken with or without the objects before it.
    private static readonly Regex IdleFlagWait = new(@"[U]ntil\([^;]*?!\s*[\w.]*\bIsBusy\b", RegexOptions.CultureInvariant);

    private static string TestsFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcMieru.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "PcMieru.Gui.Tests");
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    [Fact]
    public void No_wait_in_the_gui_tests_ends_on_the_idle_flag_alone()
    {
        var folder = TestsFolder();
        var separator = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(separator + "bin" + separator, StringComparison.OrdinalIgnoreCase) && !path.Contains(separator + "obj" + separator, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A scan that read nothing would find nothing: the folder is known to hold the tests of the checks page.
        Assert.True(files.Count >= 10, "Only " + files.Count + " test files were found under " + folder);
        Assert.Contains(files, path => path.EndsWith("ChecksPageTests.cs", StringComparison.Ordinal));

        var found = new List<string>();
        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            foreach (Match match in IdleFlagWait.Matches(text))
            {
                found.Add(Path.GetFileName(path) + ":" + LineOf(text, match.Index) + " " + match.Value.ReplaceLineEndings(" "));
            }
        }

        Assert.True(found.Count == 0, "These waits end on the idle flag alone; wait for the delivered end of the run (RunEnd.UntilRunEnded, rig.UntilRunEnded or RunEnded) instead:\n" + string.Join("\n", found));
    }

    [Fact]
    public void The_scan_sees_the_idle_flag_wait_in_every_shape_and_not_the_delivered_end()
    {
        // The samples are put together from parts so that this file does not hold them as one literal (it would be scanned too).
        const string wait = "ui.Un" + "til(";
        Assert.Matches(IdleFlagWait, wait + "() => !rig.Controller.IsBusy)");
        Assert.Matches(IdleFlagWait, wait + "() => !controller.IsBusy)");
        Assert.Matches(IdleFlagWait, wait + "() => first.Started.IsCompleted && !rig.Checks.IsBusy)");
        Assert.Matches(IdleFlagWait, wait + "() => recorder.Requests.Count == 1\n                && !rig.Checks.IsBusy)");
        Assert.Matches(IdleFlagWait, wait + "() => ! rig.Checks.IsBusy)");

        Assert.DoesNotMatch(IdleFlagWait, wait + "() => rig.Checks.RunEnded())");
        Assert.DoesNotMatch(IdleFlagWait, wait + "() => first.Started.IsCompleted && rig.Checks.RunEnded(2))");
        Assert.DoesNotMatch(IdleFlagWait, "Assert.False(rig.Checks.IsBusy);");
        Assert.DoesNotMatch(IdleFlagWait, wait + "() => runner.Started.IsCompleted);\n Assert.True(!rig.Checks.IsBusy);");
        Assert.DoesNotMatch(IdleFlagWait, wait + "() => rig.Checks.IsBusy)");
        Assert.DoesNotMatch(IdleFlagWait, "[U]ntil\\(");
    }
}
