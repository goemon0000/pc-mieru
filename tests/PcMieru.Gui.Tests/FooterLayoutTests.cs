namespace PcMieru.Gui.Tests;

public class FooterLayoutTests
{
    // Real data can put two notes after the count (users: "partial only" and "* means ..."); together they are wider than the
    // window. The smoke run found them running off the side; the footer must wrap them and grow instead.
    [Fact]
    public void Notes_too_long_for_one_line_wrap_inside_the_footer_and_the_footer_grows()
    {
        using var host = new Panel { Size = new Size(700, 400) };
        using var count = new Label { AutoSize = true, Text = "2 sessions" };
        using var partial = new ToneLabel { AutoSize = true, Text = "Partial only: other users' processes may be hidden unless you are an administrator." };
        using var hint = new ToneLabel { AutoSize = true, Text = "Press the > on the left to see that session's processes. CPU and memory are the totals of that user's processes. * means some values could not be read." };
        var footer = PageLayout.Footer("testFooter", 12, count, partial);
        footer.Controls.Add(hint);
        host.Controls.Add(footer);

        host.PerformLayout();
        footer.PerformLayout();

        Assert.True(hint.Top > count.Top, $"the long note stayed on the count's line ({hint.Top} vs {count.Top})");
        Assert.True(footer.Height >= hint.Bottom, $"footer is {footer.Height} px high, the last note ends at {hint.Bottom}");
        foreach (Control control in footer.Controls)
        {
            Assert.True(control.Left >= 0 && control.Right <= footer.ClientSize.Width, $"{control.Text[..10]} spans {control.Left}..{control.Right} in {footer.ClientSize.Width}");
            Assert.True(control.Top >= 0 && control.Bottom <= footer.ClientSize.Height, $"{control.Text[..10]} spans {control.Top}..{control.Bottom} in {footer.ClientSize.Height}");
        }
    }

    [Fact]
    public void A_short_note_keeps_the_footer_one_line_high()
    {
        using var host = new Panel { Size = new Size(1000, 400) };
        using var count = new Label { AutoSize = true, Text = "28 / 28" };
        using var hint = new ToneLabel { AutoSize = true, Text = "Short hint." };
        var footer = PageLayout.Footer("testFooter", 12, count, hint);
        host.Controls.Add(footer);
        var oneLine = footer.Height;

        host.PerformLayout();
        footer.PerformLayout();

        Assert.Equal(oneLine, footer.Height);
        Assert.Equal(count.Top, hint.Top);
    }
}
