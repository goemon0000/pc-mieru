namespace PcMieru.Core;

/// <summary>
/// The NVML clocks event reason bits (nvml.h, <c>nvmlClocksEventReason*</c>) that <see cref="GpuInfo.LimitReasons"/> holds. The mask itself is what is
/// kept and recorded; this only gives the documented bits a name for the screen. A set bit with no name here is reported as unknown
/// (<see cref="HasUnknown"/>): a reason is never guessed from a temperature or a power figure.
/// </summary>
public static class ClocksEventReasons
{
    /// <summary>Clocks are low because nothing is using the GPU (not a limit in the sense of being held back).</summary>
    public const ulong GpuIdle = 0x1;

    public const ulong ApplicationsClocksSetting = 0x2;

    /// <summary>Software power cap: the power limit is what holds the clocks.</summary>
    public const ulong SwPowerCap = 0x4;

    /// <summary>Hardware slowdown (the GPU slows itself down: temperature, power brake or supply).</summary>
    public const ulong HwSlowdown = 0x8;

    public const ulong SyncBoost = 0x10;
    public const ulong SwThermalSlowdown = 0x20;
    public const ulong HwThermalSlowdown = 0x40;
    public const ulong HwPowerBrakeSlowdown = 0x80;
    public const ulong DisplayClockSetting = 0x100;

    /// <summary>Every bit this class has a name for.</summary>
    public const ulong Known = 0x1FF;

    /// <summary>True when the driver set a bit this class does not know (a newer driver); it is shown as "other", never explained.</summary>
    public static bool HasUnknown(ulong mask) => (mask & ~Known) != 0;

    /// <summary>The ids of the named bits that are set, lowest bit first (stable ids; the screen turns them into words).</summary>
    public static IReadOnlyList<string> Ids(ulong mask)
    {
        if ((mask & Known) == 0)
        {
            return [];
        }

        var ids = new List<string>(4);
        foreach (var (bit, id) in Named)
        {
            if ((mask & bit) != 0)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static readonly (ulong Bit, string Id)[] Named =
    [
        (GpuIdle, "gpuIdle"),
        (ApplicationsClocksSetting, "applicationsClocksSetting"),
        (SwPowerCap, "swPowerCap"),
        (HwSlowdown, "hwSlowdown"),
        (SyncBoost, "syncBoost"),
        (SwThermalSlowdown, "swThermalSlowdown"),
        (HwThermalSlowdown, "hwThermalSlowdown"),
        (HwPowerBrakeSlowdown, "hwPowerBrakeSlowdown"),
        (DisplayClockSetting, "displayClockSetting"),
    ];
}
