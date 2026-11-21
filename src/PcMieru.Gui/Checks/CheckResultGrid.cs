using PcMieru.Checks;

namespace PcMieru.Gui;

/// <summary>
/// The table of one check result: item, verdict, value, evidence. Rows that belong to no part (the system's own items) come first; then
/// one group per part (a disk, a memory module, a logical processor) with a header row that carries the part's name and its overall
/// verdict. A counter's change (the ".delta" row of the registry) sits right under the row it belongs to, indented, with the change
/// written in the evidence column ("1,204 → 1,227（+23）"). Info rows are observations: their word is plain text, never the accent.
/// The rows are built once per result, so painting asks a list and does nothing else.
/// </summary>
internal sealed class CheckResultGrid : GridView
{
    private const int ItemColumn = 0;
    private const int VerdictColumn = 1;
    private const int ValueColumn = 2;
    private const int EvidenceColumn = 3;

    private readonly List<Row> _rows = [];
    private CheckResult? _result;

    public CheckResultGrid()
    {
        Dock = DockStyle.Fill;
        Cells = Cell;
        RowStyles = row => row >= 0 && row < _rows.Count && _rows[row].Header ? RowStyle.Header : RowStyle.Normal;
        CellTip = Tip;
        FillColumn = EvidenceColumn;
        SizeChanged += (_, _) => FitLastColumn();
    }

    /// <summary>The result on show (null: none).</summary>
    public CheckResult? Result => _result;

    /// <summary>The number of rows on the screen, headers included.</summary>
    public int RowCount => _rows.Count;

    /// <summary>The item of a row, or null for a header row.</summary>
    public CheckItem? ItemAt(int row) => row >= 0 && row < _rows.Count ? _rows[row].Item : null;

    /// <summary>The text of a cell, as it is drawn.</summary>
    public string TextAt(int row, int column) => row >= 0 && row < _rows.Count ? Cell(row, column).Text : string.Empty;

    /// <summary>The tone of a cell, as it is drawn.</summary>
    public CellTone ToneAt(int row, int column) => row >= 0 && row < _rows.Count ? Cell(row, column).Tone : CellTone.Normal;

    /// <summary>
    /// The cells whose text the column cuts and that the person cannot read in full anyway ("row:column text"). A label of an item and a verdict
    /// word must never be cut. Any other cell may be (a long evidence line, a part's name in its header row) when its tooltip holds the whole
    /// text. The smoke run asks this of every table it takes a picture of (DESIGN-A §7).
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
                var never = !_rows[row].Header && column is ItemColumn or VerdictColumn;
                if (never || Tip(row, column) != text)
                {
                    cuts.Add(row.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + column.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + text);
                }
            }
        }

        return cuts;
    }

    /// <summary>The columns, written for the widths of the logical pixel at the design font (a window that is denser scales them).</summary>
    public void PrepareColumns(Func<int, int> px, string accessibleName)
    {
        PageLayout.Prepare(
            this,
            accessibleName,
            (Strings.Get("gui.chk.col.item"), ItemColumnWidth(), HorizontalAlignment.Left),
            (Strings.Get("gui.chk.col.verdict"), px(110), HorizontalAlignment.Left),
            (Strings.Get("gui.chk.col.value"), px(190), HorizontalAlignment.Left),
            (Strings.Get("gui.chk.col.evidence"), px(310), HorizontalAlignment.Left));
    }

    /// <summary>
    /// The item column, in pixels of the scale the grid is at now: the longest item label of the registry (in the language on show) with the
    /// room the grid keeps around a text (its padding on both sides, and the indent of a change row under a part). A fixed width was too wide
    /// for the short labels of one language and cut the long ones of the other. It is measured in device pixels, not written in logical ones:
    /// a window at 150% grows its font as well, and the text grows with the font and the DPI while the room around it grows with the DPI only.
    /// </summary>
    internal int ItemColumnWidth()
    {
        var widest = 0;
        foreach (var spec in CheckItemRegistry.All)
        {
            var width = TextRenderer.MeasureText(
                CheckFormat.ItemLabel(spec.Id),
                Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
            widest = Math.Max(widest, width);
        }

        // The padding on both sides, the steps the grid keeps in the first column for the widest case (a change row under a part has the
        // indent 2, and the grid keeps the indent plus one step), and a few pixels of air.
        const int indentSteps = 3;
        return widest + (Scaled(8) * 2) + (indentSteps * Scaled(16)) + Scaled(4);
    }

    /// <summary>The columns were scaled for a new DPI or font: the item column is measured again at that scale.</summary>
    protected override void OnColumnsRescaled()
    {
        base.OnColumnsRescaled();
        if (Columns.Count == EvidenceColumn + 1)
        {
            Columns[ItemColumn].Width = ItemColumnWidth();
        }
    }

    /// <summary>Shows a result (or nothing). Rebuilds the rows and keeps the list's position at the top.</summary>
    public void Show(CheckResult? result)
    {
        _result = result;
        Rebuild();
    }

    /// <summary>The language changed: the headers and every row are written again.</summary>
    public void ApplyLanguage()
    {
        if (Columns.Count == EvidenceColumn + 1)
        {
            Columns[ItemColumn].Text = Strings.Get("gui.chk.col.item");
            Columns[VerdictColumn].Text = Strings.Get("gui.chk.col.verdict");
            Columns[ValueColumn].Text = Strings.Get("gui.chk.col.value");
            Columns[EvidenceColumn].Text = Strings.Get("gui.chk.col.evidence");
            // The labels are written again in the other language: the item column follows its longest.
            Columns[ItemColumn].Width = ItemColumnWidth();
            FitLastColumn();
        }

        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Clear();
        if (_result is { } result)
        {
            Build(result);
        }

        SelectedIndices.Clear();
        VirtualListSize = _rows.Count;
        Invalidate();
    }

    private void Build(CheckResult result)
    {
        var items = result.Items;
        var deltaOf = new Dictionary<string, Dictionary<string, CheckItem>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (CheckItemRegistry.TryGet(item.Id, out var spec) && spec.DeltaOf is { } baseId)
            {
                var key = item.Subject?.Id ?? string.Empty;
                if (!deltaOf.TryGetValue(key, out var byBase))
                {
                    deltaOf[key] = byBase = new Dictionary<string, CheckItem>(StringComparer.Ordinal);
                }

                byBase[baseId] = item;
            }
        }

        var shown = new HashSet<CheckItem>(ReferenceEqualityComparer.Instance);
        var order = new List<string>();
        var groups = new Dictionary<string, List<CheckItem>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = item.Subject?.Id ?? string.Empty;
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
                order.Add(key);
            }

            list.Add(item);
        }

        // Items of no part go first, then the parts in the order they first appear.
        if (groups.TryGetValue(string.Empty, out var loose))
        {
            AddGroup(loose, deltaOf.GetValueOrDefault(string.Empty), shown, null);
        }

        foreach (var key in order)
        {
            if (key.Length == 0)
            {
                continue;
            }

            var list = groups[key];
            AddGroup(list, deltaOf.GetValueOrDefault(key), shown, list[0].Subject);
        }
    }

    private void AddGroup(List<CheckItem> list, Dictionary<string, CheckItem>? deltas, HashSet<CheckItem> shown, CheckSubject? subject)
    {
        if (subject is not null)
        {
            var judged = list.Where(static i => i.Verdict != ItemVerdict.Info).ToList();
            ItemVerdict? overall = judged.Count == 0 ? null : CheckFormat.ToItem(CheckVerdicts.Overall(judged));
            _rows.Add(new Row(null, CheckFormat.SubjectText(subject), overall, Header: true, Indent: 0));
        }

        var indent = subject is null ? 0 : 1;
        foreach (var item in list)
        {
            if (shown.Contains(item))
            {
                continue;
            }

            if (CheckItemRegistry.TryGet(item.Id, out var spec) && spec.DeltaOf is not null)
            {
                // A change row is written under its own counter; one whose counter is not in the result stands alone.
                var hasBase = list.Any(other => other.Id == spec.DeltaOf);
                if (hasBase)
                {
                    continue;
                }
            }

            shown.Add(item);
            var counterAt = _rows.Count;
            _rows.Add(new Row(item, CheckFormat.ItemLabel(item.Id), item.Verdict, Header: false, Indent: indent));
            if (CheckItemRegistry.DeltaId(item.Id) is { } deltaId && deltas is not null && deltas.TryGetValue(item.Id, out var delta) && delta.Id == deltaId && shown.Add(delta))
            {
                // The change is written once, on the change row; the counter row above it does not repeat it (DESIGN-A §7).
                _rows[counterAt] = _rows[counterAt] with { DeltaBelow = true };
                _rows.Add(new Row(delta, CheckFormat.ItemLabel(delta.Id), delta.Verdict, Header: false, Indent: indent + 1));
            }
        }
    }

    private GridCell Cell(int row, int column)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return new GridCell(string.Empty);
        }

        var line = _rows[row];
        if (line.Header)
        {
            return column switch
            {
                ItemColumn => new GridCell(line.Label) { Bold = true },
                VerdictColumn when line.Verdict is { } verdict => new GridCell(CheckFormat.VerdictWord(verdict), CheckFormat.Tone(verdict)) { Bold = verdict == ItemVerdict.Check },
                _ => new GridCell(string.Empty),
            };
        }

        var item = line.Item!;
        switch (column)
        {
            case ItemColumn:
                return new GridCell(line.Label) { Indent = line.Indent };
            case VerdictColumn:
                return new GridCell(CheckFormat.VerdictWord(item.Verdict), CheckFormat.Tone(item.Verdict)) { Bold = item.Verdict == ItemVerdict.Check };
            case ValueColumn:
                return item.Value is null
                    ? new GridCell(Fmt.Dash, CellTone.Dim)
                    : new GridCell(CheckFormat.Safe(() => CheckFormat.ValueText(item), () => CheckFormat.RawValueText(item)));
            default:
                return new GridCell(Evidence(line), CellTone.Dim);
        }
    }

    /// <summary>The evidence cell of a row. A value that cannot be written (a result that did not pass validation is shown anyway) falls back to its raw text and never takes the table down.</summary>
    private static string Evidence(Row line) =>
        CheckFormat.Safe(() => CheckFormat.EvidenceText(line.Item!, withChange: !line.DeltaBelow), () => CheckFormat.RawEvidenceText(line.Item!));

    private string? Tip(int row, int column)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return null;
        }

        var line = _rows[row];
        if (line.Item is not { } item)
        {
            // A part's header row: its name, in full, when the column cuts it.
            return column == ItemColumn && IsCut(row, column) ? line.Label : null;
        }

        return column switch
        {
            ItemColumn when IsCut(row, column) => line.Label,
            VerdictColumn when item.Verdict == ItemVerdict.Info => Strings.Get("gui.chk.info.tip"),
            EvidenceColumn => Evidence(line) is { Length: > 0 } text ? text : null,
            ValueColumn when item.Value is null => Evidence(line) is { Length: > 0 } why ? why : null,
            ValueColumn when IsCut(row, column) => Cell(row, column).Text,
            _ => null,
        };
    }

    /// <param name="DeltaBelow">The change row of this counter sits directly under it, so the change text is written there and not here.</param>
    private sealed record Row(CheckItem? Item, string Label, ItemVerdict? Verdict, bool Header, int Indent, bool DeltaBelow = false);
}
