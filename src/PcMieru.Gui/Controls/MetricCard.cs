using System.ComponentModel;

namespace PcMieru.Gui;

/// <summary>
/// One Overview card: a title, one large value, a line of context, an optional state word (text, not colour alone) and a
/// small history line. Keyboard focusable: Enter, Space or a click asks the page to open the matching detail view.
/// </summary>
internal sealed class MetricCard : Control, IThemed
{
    private Palette? _palette;
    private double[]? _values;
    private int _count;
    private Font? _big;
    private Font? _small;
    private bool _hover;
    private readonly ToolTip _tip = new() { ShowAlways = true };

    /// <summary>The context under the value: at most this many lines.</summary>
    private const int MaxSubLines = 2;

    public MetricCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
    }

    public event EventHandler? Activated;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Title { get; set; } = string.Empty;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string ValueText { get; set; } = Fmt.Dash;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string SubText { get; set; } = string.Empty;

    /// <summary>A word next to the value ("高め"). Shown in the accent colour when <see cref="High"/>.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string StateText { get; set; } = string.Empty;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool High { get; set; }

    /// <summary>The history line is drawn from 0 to this value (100 for percent).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal double HistoryMaximum { get; set; } = 100;

    /// <summary>Call after changing the texts. Skips the repaint when nothing the card shows has changed.</summary>
    public void Commit(string title, string value, string sub, string state, bool high)
    {
        var changed = Title != title || ValueText != value || SubText != sub || StateText != state || High != high;
        Title = title;
        ValueText = value;
        SubText = sub;
        StateText = state;
        High = high;
        if (changed)
        {
            var spoken = sub.Replace('\n', ' ');
            AccessibleName = title + ": " + value + (state.Length > 0 ? ", " + state : string.Empty);
            AccessibleDescription = spoken;
            _tip.SetToolTip(this, title + ": " + value + (spoken.Length > 0 ? Environment.NewLine + sub.Replace("\n", Environment.NewLine) : string.Empty));
            Invalidate();
        }
    }

    public void SetHistory(double[] values, int count)
    {
        _values = values;
        _count = count;
        Invalidate();
    }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            // A tip that is showing when the page is switched away would stay on screen over the next page.
            _tip.Hide(this);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        Activated?.Invoke(this, EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            Activated?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var palette = _palette;
        if (palette is null)
        {
            return;
        }

        var g = e.Graphics;
        using (var back = new SolidBrush(palette.Surface))
        {
            g.FillRectangle(back, 0, 0, Width, Height);
        }

        var pad = LogicalToDeviceUnits(14);
        var width = Math.Max(Width - (pad * 2), 0);
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var y = LogicalToDeviceUnits(10);
        var titleHeight = Font.Height + 2;
        TextRenderer.DrawText(g, Title, Font, new Rectangle(pad, y, width, titleHeight), palette.TextDim, flags);
        y += titleHeight + LogicalToDeviceUnits(2);

        var bigHeight = Big.Height + 2;
        var stateWidth = 0;
        if (StateText.Length > 0)
        {
            stateWidth = TextRenderer.MeasureText(g, StateText, Font, new Size(width, titleHeight), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + LogicalToDeviceUnits(4);
            TextRenderer.DrawText(
                g,
                StateText,
                Font,
                new Rectangle(pad + width - stateWidth, y, stateWidth, bigHeight),
                High ? palette.Accent : palette.TextDim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        TextRenderer.DrawText(g, ValueText, Big, new Rectangle(pad, y, Math.Max(width - stateWidth, 0), bigHeight), High ? palette.Accent : palette.Text, flags);
        y += bigHeight;

        if (SubText.Length > 0)
        {
            // Up to two lines, split at a line feed (spans: painting allocates nothing).
            var rest = SubText.AsSpan();
            for (var line = 0; line < MaxSubLines && !rest.IsEmpty; line++)
            {
                var cut = rest.IndexOf('\n');
                var text = cut < 0 || line == MaxSubLines - 1 ? rest : rest[..cut];
                TextRenderer.DrawText(g, text, Small, new Rectangle(pad, y, width, Small.Height + 2), palette.TextDim, flags);
                y += Small.Height + LogicalToDeviceUnits(2);
                rest = cut < 0 ? default : rest[(cut + 1)..];
            }

            y += LogicalToDeviceUnits(4);
        }

        var spark = new Rectangle(pad, y, width, Math.Max(Height - y - LogicalToDeviceUnits(10), 0));
        if (_values is { } values && _count > 1 && spark.Height >= LogicalToDeviceUnits(12))
        {
            GraphPainter.Series(g, spark, values, _count, 0, HistoryMaximum, SnapshotPump.HistoryLength, High ? palette.Accent : GraphControl.SeriesColor(palette, 0), Math.Max(1, LogicalToDeviceUnits(1)), fill: true);
        }

        using var border = new Pen(Focused ? palette.Accent : _hover ? palette.TextDim : palette.Border, Focused ? 2 : 1);
        g.DrawRectangle(border, Focused ? 1 : 0, Focused ? 1 : 0, Width - (Focused ? 3 : 1), Height - (Focused ? 3 : 1));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _big?.Dispose();
            _small?.Dispose();
            _tip.Dispose();
        }

        base.Dispose(disposing);
    }

    private Font Big => _big != null && _big.Name == Font.Name && Math.Abs(_big.Size - (Font.Size * 1.9f)) < 0.01f
        ? _big
        : _big = new Font(Font.FontFamily, Font.Size * 1.9f, FontStyle.Regular);

    private Font Small => _small != null && _small.Name == Font.Name && Math.Abs(_small.Size - (Font.Size * 0.92f)) < 0.01f
        ? _small
        : _small = new Font(Font.FontFamily, Font.Size * 0.92f);
}
