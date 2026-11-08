using System.Drawing.Drawing2D;

namespace PcMieru.Gui;

/// <summary>Draws time series into a rectangle. Shared by the graph panels and the small cards. Reuses its point buffer: no allocation per paint.</summary>
internal static class GraphPainter
{
    [ThreadStatic]
    private static PointF[]? _points;

    [ThreadStatic]
    private static PointF[]? _polygon;

    /// <summary>
    /// Draws <paramref name="count"/> values, oldest first, newest at the right edge of <paramref name="area"/>.
    /// NaN is a gap (a value that was not collected): the line breaks there instead of dropping to zero.
    /// </summary>
    public static void Series(Graphics g, Rectangle area, double[] values, int count, double minimum, double maximum, int slots, Color line, int lineWidth, bool fill)
    {
        if (count <= 0 || maximum <= minimum || area.Width <= 2 || area.Height <= 2)
        {
            return;
        }

        var points = _points ??= new PointF[SnapshotPump.HistoryLength + 4];
        if (points.Length < count)
        {
            points = _points = new PointF[count + 4];
        }

        var span = Math.Max(slots - 1, 1);
        var dx = (float)(area.Width - 1) / span;
        var right = area.Right - 1;
        var bottom = area.Bottom - 1;
        var height = area.Height - 1;

        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(line, lineWidth) { LineJoin = LineJoin.Round };
        using var brush = fill ? new SolidBrush(Color.FromArgb(46, line)) : null;

        var run = 0;
        for (var i = 0; i <= count; i++)
        {
            var valid = i < count && !double.IsNaN(values[i]);
            if (valid)
            {
                var clamped = Math.Clamp(values[i], minimum, maximum);
                points[run++] = new PointF(right - ((count - 1 - i) * dx), bottom - (float)((clamped - minimum) / (maximum - minimum) * height));
                continue;
            }

            if (run >= 2)
            {
                if (brush is not null)
                {
                    FillUnder(g, brush, points, run, bottom);
                }

                g.DrawLines(pen, points.AsSpan(0, run));
            }
            else if (run == 1)
            {
                using var dot = new SolidBrush(line);
                g.FillEllipse(dot, points[0].X - lineWidth, points[0].Y - lineWidth, lineWidth * 2, lineWidth * 2);
            }

            run = 0;
        }

        g.SmoothingMode = old;
    }

    /// <summary>"Nice" upper bound for an automatic axis: 1, 2, 5 times a power of ten, never below <paramref name="floor"/>.</summary>
    public static double NiceMaximum(double value, double floor)
    {
        value = Math.Max(value, floor);
        var exponent = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in new[] { 1.0, 2.0, 5.0, 10.0 })
        {
            if (value <= step * exponent)
            {
                return step * exponent;
            }
        }

        return 10 * exponent;
    }

    private static void FillUnder(Graphics g, Brush brush, PointF[] points, int count, int bottom)
    {
        var polygon = _polygon;
        if (polygon is null || polygon.Length < count + 2)
        {
            polygon = _polygon = new PointF[count + 2];
        }

        Array.Copy(points, polygon, count);
        polygon[count] = new PointF(points[count - 1].X, bottom);
        polygon[count + 1] = new PointF(points[0].X, bottom);
        g.FillPolygon(brush, polygon.AsSpan(0, count + 2));
    }
}

/// <summary>
/// One performance panel: a title, the current value as text, and up to three series over the last 60 samples.
/// Painted in one pass into a double buffer; it only repaints when new data arrives and the page is showing.
/// The data arrays belong to the page and are read in place, so a new sample costs a refill and an Invalidate.
/// </summary>
internal sealed class GraphControl : Control, IThemed
{
    private readonly double[]?[] _values = new double[]?[3];
    private readonly int[] _counts = new int[3];
    private readonly string[] _names = ["", "", ""];
    private Palette? _palette;
    private string _title = string.Empty;
    private string _valueText = string.Empty;
    private string _detailText = string.Empty;

    public GraphControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        MinimumSize = new Size(120, 80);
    }

    /// <summary>A fixed axis maximum (100 for percent). Null: the axis follows the data.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal double? FixedMaximum { get; set; } = 100;

    /// <summary>Lowest value the automatic axis may shrink to.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal double AutoFloor { get; set; } = 1;

    /// <summary>Text for the axis maximum ("100 %", "5 MB/s"). Null shows nothing.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<double, string>? AxisFormatter { get; set; }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string Title
    {
        get => _title;
        set
        {
            _title = value;
            AccessibleName = value;
            Invalidate();
        }
    }

    /// <summary>The current value, large, top right. Include the unit; "-" when nothing was collected.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string ValueText
    {
        get => _valueText;
        set
        {
            if (_valueText != value)
            {
                _valueText = value;
                AccessibleDescription = value;
                Invalidate();
            }
        }
    }

    /// <summary>A line under the title (model name, link speed).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string DetailText
    {
        get => _detailText;
        set
        {
            if (_detailText != value)
            {
                _detailText = value;
                Invalidate();
            }
        }
    }

    public void SetSeriesName(int index, string name)
    {
        _names[index] = name;
        Invalidate();
    }

    /// <summary>Points the series at the page's array. <paramref name="count"/> values, oldest first; NaN marks a gap.</summary>
    public void SetSeries(int index, double[] values, int count)
    {
        _values[index] = values;
        _counts[index] = count;
    }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var palette = _palette;
        var g = e.Graphics;
        if (palette is null)
        {
            return;
        }

        var bounds = new Rectangle(0, 0, Width, Height);
        using (var back = new SolidBrush(palette.Surface))
        {
            g.FillRectangle(back, bounds);
        }

        var pad = LogicalToDeviceUnits(10);
        var headerHeight = Font.Height + LogicalToDeviceUnits(4);
        var titleBounds = new Rectangle(pad, pad - 2, Math.Max(Width - (pad * 2), 0), headerHeight);
        var valueWidth = TextRenderer.MeasureText(g, _valueText, BoldFont, new Size(Width, headerHeight), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        TextRenderer.DrawText(
            g,
            _title,
            Font,
            new Rectangle(titleBounds.Left, titleBounds.Top, Math.Max(titleBounds.Width - valueWidth - LogicalToDeviceUnits(8), 0), titleBounds.Height),
            palette.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(
            g,
            _valueText,
            BoldFont,
            titleBounds,
            palette.Text,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        var top = titleBounds.Bottom + LogicalToDeviceUnits(2);
        if (_detailText.Length > 0)
        {
            TextRenderer.DrawText(
                g,
                _detailText,
                SmallFont,
                new Rectangle(pad, top, Math.Max(Width - (pad * 2), 0), SmallFont.Height + 2),
                palette.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            top += SmallFont.Height + LogicalToDeviceUnits(4);
        }

        var legendHeight = _names[1].Length > 0 ? SmallFont.Height + LogicalToDeviceUnits(4) : 0;
        var plot = new Rectangle(pad, top, Math.Max(Width - (pad * 2), 0), Math.Max(Height - top - pad - legendHeight, 0));
        if (plot.Width > 4 && plot.Height > 4)
        {
            DrawPlot(g, plot, palette);
        }

        if (legendHeight > 0)
        {
            DrawLegend(g, new Rectangle(pad, Height - pad - legendHeight + 2, plot.Width, legendHeight), palette);
        }

        using var border = new Pen(palette.Border);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    private double Maximum()
    {
        if (FixedMaximum is { } fixedMax)
        {
            return fixedMax;
        }

        var max = 0.0;
        for (var s = 0; s < _values.Length; s++)
        {
            var data = _values[s];
            for (var i = 0; data is not null && i < _counts[s]; i++)
            {
                if (!double.IsNaN(data[i]) && data[i] > max)
                {
                    max = data[i];
                }
            }
        }

        return GraphPainter.NiceMaximum(max * 1.1, AutoFloor);
    }

    private void DrawPlot(Graphics g, Rectangle plot, Palette palette)
    {
        using (var grid = new Pen(Color.FromArgb(70, palette.Border)))
        {
            for (var quarter = 0; quarter <= 4; quarter++)
            {
                var y = plot.Top + ((plot.Height - 1) * quarter / 4);
                g.DrawLine(grid, plot.Left, y, plot.Right - 1, y);
            }
        }

        var max = Maximum();
        for (var s = _values.Length - 1; s >= 0; s--)
        {
            if (_values[s] is { } data && _counts[s] > 0)
            {
                GraphPainter.Series(g, plot, data, _counts[s], 0, max, SnapshotPump.HistoryLength, SeriesColor(palette, s), Math.Max(2, LogicalToDeviceUnits(2)), fill: s == 0);
            }
        }

        if (AxisFormatter is { } format)
        {
            TextRenderer.DrawText(
                g,
                format(max),
                SmallFont,
                new Rectangle(plot.Left + 2, plot.Top + 1, plot.Width, SmallFont.Height),
                palette.TextDim,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    private void DrawLegend(Graphics g, Rectangle area, Palette palette)
    {
        var x = area.Left;
        for (var s = 0; s < _names.Length; s++)
        {
            if (_names[s].Length == 0)
            {
                continue;
            }

            var swatch = LogicalToDeviceUnits(8);
            using (var brush = new SolidBrush(SeriesColor(palette, s)))
            {
                g.FillRectangle(brush, x, area.Top + ((area.Height - swatch) / 2), swatch, swatch);
            }

            x += swatch + LogicalToDeviceUnits(6);
            var size = TextRenderer.MeasureText(g, _names[s], SmallFont, new Size(area.Width, area.Height), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, _names[s], SmallFont, new Rectangle(x, area.Top, size.Width + 2, area.Height), palette.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            x += size.Width + LogicalToDeviceUnits(16);
        }
    }

    internal static Color SeriesColor(Palette palette, int index)
    {
        var dark = palette.Window.GetBrightness() < 0.5f;
        return index switch
        {
            0 => palette.Accent,
            1 => dark ? Color.FromArgb(255, 176, 96) : Color.FromArgb(196, 98, 16),
            _ => dark ? Color.FromArgb(120, 214, 150) : Color.FromArgb(24, 130, 70),
        };
    }

    private Font? _bold;
    private Font? _small;

    private Font BoldFont => _bold != null && _bold.Size == Font.Size && _bold.Name == Font.Name
        ? _bold
        : _bold = new Font(Font.FontFamily, Font.Size * 1.15f, FontStyle.Bold);

    private Font SmallFont => _small != null && Math.Abs(_small.Size - (Font.Size * 0.9f)) < 0.01f && _small.Name == Font.Name
        ? _small
        : _small = new Font(Font.FontFamily, Font.Size * 0.9f);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bold?.Dispose();
            _small?.Dispose();
        }

        base.Dispose(disposing);
    }
}
