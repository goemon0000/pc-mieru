using System.Runtime.InteropServices;

namespace PcMieru.Core.Collection;

/// <summary>Pulls the first icon out of an exe/dll/ico and returns it as PNG. Blocking; call it from a background thread.</summary>
internal static unsafe class IconExtractor
{
    private const uint ExtractError = 0xFFFFFFFF;
    private const uint DibRgbColors = 0;

    public static Value<byte[]> ExtractPng(string path, int size)
    {
        nint icon = 0;
        var count = Win32.PrivateExtractIcons(path, 0, size, size, &icon, null, 1, 0);
        if (count == 0 || count == ExtractError || icon == 0)
        {
            return Value.Unsupported<byte[]>(ProcessIconSource.Source, "this file has no icon");
        }

        try
        {
            return Render(icon);
        }
        finally
        {
            _ = Win32.DestroyIcon(icon);
        }
    }

    private static Value<byte[]> Render(nint icon)
    {
        if (!Win32.GetIconInfo(icon, out var info))
        {
            return Value.Failed<byte[]>(ProcessIconSource.Source, $"GetIconInfo failed (Win32 error {Marshal.GetLastPInvokeError()})");
        }

        try
        {
            if (info.Color == 0)
            {
                // A black-and-white icon has no colour bitmap; not worth special code, the GUI shows its generic icon.
                return Value.Unsupported<byte[]>(ProcessIconSource.Source, "this file only has a monochrome icon");
            }

            if (Win32.GetObjectW(info.Color, sizeof(Win32.BitmapInfoBasic), out var bitmap) == 0)
            {
                return Value.Failed<byte[]>(ProcessIconSource.Source, "the icon bitmap could not be measured");
            }

            var width = bitmap.Width;
            var height = bitmap.Height;
            if (width is <= 0 or > 256 || height is <= 0 or > 256)
            {
                return Value.Failed<byte[]>(ProcessIconSource.Source, $"the icon has an unexpected size ({width} x {height})");
            }

            var pixels = new byte[width * height * 4];
            if (!ReadBitmap(info.Color, width, height, pixels))
            {
                return Value.Failed<byte[]>(ProcessIconSource.Source, "the icon pixels could not be read");
            }

            if (!HasAnyAlpha(pixels) && info.Mask != 0)
            {
                // An icon without a real alpha channel says which pixels are see-through in its mask (white = transparent).
                var mask = new byte[width * height * 4];
                if (ReadBitmap(info.Mask, width, height, mask))
                {
                    for (var i = 0; i < pixels.Length; i += 4)
                    {
                        pixels[i + 3] = mask[i] == 0 && mask[i + 1] == 0 && mask[i + 2] == 0 ? (byte)255 : (byte)0;
                    }
                }
                else
                {
                    MakeOpaque(pixels);
                }
            }
            else if (!HasAnyAlpha(pixels))
            {
                MakeOpaque(pixels);
            }

            return Value.Ok(PngEncoder.EncodeBgra(pixels, width, height), ProcessIconSource.Source);
        }
        finally
        {
            if (info.Color != 0)
            {
                _ = Win32.DeleteObject(info.Color);
            }

            if (info.Mask != 0)
            {
                _ = Win32.DeleteObject(info.Mask);
            }
        }
    }

    /// <summary>32-bit top-down BGRA copy of a bitmap.</summary>
    private static bool ReadBitmap(nint bitmap, int width, int height, byte[] destination)
    {
        var header = new Win32.BitmapInfoHeader
        {
            Size = (uint)sizeof(Win32.BitmapInfoHeader),
            Width = width,
            Height = -height, // negative: rows run top to bottom
            Planes = 1,
            BitCount = 32,
            Compression = 0, // BI_RGB
        };

        var dc = Win32.GetDC(0);
        if (dc == 0)
        {
            return false;
        }

        try
        {
            fixed (byte* p = destination)
            {
                return Win32.GetDIBits(dc, bitmap, 0, (uint)height, p, ref header, DibRgbColors) == height;
            }
        }
        finally
        {
            _ = Win32.ReleaseDC(0, dc);
        }
    }

    private static bool HasAnyAlpha(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void MakeOpaque(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            bgra[i] = 255;
        }
    }
}
