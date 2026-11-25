using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;
using PcMieru.Core.Hardware.Storage;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>
/// The two reads behind <see cref="StorageTopology"/>, apart so a refresher can run them on different schedules. A null result means "could not
/// be read" (never an empty list standing in for it). Both are blocking, so they only run on a background thread.
/// </summary>
internal interface IStorageTopologyReader
{
    /// <summary>The physical drives and what kind each is.</summary>
    IReadOnlyList<StorageDrive>? ReadDrives();

    /// <summary>The local fixed volumes with their space; a volume on several drives is listed once with all of its drive numbers.</summary>
    IReadOnlyList<StorageVolumeUse>? ReadVolumes();
}

/// <summary>Reads the topology with the same access-0 IOCTLs the hardware page uses, so it needs no elevation.</summary>
internal sealed class WindowsStorageTopologyReader : IStorageTopologyReader
{
    public IReadOnlyList<StorageDrive>? ReadDrives()
    {
        try
        {
            var drives = new List<StorageDrive>();
            for (var n = 0; n < StorageCollector.MaxDisks; n++)
            {
                if (ReadDrive(n) is { } drive)
                {
                    drives.Add(drive);
                }
            }

            return drives;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public IReadOnlyList<StorageVolumeUse>? ReadVolumes()
    {
        try
        {
            return Fold(StorageCollector.ReadVolumes());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static StorageDrive? ReadDrive(int number)
    {
        using var handle = Kernel32.OpenDeviceNoAccess($@"\\.\PhysicalDrive{number}");
        if (handle.IsInvalid)
        {
            // A number nobody has is no drive; any other open error is a drive that exists but cannot be asked, so its kind is unknown.
            return StorageCollector.IsAbsent(Marshal.GetLastPInvokeError()) ? null : new StorageDrive(number, DiskKind.Unknown, null, "unreadable");
        }

        var descRaw = Ioctl.Call(handle, Ioctl.QueryProperty, StorageProtocol.BuildPropertyQuery(StorageProtocol.StorageDeviceProperty), 1024, out _);
        var desc = descRaw is null ? null : StorageProtocol.ParseDeviceDescriptor(descRaw);
        if (desc is null)
        {
            return new StorageDrive(number, DiskKind.Unknown, null, "unreadable");
        }

        var seekRaw = Ioctl.Call(handle, Ioctl.QueryProperty, StorageProtocol.BuildPropertyQuery(StorageProtocol.StorageDeviceSeekPenaltyProperty), 64, out _);
        var seek = seekRaw is null ? null : StorageProtocol.ParseSeekPenalty(seekRaw);
        var geometry = Ioctl.Call(handle, Ioctl.GetDriveGeometryEx, null, 256, out _);
        var size = geometry is null ? null : StorageProtocol.ParseGeometrySize(geometry);

        var model = StorageProtocol.JoinVendorModel(desc.Vendor, desc.Product);
        var identity = $"{model}|{StorageProtocol.CleanSerial(desc.Serial)}";
        return new StorageDrive(number, Classify(StorageCollector.MapBus(desc.BusType), desc.Removable, seek), size is > 0 ? size : null, identity);
    }

    /// <summary>
    /// The kind of a drive from what Windows says about it. NVMe is the bus. SATA, SCSI, RAID, USB and "other" buses are told apart by the seek
    /// penalty answer (a penalty is a spinning disk), and no answer is Unknown. Removable media and virtual, iSCSI and Storage Spaces disks are
    /// not local drives of a kind at all, and an unknown bus says nothing: all Unknown, never a guess.
    /// </summary>
    internal static DiskKind Classify(StorageBus bus, bool removable, bool? seekPenalty)
    {
        if (removable)
        {
            return DiskKind.Unknown;
        }

        return bus switch
        {
            StorageBus.Nvme => DiskKind.Nvme,
            StorageBus.Sata or StorageBus.Scsi or StorageBus.Raid or StorageBus.Usb or StorageBus.Other =>
                seekPenalty is { } penalty ? (penalty ? DiskKind.Hdd : DiskKind.Ssd) : DiskKind.Unknown,
            _ => DiskKind.Unknown,
        };
    }

    /// <summary>
    /// Local fixed volumes with a readable size, one entry per volume: the same volume under two letters has the same extents and is counted once
    /// (the first letter in A..Z order wins). A volume whose size could not be read is left out, not counted as empty.
    /// </summary>
    internal static IReadOnlyList<StorageVolumeUse> Fold(IEnumerable<StorageCollector.VolumeInfo> volumes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<StorageVolumeUse>();
        foreach (var v in volumes)
        {
            if (!v.Fixed || v.Extents.Count == 0 || !v.Total.TryGet(out var total) || !v.Free.TryGet(out var free) || total <= 0 || free < 0 || free > total)
            {
                continue;
            }

            if (!seen.Add(ExtentKey(v.Extents)))
            {
                continue;
            }

            var drives = v.Extents.Select(static e => e.Disk).Distinct().Order().ToArray();
            result.Add(new StorageVolumeUse(v.Letter, total, free, drives));
        }

        return result;
    }

    private static string ExtentKey(IReadOnlyList<(int Disk, long Start, long Length)> extents)
    {
        var sb = new StringBuilder();
        foreach (var e in extents.OrderBy(static e => e.Disk).ThenBy(static e => e.Start))
        {
            sb.Append(e.Disk.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(e.Start.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(e.Length.ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        return sb.ToString();
    }
}
