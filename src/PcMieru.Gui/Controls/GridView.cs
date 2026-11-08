using System.Drawing.Drawing2D;

namespace PcMieru.Gui;

internal enum CellTone
{
    Normal,

    /// <summary>Secondary text and "not available" values (grey).</summary>
    Dim,

    /// <summary>High load or something that needs a look (accent colour, always together with its text).</summary>
    Accent,
}

internal enum ExpanderGlyph
{
    None,
    Collapsed,
    Expanded,
}

internal enum RowStyle
{
    Normal,

    /// <summary>A group header row: a little darker, bold.</summary>
    Header,
}

/// <summary>What one cell shows. A struct, so asking for it every paint costs no allocation beyond the text itself.</summary>
internal struct GridCell
{
    public string Text;
    public CellTone Tone;
    public bool Bold;

    /// <summary>Tree depth, only meaningful for the first column.</summary>
    public int Indent;

    public ExpanderGlyph Glyph;

    public GridCell(string text, CellTone tone = CellTone.Normal)
    {
        Text = text;
        Tone = tone;
        Bold = false;
        Indent = 0;
        Glyph = ExpanderGlyph.None;
    }
}

internal delegate GridCell GridCellProvider(int row, int column);

/// <summary>
/// The one list control of the app: a virtual, owner-drawn details ListView. Virtual, so only the rows on screen are
/// asked for; owner-drawn, so the dark theme, the grey "not available" text, tree glyphs and group header rows look the
/// same on every Windows build instead of depending on what the native header and rows do in dark mode.
/// Rows live in the page's model; this control only asks for a cell when it paints it.
/// </summary>
internal class GridView : ListView, IThemed
{
    private const int WmContextMenu = 0x007B;

    private readonly ImageList _rowHeight = new();
    private readonly ToolTip _tip = new() { ShowAlways = true };
    private readonly Dictionary<int, ListViewItem> _items = [];
    private Palette? _palette;
    private Color _selectedDim = Color.Gray;
    private int _drawSelected = -1;
    private int _lastTipRow = -2;
    private int _lastTipColumn = -2;
    private int _sortColumn = -1;
    private bool _sortDescending;
    private bool _fitting;

    public GridView()
    {
        DoubleBuffered = true;
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = false;
        BorderStyle = BorderStyle.None;
        VirtualMode = true;
        OwnerDraw = true;
        Dock = DockStyle.Fill;
        SmallImageList = _rowHeight;
        RetrieveVirtualItem += OnRetrieveItem;
        TabStop = true;
    }

    /// <summary>Supplies the text and style of one cell. Must be cheap: it runs for every visible cell on every paint.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal GridCellProvider? Cells { get; set; }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<int, RowStyle>? RowStyles { get; set; }

    /// <summary>Why a cell shows what it shows (the reason a value is "-"). Null for no tooltip.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<int, int, string?>? CellTip { get; set; }

    /// <summary>The user clicked the expander glyph of a tree row (or double-clicked the row).</summary>
    public event Action<int>? ExpanderClicked;

    public int SortColumn => _sortColumn;

    public bool SortDescending => _sortDescending;

    public int RowHeight { get; private set; }

    /// <summary>Row index under a client-area point, or -1.</summary>
    public int RowAt(Point point) => HitTest(point).Item?.Index ?? -1;

    public int ColumnAt(Point point)
    {
        var x = point.X + HeaderOffset();
        var left = 0;
        for (var i = 0; i < Columns.Count; i++)
        {
            left += Columns[i].Width;
            if (x < left)
            {
                return i;
            }
        }

        return -1;
    }

    public void SetSort(int column, bool descending)
    {
        _sortColumn = column;
        _sortDescending = descending;
        Invalidate(new Rectangle(0, 0, ClientSize.Width, RowHeight + 8));
    }

    /// <summary>Selects one row by index (or none for -1) and scrolls it into view.</summary>
    public void SelectRow(int index, bool scroll = true)
    {
        if (index < 0 || index >= VirtualListSize)
        {
            SelectedIndices.Clear();
            return;
        }

        if (SelectedIndices.Count == 1 && SelectedIndices[0] == index)
        {
            return;
        }

        SelectedIndices.Clear();
        SelectedIndices.Add(index);
        if (scroll)
        {
            EnsureVisible(index);
        }
    }

    public int SelectedRow => SelectedIndices.Count > 0 ? SelectedIndices[0] : -1;

    /// <summary>Makes the last column take whatever width is left, so the header never shows an undrawn strip on its right.</summary>
    public void FitLastColumn()
    {
        if (_fitting || Columns.Count == 0 || !IsHandleCreated)
        {
            return;
        }

        _fitting = true;
        try
        {
            var used = 0;
            for (var i = 0; i < Columns.Count - 1; i++)
            {
                used += Columns[i].Width;
            }

            var last = Columns[^1];
            var wanted = Math.Max(ClientSize.Width - used, MinimumLastColumn);
            if (last.Width != wanted)
            {
                last.Width = wanted;
            }
        }
        finally
        {
            _fitting = false;
        }
    }

    public int MinimumLastColumn => LogicalToDeviceUnits(60);

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        BackColor = palette.Surface;
        ForeColor = palette.Text;
        _selectedDim = Blend(palette.SelectionText, palette.Selection, 0.25);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _rowHeight.Dispose();
            _bold?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        UpdateRowHeight();
        base.OnHandleCreated(e);
        FitLastColumn();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateRowHeight();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateRowHeight();
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        FitLastColumn();
    }

    protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
    {
        base.OnColumnWidthChanged(e);
        FitLastColumn();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var row = RowAt(e.Location);
        if (e.Button == MouseButtons.Right && row >= 0 && SelectedRow != row)
        {
            SelectRow(row);
        }
        else if (e.Button == MouseButtons.Left && row >= 0 && ColumnAt(e.Location) == 0 && GlyphBounds(row).Contains(e.Location))
        {
            ExpanderClicked?.Invoke(row);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var row = RowAt(e.Location);
        if (e.Button == MouseButtons.Left && row >= 0 && Cells is { } cells && cells(row, 0).Glyph != ExpanderGlyph.None)
        {
            ExpanderClicked?.Invoke(row);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (CellTip is null)
        {
            return;
        }

        var row = RowAt(e.Location);
        var column = row >= 0 ? ColumnAt(e.Location) : -1;
        if (row == _lastTipRow && column == _lastTipColumn)
        {
            return;
        }

        _lastTipRow = row;
        _lastTipColumn = column;
        _tip.SetToolTip(this, row >= 0 && column >= 0 ? CellTip(row, column) : null);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _lastTipRow = -2;
        _lastTipColumn = -2;
        _tip.SetToolTip(this, null);
    }

    protected override void WndProc(ref Message m)
    {
        // Menu key / Shift+F10 sends the position (-1, -1): open the menu next to the selected row, not in a corner.
        if (m.Msg == WmContextMenu && (long)m.LParam == -1 && ContextMenuStrip is { } menu)
        {
            var row = SelectedRow;
            var at = row >= 0 ? GetItemRect(row).Location : Point.Empty;
            menu.Show(this, new Point(at.X + LogicalToDeviceUnits(24), at.Y + RowHeight));
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        var palette = _palette;
        if (palette is null)
        {
            e.DrawDefault = true;
            return;
        }

        using (var back = new SolidBrush(palette.Window))
        {
            e.Graphics.FillRectangle(back, e.Bounds);
        }

        using (var line = new Pen(palette.Border))
        {
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            if (e.ColumnIndex < Columns.Count - 1)
            {
                var pad = LogicalToDeviceUnits(6);
                e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + pad, e.Bounds.Right - 1, e.Bounds.Bottom - pad);
            }
        }

        var pad8 = LogicalToDeviceUnits(8);
        var arrow = e.ColumnIndex == _sortColumn ? LogicalToDeviceUnits(14) : 0;
        var column = e.Header!;
        var alignment = column.TextAlign switch
        {
            HorizontalAlignment.Right => TextFormatFlags.Right,
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left,
        };
        var textBounds = new Rectangle(e.Bounds.Left + pad8, e.Bounds.Top, Math.Max(e.Bounds.Width - (pad8 * 2) - arrow, 0), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            column.Text,
            Font,
            textBounds,
            palette.TextDim,
            alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        if (arrow > 0)
        {
            var size = LogicalToDeviceUnits(4);
            var cx = column.TextAlign == HorizontalAlignment.Right
                ? e.Bounds.Left + pad8 // arrow sits on the left of a right-aligned header
                : e.Bounds.Right - pad8 - size;
            var cy = e.Bounds.Top + (e.Bounds.Height / 2);
            Point[] points = _sortDescending
                ? [new Point(cx - size, cy - (size / 2)), new Point(cx + size, cy - (size / 2)), new Point(cx, cy + (size / 2) + 1)]
                : [new Point(cx - size, cy + (size / 2)), new Point(cx + size, cy + (size / 2)), new Point(cx, cy - (size / 2) - 1)];
            var old = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(palette.Accent))
            {
                e.Graphics.FillPolygon(brush, points);
            }

            e.Graphics.SmoothingMode = old;
        }
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        var palette = _palette;
        if (palette is null)
        {
            e.DrawDefault = true;
            return;
        }

        // The state flags the native control hands to a virtual owner-drawn list mark every row as selected; the
        // control's own selection (one row at most) is the truth.
        _drawSelected = SelectedRow;
        var selected = e.ItemIndex == _drawSelected;
        var header = RowStyles?.Invoke(e.ItemIndex) == RowStyle.Header;
        using var back = new SolidBrush(selected ? palette.Selection : header ? palette.Window : palette.Surface);
        e.Graphics.FillRectangle(back, e.Bounds);
        if (selected)
        {
            using var mark = new SolidBrush(palette.Accent);
            e.Graphics.FillRectangle(mark, e.Bounds.Left, e.Bounds.Top + 2, LogicalToDeviceUnits(3), e.Bounds.Height - 4);
        }
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        var palette = _palette;
        if (palette is null || Cells is null)
        {
            e.DrawDefault = true;
            return;
        }

        var cell = Cells(e.ItemIndex, e.ColumnIndex);
        var selected = e.ItemIndex == _drawSelected;
        var header = RowStyles?.Invoke(e.ItemIndex) == RowStyle.Header;
        var row = GetItemRect(e.ItemIndex, ItemBoundsPortion.Entire);
        var left = row.Left;
        for (var i = 0; i < e.ColumnIndex; i++)
        {
            left += Columns[i].Width;
        }

        var width = Columns[e.ColumnIndex].Width;
        var pad = LogicalToDeviceUnits(8);
        var bounds = new Rectangle(left + pad, row.Top, Math.Max(width - (pad * 2), 0), row.Height);
        if (e.ColumnIndex == 0)
        {
            var step = LogicalToDeviceUnits(16);
            var indent = cell.Indent * step;
            if (cell.Glyph != ExpanderGlyph.None)
            {
                DrawGlyph(e.Graphics, new Rectangle(left + pad + indent - LogicalToDeviceUnits(2), row.Top, step, row.Height), cell.Glyph, selected ? palette.SelectionText : palette.TextDim);
            }

            // Text always starts after the glyph slot, so rows with and without children line up.
            var shift = indent + step;
            bounds = new Rectangle(bounds.Left + shift, bounds.Top, Math.Max(bounds.Width - shift, 0), bounds.Height);
        }

        var color = cell.Tone switch
        {
            CellTone.Dim => selected ? _selectedDim : palette.TextDim,
            CellTone.Accent => selected ? palette.SelectionText : palette.Accent,
            _ => selected ? palette.SelectionText : palette.Text,
        };
        var alignment = Columns[e.ColumnIndex].TextAlign switch
        {
            HorizontalAlignment.Right => TextFormatFlags.Right,
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left,
        };
        var font = (cell.Bold || header) ? BoldFont : Font;
        TextRenderer.DrawText(
            e.Graphics,
            cell.Text,
            font,
            bounds,
            color,
            alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    private Font? _bold;

    private Font BoldFont => _bold != null && _bold.Size == Font.Size && _bold.Name == Font.Name
        ? _bold
        : _bold = new Font(Font, FontStyle.Bold);

    private Rectangle GlyphBounds(int row)
    {
        var rect = GetItemRect(row, ItemBoundsPortion.Entire);
        var cell = Cells?.Invoke(row, 0) ?? default;
        var step = LogicalToDeviceUnits(16);
        var x = rect.Left + LogicalToDeviceUnits(8) + (cell.Indent * step) - LogicalToDeviceUnits(2);
        return cell.Glyph == ExpanderGlyph.None ? Rectangle.Empty : new Rectangle(x, rect.Top, step, rect.Height);
    }

    private int HeaderOffset()
    {
        // The horizontal scroll position: how far the first column's left edge is left of the control's.
        return VirtualListSize > 0 ? -GetItemRect(0, ItemBoundsPortion.Entire).Left : 0;
    }

    private void DrawGlyph(Graphics g, Rectangle slot, ExpanderGlyph glyph, Color color)
    {
        var size = LogicalToDeviceUnits(4);
        var cx = slot.Left + (slot.Width / 2);
        var cy = slot.Top + (slot.Height / 2);
        Point[] points = glyph == ExpanderGlyph.Expanded
            ? [new Point(cx - size, cy - (size / 2)), new Point(cx, cy + (size / 2)), new Point(cx + size, cy - (size / 2))]
            : [new Point(cx - (size / 2), cy - size), new Point(cx + (size / 2), cy), new Point(cx - (size / 2), cy + size)];
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(color, Math.Max(1.5f, LogicalToDeviceUnits(1))))
        {
            g.DrawLines(pen, points);
        }

        g.SmoothingMode = old;
    }

    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (!_items.TryGetValue(e.ItemIndex, out var item))
        {
            item = new ListViewItem(string.Empty);
            if (_items.Count > 4096)
            {
                _items.Clear();
            }

            _items[e.ItemIndex] = item;
        }

        // The native control asks for a text per column even when owner-drawn; an item without enough sub-items throws.
        while (item.SubItems.Count < Columns.Count)
        {
            item.SubItems.Add(string.Empty);
        }

        // The text is only for assistive technology; painting never reads it.
        item.Text = Cells is { } cells && Columns.Count > 0 ? cells(e.ItemIndex, 0).Text : string.Empty;
        e.Item = item;
    }

    private void UpdateRowHeight()
    {
        RowHeight = Math.Max(Font.Height + LogicalToDeviceUnits(14), LogicalToDeviceUnits(28));
        // The row height of a details ListView follows its image list: a 1-pixel-wide image of the wanted height sets it.
        if (_rowHeight.ImageSize.Height != RowHeight)
        {
            _rowHeight.Images.Clear();
            _rowHeight.ImageSize = new Size(1, Math.Min(RowHeight, 256));
        }
    }

    private static Color Blend(Color a, Color b, double amountOfA) =>
        Color.FromArgb(
            (int)((a.R * amountOfA) + (b.R * (1 - amountOfA))),
            (int)((a.G * amountOfA) + (b.G * (1 - amountOfA))),
            (int)((a.B * amountOfA) + (b.B * (1 - amountOfA))));
}
