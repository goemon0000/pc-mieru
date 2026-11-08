using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcMieru.Core;

/// <summary>128-bit unsigned counter (NVMe data units, etc.). JSON: decimal string, because JSON numbers lose precision above 2^53.</summary>
[JsonConverter(typeof(U128JsonConverter))]
public readonly record struct U128(UInt128 Value)
{
    public static implicit operator U128(UInt128 value) => new(value);

    public static implicit operator U128(ulong value) => new(value);

    public static implicit operator UInt128(U128 value) => value.Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed class U128JsonConverter : JsonConverter<U128>
{
    public override U128 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        UInt128.TryParse(reader.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? new U128(v)
            : throw new JsonException("Invalid 128-bit decimal string.");

    public override void Write(Utf8JsonWriter writer, U128 value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>ulong that may exceed 2^53 (cumulative transfer counters): decimal string.</summary>
public sealed class UInt64StringJsonConverter : JsonConverter<ulong>
{
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ulong.TryParse(reader.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new JsonException("Invalid 64-bit decimal string.");

    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>UTC instants as fixed-format ISO 8601 ("2026-11-08T01:02:03.456Z"), whatever the DateTime.Kind.</summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
