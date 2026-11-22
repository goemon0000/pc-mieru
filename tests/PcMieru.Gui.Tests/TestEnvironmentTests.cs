using System.Reflection;
using System.Text.RegularExpressions;
using PcMieru.Update.Transaction;

namespace PcMieru.Gui.Tests;

/// <summary>
/// A test must not change its outcome with the token of the machine it runs on. A hosted CI runner is elevated, a developer's machine is not, and nine tests of the gate window and the settings page read the real token
/// and failed only there (2026-11-22). Three things keep it from coming back:
/// <list type="number">
/// <item>the scans below: a test that builds a window, a recovery or a placement check without giving the answer to "is this process elevated" fails here, naming the file and the line;</item>
/// <item>the forced run: the whole suite is run with <c>-p:PcmForceElevated=true</c> (every program built from the Update project then says "elevated", see <c>ProcessElevation</c>) and
/// <c>PCM_EXPECT_FORCED_ELEVATED=1</c>, and this file fails when the build and the variable disagree, so a forced run cannot silently be a normal one;</item>
/// <item>the tests that ARE about elevation say so with an explicit true (the gate window, the real recovery, the placement of the page).</item>
/// </list>
/// </summary>
public sealed class TestEnvironmentTests
{
    private static IEnumerable<string> TestSources()
    {
        var folder = Path.Combine(Harness.Root, "tests", "PcMieru.Gui.Tests");
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(separator + "bin" + separator, StringComparison.OrdinalIgnoreCase) && !path.Contains(separator + "obj" + separator, StringComparison.OrdinalIgnoreCase))
            .Where(path => !string.Equals(Path.GetFileName(path), "TestEnvironmentTests.cs", StringComparison.Ordinal));
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    // [S]tartGateWindow and the others: the pattern's own text must not read as a hit when the folder is scanned.
    private static void NoStatementLacks(Regex statement, Regex mustHave, string why)
    {
        var sources = TestSources().ToArray();
        Assert.True(sources.Length > 20, "the test sources were not found");
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            foreach (Match hit in statement.Matches(text))
            {
                Assert.True(mustHave.IsMatch(hit.Value), $"{Path.GetFileName(file)}:{LineOf(text, hit.Index)}: {why}\n    {hit.Value.Trim()}");
            }
        }
    }

    [Fact]
    public void A_gate_window_in_a_test_is_always_given_a_recovery_that_says_whether_the_process_is_elevated()
    {
        // Without one the window takes the real recovery, which asks the real token.
        NoStatementLacks(new Regex(@"new [S]tartGateWindow\([^;]*;", RegexOptions.CultureInvariant), new Regex(@"NotElevated\(\)|[rR]ecovery", RegexOptions.CultureInvariant),
            "a StartGateWindow without a recovery reads the token of the machine the test runs on (use NotElevated(), or a FakeRecovery that says elevated: true)");
    }

    [Fact]
    public void A_real_recovery_in_a_test_is_always_given_its_answer_about_elevation()
    {
        NoStatementLacks(new Regex(@"new [R]unnerGateRecovery\([^;]*;", RegexOptions.CultureInvariant), new Regex(@"isElevated\s*:", RegexOptions.CultureInvariant),
            "a RunnerGateRecovery without isElevated: reads the real token");
    }

    [Fact]
    public void A_placement_check_in_a_test_is_always_given_its_three_answers()
    {
        // The one-argument form reads the real token (and the real drive); a test gives all of them, or uses a fake placement.
        var single = new Regex(@"[I]nstallPlacement\.Check\(\s*[\w.]+\s*\)", RegexOptions.CultureInvariant);
        foreach (var file in TestSources())
        {
            var text = File.ReadAllText(file);
            var hit = single.Match(text);
            Assert.False(hit.Success, $"{Path.GetFileName(file)}:{(hit.Success ? LineOf(text, hit.Index) : 0)}: InstallPlacement.Check(folder) reads the real token: give isElevated, the drive type and the format");
        }
    }

    [Fact]
    public void No_test_of_the_window_reads_the_token_itself()
    {
        var read = new Regex(@"[P]rocessElevation\.|[E]levationCheck\b|[S]hellActions\.IsElevated|[W]indowsPrincipal", RegexOptions.CultureInvariant);
        foreach (var file in TestSources())
        {
            var text = File.ReadAllText(file);
            var hit = read.Match(text);
            Assert.False(hit.Success, $"{Path.GetFileName(file)}:{(hit.Success ? LineOf(text, hit.Index) : 0)}: reads who the process is: a test that needs the answer gets it from a seam");
        }
    }

    [Fact]
    public void A_run_that_is_meant_to_be_forced_is_forced_and_one_that_is_not_is_not()
    {
        var built = typeof(ProcessElevation).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "PcmForcedElevation" && a.Value == "true");
        var expected = Environment.GetEnvironmentVariable("PCM_EXPECT_FORCED_ELEVATED") == "1";
        Assert.True(built == expected, expected
            ? "PCM_EXPECT_FORCED_ELEVATED=1, but the build is not forced: build with -p:PcmForceElevated=true"
            : "the build is forced (-p:PcmForceElevated=true) but PCM_EXPECT_FORCED_ELEVATED=1 is not set: rebuild without the switch, or say that the run is a forced one");
        if (expected)
        {
            Assert.True(ProcessElevation.IsElevated(), "a forced build must answer 'elevated'");
        }
    }
}
