using System.Text.Json.Serialization;

namespace PcMieru.Wire;

/// <summary>
/// What the Helper writes to the caller's pre-created result file. It is only ever used for display:
/// the caller trusts the exit code, and reads this only if the file name matches its nonce.
/// </summary>
public sealed record HelperResult(string Nonce, string Op, OperationCode Code, string? Message = null, Dictionary<string, string>? Data = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelperResult))]
public partial class WireJsonContext : JsonSerializerContext;
