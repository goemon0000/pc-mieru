using System.Globalization;
using System.Text;
using PcMieru.Core;

namespace PcMieru.Checks;

/// <summary>One source of a wide CSV (<c>core</c> or <c>gpu&lt;N&gt;</c>): its five meta columns and then its values.</summary>
internal sealed class LayoutSource(string name, string? deviceId, string deviceName)
{
    public string Name { get; } = name;

    public string? DeviceId { get; } = deviceId;

    public string DeviceName { get; } = deviceName;

    /// <summary>The value columns after the meta columns, in order, as full keys (<c>core.cpu_usage_pct</c>, <c>core.gpu1_util_pct</c>, <c>gpu1.temp_c</c>).</summary>
    public List<(string Key, RunMetricDef Def)> Values { get; } = [];
}

/// <summary>
/// The columns of one run, fixed at its first accepted sample (DESIGN-B §7.1): <c>offsetUs,utc</c>, then per source
/// <c>&lt;src&gt;.frame,.acquiredOffsetUs,.periodMs,.state,.reason</c> and the source's values. <c>core</c> comes first, then <c>gpu1..gpuN</c> in the order of
/// the GPUs' stable ids. A GPU that shows up later has no column.
/// </summary>
internal sealed class RunLayout
{
    public const string OffsetColumn = "offsetUs";
    public const string UtcColumn = "utc";

    private readonly Dictionary<string, int> _gpuIndex = new(StringComparer.Ordinal);

    private RunLayout()
    {
    }

    public List<LayoutSource> Sources { get; } = [];

    public List<string> Header { get; } = [OffsetColumn, UtcColumn];

    public List<RunMetricInfo> Metrics { get; } = [];

    public List<RunSourceInfo> SourceInfos { get; } = [];

    public static RunLayout Create(IReadOnlyList<RecordedGpu> gpus)
    {
        var layout = new RunLayout();
        var core = new LayoutSource(RunMetrics.Core, null, "core");
        foreach (var def in RunMetrics.CoreOnly)
        {
            core.Values.Add((RunMetrics.CoreKey(def), def));
        }

        var ordered = gpus.OrderBy(g => g.Id, StringComparer.Ordinal).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var number = i + 1;
            core.Values.Add((RunMetrics.CoreGpuKey(number, RunMetrics.GpuUtil), RunMetrics.GpuUtil));
            core.Values.Add((RunMetrics.CoreGpuKey(number, RunMetrics.GpuVram), RunMetrics.GpuVram));
        }

        layout.Sources.Add(core);
        for (var i = 0; i < ordered.Count; i++)
        {
            var source = new LayoutSource(RunMetrics.GpuSource(i + 1), ordered[i].Id, ordered[i].Name);
            foreach (var def in RunMetrics.GpuVendor)
            {
                source.Values.Add((RunMetrics.GpuKey(i + 1, def), def));
            }

            layout._gpuIndex[ordered[i].Id] = i + 1;
            layout.Sources.Add(source);
        }

        foreach (var source in layout.Sources)
        {
            layout.SourceInfos.Add(new RunSourceInfo(source.Name, source.DeviceId, source.DeviceName));
            layout.Header.Add(source.Name + ".frame");
            layout.Header.Add(source.Name + ".acquiredOffsetUs");
            layout.Header.Add(source.Name + ".periodMs");
            layout.Header.Add(source.Name + ".state");
            layout.Header.Add(source.Name + ".reason");
            foreach (var (key, def) in source.Values)
            {
                layout.Header.Add(key);
                layout.Metrics.Add(new RunMetricInfo(key, def.Unit, source.Name, def.Kind));
            }
        }

        return layout;
    }

    /// <summary>The index in <see cref="Sources"/> of the GPU with this id, or -1 when it was not there at the start.</summary>
    public int SourceOfGpu(string id) => _gpuIndex.TryGetValue(id, out var index) ? index : -1;

    public string HeaderLine() => string.Join(',', Header) + "\n";
}

/// <summary>
/// Formats a sample as one CSV row (DESIGN-B §7.1). It remembers the last frame it SAW per source, so a frame id it has seen again is a
/// <c>repeat</c>; a missing row in between does not forget it (a restarted host has a new generation, so its first frame is fresh anyway).
/// UTF-8 without a BOM, the invariant culture, a decimal point; NaN and infinity are missing (an empty cell).
/// </summary>
internal sealed class RunRowFormatter(RunLayout layout)
{
    private readonly FrameId?[] _lastFrame = new FrameId?[layout.Sources.Count];
    private readonly StringBuilder _row = new(512);

    /// <summary>Returns the row without its newline. <paramref name="offsetUs"/> is the row time; <paramref name="startUs"/> the monotonic start of the run.</summary>
    public string Format(RecordingSample sample, long offsetUs, long startUs)
    {
        var row = _row;
        row.Clear();
        row.Append(offsetUs.ToString(CultureInfo.InvariantCulture)).Append(',').Append(RunCodes.Utc(sample.TakenAtUtc));

        // core
        var coreFresh = AppendMeta(0, sample.Core, startUs);
        AppendCoreValues(row, sample, coreFresh);

        // gpu1..gpuN
        for (var i = 1; i < layout.Sources.Count; i++)
        {
            var gpu = FindGpu(sample, layout.Sources[i].DeviceId);
            if (gpu is null)
            {
                // The adapter is not in this sample: gone for now.
                AppendMissingMeta(row, null, RunCodes.ReasonDeviceGone);
                AppendEmpty(row, layout.Sources[i].Values.Count);
                continue;
            }

            var present = AppendMeta(i, gpu.Vendor, startUs);
            AppendGpuValues(row, gpu, present);
        }

        return row.ToString();
    }

    private static RecordedGpu? FindGpu(RecordingSample sample, string? id)
    {
        foreach (var gpu in sample.Gpus)
        {
            if (gpu.Id == id)
            {
                return gpu;
            }
        }

        return null;
    }

    /// <summary>Writes the five meta cells of a source; returns true when values follow (fresh or repeat).</summary>
    private bool AppendMeta(int index, SourceOrigin origin, long startUs)
    {
        if (origin.Frame is not { } frame)
        {
            AppendMissingMeta(_row, origin.PeriodMs > 0 ? origin.PeriodMs : null, origin.Gap == SourceGap.None ? RunCodes.ReasonNoFrame : RunCodes.ReasonOf(origin.Gap));
            return false;
        }

        var repeat = _lastFrame[index] is { } last && last == frame;
        _lastFrame[index] = frame;
        _row.Append(',').Append(frame.ToString())
            .Append(',').Append((origin.AcquiredUs - startUs).ToString(CultureInfo.InvariantCulture))
            .Append(',').Append(origin.PeriodMs.ToString(CultureInfo.InvariantCulture))
            .Append(',').Append(repeat ? RunCodes.StateRepeat : RunCodes.StateFresh)
            .Append(',');
        return true;
    }

    private static void AppendMissingMeta(StringBuilder row, int? periodMs, string reason)
    {
        row.Append(",,,");
        if (periodMs is { } p)
        {
            row.Append(p.ToString(CultureInfo.InvariantCulture));
        }

        row.Append(',').Append(RunCodes.StateMissing).Append(',').Append(reason);
    }

    private static void AppendEmpty(StringBuilder row, int count)
    {
        for (var i = 0; i < count; i++)
        {
            row.Append(',');
        }
    }

    private void AppendCoreValues(StringBuilder row, RecordingSample sample, bool present)
    {
        if (!present)
        {
            AppendEmpty(row, layout.Sources[0].Values.Count);
            return;
        }

        var memory = sample.Memory;
        Number(row, sample.CpuUsagePercent);
        Number(row, sample.CpuMhz);
        Number(row, sample.CpuPackageWatts);
        Whole(row, memory.TotalPhysicalBytes > 0 ? memory.TotalPhysicalBytes : null);
        Whole(row, memory.TotalPhysicalBytes > 0 ? memory.AvailablePhysicalBytes : null);
        Whole(row, memory.CommitTotalBytes);
        Whole(row, memory.CommitLimitBytes);
        Whole(row, memory.CacheBytes);
        Whole(row, memory.PagedPoolBytes);
        Whole(row, memory.NonPagedPoolBytes);
        Flag(row, sample.Power.AcConnected);
        Whole(row, sample.Power.BatteryPercent.TryGet(out var battery) ? battery : null);
        Text(row, sample.Power.BatteryState);

        // The GPUs' PDH values (their frame is the core frame), in the layout's order.
        for (var i = 1; i < layout.Sources.Count; i++)
        {
            var gpu = FindGpu(sample, layout.Sources[i].DeviceId);
            if (gpu is null)
            {
                AppendEmpty(row, 2);
                continue;
            }

            Number(row, gpu.UtilizationPercent);
            Whole(row, gpu.DedicatedUsedBytes);
        }
    }

    private static void AppendGpuValues(StringBuilder row, RecordedGpu gpu, bool present)
    {
        if (!present)
        {
            AppendEmpty(row, RunMetrics.GpuVendor.Length);
            return;
        }

        Number(row, gpu.TemperatureC);
        Number(row, gpu.PowerWatts);
        Number(row, gpu.FanPercent);
        Number(row, gpu.ClockMhz);
        Number(row, gpu.TemperatureLimitC);

        // The bit mask is a decimal string of the unsigned value: never through a double (a 64-bit mask would lose its low bits).
        row.Append(',');
        if (gpu.LimitReasons.TryGet(out var bits))
        {
            row.Append(bits.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void Number(StringBuilder row, Value<double> value)
    {
        row.Append(',');
        if (value.TryGet(out var d) && double.IsFinite(d))
        {
            row.Append(d.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void Whole(StringBuilder row, Value<long> value) => Whole(row, value.TryGet(out var l) ? l : null);

    private static void Whole(StringBuilder row, long? value)
    {
        row.Append(',');
        if (value is { } l)
        {
            row.Append(l.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void Flag(StringBuilder row, Value<bool> value)
    {
        row.Append(',');
        if (value.TryGet(out var b))
        {
            row.Append(b ? '1' : '0');
        }
    }

    private static void Text(StringBuilder row, Value<string> value)
    {
        row.Append(',');
        if (!value.TryGet(out var text) || string.IsNullOrEmpty(text))
        {
            return;
        }

        if (text.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            row.Append(text);
            return;
        }

        row.Append('"');
        foreach (var c in text)
        {
            if (c == '"')
            {
                row.Append("\"\"");
            }
            else
            {
                row.Append(c is '\r' or '\n' ? ' ' : c);
            }
        }

        row.Append('"');
    }
}

/// <summary>What one source did over the run: how many rows were fresh, repeated or missing, and the reason codes the missing ones gave.</summary>
internal sealed class SourceCounts
{
    public long Fresh { get; set; }

    public long Repeat { get; set; }

    public long Missing { get; set; }

    public List<string> Reasons { get; } = [];
}

/// <summary>What a CSV holds: the tracks for the summary, how many rows, where the last good row is.</summary>
internal sealed class RunCsvData
{
    public List<MetricTrack> Tracks { get; } = [];

    public long Rows { get; set; }

    /// <summary>The offset of the last good row (microseconds since the start); 0 when there is none.</summary>
    public long LastOffsetUs { get; set; }

    /// <summary>Lines that were skipped (short, wrong column count, unreadable time, time that did not advance).</summary>
    public long SkippedLines { get; set; }

    public Dictionary<string, SourceCounts> Sources { get; } = new(StringComparer.Ordinal);

    public List<string> Header { get; } = [];
}

/// <summary>
/// Reads a wide CSV tolerantly: only up to the last complete line (a torn last line is not read), a line with the wrong number of columns or an
/// unreadable offset is skipped, and so is a row whose offset does not advance (the offsets come from a monotonic clock; the UTC text is never used to
/// order anything, so a clock set back cannot reorder a run). A row is turned into points as the summary rules say: a fresh value is a new
/// acquisition at its own acquire time, an empty cell of a fresh row (and a missing row) is a "could not be read" point, a repeat adds nothing.
/// </summary>
internal static class RunCsvReader
{
    private const int MaxFieldsGuard = 4096;

    public static RunCsvData Read(byte[] bytes, IReadOnlyList<RunMetricInfo>? declared)
    {
        var data = new RunCsvData();
        var text = DecodeCompleteLines(bytes);
        if (text.Length == 0)
        {
            return data;
        }

        var lines = text.Split('\n');
        var headerFields = SplitFields(lines[0].TrimEnd('\r'));
        if (headerFields.Count < 2 || headerFields[0] != RunLayout.OffsetColumn || headerFields[1] != RunLayout.UtcColumn || headerFields.Count > MaxFieldsGuard)
        {
            throw new FormatException("The first columns are not offsetUs,utc.");
        }

        data.Header.AddRange(headerFields);
        var plan = Plan(headerFields, declared, data);
        var lastOffset = long.MinValue;
        for (var n = 1; n < lines.Length; n++)
        {
            var line = lines[n].TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var fields = SplitFields(line);
            if (fields.Count != headerFields.Count
                || !long.TryParse(fields[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offsetUs)
                || offsetUs <= lastOffset)
            {
                data.SkippedLines++;
                continue;
            }

            lastOffset = offsetUs;
            data.Rows++;
            data.LastOffsetUs = offsetUs;
            foreach (var source in plan)
            {
                ApplySource(source, fields, offsetUs, data);
            }
        }

        foreach (var source in plan)
        {
            if (data.Sources.TryGetValue(source.Name, out var counts) && counts.Reasons.Count > 0)
            {
                var joined = string.Join(',', counts.Reasons);
                foreach (var column in source.Values)
                {
                    column.Track.Reasons = joined;
                }
            }
        }

        return data;
    }

    private sealed class SourcePlan(string name)
    {
        public string Name { get; } = name;

        public int Frame { get; set; } = -1;

        public int Acquired { get; set; } = -1;

        public int Period { get; set; } = -1;

        public int State { get; set; } = -1;

        public int Reason { get; set; } = -1;

        public List<ValueColumn> Values { get; } = [];
    }

    private sealed record ValueColumn(int Index, MetricTrack Track);

    private static List<SourcePlan> Plan(List<string> header, IReadOnlyList<RunMetricInfo>? declared, RunCsvData data)
    {
        var info = new Dictionary<string, RunMetricInfo>(StringComparer.Ordinal);
        if (declared is not null)
        {
            foreach (var metric in declared)
            {
                info[metric.Key] = metric;
            }
        }

        var plans = new Dictionary<string, SourcePlan>(StringComparer.Ordinal);
        var order = new List<SourcePlan>();
        for (var i = 2; i < header.Count; i++)
        {
            var name = header[i];
            var dot = name.IndexOf('.', StringComparison.Ordinal);
            if (dot <= 0)
            {
                continue;
            }

            var sourceName = name[..dot];
            if (!plans.TryGetValue(sourceName, out var plan))
            {
                plan = new SourcePlan(sourceName);
                plans[sourceName] = plan;
                order.Add(plan);
                data.Sources[sourceName] = new SourceCounts();
            }

            switch (name[(dot + 1)..])
            {
                case "frame":
                    plan.Frame = i;
                    break;
                case "acquiredOffsetUs":
                    plan.Acquired = i;
                    break;
                case "periodMs":
                    plan.Period = i;
                    break;
                case "state":
                    plan.State = i;
                    break;
                case "reason":
                    plan.Reason = i;
                    break;
                default:
                    AddValueColumn(name, i, sourceName, info, plan, data);
                    break;
            }
        }

        // A source without its state column cannot be read as rows at all.
        return order.Where(p => p.State >= 0).ToList();
    }

    private static void AddValueColumn(string key, int index, string source, Dictionary<string, RunMetricInfo> info, SourcePlan plan, RunCsvData data)
    {
        string unit;
        RunMetricKind kind;
        if (info.TryGetValue(key, out var declared))
        {
            unit = declared.Unit;
            kind = declared.Kind;
        }
        else if (RunMetrics.Describe(key) is { } def)
        {
            unit = def.Unit;
            kind = def.Kind;
        }
        else
        {
            unit = "";
            kind = RunMetricKind.Number;
        }

        if (kind == RunMetricKind.Text)
        {
            return;
        }

        var track = new MetricTrack(key, unit, source, kind);
        data.Tracks.Add(track);
        plan.Values.Add(new ValueColumn(index, track));
    }

    private static void ApplySource(SourcePlan source, List<string> fields, long rowOffsetUs, RunCsvData data)
    {
        var counts = data.Sources[source.Name];
        var state = fields[source.State];
        if (state == RunCodes.StateFresh)
        {
            counts.Fresh++;
            if (!long.TryParse(Cell(fields, source.Acquired), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var acquired))
            {
                return;
            }

            _ = int.TryParse(Cell(fields, source.Period), NumberStyles.None, CultureInfo.InvariantCulture, out var period);
            foreach (var column in source.Values)
            {
                AddCell(column.Track, fields[column.Index], acquired, period);
            }
        }
        else if (state == RunCodes.StateRepeat)
        {
            counts.Repeat++;
        }
        else
        {
            counts.Missing++;
            var reason = Cell(fields, source.Reason);
            if (reason.Length > 0 && !counts.Reasons.Contains(reason))
            {
                counts.Reasons.Add(reason);
            }

            foreach (var column in source.Values)
            {
                column.Track.AddMissing(rowOffsetUs);
            }
        }
    }

    private static string Cell(List<string> fields, int index) => index >= 0 && index < fields.Count ? fields[index] : "";

    private static void AddCell(MetricTrack track, string cell, long acquiredUs, int periodMs)
    {
        if (cell.Length == 0)
        {
            track.AddMissing(acquiredUs);
            return;
        }

        switch (track.Kind)
        {
            case RunMetricKind.Bitmask:
                if (ulong.TryParse(cell, NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
                {
                    track.AddValue(acquiredUs, periodMs, 0, bits);
                }
                else
                {
                    track.AddMissing(acquiredUs);
                }

                break;
            default:
                if (double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
                {
                    track.AddValue(acquiredUs, periodMs, value, 0);
                }
                else
                {
                    track.AddMissing(acquiredUs);
                }

                break;
        }
    }

    /// <summary>The text up to and including the last newline (a line the writer was in the middle of is not read), without a BOM.</summary>
    private static string DecodeCompleteLines(byte[] bytes)
    {
        var last = Array.LastIndexOf(bytes, (byte)'\n');
        if (last < 0)
        {
            return "";
        }

        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, last + 1 - start);
    }

    /// <summary>Splits one line at its commas, honouring double quotes (a doubled quote is a quote).</summary>
    internal static List<string> SplitFields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
