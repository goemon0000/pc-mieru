namespace PcMieru.Gui;

/// <summary>
/// The strip that shows how far a load is. It is drawn from the palette (a native progress bar ignores the dark theme), carries no text of
/// its own (the elapsed time and the rate are labels next to it) and does nothing but paint: a value is set, and one repaint follows.
/// A null fraction means "no target yet": the track is drawn empty.
/// </summary>
internal sealed class CheckProgressBar : Control, IThemed
{
    private Palette? _palette;
    private double? _fraction;

    public CheckProgressBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
        Height = 12;
    }

    /// <summary>The share that is done (0 to 1), or null for an empty track.</summary>
    public double? Fraction => _fraction;

    public void SetFraction(double? fraction)
    {
        var clamped = fraction is { } value && !double.IsNaN(value) ? Math.Clamp(value, 0, 1) : (double?)null;
        if (_fraction == clamped)
        {
            return;
        }

        _fraction = clamped;
        Invalidate();
    }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var palette = _palette;
        var back = palette?.Window ?? SystemColors.Control;
        var track = palette?.Surface ?? SystemColors.Window;
        var border = palette?.Border ?? SystemColors.ControlDark;
        var fill = palette?.Accent ?? SystemColors.Highlight;
        e.Graphics.Clear(back);
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using (var brush = new SolidBrush(track))
        {
            e.Graphics.FillRectangle(brush, bounds);
        }

        if (_fraction is { } share && share > 0)
        {
            var width = (int)Math.Round((bounds.Width - 1) * share);
            if (width > 0)
            {
                using var brush = new SolidBrush(fill);
                e.Graphics.FillRectangle(brush, new Rectangle(1, 1, width, Math.Max(1, bounds.Height - 1)));
            }
        }

        using var pen = new Pen(border);
        e.Graphics.DrawRectangle(pen, bounds);
    }
}
