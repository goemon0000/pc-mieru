using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcMieru.Core;

/// <summary>
/// Identity of a process: PID plus creation time (FILETIME, 100 ns ticks since 1601). A PID alone is never
/// used to act on a process. JSON form is the string "&lt;pid&gt;:&lt;createTime&gt;".
/// </summary>
[JsonConverter(typeof(ProcessKeyJsonConverter))]
public readonly record struct ProcessKey(uint Pid, long CreateTime)
{
    public DateTime StartedAtUtc => CreateTime > 0 ? DateTime.FromFileTimeUtc(CreateTime) : default;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Pid}:{CreateTime}");

    public static bool TryParse(string? text, out ProcessKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
        {
            return false;
        }

        if (!uint.TryParse(text.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !long.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var created))
        {
            return false;
        }

        key = new ProcessKey(pid, created);
        return true;
    }

    public static ProcessKey Parse(string text) =>
        TryParse(text, out var key) ? key : throw new FormatException($"Not a process key: '{text}'");
}

public sealed class ProcessKeyJsonConverter : JsonConverter<ProcessKey>
{
    public override ProcessKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ProcessKey.TryParse(reader.GetString(), out var key) ? key : throw new JsonException("Invalid process key.");

    public override void Write(Utf8JsonWriter writer, ProcessKey value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());

    public override ProcessKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Read(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] ProcessKey value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.ToString());
}
