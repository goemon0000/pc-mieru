using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PcMieru.Cli.Commands;

namespace PcMieru.Cli.Tests;

public class CliCommandTests
{
    [Fact]
    public async Task Version_prints_one_line_on_stdout_and_nothing_on_stderr()
    {
        var r = await CliRun.RunAsync(["version"]);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("pcmi " + VersionCommand.CurrentVersion + "\n", r.Out);
        Assert.Equal(string.Empty, r.Err);
    }

    [Fact]
    public async Task Dash_dash_version_is_the_same_as_the_version_command()
    {
        var r = await CliRun.RunAsync(["--version"]);
        Assert.Equal(0, r.ExitCode);
        Assert.StartsWith("pcmi ", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_json_lists_the_mcp_protocol_versions()
    {
        var r = await CliRun.RunAsync(["version", "--json"]);
        var node = JsonNode.Parse(r.Out)!;
        Assert.Equal("pcmi", (string?)node["name"]);
        Assert.Equal(["2025-06-18", "2025-03-26", "2024-11-05"], node["mcpProtocolVersions"]!.AsArray().Select(v => (string)v!).ToArray());
    }

    [Fact]
    public async Task Help_lists_every_command_on_stdout()
    {
        var r = await CliRun.RunAsync(["--help"]);
        Assert.Equal(0, r.ExitCode);
        Assert.All(CliApp.Commands, c => Assert.Contains("  " + c.Name + " ", r.Out, StringComparison.Ordinal));
        Assert.Equal(string.Empty, r.Err);
    }

    [Fact]
    public async Task Help_follows_the_language_option()
    {
        var ja = await CliRun.RunAsync(["--lang", "ja", "--help"]);
        var en = await CliRun.RunAsync(["--lang=en", "--help"]);
        Assert.Contains("使い方", ja.Out, StringComparison.Ordinal);
        Assert.Contains("Usage", en.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("使い方", en.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_arguments_is_a_usage_error_with_the_help_on_stderr()
    {
        var r = await CliRun.RunAsync([]);
        Assert.Equal(2, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        Assert.Contains("Usage", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_command_exits_2_and_writes_only_to_stderr()
    {
        var r = await CliRun.RunAsync(["frobnicate"]);
        Assert.Equal(2, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        Assert.Contains("frobnicate", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Control_characters_in_an_unknown_command_are_not_echoed_raw()
    {
        var r = await CliRun.RunAsync(["bad\u001b[31m"]);
        Assert.DoesNotContain('\u001b', r.Err);
    }

    [Theory]
    [InlineData("--lang", "fr", "version")]
    [InlineData("--lang")]
    [InlineData("ps", "--sort", "bogus")]
    [InlineData("ps", "--sort", "1")]
    [InlineData("ps", "--top", "0")]
    [InlineData("ps", "--top", "x")]
    [InlineData("ps", "--sample", "forever")]
    [InlineData("ps", "--bogus")]
    [InlineData("ps", "extra")]
    [InlineData("proc")]
    [InlineData("proc", "abc")]
    [InlineData("proc", "-1")]
    [InlineData("version", "extra")]
    [InlineData("hw", "gadgets")]
    [InlineData("hw", "cpu", "gpu")]
    [InlineData("report", "--format", "xml")]
    [InlineData("report", "--duration", "99m")]
    [InlineData("mcp", "extra")]
    [InlineData("mcp", "--redact-never")]
    [InlineData("kill")]
    [InlineData("priority")]
    [InlineData("eco")]
    [InlineData("startup")]
    [InlineData("services")]
    [InlineData("run")]
    [InlineData("watch", "--interval", "10ms")]
    [InlineData("watch", "--count", "0")]
    public async Task A_bad_command_line_exits_2_and_prints_the_syntax_on_stderr(params string[] args)
    {
        var r = await CliRun.RunAsync(args);
        Assert.Equal(2, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        Assert.NotEqual(string.Empty, r.Err);
    }

    [Fact]
    public async Task Command_help_prints_the_syntax_on_stdout_and_exits_0()
    {
        var r = await CliRun.RunAsync(["ps", "--help"]);
        Assert.Equal(0, r.ExitCode);
        Assert.StartsWith("pcmi ps", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proc_for_a_pid_that_does_not_exist_exits_3()
    {
        var r = await CliRun.RunAsync(["proc", "4000000000"]);
        Assert.Equal(3, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
        Assert.Contains("PID", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proc_json_for_a_known_pid_is_one_json_document()
    {
        var pid = await AnyPidAsync();
        var r = await CliRun.RunAsync(["proc", pid.ToString(), "--json"]);
        Assert.Equal(0, r.ExitCode);
        var node = JsonNode.Parse(r.Out)!;
        Assert.StartsWith(pid + ":", (string)node["process"]!["key"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ps_json_is_one_compact_line_on_stdout_and_the_fake_notice_goes_to_stderr()
    {
        var r = await CliRun.RunAsync(["ps", "--json", "--top", "5"]);
        Assert.Equal(0, r.ExitCode);
        Assert.Single(r.OutLines);
        Assert.DoesNotContain("pcmi:", r.Out, StringComparison.Ordinal);
        Assert.StartsWith("pcmi: ", r.Err, StringComparison.Ordinal);

        var list = PcMieruJson.Deserialize<ProcessList>(r.OutLines[0])!;
        Assert.Equal("1", list.SchemaVersion);
        Assert.Equal(5, list.Processes.Count);
        Assert.Matches(new Regex(@"^\d+:\d+$"), list.Processes[0].Key.ToString());
    }

    [Fact]
    public async Task Ps_json_uses_the_wire_names_the_schema_promises()
    {
        var r = await CliRun.RunAsync(["ps", "--json", "--top", "1"]);
        var node = JsonNode.Parse(r.Out)!;
        Assert.NotNull(node["takenAtUtc"]);
        Assert.Equal("cpu", (string?)node["sort"]);
        var process = node["processes"]![0]!;
        Assert.Equal(System.Text.Json.JsonValueKind.String, process["key"]!.GetValueKind());
        Assert.Equal("ok", (string?)process["cpuPercent"]!["status"]);
        Assert.Equal(System.Text.Json.JsonValueKind.String, process["ioReadBytesTotal"]!.GetValueKind());
    }

    [Fact]
    public async Task Ps_sorts_by_the_requested_column()
    {
        var byName = await CliRun.RunAsync(["ps", "--json", "--sort", "name", "--top", "100"]);
        var names = PcMieruJson.Deserialize<ProcessList>(byName.OutLines[0])!.Processes.Select(p => p.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), names);

        var byMem = await CliRun.RunAsync(["ps", "--json", "--sort", "mem", "--top", "100"]);
        var memory = PcMieruJson.Deserialize<ProcessList>(byMem.OutLines[0])!.Processes.Select(p => p.PrivateWorkingSetBytes).ToList();
        Assert.Equal(memory.OrderByDescending(m => m).ToList(), memory);
    }

    [Fact]
    public async Task Ps_table_has_a_header_a_footer_and_no_trailing_spaces()
    {
        var r = await CliRun.RunAsync(["ps", "--top", "4"]);
        Assert.Equal(0, r.ExitCode);
        var lines = r.OutLines;
        Assert.Equal(1 + 4 + 1, lines.Length);
        Assert.StartsWith("PID", lines[0].TrimStart(), StringComparison.Ordinal);
        Assert.Contains("4 of", lines[^1], StringComparison.Ordinal);
        Assert.All(lines, l => Assert.Equal(l.TrimEnd(), l));
        Assert.DoesNotContain("pcmi:", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ps_table_follows_the_language()
    {
        var ja = await CliRun.RunAsync(["ps", "--top", "2", "--lang", "ja"]);
        Assert.Contains("名前", ja.Out, StringComparison.Ordinal);
        Assert.Contains("件", ja.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ps_tree_puts_a_child_under_its_parent_with_indent()
    {
        var r = await CliRun.RunAsync(
            ["ps", "--tree"],
            configure: registry =>
            {
                registry.TryGet<IReadOnlyInsights>(out var inner);
                var insights = new StubInsights(inner!)
                {
                    OnList = (_, _) => Task.FromResult(Samples.List(
                        Samples.Process(10, "child.exe", parent: 5),
                        Samples.Process(5, "parent.exe"),
                        Samples.Process(11, "grandchild.exe", parent: 10))),
                };
                registry.Set<IReadOnlyInsights>(insights);
            });
        var rows = r.OutLines.Skip(1).Take(3).Select(l => Regex.Replace(l, @"^\s*\d+  ", string.Empty)).ToArray();
        Assert.StartsWith("parent.exe", rows[0], StringComparison.Ordinal);
        Assert.StartsWith("  child.exe", rows[1], StringComparison.Ordinal);
        Assert.StartsWith("    grandchild.exe", rows[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hostile_process_name_cannot_add_lines_or_escape_codes_to_the_table()
    {
        var r = await CliRun.RunAsync(
            ["ps"],
            configure: registry =>
            {
                registry.TryGet<IReadOnlyInsights>(out var inner);
                registry.Set<IReadOnlyInsights>(new StubInsights(inner!)
                {
                    OnList = (_, _) => Task.FromResult(Samples.List(Samples.Process(7, "evil\n\u001b[31mred\u0007.exe"), Samples.Process(8, "ok.exe"))),
                });
            });
        Assert.Equal(1 + 2 + 1, r.OutLines.Length);
        Assert.DoesNotContain('\u001b', r.Out);
        Assert.DoesNotContain('\u0007', r.Out);
    }

    [Fact]
    public async Task A_long_name_is_cut_so_the_row_fits_the_console_width()
    {
        var longName = new string('x', 120) + ".exe";
        var r = await CliRun.RunAsync(
            ["ps"],
            width: 80,
            configure: registry =>
            {
                registry.TryGet<IReadOnlyInsights>(out var inner);
                registry.Set<IReadOnlyInsights>(new StubInsights(inner!)
                {
                    OnList = (_, _) => Task.FromResult(Samples.List(Samples.Process(7, longName))),
                });
            });
        var rows = r.OutLines.Take(2).ToArray();
        Assert.All(rows, l => Assert.True(CliText.Width(l) <= 80, l));
        Assert.Contains("…", rows[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirected_output_is_not_cut_at_all()
    {
        var longName = new string('x', 120) + ".exe";
        var r = await CliRun.RunAsync(
            ["ps"],
            width: 0,
            configure: registry =>
            {
                registry.TryGet<IReadOnlyInsights>(out var inner);
                registry.Set<IReadOnlyInsights>(new StubInsights(inner!)
                {
                    OnList = (_, _) => Task.FromResult(Samples.List(Samples.Process(7, longName))),
                });
            });
        Assert.Contains(longName, r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hw_json_is_valid_json_with_every_value_carrying_a_status()
    {
        var r = await CliRun.RunAsync(["hw", "--json"]);
        Assert.Equal(0, r.ExitCode);
        Assert.Single(r.OutLines);
        var node = JsonNode.Parse(r.Out)!;
        Assert.NotNull(node["schemaVersion"]);
        Assert.StartsWith("pcmi: ", r.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("pcmi:", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hw_category_prints_text_for_people()
    {
        var r = await CliRun.RunAsync(["hw", "cpu"]);
        Assert.Equal(0, r.ExitCode);
        Assert.NotEqual(string.Empty, r.Out.Trim());
        Assert.DoesNotContain('{', r.Out);
    }

    [Fact]
    public async Task Hw_category_json_is_narrower_than_all()
    {
        var all = await CliRun.RunAsync(["hw", "--json"]);
        var cpu = await CliRun.RunAsync(["hw", "cpu", "--json"]);
        Assert.True(cpu.Out.Length < all.Out.Length);
    }

    [Fact]
    public async Task Report_markdown_goes_to_stdout_and_redaction_is_on_by_default()
    {
        var r = await CliRun.RunAsync(["report", "--duration", "0"]);
        Assert.Equal(0, r.ExitCode);
        Assert.StartsWith("#", r.Out.TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_json_parses()
    {
        var r = await CliRun.RunAsync(["report", "--duration", "0", "--format", "json"]);
        Assert.Equal(0, r.ExitCode);
        var node = JsonNode.Parse(r.Out)!;
        Assert.True((bool)node["redacted"]!);
    }

    [Fact]
    public async Task Report_no_redact_says_so_in_the_document()
    {
        var r = await CliRun.RunAsync(["report", "--duration", "0", "--format", "json", "--no-redact"]);
        Assert.False((bool)JsonNode.Parse(r.Out)!["redacted"]!);
    }

    [Fact]
    public async Task Report_can_write_a_file_instead_of_stdout()
    {
        var path = Path.Combine(Path.GetTempPath(), "pcmi-test-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            var r = await CliRun.RunAsync(["report", "--duration", "0", "-o", path]);
            Assert.Equal(0, r.ExitCode);
            Assert.Equal(string.Empty, r.Out);
            Assert.StartsWith("#", (await File.ReadAllTextAsync(path)).TrimStart(), StringComparison.Ordinal);
            Assert.False((await File.ReadAllBytesAsync(path)) is [0xEF, 0xBB, 0xBF, ..], "the report file is UTF-8 without a BOM");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Health_and_perf_run_on_the_fakes()
    {
        var health = await CliRun.RunAsync(["health", "--json"]);
        var perf = await CliRun.RunAsync(["perf", "--json", "--sample", "0s"]);
        Assert.Equal(0, health.ExitCode);
        Assert.Equal(0, perf.ExitCode);
        Assert.NotNull(JsonNode.Parse(health.Out));
        Assert.NotNull(JsonNode.Parse(perf.Out));
    }

    private static async Task<uint> AnyPidAsync()
    {
        using var services = CliRun.Services(new PcMieruOptions());
        var snapshot = await services.Snapshots.SampleAsync();
        return snapshot.Processes.First(p => p.Key.Pid != 0).Key.Pid;
    }
}
