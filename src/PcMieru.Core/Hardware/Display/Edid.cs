using System.Text;

namespace PcMieru.Core.Hardware.Display;

/// <summary>One detailed timing descriptor. The first one in a base block is the monitor's preferred timing (recommended, not asserted to be native).</summary>
internal sealed record EdidTiming(int Width, int Height, double RefreshHz, bool Interlaced, int ImageWidthMm, int ImageHeightMm);

/// <summary>Everything the display collector reads out of an EDID. Nullable fields are things this EDID does not say.</summary>
internal sealed record EdidInfo(
    string PnpId,
    int ProductCode,
    uint NumericSerial,
    string? SerialText,
    string? MonitorName,
    int? Week,
    int? Year,
    bool WeekIsModelYear,
    int Major,
    int Minor,
    bool ChecksumOk,
    int ExtensionBlocks,
    bool ExtensionChecksumsOk,
    bool Digital,
    string? Interface,
    int? BitsPerColor,
    int? WidthCm,
    int? HeightCm,
    IReadOnlyList<EdidTiming> Timings,
    int? MinVerticalHz,
    int? MaxVerticalHz,
    bool HdrStaticMetadataBlock,
    bool HdrPq,
    bool HdrHlg,
    int? MaxLuminanceNits,
    int? MinLuminanceCdM2x10000,
    bool DisplayIdExtension)
{
    public EdidTiming? Preferred => Timings.Count > 0 ? Timings[0] : null;

    /// <summary>Physical size in millimetres: the detailed timing's image size when given (more precise), else the whole-centimetre fields.</summary>
    public (int Width, int Height)? PhysicalSizeMm
    {
        get
        {
            if (Preferred is { ImageWidthMm: > 0, ImageHeightMm: > 0 } p)
            {
                return (p.ImageWidthMm, p.ImageHeightMm);
            }

            return WidthCm is > 0 && HeightCm is > 0 ? (WidthCm.Value * 10, HeightCm.Value * 10) : null;
        }
    }
}

/// <summary>EDID 1.x base block plus CTA-861 / DisplayID extension headers. Pure byte reader, nothing here touches the system.</summary>
internal static class EdidParser
{
    private static readonly byte[] Header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    public static EdidInfo? Parse(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128 || !edid[..8].SequenceEqual(Header))
        {
            return null;
        }

        var block0 = edid[..128];
        var pnp = new string([(char)('@' + ((block0[8] >> 2) & 0x1F)), (char)('@' + (((block0[8] & 0x03) << 3) | (block0[9] >> 5))), (char)('@' + (block0[9] & 0x1F))]);
        var product = block0[10] | (block0[11] << 8);
        var serial = (uint)(block0[12] | (block0[13] << 8) | (block0[14] << 16) | (block0[15] << 24));
        var weekByte = block0[16];
        var modelYearFormat = weekByte == 0xFF;
        int? week = weekByte is >= 1 and <= 54 ? weekByte : null;
        int? year = block0[17] > 0 ? 1990 + block0[17] : null;

        var digital = (block0[20] & 0x80) != 0;
        var minor = block0[19];
        int? bits = null;
        string? iface = null;
        if (digital && minor >= 4)
        {
            bits = ((block0[20] >> 4) & 0x07) switch { 1 => 6, 2 => 8, 3 => 10, 4 => 12, 5 => 14, 6 => 16, _ => null };
            iface = (block0[20] & 0x0F) switch { 2 => "HDMI-a", 3 => "HDMI-b", 4 => "MDDI", 5 => "DisplayPort", _ => null };
        }

        int? widthCm = block0[21] > 0 ? block0[21] : null;
        int? heightCm = block0[22] > 0 ? block0[22] : null;
        if (widthCm is null || heightCm is null)
        {
            // Zero in one field means "aspect ratio", zero in both means unknown. Neither is a size.
            widthCm = null;
            heightCm = null;
        }

        var timings = new List<EdidTiming>();
        string? name = null;
        string? serialText = null;
        int? minV = null;
        int? maxV = null;
        for (var at = 54; at < 126; at += 18)
        {
            var d = block0.Slice(at, 18);
            if (d[0] != 0 || d[1] != 0)
            {
                var t = ReadTiming(d);
                if (t is not null)
                {
                    timings.Add(t);
                }

                continue;
            }

            switch (d[3])
            {
                case 0xFC:
                    name = Text(d.Slice(5, 13));
                    break;
                case 0xFF:
                    serialText = Text(d.Slice(5, 13));
                    break;
                case 0xFD:
                    {
                        var flags = d[4];
                        var min = d[5] + ((flags & 0x03) == 0x03 ? 255 : 0);
                        var max = d[6] + ((flags & 0x02) != 0 ? 255 : 0);
                        if (min > 0 && max >= min)
                        {
                            minV = min;
                            maxV = max;
                        }

                        break;
                    }
            }
        }

        var extCount = block0[126];
        var available = edid.Length / 128 - 1;
        var checksumOk = Sum(block0) == 0;
        var extOk = true;
        var hdrBlock = false;
        var pq = false;
        var hlg = false;
        int? maxLum = null;
        int? minLum = null;
        var displayId = false;
        for (var i = 0; i < extCount; i++)
        {
            if (i >= available)
            {
                extOk = false;
                break;
            }

            var ext = edid.Slice(128 * (i + 1), 128);
            extOk &= Sum(ext) == 0;
            if (ext[0] == 0x02)
            {
                ReadCta(ext, timings, ref hdrBlock, ref pq, ref hlg, ref maxLum, ref minLum);
            }
            else if (ext[0] == 0x70)
            {
                displayId = true;
            }
        }

        return new EdidInfo(
            pnp,
            product,
            serial,
            serialText,
            name,
            week,
            year,
            modelYearFormat,
            block0[18],
            minor,
            checksumOk,
            extCount,
            extOk,
            digital,
            iface,
            bits,
            widthCm,
            heightCm,
            timings,
            minV,
            maxV,
            hdrBlock,
            pq,
            hlg,
            maxLum,
            minLum,
            displayId);
    }

    private static int Sum(ReadOnlySpan<byte> block)
    {
        var sum = 0;
        foreach (var b in block)
        {
            sum += b;
        }

        return sum & 0xFF;
    }

    private static string? Text(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0x0A);
        var text = Encoding.ASCII.GetString(end < 0 ? field : field[..end]).Trim();
        return text.Length == 0 ? null : text;
    }

    private static EdidTiming? ReadTiming(ReadOnlySpan<byte> d)
    {
        var clock = (d[0] | (d[1] << 8)) * 10_000.0;
        var hActive = d[2] | ((d[4] & 0xF0) << 4);
        var hBlank = d[3] | ((d[4] & 0x0F) << 8);
        var vActive = d[5] | ((d[7] & 0xF0) << 4);
        var vBlank = d[6] | ((d[7] & 0x0F) << 8);
        var widthMm = d[12] | ((d[14] & 0xF0) << 4);
        var heightMm = d[13] | ((d[14] & 0x0F) << 8);
        var interlaced = (d[17] & 0x80) != 0;
        var total = (double)(hActive + hBlank) * (vActive + vBlank);
        if (hActive == 0 || vActive == 0 || total <= 0)
        {
            return null;
        }

        var hz = clock / total;
        if (interlaced)
        {
            hz *= 2;
        }

        return new EdidTiming(hActive, vActive, Math.Round(hz, 2), interlaced, widthMm, heightMm);
    }

    private static void ReadCta(ReadOnlySpan<byte> ext, List<EdidTiming> timings, ref bool hdrBlock, ref bool pq, ref bool hlg, ref int? maxLum, ref int? minLum)
    {
        var dtdOffset = ext[2];
        if (dtdOffset is < 4 or > 127)
        {
            return;
        }

        var i = 4;
        while (i < dtdOffset)
        {
            var tag = ext[i] >> 5;
            var len = ext[i] & 0x1F;
            if (i + 1 + len > dtdOffset)
            {
                break;
            }

            if (tag == 7 && len >= 2 && ext[i + 1] == 6)
            {
                // HDR Static Metadata Data Block: EOTF bits, supported descriptor types, then three optional luminance codes.
                hdrBlock = true;
                var eotf = ext[i + 2];
                pq = (eotf & 0x04) != 0;
                hlg = (eotf & 0x08) != 0;
                // Layout after the header byte: extended tag, EOTF flags, descriptor types, max luminance, max frame-average, min luminance.
                if (len >= 4 && ext[i + 4] != 0)
                {
                    maxLum = Luminance(ext[i + 4]);
                }

                if (len >= 6 && maxLum is { } max)
                {
                    // min = max * (code/255)^2 / 100 cd/m2, kept in units of 0.0001 cd/m2.
                    minLum = (int)Math.Round(max * Math.Pow(ext[i + 6] / 255.0, 2) / 100.0 * 10_000);
                }
            }

            i += 1 + len;
        }

        for (var at = dtdOffset; at + 18 <= 127; at += 18)
        {
            var d = ext.Slice(at, 18);
            if (d[0] == 0 && d[1] == 0)
            {
                break;
            }

            var t = ReadTiming(d);
            if (t is not null)
            {
                timings.Add(t);
            }
        }
    }

    /// <summary>CTA-861.3: 50 * 2^(code / 32) cd/m2.</summary>
    private static int Luminance(byte code) => code == 0 ? 0 : (int)Math.Round(50 * Math.Pow(2, code / 32.0));
}
