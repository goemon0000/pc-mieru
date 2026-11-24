using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace PcMieru.Checks;

/// <summary>
/// What a <c>run-&lt;UUID&gt;.json</c> holds (DESIGN-B §7.1). Beyond the fields the design lists, three are written that only ADD to it and that a reader may
/// ignore: <c>rows</c> (the CSV rows written so far), <c>sources</c> (the stable adapter id and name behind each <c>gpu&lt;N&gt;</c>) and a marker's
/// <c>detail</c> (for a <c>warning</c>: <c>late_gpu:&lt;name&gt;</c>).
/// </summary>
internal sealed record RunHeader(
    Guid RunId,
    string App,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    long DurationUs,
    RunStatus Status,
    RunStopReason? StopReason,
    long DroppedTicks,
    long Rows,
    string Csv,
    IReadOnlyList<RunMetricInfo> Metrics,
    IReadOnlyList<RunSourceInfo> Sources,
    IReadOnlyList<RunMarker> Markers);

/// <summary>The run's JSON, written by hand with <see cref="Utf8JsonWriter"/> (no reflection) and read with <see cref="JsonDocument"/>.</summary>
internal static class RunJson
{
    /// <summary>A header is a few kilobytes; a file bigger than this is not one of ours.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);

    public static byte[] Write(RunHeader h)
    {
        var buffer = new ArrayBufferWriter<byte>(2048);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = Encoder }))
        {
            w.WriteStartObject();
            w.WriteString("format", RunCodes.Format);
            w.WriteString("app", h.App);
            w.WriteString("runId", RunCodes.IdText(h.RunId));
            w.WriteString("startedAtUtc", RunCodes.Utc(h.StartedAtUtc));
            if (h.EndedAtUtc is { } ended)
            {
                w.WriteString("endedAtUtc", RunCodes.Utc(ended));
            }
            else
            {
                w.WriteNull("endedAtUtc");
            }

            w.WriteNumber("durationUs", h.DurationUs);
            w.WriteString("status", RunCodes.Of(h.Status));
            if (h.StopReason is { } reason)
            {
                w.WriteString("stopReason", RunCodes.Of(reason));
            }
            else
            {
                w.WriteNull("stopReason");
            }

            w.WriteNumber("droppedTicks", h.DroppedTicks);
            w.WriteNumber("rows", h.Rows);
            w.WriteString("csv", h.Csv);

            w.WriteStartArray("sources");
            foreach (var source in h.Sources)
            {
                w.WriteStartObject();
                w.WriteString("source", source.Source);
                if (source.DeviceId is { } id)
                {
                    w.WriteString("deviceId", id);
                }
                else
                {
                    w.WriteNull("deviceId");
                }

                w.WriteString("name", source.Name);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            // The design calls it a dictionary keyed by the column; it is written as an array of {key,unit,source,kind} so the order of the columns is kept.
            w.WriteStartArray("metrics");
            foreach (var metric in h.Metrics)
            {
                w.WriteStartObject();
                w.WriteString("key", metric.Key);
                w.WriteString("unit", metric.Unit);
                w.WriteString("source", metric.Source);
                w.WriteString("kind", RunCodes.Of(metric.Kind));
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("markers");
            foreach (var marker in h.Markers)
            {
                w.WriteStartObject();
                w.WriteString("kind", RunCodes.Of(marker.Kind));
                w.WriteNumber("offsetUs", marker.OffsetUs);
                w.WriteString("utc", RunCodes.Utc(marker.Utc));
                if (marker.Detail is { Length: > 0 } detail)
                {
                    w.WriteString("detail", detail);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads a <c>pcm-run/1</c> file. <see cref="FormatException"/> when it is not one, or a required field is missing or unreadable.</summary>
    public static RunHeader Read(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "format") != RunCodes.Format)
            {
                throw new FormatException("not a pcm-run/1 file");
            }

            if (!Guid.TryParseExact(Str(root, "runId"), "D", out var runId))
            {
                throw new FormatException("runId is not a UUID");
            }

            var started = Time(root, "startedAtUtc") ?? throw new FormatException("startedAtUtc is missing");
            if (!RunCodes.TryParse(Str(root, "status"), out RunStatus status))
            {
                throw new FormatException("status is not a known code");
            }

            RunStopReason? stopReason = null;
            if (Str(root, "stopReason") is { Length: > 0 } stopCode)
            {
                stopReason = RunCodes.TryParse(stopCode, out RunStopReason parsed) ? parsed : RunStopReason.Unknown;
            }

            var metrics = new List<RunMetricInfo>();
            if (root.TryGetProperty("metrics", out var metricsElement))
            {
                if (metricsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in metricsElement.EnumerateArray())
                    {
                        if (Str(m, "key") is { Length: > 0 } key)
                        {
                            metrics.Add(new RunMetricInfo(key, Str(m, "unit"), Str(m, "source"), Kind(m)));
                        }
                    }
                }
                else if (metricsElement.ValueKind == JsonValueKind.Object)
                {
                    // The design's dictionary form: the property name is the key.
                    foreach (var p in metricsElement.EnumerateObject())
                    {
                        metrics.Add(new RunMetricInfo(p.Name, Str(p.Value, "unit"), Str(p.Value, "source"), Kind(p.Value)));
                    }
                }
            }

            var sources = new List<RunSourceInfo>();
            if (root.TryGetProperty("sources", out var sourcesElement) && sourcesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sourcesElement.EnumerateArray())
                {
                    if (Str(s, "source") is { Length: > 0 } name)
                    {
                        sources.Add(new RunSourceInfo(name, Str(s, "deviceId") is { Length: > 0 } id ? id : null, Str(s, "name")));
                    }
                }
            }

            var markers = new List<RunMarker>();
            if (root.TryGetProperty("markers", out var markersElement) && markersElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in markersElement.EnumerateArray())
                {
                    RunCodes.TryParse(Str(m, "kind"), out MarkerKind kind);
                    markers.Add(new RunMarker(kind, Long(m, "offsetUs"), Time(m, "utc") ?? started, Str(m, "detail") is { Length: > 0 } detail ? detail : null));
                }
            }

            return new RunHeader(
                runId,
                Str(root, "app"),
                started,
                Time(root, "endedAtUtc"),
                Math.Max(0, Long(root, "durationUs")),
                status,
                stopReason,
                Math.Max(0, Long(root, "droppedTicks")),
                Math.Max(0, Long(root, "rows")),
                Str(root, "csv"),
                metrics,
                sources,
                markers);
        }
        catch (JsonException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
    }

    private static RunMetricKind Kind(JsonElement e)
    {
        RunCodes.TryParse(Str(e, "kind"), out RunMetricKind kind);
        return kind;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static DateTime? Time(JsonElement e, string name)
    {
        var text = Str(e, name);
        return text.Length > 0
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;
    }
}
