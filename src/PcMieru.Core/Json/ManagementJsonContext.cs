using System.Text.Json.Serialization;

namespace PcMieru.Core;

// Owner D (Management + Helper). Register here: management roots (service/startup/session lists, operation results).
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IReadOnlyList<ServiceInfo>))]
[JsonSerializable(typeof(IReadOnlyList<StartupItem>))]
[JsonSerializable(typeof(IReadOnlyList<SessionInfo>))]
[JsonSerializable(typeof(OperationResult))]
[JsonSerializable(typeof(TerminatePlan))]
[JsonSerializable(typeof(Dictionary<string, PcMieru.Core.Management.EcoRecord>))]
[JsonSerializable(typeof(Dictionary<string, PcMieru.Core.Management.StartupUndoRecord>))]
public partial class ManagementJsonContext : JsonSerializerContext;
