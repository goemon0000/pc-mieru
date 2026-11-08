namespace PcMieru.Gui;

/// <summary>One line of a <see cref="WrapListBox"/>: a short tag ("[事実]"), the text, and whether the tag is a firm statement.</summary>
internal readonly record struct WrapItem(string Tag, string Text, bool Firm)
{
    public override string ToString() => (Tag.Length > 0 ? Tag + " " : string.Empty) + Text;
}

/// <summary>A list whose rows wrap to the width they are given. A ListBox underneath, so the arrow keys and screen readers work.</summary>
internal sealed class WrapListBox : ListBox, IThemed
{
    private Palette? _palette;
    private Font? _bold;

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
        var x = e.Bounds.Left + pad;
        var textColor = selected ? palette.SelectionText : palette.Text;
        var flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;
        var top = e.Bounds.Top + LogicalToDeviceUnits(6);
        if (item.Tag.Length > 0)
        {
            var tagWidth = TagWidth(e.Graphics, item.Tag);
            TextRenderer.DrawText(e.Graphics, item.Tag, Bold, new Rectangle(x, top, tagWidth, Font.Height + 2), item.Firm ? palette.Accent : palette.TextDim, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            x += tagWidth + LogicalToDeviceUnits(8);
        }

        TextRenderer.DrawText(e.Graphics, item.Text, Font, new Rectangle(x, top, Math.Max(e.Bounds.Right - pad - x, 1), e.Bounds.Height), textColor, flags);
        using var line = new Pen(palette.Border);
        e.Graphics.DrawLine(line, e.Bounds.Left + pad, e.Bounds.Bottom - 1, e.Bounds.Right - pad, e.Bounds.Bottom - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bold?.Dispose();
        }

        base.Dispose(disposing);
    }

    private Font Bold => _bold != null && _bold.Name == Font.Name && Math.Abs(_bold.Size - Font.Size) < 0.01f
        ? _bold
        : _bold = new Font(Font, FontStyle.Bold);

    private int TagWidth(Graphics g, string tag) =>
        TextRenderer.MeasureText(g, tag, Bold, new Size(int.MaxValue, Font.Height), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;

    private int MeasureRow(int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            return Font.Height;
        }

        var item = (WrapItem)Items[index];
        var pad = LogicalToDeviceUnits(10);
        using var g = CreateGraphics();
        var tag = item.Tag.Length > 0 ? TagWidth(g, item.Tag) + LogicalToDeviceUnits(8) : 0;
        var width = Math.Max(ClientSize.Width - (pad * 2) - tag, LogicalToDeviceUnits(80));
        var size = TextRenderer.MeasureText(g, item.Text, Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        return size.Height + LogicalToDeviceUnits(14);
    }
}
