namespace PcMieru.Gui;

/// <summary>The left navigation. A ListBox, so the arrow keys, type-ahead and screen readers work without extra code.</summary>
internal sealed class NavListBox : ListBox, IThemed
{
    private Palette? _palette;

    public NavListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        Dock = DockStyle.Left;
        TabStop = true;
    }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        BackColor = palette.Window;
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ItemHeight = Math.Max(Font.Height + LogicalToDeviceUnits(14), LogicalToDeviceUnits(36));
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var palette = _palette;
        if (e.Index < 0 || palette is null)
        {
            return;
        }

        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? palette.Selection : palette.Window))
        {
            e.Graphics.FillRectangle(back, e.Bounds);
        }

        if (selected)
        {
            using var accent = new SolidBrush(palette.Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Y + LogicalToDeviceUnits(6), LogicalToDeviceUnits(3), e.Bounds.Height - LogicalToDeviceUnits(12));
        }

        var bounds = new Rectangle(e.Bounds.X + LogicalToDeviceUnits(14), e.Bounds.Y, e.Bounds.Width - LogicalToDeviceUnits(14), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            Items[e.Index].ToString(),
            Font,
            bounds,
            selected ? palette.SelectionText : palette.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if ((e.State & DrawItemState.Focus) != 0 && Focused)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, palette.Text, selected ? palette.Selection : palette.Window);
        }
    }
}
