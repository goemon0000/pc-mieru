using System.Globalization;
using PcMieru.Core.Hardware.Display;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>
/// Monitors: the active display paths from the display configuration API, each joined to the monitor's EDID
/// (path -> monitor device interface -> device node -> Device Parameters\EDID) and to Windows' HDR state.
/// </summary>
internal static class DisplayCollector
{
    private const string Src = "displayconfig:QueryDisplayConfig";
    private static readonly Guid MonitorInterface = new("e6f07b5f-ee97-4a90-b076-33f57bf4eaa7");

    public static Value<IReadOnlyList<DisplayDevice>> Collect(int? currentYear = null)
    {
        try
        {
            var paths = DisplayConfig.Query(out var error);
            if (paths is null)
            {
                return Probe.FromWin32<IReadOnlyList<DisplayDevice>>(error, Src, "QueryDisplayConfig");
            }

            if (paths.Count == 0)
            {
                return new Value<IReadOnlyList<DisplayDevice>>([], ValueStatus.Ok, Src, "no active display (no monitor is switched on, or this is a service/remote session)");
            }

            var gpus = Native.Dxgi.Enumerate(out _)?.ToDictionary(a => a.LuidText, a => a.Name) ?? [];
            var year = currentYear ?? DateTime.UtcNow.Year;
            var edids = ReadEdids(paths);
            var list = new List<DisplayDevice>();
            foreach (var p in paths)
            {
                var edid = edids.GetValueOrDefault(p.DevicePath ?? string.Empty);
                var color = DisplayConfig.Advanced(p.AdapterLuidLow, p.AdapterLuidHigh, p.TargetId);
                list.Add(DisplayBuilder.Build(p, edid, color, gpus.GetValueOrDefault(p.AdapterLuidText), year));
            }

            return Value.Ok<IReadOnlyList<DisplayDevice>>(list, Src);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<IReadOnlyList<DisplayDevice>>(ex, Src);
        }
    }

    /// <summary>The raw EDID of every monitor the paths refer to, keyed by monitor device path.</summary>
    internal static Dictionary<string, byte[]> ReadEdids(IReadOnlyList<ActiveDisplayPath> paths)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var wanted = paths.Select(p => p.DevicePath).Where(p => !string.IsNullOrEmpty(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return result;
        }

        using var set = DeviceInfoSet.ForInterface(MonitorInterface);
        if (set is null)
        {
            return result;
        }

        foreach (var (path, node) in set.Interfaces(MonitorInterface))
        {
            if (!wanted.Contains(path))
            {
                continue;
            }

            try
            {
                using var key = node.OpenDeviceKey();
                if (key?.GetValue("EDID") is byte[] { Length: >= 128 } edid)
                {
                    result[path] = edid;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A monitor whose key cannot be read is reported without EDID details; the others still work.
            }
        }

        return result;
    }
}

/// <summary>Turns one display path plus its EDID into the public record. No system calls, so it is unit tested with synthetic input.</summary>
internal static class DisplayBuilder
{
    private const string EdidSrc = "edid";
    private const string Src = "displayconfig:QueryDisplayConfig";
    private const string NoEdid = "EDID is not available for this display (virtual, remote or indirect display)";

    public static DisplayDevice Build(ActiveDisplayPath path, byte[]? rawEdid, AdvancedColorState? color, string? gpuName, int currentYear)
    {
        var edid = rawEdid is null ? null : EdidParser.Parse(rawEdid);
        var edidMissing = edid is null
            ? rawEdid is null ? NoEdid : "EDID is damaged (header does not match)"
            : null;

        Value<T> FromEdid<T>(Func<EdidInfo, Value<T>> read) =>
            edid is null ? Value.Unsupported<T>(EdidSrc, edidMissing) : read(edid);

        // Manufacturer
        Value<string> manufacturer;
        if (edid is not null)
        {
            var name = PnpVendors.Name(edid.PnpId);
            manufacturer = name is not null
                ? Value.Ok(name, "edid:manufacturer id (" + edid.PnpId + ") + built-in PNP list")
                : new Value<string>(edid.PnpId, ValueStatus.Ok, "edid:manufacturer id", "three-letter id; its brand name is not in the built-in list");
        }
        else if (path.EdidIdsValid && PnpFromWord(path.EdidManufacturerId) is { } id)
        {
            var name = PnpVendors.Name(id);
            manufacturer = new Value<string>(name ?? id, ValueStatus.Ok, "displayconfig:edidManufactureId", name is null ? "three-letter id; its brand name is not in the built-in list" : null);
        }
        else
        {
            manufacturer = Value.Unsupported<string>(EdidSrc, edidMissing);
        }

        // Model
        Value<string> model;
        if (edid?.MonitorName is { } named)
        {
            model = Value.Ok(named, "edid:monitor name descriptor (0xFC)");
        }
        else if (!string.IsNullOrWhiteSpace(path.FriendlyName) && !IsGenericName(path.FriendlyName))
        {
            model = Value.Ok(path.FriendlyName!, "displayconfig:monitorFriendlyDeviceName");
        }
        else
        {
            model = Value.Unsupported<string>(EdidSrc, "the monitor does not give a model name (Windows shows only a generic name)");
        }

        // Manufacture date
        Value<int> yearValue;
        Value<int> weekValue;
        if (edid is null)
        {
            yearValue = Value.Unsupported<int>(EdidSrc, edidMissing);
            weekValue = Value.Unsupported<int>(EdidSrc, edidMissing);
        }
        else
        {
            if (edid.Year is { } y && y >= 1990 && y <= currentYear + 1)
            {
                yearValue = edid.WeekIsModelYear
                    ? new Value<int>(y, ValueStatus.Ok, "edid:base block year", "this is the model year, not the manufacture year")
                    : Value.Ok(y, "edid:base block year");
            }
            else
            {
                yearValue = Value.Unsupported<int>(EdidSrc, edid.Year is null ? "year is not set in EDID" : "year in EDID is not plausible");
            }

            weekValue = edid.Week is { } w
                ? Value.Ok(w, "edid:base block week")
                : Value.Unsupported<int>(EdidSrc, edid.WeekIsModelYear ? "EDID gives a model year instead of a week" : "week is not set in EDID");
        }

        // Physical size
        var size = edid?.PhysicalSizeMm;
        var sizeFromTiming = edid?.Preferred is { ImageWidthMm: > 0, ImageHeightMm: > 0 };
        var sizeSource = sizeFromTiming ? "edid:first detailed timing image size" : "edid:base block size in cm";
        var sizeDetail = sizeFromTiming ? null : "EDID gives whole centimetres only";
        var width = size is { } sz
            ? new Value<int>(sz.Width, ValueStatus.Ok, sizeSource, sizeDetail)
            : Value.Unsupported<int>(EdidSrc, edid is null ? edidMissing : "EDID does not give a physical size (projector or aspect ratio only)");
        var height = size is { } sz2
            ? new Value<int>(sz2.Height, ValueStatus.Ok, sizeSource, sizeDetail)
            : Value.Unsupported<int>(EdidSrc, edid is null ? edidMissing : "EDID does not give a physical size (projector or aspect ratio only)");

        // Resolutions
        Value<string> preferred;
        if (edid?.Preferred is { } pt)
        {
            preferred = new Value<string>(
                $"{pt.Width}x{pt.Height}",
                ValueStatus.Ok,
                "edid:first detailed timing (preferred)",
                $"{pt.RefreshHz.ToString("0.##", CultureInfo.InvariantCulture)} Hz{(pt.Interlaced ? " interlaced" : string.Empty)}; the monitor's recommended mode, not proof of the native panel resolution");
        }
        else
        {
            preferred = Value.Unsupported<string>(EdidSrc, edid is null ? edidMissing : "EDID has no detailed timing");
        }

        var current = path.Width is { } cw && path.Height is { } ch
            ? Value.Ok($"{cw}x{ch}", Src + " source mode")
            : Value.Unsupported<string>(Src, "no active mode for this display");
        var refresh = path.RefreshHz is { } hz
            ? Value.Ok(hz, Src + " refresh rate")
            : Value.Unsupported<double>(Src, "refresh rate is not reported");

        var hdrEnabled = color is null
            ? Value.Unsupported<bool>("displayconfig:GET_ADVANCED_COLOR_INFO", "Windows did not report the colour state of this display")
            : new Value<bool>(color.HdrEnabled, ValueStatus.Ok, color.Source, color.Detail);

        var details = new DisplayDetails(
            FromEdid(e => Value.Ok(e.PnpId, "edid:manufacturer id")),
            FromEdid(e => Value.Ok(e.ProductCode, "edid:product code")),
            FromEdid(Serial),
            FromEdid(e => Value.Ok($"{e.Major}.{e.Minor}", "edid:version")),
            FromEdid(e =>
            {
                var ok = e.ChecksumOk && e.ExtensionChecksumsOk;
                return new Value<bool>(
                    ok,
                    ValueStatus.Ok,
                    $"edid:checksums of the base block and {e.ExtensionBlocks} extension block(s)",
                    ok ? null : "at least one block does not add up to zero, so its data may be damaged");
            }),
            FromEdid(e => Value.Ok(e.ExtensionBlocks, "edid:extension block count")),
            FromEdid(Diagonal),
            FromEdid(e => Value.Ok(e.Digital ? e.Interface ?? "Digital" : "Analog", "edid:video input definition")),
            FromEdid(e => e.BitsPerColor is { } b
                ? new Value<int>(b, ValueStatus.Ok, "edid:video input definition", "the most the monitor declares, not the current signal")
                : Value.Unsupported<int>(EdidSrc, e.Digital ? "EDID older than 1.4 or colour depth undefined" : "analog input has no colour depth field")),
            FromEdid(e => e.MonitorName is { } n ? Value.Ok(n, "edid:monitor name descriptor (0xFC)") : Value.Unsupported<string>(EdidSrc, "no monitor name descriptor")),
            FromEdid(e => e.MinVerticalHz is { } m ? Value.Ok((double)m, "edid:range limits descriptor (0xFD)") : Value.Unsupported<double>(EdidSrc, "no range limits descriptor")),
            FromEdid(e => e.MaxVerticalHz is { } m ? Value.Ok((double)m, "edid:range limits descriptor (0xFD)") : Value.Unsupported<double>(EdidSrc, "no range limits descriptor")),
            color is not null
                ? new Value<bool>(color.HdrSupported, ValueStatus.Ok, color.Source, "Windows answer for the GPU, cable and monitor together")
                : FromEdid(e => e.HdrStaticMetadataBlock && e.HdrPq
                    ? new Value<bool>(true, ValueStatus.Ok, "edid:CTA-861 HDR static metadata block", "monitor advertises HDR10 (ST 2084)")
                    : Value.Unsupported<bool>(EdidSrc, "Windows did not answer and the EDID has no HDR10 block")),
            FromEdid(e =>
                e.HdrStaticMetadataBlock
                    ? Value.Ok(true, "edid:CTA-861 HDR static metadata block")
                    : e.DisplayIdExtension
                        ? Value.Unsupported<bool>(EdidSrc, "the EDID has a DisplayID extension, which is not decoded")
                        : Value.Ok(false, e.ExtensionBlocks == 0 ? "edid:no extension block" : "edid:CTA-861 (no HDR static metadata block)")),
            FromEdid(e => e.MaxLuminanceNits is { } n
                ? new Value<int>(n, ValueStatus.Ok, "edid:CTA-861 HDR static metadata block", "the peak the monitor declares, not a measurement")
                : Value.Unsupported<int>(EdidSrc, e.HdrStaticMetadataBlock ? "the HDR block does not give a peak brightness" : "no HDR metadata in EDID")));

        return new DisplayDevice(
            manufacturer,
            model,
            yearValue,
            weekValue,
            width,
            height,
            preferred,
            current,
            refresh,
            hdrEnabled,
            Connection(path.OutputTechnology),
            string.IsNullOrWhiteSpace(gpuName) ? Value.Unsupported<string>("dxgi:adapter luid", "the graphics adapter for this output was not found") : Value.Ok(gpuName, "dxgi:adapter luid joined to displayconfig adapter"),
            details);
    }

    private static Value<string> Serial(EdidInfo e)
    {
        if (!string.IsNullOrWhiteSpace(e.SerialText))
        {
            return Value.Ok(e.SerialText, "edid:serial number descriptor (0xFF)");
        }

        return e.NumericSerial is 0 or 1 or 0x01010101
            ? Value.Unsupported<string>(EdidSrc, "the monitor has no serial number set")
            : Value.Ok(e.NumericSerial.ToString(CultureInfo.InvariantCulture), "edid:base block serial number");
    }

    private static Value<double> Diagonal(EdidInfo e) =>
        e.PhysicalSizeMm is { } s
            ? Value.Ok(Math.Round(Math.Sqrt((double)s.Width * s.Width + (double)s.Height * s.Height) / 25.4, 1), "calculated from the EDID physical size")
            : Value.Unsupported<double>(EdidSrc, "no physical size in EDID");

    internal static Value<DisplayConnection> Connection(int technology)
    {
        // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY
        var kind = (uint)technology switch
        {
            0 => DisplayConnection.Vga,
            4 => DisplayConnection.Dvi,
            5 => DisplayConnection.Hdmi,
            10 or 18 => DisplayConnection.DisplayPort,
            6 or 11 or 13 or 0x80000000 => DisplayConnection.Internal,
            0xFFFFFFFF => DisplayConnection.Other,
            _ => DisplayConnection.Other,
        };
        return Value.Ok(kind, "displayconfig:outputTechnology");
    }

    private static bool IsGenericName(string name) =>
        name.Contains("Generic PnP", StringComparison.OrdinalIgnoreCase) || name.Contains("Generic Monitor", StringComparison.OrdinalIgnoreCase);

    /// <summary>The CCD API returns the two manufacturer bytes read as a little-endian word, so swap before decoding the 5-bit letters.</summary>
    internal static string? PnpFromWord(ushort word)
    {
        var be = (ushort)((word >> 8) | ((word & 0xFF) << 8));
        var a = (be >> 10) & 0x1F;
        var b = (be >> 5) & 0x1F;
        var c = be & 0x1F;
        if (a is < 1 or > 26 || b is < 1 or > 26 || c is < 1 or > 26)
        {
            return null;
        }

        return new string([(char)('@' + a), (char)('@' + b), (char)('@' + c)]);
    }
}
