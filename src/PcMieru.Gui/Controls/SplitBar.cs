namespace PcMieru.Gui;

/// <summary>
/// The bar between two areas stacked one on the other (the list of recordings and the summary under it): the person drags it up or down, or
/// presses Up and Down on it, and it says by how much (<see cref="Moved"/>); what is above it and below it is the page's to resize. It only
/// draws a line and a grip in the palette's colours; nothing is stored (the page keeps the share for the session).
/// </summary>
internal sealed class SplitBar : Control, IThemed
{
    private Palette? _palette;
    private bool _dragging;
    private int _grabY;

    public SplitBar()
    {
        TabStop = true;
        Dock = DockStyle.Top;
        Cursor = Cursors.SizeNS;
        AccessibleRole = AccessibleRole.Grip;
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>The bar was moved by this many pixels (negative: up). Raised while it is dragged, and for each press of an arrow key (<see cref="KeyStep"/>).</summary>
    public event Action<int>? Moved;

    /// <summary>How far an arrow key moves the bar (a row of the list above it: the page sets it).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int KeyStep { get; set; } = 24;

    public bool IsDragging => _dragging;

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        Invalidate();
    }

    /// <summary>Moves the bar as the pointer or an arrow key would (a test calls it; so does a key press).</summary>
    internal void MoveBy(int delta)
    {
        if (delta != 0)
        {
            Moved?.Invoke(delta);
        }
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Up)
        {
            MoveBy(-KeyStep);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Down)
        {
            MoveBy(KeyStep);
            e.Handled = true;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            _dragging = true;
            _grabY = e.Y;
            Capture = true;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            // The bar moves with the list above it, so the pointer's place in the bar stays where it was grabbed: the difference is what is left to move.
            MoveBy(e.Y - _grabY);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            Invalidate();
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

        using var back = new SolidBrush(palette.Window);
        e.Graphics.FillRectangle(back, ClientRectangle);
        var middle = Height / 2;
        var line = Focused || _dragging ? palette.Accent : palette.TextDim;
        using var pen = new Pen(line, Math.Max(1, LogicalToDeviceUnits(1)));
        e.Graphics.DrawLine(pen, 0, middle, Width, middle);

        // A grip in the middle: five dots, so the bar looks like something to take hold of.
        using var dot = new SolidBrush(line);
        var size = Math.Max(2, LogicalToDeviceUnits(3));
        var gap = size * 2;
        var left = (Width / 2) - (((5 * size) + (4 * (gap - size))) / 2);
        for (var i = 0; i < 5; i++)
        {
            e.Graphics.FillRectangle(dot, left + (i * gap), middle - (size / 2), size, size);
        }
    }
}
