namespace PcMieru.Gui;

/// <summary>
/// A Label whose colour follows the palette and a tone (normal, grey for "not available", accent for "needs a look").
/// ThemeService paints children after their parent, so a plain Label would lose a grey set by hand on every theme change;
/// this one re-applies its own tone last.
/// </summary>
internal class ToneLabel : Label, IThemed
{
    private Palette? _palette;
    private CellTone _tone;

    public CellTone Tone => _tone;

    public void SetTone(CellTone tone)
    {
        if (_tone == tone)
        {
            return;
        }

        _tone = tone;
        Recolor();
    }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        Recolor();
    }

    /// <summary>The colour this label must have in a palette; the smoke run compares against it.</summary>
    public Color ExpectedColor(Palette palette) => _tone switch
    {
        CellTone.Dim => palette.TextDim,
        CellTone.Accent => palette.Accent,
        _ => palette.Text,
    };

    private void Recolor()
    {
        if (_palette is { } palette)
        {
            ForeColor = ExpectedColor(palette);
        }
    }
}
