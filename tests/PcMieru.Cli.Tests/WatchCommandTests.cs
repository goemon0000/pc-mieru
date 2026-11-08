using System.Text.Json.Nodes;

namespace PcMieru.Cli.Tests;

/// <summary>A writer that notes how many lines were already written each time somebody flushes it.</summary>
internal sealed class FlushTrackingWriter : StringWriter
{
    public List<int> LinesAtFlush { get; } = [];

    public FlushTrackingWriter()
    {
        NewLine = "\n";
    }

    public override void Flush()
    {
        Note();
        base.Flush();
    }

    public override Task FlushAsync()
    {
        Note();
        return base.FlushAsync();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        Note();
        return base.FlushAsync(cancellationToken);
    }

    private void Note() => LinesAtFlush.Add(ToString().Count(c => c == '\n'));
}

public sealed class WatchCommandTests
{
    [Fact]
    public async Task Watch_prints_one_block_per_tick_and_stops_after_count()
    {
        var r = await CliRun.RunAsync(["watch", "--count", "2", "--interval", "250ms", "--top", "3"]);

        Assert.Equal(0, r.ExitCode);
        var headers = r.OutLines.Where(l => l.Contains("memory used", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, headers.Length);
        Assert.Equal(2, r.OutLines.Count(l => l.Contains("Mem MB", StringComparison.Ordinal)));
        // The busiest process of the fake machine is in the top rows.
        Assert.Contains("SkyrimVR.exe", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watch_in_japanese_uses_the_japanese_header()
    {
        var r = await CliRun.RunAsync(["watch", "--count", "1", "--interval", "250ms"], language: Language.Ja);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("メモリ使用", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ndjson_gives_one_valid_single_line_json_object_per_tick()
    {
        var r = await CliRun.RunAsync(["watch", "--ndjson", "--count", "3", "--interval", "250ms", "--top", "2"]);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(3, r.OutLines.Length);
        foreach (var line in r.OutLines)
        {
            var node = Assert.IsType<JsonObject>(JsonNode.Parse(line));
            Assert.Equal("1", node["schemaVersion"]!.GetValue<string>());
            Assert.Equal(2, node["topProcesses"]!.AsArray().Count);
            Assert.NotNull(node["system"]);
            Assert.NotNull(node["observations"]);
        }
    }

    [Fact]
    public async Task Each_tick_is_flushed_so_a_follower_sees_it_when_it_happens()
    {
        var output = new FlushTrackingWriter();
        var error = new StringWriter();
        var env = new CliEnvironment(new StringReader(string.Empty), output, error, o => CliRun.Services(o), 120, Language.En);

        var code = await CliApp.RunAsync(["watch", "--ndjson", "--count", "3", "--interval", "250ms"], env, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains(1, output.LinesAtFlush);
        Assert.Contains(2, output.LinesAtFlush);
        Assert.Contains(3, output.LinesAtFlush);
    }

    [Fact]
    public async Task Ctrl_c_ends_watch_normally_with_exit_0_after_at_least_one_tick()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(900));

        var r = await CliRun.RunAsync(["watch", "--ndjson", "--interval", "250ms"], ct: cts.Token).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, r.ExitCode);
        Assert.InRange(r.OutLines.Length, 1, 5);
        Assert.All(r.OutLines, l => Assert.NotNull(JsonNode.Parse(l)));
    }

    [Theory]
    [InlineData("watch", "--interval", "10ms")]
    [InlineData("watch", "--interval", "2h")]
    [InlineData("watch", "--interval", "soon")]
    [InlineData("watch", "--top", "0")]
    [InlineData("watch", "--top", "101")]
    [InlineData("watch", "--count", "0")]
    [InlineData("watch", "--count", "x")]
    [InlineData("watch", "extra")]
    public async Task A_bad_watch_command_line_exits_2_before_sampling_anything(params string[] args)
    {
        var r = await CliRun.RunAsync(args).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, r.ExitCode);
        Assert.Equal(string.Empty, r.Out);
    }
}
