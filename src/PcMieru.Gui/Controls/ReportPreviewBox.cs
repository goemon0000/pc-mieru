using System.Runtime.InteropServices;

namespace PcMieru.Gui;

/// <summary>
/// The diagnostic report drawn as a document (a title, headings, bullets, a table) instead of Markdown source. It is a
/// read-only rich text box that is filled from <see cref="ReportPreview"/>, in the colours of the theme: a change of theme
/// or text size draws it again from the same lines. It shows what Copy and Save give, in a readable form.
/// </summary>
internal sealed class ReportPreviewBox : RichTextBox, IThemed
{
    private const int WmSetRedraw = 0x000B;
    private const int MaxNameCharacters = 30;
    private const int EdgePx = 4;
    private const int GapPx = 18;
    private const int EmSetMargins = 0x00D3;
    private const int EcLeftRightMargin = 3;

    private IReadOnlyList<PreviewBlock> _blocks = [];
    private Palette? _palette;
    private bool _dirty;

    public ReportPreviewBox()
    {
        ReadOnly = true;
        DetectUrls = false;
        WordWrap = true;
        ScrollBars = RichTextBoxScrollBars.Vertical;
        HideSelection = false;
        BorderStyle = BorderStyle.None;
    }

    /// <summary>Draws the report that is written in <paramref name="markdown"/>.</summary>
    public void ShowMarkdown(string markdown)
    {
        _blocks = ReportPreview.Parse(markdown);
        Rebuild();
    }

    /// <summary>Draws a plain line of text (the hint before there is a report).</summary>
    public void ShowPlain(string text)
    {
        _blocks = text.Length == 0 ? [] : [new PreviewBlock(PreviewKind.Paragraph, [[new PreviewRun(text, PreviewStyle.Plain)]])];
        Rebuild();
    }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        Rebuild();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var side = LogicalToDeviceUnits(10);
        SendMessage(Handle, EmSetMargins, (IntPtr)EcLeftRightMargin, (IntPtr)((side << 16) | side));
        if (_dirty)
        {
            Rebuild();
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Rebuild();
    }

    private void Rebuild()
    {
        if (_palette is not { } palette || !IsHandleCreated)
        {
            _dirty = true;
            return;
        }

        _dirty = false;
        SendMessage(Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        try
        {
            Clear();
            BackColor = palette.Surface;
            ForeColor = palette.Text;
            var fonts = new Fonts(Font);
            try
            {
                var tabs = TableTabs(fonts);
                BulletIndent = LogicalToDeviceUnits(14);
                for (var i = 0; i < _blocks.Count; i++)
                {
                    Write(_blocks[i], palette, fonts, tabs);
                    if (i < _blocks.Count - 1)
                    {
                        // The end of the line takes the line's height too: an empty line is a small gap, not a full one.
                        Append("\n", _blocks[i].Kind == PreviewKind.Blank ? fonts.Gap : fonts.Body, palette.Text);
                    }
                }
            }
            finally
            {
                fonts.Dispose();
            }

            Select(0, 0);
            ScrollToCaret();
        }
        finally
        {
            SendMessage(Handle, WmSetRedraw, (IntPtr)1, IntPtr.Zero);
            Invalidate();
        }
    }

    private void Write(PreviewBlock block, Palette palette, Fonts fonts, int[] tabs)
    {
        var gap = LogicalToDeviceUnits(10);
        var nameLimit = tabs.Length > 0 ? tabs[0] - LogicalToDeviceUnits(EdgePx + GapPx) : 0;
        switch (block.Kind)
        {
            case PreviewKind.Title:
                Format(0, bullet: false, []);
                Runs(block.Runs, fonts.Title, fonts, palette.Text, palette);
                break;
            case PreviewKind.Heading:
                Format(0, bullet: false, []);
                Runs(block.Runs, fonts.Heading, fonts, palette.Accent, palette);
                break;
            case PreviewKind.Bullet:
                Format(gap / 2, bullet: true, []);
                Runs(block.Runs, fonts.Body, fonts, palette.Text, palette);
                break;
            case PreviewKind.SubBullet:
                Format(gap * 3, bullet: true, []);
                Runs(block.Runs, fonts.Body, fonts, palette.TextDim, palette);
                break;
            case PreviewKind.Quote:
                Format(gap, bullet: false, []);
                Runs(block.Runs, fonts.Quote, fonts, palette.TextDim, palette);
                break;
            case PreviewKind.TableHeader:
                Format(gap / 2, bullet: false, tabs);
                Cells(block, fonts.Strong, fonts, palette.TextDim, palette, nameLimit);
                break;
            case PreviewKind.TableRow:
                Format(gap / 2, bullet: false, tabs);
                Cells(block, fonts.Body, fonts, palette.Text, palette, nameLimit);
                break;
            case PreviewKind.Blank:
                Format(0, bullet: false, []);
                SelectionFont = fonts.Gap;
                break;
            default:
                Format(0, bullet: false, []);
                Runs(block.Runs, fonts.Body, fonts, palette.Text, palette);
                break;
        }
    }

    /// <summary>The paragraph settings of the line that is about to be written. Each line sets them all: a new paragraph keeps those of the one before.</summary>
    private void Format(int indent, bool bullet, int[] tabs)
    {
        SelectionStart = TextLength;
        SelectionLength = 0;
        SelectionBullet = bullet;
        SelectionIndent = indent;
        SelectionTabs = tabs;
    }

    private void Runs(IEnumerable<PreviewRun> runs, Font baseFont, Fonts fonts, Color color, Palette palette)
    {
        foreach (var run in runs)
        {
            switch (run.Style)
            {
                case PreviewStyle.Code:
                    Append(run.Text, fonts.Code, color);
                    break;
                case PreviewStyle.Tag:
                    Append(run.Text, fonts.Strong, palette.Accent);
                    break;
                default:
                    Append(run.Text, baseFont, color);
                    break;
            }
        }
    }

    private void Cells(PreviewBlock block, Font baseFont, Fonts fonts, Color color, Palette palette, int nameLimit)
    {
        for (var i = 0; i < block.Cells.Count; i++)
        {
            if (i > 0)
            {
                Append("\t", baseFont, color);
            }

            // A name longer than its column would push the next cell past its tab stop: it is cut with an ellipsis.
            var runs = i == 0 && nameLimit > 0 ? Truncated(block.Cells[i], fonts, nameLimit) : block.Cells[i];
            Runs(runs, baseFont, fonts, color, palette);
        }
    }

    private IReadOnlyList<PreviewRun> Truncated(IReadOnlyList<PreviewRun> runs, Fonts fonts, int width)
    {
        var text = PreviewBlock.Plain(runs);
        var font = runs.Count > 0 && runs[0].Style == PreviewStyle.Code ? fonts.Code : fonts.Body;
        if (width <= 0 || TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width <= width)
        {
            return runs;
        }

        var cut = text.Length;
        while (cut > 1 && TextRenderer.MeasureText(text[..cut] + "...", font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width > width)
        {
            cut--;
        }

        return [new PreviewRun(text[..cut] + "...", runs.Count > 0 ? runs[0].Style : PreviewStyle.Plain)];
    }

    private void Append(string text, Font font, Color color)
    {
        SelectionStart = TextLength;
        SelectionLength = 0;
        SelectionFont = font;
        SelectionColor = color;
        SelectedText = text;
    }

    /// <summary>
    /// Tab stops (left aligned: a rich text box has no other kind) for the columns of the table. The first column is as wide
    /// as its longest name up to <see cref="MaxNameCharacters"/> characters; every other one fits its header or its widest value.
    /// </summary>
    private int[] TableTabs(Fonts fonts)
    {
        var rows = _blocks.Where(b => b.Kind is PreviewKind.TableHeader or PreviewKind.TableRow).ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        var columns = rows.Max(r => r.Cells.Count);
        var gap = LogicalToDeviceUnits(GapPx);
        var tabs = new int[columns - 1];
        var edge = LogicalToDeviceUnits(EdgePx);
        for (var column = 0; column < columns - 1; column++)
        {
            var widest = 0;
            foreach (var row in rows)
            {
                if (column >= row.Cells.Count)
                {
                    continue;
                }

                var font = row.Kind == PreviewKind.TableHeader ? fonts.Strong : column == 0 ? fonts.Code : fonts.Body;
                var width = TextRenderer.MeasureText(PreviewBlock.Plain(row.Cells[column]), font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                widest = Math.Max(widest, width);
            }

            if (column == 0)
            {
                widest = Math.Min(widest, TextRenderer.MeasureText(new string('0', MaxNameCharacters), fonts.Code, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width);
            }

            edge += widest + gap;
            tabs[column] = edge;
        }

        return tabs;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>The faces of one drawing, made from the control's font and freed together.</summary>
    private sealed class Fonts : IDisposable
    {
        public Fonts(Font basis)
        {
            Body = basis;
            Title = new Font(basis.FontFamily, basis.SizeInPoints * 1.5f, FontStyle.Bold, GraphicsUnit.Point);
            Heading = new Font(basis.FontFamily, basis.SizeInPoints * 1.2f, FontStyle.Bold, GraphicsUnit.Point);
            Strong = new Font(basis.FontFamily, basis.SizeInPoints, FontStyle.Bold, GraphicsUnit.Point);
            Quote = new Font(basis.FontFamily, basis.SizeInPoints, FontStyle.Italic, GraphicsUnit.Point);
            Code = new Font("Consolas", basis.SizeInPoints, FontStyle.Regular, GraphicsUnit.Point);
            Gap = new Font(basis.FontFamily, basis.SizeInPoints * 0.5f, FontStyle.Regular, GraphicsUnit.Point);
        }

        public Font Body { get; }

        public Font Title { get; }

        public Font Heading { get; }

        public Font Strong { get; }

        public Font Quote { get; }

        public Font Code { get; }

        public Font Gap { get; }

        public void Dispose()
        {
            Title.Dispose();
            Heading.Dispose();
            Strong.Dispose();
            Quote.Dispose();
            Code.Dispose();
            Gap.Dispose();
        }
    }
}
