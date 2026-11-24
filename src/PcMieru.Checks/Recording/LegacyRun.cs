using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PcMieru.Checks;

/// <summary>An old run's header and the sampling period its file names (<c>intervalMs</c>, at least 100).</summary>
internal sealed record LegacyHeader(RunInfo Info, int IntervalMs);

/// <summary>A legacy run's series (for the summary), the metrics its CSV header gave and how many rows were read.</summary>
internal sealed record LegacyData(RunSeries Series, IReadOnlyList<RunMetricInfo> Metrics, long Rows);

/// <summary>
/// Reads the old <c>partslife-run/1</c> runs (DESIGN-B §7.1, step 7). ONLY reads: nothing is rewritten and nothing is migrated, the files stay as they
/// are. The row time <c>t_s</c> stands for the acquire time and the file's <c>intervalMs</c> for the period, so the summary says "old format,
/// estimated" (<see cref="SummaryFreshness.LegacyEstimated"/>). The old column names are mapped to the new keys where there is a counterpart; the rest keep
/// their old key (source <c>legacy</c>). The per-logical-processor columns are not read (the old summary only used their average).
/// </summary>
internal static class LegacyRun
{
    public const string FormatName = "partslife-run";

    /// <summary>The source name of a column that has no counterpart in the new vocabulary.</summary>
    public const string LegacySource = "legacy";

    /// <summary>The old run id is lowercase letters and digits, 8 to 40 long (it was used to build file names, so nothing else is accepted).</summary>
    public static bool IsValidId(string? id)
    {
        if (id is null || id.Length is < 8 or > 40)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The new id of an old run: the MD5 of <c>partslife-run/1:&lt;runId&gt;</c> read as a UUID. It is a stable name, not a secret (nothing here is signed),
    /// so a listing of the old runs gives the same ids every time without a file being written.
    /// </summary>
#pragma warning disable CA5351 // MD5 is only used to derive a stable id from a name; nothing depends on its collision resistance.
    public static Guid IdOf(string legacyId) => new(MD5.HashData(Encoding.UTF8.GetBytes(RunCodes.LegacyFormat + ":" + legacyId)));
#pragma warning restore CA5351

    public static string JsonName(string legacyId) => "run-" + legacyId + ".json";

    public static string CsvName(string legacyId) => "sensors-" + legacyId + ".csv";

    /// <summary>True for a file name <c>run-&lt;id&gt;.json</c> whose id is a valid old id.</summary>
    public static bool TryIdOfJson(string fileName, out string legacyId)
    {
        legacyId = "";
        if (!fileName.StartsWith("run-", StringComparison.Ordinal) || !fileName.EndsWith(".json", StringComparison.Ordinal) || fileName.Length <= 9)
        {
            return false;
        }

        var middle = fileName.Substring(4, fileName.Length - 9);
        if (!IsValidId(middle))
        {
            return false;
        }

        legacyId = middle;
        return true;
    }

    /// <summary>Reads the header. <see cref="FormatException"/> when it is not a <c>partslife-run</c> version 1 file or a required field is missing.</summary>
    public static LegacyHeader ParseHeader(byte[] json, string jsonPath, string csvPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "format") != FormatName)
            {
                throw new FormatException("not a partslife-run file");
            }

            var version = (int)Long(root, "formatVersion");
            if (version != 1)
            {
                throw new FormatException($"formatVersion {version} is not readable");
            }

            var legacyId = Str(root, "runId");
            if (!IsValidId(legacyId))
            {
                throw new FormatException("runId is not valid");
            }

            var started = Time(root, "startUtc") ?? throw new FormatException("startUtc is missing");
            var ended = Time(root, "endUtc");
            var interval = Math.Max(100, root.TryGetProperty("intervalMs", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var ms) ? ms : 1000);

            var appName = "PartsLife";
            var appVersion = "";
            if (root.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.Object)
            {
                appName = Str(app, "name") is { Length: > 0 } n ? n : appName;
                appVersion = Str(app, "version");
            }

            // A "recording" that is found on the disk was cut off (the old recorder had no lock): it is read as interrupted.
            var status = Str(root, "status") switch
            {
                "completed" => RunStatus.Completed,
                "recording" or "interrupted" => RunStatus.Interrupted,
                _ => RunStatus.Failed,
            };
            RunStopReason? stopReason = status == RunStatus.Completed ? null : RunStopReason.Unknown;
            var stop = Str(root, "stopReason");
            if (stop == "user")
            {
                stopReason = RunStopReason.User;
            }
            else if (stop == "io-error")
            {
                stopReason = RunStopReason.IoError;
            }

            var markers = new List<RunMarker>();
            if (root.TryGetProperty("markers", out var markersElement) && markersElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in markersElement.EnumerateArray())
                {
                    if (RunCodes.TryParse(Str(m, "kind"), out MarkerKind kind))
                    {
                        markers.Add(new RunMarker(kind, ToUs(Double(m, "tSec")), Time(m, "utc") ?? started));
                    }
                }
            }

            var sources = new List<RunSourceInfo> { new(RunMetrics.Core, null, "") };
            if (root.TryGetProperty("gpus", out var gpus) && gpus.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in gpus.EnumerateArray())
                {
                    var gid = Str(g, "id");
                    if (RunMetrics.IsGpuSource(gid))
                    {
                        sources.Add(new RunSourceInfo(gid, null, Str(g, "name")));
                    }
                }
            }

            var metrics = new List<RunMetricInfo>();
            long samples = 0;
            if (root.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.Object
                && stream.TryGetProperty("columns", out var columns) && columns.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in columns.EnumerateArray())
                {
                    if (c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } key && !IsPerLogicalProcessor(key))
                    {
                        metrics.Add(Map(key));
                    }
                }
            }

            if (root.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
            {
                samples = Math.Max(0, Long(stats, "samples"));
            }

            var duration = ended is { } end && end >= started ? (end - started).Ticks / 10 : 0;
            var info = new RunInfo(
                IdOf(legacyId),
                RunCodes.LegacyFormat,
                (appName + " " + appVersion).Trim(),
                IsLegacy: true,
                legacyId,
                started,
                ended,
                duration,
                status,
                stopReason,
                DroppedTicks: 0,
                samples,
                metrics,
                sources,
                markers,
                jsonPath,
                csvPath);
            return new LegacyHeader(info, interval);
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

    /// <summary>
    /// Reads the CSV (<c>t_s,utc,...</c>) up to its last COMPLETE line. A line with the wrong number of cells, a bad time, or a time that goes back
    /// or stands still is skipped. A cell with a number is a new value acquired at <c>t_s</c> with the file's period; an empty cell is "could not be read".
    /// </summary>
    public static LegacyData ReadSeries(LegacyHeader header, byte[] csvBytes)
    {
        var end = Array.LastIndexOf(csvBytes, (byte)'\n');
        var text = end < 0 ? "" : Encoding.UTF8.GetString(csvBytes, 0, end + 1);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var info = header.Info;
        var periodMs = header.IntervalMs;
        var tracks = new List<MetricTrack>();
        var columnTrack = Array.Empty<MetricTrack?>();
        var metrics = new List<RunMetricInfo>();
        var names = Array.Empty<string>();
        long rows = 0;
        long lastUs = -1;
        var headerSeen = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (!headerSeen)
            {
                names = line.Split(',');
                if (names.Length < 2 || names[0] != "t_s" || names[1] != "utc")
                {
                    throw new FormatException("the first columns are not t_s,utc");
                }

                columnTrack = new MetricTrack?[names.Length];
                for (var c = 2; c < names.Length; c++)
                {
                    if (IsPerLogicalProcessor(names[c]))
                    {
                        continue;
                    }

                    var metric = Map(names[c]);
                    metrics.Add(metric);
                    var track = new MetricTrack(metric.Key, metric.Unit, metric.Source, metric.Kind);
                    tracks.Add(track);
                    columnTrack[c] = track;
                }

                headerSeen = true;
                continue;
            }

            var cells = line.Split(',');
            if (cells.Length != names.Length)
            {
                continue; // cut off or damaged
            }

            if (!double.TryParse(cells[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds < 0)
            {
                continue;
            }

            var atUs = ToUs(seconds);
            if (atUs <= lastUs)
            {
                continue; // the time went back or stood still: not trusted
            }

            lastUs = atUs;
            rows++;
            for (var c = 2; c < cells.Length; c++)
            {
                if (columnTrack[c] is not { } track)
                {
                    continue;
                }

                if (cells[c].Length > 0
                    && double.TryParse(cells[c], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    && double.IsFinite(value))
                {
                    // The old throttle column went through a double; its bits are the number's whole value.
                    track.AddValue(atUs, periodMs, value, track.Kind == RunMetricKind.Bitmask ? ToBits(value) : 0);
                }
                else
                {
                    track.AddMissing(atUs);
                }
            }
        }

        var normal = info.Status == RunStatus.Completed;
        var series = new RunSeries
        {
            Markers = info.Markers,
            NormalEnd = normal,
            EndUs = rows == 0 ? 0 : normal ? lastUs + periodMs * 1000L : lastUs,
            Rows = rows,
            DroppedTicks = 0,
            Status = info.Status,
            StopReason = info.StopReason,
            Legacy = true,
        };
        series.Tracks.AddRange(tracks);
        return new LegacyData(series, metrics, rows);
    }

    /// <summary>The new key, unit, source and kind of an old column; a column with no counterpart keeps its old key.</summary>
    public static RunMetricInfo Map(string legacyKey)
    {
        switch (legacyKey)
        {
            case "cpu.load_pct":
                return Describe(RunMetrics.CoreKey(RunMetrics.CpuUsage), RunMetrics.CpuUsage, RunMetrics.Core);
            case "cpu.clock_mhz":
                return Describe(RunMetrics.CoreKey(RunMetrics.CpuMhz), RunMetrics.CpuMhz, RunMetrics.Core);
            case "cpu.pkg_power_w":
                return Describe(RunMetrics.CoreKey(RunMetrics.CpuPackageW), RunMetrics.CpuPackageW, RunMetrics.Core);
            case "power.ac":
                return Describe(RunMetrics.CoreKey(RunMetrics.AcConnected), RunMetrics.AcConnected, RunMetrics.Core);
            case "power.battery_pct":
                return Describe(RunMetrics.CoreKey(RunMetrics.BatteryPct), RunMetrics.BatteryPct, RunMetrics.Core);
        }

        // The cpu.perf_limit_pct, cpu.perf_limit_flags and cpu.temp_c columns keep their old keys: the CPU-limit and clock-drop notes read them by name.
        if (TryGpu(legacyKey, out var number, out var metric))
        {
            var source = RunMetrics.GpuSource(number);
            switch (metric)
            {
                case "util_pct":
                    return Describe(RunMetrics.CoreGpuKey(number, RunMetrics.GpuUtil), RunMetrics.GpuUtil, RunMetrics.Core);
                case "temp_c":
                    return Describe(RunMetrics.GpuKey(number, RunMetrics.GpuTemp), RunMetrics.GpuTemp, source);
                case "clock_mhz":
                    return Describe(RunMetrics.GpuKey(number, RunMetrics.GpuClock), RunMetrics.GpuClock, source);
                case "power_w":
                    return Describe(RunMetrics.GpuKey(number, RunMetrics.GpuPower), RunMetrics.GpuPower, source);
                case "fan_pct":
                    return Describe(RunMetrics.GpuKey(number, RunMetrics.GpuFan), RunMetrics.GpuFan, source);
                case "throttle":
                    return Describe(RunMetrics.GpuKey(number, RunMetrics.GpuLimitReasons), RunMetrics.GpuLimitReasons, source);
            }
        }

        return new RunMetricInfo(legacyKey, UnitOf(legacyKey), LegacySource, RunMetricKind.Number);
    }

    private static RunMetricInfo Describe(string key, RunMetricDef def, string source) => new(key, def.Unit, source, def.Kind);

    private static bool TryGpu(string key, out int number, out string metric)
    {
        number = 0;
        metric = "";
        if (!key.StartsWith("gpu", StringComparison.Ordinal))
        {
            return false;
        }

        var dot = key.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 3 || !int.TryParse(key.AsSpan(3, dot - 3), NumberStyles.None, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        metric = key[(dot + 1)..];
        return metric.Length > 0;
    }

    private static string UnitOf(string key)
    {
        if (key.EndsWith("_pct", StringComparison.Ordinal))
        {
            return "%";
        }

        if (key.EndsWith("_mhz", StringComparison.Ordinal))
        {
            return "MHz";
        }

        if (key.EndsWith("_mbps", StringComparison.Ordinal))
        {
            return "MB/s";
        }

        if (key.EndsWith("_mb", StringComparison.Ordinal))
        {
            return "MB";
        }

        if (key.EndsWith("_rpm", StringComparison.Ordinal))
        {
            return "rpm";
        }

        if (key.EndsWith("_w", StringComparison.Ordinal))
        {
            return "W";
        }

        if (key.EndsWith("_c", StringComparison.Ordinal))
        {
            return "°C";
        }

        if (key.EndsWith("_v", StringComparison.Ordinal))
        {
            return "V";
        }

        return "";
    }

    /// <summary><c>cpu.lp12.load_pct</c>: the old per-logical-processor columns, which are not read.</summary>
    private static bool IsPerLogicalProcessor(string key) =>
        key.StartsWith("cpu.lp", StringComparison.Ordinal) && key.Length > 6 && char.IsAsciiDigit(key[6]);

    private static long ToUs(double seconds) => (long)Math.Round(seconds * 1_000_000d, MidpointRounding.AwayFromZero);

    /// <summary>A legacy bit mask was written through a double: its value is the number (clamped into the unsigned range).</summary>
    private static ulong ToBits(double value) =>
        value <= 0 ? 0UL : value >= 18446744073709551615d ? ulong.MaxValue : (ulong)Math.Round(value);

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static double Double(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

    private static DateTime? Time(JsonElement e, string name)
    {
        var text = Str(e, name);
        return text.Length > 0
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;
    }
}
