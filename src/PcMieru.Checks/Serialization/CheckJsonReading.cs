using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PcMieru.Checks;

/// <summary>
/// The small readers every pcm-check/1 and history reader shares. Each throws <see cref="CheckFormatException"/> with the
/// place that is wrong. A number is accepted only in the form this library writes (DESIGN-A §3.5-3): the raw token must be
/// the same text the writer would produce, so a result that is read and written again is the same bytes.
/// </summary>
internal static class CheckJsonReading
{
    public static string Str(JsonElement element, string at) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new CheckFormatException($"{at} must be a string.");

    public static int ReadInt(JsonElement element, string at) =>
        element.ValueKind == JsonValueKind.Number
        && element.TryGetInt32(out var value)
        && string.Equals(element.GetRawText(), value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            ? value
            : throw new CheckFormatException($"{at} must be a JSON integer written plainly (no fraction, exponent, plus sign, leading zero or -0).");

    public static int ReadNonNegativeInt(JsonElement element, string at)
    {
        var value = ReadInt(element, at);
        return value >= 0 ? value : throw new CheckFormatException($"{at} must not be negative.");
    }

    public static long ReadLong(JsonElement element, string at) =>
        element.ValueKind == JsonValueKind.Number
        && element.TryGetInt64(out var value)
        && string.Equals(element.GetRawText(), value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            ? value
            : throw new CheckFormatException($"{at} must be a JSON integer written plainly (no fraction, exponent, plus sign, leading zero or -0).");

    /// <summary>A finite JSON number in the canonical form of <see cref="Utf8JsonWriter"/> (so 6400.0, 6.4e3 and -0 are refused).</summary>
    public static double FiniteNumber(JsonElement element, string at)
    {
        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out var value)
            && double.IsFinite(value)
            && !(value == 0 && double.IsNegative(value))
            && string.Equals(element.GetRawText(), WriterText(value), StringComparison.Ordinal))
        {
            return value;
        }

        throw new CheckFormatException($"{at} must be a finite JSON number written canonically (no trailing .0, no exponent that the writer would not use, no -0).");
    }

    /// <summary>The text <see cref="Utf8JsonWriter.WriteNumberValue(double)"/> produces for a number, which is the canonical token.</summary>
    internal static string WriterText(double value)
    {
        var buffer = new ArrayBufferWriter<byte>(32);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteNumberValue(value);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static DateTimeOffset Time(JsonElement element, string at) =>
        element.ValueKind == JsonValueKind.String && CheckTime.TryParse(element.GetString(), out var time)
            ? time
            : throw new CheckFormatException($"{at} must be a UTC time like 2026-11-20T03:00:00.000Z.");

    public static T ReadEnum<T>(JsonElement element, string at) where T : struct, Enum
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new CheckFormatException($"{at} must be a string.");
        }

        var text = element.GetString();
        return SnakeCaseEnum<T>.TryParse(text, out var value)
            ? value
            : throw new CheckFormatException($"{at}: unknown {typeof(T).Name.Replace("Check", string.Empty, StringComparison.Ordinal).ToLowerInvariant()} '{text}'.");
    }

    public static Guid RunId(JsonElement element, string at)
    {
        var text = Str(element, at);
        return Guid.TryParseExact(text, "D", out var runId) && runId.ToString("D", CultureInfo.InvariantCulture) == text
            ? runId
            : throw new CheckFormatException($"{at} must be a lowercase UUID like 89914ef9-5781-4cf7-a4f8-6d58003ddf86.");
    }

    public static CheckCounts ReadCounts(JsonElement element, bool lenient, string at = "counts")
    {
        var obj = Obj.Of(element, at, lenient);
        obj.RequireOnly("ok", "caution", "check", "unavailable", "info");
        return new CheckCounts(
            ReadInt(obj.Required("ok"), $"{at}.ok"),
            ReadInt(obj.Required("caution"), $"{at}.caution"),
            ReadInt(obj.Required("check"), $"{at}.check"),
            ReadInt(obj.Required("unavailable"), $"{at}.unavailable"),
            ReadInt(obj.Required("info"), $"{at}.info"));
    }

    public static List<string> ReadStrings(JsonElement element, string at)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new CheckFormatException($"{at} must be an array of strings.");
        }

        var list = new List<string>(element.GetArrayLength());
        foreach (var entry in element.EnumerateArray())
        {
            list.Add(Str(entry, at));
        }

        return list;
    }

    /// <summary>A lowercase name a later version may have added for a source, a kind or an evidence code: [a-z][a-z0-9_]*.</summary>
    public static bool IsToken(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// An item ID of the shape the registry uses (lowercase segments joined by dots, at least two, the first starting with
    /// a letter). A tolerant reader skips such an ID it does not know; anything else is a broken file.
    /// </summary>
    public static bool IsFutureItemId(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] is < 'a' or > 'z')
        {
            return false;
        }

        var segments = 0;
        var length = 0;
        foreach (var c in text)
        {
            if (c == '.')
            {
                if (length == 0)
                {
                    return false;
                }

                segments++;
                length = 0;
            }
            else if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
            {
                length++;
            }
            else
            {
                return false;
            }
        }

        return length > 0 && segments >= 1;
    }

    /// <summary>A JSON object read once: duplicate keys are an error; unknown keys are an error unless the reader is lenient; required keys are checked.</summary>
    internal sealed class Obj
    {
        private readonly Dictionary<string, JsonElement> _props;
        private readonly string _at;
        private readonly bool _lenient;

        private Obj(Dictionary<string, JsonElement> props, string at, bool lenient)
        {
            _props = props;
            _at = at;
            _lenient = lenient;
        }

        public static Obj Of(JsonElement element, string at, bool lenient = false)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new CheckFormatException($"{at} must be an object.");
            }

            var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!props.TryAdd(property.Name, property.Value))
                {
                    throw new CheckFormatException($"{at}: duplicate key '{property.Name}'.");
                }
            }

            return new Obj(props, at, lenient);
        }

        public bool TryGet(string key, out JsonElement element) => _props.TryGetValue(key, out element);

        public JsonElement Required(string key) =>
            _props.TryGetValue(key, out var element) ? element : throw new CheckFormatException($"{_at}: missing key '{key}'.");

        /// <summary>Every key of the object must be one of these. A lenient reader ignores the others (DESIGN-A §3.5-1).</summary>
        public void RequireOnly(params string[] allowed)
        {
            if (_lenient)
            {
                return;
            }

            foreach (var key in _props.Keys)
            {
                if (Array.IndexOf(allowed, key) < 0)
                {
                    throw new CheckFormatException($"{_at}: unknown key '{key}'.");
                }
            }
        }

        /// <summary>These keys must be present (the others of RequireOnly are optional). Required in both modes.</summary>
        public void RequireKeys(params string[] required)
        {
            foreach (var key in required)
            {
                if (!_props.ContainsKey(key))
                {
                    throw new CheckFormatException($"{_at}: missing key '{key}'.");
                }
            }
        }
    }
}
