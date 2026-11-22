using System.Globalization;
using PcMieru.Checks;
using PcMieru.Core;

namespace PcMieru.Gui;

internal enum SummaryRowKind
{
    /// <summary>A group title ("by reading", "why clocks were held back").</summary>
    Heading,

    /// <summary>The line about the whole recording (its length, its rows, how it ended): one sentence across the table, so the readings start higher up.</summary>
    Record,

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

    /// <summary>True when the table has the line about the sustained value (some reading has a measured second); the "How this is calculated" switch only means something then.</summary>
    public static bool HasSustainedNote(RunDetail detail) => detail.Summary.Metrics.Any(static m => m.ObservedSeconds > 0);

    /// <param name="detail">The run.</param>
    /// <param name="showCalculation">The exact conditions of the sustained value, as a line of their own under the short sentence. Off until the person asks for them.</param>
    public static IReadOnlyList<SummaryRow> Build(RunDetail detail, bool showCalculation = false)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var info = detail.Info;
        var sum = detail.Summary;
        var language = Strings.Current;
        var rows = new List<SummaryRow>();

        // A recording that stopped before its end (it was cut off, or it could not be saved) says first what is left of it, and for one that failed what to
        // check: that is what a person looking at it came for, and it stays in the first lines at 200 %. A recording that ended well has that line after the readings.
        var cutShort = info.Status is RunStatus.Interrupted or RunStatus.Failed;
        var record = RecordRow(info, sum, cutShort);
        if (cutShort)
        {
            rows.Add(record);
            if (info.Status == RunStatus.Failed)
            {
                rows.Add(new SummaryRow(SummaryRowKind.Note, string.Empty, Time: Strings.Get("gui.rtab.sum.advice.failed"), Tone: CellTone.Warning));
            }
        }

        // By reading. After a cut-short recording's own first lines the readings follow with no heading of their own: at 200 % the table has five lines, and
        // the first lines and the readings both have to be whole in them (the heading was a line of that room).
        if (!cutShort)
        {
            rows.Add(new SummaryRow(SummaryRowKind.Heading, Strings.Get("gui.rtab.sum.head.metrics")));
        }

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
            // There is no CPU temperature in a recording of this version, and the table says so instead of leaving it out, in the words of the other rows that
            // were not measured and on one line (the sentence of the Checks tabs wrapped to a second line at 200 %).
            rows.Add(NeverMeasured(Strings.Get("gui.rtab.sum.cputemp"), Strings.Get("gui.rtab.sum.cputemp.why")));
        }

        // What the sustained value is: one short sentence, a row of the table right under the readings (it was a label under the table, which took a third of
        // the room at 200 %). The exact conditions are a row of their own that the "How this is calculated" switch adds: they were in the sentence, and the row was too dense to read.
        if (HasSustainedNote(detail))
        {
            rows.Add(new SummaryRow(SummaryRowKind.Note, string.Empty, Time: Strings.Get("gui.rtab.sum.sustained.note"), Tone: CellTone.Dim));
            if (showCalculation)
            {
                rows.Add(new SummaryRow(SummaryRowKind.Note, string.Empty, Time: Strings.Get("gui.rtab.sum.sustained.rule"), Tone: CellTone.Dim));
            }
        }

        // This recording, after the readings (a person comes for the readings; what it is, is on the head above the table): the line about the whole of it
        // (a cut-short one has it at the top instead), and the facts that go with it. The heading is there only when something is under it.
        var facts = new List<SummaryRow>();
        if (!cutShort)
        {
            facts.Add(record);
        }

        if (sum.DroppedTicks > 0)
        {
            facts.Add(Fact(Strings.Get("gui.rec.sum.dropped"), Strings.Format("gui.rtab.sum.count.dropped", Fmt.Dict(("count", RecordFormat.Whole(sum.DroppedTicks)))), CellTone.Warning));
        }

        if (sum.Freshness == SummaryFreshness.LegacyEstimated)
        {
            facts.Add(Fact(Strings.Get("gui.rtab.sum.fact.seconds"), Strings.Get("gui.rtab.legacy.tag"), CellTone.Warning));
        }

        if (facts.Count > 0)
        {
            rows.Add(new SummaryRow(SummaryRowKind.Heading, Strings.Get("gui.rtab.sum.head.record")));
            rows.AddRange(facts);
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

    private static string Labelled(string label, string value) =>
        Strings.Format("gui.rtab.sum.labelled", Fmt.Dict(("label", label), ("value", value)));

    /// <summary>
    /// The line about the whole recording: how long it is (the data that is left, for one that was cut short), how many rows, how it ended. The rows have no
    /// label of their own ("742 rows" says it), and the parts are set apart by a spaced dot.
    /// </summary>
    private static SummaryRow RecordRow(RunInfo info, RunSummary sum, bool cutShort)
    {
        var lengthLabel = Strings.Get(cutShort ? "gui.rtab.sum.fact.saved" : "gui.rec.sum.duration");
        var length = sum.Freshness == SummaryFreshness.LegacyEstimated ? RecordFormat.ApproxLength((long)(sum.DurationSec * 1_000_000)) : RecordFormat.ExactLength(sum.DurationSec);
        var text = string.Join(
            Strings.Get("gui.rtab.sum.sep"),
            Labelled(lengthLabel, length),
            Strings.Format("gui.rtab.sum.count.rows", Fmt.Dict(("count", RecordFormat.Whole(sum.Rows)))),
            Labelled(Strings.Get("gui.rtab.sum.fact.end"), EndText(info)));
        return new SummaryRow(SummaryRowKind.Record, text);
    }

    private static SummaryRow Fact(string label, string value, CellTone tone = CellTone.Normal) =>
        new(SummaryRowKind.Fact, label, Time: value, Tone: tone);

    /// <summary>How the run ended, in plain words: the status, and why when it was not the user.</summary>
    private static string EndText(RunInfo info)
    {
        var status = RecordFormat.StatusWord(info.Status);
        var why = RecordFormat.NoteText(info);
        return why.Length == 0 || info.IsLegacy ? status : status + Strings.Get("gui.rtab.sum.sep") + why;
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

    /// <summary>
    /// A reading with no value at all. The four number cells hold the dash (never 0, and not the words four times over, which made every number column
    /// as wide as "not measured" and left the last column too narrow at 200 %); the last column says "Not measured" in words, and why when it is known.
    /// </summary>
    private static SummaryRow NeverMeasured(string label, string why)
    {
        var time = why.Length == 0
            ? Strings.Get("gui.rtab.sum.time.nothing")
            : Strings.Format("gui.rtab.sum.time.none", Fmt.Dict(("why", why)));
        return new SummaryRow(SummaryRowKind.NotMeasured, label, Fmt.Dash, Fmt.Dash, Fmt.Dash, Fmt.Dash, time, Tone: CellTone.Dim, Missing: true);
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
/// column takes the rest. A sentence that is longer than its column wraps onto the lines under it (the table is made of those lines, one list row
/// each: a native list has one row height), and a sentence of the summary's own (a note, the line about the recording) runs across the whole width;
/// nothing ends in an ellipsis. <see cref="RowCount"/> and <see cref="TextAt"/> speak of the summary's rows; <see cref="LineCount"/> of the lines drawn.
/// </summary>
internal sealed class RunSummaryGrid : GridView
{
    private const int ItemColumn = 0;
    private const int MinColumn = 1;
    private const int SustainedColumn = 4;
    private const int TimeColumn = 5;

    private readonly int[] _numberNeed = new int[4];
    private IReadOnlyList<SummaryRow> _rows = [];
    private List<Line> _lines = [];
    private RunDetail? _detail;
    private bool _showCalculation;
    private int _readingItemNeed;
    private int _readingTimeNeed;
    private int _itemWordNeed;
    private (int Item, int Time, int Span) _wrapKey = (-1, -1, -1);

    public RunSummaryGrid()
    {
        Dock = DockStyle.Fill;
        RowPadding = 6;
        Cells = LineCell;
        RowStyles = line => line >= 0 && line < _lines.Count && _lines[line].Row is var row and >= 0 && row < _rows.Count && _rows[row].Kind == SummaryRowKind.Heading ? RowStyle.Header : RowStyle.Normal;
        CellTip = Tip;
        HeaderTip = column => column == SustainedColumn ? Strings.Get("gui.rtab.sum.sustained.tip") : null;
        FillColumn = TimeColumn;
        ScrollsBackWhenWhole = true;
        SizeChanged += (_, _) => FitContentColumns();
    }

    /// <summary>One line of the table as drawn: part <see cref="Part"/> (0 is the first) of one row of the summary, with the text that is on it.</summary>
    private readonly record struct Line(int Row, int Part, string Item, string Time, bool Span);

    public RunDetail? Detail => _detail;

    /// <summary>Whether the table has the sustained value's note (so the "How this is calculated" switch has something to open).</summary>
    public bool HasSustainedNote => _detail is not null && RunSummaryRows.HasSustainedNote(_detail);

    /// <summary>Whether the exact conditions of the sustained value are in the table. Off for a new window; the tab's switch sets it.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowCalculation
    {
        get => _showCalculation;
        set
        {
            if (_showCalculation == value)
            {
                return;
            }

            _showCalculation = value;
            Rebuild();
        }
    }

    /// <summary>The rows of the summary (a long sentence is one row; see <see cref="LineCount"/> for the lines it is drawn on).</summary>
    public int RowCount => _rows.Count;

    public IReadOnlyList<SummaryRow> Rows => _rows;

    /// <summary>The lines the table has: a row that wraps takes several.</summary>
    public int LineCount => _lines.Count;

    /// <summary>The text of a cell of a row of the summary, whole as it was written (a wrapped sentence is not cut here).</summary>
    public string TextAt(int row, int column) => row >= 0 && row < _rows.Count ? Cell(row, column).Text : string.Empty;

    public CellTone ToneAt(int row, int column) => row >= 0 && row < _rows.Count ? Cell(row, column).Tone : CellTone.Normal;

    /// <summary>The text of a cell of a line of the table, as it is drawn (a test and the smoke run read what a person would see).</summary>
    public string LineText(int line, int column) => line >= 0 && line < _lines.Count ? LineCell(line, column).Text : string.Empty;

    /// <summary>The row of the summary a line of the table belongs to, or -1.</summary>
    public int RowOfLine(int line) => line >= 0 && line < _lines.Count ? _lines[line].Row : -1;

    /// <summary>The first line of a row of the summary, or -1.</summary>
    public int FirstLineOf(int row) => _lines.FindIndex(line => line.Row == row);

    /// <summary>
    /// How many readings (a row with numbers, or one that was not measured) the grid shows whole from its top: every line of the row is among the lines that
    /// fit. At 200 % on a 1920 x 1080 screen the table has to show several at once; a test and the smoke run ask this.
    /// </summary>
    internal int ReadingsWholeAtTop()
    {
        var fit = RowsThatFit(Height);
        var count = 0;
        for (var row = 0; row < _rows.Count; row++)
        {
            if (_rows[row].Kind is not (SummaryRowKind.Metric or SummaryRowKind.NotMeasured))
            {
                continue;
            }

            var last = LastLineOf(row);
            if (last >= 0 && last < fit)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The last line of a row of the summary, or -1.</summary>
    private int LastLineOf(int row)
    {
        var last = FirstLineOf(row);
        if (last < 0)
        {
            return -1;
        }

        while (last + 1 < _lines.Count && _lines[last + 1].Row == row)
        {
            last++;
        }

        return last;
    }

    /// <summary>
    /// How many rows come before the first heading or reading of the summary: what a cut-short recording says first (what is left of it, and what to check).
    /// None for a recording that ended well, whose table starts with its readings.
    /// </summary>
    internal int LeadRowCount()
    {
        for (var row = 0; row < _rows.Count; row++)
        {
            if (_rows[row].Kind is SummaryRowKind.Heading or SummaryRowKind.Metric or SummaryRowKind.NotMeasured)
            {
                return row;
            }
        }

        return _rows.Count;
    }

    /// <summary>True when every line of the rows that come first (see <see cref="LeadRowCount"/>) is among the lines the table shows from its top, with no scrolling. At 200 % the advice must be seen at once.</summary>
    internal bool LeadWholeAtTop()
    {
        var fit = RowsThatFit(Height);
        for (var row = 0; row < LeadRowCount(); row++)
        {
            var last = LastLineOf(row);
            if (last < 0 || last >= fit)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The cells of the table whose text the column cuts ("line:column text"): none must be, now that a sentence wraps. The smoke run asks this of every table it takes a picture of.</summary>
    internal IReadOnlyList<string> UnreadableCuts()
    {
        var cuts = new List<string>();
        for (var line = 0; line < _lines.Count; line++)
        {
            for (var column = 0; column < Columns.Count; column++)
            {
                if (IsCut(line, column))
                {
                    cuts.Add(line.ToString(CultureInfo.InvariantCulture) + ":" + column.ToString(CultureInfo.InvariantCulture) + " " + LineCell(line, column).Text);
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

    private static bool IsBold(SummaryRowKind kind) => kind is SummaryRowKind.Heading or SummaryRowKind.Section;

    /// <summary>A sentence of the summary's own: it starts in the first column and runs across the others.</summary>
    private static bool IsSpan(SummaryRow row) => row.Kind is SummaryRowKind.Record or SummaryRowKind.Heading || (row.Kind == SummaryRowKind.Note && row.Item.Length == 0);

    private static string SpanText(SummaryRow row) => row.Kind is SummaryRowKind.Record or SummaryRowKind.Heading ? row.Item : row.Time;

    /// <summary>A reading: a line with numbers in it (a reading of the run, a not-measured one, a reading of a bench or load section).</summary>
    private static bool IsReading(SummaryRow row) => row.Kind is SummaryRowKind.Metric or SummaryRowKind.NotMeasured || (row.Kind == SummaryRowKind.Detail && row.Min.Length > 0);

    private void Rebuild()
    {
        // The native list keeps its place when the rows change: a table scrolled down for a long run would show nothing at all for a short one.
        // It goes back to its first row while it still has the rows it was scrolled in.
        ScrollToFirstRow();
        _rows = _detail is null ? [] : RunSummaryRows.Build(_detail, _showCalculation);

        // The lines say which row they belong to: the lines of the old run mean nothing to the rows of the new one. Between here and the new lines (the
        // columns change width, and the native list may ask for a line then) the grid has none, so it answers with empty cells and never with the rows
        // of one run under the lines of another.
        _lines = [];
        SelectedIndices.Clear();
        MeasureNeeds();
        FitContentColumns(force: true);
        Invalidate();
    }

    private void MeasureNeeds()
    {
        _readingItemNeed = 0;
        _readingTimeNeed = 0;
        _itemWordNeed = 0;
        Array.Clear(_numberNeed);
        var around = Scaled(8) * 2;
        foreach (var row in _rows)
        {
            if (IsSpan(row))
            {
                continue;
            }

            var indent = around + ((row.Indent + 1) * Scaled(16));
            // A label wraps between its words, never inside one: the column is at least as wide as the widest word, or that word would be cut.
            foreach (var word in Tokens(row.Item))
            {
                _itemWordNeed = Math.Max(_itemWordNeed, indent + TextWidth(word.Trim(), IsBold(row.Kind)));
            }

            // What the readings need, one line each: their label, and the line saying how long they were measured. Everything else may wrap.
            if (IsReading(row))
            {
                _readingItemNeed = Math.Max(_readingItemNeed, indent + TextWidth(row.Item, IsBold(row.Kind)));
                if (row.Kind != SummaryRowKind.NotMeasured)
                {
                    _readingTimeNeed = Math.Max(_readingTimeNeed, around + TextWidth(row.Time, bold: false));
                }
            }

            for (var i = 0; i < _numberNeed.Length; i++)
            {
                _numberNeed[i] = Math.Max(_numberNeed[i], around + TextWidth(NumberText(row, MinColumn + i), bold: false));
            }
        }
    }

    /// <summary>The item and number columns are as wide as their own content (and header); the last column takes what is left. The lines are made again when the room for a sentence changed.</summary>
    private void FitContentColumns(bool force = false)
    {
        if (Columns.Count != TimeColumn + 1)
        {
            return;
        }

        var air = Scaled(8);
        var numbers = 0;
        for (var i = 0; i < _numberNeed.Length; i++)
        {
            var width = Math.Max(Math.Max(HeaderNeed(MinColumn + i), _numberNeed[i]), Scaled(56)) + Scaled(2);
            Columns[MinColumn + i].Width = width;
            numbers += width;
        }

        // The item column is as wide as the labels of the readings (a longer label, of a limit or a note, wraps), but it leaves the last column room for
        // its own header and for the line that says how long a reading was measured, whole on one line: at 200 % on a 1080-line screen a reading took two
        // lines for that line, and the table showed two readings. The columns together are never wider than the list (the right end was cut off).
        var itemCeiling = Math.Max(ClientSize.Width * 40 / 100, Scaled(200));
        var itemWanted = Math.Min(Math.Max(Math.Max(HeaderNeed(ItemColumn), _readingItemNeed), Scaled(110)), itemCeiling) + air;
        var timeFloor = Math.Max(HeaderNeed(TimeColumn), Math.Max(Scaled(200), _readingTimeNeed + air));
        var itemFloor = Math.Max(Math.Max(HeaderNeed(ItemColumn), Scaled(110)), _itemWordNeed + air);
        var itemRoom = Math.Max(ColumnRoom() - numbers - timeFloor, itemFloor);
        Columns[ItemColumn].Width = Math.Max(Math.Min(itemWanted, itemRoom), itemFloor);

        FitLastColumn();
        var key = (TextRoom(ItemColumn, 0, false), TextRoom(TimeColumn, 0, false), TextRoom(ItemColumn, 0, true));
        if (force || key != _wrapKey)
        {
            _wrapKey = key;
            _lines = BuildLines();
            VirtualListSize = _lines.Count;
            Invalidate();
        }
    }

    private List<Line> BuildLines()
    {
        var lines = new List<Line>(_rows.Count);
        for (var r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            if (IsSpan(row))
            {
                var parts = Wrap(SpanText(row), TextRoom(ItemColumn, row.Indent, true), IsBold(row.Kind));
                for (var i = 0; i < Math.Max(parts.Count, 1); i++)
                {
                    lines.Add(new Line(r, i, i < parts.Count ? parts[i] : string.Empty, string.Empty, Span: true));
                }

                continue;
            }

            var items = Wrap(row.Item, TextRoom(ItemColumn, row.Indent, false), IsBold(row.Kind));
            var times = Wrap(row.Time, TextRoom(TimeColumn, 0, false), bold: false);
            for (var i = 0; i < Math.Max(Math.Max(items.Count, times.Count), 1); i++)
            {
                lines.Add(new Line(r, i, i < items.Count ? items[i] : string.Empty, i < times.Count ? times[i] : string.Empty, Span: false));
            }
        }

        return lines;
    }

    /// <summary>A text broken into lines that each fit <paramref name="room"/> pixels (a single word wider than that stays whole on a line of its own). A text that fits is one line; no text, none.</summary>
    internal List<string> Wrap(string text, int room, bool bold)
    {
        var lines = new List<string>();
        if (text.Length == 0)
        {
            return lines;
        }

        if (room <= 0 || TextWidth(text, bold) <= room)
        {
            lines.Add(text);
            return lines;
        }

        var line = string.Empty;
        foreach (var token in Tokens(text))
        {
            var candidate = line + token;
            if (line.Length == 0 || TextWidth(candidate.TrimEnd(), bold) <= room)
            {
                line = candidate;
                continue;
            }

            lines.Add(line.TrimEnd());
            line = token.TrimStart();
        }

        if (line.Length > 0)
        {
            lines.Add(line.TrimEnd());
        }

        return lines;
    }

    private const string NoStart = "、。，．）」』】〕！？：；・ー…）,.:;!?)]%·";
    private const string NoEnd = "（「『【〔([";

    /// <summary>The places a text may be broken at: after a space, and between two letters of a language that does not use spaces (not before a closing mark, not after an opening one).</summary>
    private static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var start = 0;
        for (var i = 1; i < text.Length; i++)
        {
            var before = text[i - 1];
            var at = text[i];
            // A mark that may not start a line (a dot between two parts of a line, a percent sign) stays with the word before it, a space or not.
            var afterSpace = before == ' ' && at != ' ' && NoStart.IndexOf(at, StringComparison.Ordinal) < 0;
            var betweenWide = (IsWide(before) || IsWide(at)) && before != ' ' && at != ' '
                && NoStart.IndexOf(at, StringComparison.Ordinal) < 0 && NoEnd.IndexOf(before, StringComparison.Ordinal) < 0;
            var breakHere = afterSpace || betweenWide;
            if (breakHere)
            {
                tokens.Add(text[start..i]);
                start = i;
            }
        }

        tokens.Add(text[start..]);
        return tokens;
    }

    /// <summary>True for a letter of the languages that are written without spaces (kana, kanji, full-width forms).</summary>
    private static bool IsWide(char c) => c >= 0x2E80 && c <= 0xFFEF;

    private GridCell Cell(int row, int column)
    {
        var line = _rows[row];
        switch (column)
        {
            case ItemColumn:
                return new GridCell(line.Item, line.Kind is SummaryRowKind.Detail or SummaryRowKind.Note ? CellTone.Dim : CellTone.Normal)
                {
                    Bold = IsBold(line.Kind),
                    Indent = line.Indent,
                };
            case TimeColumn:
                return new GridCell(line.Time, line.Tone == CellTone.Normal && line.Kind == SummaryRowKind.Metric ? CellTone.Dim : line.Tone);
            default:
                var text = NumberText(line, column);
                return new GridCell(text, line.Missing || text == Fmt.Dash ? CellTone.Dim : CellTone.Normal);
        }
    }

    private static string NumberText(SummaryRow line, int column) => column switch
    {
        1 => line.Min,
        2 => line.Avg,
        3 => line.Max,
        _ => line.Sustained,
    };

    /// <summary>One cell of one line of the table: what a paint draws.</summary>
    private GridCell LineCell(int lineIndex, int column)
    {
        if (lineIndex < 0 || lineIndex >= _lines.Count)
        {
            return new GridCell(string.Empty);
        }

        var line = _lines[lineIndex];
        if (line.Row < 0 || line.Row >= _rows.Count)
        {
            return new GridCell(string.Empty);
        }

        var row = _rows[line.Row];
        if (line.Span)
        {
            return column == ItemColumn
                ? new GridCell(line.Item, row.Tone) { Indent = row.Indent, Span = true, Bold = IsBold(row.Kind) }
                : new GridCell(string.Empty);
        }

        switch (column)
        {
            case ItemColumn:
                return new GridCell(line.Item, row.Kind is SummaryRowKind.Detail or SummaryRowKind.Note ? CellTone.Dim : CellTone.Normal)
                {
                    Bold = IsBold(row.Kind),
                    Indent = row.Indent,
                };
            case TimeColumn:
                return new GridCell(line.Time, row.Tone == CellTone.Normal && row.Kind == SummaryRowKind.Metric ? CellTone.Dim : row.Tone);
            default:
                if (line.Part != 0)
                {
                    return new GridCell(string.Empty);
                }

                var text = NumberText(row, column);
                return new GridCell(text, row.Missing || text == Fmt.Dash ? CellTone.Dim : CellTone.Normal);
        }
    }

    private string? Tip(int line, int column)
    {
        if (line < 0 || line >= _lines.Count || !IsCut(line, column))
        {
            return null;
        }

        return LineCell(line, column).Text;
    }
}
