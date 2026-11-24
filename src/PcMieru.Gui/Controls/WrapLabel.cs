namespace PcMieru.Gui;

/// <summary>
/// A line of the page that wraps instead of ending in "…": docked to the top (or the bottom) of its parent, it is as tall as its text needs
/// at the width it has, and takes that height again when the width, the text or the font changes. Nothing is cut and nothing needs a tooltip to
/// be read. With no text it takes no room at all.
/// </summary>
internal class WrapLabel : ToneLabel
{
    private int _heightFor = -1;
    private int _lastHeight = -1;

    public WrapLabel()
    {
        AutoSize = false;
        AutoEllipsis = false;
        Dock = DockStyle.Top;
    }

    /// <summary>The height this label would take at a width (its text wrapped there, with its padding). 0 for no text.</summary>
    public int HeightAt(int width)
    {
        if (Text.Length == 0 || width <= 0)
        {
            return 0;
        }

        return Math.Max(GetPreferredSize(new Size(width, 0)).Height, Font.Height + Padding.Vertical);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Fit();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Fit();
    }

    protected override void OnPaddingChanged(EventArgs e)
    {
        base.OnPaddingChanged(e);
        Fit();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Fit();
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        Fit();
    }

    private void Fit()
    {
        if (Width <= 0 || IsDisposed)
        {
            return;
        }

        var wanted = HeightAt(Width);
        if (wanted == Height)
        {
            return;
        }

        // A height that follows the width only (never the other way round), so setting it cannot start a loop.
        if (_heightFor == Width && wanted == _lastHeight)
        {
            return;
        }

        _heightFor = Width;
        _lastHeight = wanted;
        Height = wanted;
    }
}
