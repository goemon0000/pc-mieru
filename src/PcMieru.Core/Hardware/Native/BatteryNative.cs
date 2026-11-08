using System.Buffers.Binary;
using System.Text;

namespace PcMieru.Core.Hardware.Native;

/// <summary>GetSystemPowerStatus as Windows returns it. 255 means "unknown" in the byte fields and 0xFFFFFFFF in the time field.</summary>
internal sealed record RawPowerStatus(byte AcLine, byte BatteryFlag, byte BatteryPercent, uint LifeSeconds);

/// <summary>One battery-class device (a laptop battery, or a UPS) as its driver reports it. The capability flags tell the two apart.</summary>
internal sealed record RawBattery(
    uint Capabilities,
    string Chemistry,
    uint DesignedCapacity,
    uint FullChargedCapacity,
    uint CycleCount,
    uint? PowerState,
    uint? Voltage);

/// <summary>Read-only battery queries: GetSystemPowerStatus and the battery class IOCTLs. Nothing is written to the battery.</summary>
internal static class BatteryNative
{
    public const uint CapSystemBattery = 0x80000000;
    public const uint CapRelative = 0x40000000;
    public const uint Unknown = 0xFFFFFFFF;

    private const uint IoctlQueryTag = 0x294040;
    private const uint IoctlQueryInformation = 0x294044;
    private const uint IoctlQueryStatus = 0x29404C;
    private static readonly Guid BatteryInterface = new("72631E54-78A4-11d0-BCF7-00AA00B7B32A");

    public static RawPowerStatus? ReadPowerStatus(out int error)
    {
        error = 0;
        if (!Kernel32.GetSystemPowerStatus(out var s))
        {
            error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            return null;
        }

        return new RawPowerStatus(s.AcLineStatus, s.BatteryFlag, s.BatteryLifePercent, s.BatteryLifeTime);
    }

    /// <summary>Every battery-class device that currently has a battery in it. Empty on a desktop without a UPS. Null when the device list cannot be opened.</summary>
    public static List<RawBattery>? ReadBatteries()
    {
        using var set = DeviceInfoSet.ForInterface(BatteryInterface);
        if (set is null)
        {
            return null;
        }

        var list = new List<RawBattery>();
        foreach (var (path, _) in set.Interfaces(BatteryInterface))
        {
            using var handle = Kernel32.OpenDeviceRead(path);
            if (handle.IsInvalid)
            {
                continue;
            }

            // Wait timeout 0: answer now. A tag of 0 means the slot is empty.
            var tagBytes = Ioctl.Call(handle, IoctlQueryTag, new byte[4], 4, out _);
            var tag = tagBytes is { Length: >= 4 } ? BinaryPrimitives.ReadUInt32LittleEndian(tagBytes) : 0u;
            if (tag == 0)
            {
                continue;
            }

            var info = Ioctl.Call(handle, IoctlQueryInformation, QueryInformation(tag, level: 0), 36, out _);
            var status = Ioctl.Call(handle, IoctlQueryStatus, WaitStatus(tag), 16, out _);
            if (ParseInformation(info) is { } battery)
            {
                var (state, voltage) = ParseStatus(status);
                list.Add(battery with { PowerState = state, Voltage = voltage });
            }
        }

        return list;
    }

    /// <summary>BATTERY_QUERY_INFORMATION: tag, information level, at-rate.</summary>
    public static byte[] QueryInformation(uint tag, uint level)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(b, tag);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), level);
        return b;
    }

    /// <summary>BATTERY_WAIT_STATUS with a zero timeout, so the driver answers at once.</summary>
    public static byte[] WaitStatus(uint tag)
    {
        var b = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(b, tag);
        return b;
    }

    /// <summary>BATTERY_INFORMATION (36 bytes). Null when the answer is too short.</summary>
    public static RawBattery? ParseInformation(ReadOnlySpan<byte> b)
    {
        if (b.Length < 36)
        {
            return null;
        }

        var chem = Encoding.ASCII.GetString(b.Slice(8, 4)).Trim('\0', ' ');
        return new RawBattery(
            BinaryPrimitives.ReadUInt32LittleEndian(b),
            new string(chem.Where(c => c is >= ' ' and <= '~').ToArray()),
            BinaryPrimitives.ReadUInt32LittleEndian(b[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[32..]),
            null,
            null);
    }

    /// <summary>BATTERY_STATUS (16 bytes): power state flags and voltage in mV. Both null when the answer is too short.</summary>
    public static (uint? PowerState, uint? Voltage) ParseStatus(ReadOnlySpan<byte> b) =>
        b.Length < 16 ? (null, null) : (BinaryPrimitives.ReadUInt32LittleEndian(b), BinaryPrimitives.ReadUInt32LittleEndian(b[8..]));
}
