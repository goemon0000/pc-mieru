using System.Text.Json.Serialization;

namespace PcMieru.Core;

// Owner A (Collection). Register here: Snapshot-side roots and the T of every Value<T> in snapshot/process types.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Snapshot))]
[JsonSerializable(typeof(ProcessList))]
[JsonSerializable(typeof(ProcessDetail))]
[JsonSerializable(typeof(ProcessDetails))]
[JsonSerializable(typeof(ProcessState))]
[JsonSerializable(typeof(SignatureInfo))]
public partial class CollectionJsonContext : JsonSerializerContext;
