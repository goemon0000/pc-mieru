using System.Globalization;

namespace PcMieru.Checks;

/// <summary>
/// The rule-based summary of a run (DESIGN-B §7.2). A pure function of a <see cref="RunSeries"/>: it touches no file and no machine. It gives numbers,
/// lists what happened together and says what could not be read. It never names a cause and never says "broken".
///
/// The thresholds and the wording rules are the old <c>PartsLife.Checks.Recording.Summarizer</c>'s, line for line (see the comments); what changed is the
/// unit. The old code counted samples at a fixed 1 s; this one counts SECONDS, taken from the intervals (a value lasts from its acquisition to the next
/// point, at most 3P; a repeat adds no seconds), so a 2 s source, a 1 s source and a source that stalled all weigh by time.
/// </summary>
public static class RunSummarizer
{
    /// <summary>"Under load" when the previous 5 s observed mean is at least this (old Summarizer.cs line 33, BusyPct; the 5 s smoothing is lines 197-203).</summary>
    public const double LoadedPct = 50;

    /// <summary>A device that never passed this is flagged instead of judged (old line 34, IdleMaxPct).</summary>
    public const double IdleMaxPct = 20;

    /// <summary>No sustained values under this run length (old line 35, MinDurationForSustained).</summary>
    public const double MinDurationForSustainedSec = 60;

    /// <summary>The co-occurrence window (old line 36, CoWindowSec) and its three conditions (old lines 37-39).</summary>
    public const double CoWindowSec = 30;

    public const double CoTempRiseC = 3.0;

    public const double CoClockDropRatio = 0.05;

    public const double CoMinClockMhz = 300;

    /// <summary>At most this many co-occurrence windows are listed, the biggest temperature rises (old line 40).</summary>
    public const int MaxCoOccurrences = 6;

    /// <summary>The loaded time a sustained value needs in its window: the old 10 samples (line 41, MinBusySamples) at 1 sample per second.</summary>
    public const double MinLoadedSeconds = 10;

    /// <summary>The smoothing of the load, the temperature and the clock (old lines 197-203 and 285-287: a trailing mean of 5 samples at 1 s).</summary>
    public const double SmoothingSec = 5;

    /// <summary>A metric observed for less than this share of the run is reported as sparse (old line 102: <c>cnt &lt; n * 0.8</c>).</summary>
    public const double SparseShare = 0.8;

    /// <summary>A missing stretch longer than this many periods (old line 146: <c>maxGap &gt; interval * 3</c>) or more than this share of the run (<c>gapSec &gt; duration * 0.05</c>) is a gap.</summary>
    public const double GapPeriods = 3;

    public const double GapShare = 0.05;

    private const double Us = 1_000_000.0;

    internal static RunSummary Summarize(RunSeries s)
    {
        var notes = new List<RunNote>();
        var end = Math.Max(0, s.EndUs);
        var duration = end / Us;
        if (s.Legacy)
        {
            notes.Add(new RunNote("legacy-estimated", ""));
        }

        // Every number column becomes its intervals once; the text columns carry nothing to add up.
        var intervals = new Dictionary<string, IntervalSeries>(StringComparer.Ordinal);
        foreach (var track in s.Tracks)
        {
            if (track.Kind != RunMetricKind.Text)
            {
                intervals[track.Key] = IntervalSeries.Build(track, end, s.NormalEnd);
            }
        }

        if (!s.Tracks.Any(t => t.Points.Any(p => !p.Missing)))
        {
            // The old "no-samples" (line 50): nothing was ever read.
            notes.Add(new RunNote("no-samples", ""));
            AddRunNotes(s, notes);
            return new RunSummary
            {
                DurationSec = duration,
                Rows = s.Rows,
                DroppedTicks = s.DroppedTicks,
                Freshness = s.Legacy ? SummaryFreshness.LegacyEstimated : SummaryFreshness.Exact,
                Notes = notes,
            };
        }

        // --- the devices under load: cpu, gpu<N> (old lines 58-68) ---
        var devices = new List<string>();
        var loads = new Dictionary<string, IntervalSeries>(StringComparer.Ordinal);
        var loaded = new Dictionary<string, List<(long S, long E)>>(StringComparer.Ordinal);
        if (intervals.TryGetValue(RunMetrics.LoadKeyOf("cpu"), out var cpuLoad))
        {
            devices.Add("cpu");
            loads["cpu"] = cpuLoad;
        }

        foreach (var device in GpuDevices(s.Tracks))
        {
            if (intervals.TryGetValue(RunMetrics.LoadKeyOf(device), out var gpuLoad))
            {
                devices.Add(device);
                loads[device] = gpuLoad;
            }
        }

        foreach (var device in devices)
        {
            loaded[device] = LoadedSpans(loads[device]);
        }

        // --- the window and the loaded time the sustained values are taken from (old lines 70-85) ---
        double window = 0;
        var sustain = new Dictionary<string, List<(long S, long E)>>(StringComparer.Ordinal);
        if (duration >= MinDurationForSustainedSec)
        {
            window = Math.Clamp(duration / 3, 30, 300);
            var windowUs = (long)(window * Us);
            foreach (var device in devices)
            {
                var spans = loaded[device];
                if (spans.Count == 0)
                {
                    continue;
                }

                var last = spans[^1].E;
                var inWindow = SpanSet.Intersect(spans, [(last - windowUs, last)]);
                if (SpanSet.Total(inWindow) >= MinLoadedSeconds * Us)
                {
                    sustain[device] = inWindow;
                }
            }
        }
        else
        {
            notes.Add(new RunNote("too-short", "", duration.ToString("0", CultureInfo.InvariantCulture)));
        }

        // --- one summary per number metric (old lines 87-103) ---
        var metrics = new List<RunMetricSummary>();
        foreach (var track in s.Tracks.Where(t => t.Kind == RunMetricKind.Number).OrderBy(t => OrderOf(t.Key)).ThenBy(t => t.Key, StringComparer.Ordinal))
        {
            var series = intervals[track.Key];
            var observedUs = series.ObservedUs;
            if (observedUs == 0)
            {
                // Nothing of it could be read: the note keeps the reason codes the source's missing rows carried.
                notes.Add(new RunNote("no-data", track.Key, track.Reasons ?? ""));
                continue;
            }

            double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
            foreach (var seg in series.Items)
            {
                min = Math.Min(min, seg.V);
                max = Math.Max(max, seg.V);
                sum += seg.V * seg.Length;
            }

            double? sustained = null;
            var owner = RunMetrics.DeviceOf(track.Key);
            if (owner is not null && sustain.TryGetValue(owner, out var inWindow))
            {
                var picked = SpanSet.Clip(series.Items, inWindow).Select(p => (p.V, p.Length)).ToList();
                if (picked.Count > 0)
                {
                    sustained = SpanSet.WeightedMedian(picked);
                }
            }

            var newPoints = track.Points.LongCount(p => !p.Missing);
            metrics.Add(new RunMetricSummary(
                track.Key, track.Unit, track.Source, newPoints, observedUs / Us, Math.Max(0, end - observedUs) / Us, min, sum / observedUs, max, sustained));
            if (observedUs < end * SparseShare)
            {
                notes.Add(new RunNote("sparse", track.Key, (observedUs * 100.0 / end).ToString("0", CultureInfo.InvariantCulture)));
            }
        }

        // --- a device that was loaded but never long enough, and one that never got loaded (old lines 104-112) ---
        foreach (var device in devices)
        {
            var peak = PeakOf(s.Find(RunMetrics.LoadKeyOf(device)));
            if (window > 0 && !sustain.ContainsKey(device) && peak >= LoadedPct)
            {
                notes.Add(new RunNote("no-sustained-busy", device));
            }
        }

        foreach (var device in devices)
        {
            var peak = PeakOf(s.Find(RunMetrics.LoadKeyOf(device)));
            if (peak < IdleMaxPct)
            {
                notes.Add(new RunNote("device-idle", device, peak.ToString("0", CultureInfo.InvariantCulture)));
            }
        }

        // --- the limit marks (old lines 114-119, 226-277) ---
        var limits = new List<RunLimitSummary>();
        foreach (var device in LimitDevices(s.Tracks))
        {
            var key = device + "." + RunMetrics.GpuLimitReasons.Name;
            if (!intervals.TryGetValue(key, out var mask) || mask.Items.Length == 0)
            {
                continue;
            }

            limits.Add(GpuLimits(device, mask, loads.GetValueOrDefault(device), loaded.GetValueOrDefault(device)));
        }

        if (CpuLimits(intervals, loads.GetValueOrDefault("cpu"), loaded.GetValueOrDefault("cpu")) is { } cpuLimit)
        {
            limits.Add(cpuLimit);
        }

        // --- temperature up while the clock goes down (old lines 121-129, 283-323) ---
        var together = new List<RunCoOccurrence>();
        foreach (var device in devices.Where(d => d != "cpu"))
        {
            if (intervals.TryGetValue(RunMetrics.GpuKey(DeviceNumber(device), RunMetrics.GpuTemp), out var temp)
                && intervals.TryGetValue(RunMetrics.GpuKey(DeviceNumber(device), RunMetrics.GpuClock), out var clock))
            {
                together.AddRange(FindCoOccurrences(device, temp, clock, loads.GetValueOrDefault(device)));
            }
        }

        // --- sections cut out by the marks (old lines 131-132, 325-375) ---
        var sections = Sections(s, intervals, end, notes);

        // --- notes (old lines 142-156) ---
        if (intervals.TryGetValue(RunMetrics.CoreKey(RunMetrics.AcConnected), out var ac)
            && ac.Items.Length > 0
            && ac.Items.Min(seg => seg.V) < 0.5)
        {
            notes.Add(new RunNote("battery-used", ""));
        }

        AddGapNote(s, metrics, intervals, end, duration, notes);
        AddRunNotes(s, notes);
        var clockSummary = metrics.FirstOrDefault(m => m.Key == RunMetrics.CoreKey(RunMetrics.CpuMhz));
        if (clockSummary?.Sustained is { } sustainedClock
            && sustainedClock < clockSummary.Max * 0.95
            && !s.Tracks.Any(t => t.Key is "cpu.temp_c" or "core.cpu_temp_c"))
        {
            // Old lines 152-156: the clock fell, but there is no CPU temperature, so the cause cannot be narrowed down.
            notes.Add(new RunNote("cpu-clock-drop-no-temp", "cpu",
                sustainedClock.ToString("0", CultureInfo.InvariantCulture) + "/" + clockSummary.Max.ToString("0", CultureInfo.InvariantCulture)));
        }

        return new RunSummary
        {
            DurationSec = duration,
            Rows = s.Rows,
            DroppedTicks = s.DroppedTicks,
            Freshness = s.Legacy ? SummaryFreshness.LegacyEstimated : SummaryFreshness.Exact,
            SustainedWindowSec = sustain.Count > 0 ? window : 0,
            Metrics = metrics,
            Limits = limits,
            CoOccurrences = together,
            Sections = sections,
            Notes = notes,
        };
    }

    // ----------------------------------------------------------------- parts

    /// <summary>The notes that do not need a value: dropped ticks and a run that did not complete (old lines 150-151).</summary>
    private static void AddRunNotes(RunSeries s, List<RunNote> notes)
    {
        if (s.DroppedTicks > 0)
        {
            notes.Add(new RunNote("dropped-ticks", "", s.DroppedTicks.ToString(CultureInfo.InvariantCulture)));
        }

        if (s.Status != RunStatus.Completed)
        {
            notes.Add(new RunNote("not-completed", RunCodes.Of(s.Status), s.StopReason is { } reason ? RunCodes.Of(reason) : ""));
        }
    }

    /// <summary>
    /// The gaps note (old line 146). It looks at ONE reference column (the CPU usage, else the first number column) so a run total is never a sum of
    /// the metrics' own missing seconds: the longest missing stretch above 3P, or more than 5% of the run missing.
    /// </summary>
    private static void AddGapNote(
        RunSeries s, List<RunMetricSummary> metrics, Dictionary<string, IntervalSeries> intervals, long end, double duration, List<RunNote> notes)
    {
        var referenceKey = intervals.ContainsKey(RunMetrics.CoreKey(RunMetrics.CpuUsage))
            ? RunMetrics.CoreKey(RunMetrics.CpuUsage)
            : metrics.FirstOrDefault()?.Key;
        if (referenceKey is null || !intervals.TryGetValue(referenceKey, out var reference) || end <= 0)
        {
            return;
        }

        var track = s.Find(referenceKey)!;
        var periodUs = (track.Points.FirstOrDefault(p => !p.Missing).PeriodMs is > 0 and var ms ? ms : IntervalSeries.DefaultPeriodMs) * 1000L;
        long missing = 0, longest = 0, cursor = 0;
        foreach (var (a, b) in reference.Spans())
        {
            longest = Math.Max(longest, a - cursor);
            missing += a - cursor;
            cursor = b;
        }

        longest = Math.Max(longest, end - cursor);
        missing += end - cursor;
        if (longest > GapPeriods * periodUs || missing > duration * Us * GapShare)
        {
            notes.Add(new RunNote("gaps", "",
                (missing / Us).ToString("0.#", CultureInfo.InvariantCulture) + "/" + (longest / Us).ToString("0.#", CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>The loaded stretches: each segment whose previous 5 s observed mean is at least 50% (old lines 197-203), joined.</summary>
    private static List<(long S, long E)> LoadedSpans(IntervalSeries load)
    {
        var spans = new List<(long S, long E)>();
        foreach (var seg in load.Items)
        {
            var mean = load.MeanOver(Math.Max(0, seg.E - (long)(SmoothingSec * Us)), seg.E);
            if (!(mean >= LoadedPct))
            {
                continue;
            }

            if (spans.Count > 0 && spans[^1].E >= seg.S)
            {
                spans[^1] = (spans[^1].S, Math.Max(spans[^1].E, seg.E));
            }
            else
            {
                spans.Add((seg.S, seg.E));
            }
        }

        return spans;
    }

    /// <summary>The highest value ever read, over the points (not the intervals), as the old peak was a maximum over the samples (lines 106 and 111).</summary>
    private static double PeakOf(MetricTrack? track)
    {
        var peak = double.NegativeInfinity;
        if (track is not null)
        {
            foreach (var point in track.Points)
            {
                if (!point.Missing)
                {
                    peak = Math.Max(peak, point.Value);
                }
            }
        }

        return double.IsNegativeInfinity(peak) ? double.NaN : peak;
    }

    private static IEnumerable<string> GpuDevices(List<MetricTrack> tracks)
    {
        var numbers = new SortedSet<int>();
        foreach (var track in tracks)
        {
            var device = RunMetrics.DeviceOf(track.Key);
            if (device is not null && device != "cpu")
            {
                numbers.Add(DeviceNumber(device));
            }
        }

        return numbers.Select(RunMetrics.GpuSource);
    }

    private static IEnumerable<string> LimitDevices(List<MetricTrack> tracks) =>
        GpuDevices(tracks).Where(d => tracks.Any(t => t.Key == d + "." + RunMetrics.GpuLimitReasons.Name));

    private static int DeviceNumber(string device) =>
        int.Parse(device.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture);

    private static int OrderOf(string key)
    {
        var device = RunMetrics.DeviceOf(key);
        if (device == "cpu")
        {
            return 0;
        }

        if (device is not null)
        {
            return 1;
        }

        var dot = key.IndexOf('.', StringComparison.Ordinal);
        var source = dot > 0 ? key[..dot] : key;
        var tail = dot > 0 ? key[(dot + 1)..] : "";
        if (source == "mem" || (source == RunMetrics.Core && tail.StartsWith("mem_", StringComparison.Ordinal)))
        {
            return 2;
        }

        if (source == "disk")
        {
            return 3;
        }

        if (source == "power" || (source == RunMetrics.Core && (tail.StartsWith("ac_", StringComparison.Ordinal) || tail.StartsWith("battery_", StringComparison.Ordinal))))
        {
            return 4;
        }

        return 5;
    }

    /// <summary>
    /// The seconds per limit bit (old lines 226-248). The denominator is the limit column's own observed seconds; the loaded breakdown's denominator is
    /// the common part where the load was observed too, and "while loaded" is counted only there. A bit no name covers is one "unknown-bits" reason.
    /// </summary>
    private static RunLimitSummary GpuLimits(string device, IntervalSeries mask, IntervalSeries? load, List<(long S, long E)>? loaded)
    {
        var observed = mask.Spans();
        var loadObserved = load is null ? [] : SpanSet.Intersect(observed, load.Spans());
        var loadedPart = loaded is null ? [] : SpanSet.Intersect(observed, loaded);
        var reasons = new List<RunLimitReason>();
        foreach (var (bit, name) in RunLimitBits.All)
        {
            AddReason(reasons, name, mask, seg => (seg.B & bit) != 0, loaded);
        }

        AddReason(reasons, RunLimitBits.UnknownName, mask, seg => (seg.B & RunLimitBits.Unnamed) != 0, loaded);
        return new RunLimitSummary(device, SpanSet.Total(observed) / Us, SpanSet.Total(loadObserved) / Us, SpanSet.Total(loadedPart) / Us, reasons);
    }

    private static void AddReason(List<RunLimitReason> reasons, string name, IntervalSeries mask, Func<Seg, bool> has, List<(long S, long E)>? loaded)
    {
        var spans = new List<(long S, long E)>();
        foreach (var seg in mask.Items)
        {
            if (has(seg))
            {
                spans.Add((seg.S, seg.E));
            }
        }

        var seconds = SpanSet.Total(spans);
        if (seconds > 0)
        {
            var whileLoaded = loaded is null ? 0 : SpanSet.Total(SpanSet.Intersect(spans, loaded));
            reasons.Add(new RunLimitReason(name, seconds / Us, whileLoaded / Us));
        }
    }

    /// <summary>
    /// The CPU's limits come from the OS indicators of an old run only (old lines 250-277): a performance limit under 99.5% and the flags above 0. A new
    /// run has neither column, so nothing is invented for it.
    /// </summary>
    private static RunLimitSummary? CpuLimits(Dictionary<string, IntervalSeries> intervals, IntervalSeries? load, List<(long S, long E)>? loaded)
    {
        intervals.TryGetValue("cpu.perf_limit_pct", out var pct);
        intervals.TryGetValue("cpu.perf_limit_flags", out var flags);
        if ((pct is null || pct.Items.Length == 0) && (flags is null || flags.Items.Length == 0))
        {
            return null;
        }

        var observed = SpanSet.Union(pct?.Spans() ?? [], flags?.Spans() ?? []);
        var loadObserved = load is null ? [] : SpanSet.Intersect(observed, load.Spans());
        var loadedPart = loaded is null ? [] : SpanSet.Intersect(observed, loaded);
        var reasons = new List<RunLimitReason>();
        if (pct is not null)
        {
            AddReason(reasons, "os-performance-limit-below-100", pct, seg => seg.V < 99.5, loaded);
        }

        if (flags is not null)
        {
            AddReason(reasons, "os-performance-limit-flags-set", flags, seg => seg.B > 0, loaded);
        }

        return new RunLimitSummary("cpu", SpanSet.Total(observed) / Us, SpanSet.Total(loadObserved) / Us, SpanSet.Total(loadedPart) / Us, reasons);
    }

    /// <summary>
    /// Windows of <see cref="CoWindowSec"/> s where the temperature rose by at least 3 C, the clock fell by at least 5% (from at least 300 MHz) and the load
    /// was at least 50% (old lines 283-323). The grid is the temperature's own points inside stretches where the temperature and the clock were both
    /// observed; the smoothing never reaches across such a stretch's start, and a window never spans two stretches (a gap). Overlapping windows are merged,
    /// the six biggest temperature rises are kept, in time order. They only happened in the same time window: no cause is claimed.
    /// </summary>
    internal static List<RunCoOccurrence> FindCoOccurrences(string device, IntervalSeries temp, IntervalSeries clock, IntervalSeries? load)
    {
        var stretches = SpanSet.Intersect(temp.Spans(), clock.Spans());
        var grid = new List<(long T, int Stretch, double St, double Sc)>();
        var k = 0;
        foreach (var seg in temp.Items)
        {
            while (k < stretches.Count && stretches[k].E <= seg.S)
            {
                k++;
            }

            if (k >= stretches.Count)
            {
                break;
            }

            if (seg.S < stretches[k].S)
            {
                continue;
            }

            var to = Math.Min(seg.E, stretches[k].E);
            var from = Math.Max(stretches[k].S, to - (long)(SmoothingSec * Us));
            var st = temp.MeanOver(from, to);
            var sc = clock.MeanOver(from, to);
            if (!double.IsNaN(st) && !double.IsNaN(sc))
            {
                grid.Add((seg.S, k, st, sc));
            }
        }

        var windowUs = (long)(CoWindowSec * Us);
        var flagged = new List<(int A, int B)>();
        var j = 0;
        for (var i = 0; i < grid.Count; i++)
        {
            if (j < i)
            {
                j = i;
            }

            while (j < grid.Count && grid[j].Stretch == grid[i].Stretch && grid[j].T < grid[i].T + windowUs)
            {
                j++;
            }

            if (j >= grid.Count || grid[j].Stretch != grid[i].Stretch)
            {
                continue;
            }

            if (grid[i].Sc < CoMinClockMhz)
            {
                continue;
            }

            var rise = grid[j].St - grid[i].St;
            var drop = (grid[i].Sc - grid[j].Sc) / grid[i].Sc;
            if (rise < CoTempRiseC || drop < CoClockDropRatio)
            {
                continue;
            }

            if (load is not null && !(load.MeanOver(grid[i].T, grid[j].T) >= LoadedPct))
            {
                continue;
            }

            flagged.Add((i, j));
        }

        var merged = new List<(int A, int B)>();
        foreach (var (a, b) in flagged)
        {
            if (merged.Count > 0 && a <= merged[^1].B)
            {
                merged[^1] = (merged[^1].A, Math.Max(merged[^1].B, b));
            }
            else
            {
                merged.Add((a, b));
            }
        }

        return merged
            .Select(m => new RunCoOccurrence(
                device,
                grid[m.A].T / Us,
                grid[m.B].T / Us,
                grid[m.A].St,
                grid[m.B].St,
                grid[m.A].Sc,
                grid[m.B].Sc,
                load is null ? double.NaN : load.MeanOver(grid[m.A].T, grid[m.B].T)))
            .OrderByDescending(c => c.TempToC - c.TempFromC)
            .Take(MaxCoOccurrences)
            .OrderBy(c => c.StartSec)
            .ToList();
    }

    /// <summary>
    /// Bench and stress sections (old lines 325-351). A start after a start closes the previous section as open; an end without a start is a note; a
    /// start without an end closes at the end of the run, open. The numbers of a section are cut at the marker times.
    /// </summary>
    private static List<RunSection> Sections(RunSeries s, Dictionary<string, IntervalSeries> intervals, long end, List<RunNote> notes)
    {
        var sections = new List<(long From, RunSection Section)>();
        foreach (var (kind, startKind, endKind) in new[]
                 {
                     ("bench", MarkerKind.BenchStart, MarkerKind.BenchEnd),
                     ("stress", MarkerKind.StressStart, MarkerKind.StressEnd),
                 })
        {
            RunMarker? open = null;
            foreach (var marker in s.Markers.Where(m => m.Kind == startKind || m.Kind == endKind).OrderBy(m => m.OffsetUs))
            {
                if (marker.Kind == startKind)
                {
                    if (open is not null)
                    {
                        sections.Add((open.OffsetUs, MakeSection(s, intervals, kind, open.OffsetUs, marker.OffsetUs, true)));
                    }

                    open = marker;
                }
                else if (open is not null)
                {
                    sections.Add((open.OffsetUs, MakeSection(s, intervals, kind, open.OffsetUs, marker.OffsetUs, false)));
                    open = null;
                }
                else
                {
                    notes.Add(new RunNote("marker-unpaired", RunCodes.Of(marker.Kind)));
                }
            }

            if (open is not null)
            {
                sections.Add((open.OffsetUs, MakeSection(s, intervals, kind, open.OffsetUs, Math.Max(end, open.OffsetUs), true)));
            }
        }

        return sections.OrderBy(x => x.From).Select(x => x.Section).ToList();
    }

    private static RunSection MakeSection(RunSeries s, Dictionary<string, IntervalSeries> intervals, string kind, long from, long to, bool open)
    {
        var list = new List<RunSectionMetric>();
        foreach (var track in s.Tracks.Where(t => t.Kind == RunMetricKind.Number && IsSectionMetric(t.Key)).OrderBy(t => OrderOf(t.Key)).ThenBy(t => t.Key, StringComparer.Ordinal))
        {
            var items = new List<(double V, long W)>();
            foreach (var piece in intervals[track.Key].Within(from, to))
            {
                items.Add((piece.V, piece.Length));
            }

            if (items.Count == 0)
            {
                continue;
            }

            long weight = items.Sum(i => i.W);
            list.Add(new RunSectionMetric(
                track.Key,
                track.Unit,
                weight / Us,
                items.Min(i => i.V),
                items.Sum(i => i.V * i.W) / weight,
                items.Max(i => i.V),
                SpanSet.WeightedMedian(items)));
        }

        return new RunSection(kind, from / Us, to / Us, open, list);
    }

    /// <summary>What a section reports (old lines 369-375): the CPU's usage, clock and package power; a GPU's utilization, clock, temperature and power.</summary>
    private static bool IsSectionMetric(string key)
    {
        var device = RunMetrics.DeviceOf(key);
        var name = RunMetrics.Describe(key)?.Name;
        return device switch
        {
            "cpu" => name is "cpu_usage_pct" or "cpu_mhz" or "cpu_package_w",
            not null => name is "util_pct" or "clock_mhz" or "temp_c" or "power_w",
            _ => false,
        };
    }
}
