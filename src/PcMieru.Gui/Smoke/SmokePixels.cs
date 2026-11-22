using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PcMieru.Gui;

/// <summary>
/// A look at the pixels of a picture the smoke run took: whether a region that must show text shows something. The window is
/// asked about its text (<c>CodeText == result.Code</c>), which says nothing about whether it was painted; a code box that was
/// not painted yet is one flat colour in the picture.
/// </summary>
internal static class SmokePixels
{
    /// <summary>
    /// The fewest distinct colours the text area of a region shows. Text on a plain background is the background, the text colour and
    /// the anti-aliased steps between them (ClearType adds more): a line of the code box has well over twenty (every picture of the
    /// real window reached the count's cap of 64). A flat region has one. The scroll bar is not part of the text area: the first
    /// version counted the whole client rectangle, and a blank code box with its scroll bar showed twelve colours and passed, so the
    /// count is taken on <see cref="TextArea"/> and eight is far from the one a blank box has and far from the sixty-four a written one has.
    /// </summary>
    internal const int MinDistinctColours = 8;

    /// <summary>How many distinct colours a region shows, counted up to <paramref name="cap"/> (the count stops there).</summary>
    internal static int DistinctColours(Bitmap picture, Rectangle region, int cap = 64)
    {
        region.Intersect(new Rectangle(Point.Empty, picture.Size));
        if (region.IsEmpty)
        {
            return 0;
        }

        var data = picture.LockBits(new Rectangle(Point.Empty, picture.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * picture.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var seen = new HashSet<int>();
            for (var y = region.Top; y < region.Bottom; y++)
            {
                for (var x = region.Left; x < region.Right; x++)
                {
                    var i = (y * data.Stride) + (x * 4);
                    seen.Add(bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16));
                    if (seen.Count >= cap)
                    {
                        return seen.Count;
                    }
                }
            }

            return seen.Count;
        }
        finally
        {
            picture.UnlockBits(data);
        }
    }

    /// <summary>
    /// What of a control's client rectangle can hold text: two pixels in from every edge (the border and its shadow) and without a strip the
    /// width of a vertical scroll bar at the right (a list or a text box shows its scroll bar there, with arrows, a thumb and a track that are
    /// many colours on their own). Text starts at the left and runs across, so what is left of the width still holds all of it.
    /// </summary>
    internal static Rectangle TextArea(Rectangle client, int dpi)
    {
        var bar = SystemInformation.GetVerticalScrollBarWidthForDpi(dpi);
        client.Inflate(-2, -2);
        client.Width -= bar;
        return client.Width > 0 && client.Height > 0 ? client : Rectangle.Empty;
    }

    /// <summary>The text area of a control in the coordinates of a picture of the window at <paramref name="windowOrigin"/> (screen coordinates).</summary>
    internal static Rectangle TextAreaInPicture(Control control, Point windowOrigin)
    {
        var onScreen = control.RectangleToScreen(control.ClientRectangle);
        onScreen.Offset(-windowOrigin.X, -windowOrigin.Y);
        return TextArea(onScreen, control.DeviceDpi);
    }
}
