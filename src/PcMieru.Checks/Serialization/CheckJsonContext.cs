using System.Text.Json.Serialization;

namespace PcMieru.Checks;

/// <summary>
/// Source-generated metadata for the pcm-check/1 shapes. camelCase; every enum is a lowercase snake_case string through
/// its own converter; times go through <see cref="CheckUtcTimeJsonConverter"/>. A result itself is read and written by
/// <see cref="CheckResultJsonConverter"/> (strict, fixed key order). Use <see cref="CheckJson"/>, not this class, to
/// serialize: it adds the indented form and the strict entry points.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Converters = [typeof(CheckUtcTimeJsonConverter)])]
[JsonSerializable(typeof(CheckResult))]
[JsonSerializable(typeof(CheckCounts))]
[JsonSerializable(typeof(CheckHistoryEntry))]
[JsonSerializable(typeof(CheckHistoryWarning))]
[JsonSerializable(typeof(CheckHistoryPage))]
internal sealed partial class CheckJsonContext : JsonSerializerContext;
