using System.Drawing.Drawing2D;

namespace PcMieru.Gui;

internal enum CellTone
{
    Normal,

    /// <summary>Secondary text and "not available" values (grey).</summary>
    Dim,

    /// <summary>High load or something that needs a look (accent colour, always together with its text).</summary>
    Accent,

    /// <summary>A check that came out well (green, always together with its word).</summary>
    Good,

    /// <summary>A check that wants attention but is not a failure (amber, always together with its word).</summary>
    Warning,

    /// <summary>A check that needs review (red, always together with its word).</summary>
    Danger,
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

    /// <summary>First column only: leave room for a program icon before the text, whether or not it has arrived yet (rows line up).</summary>
    public bool IconSlot;

    /// <summary>First column only: the icon to draw in the slot. Null draws nothing (not known yet, or the file has none).</summary>
    public Image? Icon;

    /// <summary>
    /// A load cue: how far (0 to 1) a quiet bar tinted in the accent colour fills the cell behind the text. 0 draws nothing.
    /// It only helps the eye find the heavy rows; the number is always in the text, so nothing depends on seeing the bar.
    /// </summary>
    public double Bar;

    /// <summary>A cell that opens something when it is clicked: drawn underlined in the accent colour, so the colour is not the only sign.</summary>
    public bool Link;

    /// <summary>First column only: the text runs on across the columns to its right (a sentence of its own; those cells hold nothing and draw nothing).</summary>
    public bool Span;

    public GridCell(string text, CellTone tone = CellTone.Normal)
    {
        Text = text;
        Tone = tone;
        Bold = false;
        Indent = 0;
        Glyph = ExpanderGlyph.None;
        IconSlot = false;
        Icon = null;
        Bar = 0;
        Link = false;
        Span = false;
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
    private const int WmWindowPosChanging = 0x0046;
    private const int SwpNoSize = 0x0001;

    private readonly ImageList _rowHeight = new();
    private readonly ToolTip _tip = new() { ShowAlways = true };
    private readonly Dictionary<int, ListViewItem> _items = [];
    private Palette? _palette;
    private int _drawSelected = -1;
    private int _lastTipRow = -2;
    private int _lastTipColumn = -2;
    private int _headerTipColumn = -2;
    private int _sortColumn = -1;
    private bool _sortDescending;
    private bool _fitting;

    /// <summary>The font size column widths are written for: widths in the code are logical pixels at this size.</summary>
    public const float DesignPoints = 9f;

    /// <summary>The DPI and the font size the column widths are in. Column widths are plain pixels that nothing rescales, so a change is followed by hand.</summary>
    private int _columnDpi;

    private float _columnPoints = DesignPoints;

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

    /// <summary>
    /// <c>--measure</c> only: told which cells (row, column) one paint of the control drew, after that paint is complete (the native paint of a
    /// double-buffered list has presented its buffer when the window procedure returns from WM_PAINT). Null in a normal start: no cell is recorded.
    /// </summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action<IReadOnlyList<(int Row, int Column)>>? PaintObserver
    {
        get => _paintObserver;
        set
        {
            _paintObserver = value;
            if (value is null)
            {
                // Detached: nothing is recorded from here on, and what was recorded is dropped.
                _paintedCells = null;
            }
        }
    }

    private Action<IReadOnlyList<(int Row, int Column)>>? _paintObserver;

    private List<(int Row, int Column)>? _paintedCells;

    /// <summary>Tests: the drawn-cell list exists only while a paint observer is attached (a normal start never builds it).</summary>
    internal bool RecordsPaintedCells => _paintedCells is not null;

    /// <summary>Why a cell shows what it shows (the reason a value is "-"). Null for no tooltip.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<int, int, string?>? CellTip { get; set; }

    /// <summary>What a column header says when the pointer rests on it (its meaning, in a sentence). Null for none. The header is a window of its own, so the grid shows this itself.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<int, string?>? HeaderTip { get; set; }

    /// <summary>The text of the header tip shown last (tests and the smoke run read it; null when none is up).</summary>
    internal string? ShownHeaderTip { get; private set; }

    /// <summary>The user clicked the expander glyph of a tree row (or double-clicked the row).</summary>
    public event Action<int>? ExpanderClicked;

    public int SortColumn => _sortColumn;

    public bool SortDescending => _sortDescending;

    public int RowHeight { get; private set; }

    private int _rowPadding = 14;

    /// <summary>
    /// The room a row has besides its text, in pixels at 96 DPI (the height of a row is the height of the font and this). The default is the roomy row
    /// of every list of the app; a table that has to show many lines in a low window (the recording summary at 200 %) asks for less.
    /// </summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int RowPadding
    {
        get => _rowPadding;
        set
        {
            var padding = Math.Clamp(value, 4, 24);
            if (padding == _rowPadding)
            {
                return;
            }

            _rowPadding = padding;
            UpdateRowHeight();
            Invalidate();
        }
    }

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
            var wanted = Math.Max(ClientSize.Width - used - VerticalBarAllowance(), FillColumn >= 0 ? Math.Max(Scaled(140), HeaderNeed(fill)) : MinimumFillWidth(last));
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

    /// <summary>The width the columns have together: the client width less the vertical bar's (columns that are wider than this are clipped on the right, the horizontal bar being hidden).</summary>
    internal int ColumnRoom() => Math.Max(ClientSize.Width - VerticalBarAllowance(), 0);

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
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _rowHeight.Dispose();
            _bold?.Dispose();
            _underline?.Dispose();
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
        private const int WmMouseMove = 0x0200;
        private const int WmMouseLeave = 0x02A3;
        private const int HdmGetItemCount = 0x1200;
        private const int HdmGetItemRect = 0x1207;

        private bool _tracking;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WmPaint && owner._palette is { } palette)
            {
                Fill(palette);
            }
            else if (m.Msg == WmMouseMove)
            {
                if (!_tracking)
                {
                    // Ask for the message that says the pointer left the header, to put the tip away.
                    var track = new TrackMouseEventData { Size = System.Runtime.InteropServices.Marshal.SizeOf<TrackMouseEventData>(), Flags = 2, Window = Handle };
                    _tracking = TrackMouseEvent(ref track);
                }

                owner.OnHeaderMouseMove((short)(m.LParam.ToInt64() & 0xFFFF));
            }
            else if (m.Msg == WmMouseLeave)
            {
                _tracking = false;
                owner.OnHeaderMouseLeave();
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct TrackMouseEventData
        {
            public int Size;
            public int Flags;
            public IntPtr Window;
            public int HoverTime;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool TrackMouseEvent(ref TrackMouseEventData data);

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

    /// <summary>The column whose header is under this x (in the list's client pixels), or -1.</summary>
    internal int HeaderColumnAt(int x)
    {
        var left = 0;
        for (var i = 0; i < Columns.Count; i++)
        {
            left += Columns[i].Width;
            if (x < left)
            {
                return x >= 0 ? i : -1;
            }
        }

        return -1;
    }

    private void OnHeaderMouseMove(int x)
    {
        var column = HeaderColumnAt(x);
        if (column == _headerTipColumn)
        {
            return;
        }

        _headerTipColumn = column;
        var text = column >= 0 ? HeaderTip?.Invoke(column) : null;
        ShownHeaderTip = string.IsNullOrEmpty(text) ? null : text;
        if (ShownHeaderTip is null)
        {
            _tip.Hide(this);
            return;
        }

        _tip.Show(ShownHeaderTip, this, Math.Max(x - Scaled(8), 0), HeaderHeight + Scaled(4), 9000);
    }

    private void OnHeaderMouseLeave()
    {
        _headerTipColumn = -2;
        ShownHeaderTip = null;
        _tip.Hide(this);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_headerTipColumn != -2)
        {
            OnHeaderMouseLeave();
        }

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

        if (m.Msg == WmWindowPosChanging && ScrollsBackWhenWhole)
        {
            BeforeResize(m.LParam);
        }

        base.WndProc(ref m);
        if (m.Msg == WmPaint)
        {
            BlankBelowRows();
            ReportPaint();
        }

        // The list raises its own horizontal bar at moments of its choosing (after an item count or a size change): checked after every message.
        HideStaleHorizontalScroll();
    }

    private void ReportPaint()
    {
        if (_paintedCells is not { Count: > 0 } cells)
        {
            return;
        }

        var drawn = cells.ToArray();
        cells.Clear();
        PaintObserver?.Invoke(drawn);
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

        if (PaintObserver is not null)
        {
            (_paintedCells ??= []).Add((e.ItemIndex, e.ColumnIndex));
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

        var width = cell.Span && e.ColumnIndex == 0 ? SpanWidth() : Columns[e.ColumnIndex].Width;
        var pad = Scaled(8);
        var bounds = new Rectangle(left + pad, row.Top, Math.Max(width - (pad * 2), 0), row.Height);
        if (cell.Bar > 0 && !header)
        {
            DrawBar(e.Graphics, palette, new Rectangle(left, row.Top, width, row.Height), cell.Bar, selected);
        }

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
            if (cell.IconSlot)
            {
                var side = Scaled(16);
                if (cell.Icon is { } icon)
                {
                    var mode = e.Graphics.InterpolationMode;
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(icon, new Rectangle(bounds.Left + shift, row.Top + ((row.Height - side) / 2), side, side));
                    e.Graphics.InterpolationMode = mode;
                }

                shift += side + Scaled(6);
            }

            bounds = new Rectangle(bounds.Left + shift, bounds.Top, Math.Max(bounds.Width - shift, 0), bounds.Height);
        }

        var tone = cell.Link ? CellTone.Accent : cell.Tone;
        var color = ToneColor(palette, tone, selected);
        var alignment = Columns[e.ColumnIndex].TextAlign switch
        {
            HorizontalAlignment.Right => TextFormatFlags.Right,
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left,
        };
        var font = cell.Link ? UnderlineFont : (cell.Bold || header) ? BoldFont : Font;
        TextRenderer.DrawText(
            e.Graphics,
            cell.Text,
            font,
            bounds,
            color,
            alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    /// <summary>The load cue of a cell: a translucent accent bar from the left edge of the cell, drawn under the text.</summary>
    private void DrawBar(Graphics graphics, Palette palette, Rectangle cell, double fraction, bool selected)
    {
        var inset = Scaled(2);
        var room = cell.Width - (inset * 2);
        var length = (int)Math.Round(room * Math.Clamp(fraction, 0, 1));
        if (length < 1 || cell.Height <= inset * 2)
        {
            return;
        }

        // A quiet tint: the number on top has to stay as readable as without it, in both themes and on the selected row.
        // A blue on a dark surface needs a little more opacity than on a light one to be seen at all.
        var dark = palette.Window.GetBrightness() < 0.5f;
        var alpha = selected ? BarAlphaSelected : dark ? BarAlphaDark : BarAlpha;
        using var brush = new SolidBrush(Color.FromArgb(alpha, palette.Accent));
        graphics.FillRectangle(brush, cell.Left + inset, cell.Top + inset, length, cell.Height - (inset * 2));
    }

    private const int BarAlpha = 48;
    private const int BarAlphaDark = 72;
    private const int BarAlphaSelected = 64;

    private Font? _underline;

    private Font UnderlineFont
    {
        get
        {
            if (_underline is null || _underline.Size != Font.Size || _underline.Name != Font.Name)
            {
                _underline?.Dispose();
                _underline = new Font(Font, FontStyle.Underline);
            }

            return _underline;
        }
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

    /// <summary>A length written in logical pixels, in pixels for the DPI the window is at now (the font size does not enter: it is the room the grid keeps around a text).</summary>
    internal int Scaled(int logical) => (int)Math.Round(logical * CurrentDpi / 96.0);

    /// <summary>
    /// The columns were brought to a new DPI or font size (after <see cref="SyncDpi"/> scaled them by the ratio). A grid that sizes a column by
    /// what its text needs measures again here, in pixels of the scale it is at now.
    /// </summary>
    protected virtual void OnColumnsRescaled() => ColumnsRescaled?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised after the columns were brought to a new DPI or font size (see <see cref="OnColumnsRescaled"/>).</summary>
    internal event EventHandler? ColumnsRescaled;

    /// <summary>A column width written in logical pixels at the design font size, in pixels for this table's DPI and font.</summary>
    public int ColumnWidth(int logical) => (int)Math.Round(logical * CurrentDpi / 96.0 * Font.SizeInPoints / DesignPoints);

    /// <summary>The reverse of <see cref="ColumnWidth"/>: what a width on screen is worth in the units the code and the settings file use.</summary>
    public int ToLogicalWidth(int device) => (int)Math.Round(device * 96.0 / Math.Max(CurrentDpi, 96) * DesignPoints / Font.SizeInPoints);

    /// <summary>After a DPI change the column widths (and row height) are brought to the new scale.</summary>
    private void SyncDpi()
    {
        var dpi = CurrentDpi;
        var points = Font.SizeInPoints;
        if (dpi == _columnDpi && Math.Abs(points - _columnPoints) < 0.01f)
        {
            return;
        }

        var factor = (double)dpi / _columnDpi * (points / _columnPoints);
        _columnDpi = dpi;
        _columnPoints = points;
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
        OnColumnsRescaled();
        FitLastColumn();
        Invalidate();
    }

    private void UpdateRowHeight()
    {
        RowHeight = Math.Max(Font.Height + Scaled(_rowPadding), Scaled(_rowPadding * 2));
        // The row height of a details ListView follows its image list: a 1-pixel-wide image of the wanted height sets it.
        if (_rowHeight.ImageSize.Height != RowHeight)
        {
            _rowHeight.Images.Clear();
            _rowHeight.ImageSize = new Size(1, Math.Min(RowHeight, 256));
        }
    }

    /// <summary>
    /// True when the text of a cell does not fit its column, so the grid draws it with an ellipsis. It measures what <c>DrawCell</c> draws (the
    /// font of the cell, the padding on both sides and, in the first column, the indent and the glyph slot), so the smoke run and the tests ask
    /// the grid itself and never a copy of its arithmetic.
    /// </summary>
    internal bool IsCut(int row, int column) =>
        column >= 0 && column < Columns.Count && NeededWidth(row, column) > CellWidth(row, column);

    /// <summary>The width a cell has: its column's, or every column's together for a first-column cell that spans them.</summary>
    internal int CellWidth(int row, int column) =>
        column == 0 && Cells is { } cells && row >= 0 && row < VirtualListSize && cells(row, 0).Span ? SpanWidth() : Columns[column].Width;

    private int SpanWidth()
    {
        var total = 0;
        foreach (ColumnHeader column in Columns)
        {
            total += column.Width;
        }

        return total;
    }

    /// <summary>
    /// The width a text can use in a cell of this column before it has to wrap: the column (all of them for a cell that spans), less the padding on
    /// both sides and, in the first column, the indent and the glyph slot. The other side of <see cref="NeededWidth"/>.
    /// </summary>
    internal int TextRoom(int column, int indent, bool span)
    {
        var room = (span && column == 0 ? SpanWidth() : Columns[column].Width) - (Scaled(8) * 2);
        return column == 0 ? room - ((indent + 1) * Scaled(16)) : room;
    }

    /// <summary>The width a text takes drawn on one line in the font of a cell (bold or not).</summary>
    internal int TextWidth(string text, bool bold) =>
        text.Length == 0
            ? 0
            : TextRenderer.MeasureText(text, bold ? BoldFont : Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;

    /// <summary>
    /// The width the column has to be for the text of this cell to be drawn whole: the text, the padding on both sides and, in the first column,
    /// the indent and the glyph slot (0 for a cell with no text). Measured in pixels of the scale the grid is at now.
    /// </summary>
    internal int NeededWidth(int row, int column)
    {
        if (Cells is not { } cells || row < 0 || row >= VirtualListSize || column < 0 || column >= Columns.Count)
        {
            return 0;
        }

        var cell = cells(row, column);
        if (cell.Text.Length == 0)
        {
            return 0;
        }

        var header = RowStyles?.Invoke(row) == RowStyle.Header;
        var font = cell.Link ? UnderlineFont : (cell.Bold || header) ? BoldFont : Font;
        var around = Scaled(8) * 2;
        if (column == 0)
        {
            around += (cell.Indent + 1) * Scaled(16);
            if (cell.IconSlot)
            {
                around += Scaled(16) + Scaled(6);
            }
        }

        return around + TextRenderer.MeasureText(
            cell.Text,
            font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
    }

    /// <summary>The widest <see cref="NeededWidth"/> of the column over every row now on show.</summary>
    internal int WidestNeed(int column)
    {
        var widest = 0;
        for (var row = 0; row < VirtualListSize; row++)
        {
            widest = Math.Max(widest, NeededWidth(row, column));
        }

        return widest;
    }

    /// <summary>
    /// The width the header of the column needs to be read whole: its text, the padding on both sides and, for the sorted column, the room of
    /// the arrow (<c>OnDrawColumnHeader</c> draws exactly that). Measured at the scale the grid is at now. It is never less than what the smoke run asks of
    /// every list header (the text as the system measures it, with its own padding, and 14 px): at the default text size, one point above the system's,
    /// the drawn text needed 7 px less than that and a header was 1 px short of the rule.
    /// </summary>
    internal int HeaderNeed(int column)
    {
        if (column < 0 || column >= Columns.Count)
        {
            return 0;
        }

        var header = Columns[column];
        var arrow = column == _sortColumn && header.TextAlign != HorizontalAlignment.Right ? Scaled(14) : 0;
        var drawn = (Scaled(8) * 2) + arrow + TextRenderer.MeasureText(
            header.Text,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        return Math.Max(drawn, header.Text.Length == 0 ? 0 : TextRenderer.MeasureText(header.Text, Font).Width + Scaled(14));
    }

    /// <summary>The width a column needs for its header and for every cell on show, whichever is wider (<see cref="WidestNeed"/> and <see cref="HeaderNeed"/>).</summary>
    internal int ContentNeed(int column) => Math.Max(WidestNeed(column), HeaderNeed(column));

    private const int ListTopIndexMessage = 0x1027;

    /// <summary>
    /// The height of the header strip as the list has it now, not a guess from the font: at 150% and with other fonts the header is not one row
    /// high. The rows start where the header ends, so the first row on show says it (a header item's own rectangle is a couple of pixels
    /// shorter than the strip, and a row would then be cut by that much); with no row on show, the header window's own height; before the
    /// header exists, the guess the grid draws with.
    /// </summary>
    internal int HeaderHeight
    {
        get
        {
            if (IsHandleCreated && Columns.Count > 0)
            {
                if (VirtualListSize > 0)
                {
                    var top = (int)SendMessage(Handle, ListTopIndexMessage, IntPtr.Zero, IntPtr.Zero);
                    if (top >= 0 && top < VirtualListSize && RowRect(top) is { } row && row.Top > 0)
                    {
                        return row.Top;
                    }
                }

                if (HeaderWindowHeight() is var window and > 0)
                {
                    return window;
                }
            }

            return HeaderBottom();
        }
    }

    /// <summary>The height of the header's own window (0 while there is none).</summary>
    private int HeaderWindowHeight()
    {
        if (_headerWindow is not { Handle: var header } || header == IntPtr.Zero)
        {
            return 0;
        }

        var rect = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try
        {
            return GetWindowRect(header, rect)
                ? Math.Max(System.Runtime.InteropServices.Marshal.ReadInt32(rect, 12) - System.Runtime.InteropServices.Marshal.ReadInt32(rect, 4), 0)
                : 0;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(rect);
        }
    }

    /// <summary>The index of the first row on show: 0 for a list that is not scrolled (and for one that has no window or no rows yet).</summary>
    internal int TopRow => IsHandleCreated && VirtualListSize > 0 ? Math.Max((int)SendMessage(Handle, ListTopIndexMessage, IntPtr.Zero, IntPtr.Zero), 0) : 0;

    /// <summary>
    /// True when a row is whole inside the rows on show: not scrolled out above or below, and not cut by the bottom edge. A list with no window, or a row it
    /// gives no rectangle for, answers false. The recording tab's list is a few rows high, and the smoke run asks this of the selected row.
    /// </summary>
    internal bool IsRowVisible(int row)
    {
        if (!IsHandleCreated || row < 0 || row >= VirtualListSize || RowRect(row) is not { } rect)
        {
            return false;
        }

        return rect.Top >= HeaderHeight - 1 && rect.Bottom <= ClientSize.Height + 1;
    }

    /// <summary>
    /// Scrolls the list so that the selected row is whole on show, when it is not (the list got shorter, or the row was chosen before the list had its height).
    /// Nothing is selected, no window, or the row is already on show: nothing happens (a list the person scrolled away from a row they are not on is left alone).
    /// </summary>
    internal void EnsureSelectedVisible()
    {
        if (IsHandleCreated && SelectedRow is >= 0 and var row && row < VirtualListSize && !IsRowVisible(row))
        {
            EnsureVisible(row);
        }
    }

    /// <summary>Puts a scrolled list back at its first row (a list with no window or no rows is already there).</summary>
    internal void ScrollToFirstRow()
    {
        if (IsHandleCreated && VirtualListSize > 0 && TopRow > 0)
        {
            EnsureVisible(0);
        }
    }

    /// <summary>
    /// Sets the number of rows of a list that asks to go back to its first row (<see cref="ScrollsBackWhenWhole"/>): when the new count fits in
    /// the window it goes back to its first row first, while the row it was scrolled to is still there. The native list keeps its place when
    /// the count shrinks, and then shows its first row far below the header with nothing to bring it back.
    /// </summary>
    internal void SetRowCount(int count)
    {
        if (ScrollsBackWhenWhole && count <= RowsThatFit(Height))
        {
            ScrollToFirstRow();
        }

        VirtualListSize = count;
    }

    /// <summary>
    /// For a list that is made taller than its rows: it goes back to its first row before it grows. The native list keeps the place it was
    /// scrolled to when it grows until every row fits, and then shows its first row far below the header with nothing to bring it back.
    /// Off unless a page asks for it (the lists of the recording tab do).
    /// </summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool ScrollsBackWhenWhole { get; set; }

    private void BeforeResize(IntPtr windowPos)
    {
        // WINDOWPOS: two handles, then x, y, cx, cy and the flags.
        var flags = System.Runtime.InteropServices.Marshal.ReadInt32(windowPos, (2 * IntPtr.Size) + 16);
        if ((flags & SwpNoSize) != 0 || !IsHandleCreated)
        {
            return;
        }

        var height = System.Runtime.InteropServices.Marshal.ReadInt32(windowPos, (2 * IntPtr.Size) + 12);
        if (height > 0 && RowsThatFit(height) >= VirtualListSize)
        {
            ScrollToFirstRow();
        }
    }

    /// <summary>
    /// What is wrong with the rows as the native list lays them out now, for a list that is meant to end on a whole row under its header; null
    /// when they are whole. The first row must start where the header's own window ends (a gap above it is how a list that has lost its place
    /// looks), the last row on show must end at the bottom of the client area (of a list that holds as many rows as it has room for) and no part of the next one may show under it. The smoke run asks
    /// this of the lists on the real, shown windows; <paramref name="facts"/> holds the numbers either way.
    /// </summary>
    internal string? WholeRowsProblem(out string facts)
    {
        facts = "no window or no rows";
        if (!IsHandleCreated || VirtualListSize == 0)
        {
            return "the list has no window or no rows to look at";
        }

        var header = HeaderWindowHeight();
        var fit = RowsThatFit(Height);

        // Every row fits and yet the list is scrolled: a list that kept the place it had for a longer list. The check below starts from the top row on show and would take
        // that for the first row, however many rows are out of sight above it.
        if (ScrolledWithRoomProblem(TopRow, VirtualListSize, fit) is { } scrolled)
        {
            facts = $"top row {TopRow}, {VirtualListSize} rows, room for {fit}";
            return scrolled;
        }

        // A list scrolled to show a row further down starts at its top row, not at the first one (a list that holds more rows than it has room for).
        var top = Math.Min(TopRow, VirtualListSize - 1);
        var left = VirtualListSize - top;
        var shown = Math.Min(left, fit);
        if (header <= 0 || shown <= 0 || RowRect(top) is not { } first || RowRect(top + shown - 1) is not { } last)
        {
            facts = $"header {header} px, {shown} rows on show";
            return "the list gives no rectangle for its header or its rows";
        }

        var client = ClientSize.Height;
        var next = shown < left ? RowRect(top + shown) : null;
        facts = $"header {header} px, first row on show ({top}) at {first.Top} px, row {first.Height} px, {shown} of {left} rows on show, last row ends at {last.Bottom} px of {client} px" + (next is { } n ? $", next row at {n.Top} px" : string.Empty);
        if (Math.Abs(first.Top - header) > 3)
        {
            return $"the first row starts at {first.Top} px where the header ends at {header} px";
        }

        // A list with fewer rows than it has room for ends where its rows end; a full one ends at the bottom.
        if (last.Bottom > client + 1 || (left >= fit && last.Bottom < client - 3))
        {
            return $"the last row on show ends at {last.Bottom} px in a client area {client} px high";
        }

        return next is { } below && below.Top < client - 1 ? $"part of the next row shows under the last (it starts at {below.Top} px of {client} px)" : null;
    }

    /// <summary>The words for a list that is scrolled (its top row on show is <paramref name="topRow"/>) although all its <paramref name="rows"/> rows fit in the room for <paramref name="fit"/>; null when it is not.</summary>
    internal static string? ScrolledWithRoomProblem(int topRow, int rows, int fit) =>
        topRow > 0 && rows <= fit ? $"the list is scrolled to row {topRow} although all {rows} rows fit" : null;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, IntPtr rect);

    /// <summary>The rectangle of a row as the native list has it now; null when it answers "none" (no handle yet, or a handle not yet given its rows).</summary>
    internal Rectangle? RowRect(int row)
    {
        var rect = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try
        {
            // The first int is the portion asked for (0: the whole item); the answer is written over it.
            System.Runtime.InteropServices.Marshal.WriteInt32(rect, 0, 0);
            if (SendMessage(Handle, ListItemRectMessage, (IntPtr)row, rect) == IntPtr.Zero)
            {
                return null;
            }

            var left = System.Runtime.InteropServices.Marshal.ReadInt32(rect, 0);
            var top = System.Runtime.InteropServices.Marshal.ReadInt32(rect, 4);
            var right = System.Runtime.InteropServices.Marshal.ReadInt32(rect, 8);
            var bottom = System.Runtime.InteropServices.Marshal.ReadInt32(rect, 12);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(rect);
        }
    }

    private const int ListItemRectMessage = 0x100E;

    /// <summary>
    /// The height of one row as the control has it (the first row's rectangle), or <see cref="RowHeight"/> while there is no row to ask (no handle, no
    /// row, or a handle that has just been made and has not been given its rows yet: the native list answers "none" then, where
    /// <see cref="ListView.GetItemRect(int, ItemBoundsPortion)"/> would throw out of the window procedure).
    /// </summary>
    internal int MeasuredRowHeight
    {
        get
        {
            if (IsHandleCreated && VirtualListSize > 0 && RowRect(0) is { Height: > 0 } row)
            {
                return row.Height;
            }

            return Math.Max(RowHeight, 1);
        }
    }

    /// <summary>The border and anything else between the control's height and its client area.</summary>
    private int FrameHeight => Math.Max(Height - ClientSize.Height, 0);

    /// <summary>The control height that shows the header, <paramref name="rows"/> whole rows and the border, and no part of another row.</summary>
    internal int HeightForRows(int rows) => FrameHeight + HeaderHeight + (Math.Max(rows, 0) * MeasuredRowHeight);

    /// <summary>How many whole rows a control of this height shows below its header (the other side of <see cref="HeightForRows"/>).</summary>
    internal int RowsThatFit(int height) => Math.Max((height - FrameHeight - HeaderHeight) / MeasuredRowHeight, 0);

    /// <summary>How far the dim text of a selected row goes toward the selection's own text colour (the rest is the selection's background).</summary>
    internal const double SelectedDimAmount = 0.75;

    /// <summary>
    /// The colour of a dim cell (a detail, an evidence line) on a selected row: mostly the selection's text colour, so it stays readable on the
    /// selection's background (a 25% blend, as it was, came out at about 1.6:1 in the light theme). Every grid draws it.
    /// </summary>
    internal static Color SelectedDim(Palette palette) => Blend(palette.SelectionText, palette.Selection, SelectedDimAmount);

    /// <summary>How much of its own colour a green, amber or red cell keeps on a selected row (the rest is the selection's text colour).</summary>
    internal const double SelectedToneAmount = 0.7;

    /// <summary>
    /// The colour a cell of a tone is drawn in, on a plain row or on the selected one. A green, amber or red cell on the selected row is
    /// blended toward the selection's text colour, as the dim one is, so each stays readable on the selection's background in both themes
    /// (the test pins the contrast) while the three still read as three colours.
    /// </summary>
    internal static Color ToneColor(Palette palette, CellTone tone, bool selected) => tone switch
    {
        CellTone.Dim => selected ? SelectedDim(palette) : palette.TextDim,
        CellTone.Accent => selected ? palette.SelectionText : palette.Accent,
        CellTone.Good => selected ? SelectedTone(palette, palette.Good) : palette.Good,
        CellTone.Warning => selected ? SelectedTone(palette, palette.Warning) : palette.Warning,
        CellTone.Danger => selected ? SelectedTone(palette, palette.Danger) : palette.Danger,
        _ => selected ? palette.SelectionText : palette.Text,
    };

    internal static Color SelectedTone(Palette palette, Color tone) => Blend(tone, palette.SelectionText, SelectedToneAmount);

    private static Color Blend(Color a, Color b, double amountOfA) =>
        Color.FromArgb(
            (int)((a.R * amountOfA) + (b.R * (1 - amountOfA))),
            (int)((a.G * amountOfA) + (b.G * (1 - amountOfA))),
            (int)((a.B * amountOfA) + (b.B * (1 - amountOfA))));
}
