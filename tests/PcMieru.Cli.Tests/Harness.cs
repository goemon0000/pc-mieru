using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PcMieru.Cli.Mcp;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Cli.Tests;

internal sealed record CliResult(int ExitCode, string Out, string Err)
{
    public string[] OutLines => Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

internal static class CliRun
{
    public static PcMieruServices Services(PcMieruOptions options, Action<ServiceRegistry>? configure = null) =>
        PcMieruServices.Create(options with { ForceFakes = true, DefaultSampleWindow = TimeSpan.Zero }, configure);

    public static async Task<CliResult> RunAsync(
        string[] args,
        string stdin = "",
        Language language = Language.En,
        int width = 120,
        Action<ServiceRegistry>? configure = null,
        string? helperDirectory = null,
        CancellationToken ct = default)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var env = new CliEnvironment(new StringReader(stdin), output, error, o => Services(o, configure), width, language, helperDirectory);
        var code = await CliApp.RunAsync(args, env, ct);
        return new CliResult(code, output.ToString(), error.ToString());
    }
}

internal static class Samples
{
    public static ProcessInfo Process(uint pid, string name, uint parent = 0, double cpu = 1.0) =>
        new(
            new ProcessKey(pid, 1000 + pid),
            name,
            parent,
            parent == 0 ? null : new ProcessKey(parent, 1000 + parent),
            1,
            4,
            10,
            8,
            1_000_000,
            900_000,
            2_000_000,
            0,
            0,
            Value.Ok(cpu, "test"),
            Value.Ok(0.0, "test"),
            Value.Ok(0.0, "test"),
            Value.NotCollected<double>("test"),
            Value.Ok(ProcessState.Running, "test"));

    public static ProcessList List(params ProcessInfo[] processes) =>
        new(ProcessList.CurrentSchemaVersion, DateTime.UtcNow, 1000, ProcessSort.Cpu, processes.Length, processes);

    public static ProcessDetails Details(ProcessKey key, string path, string commandLine) =>
        new(
            key,
            Value.Ok(path, "test"),
            Value.Ok(commandLine, "test"),
            Value.NotCollected<string>("test"),
            Value.NotCollected<string>("test"),
            Value.NotCollected<string>("test"),
            Value.NotCollected<string>("test"),
            Value.NotCollected<string>("test"),
            Value.NotCollected<bool>("test"),
            Value.NotCollected<SignatureInfo>("test"),
            [],
            null);
}

/// <summary>Forwards to the Fake insights; a test overrides the one method it wants to change.</summary>
internal class DelegatingInsights(IReadOnlyInsights inner) : IReadOnlyInsights
{
    public virtual Task<SystemSummary> GetSystemSummaryAsync(CancellationToken ct = default) => inner.GetSystemSummaryAsync(ct);

    public virtual Task<ProcessList> ListProcessesAsync(ProcessQuery query, CancellationToken ct = default) => inner.ListProcessesAsync(query, ct);

    public virtual Task<ProcessDetail?> GetProcessAsync(uint pid, CancellationToken ct = default) => inner.GetProcessAsync(pid, ct);

    public virtual Task<HardwareInfo> GetHardwareAsync(HardwareCategory category, CancellationToken ct = default) => inner.GetHardwareAsync(category, ct);

    public virtual Task<StorageHealthReport> GetStorageHealthAsync(CancellationToken ct = default) => inner.GetStorageHealthAsync(ct);

    public virtual Task<DiagnoseResult> DiagnoseAsync(TimeSpan duration, CancellationToken ct = default) => inner.DiagnoseAsync(duration, ct);

    public virtual Task<ReportDocument> GetReportAsync(ReportOptions options, CancellationToken ct = default) => inner.GetReportAsync(options, ct);
}

internal sealed class StubInsights(IReadOnlyInsights inner) : DelegatingInsights(inner)
{
    public Func<ProcessQuery, CancellationToken, Task<ProcessList>>? OnList { get; set; }

    public Func<uint, CancellationToken, Task<ProcessDetail?>>? OnProcess { get; set; }

    public Func<CancellationToken, Task<StorageHealthReport>>? OnStorage { get; set; }

    public override Task<StorageHealthReport> GetStorageHealthAsync(CancellationToken ct = default) =>
        OnStorage is null ? base.GetStorageHealthAsync(ct) : OnStorage(ct);

    public override Task<ProcessList> ListProcessesAsync(ProcessQuery query, CancellationToken ct = default) =>
        OnList is null ? base.ListProcessesAsync(query, ct) : OnList(query, ct);

    public override Task<ProcessDetail?> GetProcessAsync(uint pid, CancellationToken ct = default) =>
        OnProcess is null ? base.GetProcessAsync(pid, ct) : OnProcess(pid, ct);
}

/// <summary>A reader the test feeds line by line, so a request can be sent after something else has happened.</summary>
internal sealed class LiveInput : TextReader
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private string _current = string.Empty;
    private int _position;

    public void Send(string line) => _lines.Writer.TryWrite(line + "\n");

    public void Finish() => _lines.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        while (_position >= _current.Length)
        {
            if (!await _lines.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            if (_lines.Reader.TryRead(out var next))
            {
                _current = next;
                _position = 0;
            }
        }

        var count = Math.Min(buffer.Length, _current.Length - _position);
        _current.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }
}

/// <summary>Collects what the server writes, readable while the server is still running.</summary>
internal sealed class LineSink : TextWriter
{
    private readonly object _gate = new();
    private readonly StringBuilder _all = new();

    public override Encoding Encoding => Encoding.UTF8;

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _all.ToString();
            }
        }
    }

    public List<JsonNode> Messages => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();

    public override void Write(char value)
    {
        lock (_gate)
        {
            _all.Append(value);
        }
    }

    public override void Write(string? value)
    {
        lock (_gate)
        {
            _all.Append(value);
        }
    }

    public async Task WaitForLinesAsync(int count)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (Text.Count(c => c == '\n') < count)
        {
            Assert.True(DateTime.UtcNow < until, $"timed out waiting for {count} output lines; got: {Text}");
            await Task.Delay(10);
        }
    }
}

internal static class McpRun
{
    public const string Initialize = """{"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""";

    public static string InitializeWith(string version) =>
        "{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + version + "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"0\"}}}";

    public const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    public static string Call(object id, string tool, string arguments = "{}") =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + IdText(id) + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}";

    public static string Request(object id, string method) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + IdText(id) + ",\"method\":\"" + method + "\"}";

    private static string IdText(object id) => id is string s ? "\"" + s + "\"" : id.ToString()!;

    public static PcMieruServices Services(bool redact = true) =>
        CliRun.Services(new PcMieruOptions { Redact = redact });

    public static async Task<List<JsonNode>> RunAsync(IReadOnlyInsights insights, IRedactor redactor, params string[] lines)
    {
        var sink = new LineSink();
        var server = new McpServer(insights, redactor, "9.9.9");
        await server.RunAsync(new StringReader(string.Join("\n", lines) + "\n"), sink, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        return sink.Messages;
    }

    public static async Task<List<JsonNode>> RunAsync(params string[] lines)
    {
        using var services = Services();
        return await RunAsync(services.Insights, services.Redactor, lines);
    }

    public static JsonNode Reply(IEnumerable<JsonNode> messages, string id) =>
        messages.Single(m => m["id"]?.ToJsonString() == "\"" + id + "\"");

    /// <summary>Initialize, then one tools/call; returns the call result object.</summary>
    public static async Task<JsonNode> ToolAsync(string tool, string arguments = "{}", string version = "2025-06-18")
    {
        var messages = await RunAsync(InitializeWith(version), Initialized, Call("c", tool, arguments));
        return Reply(messages, "c")["result"]!;
    }
}
