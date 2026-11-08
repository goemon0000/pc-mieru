using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace PcMieru.Core.Hardware.Native;

/// <summary>One active display path: a monitor on one GPU output, as the Windows display configuration (CCD) API lists it.</summary>
internal sealed record ActiveDisplayPath(
    uint AdapterLuidLow,
    int AdapterLuidHigh,
    uint SourceId,
    uint TargetId,
    int OutputTechnology,
    double? RefreshHz,
    int? Width,
    int? Height,
    string? FriendlyName,
    string? DevicePath,
    ushort EdidManufacturerId,
    ushort EdidProductCode,
    bool EdidIdsValid)
{
    public string AdapterLuidText => $"luid_0x{(uint)AdapterLuidHigh:x8}_0x{AdapterLuidLow:x8}";
}

/// <summary>HDR / wide-colour state of one monitor as Windows reports it.</summary>
internal sealed record AdvancedColorState(bool HdrSupported, bool HdrEnabled, bool? WideColorSupported, int? BitsPerColor, string Source, string? Detail);

internal static partial class DisplayConfigNative
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [In, Out] byte[] paths, ref uint numModes, [In, Out] byte[] modes, IntPtr topology);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int DisplayConfigGetDeviceInfo([In, Out] byte[] packet);
}

/// <summary>
/// QueryDisplayConfig / DisplayConfigGetDeviceInfo, read only. Structures are read by fixed offset from byte buffers
/// (DISPLAYCONFIG_PATH_INFO 72 bytes, DISPLAYCONFIG_MODE_INFO 64 bytes, x64 layout).
/// </summary>
internal static class DisplayConfig
{
    private const uint QdcOnlyActivePaths = 2;
    private const int PathSize = 72;
    private const int ModeSize = 64;
    private const int ErrorInsufficientBuffer = 122;
    private const uint InvalidModeIndex = 0xFFFFFFFF;

    public static IReadOnlyList<ActiveDisplayPath>? Query(out int error)
    {
        byte[] paths = [];
        byte[] modes = [];
        uint nPaths = 0, nModes = 0;
        error = ErrorInsufficientBuffer;
        for (var attempt = 0; attempt < 4 && error == ErrorInsufficientBuffer; attempt++)
        {
            error = DisplayConfigNative.GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out nPaths, out nModes);
            if (error != 0)
            {
                return null;
            }

            paths = new byte[(int)nPaths * PathSize];
            modes = new byte[(int)nModes * ModeSize];
            error = DisplayConfigNative.QueryDisplayConfig(QdcOnlyActivePaths, ref nPaths, paths, ref nModes, modes, IntPtr.Zero);
        }

        if (error != 0)
        {
            return null;
        }

        var list = new List<ActiveDisplayPath>();
        for (var i = 0; i < nPaths; i++)
        {
            var p = paths.AsSpan(i * PathSize, PathSize);
            var sourceId = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
            var sourceModeIdx = BinaryPrimitives.ReadUInt32LittleEndian(p[12..]);
            var targetLow = BinaryPrimitives.ReadUInt32LittleEndian(p[20..]);
            var targetHigh = BinaryPrimitives.ReadInt32LittleEndian(p[24..]);
            var targetId = BinaryPrimitives.ReadUInt32LittleEndian(p[28..]);
            var technology = BinaryPrimitives.ReadInt32LittleEndian(p[36..]);
            var rateNum = BinaryPrimitives.ReadUInt32LittleEndian(p[48..]);
            var rateDen = BinaryPrimitives.ReadUInt32LittleEndian(p[52..]);

            int? width = null, height = null;
            if (sourceModeIdx != InvalidModeIndex && sourceModeIdx < nModes)
            {
                var m = modes.AsSpan((int)sourceModeIdx * ModeSize, ModeSize);
                if (BinaryPrimitives.ReadUInt32LittleEndian(m) == 1)
                {
                    width = (int)BinaryPrimitives.ReadUInt32LittleEndian(m[16..]);
                    height = (int)BinaryPrimitives.ReadUInt32LittleEndian(m[20..]);
                }
            }

            var (name, path, mfg, product, idsValid) = TargetName(targetLow, targetHigh, targetId);
            list.Add(new ActiveDisplayPath(
                targetLow,
                targetHigh,
                sourceId,
                targetId,
                technology,
                rateNum > 0 && rateDen > 0 ? Math.Round((double)rateNum / rateDen, 2) : null,
                width,
                height,
                name,
                path,
                mfg,
                product,
                idsValid));
        }

        return list;
    }

    private static (string? Name, string? Path, ushort Manufacturer, ushort Product, bool IdsValid) TargetName(uint luidLow, int luidHigh, uint targetId)
    {
        var buf = new byte[420];
        WriteHeader(buf, 2, luidLow, luidHigh, targetId);
        if (DisplayConfigNative.DisplayConfigGetDeviceInfo(buf) != 0)
        {
            return (null, null, 0, 0, false);
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(20));
        var mfg = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(28));
        var product = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(30));
        var name = Wide(buf.AsSpan(36, 128));
        var path = Wide(buf.AsSpan(164, 256));
        return (name, path, mfg, product, (flags & 0x4) != 0);
    }

    /// <summary>
    /// HDR state. Windows 11 24H2 and later answer GET_ADVANCED_COLOR_INFO_2 (separates HDR from wide colour); older systems only
    /// know GET_ADVANCED_COLOR_INFO, whose "enabled" bit means HDR there.
    /// </summary>
    public static AdvancedColorState? Advanced(uint luidLow, int luidHigh, uint targetId)
    {
        var v2 = new byte[36];
        WriteHeader(v2, 15, luidLow, luidHigh, targetId);
        if (DisplayConfigNative.DisplayConfigGetDeviceInfo(v2) == 0)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(v2.AsSpan(20));
            var bits = (int)BinaryPrimitives.ReadUInt32LittleEndian(v2.AsSpan(28));
            var mode = BinaryPrimitives.ReadUInt32LittleEndian(v2.AsSpan(32));
            return new AdvancedColorState(
                (value & 0x10) != 0,
                mode == 2,
                (value & 0x40) != 0,
                bits is > 0 and <= 16 ? bits : null,
                "displayconfig:DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2",
                "bits per colour channel is the current signal, not the panel's capability");
        }

        var v1 = new byte[32];
        WriteHeader(v1, 9, luidLow, luidHigh, targetId);
        if (DisplayConfigNative.DisplayConfigGetDeviceInfo(v1) == 0)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(v1.AsSpan(20));
            var bits = (int)BinaryPrimitives.ReadUInt32LittleEndian(v1.AsSpan(28));
            return new AdvancedColorState(
                (value & 0x1) != 0,
                (value & 0x2) != 0,
                null,
                bits is > 0 and <= 16 ? bits : null,
                "displayconfig:DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO",
                "older Windows: advanced colour may include wide colour without HDR");
        }

        return null;
    }

    private static void WriteHeader(byte[] buf, int type, uint luidLow, int luidHigh, uint id)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0), type);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)buf.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), luidLow);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12), luidHigh);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), id);
    }

    private static string? Wide(ReadOnlySpan<byte> field)
    {
        var s = Encoding.Unicode.GetString(field);
        var end = s.IndexOf('\0');
        s = (end < 0 ? s : s[..end]).Trim();
        return s.Length == 0 ? null : s;
    }
}
