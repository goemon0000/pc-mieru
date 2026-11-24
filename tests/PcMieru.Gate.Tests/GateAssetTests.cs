using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PcMieru.Wire.Gate;

namespace PcMieru.Gate.Tests;

/// <summary>
/// The gate FILE as a thing that ships: the bytes in assets\ are the bytes fixed in the client, tools/publish.ps1 writes them into the publish folder, every exe project has the dev copy
/// beside its build, and every exe project has its call of the gate (a new exe that forgot the call is a hole that nothing else would show).
/// </summary>
public sealed class GateAssetTests
{
    private static readonly string[] ShippedExes = ["Gui", "Cli", "SensorHost", "Helper", "NetMeter"];

    [Fact]
    public void The_asset_is_exactly_the_bytes_fixed_in_the_client_and_its_hash_is_the_fixed_one()
    {
        var asset = File.ReadAllBytes(Path.Combine(Repo.Root, "assets", GateFile.FileName));

        Assert.Equal(GateFile.Bytes(), asset);
        Assert.Equal(GateFile.Sha256Hex, Convert.ToHexString(SHA256.HashData(asset)).ToLowerInvariant());
        Assert.Matches("^[0-9a-f]{64}$", GateFile.Sha256Hex);
    }

    [Fact]
    public void The_bytes_are_plain_utf8_with_lf_line_ends_and_one_final_lf()
    {
        var bytes = GateFile.Bytes();

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no byte order mark");
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^2]);
        Assert.Equal(GateFile.Text, new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes));
    }

    [Fact]
    public void The_text_says_what_it_is_in_japanese_and_in_english_and_has_no_version_no_date_and_no_number()
    {
        var text = GateFile.Text;

        Assert.Contains("start gate file", text, StringComparison.Ordinal);
        Assert.Contains("起動の門のファイル", text, StringComparison.Ordinal);
        // The same bytes in every release is the point of the file: nothing in it may change with a version, a year or a count.
        Assert.DoesNotMatch("[0-9]", text);
    }

    [Fact]
    public void The_last_sentence_of_each_language_says_what_to_do_to_repair_it_and_it_never_says_release()
    {
        // The text is frozen (the same bytes in every release): this is the wording that was decided, in the two languages.
        Assert.Contains("To repair it, download PC Mieru's ZIP file again and unzip it into a new folder.\n", GateFile.Text, StringComparison.Ordinal);
        Assert.Contains("直すには、PCミエルの ZIP をもう一度ダウンロードして、新しいフォルダーに展開してください。\n", GateFile.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("配布のZIP", GateFile.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("release", GateFile.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("リリース", GateFile.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sentence_a_console_program_writes_for_a_busy_gate_does_not_say_for_certain_that_an_update_is_running_and_none_blames_the_person()
    {
        Assert.Equal("PC Mieru's files are in use (an update being installed, for example); try again in a moment", InstallGate.ReasonOf(GateOutcome.Busy));
        foreach (var outcome in Enum.GetValues<GateOutcome>())
        {
            var reason = InstallGate.ReasonOf(outcome);
            Assert.False(reason.StartsWith("an update is being installed", StringComparison.Ordinal), reason);
            foreach (var blame in new[] { "you ", "your ", "deleted", "moved" })
            {
                Assert.DoesNotContain(blame, reason, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Theory]
    [InlineData("PCMieru.gate.txt", true)]
    [InlineData("pcmieru.GATE.txt", true)]
    [InlineData("PCMieru.exe", false)]
    [InlineData("update/PCMieru.gate.txt", false)]
    [InlineData("", false)]
    public void The_swap_and_the_rollback_leave_the_gate_file_alone_by_name_without_regard_to_case(string name, bool excluded) =>
        Assert.Equal(excluded, GateFile.IsExcludedFromSwap(name));

    [Fact]
    public void Publish_writes_the_gate_into_the_publish_folder_expects_it_and_keeps_the_dev_copy_out_of_the_publish()
    {
        var publish = Repo.FileText("tools/publish.ps1");

        Assert.Contains("Copy-Item -LiteralPath (Join-Path $root 'assets\\PCMieru.gate.txt') -Destination (Join-Path $folder 'PCMieru.gate.txt')", publish, StringComparison.Ordinal);
        Assert.Matches(@"\$expected = @\([^)]*'PCMieru\.gate\.txt'[^)]*'PcMieru\.Update\.dll'", publish);
        Assert.Contains("'-p:GateNextToExe=false'", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gate_file_is_kept_byte_for_byte_by_git()
    {
        var attributes = Repo.FileText(".gitattributes");

        Assert.Matches(@"(?m)^PCMieru\.gate\.txt\s+-text\s*$", attributes);
    }

    [Fact]
    public void Every_exe_project_copies_the_gate_beside_its_dev_build()
    {
        foreach (var project in ExeProjects())
        {
            Assert.True(File.ReadAllText(project).Contains("build\\GateNextToExe.targets", StringComparison.Ordinal), project + " must import build\\GateNextToExe.targets");
        }
    }

    [Fact]
    public void Every_exe_project_of_the_solution_has_a_program_that_enters_the_gate()
    {
        var projects = ExeProjects().ToList();
        Assert.True(projects.Count >= 6, "the five shipped exes and the party stub; found " + string.Join(", ", projects.Select(Path.GetFileName)));

        foreach (var project in projects)
        {
            // The test build of the swapper (tests/PcMieru.Swap.TestBuild) is the swapper's own Program.cs compiled with one more symbol, plus the test-only entry: it has no Program.cs of its own, and like the
            // swapper it must NOT ask the start gate.
            if (Path.GetFileName(Path.GetDirectoryName(project)) == "PcMieru.Swap.TestBuild")
            {
                var text = File.ReadAllText(project);
                Assert.Contains("src\\PcMieru.Swap\\Program.cs", text, StringComparison.Ordinal);
                Assert.Contains("PCM_SWAP_TESTBUILD", text, StringComparison.Ordinal);
                Assert.DoesNotContain("InstallGate.EnterStartup(", File.ReadAllText(Path.Combine(Path.GetDirectoryName(project)!, "TestEntry.cs")), StringComparison.Ordinal);
                continue;
            }

            var program = Path.Combine(Path.GetDirectoryName(project)!, "Program.cs");
            Assert.True(File.Exists(program), "no Program.cs next to " + project);
            var code = File.ReadAllText(program);

            // PCMieru.Swap.exe is the one exe that is not a program of the install: it is the swapper. It must NOT ask the start gate (the shared gate refuses it while a recovery is pending, which is
            // exactly when it has to work; and the Runner lives in I\update, where no gate file is beside it). What it does with the gate instead is the exclusive entry, which is asserted next.
            if (Path.GetFileName(Path.GetDirectoryName(project)) == "PcMieru.Swap")
            {
                Assert.DoesNotContain("InstallGate.EnterStartup()", code, StringComparison.Ordinal);
                var cli = Repo.FileText("src/PcMieru.Update/Transaction/SwapCli.cs");
                Assert.Contains("InstallGate.EnterExclusive(", cli, StringComparison.Ordinal);
                Assert.DoesNotContain("InstallGate.EnterStartup()", cli, StringComparison.Ordinal);
                continue;
            }

            Assert.True(
                code.Contains("InstallGate.EnterStartup()", StringComparison.Ordinal) || code.Contains("StartGateScreen.Enter(", StringComparison.Ordinal),
                project + " has an entry point that never calls the start gate");
        }
    }

    [Fact]
    public void The_gate_call_comes_before_the_first_thing_each_entry_point_does_that_matters()
    {
        // The place of the call in each Program.cs, as a position in the text: before the first use of what the exe is for. A text position is a blunt tool, and exactly as strong as the
        // fixed anchors below; a refactoring that moves the call fails here and gets looked at.
        AssertBefore("src/PcMieru.Cli/Program.cs", "InstallGate.EnterStartup()", "CliApp.RunAsync");
        AssertBefore("src/PcMieru.SensorHost/Program.cs", "InstallGate.EnterStartup()", "new GpuSampler");
        AssertBefore("src/PcMieru.Helper/Program.cs", "InstallGate.EnterStartup()", "HelperRequest.TryParse");
        AssertBefore("src/PcMieru.NetMeter/Program.cs", "InstallGate.EnterStartup()", "NetMeterArguments.TryParse");
        AssertBefore("src/PcMieru.Gui/Program.cs", "StartGateScreen.Enter(", "PcMieruServices.Create");
    }

    [Fact]
    public void Every_exe_that_was_built_for_these_tests_has_the_gate_beside_it_with_the_exact_bytes()
    {
        foreach (var exe in Enum.GetValues<Exe>())
        {
            var gate = Path.Combine(Repo.BuildFolderOf(exe), GateFile.FileName);
            Assert.True(File.Exists(gate), exe + " was built without its gate file: " + gate);
            Assert.Equal(GateFile.Bytes(), File.ReadAllBytes(gate));
        }
    }

    private static IEnumerable<string> ExeProjects()
    {
        var folders = new[] { Path.Combine(Repo.Root, "src"), Path.Combine(Repo.Root, "tests") };
        return folders
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.csproj", SearchOption.AllDirectories))
            .Where(project => !project.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !project.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(project => Regex.IsMatch(File.ReadAllText(project), "<OutputType>(Win)?Exe</OutputType>"))
            .Where(project => !Regex.IsMatch(File.ReadAllText(project), "<IsTestProject>\\s*true"))
            .OrderBy(project => project, StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertBefore(string file, string gate, string firstUse)
    {
        var code = Repo.FileText(file);
        var gateAt = code.IndexOf(gate, StringComparison.Ordinal);
        var useAt = code.IndexOf(firstUse, StringComparison.Ordinal);
        Assert.True(gateAt >= 0, file + " does not call " + gate);
        Assert.True(useAt < 0 || gateAt < useAt, file + ": the gate must come before " + firstUse);
    }
}
