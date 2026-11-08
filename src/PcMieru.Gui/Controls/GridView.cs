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
    private const int WmPaint = 0x000F;

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

    /// <summary>The DPI the column widths are in. Column widths are plain pixels that nothing rescales, so a change is followed by hand.</summary>
    private int _columnDpi;

    public GridView()
    {
        _columnDpi = DeviceDpi;
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

    /// <summary>The column that takes the width left over (see <see cref="FitLastColumn"/>); -1 is the last one. A table of numbers lets its name column take it.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int FillColumn { get; set; } = -1;

    /// <summary>Makes one column (the last unless <see cref="FillColumn"/> says otherwise) take whatever width is left, so the header never shows an undrawn strip on its right.</summary>
    public void FitLastColumn()
    {
        if (_fitting || Columns.Count == 0 || !IsHandleCreated)
        {
            return;
        }

        var fitted = false;
        _fitting = true;
        try
        {
            var fill = FillColumn >= 0 && FillColumn < Columns.Count ? FillColumn : Columns.Count - 1;
            var used = 0;
            for (var i = 0; i < Columns.Count; i++)
            {
                if (i != fill)
                {
                    used += Columns[i].Width;
                }
            }

            var last = Columns[fill];
            var wanted = Math.Max(ClientSize.Width - used - VerticalBarAllowance(), FillColumn >= 0 ? Scaled(140) : MinimumFillWidth(last));
            if (last.Width != wanted)
            {
                last.Width = wanted;
            }

            fitted = used + last.Width + VerticalBarAllowance() <= ClientSize.Width;
        }
        finally
        {
            _fitting = false;
        }

        if (fitted)
        {
            HideStaleHorizontalScroll();
        }
    }

    /// <summary>
    /// The list shows a horizontal scroll bar for the moment its columns were wider than the window (a vertical bar
    /// appearing, a DPI change), and it counts the vertical bar's width into its range even when the columns fit.
    /// The bar costs a row's height (the last row is cut in half), so it is taken away whenever the columns fit.
    /// </summary>
    private void HideStaleHorizontalScroll()
    {
        // Reading a column width is a message to the list, which comes back here: the flag stops that.
        if (_hidingScroll || !IsHandleCreated || (GetWindowLongPtr(Handle, GwlStyle).ToInt64() & WsHScroll) == 0)
        {
            return;
        }

        _hidingScroll = true;
        try
        {
            if (!ColumnsOverflow())
            {
                ShowScrollBar(Handle, SbHorz, false);
            }
        }
        finally
        {
            _hidingScroll = false;
        }
    }

    private bool _hidingScroll;

    private bool ColumnsOverflow()
    {
        var total = 0;
        foreach (ColumnHeader column in Columns)
        {
            total += column.Width;
        }

        return total + VerticalBarAllowance() > ClientSize.Width;
    }

    /// <summary>
    /// With a vertical scroll bar showing, the list counts the bar's width into the width its columns need (the client
    /// width is already without it): columns that fill the client then still raise a horizontal bar. This much is left free.
    /// </summary>
    private int VerticalBarAllowance() =>
        IsHandleCreated && (GetWindowLongPtr(Handle, GwlStyle).ToInt64() & WsVScroll) != 0
            ? SystemInformation.GetVerticalScrollBarWidthForDpi(CurrentDpi)
            : 0;

    private const long WsVScroll = 0x00200000;
    private const int GwlStyle = -16;
    private const long WsHScroll = 0x00100000;
    private const int SbHorz = 0;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowScrollBar(IntPtr hwnd, int bar, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool show);

    /// <summary>The least a column that takes the leftover width may shrink to: its own header text must stay readable.</summary>
    private int MinimumFillWidth(ColumnHeader column) =>
        Math.Max(Scaled(48), TextRenderer.MeasureText(column.Text, Font).Width + Scaled(14));

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
        var header = SendMessage(Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
        if (header != IntPtr.Zero)
        {
            _headerWindow ??= new HeaderStrip(this);
            _headerWindow.AssignHandle(header);
        }

        FitLastColumn();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _headerWindow?.ReleaseHandle();
        base.OnHandleDestroyed(e);
    }

    private HeaderStrip? _headerWindow;

    private const int LvmGetHeader = 0x101F;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// The native header paints the strip right of the last column in the system colour (white in the dark theme). The
    /// strip exists because the columns leave the vertical bar's width free (see <see cref="VerticalBarAllowance"/>);
    /// this paints it like the rest of the header after the header has drawn itself.
    /// </summary>
    private sealed class HeaderStrip(GridView owner) : NativeWindow
    {
        private const int WmPaint = 0x000F;
        private const int HdmGetItemCount = 0x1200;
        private const int HdmGetItemRect = 0x1207;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WmPaint && owner._palette is { } palette)
            {
                Fill(palette);
            }
        }

        private void Fill(Palette palette)
        {
            var count = (int)SendMessage(Handle, HdmGetItemCount, IntPtr.Zero, IntPtr.Zero);
            if (count <= 0)
            {
                return;
            }

            var rect = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
            try
            {
                if (SendMessage(Handle, HdmGetItemRect, (IntPtr)(count - 1), rect) == IntPtr.Zero)
                {
                    return;
                }

                var right = System.Runtime.InteropServices.Marshal.ReadInt32(rect, 8);
                var bottom = System.Runtime.InteropServices.Marshal.ReadInt32(rect, 12);
                var width = owner.ClientSize.Width;
                if (right >= width)
                {
                    return;
                }

                using var graphics = Graphics.FromHwnd(Handle);
                using var back = new SolidBrush(palette.Window);
                graphics.FillRectangle(back, right, 0, width - right, bottom);
                using var line = new Pen(palette.Border);
                graphics.DrawLine(line, right, bottom - 1, width, bottom - 1);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(rect);
            }
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateRowHeight();
        SyncDpi();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateRowHeight();
        SyncDpi();
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        SyncDpi();
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        SyncDpi();
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
            menu.Show(this, new Point(at.X + Scaled(24), at.Y + RowHeight));
            return;
        }

        base.WndProc(ref m);
        if (m.Msg == WmPaint)
        {
            BlankBelowRows();
        }

        // The list raises its own horizontal bar at moments of its choosing (after an item count or a size change): checked after every message.
        HideStaleHorizontalScroll();
    }

    /// <summary>
    /// The themed list draws its column dividers down the empty area under the last row (two thin lines per column, a
    /// light-blue one in the light theme), which looks like leftover rows. The area under the rows is simply plain surface.
    /// </summary>
    private void BlankBelowRows()
    {
        if (_palette is not { } palette || !IsHandleCreated || View != View.Details)
        {
            return;
        }

        var top = VirtualListSize > 0 ? GetItemRect(VirtualListSize - 1).Bottom : HeaderBottom();
        var height = ClientSize.Height - top;
        if (top < 0 || height <= 0)
        {
            return;
        }

        using var graphics = Graphics.FromHwnd(Handle);
        using var back = new SolidBrush(palette.Surface);
        graphics.FillRectangle(back, 0, top, ClientSize.Width, height);
    }

    private int HeaderBottom() => Math.Max(Font.Height + Scaled(10), RowHeight);

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
                var pad = Scaled(6);
                e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + pad, e.Bounds.Right - 1, e.Bounds.Bottom - pad);
            }
        }

        var pad8 = Scaled(8);
        var column = e.Header!;
        var rightAligned = column.TextAlign == HorizontalAlignment.Right;
        // A right-aligned header keeps its text over the numbers; its arrow goes right next to the text, on the left of it.
        var arrow = e.ColumnIndex == _sortColumn && !rightAligned ? Scaled(14) : 0;
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

        if (e.ColumnIndex == _sortColumn)
        {
            var size = Scaled(4);
            var cx = e.Bounds.Right - pad8 - size;
            if (rightAligned)
            {
                var textWidth = TextRenderer.MeasureText(e.Graphics, column.Text, Font, new Size(int.MaxValue, e.Bounds.Height), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                cx = Math.Max(e.Bounds.Right - pad8 - textWidth - Scaled(8), e.Bounds.Left + size + 2);
            }

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
        // Across the whole width: with a vertical bar showing, the columns stop a little short of the right edge.
        var band = new Rectangle(e.Bounds.Left, e.Bounds.Top, Math.Max(ClientSize.Width - e.Bounds.Left, e.Bounds.Width), e.Bounds.Height);
        e.Graphics.FillRectangle(back, band);
        if (selected)
        {
            using var mark = new SolidBrush(palette.Accent);
            e.Graphics.FillRectangle(mark, e.Bounds.Left, e.Bounds.Top + 2, Scaled(3), e.Bounds.Height - 4);
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
        var pad = Scaled(8);
        var bounds = new Rectangle(left + pad, row.Top, Math.Max(width - (pad * 2), 0), row.Height);
        if (e.ColumnIndex == 0)
        {
            var step = Scaled(16);
            var indent = cell.Indent * step;
            if (cell.Glyph != ExpanderGlyph.None)
            {
                DrawGlyph(e.Graphics, new Rectangle(left + pad + indent - Scaled(2), row.Top, step, row.Height), cell.Glyph, selected ? palette.SelectionText : palette.TextDim);
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
        var step = Scaled(16);
        var x = rect.Left + Scaled(8) + (cell.Indent * step) - Scaled(2);
        return cell.Glyph == ExpanderGlyph.None ? Rectangle.Empty : new Rectangle(x, rect.Top, step, rect.Height);
    }

    private int HeaderOffset()
    {
        // The horizontal scroll position: how far the first column's left edge is left of the control's.
        return VirtualListSize > 0 ? -GetItemRect(0, ItemBoundsPortion.Entire).Left : 0;
    }

    private void DrawGlyph(Graphics g, Rectangle slot, ExpanderGlyph glyph, Color color)
    {
        var size = Scaled(4);
        var cx = slot.Left + (slot.Width / 2);
        var cy = slot.Top + (slot.Height / 2);
        Point[] points = glyph == ExpanderGlyph.Expanded
            ? [new Point(cx - size, cy - (size / 2)), new Point(cx, cy + (size / 2)), new Point(cx + size, cy - (size / 2))]
            : [new Point(cx - (size / 2), cy - size), new Point(cx + (size / 2), cy), new Point(cx - (size / 2), cy + size)];
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(color, Math.Max(1.5f, Scaled(1))))
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

    /// <summary>The DPI the window is at now. The form is asked first: it is the first to learn about a change.</summary>
    private int CurrentDpi => FindForm()?.DeviceDpi ?? DeviceDpi;

    private int Scaled(int logical) => (int)Math.Round(logical * CurrentDpi / 96.0);

    /// <summary>After a DPI change the column widths (and row height) are brought to the new scale.</summary>
    private void SyncDpi()
    {
        var dpi = CurrentDpi;
        if (dpi == _columnDpi)
        {
            return;
        }

        var factor = (double)dpi / _columnDpi;
        _columnDpi = dpi;
        _fitting = true;
        try
        {
            foreach (ColumnHeader column in Columns)
            {
                column.Width = (int)Math.Round(column.Width * factor);
            }
        }
        finally
        {
            _fitting = false;
        }

        UpdateRowHeight();
        FitLastColumn();
        Invalidate();
    }

    private void UpdateRowHeight()
    {
        RowHeight = Math.Max(Font.Height + Scaled(14), Scaled(28));
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
