using System.Text.RegularExpressions;

namespace PcMieru.Update.Tests;

/// <summary>
/// What the build files promise about the release floor and the zip path (DESIGN-B 11 step 1): they are text, they were wrong once without any test
/// noticing, and the only way to see the mistake is to read them. A step that says one thing in a comment and another in the command is what these tests are for.
/// </summary>
public partial class WorkflowPolicyTests
{
    private static string Read(params string[] relative) => File.ReadAllText(Path.Combine([RepoRoot(), .. relative]));

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>The steps of a workflow: the text from one "      - " line (6 spaces) to the next, with the job's lines before the first one left out.</summary>
    private static List<string> Steps(string yaml)
    {
        var steps = new List<string>();
        var current = new List<string>();
        foreach (var line in Lines(yaml))
        {
            if (StepStart().IsMatch(line))
            {
                if (current.Count > 0)
                {
                    steps.Add(string.Join("\n", current));
                }

                current = [];
            }

            if (current.Count > 0 || StepStart().IsMatch(line))
            {
                current.Add(line);
            }
        }

        if (current.Count > 0)
        {
            steps.Add(string.Join("\n", current));
        }

        return steps;
    }

    [GeneratedRegex(@"^      - ")]
    private static partial Regex StepStart();

    private static string CodeOf(string step) => string.Join("\n", Lines(step).Where(l => !l.TrimStart().StartsWith('#')));

    private static string[] PublishSteps(string yaml) => Steps(yaml).Where(s => CodeOf(s).Contains("publish.ps1", StringComparison.Ordinal)).ToArray();

    // ----------------------------------------------------------------------------------------------------------------------------- ci.yml

    [Fact]
    public void Ci_publishes_the_zip_it_no_longer_skips_it()
    {
        var yaml = Read(".github", "workflows", "ci.yml");
        foreach (var step in PublishSteps(yaml))
        {
            Assert.DoesNotContain("-SkipZip", CodeOf(step), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("-SkipZip", yaml, StringComparison.Ordinal); // not even in a comment that says it still does
    }

    [Fact]
    public void Ci_publishes_twice_once_in_pwsh_and_once_in_windows_powershell_5_1_each_into_its_own_folder_against_the_full_floor()
    {
        var steps = PublishSteps(Read(".github", "workflows", "ci.yml"));
        Assert.Equal(2, steps.Length);

        var pwsh = steps.Single(s => s.Contains("shell: pwsh", StringComparison.Ordinal));
        var legacy = steps.Single(s => s.Contains("shell: powershell", StringComparison.Ordinal));
        Assert.NotEqual(pwsh, legacy);

        foreach (var step in steps)
        {
            var code = CodeOf(step);
            Assert.Contains("-Verify", code, StringComparison.Ordinal);
            Assert.DoesNotContain("-PartialFloor", code, StringComparison.Ordinal); // the swapper ships: the full floor is the only floor
            Assert.Contains("-Output", code, StringComparison.Ordinal);
        }

        // Two different output folders, or the second publish would only clean up after the first and the lists would be of one zip twice.
        var outputs = steps.Select(s => OutputOf(CodeOf(s))).ToArray();
        Assert.All(outputs, o => Assert.False(string.IsNullOrWhiteSpace(o), "a publish step has no -Output"));
        Assert.NotEqual(outputs[0], outputs[1], StringComparer.Ordinal);
    }

    [Fact]
    public void Ci_compares_the_entry_names_of_the_two_zips_and_fails_when_they_differ()
    {
        var yaml = Read(".github", "workflows", "ci.yml");
        var compare = Steps(yaml).Where(s => CodeOf(s).Contains("--list", StringComparison.Ordinal)).ToArray();
        Assert.Single(compare);
        var code = CodeOf(compare[0]);

        // The two lists come from the validator tool (the code the client uses), one per zip, and the step throws on a difference.
        Assert.Equal(2, Regex.Matches(code, @"--list").Count);
        Assert.Contains("throw", code, StringComparison.Ordinal);
        Assert.Contains("BundleCheck", code, StringComparison.Ordinal);

        // After the two publishes, not before: the lists are of zips that exist.
        var steps = Steps(yaml);
        var last = steps.IndexOf(compare[0]);
        foreach (var publish in PublishSteps(yaml))
        {
            Assert.True(steps.IndexOf(publish) < last, "the comparison runs before a publish");
        }
    }

    [Fact]
    public void Neither_a_workflow_nor_the_publish_script_has_a_partial_floor_any_more()
    {
        // Step 4 shipped PCMieru.Swap.exe, BundleFloor.NotYetBuilt is empty, and -PartialFloor is gone from the two workflows and from the script (not even a comment that still offers it).
        foreach (var name in new[] { "ci.yml", "release.yml" })
        {
            Assert.DoesNotContain("PartialFloor", Read(".github", "workflows", name), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("PartialFloor", Read("tools", "publish.ps1"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------------- release.yml

    [Fact]
    public void Release_publishes_against_the_full_floor()
    {
        var yaml = Read(".github", "workflows", "release.yml");
        var steps = PublishSteps(yaml);
        Assert.Single(steps);
        Assert.Contains("-Verify", CodeOf(steps[0]), StringComparison.Ordinal);
        Assert.DoesNotContain("-PartialFloor", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("-SkipZip", yaml, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------------- publish.ps1

    [Fact]
    public void Publish_always_asks_the_validator_for_the_full_floor_and_has_no_switch_for_a_smaller_one()
    {
        var script = Read("tools", "publish.ps1");
        Assert.DoesNotMatch(@"\[switch\]\s*\$PartialFloor", script);

        var code = Lines(script).Where(l => !l.TrimStart().StartsWith('#')).ToArray();
        Assert.DoesNotContain(code, l => l.Contains("this-build", StringComparison.Ordinal)); // the smaller floor is not named by any line of code
        Assert.Contains(code, l => l.Trim() == "$floorName = 'full'");

        // The floor is always spelled out for the validator, never left to its own default.
        Assert.Contains("'--floor'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_builds_the_swapper_checks_it_for_network_code_and_expects_it_in_the_folder()
    {
        var script = Read("tools", "publish.ps1");
        Assert.Contains("PCMieru.Swap.exe", script, StringComparison.Ordinal);
        Assert.Contains("PcMieru.Swap.csproj", script, StringComparison.Ordinal);
        Assert.Contains("Test-SwapExe", script, StringComparison.Ordinal);       // the check on the built exe: imports, type names, manifest
        Assert.Contains("Find-NetworkMarkers", script, StringComparison.Ordinal); // and the scan it uses
        Assert.Contains("System.Net.Http", script, StringComparison.Ordinal);     // which is first proved on PcMieru.Update.dll, where the names are
    }

    [Fact]
    public void Publish_looks_at_hidden_and_system_files_every_time_it_lists_a_folder()
    {
        var script = Read("tools", "publish.ps1");
        var listings = Lines(script).Where(l => l.Contains("Get-ChildItem", StringComparison.Ordinal) && l.Contains("-Recurse", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(listings);
        foreach (var line in listings)
        {
            Assert.True(line.Contains("-Force", StringComparison.Ordinal), "a recursive listing without -Force skips hidden and system files: " + line.Trim());
        }

        Assert.Contains("[IO.FileAttributes]::Hidden", script, StringComparison.Ordinal);
        Assert.Contains("[IO.FileAttributes]::System", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_takes_the_same_versions_as_the_client_each_part_of_one_to_six_digits_with_no_leading_zero()
    {
        var script = Read("tools", "publish.ps1");
        var line = Lines(script).Single(l => l.Contains("Version must look like", StringComparison.Ordinal));
        var pattern = Regex.Match(line, @"-notmatch '(?<p>[^']+)'").Groups["p"].Value;
        Assert.False(string.IsNullOrEmpty(pattern), "the version check was not found: " + line);

        var version = new Regex(pattern);
        foreach (var good in new[] { "0.0.0", "0.5.0", "1.2.3", "10.20.30", "999999.999999.999999", "0.0.100000" })
        {
            Assert.Matches(version, good);
        }

        foreach (var bad in new[] { "1.2", "1.2.3.4", "01.2.3", "1.02.3", "1.2.03", "1234567.0.0", "0.1234567.0", "0.0.1234567", "1.2.3-beta", "v1.2.3","١.2.3", "" })
        {
            Assert.DoesNotMatch(version, bad);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------- the floor

    [Fact]
    public void The_floor_has_nothing_left_that_is_not_built()
    {
        var source = Read("src", "PcMieru.Update", "Bundle", "BundleFloor.cs");
        var at = source.IndexOf("public static IReadOnlyList<string> NotYetBuilt", StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.Contains("= [];", source[at..Math.Min(source.Length, at + 160)], StringComparison.Ordinal); // empty: the swapper is on the floor and in every publish
        Assert.Contains("\"PCMieru.Swap.exe\"", source, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------------- helpers

    private static string OutputOf(string code)
    {
        var match = Regex.Match(code, @"-Output\s+(?<o>""[^""]*""|'[^']*'|\S+)");
        return match.Success ? match.Groups["o"].Value : string.Empty;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcMieru.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("PcMieru.slnx not found above " + AppContext.BaseDirectory);
    }
}
