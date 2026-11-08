using System.Text.Json.Serialization;

namespace PcMieru.Core;

// Owner E (Cli + Reporting + MCP). Register here: observation and report roots.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Observation))]
[JsonSerializable(typeof(IReadOnlyList<Observation>))]
[JsonSerializable(typeof(SystemSummary))]
[JsonSerializable(typeof(DiagnoseResult))]
[JsonSerializable(typeof(ReportDocument))]
public partial class ReportingJsonContext : JsonSerializerContext;
