using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcMieru.Checks;

/// <summary>
/// The pcm-check/1 reader and writer. Hand-written so the output is deterministic (fixed key order, fixed number
/// and time forms, decimal strings kept as strings) and the input is strict: an unknown key, an unknown enum value,
/// an unknown item ID, a wrong type, a duplicate key or a non-canonical number is an error, never skipped. (The tolerant
/// reading of the history is <see cref="CheckResultReader"/> through <see cref="CheckJson.Read(string, CheckReadMode)"/>.)
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

    // The strict reading (the writer's and the tests'). A tolerant read goes through CheckJson.Read(json, CheckReadMode.Tolerant).
    public override CheckResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return CheckResultReader.Read(document.RootElement, CheckReadMode.Strict).Result;
    }
}
