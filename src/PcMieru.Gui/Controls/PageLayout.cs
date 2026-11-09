namespace PcMieru.Gui;

/// <summary>Small layout helpers shared by the pages: the bar above a list and the line below it.</summary>
internal static class PageLayout
{
    /// <summary>A one-row bar: the left controls, a stretching gap, then the right controls. Everything is vertically centred.</summary>
    public static TableLayoutPanel Bar(string name, int pad, Control[] left, Control[] right)
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = left.Length + right.Length + 1,
            RowCount = 1,
            Padding = new Padding(pad, pad * 5 / 6, pad, pad * 2 / 3),
            Name = name,
        };
        var column = 0;
        foreach (var control in left)
        {
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            control.Anchor = AnchorStyles.Left;
            control.Margin = new Padding(0, 0, pad * 2 / 3, 0);
            bar.Controls.Add(control, column++, 0);
        }

        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        column++;
        foreach (var control in right)
        {
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            control.Anchor = AnchorStyles.Right;
            control.Margin = new Padding(pad * 2 / 3, 0, 0, 0);
            bar.Controls.Add(control, column++, 0);
        }

        return bar;
    }

    /// <summary>The thin line under a list: a count on the left, a hint in the dim tone next to it.</summary>
    public static FlowLayoutPanel Footer(string name, int pad, Label count, ToneLabel hint)
    {
        var footer = new FooterPanel(pad)
        {
            Dock = DockStyle.Bottom,
            WrapContents = false,
            Padding = new Padding(pad, pad * 7 / 12, pad, 0),
            Name = name,
        };
        hint.SetTone(CellTone.Dim);
        footer.Controls.Add(count);
        footer.Controls.Add(hint);
        return footer;
    }

    /// <summary>The line under a list. Its height follows the font, so a larger text size is not cut off.</summary>
    private sealed class FooterPanel : FlowLayoutPanel
    {
        private readonly int _pad;

        public FooterPanel(int pad)
        {
            _pad = pad;
            Height = MinimumHeight();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Height = MinimumHeight();
        }

        private int MinimumHeight() => Math.Max(_pad * 8 / 3, Font.Height + (_pad * 7 / 12) + (_pad * 2 / 3));
    }

    /// <summary>Common settings of a simple virtual list.</summary>
    public static void Prepare(GridView grid, string accessibleName, params (string Text, int Width, HorizontalAlignment Align)[] columns)
    {
        grid.AccessibleName = accessibleName;
        grid.Dock = DockStyle.Fill;
        foreach (var (text, width, align) in columns)
        {
            grid.Columns.Add(new ColumnHeader { Text = text, Width = width, TextAlign = align });
        }
    }
}
