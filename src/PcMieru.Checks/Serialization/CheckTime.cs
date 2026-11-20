using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcMieru.Checks;

/// <summary>
/// The one time format of pcm-check/1: UTC, ISO 8601, milliseconds, a trailing Z ("2026-11-20T03:00:00.000Z").
/// Always 24 characters, so the same instant is always the same bytes.
/// </summary>
public static class CheckTime
{
    private const string Pattern = "yyyy-MM-dd'T'HH':'mm':'ss'.'fff'Z'";

    /// <summary>True for a UTC instant that a JSON time can say exactly (offset zero, whole milliseconds).</summary>
    public static bool IsCanonical(DateTimeOffset time) => time.Offset == TimeSpan.Zero && time.Ticks % TimeSpan.TicksPerMillisecond == 0;

    /// <summary>A clock reading cut down (never rounded up) to whole milliseconds in UTC. Producers call this before they build a result.</summary>
    public static DateTimeOffset TruncateToMilliseconds(DateTimeOffset time)
    {
        var utc = time.UtcDateTime;
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    public static string Format(DateTimeOffset time) => time.UtcDateTime.ToString(Pattern, CultureInfo.InvariantCulture);

    /// <summary>Strict: exactly the 24-character form, UTC, with the Z. Nothing else (no offset, no other precision, no spaces).</summary>
    public static bool TryParse(string? text, out DateTimeOffset time)
    {
        if (text is { Length: 24 }
            && DateTime.TryParseExact(text, Pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            time = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
            return true;
        }

        time = default;
        return false;
    }
}

/// <summary>Reads and writes a DateTimeOffset in the <see cref="CheckTime"/> form. Used by the history DTOs.</summary>
public sealed class CheckUtcTimeJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !CheckTime.TryParse(reader.GetString(), out var time))
        {
            throw new JsonException("A time must be a UTC string like 2026-11-20T03:00:00.000Z.");
        }

        return time;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(CheckTime.Format(value));
}
