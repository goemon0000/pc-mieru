using System.Text.Json.Serialization;
using PcMieru.Wire;

namespace PcMieru.Core;

/// <summary>
/// What kind of storage a physical drive is, for the Parts Life board (the old slots m2, ssd and hdd). <see cref="Unknown"/> is any drive whose
/// kind Core could not confirm (unreadable, removable, virtual, a Storage Spaces disk, no seek-penalty answer): it is never guessed into a kind.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<DiskKind>))]
public enum DiskKind
{
    Unknown,
    Nvme,
    Ssd,
    Hdd,
}

/// <summary>One physical drive as the storage topology knows it. <paramref name="Identity"/> is the drive's own model and serial, so a number that now belongs to another drive reads as a change.</summary>
public sealed record StorageDrive(int Number, DiskKind Kind, long? CapacityBytes, string Identity);

/// <summary>
/// One local fixed volume, deduplicated (the same volume under two letters is one entry). <paramref name="DriveNumbers"/> are the physical drives
/// the volume has space on, ascending and distinct; more than one means it spans disks and belongs to none of them alone.
/// </summary>
public sealed record StorageVolumeUse(string Letter, long TotalBytes, long FreeBytes, IReadOnlyList<int> DriveNumbers)
{
    public long UsedBytes => TotalBytes - FreeBytes;

    /// <summary>True when the volume is on several physical drives, or on none that is known.</summary>
    public bool SpansDrives => DriveNumbers.Count != 1;
}

/// <summary>
/// The machine's physical drives and local volumes, as a background reader last saw them (see <c>StorageTopologyRefresher</c>). It exists so the
/// Parts Life board can tie a PhysicalDisk row (whose id is <c>PhysicalDrive&lt;n&gt;</c>) to a drive kind by drive number. Public because that board
/// reads it from another assembly; kept out of the JSON with an attribute on <see cref="SystemInfo.Storage"/>, like <see cref="CpuInfo.Sockets"/>.
/// </summary>
/// <param name="Drives">Every physical drive that was found, ascending by number.</param>
/// <param name="Volumes">The local fixed volumes; null when they could not be read (not the same as "there are none").</param>
public sealed record StorageTopology(IReadOnlyList<StorageDrive> Drives, IReadOnlyList<StorageVolumeUse>? Volumes)
{
    /// <summary>The volumes that sit wholly on this one drive (a volume that spans drives is on none of them). Empty when the volumes were not read.</summary>
    public IEnumerable<StorageVolumeUse> VolumesOn(int driveNumber) =>
        Volumes?.Where(v => !v.SpansDrives && v.DriveNumbers[0] == driveNumber) ?? [];

    /// <summary>The number in a <see cref="DiskInfo.Id"/> (<c>PhysicalDrive3</c> is 3); false for any other id.</summary>
    public static bool TryDriveNumber(string? diskId, out int number)
    {
        number = 0;
        const string prefix = "PhysicalDrive";
        return diskId is not null
            && diskId.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(diskId.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number);
    }
}
