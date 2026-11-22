using System.Globalization;
using PcMieru.Checks;
using PcMieru.Core;

namespace PcMieru.Gui;

internal enum SummaryRowKind
{
    /// <summary>A group title ("by reading", "why clocks were held back").</summary>
    Heading,

    /// <summary>A fact about the whole run (its length, its rows, how it ended).</summary>
    Fact,

    /// <summary>One reading: min, average, max, sustained value, and the time it was and was not measured.</summary>
    Metric,

    /// <summary>A reading that was never measured: its cells say so; they are never 0.</summary>
    NotMeasured,

    /// <summary>A line under a group (a limit reason, a co-occurrence, a section's reading).</summary>
    Detail,

    /// <summary>A bench or load section's title.</summary>
    Section,

    /// <summary>Something that wants a look (a late GPU, a section with no end mark, a stop for size or space).</summary>
    Warning,

    /// <summary>An explanation (how to read the group above, a note of the summary).</summary>
    Note,
}

/// <summary>One line of the summary table. The text is already in words; a cell is drawn exactly as written here.</summary>
internal sealed record SummaryRow(
    SummaryRowKind Kind,
    string Item,
    string Min = "",
    string Avg = "",
    string Max = "",
    string Sustained = "",
    string Time = "",
    int Indent = 0,
    CellTone Tone = CellTone.Normal,
    bool Missing = false);

/// <summary>
/// Turns one run's summary into the lines of the summary table (DESIGN-B §7.2), with no control in it so a test can read every cell. What it
/// never does: print 0 for a value that was not measured (those cells say "not measured"), add the missing seconds of different readings into one
/// run total, call a temperature rise with a clock fall a "cause" (it says they "happened together"), or invent a CPU temperature (it says it is
/// not measured). The seconds of a reading are its own: measured seconds and not-measured seconds side by side.
/// </summary>
internal static class RunSummaryRows
{
    private static readonly CultureInfo Cur = CultureInfo.CurrentCulture;

    // The summary's own notes that the table says in its columns instead (the per-reading time cells, the not-measured rows, the facts).
    private static readonly HashSet<string> NotesInColumns = new(StringComparer.Ordinal) { "no-data", "gaps", "sparse", "dropped-ticks", "legacy-estimated" };

    public static IReadOnlyList<SummaryRow> Build(RunDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var info = detail.Info;
        var sum = detail.Summary;
        var language = Strings.Current;
        var rows = new List<SummaryRow>();

        // This recording.
        rows.Add(new SummaryRow(SummaryRowKind.Heading, Strings.Get("gui.rtab.sum.head.record")));
        rows.Add(Fact(Strings.Get("gui.rec.sum.duration"), sum.Freshness == SummaryFreshness.LegacyEstimated ? RecordFormat.ApproxLength((long)(sum.DurationSec * 1_000_000)) : RecordFormat.ExactLength(sum.DurationSec)));
        rows.Add(Fact(Strings.Get("gui.rec.sum.rows"), Strings.Format("gui.rtab.sum.count.rows", Fmt.Dict(("count", RecordFormat.Whole(sum.Rows))))));
        if (sum.DroppedTicks > 0)
        {
            rows.Add(Fact(Strings.Get("gui.rec.sum.dropped"), Strings.Format("gui.rtab.sum.count.dropped", Fmt.Dict(("count", RecordFormat.Whole(sum.DroppedTicks)))), CellTone.Warning));
        }

        rows.Add(Fact(Strings.Get("gui.rtab.sum.fact.end"), EndText(info)));
        if (sum.Freshness == SummaryFreshness.LegacyEstimated)
        {
            rows.Add(Fact(Strings.Get("gui.rtab.sum.fact.seconds"), Strings.Get("gui.rtab.legacy.tag"), CellTone.Warning));
        }

        // By reading.
        rows.Add(new SummaryRow(SummaryRowKind.Heading, Strings.Get("gui.rtab.sum.head.metrics")));
        foreach (var metric in sum.Metrics)
        {
            rows.Add(MetricRow(metric, language));
        }

        foreach (var note in sum.Notes.Where(static n => n.Code == "no-data"))
        {
            rows.Add(NeverMeasured(RecordStrings.MetricLabel(note.Subject, language), RecordStrings.NoteArgs(note, language)["detail"]));
        }

        if (!sum.Metrics.Any(static m => m.Key is "cpu.temp_c" or "core.cpu_temp_c"))
        {
            // There is no CPU temperature in a recording of this version, and the table says so instead of leaving it out.
            rows.Add(NeverMeasured(Strings.Get("gui.rtab.sum.cputemp"), Strings.Get("gui.chk.note.cpu.temperature_not_measured"), wholeNote: true));
        }

        // Why clocks were held back.
        if (sum.Limits.Count > 0)
        {
            rows.Add(new SummaryRow(SummaryRowKind.Heading, Strings.Get("gui.rtab.sum.head.limits")));
            foreach (var limit in sum.Limits)
            {
                rows.Add(new SummaryRow(
                    SummaryRowKind.Fact,
                    RecordStrings.DeviceLabel(limit.Device, language),
                    Time: Strings.Format("gui.rtab.sum.limit.device", Fmt.Dict(("seen", RecordFormat.Seconds(limit.ObservedSeconds))))));
                if (limit.Reasons.Count == 0)
                {
                    rows.Add(new SummaryRow(SummaryRowKind.Detail, Strings.Get("gui.rtab.sum.limit.none"), Indent: 1, Tone: CellTone.Dim));
                }

                foreach (var reason in limit.Reasons)
                {
                    rows.Add(new SummaryRow(
                        SummaryRowKind.Detail,
                        Strings.Get(RecordStrings.LimitKey(reason.Name)),
                        Time: Strings.Format("gui.rtab.sum.limit.reason", Fmt.Dict(("s", RecordFormat.Seconds(reason.Seconds)), ("l", RecordFormat.Seconds(reason.SecondsWhileLoaded)))),
                        Indent: 1));
                }
            }

            rows.Add(new SummaryRow(SummaryRowKind.Note, string.Empty, Time: Strings.Get("gui.rtab.sum.limit.note"), Tone: CellTone.Dim));
        }

        // Temperature and clock together.
        if (sum.CoOccurrences.Count > 0)
        {
            rows.Add(new SummaryRow(SummaryRowKind.Heading, Strings.Get("gui.rtab.sum.head.co")));
            foreach (var co in sum.CoOccurrences)
            {
                rows.Add(new SummaryRow(SummaryRowKind.Detail, RecordStrings.DeviceLabel(co.Device, language), Time: CoText(co)));
            }

            rows.Add(new SummaryRow(SummaryRowKind.Note, string.Empty, Time: Strings.Get("gui.rtab.sum.co.note"), Tone: CellTone.Dim));
        }

        // Bench and load sections.
        if (sum.Sections.Count > 0)
        {
            rows.Add(new SummaryRow(SummaryRowKind.Heading, Strings.Get("gui.rtab.sum.head.sections")));
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var section in sum.Sections)
            {
                counts[section.Kind] = counts.GetValueOrDefault(section.Kind) + 1;
                AddSection(rows, section, counts[section.Kind], language);
            }
        }

        // Warnings and notes.
        var warnings = Warnings(detail);
        var notes = sum.Notes.Where(static n => !NotesInColumns.Contains(n.Code)).ToList();
        if (warnings.Count > 0 || notes.Count > 0)
        {
            rows.Add(new SummaryRow(SummaryRowKind.Heading, Strings.Get("gui.rtab.sum.head.warnings")));
            rows.AddRange(warnings);
            foreach (var note in notes)
            {
                rows.Add(new SummaryRow(SummaryRowKind.Note, Strings.Get("gui.rtab.sum.note.label"), Time: RecordStrings.NoteText(note, language), Tone: CellTone.Dim));
            }
        }

        return rows;
    }

    private static SummaryRow Fact(string label, string value, CellTone tone = CellTone.Normal) =>
        new(SummaryRowKind.Fact, label, Time: value, Tone: tone);

    /// <summary>How the run ended, in plain words: the status, and why when it was not the user.</summary>
    private static string EndText(RunInfo info)
    {
        var status = RecordFormat.StatusWord(info.Status);
        var why = RecordFormat.NoteText(info);
        return why.Length == 0 || info.IsLegacy ? status : status + "・" + why;
    }

    private static SummaryRow MetricRow(RunMetricSummary metric, Language language)
    {
        var label = RecordStrings.MetricLabel(metric.Key, language);
        if (metric.ObservedSeconds <= 0)
        {
            return NeverMeasured(label, string.Empty);
        }

        return new SummaryRow(
            SummaryRowKind.Metric,
            label,
            RecordFormat.Value(metric.Min, metric.Unit),
            RecordFormat.Value(metric.Avg, metric.Unit),
            RecordFormat.Value(metric.Max, metric.Unit),
            metric.Sustained is { } sustained ? RecordFormat.Value(sustained, metric.Unit) : Fmt.Dash,
            Strings.Format("gui.rtab.sum.time", Fmt.Dict(("seen", RecordFormat.Seconds(metric.ObservedSeconds)), ("missing", RecordFormat.Seconds(metric.MissingSeconds)))),
            Indent: 0,
            Tone: CellTone.Normal);
    }

    /// <summary>A reading with no value at all. Every number cell says "not measured"; the time cell says why when it is known.</summary>
    private static SummaryRow NeverMeasured(string label, string why, bool wholeNote = false)
    {
        var word = Strings.Get("gui.rtab.sum.notmeasured");
        var time = why.Length == 0 ? string.Empty : wholeNote ? why : Strings.Format("gui.rtab.sum.time.none", Fmt.Dict(("why", why)));
        return new SummaryRow(SummaryRowKind.NotMeasured, label, word, word, word, word, time, Tone: CellTone.Dim, Missing: true);
    }

    private static string CoText(RunCoOccurrence co) =>
        Strings.Format(
            "gui.rtab.sum.co.line",
            Fmt.Dict(
                ("tfrom", co.TempFromC.ToString("0.#", Cur)),
                ("tto", co.TempToC.ToString("0.#", Cur)),
                ("cfrom", co.ClockFromMhz.ToString("N0", Cur)),
                ("cto", co.ClockToMhz.ToString("N0", Cur)),
                ("start", co.StartSec.ToString("N0", Cur)),
                ("end", co.EndSec.ToString("N0", Cur)),
                ("load", co.AvgLoadPct.ToString("0", Cur))));

    private static void AddSection(List<SummaryRow> rows, RunSection section, int number, Language language)
    {
        var titleKey = section.Kind == "stress" ? "gui.rtab.sum.section.stress" : "gui.rtab.sum.section.bench";
        var range = Strings.Format(
            "gui.rtab.sum.section.range",
            Fmt.Dict(("start", section.StartSec.ToString("N0", Cur)), ("end", section.EndSec.ToString("N0", Cur)), ("length", RecordFormat.Seconds(Math.Max(0, section.EndSec - section.StartSec)))));
        var time = section.Open ? Strings.Format("gui.rtab.sum.section.open", Fmt.Dict(("range", range))) : range;
        rows.Add(new SummaryRow(
            SummaryRowKind.Section,
            Strings.Format(titleKey, Fmt.Dict(("n", number.ToString(Cur)))),
            Time: time,
            Tone: section.Open ? CellTone.Warning : CellTone.Normal));
        if (section.Metrics.Count == 0)
        {
            rows.Add(new SummaryRow(SummaryRowKind.Detail, Strings.Get("gui.rtab.sum.section.nodata"), Indent: 1, Tone: CellTone.Dim));
            return;
        }

        foreach (var metric in section.Metrics)
        {
            rows.Add(new SummaryRow(
                SummaryRowKind.Detail,
                RecordStrings.MetricLabel(metric.Key, language),
                RecordFormat.Value(metric.Min, metric.Unit),
                RecordFormat.Value(metric.Avg, metric.Unit),
                RecordFormat.Value(metric.Max, metric.Unit),
                Time: Strings.Format("gui.rtab.sum.section.metric", Fmt.Dict(("median", RecordFormat.Value(metric.Median, metric.Unit)), ("seen", RecordFormat.Seconds(metric.ObservedSeconds)))),
                Indent: 1));
        }
    }

    /// <summary>The warnings of a run: a GPU that came late or came back, sections left open, and a stop for size or for space.</summary>
    private static List<SummaryRow> Warnings(RunDetail detail)
    {
        var rows = new List<SummaryRow>();
        foreach (var marker in detail.Info.Markers)
        {
            if (marker.Kind != MarkerKind.Warning || marker.Detail is not { } text)
            {
                continue;
            }

            var colon = text.IndexOf(':', StringComparison.Ordinal);
            var code = colon > 0 ? text[..colon] : text;
            var name = colon > 0 ? text[(colon + 1)..] : string.Empty;
            if (code is "late_gpu" or "gpu_rebound")
            {
                rows.Add(new SummaryRow(
                    SummaryRowKind.Warning,
                    Strings.Get("gui.rtab.warn." + code + ".label"),
                    Time: Strings.Format("gui.rtab.warn." + code, Fmt.Dict(("name", name))),
                    Tone: CellTone.Warning));
            }
        }

        var open = detail.Summary.Sections.Count(static s => s.Open);
        if (open > 0)
        {
            rows.Add(new SummaryRow(
                SummaryRowKind.Warning,
                Strings.Get("gui.rtab.warn.open_sections.label"),
                Time: Strings.Format("gui.rtab.warn.open_sections", Fmt.Dict(("count", open.ToString(Cur)))),
                Tone: CellTone.Warning));
        }

        if (detail.Info.StopReason == RunStopReason.SizeLimit)
        {
            rows.Add(new SummaryRow(SummaryRowKind.Warning, Strings.Get("gui.rtab.warn.size_limit.label"), Time: Strings.Get("gui.rtab.warn.size_limit"), Tone: CellTone.Warning));
        }

        if (detail.Info.StopReason == RunStopReason.LowSpace)
        {
            rows.Add(new SummaryRow(SummaryRowKind.Warning, Strings.Get("gui.rtab.warn.low_space.label"), Time: Strings.Get("gui.rtab.warn.low_space"), Tone: CellTone.Warning));
        }

        return rows;
    }
}

/// <summary>
/// The summary of one run as a table: item, min, average, max, sustained value, and "time and notes". The rows are built once per run and language
/// (<see cref="RunSummaryRows"/>), so painting asks a list and does nothing else. The item and number columns are as wide as what they hold; the last
/// column takes the rest, and a sentence it cuts has its whole text in the tooltip.
/// </summary>
internal sealed class RunSummaryGrid : GridView
{
    private const int ItemColumn = 0;
    private const int MinColumn = 1;
    private const int SustainedColumn = 4;
    private const int TimeColumn = 5;

    private IReadOnlyList<SummaryRow> _rows = [];
    private RunDetail? _detail;
    private int _itemNeed;
    private readonly int[] _numberNeed = new int[4];

    public RunSummaryGrid()
    {
        Dock = DockStyle.Fill;
        Cells = Cell;
        RowStyles = row => row >= 0 && row < _rows.Count && _rows[row].Kind == SummaryRowKind.Heading ? RowStyle.Header : RowStyle.Normal;
        CellTip = Tip;
        FillColumn = TimeColumn;
        ScrollsBackWhenWhole = true;
        SizeChanged += (_, _) => FitContentColumns();
    }

    public RunDetail? Detail => _detail;

    public int RowCount => _rows.Count;

    public IReadOnlyList<SummaryRow> Rows => _rows;

    /// <summary>The text of a cell, as it is drawn.</summary>
    public string TextAt(int row, int column) => row >= 0 && row < _rows.Count ? Cell(row, column).Text : string.Empty;

    public CellTone ToneAt(int row, int column) => row >= 0 && row < _rows.Count ? Cell(row, column).Tone : CellTone.Normal;

    /// <summary>
    /// The cells whose text the column cuts and that the person cannot read in full anyway ("row:column text"): an item label and a number must never
    /// be cut; the last column may be (its tooltip holds the whole text). The smoke run asks this of every table it takes a picture of.
    /// </summary>
    internal IReadOnlyList<string> UnreadableCuts()
    {
        var cuts = new List<string>();
        for (var row = 0; row < _rows.Count; row++)
        {
            for (var column = 0; column < Columns.Count; column++)
            {
                if (!IsCut(row, column))
                {
                    continue;
                }

                var text = Cell(row, column).Text;
                if (column != TimeColumn || Tip(row, column) != text)
                {
                    cuts.Add(row.ToString(CultureInfo.InvariantCulture) + ":" + column.ToString(CultureInfo.InvariantCulture) + " " + text);
                }
            }
        }

        return cuts;
    }

    public void PrepareColumns(Func<int, int> px, string accessibleName)
    {
        PageLayout.Prepare(
            this,
            accessibleName,
            (Strings.Get("gui.rtab.sum.col.item"), px(190), HorizontalAlignment.Left),
            (Strings.Get("gui.rtab.sum.col.min"), px(90), HorizontalAlignment.Right),
            (Strings.Get("gui.rtab.sum.col.avg"), px(90), HorizontalAlignment.Right),
            (Strings.Get("gui.rtab.sum.col.max"), px(90), HorizontalAlignment.Right),
            (Strings.Get("gui.rtab.sum.col.sustained"), px(90), HorizontalAlignment.Right),
            (Strings.Get("gui.rtab.sum.col.time"), px(330), HorizontalAlignment.Left));
        FitContentColumns();
    }

    /// <summary>Shows a run (or nothing).</summary>
    public void Show(RunDetail? detail)
    {
        _detail = detail;
        Rebuild();
    }

    public void ApplyLanguage()
    {
        if (Columns.Count == TimeColumn + 1)
        {
            Columns[ItemColumn].Text = Strings.Get("gui.rtab.sum.col.item");
            Columns[MinColumn].Text = Strings.Get("gui.rtab.sum.col.min");
            Columns[MinColumn + 1].Text = Strings.Get("gui.rtab.sum.col.avg");
            Columns[MinColumn + 2].Text = Strings.Get("gui.rtab.sum.col.max");
            Columns[SustainedColumn].Text = Strings.Get("gui.rtab.sum.col.sustained");
            Columns[TimeColumn].Text = Strings.Get("gui.rtab.sum.col.time");
        }

        Rebuild();
    }

    protected override void OnColumnsRescaled()
    {
        base.OnColumnsRescaled();
        MeasureNeeds();
        FitContentColumns();
    }

    private void Rebuild()
    {
        // The native list keeps its place when the rows change: a table scrolled down for a long run would show nothing at all for a short one.
        // It goes back to its first row while it still has the rows it was scrolled in.
        ScrollToFirstRow();
        _rows = _detail is null ? [] : RunSummaryRows.Build(_detail);
        SelectedIndices.Clear();
        VirtualListSize = _rows.Count;

        MeasureNeeds();
        FitContentColumns();
        Invalidate();
    }

    private void MeasureNeeds()
    {
        _itemNeed = 0;
        Array.Clear(_numberNeed);
        for (var row = 0; row < _rows.Count; row++)
        {
            _itemNeed = Math.Max(_itemNeed, NeededWidth(row, ItemColumn));
            for (var i = 0; i < _numberNeed.Length; i++)
            {
                _numberNeed[i] = Math.Max(_numberNeed[i], NeededWidth(row, MinColumn + i));
            }
        }
    }

    /// <summary>The item and number columns are as wide as their own content (and header); the last column takes what is left.</summary>
    private void FitContentColumns()
    {
        if (Columns.Count != TimeColumn + 1)
        {
            return;
        }

        var air = Scaled(8);
        var itemCeiling = Math.Max(ClientSize.Width * 40 / 100, Scaled(200));
        Columns[ItemColumn].Width = Math.Min(Math.Max(Math.Max(HeaderNeed(ItemColumn), _itemNeed), Scaled(140)), itemCeiling) + air;
        for (var i = 0; i < _numberNeed.Length; i++)
        {
            Columns[MinColumn + i].Width = Math.Max(Math.Max(HeaderNeed(MinColumn + i), _numberNeed[i]), Scaled(70)) + air;
        }

        FitLastColumn();
    }

    private GridCell Cell(int row, int column)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return new GridCell(string.Empty);
        }

        var line = _rows[row];
        switch (column)
        {
            case ItemColumn:
                return new GridCell(line.Item, line.Kind is SummaryRowKind.Detail or SummaryRowKind.Note ? CellTone.Dim : CellTone.Normal)
                {
                    Bold = line.Kind is SummaryRowKind.Heading or SummaryRowKind.Section,
                    Indent = line.Indent,
                };
            case TimeColumn:
                return new GridCell(line.Time, line.Tone == CellTone.Normal && line.Kind == SummaryRowKind.Metric ? CellTone.Dim : line.Tone);
            default:
                var text = column switch
                {
                    1 => line.Min,
                    2 => line.Avg,
                    3 => line.Max,
                    _ => line.Sustained,
                };
                return new GridCell(text, line.Missing || text == Fmt.Dash ? CellTone.Dim : CellTone.Normal);
        }
    }

    private string? Tip(int row, int column)
    {
        if (row < 0 || row >= _rows.Count || !IsCut(row, column))
        {
            return null;
        }

        return Cell(row, column).Text;
    }
}
