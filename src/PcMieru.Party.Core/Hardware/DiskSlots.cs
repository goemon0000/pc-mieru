using PcMieru.Core;

namespace PcMieru.Party.Hardware;

/// <summary>
/// One disk slot of the old Parts Life board: the key the saved game knows it by, the character, the title, and the throughput that fills its bar.
/// </summary>
/// <param name="Key">The old slot key (the save keys experience by it). Kept exactly.</param>
/// <param name="FullScaleBytesPerSec">The read + write rate (bytes per second) that counts as full load for this slot.</param>
public sealed record DiskSlotSpec(string Key, string CharacterId, string Title, double FullScaleBytesPerSec)
{
    /// <summary>
    /// The share of full load, 0..1, for a read + write rate. A rate that is not a rate (NaN, infinite or negative) is null, never 0: a bad number
    /// is missing, and a caller keeps it missing instead of drawing an idle disk. Zero is a real rate and gives 0.
    /// </summary>
    public double? Load(double bytesPerSec) =>
        double.IsFinite(bytesPerSec) && bytesPerSec >= 0 ? Math.Clamp(bytesPerSec / FullScaleBytesPerSec, 0, 1) : null;
}

/// <summary>
/// The old board's disk slots and their scales (Parts Life Composition.cs at 25e4571, lines 30-90, and MainWindow.xaml.cs OnSensorTick), kept
/// unchanged. Each kind is scaled to what that kind can do, so an NVMe is not always pegged and a hard disk is not always idle. The totals are
/// not split across kinds: a kind's rate is the sum of the disks of that kind (<see cref="BoardDiskKind.BytesPerSec"/>).
/// </summary>
public static class DiskSlots
{
    private const double MiB = 1024.0 * 1024.0;

    /// <summary>NVMe: key "m2", titled "M.2", full at 2000 MiB/s.</summary>
    public static DiskSlotSpec Nvme { get; } = new("m2", "m2", "M.2", 2000 * MiB);

    /// <summary>A SATA-style SSD: key "ssd", full at 500 MiB/s.</summary>
    public static DiskSlotSpec Ssd { get; } = new("ssd", "ssd", "SSD", 500 * MiB);

    /// <summary>A hard disk: key "hdd", full at 120 MiB/s.</summary>
    public static DiskSlotSpec Hdd { get; } = new("hdd", "hdd", "HDD", 120 * MiB);

    /// <summary>
    /// The single slot the old board stood up when it could tell no disk's kind: key "ssd" (the same key as <see cref="Ssd"/>; the two never
    /// stand together), titled "DISK", full at 500 MiB/s of the whole machine's read + write. Use it only when no kind slot exists.
    /// </summary>
    public static DiskSlotSpec Plain { get; } = new("ssd", "ssd", "DISK", 500 * MiB);

    /// <summary>
    /// The scale of the whole-machine disk number in the experience formula (the old <c>Work.Disk</c>): read + write over 200 MiB/s, clamped to 0..1.
    /// Not a slot scale: a drive's real speed is several GB/s, which would leave everyday use looking idle.
    /// </summary>
    public const double WorkScaleBytesPerSec = 200 * MiB;

    /// <summary>The slot of a drive kind; null for <see cref="DiskKind.Unknown"/>, which has none (its I/O is only in the plain DISK total).</summary>
    public static DiskSlotSpec? For(DiskKind kind) => kind switch
    {
        DiskKind.Nvme => Nvme,
        DiskKind.Ssd => Ssd,
        DiskKind.Hdd => Hdd,
        _ => null,
    };
}
