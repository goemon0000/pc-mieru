// Provenance: the file shape (key, model, entries with at and counters, withheld), SafeName and the file name rule are those of the
// owner's pl-sensors BaselineStore and BaselineIdentity (src/PartsLife.Checks/Storage/Baseline.cs), MIT, same owner.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PcMieru.Checks.Storage;

/// <summary>
/// One recording of the counters of one disk. The file shape is v0.7.4's (<c>at</c>, <c>counters</c>, <c>withheld</c> when true) plus
/// <c>acq</c>, which v0.7.4 skips on reading. A record without <c>acq</c> (everything v0.7.4 wrote) counts as 0.
/// </summary>
internal sealed class BaselineEntry
{
    public DateTimeOffset At { get; init; }

    /// <summary>Counter name (<see cref="BaselineCounters"/>) to value. A counter that did not fit in 64 bits is stored as ulong.MaxValue, as v0.7.4 did.</summary>
    public SortedDictionary<string, ulong> Counters { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The acquisition number of the run that recorded this (DESIGN-A §14.5).</summary>
    public long Acq { get; init; }

    /// <summary>The run's clock was before the last record's, so it could not be compared: never the base of the next comparison (v0.7.4).</summary>
    public bool Withheld { get; init; }
}

/// <summary>One disk's history: one file per disk key.</summary>
internal sealed class BaselineHistory
{
    public string Key { get; init; } = "";

    public string Model { get; set; } = "";

    public List<BaselineEntry> Entries { get; } = [];
}

/// <summary>The names of the baseline: the file name of a key, and what a key, a counter name or a file name may look like.</summary>
internal static class BaselineNames
{
    public const int MaxEntries = 200;

    /// <summary>v0.7.4's <c>BaselineStore.SafeName</c>, letter for letter: what is not an ASCII letter, digit, "-" or "_" becomes "_".</summary>
    public static string SafeName(string key)
    {
        var chars = key.Trim().Select(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray();
        var s = new string(chars).Trim('_');
        if (s.Length == 0)
        {
            s = "unknown";
        }

        return s.Length > 80 ? s[..80] : s;
    }

    /// <summary>The first 16 hex digits (8 bytes, lower case) of SHA-256 over the UTF-8 key.</summary>
    public static string KeyHash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 8));

    /// <summary>"disk-&lt;SafeName(label), at most 40&gt;-&lt;KeyHash&gt;.json" (v0.7.4's <c>BaselineIdentity.FileName</c>).</summary>
    public static string FileName(DiskKey key)
    {
        var prefix = SafeName(key.Label);
        if (prefix.Length > 40)
        {
            prefix = prefix[..40];
        }

        return $"disk-{prefix}-{KeyHash(key.Key)}.json";
    }

    /// <summary>
    /// True for the name of a temporary file this saver makes: the name of one of its files (a history, <c>twins.json</c>,
    /// <c>imported-twins.json</c>, <c>imported-v074.json</c>, <c>disk-ordinals.json</c>, <c>acq-seq.json</c>), a dot, 16 lower case hex
    /// digits and ".tmp". Matched in full: any other ".tmp" file is not ours. There is no process id in the name, so none is looked at.
    /// </summary>
    public static bool IsOwnTemp(string name)
    {
        const string suffix = ".tmp";
        const int nonceLength = 16;
        if (!name.EndsWith(suffix, StringComparison.Ordinal) || name.Length < suffix.Length + 1 + nonceLength + 1)
        {
            return false;
        }

        var body = name[..^suffix.Length];
        var dot = body.Length - nonceLength - 1;
        if (body[dot] != '.' || !body[(dot + 1)..].All(static c => char.IsAsciiHexDigitLower(c)))
        {
            return false;
        }

        var final = body[..dot];
        return final is "twins.json" or "imported-twins.json" or "imported-v074.json" or "disk-ordinals.json" or "acq-seq.json"
            || TryParseFileName(final, out _);
    }

    /// <summary>v1 (before keys) named the file after the serial alone: "disk-&lt;SafeName(serial)&gt;.json".</summary>
    public static string LegacyV1FileName(string serial) => "disk-" + SafeName(serial) + ".json";

    /// <summary>
    /// Takes "disk-&lt;prefix&gt;-&lt;16 hex&gt;.json" apart: a prefix of 1 to 40 characters from the SafeName alphabet, then the lower case hash.
    /// A v1 file name can look like this by accident; only the content (the key and its hash) tells them apart.
    /// </summary>
    public static bool TryParseFileName(string name, out string hash)
    {
        hash = "";
        if (!name.StartsWith("disk-", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal) || name.Length < 5 + 1 + 1 + 16 + 5)
        {
            return false;
        }

        var core = name[5..^5];
        if (core.Length < 18 || core.Length > 40 + 1 + 16 || core[^17] != '-')
        {
            return false;
        }

        var prefix = core[..^17];
        var tail = core[^16..];
        if (!prefix.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') || !tail.All(static c => char.IsAsciiHexDigitLower(c)))
        {
            return false;
        }

        hash = tail;
        return true;
    }

    /// <summary>
    /// "v2|sn=..|uid=..|model=..|size=&lt;digits&gt;|bus=..[|loc=..]". The values escape "|" as "%7C", so splitting on "|" is exact.
    /// <paramref name="allowLocation"/> is false for a twin key (a twin is always the key without the location).
    /// </summary>
    public static bool IsValidKey(string key, bool allowLocation)
    {
        if (key.Length is 0 or > 2048)
        {
            return false;
        }

        var parts = key.Split('|');
        if (parts.Length is not (6 or 7) || parts[0] != DiskKey.Version)
        {
            return false;
        }

        if (!parts[1].StartsWith("sn=", StringComparison.Ordinal) || !parts[2].StartsWith("uid=", StringComparison.Ordinal) || !parts[3].StartsWith("model=", StringComparison.Ordinal))
        {
            return false;
        }

        var size = parts[4];
        if (!size.StartsWith("size=", StringComparison.Ordinal) || size.Length == 5 || !size[5..].All(static c => char.IsAsciiDigit(c)))
        {
            return false;
        }

        if (!parts[5].StartsWith("bus=", StringComparison.Ordinal) || parts[5].Length == 4)
        {
            return false;
        }

        return parts.Length == 6 || (allowLocation && parts[6].StartsWith("loc=", StringComparison.Ordinal));
    }

    /// <summary>"nvme.&lt;lower case letters and _&gt;" or "ata.&lt;0-255, no leading zero&gt;".</summary>
    public static bool IsValidCounterName(string name)
    {
        if (name.StartsWith("nvme.", StringComparison.Ordinal))
        {
            var rest = name.AsSpan(5);
            if (rest.Length is 0 or > 40)
            {
                return false;
            }

            foreach (var c in rest)
            {
                if (c is not ((>= 'a' and <= 'z') or '_'))
                {
                    return false;
                }
            }

            return true;
        }

        if (name.StartsWith("ata.", StringComparison.Ordinal))
        {
            var number = name[4..];
            return byte.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && number == id.ToString(CultureInfo.InvariantCulture);
        }

        return false;
    }
}

/// <summary>The bytes of every baseline file: written (indented UTF-8, no BOM) and read back strictly. A file that does not pass is corrupt.</summary>
internal static class BaselineJson
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow };

    private const int MaxCountersPerEntry = 512;

    private const int MaxKeys = 100_000;

    public static string FormatTime(DateTimeOffset time) => time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    // ---- one disk's history ----

    public static byte[] WriteHistory(BaselineHistory history)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("key", history.Key);
            writer.WriteString("model", history.Model);
            writer.WriteStartArray("entries");
            foreach (var entry in history.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("at", FormatTime(entry.At));
                writer.WriteStartObject("counters");
                foreach (var (name, value) in entry.Counters)
                {
                    writer.WriteNumber(name, value);
                }

                writer.WriteEndObject();
                writer.WriteNumber("acq", entry.Acq);
                if (entry.Withheld)
                {
                    writer.WriteBoolean("withheld", true);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Parses a history. False for anything that is not exactly the shape: a wrong type, a bad time, a counter name or value that cannot be one, more than <see cref="BaselineNames.MaxEntries"/> entries.</summary>
    public static bool TryParseHistory(byte[] bytes, out BaselineHistory? history, bool zeroAcq = false)
    {
        history = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array
                || entries.GetArrayLength() > BaselineNames.MaxEntries)
            {
                return false;
            }

            var model = "";
            if (root.TryGetProperty("model", out var modelElement))
            {
                if (modelElement.ValueKind == JsonValueKind.String)
                {
                    model = modelElement.GetString() ?? "";
                }
                else if (modelElement.ValueKind != JsonValueKind.Null)
                {
                    return false;
                }
            }

            var result = new BaselineHistory { Key = keyElement.GetString() ?? "", Model = model };
            foreach (var element in entries.EnumerateArray())
            {
                if (!TryParseEntry(element, zeroAcq, out var entry))
                {
                    return false;
                }

                result.Entries.Add(entry!);
            }

            history = result;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseEntry(JsonElement element, bool zeroAcq, out BaselineEntry? entry)
    {
        entry = null;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.String || !at.TryGetDateTimeOffset(out var time)
            || !element.TryGetProperty("counters", out var counters) || counters.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var values = new SortedDictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var property in counters.EnumerateObject())
        {
            if (values.Count >= MaxCountersPerEntry
                || !BaselineNames.IsValidCounterName(property.Name)
                || property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetUInt64(out var value)
                || !values.TryAdd(property.Name, value))
            {
                return false;
            }
        }

        long acq = 0;
        if (element.TryGetProperty("acq", out var acqElement))
        {
            if (acqElement.ValueKind != JsonValueKind.Number || !acqElement.TryGetInt64(out acq) || acq < 0)
            {
                return false;
            }
        }

        var withheld = false;
        if (element.TryGetProperty("withheld", out var withheldElement))
        {
            switch (withheldElement.ValueKind)
            {
                case JsonValueKind.True:
                    withheld = true;
                    break;
                case JsonValueKind.False:
                case JsonValueKind.Null:
                    break;
                default:
                    return false;
            }
        }

        entry = new BaselineEntry { At = time, Counters = values, Acq = zeroAcq ? 0 : acq, Withheld = withheld };
        return true;
    }

    // ---- twins.json and imported-twins.json: a JSON array of key strings, sorted, as v0.7.4 wrote twins.json ----

    public static byte[] WriteKeyList(IEnumerable<string> keys)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (var key in keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(key);
            }

            writer.WriteEndArray();
        }

        return stream.ToArray();
    }

    /// <summary>False when it is not an array of plain v2 keys. Every key is checked: a set that is half wrong is not trusted at all.</summary>
    public static bool TryParseKeyList(byte[] bytes, out HashSet<string>? keys)
    {
        keys = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > MaxKeys)
            {
                return false;
            }

            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } key || !BaselineNames.IsValidKey(key, allowLocation: false))
                {
                    return false;
                }

                set.Add(key);
            }

            keys = set;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---- acq-seq.json ----

    public static byte[] WriteSequence(long seq) => WriteSmall(writer => writer.WriteNumber("seq", seq));

    public static bool TryParseSequence(byte[] bytes, out long seq)
    {
        seq = 0;
        try
        {
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && IsFormat1(root)
                && root.TryGetProperty("seq", out var element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt64(out seq)
                && seq >= 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---- disk-ordinals.json: key hash (not the key) to the number shown as "disk-N" ----

    public static byte[] WriteOrdinals(IReadOnlyDictionary<string, int> ordinals) => WriteSmall(writer =>
    {
        writer.WriteStartObject("ordinals");
        foreach (var (hash, number) in ordinals.OrderBy(static pair => pair.Value).ThenBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteNumber(hash, number);
        }

        writer.WriteEndObject();
    });

    public static bool TryParseOrdinals(byte[] bytes, out Dictionary<string, int>? ordinals)
    {
        ordinals = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !IsFormat1(root)
                || !root.TryGetProperty("ordinals", out var map) || map.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            var used = new HashSet<int>();
            foreach (var property in map.EnumerateObject())
            {
                if (property.Name.Length != 16
                    || !property.Name.All(static c => char.IsAsciiHexDigitLower(c))
                    || property.Value.ValueKind != JsonValueKind.Number
                    || !property.Value.TryGetInt32(out var number)
                    || number is < 1 or > 99_999_999
                    || !used.Add(number)
                    || !result.TryAdd(property.Name, number))
                {
                    return false;
                }
            }

            ordinals = result;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---- imported-v074.json: the completion mark of the migration. Counts only: no key, no serial, no content ----

    private const string StatusAbandoned = "abandoned";

    /// <summary>
    /// The mark of a migration that ran to its end. <paramref name="abandoned"/> writes the other mark, "status": "abandoned": the old
    /// set of twins could not be trusted, nothing of the old folder was imported (all the counts are 0) and none of it is to be used
    /// (DESIGN-A §5.3-2). A mark with no "status" is a complete one (what every earlier file is).
    /// </summary>
    public static byte[] WriteMarker(DateTimeOffset completedAt, int histories, int alreadyThere, int skipped, int twins, bool abandoned = false) => WriteSmall(writer =>
    {
        writer.WriteString("completedAtUtc", FormatTime(completedAt));
        writer.WriteNumber("histories", histories);
        writer.WriteNumber("alreadyThere", alreadyThere);
        writer.WriteNumber("skippedFiles", skipped);
        writer.WriteNumber("twins", twins);
        if (abandoned)
        {
            writer.WriteString("status", StatusAbandoned);
        }
    });

    /// <summary>True for a real mark. <paramref name="abandoned"/> says which: "status": "abandoned", or (no "status", or "complete") a finished import. Any other status is not a mark this version knows.</summary>
    public static bool TryReadMarker(byte[] bytes, out bool abandoned)
    {
        abandoned = false;
        try
        {
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !IsFormat1(root)
                || !root.TryGetProperty("completedAtUtc", out var at)
                || at.ValueKind != JsonValueKind.String
                || !at.TryGetDateTimeOffset(out _))
            {
                return false;
            }

            if (!root.TryGetProperty("status", out var status))
            {
                return true;
            }

            if (status.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            switch (status.GetString())
            {
                case StatusAbandoned:
                    abandoned = true;
                    return true;
                case "complete":
                    return true;
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsFormat1(JsonElement root) =>
        root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Number && format.TryGetInt32(out var number) && number == 1;

    private static byte[] WriteSmall(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", 1);
            body(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
