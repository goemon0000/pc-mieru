using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcMieru.Checks;

/// <summary>
/// The pcm-check/1 reader and writer. Hand-written so the output is deterministic (fixed key order, fixed number
/// and time forms, decimal strings kept as strings) and the input is strict: an unknown key, an unknown enum value,
/// an unknown item ID, a wrong type, a duplicate key or a non-canonical number is an error, never skipped.
/// Both directions end in <see cref="CheckResultValidator"/>, so what is written and what is read obey the same rules.
/// Errors are <see cref="CheckFormatException"/>.
/// </summary>
public sealed class CheckResultJsonConverter : JsonConverter<CheckResult>
{
    // ---- write ----

    public override void Write(Utf8JsonWriter writer, CheckResult value, JsonSerializerOptions options)
    {
        CheckResultValidator.EnsureValid(value);

        writer.WriteStartObject();
        writer.WriteString("format", value.Format);
        writer.WriteString("app", value.App);
        writer.WriteString("runId", value.RunId.ToString("D", CultureInfo.InvariantCulture));
        writer.WriteString("kind", Name(value.Kind));
        writer.WriteString("startedAtUtc", CheckTime.Format(value.StartedAtUtc));
        writer.WriteString("endedAtUtc", CheckTime.Format(value.EndedAtUtc));
        writer.WriteString("outcome", Name(value.Outcome));
        writer.WriteString("verdict", Name(value.Verdict));

        writer.WriteStartObject("counts");
        writer.WriteNumber("ok", value.Counts.Ok);
        writer.WriteNumber("caution", value.Counts.Caution);
        writer.WriteNumber("check", value.Counts.Check);
        writer.WriteNumber("unavailable", value.Counts.Unavailable);
        writer.WriteNumber("info", value.Counts.Info);
        writer.WriteEndObject();

        writer.WriteStartArray("items");
        foreach (var item in value.Items)
        {
            WriteItem(writer, item);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("notes");
        foreach (var note in value.Notes)
        {
            writer.WriteStringValue(note);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteItem(Utf8JsonWriter writer, CheckItem item)
    {
        writer.WriteStartObject();
        writer.WriteString("id", item.Id);
        if (item.Subject is { } subject)
        {
            writer.WriteStartObject("subject");
            writer.WriteString("id", subject.Id);
            if (subject.Model is not null)
            {
                writer.WriteString("model", subject.Model);
            }

            writer.WriteEndObject();
        }

        writer.WritePropertyName("value");
        WriteValue(writer, item.Value);
        if (item.Unit is null)
        {
            writer.WriteNull("unit");
        }
        else
        {
            writer.WriteString("unit", item.Unit);
        }

        writer.WriteString("verdict", Name(item.Verdict));
        writer.WriteString("source", Name(item.Source));
        if (item.Reason is { } reason)
        {
            writer.WriteString("reason", Name(reason));
        }
        else
        {
            writer.WriteNull("reason");
        }

        if (item.Evidence is { } evidence)
        {
            WriteEvidence(writer, evidence);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, CheckValue? value)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        switch (value.Type)
        {
            case CheckValueType.Int:
                writer.WriteNumberValue(value.AsInt);
                break;
            case CheckValueType.Number:
                writer.WriteNumberValue(value.AsNumber);
                break;
            case CheckValueType.Dec:
            case CheckValueType.SignedDec:
            case CheckValueType.Text:
                writer.WriteStringValue(value.AsText);
                break;
            case CheckValueType.Bool:
                writer.WriteBooleanValue(value.AsBool);
                break;
            case CheckValueType.OsVersion:
                var os = value.AsOsVersion;
                writer.WriteStartObject();
                writer.WriteString("name", os.Name);
                if (os.DisplayVersion is null)
                {
                    writer.WriteNull("displayVersion");
                }
                else
                {
                    writer.WriteString("displayVersion", os.DisplayVersion);
                }

                writer.WriteNumber("build", os.Build);
                if (os.Ubr is { } ubr)
                {
                    writer.WriteNumber("ubr", ubr);
                }
                else
                {
                    writer.WriteNull("ubr");
                }

                writer.WriteEndObject();
                break;
            default:
                throw new CheckFormatException($"Unknown value type {value.Type}.");
        }
    }

    // The key order is the order of DESIGN-A §14.0.
    private static void WriteEvidence(Utf8JsonWriter writer, CheckEvidence e)
    {
        writer.WriteStartObject("evidence");
        if (e.Code is { } code)
        {
            writer.WriteString("code", Name(code));
        }

        if (e.Before is not null)
        {
            writer.WriteString("before", e.Before);
        }

        if (e.After is not null)
        {
            writer.WriteString("after", e.After);
        }

        if (e.SinceUtc is { } since)
        {
            writer.WriteString("sinceUtc", CheckTime.Format(since));
        }

        if (e.NewBits is { } newBits)
        {
            writer.WriteNumber("newBits", newBits);
        }

        if (e.TotalBytes is not null)
        {
            writer.WriteString("totalBytes", e.TotalBytes);
        }

        if (e.DiskId is not null)
        {
            writer.WriteString("diskId", e.DiskId);
        }

        if (e.EfficiencyClass is { } efficiencyClass)
        {
            writer.WriteNumber("efficiencyClass", efficiencyClass);
        }

        if (e.Cv is { } cv)
        {
            writer.WriteNumber("cv", cv);
        }

        if (e.ClockSteadiness is { } clockSteadiness)
        {
            writer.WriteNumber("clockSteadiness", clockSteadiness);
        }

        if (e.WarningTempMinutes is not null)
        {
            writer.WriteString("warningTempMinutes", e.WarningTempMinutes);
        }

        if (e.CriticalTempMinutes is not null)
        {
            writer.WriteString("criticalTempMinutes", e.CriticalTempMinutes);
        }

        if (e.RecentMediaErrors is { } recentMediaErrors)
        {
            writer.WriteNumber("recentMediaErrors", recentMediaErrors);
        }

        if (e.RecentEntries is { } recentEntries)
        {
            writer.WriteNumber("recentEntries", recentEntries);
        }

        if (e.Capacities is { } capacities)
        {
            writer.WriteStartArray("capacities");
            foreach (var capacity in capacities)
            {
                writer.WriteStringValue(capacity);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static string Name<T>(T value) where T : struct, Enum => SnakeCaseEnum<T>.Name(value);

    // ---- read ----

    public override CheckResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var result = ReadResult(document.RootElement);
        CheckResultValidator.EnsureValid(result);
        return result;
    }

    private static CheckResult ReadResult(JsonElement root)
    {
        var obj = Obj.Of(root, "result");

        // The format is checked before anything else: a newer version may have other keys, and the reader must say
        // "unknown format" (keep the file) rather than "broken" (move it aside).
        if (!obj.TryGet("format", out var formatElement) || formatElement.ValueKind != JsonValueKind.String)
        {
            throw new CheckFormatException("format is missing or not a string.");
        }

        var format = formatElement.GetString()!;
        if (format != CheckResult.FormatId)
        {
            throw new CheckFormatException($"Unknown format '{format}'.") { UnknownFormat = format };
        }

        obj.RequireOnly("format", "app", "runId", "kind", "startedAtUtc", "endedAtUtc", "outcome", "verdict", "counts", "items", "notes");

        var runIdText = Str(obj.Required("runId"), "runId");
        if (!Guid.TryParseExact(runIdText, "D", out var runId) || runId.ToString("D", CultureInfo.InvariantCulture) != runIdText)
        {
            throw new CheckFormatException("runId must be a lowercase UUID like 89914ef9-5781-4cf7-a4f8-6d58003ddf86.");
        }

        var kind = ReadEnum<CheckKind>(obj.Required("kind"), "kind");
        var items = ReadItems(obj.Required("items"));
        var notes = ReadStrings(obj.Required("notes"), "notes");

        return new CheckResult(
            format,
            Str(obj.Required("app"), "app"),
            runId,
            kind,
            Time(obj.Required("startedAtUtc"), "startedAtUtc"),
            Time(obj.Required("endedAtUtc"), "endedAtUtc"),
            ReadEnum<CheckOutcome>(obj.Required("outcome"), "outcome"),
            ReadEnum<CheckVerdict>(obj.Required("verdict"), "verdict"),
            ReadCounts(obj.Required("counts")),
            items,
            notes);
    }

    private static CheckCounts ReadCounts(JsonElement element)
    {
        var obj = Obj.Of(element, "counts");
        obj.RequireOnly("ok", "caution", "check", "unavailable", "info");
        return new CheckCounts(
            Int32(obj.Required("ok"), "counts.ok"),
            Int32(obj.Required("caution"), "counts.caution"),
            Int32(obj.Required("check"), "counts.check"),
            Int32(obj.Required("unavailable"), "counts.unavailable"),
            Int32(obj.Required("info"), "counts.info"));
    }

    private static List<CheckItem> ReadItems(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new CheckFormatException("items must be an array.");
        }

        var items = new List<CheckItem>(element.GetArrayLength());
        var index = 0;
        foreach (var entry in element.EnumerateArray())
        {
            items.Add(ReadItem(entry, index++));
        }

        return items;
    }

    private static CheckItem ReadItem(JsonElement element, int index)
    {
        var where = $"items[{index}]";
        var obj = Obj.Of(element, where);
        obj.RequireOnly("id", "subject", "value", "unit", "verdict", "source", "reason", "evidence");
        obj.RequireKeys("id", "value", "unit", "verdict", "source", "reason");

        var id = Str(obj.Required("id"), $"{where}.id");
        if (!CheckItemRegistry.TryGet(id, out var spec))
        {
            throw new CheckFormatException($"{where}: unknown item ID '{id}'.");
        }

        where = $"{where} {id}";

        CheckSubject? subject = null;
        if (obj.TryGet("subject", out var subjectElement))
        {
            var s = Obj.Of(subjectElement, $"{where}.subject");
            s.RequireOnly("id", "model");
            s.RequireKeys("id");
            subject = new CheckSubject(
                Str(s.Required("id"), $"{where}.subject.id"),
                s.TryGet("model", out var model) ? Str(model, $"{where}.subject.model") : null);
        }

        var unitElement = obj.Required("unit");
        var unit = unitElement.ValueKind == JsonValueKind.Null ? null : Str(unitElement, $"{where}.unit");

        var reasonElement = obj.Required("reason");
        CheckReason? reason = reasonElement.ValueKind == JsonValueKind.Null ? null : ReadEnum<CheckReason>(reasonElement, $"{where}.reason");

        CheckEvidence? evidence = obj.TryGet("evidence", out var evidenceElement) ? ReadEvidence(evidenceElement, where) : null;

        return new CheckItem(
            id,
            subject,
            ReadValue(obj.Required("value"), spec, where),
            unit,
            ReadEnum<ItemVerdict>(obj.Required("verdict"), $"{where}.verdict"),
            ReadEnum<CheckSource>(obj.Required("source"), $"{where}.source"),
            reason,
            evidence);
    }

    private static CheckValue? ReadValue(JsonElement element, CheckItemSpec spec, string where)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var at = $"{where}.value";
        switch (spec.ValueType)
        {
            case CheckValueType.Int:
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var i))
                {
                    return CheckValue.Int(i);
                }

                break;
            case CheckValueType.Number:
                if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var d) && double.IsFinite(d))
                {
                    return CheckValue.Number(d);
                }

                break;
            case CheckValueType.Dec:
                if (element.ValueKind == JsonValueKind.String && CheckValue.IsCanonicalDec(element.GetString()))
                {
                    return CheckValue.Dec(element.GetString()!);
                }

                break;
            case CheckValueType.SignedDec:
                if (element.ValueKind == JsonValueKind.String && CheckValue.IsCanonicalSignedDec(element.GetString()))
                {
                    return CheckValue.SignedDec(element.GetString()!);
                }

                break;
            case CheckValueType.Text:
                if (element.ValueKind == JsonValueKind.String)
                {
                    return CheckValue.Text(element.GetString()!);
                }

                break;
            case CheckValueType.Bool:
                if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return CheckValue.Bool(element.GetBoolean());
                }

                break;
            case CheckValueType.OsVersion:
                return CheckValue.OsVersion(ReadOsVersion(element, at));
        }

        throw new CheckFormatException($"{at} must be a {Describe(spec.ValueType)}.");
    }

    private static OsVersionValue ReadOsVersion(JsonElement element, string at)
    {
        var obj = Obj.Of(element, at);
        obj.RequireOnly("name", "displayVersion", "build", "ubr");
        obj.RequireKeys("name", "displayVersion", "build", "ubr");
        var display = obj.Required("displayVersion");
        var ubr = obj.Required("ubr");
        return new OsVersionValue(
            Str(obj.Required("name"), $"{at}.name"),
            display.ValueKind == JsonValueKind.Null ? null : Str(display, $"{at}.displayVersion"),
            Int32(obj.Required("build"), $"{at}.build"),
            ubr.ValueKind == JsonValueKind.Null ? null : Int32(ubr, $"{at}.ubr"));
    }

    private static CheckEvidence ReadEvidence(JsonElement element, string where)
    {
        var at = $"{where}.evidence";
        var obj = Obj.Of(element, at);
        obj.RequireOnly(
            "code", "before", "after", "sinceUtc", "newBits", "totalBytes", "diskId", "efficiencyClass", "cv", "clockSteadiness",
            "warningTempMinutes", "criticalTempMinutes", "recentMediaErrors", "recentEntries", "capacities");

        return new CheckEvidence
        {
            Code = obj.TryGet("code", out var code) ? ReadEnum<CheckEvidenceCode>(code, $"{at}.code") : null,
            Before = OptionalStr(obj, "before", at),
            After = OptionalStr(obj, "after", at),
            SinceUtc = obj.TryGet("sinceUtc", out var since) ? Time(since, $"{at}.sinceUtc") : null,
            NewBits = OptionalInt(obj, "newBits", at),
            TotalBytes = OptionalStr(obj, "totalBytes", at),
            DiskId = OptionalStr(obj, "diskId", at),
            EfficiencyClass = OptionalInt(obj, "efficiencyClass", at),
            Cv = OptionalNumber(obj, "cv", at),
            ClockSteadiness = OptionalNumber(obj, "clockSteadiness", at),
            WarningTempMinutes = OptionalStr(obj, "warningTempMinutes", at),
            CriticalTempMinutes = OptionalStr(obj, "criticalTempMinutes", at),
            RecentMediaErrors = OptionalInt(obj, "recentMediaErrors", at),
            RecentEntries = OptionalInt(obj, "recentEntries", at),
            Capacities = obj.TryGet("capacities", out var capacities) ? ReadStrings(capacities, $"{at}.capacities") : null,
        };
    }

    private static string? OptionalStr(Obj obj, string key, string at) => obj.TryGet(key, out var e) ? Str(e, $"{at}.{key}") : null;

    private static int? OptionalInt(Obj obj, string key, string at) => obj.TryGet(key, out var e) ? Int32(e, $"{at}.{key}") : null;

    private static double? OptionalNumber(Obj obj, string key, string at) =>
        obj.TryGet(key, out var e)
            ? e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) && double.IsFinite(d) ? d : throw new CheckFormatException($"{at}.{key} must be a finite number.")
            : null;

    private static List<string> ReadStrings(JsonElement element, string at)
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

    private static string Str(JsonElement element, string at) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new CheckFormatException($"{at} must be a string.");

    private static int Int32(JsonElement element, string at) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : throw new CheckFormatException($"{at} must be an integer.");

    private static DateTimeOffset Time(JsonElement element, string at) =>
        element.ValueKind == JsonValueKind.String && CheckTime.TryParse(element.GetString(), out var time)
            ? time
            : throw new CheckFormatException($"{at} must be a UTC time like 2026-11-20T03:00:00.000Z.");

    private static T ReadEnum<T>(JsonElement element, string at) where T : struct, Enum
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

    private static string Describe(CheckValueType type) => type switch
    {
        CheckValueType.Int => "JSON integer",
        CheckValueType.Number => "finite JSON number",
        CheckValueType.Dec => "canonical unsigned decimal string",
        CheckValueType.SignedDec => "canonical signed decimal string",
        CheckValueType.Text => "string",
        CheckValueType.Bool => "boolean",
        _ => "value",
    };

    /// <summary>A JSON object read once: duplicate keys are an error, unknown keys are an error, required keys are checked.</summary>
    private sealed class Obj
    {
        private readonly Dictionary<string, JsonElement> _props;
        private readonly string _at;

        private Obj(Dictionary<string, JsonElement> props, string at)
        {
            _props = props;
            _at = at;
        }

        public static Obj Of(JsonElement element, string at)
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

            return new Obj(props, at);
        }

        public bool TryGet(string key, out JsonElement element) => _props.TryGetValue(key, out element);

        public JsonElement Required(string key) =>
            _props.TryGetValue(key, out var element) ? element : throw new CheckFormatException($"{_at}: missing key '{key}'.");

        /// <summary>Every key of the object must be one of these.</summary>
        public void RequireOnly(params string[] allowed)
        {
            foreach (var key in _props.Keys)
            {
                if (Array.IndexOf(allowed, key) < 0)
                {
                    throw new CheckFormatException($"{_at}: unknown key '{key}'.");
                }
            }
        }

        /// <summary>These keys must be present (the others of RequireOnly are optional).</summary>
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
