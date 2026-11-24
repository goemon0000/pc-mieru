using System.Reflection;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using PcMieru.Update.Transaction;

namespace PcMieru.Update.Tests;

/// <summary>
/// The two switches that exist only so that the tests give the same answer on every machine, and the proof that they cannot reach the product:
/// <list type="bullet">
/// <item><b>Forced elevation</b> (<c>dotnet test -p:PcmForceElevated=true</c>): a hosted CI runner runs elevated, so a test that reads the real token of the machine it runs on gave a different answer there (nine GUI tests,
/// 2026-11-22). Every program built from the Update project then says "elevated", and the whole suite is run that way on a machine that is not.</item>
/// <item><b>Durability off</b> (<see cref="TxSeams.Durable"/>): the fault matrix and the kill tests stop the transaction at named steps; flushing every file to the disk added nothing to what they prove and, on a disk with a slow
/// flush, took an hour (the same CI run lost its 20 minutes in the Update tests).</item>
/// </list>
/// </summary>
public sealed class TestEnvironmentTests
{
    private const string ExpectForced = "PCM_EXPECT_FORCED_ELEVATED";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcMieru.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("PcMieru.slnx not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> SourcesUnder(string folder, params string[] patterns)
    {
        var scratch = Path.Combine(RepoRoot(), ".testtmp");
        return new FileSystemEnumerable<string>(folder, static (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false })
        {
            // Other test classes create and remove fixtures concurrently. Keep the source scan out of the worktree's scratch directory.
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                !entry.FileName.Equals("obj", StringComparison.OrdinalIgnoreCase)
                && !entry.FileName.Equals("bin", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.ToFullPath(), scratch, StringComparison.OrdinalIgnoreCase),
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && MatchesAny(entry.FileName, patterns),
        };
    }

    private static bool MatchesAny(ReadOnlySpan<char> name, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------------------------------------------------------------------- forced elevation

    private static bool BuiltForced() =>
        typeof(ProcessElevation).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "PcmForcedElevation" && a.Value == "true");

    [Fact]
    public void A_run_that_is_meant_to_be_forced_is_forced_and_one_that_is_not_is_not()
    {
        // The run of the whole suite in the elevated state sets PCM_EXPECT_FORCED_ELEVATED=1 and builds with -p:PcmForceElevated=true. Both must agree: a forced build that is run without the variable (a leftover
        // of the last build) and a run that says "forced" on a normal build would each prove nothing, so each fails here.
        var expected = Environment.GetEnvironmentVariable(ExpectForced) == "1";
        Assert.True(BuiltForced() == expected, expected
            ? "PCM_EXPECT_FORCED_ELEVATED=1, but the build is not forced: build with -p:PcmForceElevated=true"
            : "the build is forced (-p:PcmForceElevated=true) but PCM_EXPECT_FORCED_ELEVATED=1 is not set: rebuild without the switch, or say that the run is a forced one");
        if (expected)
        {
            Assert.True(ProcessElevation.IsElevated(), "a forced build must answer 'elevated'");
        }
    }

    [Fact]
    public void Only_the_update_project_and_its_token_reader_use_the_forcing_and_the_publish_script_only_refuses_it()
    {
        var root = RepoRoot();
        foreach (var file in SourcesUnder(root, "*.cs", "*.csproj", "*.props", "*.targets", "*.ps1", "*.yml", "*.yaml", "*.json", "*.slnx", "*.sh", "*.cmd", "*.bat"))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith(".git/", StringComparison.Ordinal) || relative.StartsWith("tests/PcMieru.Update.Tests/", StringComparison.Ordinal)
                || relative == "tests/PcMieru.Gui.Tests/TestEnvironmentTests.cs")
            {
                continue; // this file, its neighbours and the one of the GUI tests (which checks that a forced run is a forced one) name the switch to test it
            }

            var text = File.ReadAllText(file);
            var allowed = relative is "src/PcMieru.Update/PcMieru.Update.csproj" or "src/PcMieru.Update/Transaction/ProcessElevation.cs" or "tools/publish.ps1";
            if (!allowed)
            {
                Assert.False(text.Contains("PcmForceElevated", StringComparison.Ordinal) || text.Contains("PCM_FORCE_ELEVATED", StringComparison.Ordinal),
                    $"{relative} names the forced-elevation switch: it belongs to the Update project, ProcessElevation.cs and the publish guard only");
            }
        }

        // The product reads the token in one place; only that file may branch on the symbol.
        var elevation = File.ReadAllText(Path.Combine(root, "src", "PcMieru.Update", "Transaction", "ProcessElevation.cs"));
        Assert.Single(Regex.Matches(elevation, @"^#if PCM_FORCE_ELEVATED\s*$", RegexOptions.Multiline));
    }

    [Fact]
    public void A_publish_with_the_forcing_stops_before_it_compiles()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PcMieru.Update", "PcMieru.Update.csproj"));
        var target = Regex.Match(csproj, @"<Target Name=""NoForcedElevationInTheProduct""[^>]*>", RegexOptions.Singleline);
        Assert.True(target.Success, "the guard target is missing");
        Assert.Contains("BeforeTargets=\"CoreCompile\"", target.Value, StringComparison.Ordinal);
        Assert.Contains("'$(PcmForceElevated)' == 'true'", target.Value, StringComparison.Ordinal);
        Assert.Contains("'$(_IsPublishing)' == 'true'", target.Value, StringComparison.Ordinal);
        Assert.Matches(@"<Target Name=""NoForcedElevationInTheProduct""[^>]*>\s*<Error ", csproj);
        // The symbol is defined only under the switch (never unconditionally).
        Assert.Matches(@"<PropertyGroup Condition=""'\$\(PcmForceElevated\)' == 'true'"">\s*<DefineConstants>\$\(DefineConstants\);PCM_FORCE_ELEVATED</DefineConstants>", csproj);

        // The script catches the evaluated symbol even when the Update project is a referenced build outside _IsPublishing.
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "publish.ps1"));
        var guard = script.IndexOf("-contains 'PCM_FORCE_ELEVATED'", StringComparison.Ordinal);
        var refused = script.IndexOf("throw 'PCM_FORCE_ELEVATED is defined in the product build.", StringComparison.Ordinal);
        var clean = script.IndexOf("Write-Step \"Clean $Output\"", StringComparison.Ordinal);
        Assert.Contains("-getProperty:DefineConstants", script, StringComparison.Ordinal);
        Assert.True(guard >= 0 && refused > guard && clean > refused, "the script must reject the evaluated test symbol before preparing the publish");
        Assert.DoesNotContain("PcmForceElevated", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:DefineConstants", script, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- durability

    [Fact]
    public void Every_seam_the_product_makes_is_durable_and_a_copy_keeps_what_it_was_given()
    {
        Assert.True(new TxSeams().Durable);
        Assert.True(TxSeams.Production().Durable);
        Assert.True(TxSeams.Production(launcher: null).With().Durable);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.True(new SwapHost { SelfPath = Path.Combine(Path.GetTempPath(), "PCMieru.exe"), Out = output, Error = error }.Seams.Durable);

        Assert.False(new TxSeams { Durable = false }.With().Durable);
        Assert.False(Rig.Seams().Durable, "the seams of the tests are the ones with the flush off");
    }

    [Fact]
    public void Nothing_in_the_product_turns_durability_off()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var files = SourcesUnder(src, "*.cs").ToArray();
        Assert.True(files.Length > 50, "the sources were not found");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var code = string.Join('\n', text.Split('\n').Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) || l.TrimStart().StartsWith("///", StringComparison.Ordinal) ? string.Empty : l));
            Assert.False(Regex.IsMatch(code, @"\bDurable\s*=\s*false\b|\bdurable\s*:\s*false\b|new\s+TxIo\s*\([^)]*,\s*false\s*\)|flushToDisk\s*:\s*false"),
                $"{Path.GetRelativePath(src, file)} turns durability off");
        }

        // The one place that decides what is flushed is TxIo; nothing there flushes with a fixed 'true' any more (a fixed true would make the switch a lie), and the move takes the same switch.
        var io = File.ReadAllText(Path.Combine(src, "PcMieru.Update", "Transaction", "TxIo.cs"));
        Assert.DoesNotContain("flushToDisk: true", io, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(io, @"flushToDisk: Durable").Count);
        var moves = Regex.Matches(Regex.Replace(io, @"//[^\n]*", string.Empty), @"TxNative\.Move\([^;]*;").Select(m => m.Value).ToArray();
        Assert.True(moves.Length >= 5, "the renames of TxIo were not found");
        Assert.All(moves, m => Assert.EndsWith("Durable);", m, StringComparison.Ordinal));
    }

    [Fact]
    public void A_swap_with_the_real_flush_and_write_through_commits_too()
    {
        // The matrix and the kill tests run with the flush off. One whole swap and one recovery run with it on, so the durable path is still run by the tests.
        using var rig = Rig.Create("durable");
        var launcher = FakeLauncher.Ok();

        var result = rig.Swap(Rig.Seams(launcher: launcher, durable: true));

        Assert.Equal(TxOutcome.Committed, result.Outcome);
        rig.AssertTree(Generation.New, "after a durable commit");
        Assert.Equal(TxOutcome.NothingToDo, rig.Recover().Outcome);
    }

    [Fact]
    public void A_durable_swap_that_a_launch_failure_rolls_back_ends_whole_too()
    {
        using var rig = Rig.Create("durable-back");

        var result = rig.Swap(Rig.Seams(launcher: FakeLauncher.AlwaysFail(), durable: true));

        Assert.Equal(TxOutcome.RolledBack, result.Outcome);
        rig.AssertTree(Generation.Old, "after a durable rollback");
    }
}
