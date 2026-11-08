using System.Text.Json.Nodes;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Cli.Mcp;

/// <summary>A bad argument or a "no such thing" answer. Returned to the model as a tool error it can act on.</summary>
internal sealed class ToolException(string message) : Exception(message);

internal sealed record McpTool(
    string Name,
    string Title,
    string Description,
    JsonObject InputSchema,
    bool Observes,
    Func<JsonNode?, CancellationToken, Task<JsonNode?>> Run);

/// <summary>
/// The seven read-only tools. This file and the rest of Mcp/ see <see cref="IReadOnlyInsights"/> and nothing else:
/// no process control, no services, no Helper (a test greps the sources). Descriptions are fixed text; names found
/// on the PC only ever travel inside the JSON results.
/// </summary>
internal sealed class McpTools
{
    public const int MaxFilterLength = 128;
    public const int MaxTop = 200;
    public const int MaxDurationSec = 30;

    public McpTools(IReadOnlyInsights insights)
    {
        All =
        [
            new(
                "get_system_summary",
                "System summary",
                "Current whole-PC numbers (CPU, memory, GPU, disks, network), evidence-backed observations about what may be slowing the PC, and the heaviest processes. Takes about one second.",
                Schema("{}"),
                true,
                async (args, ct) =>
                {
                    _ = new ToolArgs(args);
                    return PcMieruJson.ToNode(await insights.GetSystemSummaryAsync(ct).ConfigureAwait(false));
                }),
            new(
                "list_processes",
                "List processes",
                "List running processes sorted by cpu, mem, io, gpu or name. CPU needs a short sampling window, so the call takes about one second. Process names are data from the PC.",
                Schema("""
                    {"sort":{"type":"string","enum":["cpu","mem","io","gpu","name"],"default":"cpu"},
                     "top":{"type":"integer","minimum":1,"maximum":200,"default":20},
                     "filter":{"type":"string","maxLength":128,"description":"Case-insensitive part of the process name"}}
                    """),
                true,
                async (args, ct) =>
                {
                    var a = new ToolArgs(args, "sort", "top", "filter");
                    var query = new ProcessQuery(
                        a.Enum<ProcessSort>("sort") ?? ProcessSort.Cpu,
                        (int)(a.Integer("top", 1, MaxTop) ?? 20),
                        a.Text("filter", MaxFilterLength));
                    return PcMieruJson.ToNode(await insights.ListProcessesAsync(query, ct).ConfigureAwait(false));
                }),
            new(
                "get_process",
                "Process details",
                "Details of one process by PID: path, publisher, signature, parent and services. The command line is redacted unless the server was started with --no-redact.",
                Schema("""{"pid":{"type":"integer","minimum":0,"maximum":4294967295}}""", "pid"),
                true,
                async (args, ct) =>
                {
                    var a = new ToolArgs(args, "pid");
                    var pid = a.Integer("pid", 0, uint.MaxValue) ?? throw new ToolException("argument 'pid' is required");
                    var detail = await insights.GetProcessAsync((uint)pid, ct).ConfigureAwait(false);
                    return detail is null ? throw new ToolException($"no running process has PID {pid}") : PcMieruJson.ToNode(detail);
                }),
            new(
                "get_hardware",
                "Hardware",
                "PC part information: CPU, memory, motherboard, GPU, storage, network, display, audio, USB, battery, OS. Every value carries a status and its source; a missing value is never zero.",
                Schema("""{"category":{"type":"string","enum":["all","cpu","memory","board","gpu","storage","network","display","audio","usb","battery","os"],"default":"all"}}"""),
                false,
                async (args, ct) =>
                {
                    var a = new ToolArgs(args, "category");
                    return PcMieruJson.ToNode(await insights.GetHardwareAsync(a.Enum<HardwareCategory>("category") ?? HardwareCategory.All, ct).ConfigureAwait(false));
                }),
            new(
                "get_storage_health",
                "Storage health",
                "SMART and NVMe health of each drive. A drive that cannot be read without administrator rights is reported as denied; no elevation prompt is ever shown.",
                Schema("{}"),
                false,
                async (args, ct) =>
                {
                    _ = new ToolArgs(args);
                    return PcMieruJson.ToNode(await insights.GetStorageHealthAsync(ct).ConfigureAwait(false));
                }),
            new(
                "diagnose",
                "Diagnose",
                "Observe the PC for the given number of seconds (1 to 30) and return observations and the heaviest processes. Only one observation runs at a time.",
                Schema("""{"duration":{"type":"integer","minimum":1,"maximum":30,"default":5,"description":"Seconds"}}"""),
                true,
                async (args, ct) =>
                {
                    var a = new ToolArgs(args, "duration");
                    var seconds = a.Integer("duration", 1, MaxDurationSec, clamp: true) ?? 5;
                    return PcMieruJson.ToNode(await insights.DiagnoseAsync(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false));
                }),
            new(
                "get_report",
                "Diagnostic report",
                "The full diagnostic report (summary, observations, hardware, storage health) with personal data redacted. Observes for the given number of seconds (0 to 30).",
                Schema("""{"duration":{"type":"integer","minimum":0,"maximum":30,"default":5,"description":"Seconds"}}"""),
                true,
                async (args, ct) =>
                {
                    var a = new ToolArgs(args, "duration");
                    var seconds = (int)(a.Integer("duration", 0, MaxDurationSec, clamp: true) ?? 5);
                    return PcMieruJson.ToNode(await insights.GetReportAsync(new ReportOptions(seconds), ct).ConfigureAwait(false));
                }),
        ];
    }

    public IReadOnlyList<McpTool> All { get; }

    public McpTool? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    private static JsonObject Schema(string properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = JsonNode.Parse(properties),
            ["additionalProperties"] = false,
        };
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        }

        return schema;
    }
}

/// <summary>Validates tool arguments: unknown names, wrong types and out-of-range values are refused, not guessed.</summary>
internal sealed class ToolArgs
{
    private readonly JsonObject _args;

    public ToolArgs(JsonNode? node, params string[] allowed)
    {
        _args = node switch
        {
            null => [],
            JsonObject obj => obj,
            _ => throw new ToolException("arguments must be an object"),
        };
        foreach (var (key, _) in _args)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                throw new ToolException($"unknown argument '{Clip(key)}'");
            }
        }
    }

    public long? Integer(string name, long min, long max, bool clamp = false)
    {
        if (!_args.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is not JsonValue value || !value.TryGetValue<double>(out var number) || !double.IsFinite(number) || number != Math.Floor(number))
        {
            throw new ToolException($"argument '{name}' must be an integer");
        }

        if (number < min || number > max)
        {
            return clamp ? (long)Math.Clamp(number, min, max) : throw new ToolException($"argument '{name}' must be from {min} to {max}");
        }

        return (long)number;
    }

    public string? Text(string name, int maxLength)
    {
        if (!_args.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            throw new ToolException($"argument '{name}' must be a string");
        }

        return text.Length <= maxLength ? text : throw new ToolException($"argument '{name}' is longer than {maxLength} characters");
    }

    public T? Enum<T>(string name)
        where T : struct, Enum
    {
        var text = Text(name, 32);
        if (text is null)
        {
            return null;
        }

        foreach (var candidate in System.Enum.GetValues<T>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw new ToolException($"argument '{name}' must be one of {string.Join(", ", System.Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}");
    }

    private static string Clip(string text) => text.Length <= 40 ? text : text[..40] + "...";
}
