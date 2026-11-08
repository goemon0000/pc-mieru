using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcMieru.Wire;

/// <summary>camelCase string enums, AOT-safe (put [JsonConverter] on each enum).</summary>
public sealed class CamelCaseEnumConverter<T> : JsonStringEnumConverter<T> where T : struct, Enum
{
    public CamelCaseEnumConverter() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}
