namespace PcMieru.Gui;

/// <summary>
/// One entry of a <see cref="WrapListBox"/>: a short tag ("[事実]"), the text, and whether the tag is a firm statement.
/// An entry may also carry a headline (the short conclusion, bold, above the text), and a link line below the text that
/// runs <see cref="Open"/> (the Overview's "see details"); without them it is the plain tag-and-text row.
/// </summary>
internal readonly record struct WrapItem(string Tag, string Text, bool Firm, string Headline = "", string LinkText = "", Action? Open = null)
{
    public override string ToString() => string.Join(" ", new[] { Tag, Headline, Text, LinkText }.Where(s => s.Length > 0));
}

/// <summary>A list whose rows wrap to the width they are given. A ListBox underneath, so the arrow keys and screen readers work.</summary>
internal sealed class WrapListBox : ListBox, IThemed
{
    private const int TopPad = 6;
    private const int BottomPad = 8;
    private const int LineGap = 2;
    private const int LinkGap = 4;

    private Palette? _palette;
    private Font? _bold;
    private Font? _underline;

    public WrapListBox()
    {
        DrawMode = DrawMode.OwnerDrawVariable;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        SelectionMode = SelectionMode.One;
        TabStop = true;
    }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        BackColor = palette.Surface;
        Invalidate();
    }

    public void SetItems(IReadOnlyList<WrapItem> items)
    {
        BeginUpdate();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        EndUpdate();
    }

    /// <summary>Total height the current rows need at the current width (so a page can size around it).</summary>
    public int ContentHeight()
    {
        var total = 0;
        for (var i = 0; i < Items.Count; i++)
        {
            total += MeasureRow(i);
        }

        return total;
    }

    /// <summary>Where the link line of a row is, in the control's own coordinates (empty when the row has none).</summary>
    public Rectangle LinkBounds(int index)
    {
        if (index < 0 || index >= Items.Count || ((WrapItem)Items[index]).LinkText.Length == 0)
        {
            return Rectangle.Empty;
        }

        var bounds = GetItemRectangle(index);
        using var g = CreateGraphics();
        var row = RowFor(g, (WrapItem)Items[index], bounds.Width);
        return new Rectangle(bounds.Left + row.TextLeft, bounds.Top + row.LinkTop, row.LinkWidth, row.LinkHeight);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Items.Count > 0 && IsHandleCreated)
        {
            RefreshItems();
        }
    }

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        base.OnMeasureItem(e);
        e.ItemHeight = MeasureRow(e.Index);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var palette = _palette;
        if (e.Index < 0 || palette is null)
        {
            return;
        }

        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? palette.Selection : palette.Surface))
        {
            e.Graphics.FillRectangle(back, e.Bounds);
        }

        var item = (WrapItem)Items[e.Index];
        var pad = LogicalToDeviceUnits(10);
        var row = RowFor(e.Graphics, item, e.Bounds.Width);
        var textColor = selected ? palette.SelectionText : palette.Text;
        var wrap = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;
        var x = e.Bounds.Left + row.TextLeft;
        var width = Math.Max(e.Bounds.Width - row.TextLeft - pad, 1);
        if (item.Tag.Length > 0)
        {
            TextRenderer.DrawText(e.Graphics, item.Tag, Bold, new Rectangle(e.Bounds.Left + pad, e.Bounds.Top + row.TextTop, row.TagWidth, Font.Height + 2), item.Firm ? palette.Accent : palette.TextDim, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        if (item.Headline.Length > 0)
        {
            TextRenderer.DrawText(e.Graphics, item.Headline, Bold, new Rectangle(x, e.Bounds.Top + row.TextTop, width, row.HeadlineHeight), textColor, wrap);
        }

        TextRenderer.DrawText(e.Graphics, item.Text, Font, new Rectangle(x, e.Bounds.Top + row.BodyTop, width, row.BodyHeight), textColor, wrap);
        if (item.LinkText.Length > 0)
        {
            // Underlined, and in the accent colour: the one blue text in the window that can be clicked.
            TextRenderer.DrawText(e.Graphics, item.LinkText, Underline, new Rectangle(x, e.Bounds.Top + row.LinkTop, row.LinkWidth, row.LinkHeight), selected ? palette.SelectionText : palette.Accent, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        using var line = new Pen(palette.Border);
        e.Graphics.DrawLine(line, e.Bounds.Left + pad, e.Bounds.Bottom - 1, e.Bounds.Right - pad, e.Bounds.Bottom - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = OverLink(e.Location) is not null ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Cursor = Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && OverLink(e.Location) is { } open)
        {
            open();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter && SelectedIndex >= 0 && ((WrapItem)Items[SelectedIndex]).Open is { } open)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            open();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bold?.Dispose();
            _underline?.Dispose();
        }

        base.Dispose(disposing);
    }

    private Font Bold => _bold != null && _bold.Name == Font.Name && Math.Abs(_bold.Size - Font.Size) < 0.01f
        ? _bold
        : _bold = new Font(Font, FontStyle.Bold);

    private Font Underline => _underline != null && _underline.Name == Font.Name && Math.Abs(_underline.Size - Font.Size) < 0.01f
        ? _underline
        : _underline = new Font(Font, FontStyle.Underline);

    /// <summary>The action of the link under a point, or null when the point is not on a link line.</summary>
    private Action? OverLink(Point point)
    {
        var index = IndexFromPoint(point);
        return index >= 0 && ((WrapItem)Items[index]).Open is { } open && LinkBounds(index).Contains(point) ? open : null;
    }

    private int TagWidth(Graphics g, string tag) =>
        TextRenderer.MeasureText(g, tag, Bold, new Size(int.MaxValue, Font.Height), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;

    private int MeasureRow(int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            return Font.Height;
        }

        using var g = CreateGraphics();
        return RowFor(g, (WrapItem)Items[index], ClientSize.Width).Height;
    }

    /// <summary>Everything a row needs to be measured, drawn and hit-tested: positions are relative to the top left of the row.</summary>
    private RowLayout RowFor(Graphics g, WrapItem item, int rowWidth)
    {
        var pad = LogicalToDeviceUnits(10);
        var tag = item.Tag.Length > 0 ? TagWidth(g, item.Tag) : 0;
        var textLeft = pad + (tag > 0 ? tag + LogicalToDeviceUnits(8) : 0);
        var width = Math.Max(rowWidth - textLeft - pad, LogicalToDeviceUnits(80));
        var measure = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var top = LogicalToDeviceUnits(TopPad);
        var headline = 0;
        if (item.Headline.Length > 0)
        {
            headline = TextRenderer.MeasureText(g, item.Headline, Bold, new Size(width, int.MaxValue), measure).Height;
        }

        var bodyTop = top + (headline > 0 ? headline + LogicalToDeviceUnits(LineGap) : 0);
        var body = TextRenderer.MeasureText(g, item.Text, Font, new Size(width, int.MaxValue), measure).Height;
        var linkTop = 0;
        var linkWidth = 0;
        var linkHeight = 0;
        var end = bodyTop + body;
        if (item.LinkText.Length > 0)
        {
            linkTop = end + LogicalToDeviceUnits(LinkGap);
            linkHeight = Font.Height;
            linkWidth = Math.Min(TextRenderer.MeasureText(g, item.LinkText, Underline, new Size(int.MaxValue, Font.Height), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + LogicalToDeviceUnits(2), width);
            end = linkTop + linkHeight;
        }

        return new RowLayout(tag, textLeft, top, headline, bodyTop, body, linkTop, linkWidth, linkHeight, end + LogicalToDeviceUnits(BottomPad));
    }

    private readonly record struct RowLayout(int TagWidth, int TextLeft, int TextTop, int HeadlineHeight, int BodyTop, int BodyHeight, int LinkTop, int LinkWidth, int LinkHeight, int Height);
}
