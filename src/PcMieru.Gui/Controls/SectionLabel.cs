namespace PcMieru.Gui;

/// <summary>A small heading above a block of a page: the page font, a little larger, bold. Keeps its font in step with the page's.</summary>
internal sealed class SectionLabel : Label
{
    private Font? _own;

    public SectionLabel()
    {
        AutoSize = true;
        UseMnemonic = false;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (ReferenceEquals(Font, _own))
        {
            return;
        }

        var previous = _own;
        _own = new Font(Font.FontFamily, Font.Size * 1.1f, FontStyle.Bold, GraphicsUnit.Point);
        // Setting Font raises this method again; the check above stops it there.
        Font = _own;
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _own?.Dispose();
        }

        base.Dispose(disposing);
    }
}
