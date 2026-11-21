using System.Drawing.Imaging;

namespace PcMieru.Gui.Tests;

/// <summary>
/// The smoke run's look at the pixels of a picture (does the code box show text, or a blank box with its scroll bar?). The first version counted the
/// colours of the whole client rectangle, and a blank code box showed twelve of them (the scroll bar's arrows and thumb) and passed. These tests keep
/// the blank box failing and the written one passing: the count is of the text area, the scroll bar's strip is left out.
/// </summary>
public class SmokePixelsTests
{
    private const int Dpi = 96;

    /// <summary>A white picture of a code box (border, white inside) with a scroll bar of many colours in its right strip; text is added by the caller.</summary>
    private static Bitmap CodeBoxPicture(out Rectangle client)
    {
        var picture = new Bitmap(300, 120, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(picture))
        {
            g.Clear(Color.White);
            client = new Rectangle(10, 10, 280, 100);
            var bar = SystemInformation.GetVerticalScrollBarWidthForDpi(Dpi);
            for (var i = 0; i < 20; i++)
            {
                using var brush = new SolidBrush(Color.FromArgb(200 + i, 200 + i, 200 + i));
                g.FillRectangle(brush, client.Right - bar, client.Top + (i * 5), bar, 5);
            }
        }

        return picture;
    }

    [Fact]
    public void A_blank_code_box_with_its_scroll_bar_is_flat_in_its_text_area()
    {
        using var picture = CodeBoxPicture(out var client);

        // The whole client rectangle, scroll bar included, is far over the limit: counting it let a blank code box pass.
        Assert.True(SmokePixels.DistinctColours(picture, client) >= SmokePixels.MinDistinctColours);

        var textArea = SmokePixels.TextArea(client, Dpi);
        Assert.Equal(1, SmokePixels.DistinctColours(picture, textArea));
        Assert.True(SmokePixels.DistinctColours(picture, textArea) < SmokePixels.MinDistinctColours);
    }

    [Fact]
    public void A_code_box_with_text_is_painted_in_its_text_area()
    {
        using var picture = CodeBoxPicture(out var client);
        using (var g = Graphics.FromImage(picture))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var font = new Font("Consolas", 10f);
            g.DrawString("QkxERzEuMGVyN2ZiQ0JB0123456789abcdefghijklmnopqrstuvwxyz", font, Brushes.Black, client.Left + 4, client.Top + 4);
        }

        var textArea = SmokePixels.TextArea(client, Dpi);
        Assert.True(SmokePixels.DistinctColours(picture, textArea) >= SmokePixels.MinDistinctColours);
    }

    [Fact]
    public void The_text_area_leaves_out_the_edges_and_the_scroll_bar_strip_and_never_goes_negative()
    {
        var client = new Rectangle(10, 20, 300, 80);
        var area = SmokePixels.TextArea(client, Dpi);
        Assert.Equal(client.Left + 2, area.Left);
        Assert.Equal(client.Top + 2, area.Top);
        Assert.Equal(client.Bottom - 2, area.Bottom);
        Assert.Equal(client.Right - 2 - SystemInformation.GetVerticalScrollBarWidthForDpi(Dpi), area.Right);

        Assert.Equal(Rectangle.Empty, SmokePixels.TextArea(new Rectangle(0, 0, 10, 10), Dpi));
        using var blank = new Bitmap(20, 20);
        Assert.Equal(0, SmokePixels.DistinctColours(blank, Rectangle.Empty));
    }
}
