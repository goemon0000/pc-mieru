using System.Runtime.InteropServices;
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

/// <summary>
/// One volume as the enumeration saw it, before it is folded into a <see cref="StorageVolumeUse"/>. <paramref name="Guid"/> is the volume GUID
/// path (<c>\\?\Volume{...}\</c>), which names one volume however many letters or folders it is mounted on. <paramref name="Total"/> and
/// <paramref name="Free"/> are null when the size could not be read; <paramref name="Drives"/> is null when the disk extents could not be read.
/// </summary>
internal sealed record VolumeProbe(string Guid, string Label, bool Fixed, long? Total, long? Free, IReadOnlyList<int>? Drives);

/// <summary>
/// What the reader remembers of a drive between passes, keyed by its identity (model and serial): only the seek-penalty answer, because a disk
/// never changes between spinning and not spinning while it stays. The size is not remembered (a RAID logical disk can grow without a new model
/// or serial), so it is asked every pass. An answer that was not given is not remembered (it is asked again next time), and a drive that is
/// not <paramref name="cacheable"/> (no serial, or a bus where the serial does not name the disk) is never remembered at all.
/// </summary>
internal sealed class SeekPenaltyCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _seek = new(StringComparer.Ordinal);

    /// <summary>Returns the seek-penalty answer of the drive, asking only when it is not already known.</summary>
    public bool? Resolve(string identity, bool cacheable, Func<bool?> askSeek)
    {
        if (!cacheable)
        {
            return askSeek();
        }

        lock (_gate)
        {
            if (_seek.TryGetValue(identity, out var known))
            {
                return known;
            }

            var seek = askSeek();
            if (seek is { } answer)
            {
                _seek[identity] = answer;
            }

            return seek;
        }
    }
}

/// <summary>
/// Reads the topology with the same access-0 IOCTLs the hardware page uses, so it needs no elevation. Every query here is a property query
/// answered by the storage stack (the device descriptor, the seek-penalty property, the geometry, the volume's disk extents, the volume's free
/// space as the file system keeps it): none of them reads the media or wakes a sleeping disk. The seek penalty is asked once per drive (on the
/// buses where the serial names the disk) and remembered, so a 60-second pass sends the descriptor and geometry queries per drive and the seek
/// query only for a drive not seen before.
/// </summary>
internal sealed class WindowsStorageTopologyReader : IStorageTopologyReader
{
    private const uint DriveFixed = 3;

    private readonly SeekPenaltyCache _seekCache = new();

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
            return EnumerateVolumes() is { } found ? Fold(found) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private StorageDrive? ReadDrive(int number)
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

        return Describe(
            number,
            desc,
            _seekCache,
            () =>
            {
                var seekRaw = Ioctl.Call(handle, Ioctl.QueryProperty, StorageProtocol.BuildPropertyQuery(StorageProtocol.StorageDeviceSeekPenaltyProperty), 64, out _);
                return seekRaw is null ? null : StorageProtocol.ParseSeekPenalty(seekRaw);
            },
            () =>
            {
                var geometry = Ioctl.Call(handle, Ioctl.GetDriveGeometryEx, null, 256, out _);
                return geometry is null ? null : StorageProtocol.ParseGeometrySize(geometry);
            });
    }

    /// <summary>
    /// A drive from its device descriptor, asking the seek-penalty and size probes only when they are needed. A removable drive, and a bus that is
    /// Unknown whatever the seek answer, is Unknown at once and neither probe is sent; a bus whose kind does not depend on the seek answer (NVMe)
    /// is not asked for it. The seek answer of a drive seen before (<paramref name="seekCache"/>) is not asked again, but only on a bus where
    /// the serial names the disk itself and only with a serial; the size is asked every pass, since a logical disk can grow under the same name.
    /// </summary>
    internal static StorageDrive Describe(int number, DeviceDescriptor desc, SeekPenaltyCache seekCache, Func<bool?> askSeek, Func<long?> askSize)
    {
        var serial = StorageProtocol.CleanSerial(desc.Serial);
        var identity = $"{StorageProtocol.JoinVendorModel(desc.Vendor, desc.Product)}|{serial}";
        var bus = StorageCollector.MapBus(desc.BusType);
        if (!HasLocalKind(bus, desc.Removable))
        {
            return new StorageDrive(number, DiskKind.Unknown, null, identity);
        }

        var seek = AsksSeekPenalty(bus) ? seekCache.Resolve(identity, serial.Length > 0 && SerialNamesTheDisk(bus), askSeek) : null;
        return new StorageDrive(number, Classify(bus, desc.Removable, seek), Positive(askSize()), identity);
    }

    private static long? Positive(long? value) => value is > 0 ? value : null;

    /// <summary>
    /// True on the buses where the serial in the descriptor is the disk's own (NVMe, SATA, SCSI, RAID), so what was learned of that model and
    /// serial holds for the disk. A USB bridge or an "other" bus can report one model and serial for every enclosure, so nothing is remembered there.
    /// </summary>
    internal static bool SerialNamesTheDisk(StorageBus bus) =>
        bus is StorageBus.Nvme or StorageBus.Sata or StorageBus.Scsi or StorageBus.Raid;

    /// <summary>False for a drive <see cref="Classify"/> calls Unknown whatever the seek answer: removable media, and virtual, iSCSI, Storage Spaces and unknown buses.</summary>
    internal static bool HasLocalKind(StorageBus bus, bool removable) =>
        !removable && bus is StorageBus.Nvme or StorageBus.Sata or StorageBus.Scsi or StorageBus.Raid or StorageBus.Usb or StorageBus.Other;

    /// <summary>True for the buses where <see cref="Classify"/> tells an SSD from a hard disk by the seek penalty (NVMe is the bus alone).</summary>
    internal static bool AsksSeekPenalty(StorageBus bus) =>
        bus is StorageBus.Sata or StorageBus.Scsi or StorageBus.Raid or StorageBus.Usb or StorageBus.Other;

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
    /// Local fixed volumes with a readable size, one entry per volume GUID: the same volume under two letters, or a letter and a folder, is one
    /// volume and is counted once (the first one seen wins). A volume whose size could not be read is left out, not counted as empty. A volume
    /// whose drives could not be read stays, with no drive numbers: it counts for the fullest volume but belongs to no drive kind. Removable media
    /// is not part of the board, and neither is a volume with no drive letter and no mount folder (<see cref="IsOnBoard"/>; the enumeration leaves
    /// it out before it gets here).
    /// </summary>
    internal static IReadOnlyList<StorageVolumeUse> Fold(IEnumerable<VolumeProbe> volumes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<StorageVolumeUse>();
        foreach (var v in volumes)
        {
            if (!v.Fixed || v.Total is not { } total || v.Free is not { } free || total <= 0 || free < 0 || free > total)
            {
                continue;
            }

            if (!seen.Add(v.Guid))
            {
                continue;
            }

            var drives = (v.Drives ?? []).Distinct().Order().ToArray();
            result.Add(new StorageVolumeUse(v.Label, total, free, drives));
        }

        return result;
    }

    /// <summary>
    /// False for a volume with no path names (no drive letter and no mount folder), which does not count; true when it has some, and true when
    /// the names could not be read (<c>null</c>), because a volume that cannot be told is not dropped.
    /// </summary>
    internal static bool IsOnBoard(IReadOnlyList<string>? pathNames) => pathNames is null || pathNames.Count > 0;

    /// <summary>
    /// How a volume is shown: its first drive letter ("C:"), or else its first mount folder, or else its GUID path. The trailing backslash of a
    /// letter or a folder is left off.
    /// </summary>
    internal static string DisplayLabel(IReadOnlyList<string> pathNames, string guidPath)
    {
        foreach (var path in pathNames)
        {
            if (path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
            {
                return path[..2];
            }
        }

        return pathNames.Count > 0 ? pathNames[0].TrimEnd('\\') : guidPath;
    }

    /// <summary>The strings of a multi-string buffer (each ended by a null, the list by an empty one); empty strings are dropped.</summary>
    internal static List<string> SplitMultiString(ReadOnlySpan<char> buffer)
    {
        var list = new List<string>();
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
            {
                continue;
            }

            if (i > start)
            {
                list.Add(new string(buffer[start..i]));
            }

            start = i + 1;
        }

        return list;
    }

    /// <summary>
    /// Every volume Windows has, by FindFirstVolume and FindNextVolume, so a volume with no drive letter is seen. Null when the walk could not
    /// start or stopped early: a partial list could hide the fullest volume, so it is "could not be read", not a shorter list.
    /// </summary>
    private static List<VolumeProbe>? EnumerateVolumes()
    {
        var name = new char[261];
        var find = Kernel32.FindFirstVolumeW(name, (uint)name.Length);
        if (find == Kernel32.InvalidFindHandle)
        {
            return null;
        }

        try
        {
            var list = new List<VolumeProbe>();
            do
            {
                var length = Array.IndexOf(name, '\0');
                if (ProbeVolume(new string(name, 0, length < 0 ? name.Length : length)) is { } probe)
                {
                    list.Add(probe);
                }
            }
            while (Kernel32.FindNextVolumeW(find, name, (uint)name.Length));

            return Marshal.GetLastPInvokeError() == Kernel32.ErrorNoMoreFiles ? list : null;
        }
        finally
        {
            Kernel32.FindVolumeClose(find);
        }
    }

    /// <summary>
    /// One volume, or null for a volume that is not on the board because it has no path names at all (no drive letter and no mount folder: the
    /// recovery, system and other partitions nobody can open). A volume whose names could not be read is kept, labelled by its GUID: not being
    /// able to tell is not the same as having none.
    /// </summary>
    private static VolumeProbe? ProbeVolume(string guidPath)
    {
        var type = Kernel32Errors.Quiet(() => Kernel32.GetDriveTypeW(guidPath));
        if (type != DriveFixed)
        {
            // Removable, remote, optical and the rest are not on the board: no further call touches them.
            return new VolumeProbe(guidPath, guidPath, false, null, null, null);
        }

        var pathNames = PathNames(guidPath);
        if (!IsOnBoard(pathNames))
        {
            return null;
        }

        ulong avail = 0;
        ulong tot = 0;
        ulong totFree = 0;
        var spaceOk = Kernel32Errors.Quiet(() => Kernel32.GetDiskFreeSpaceExW(guidPath, out avail, out tot, out totFree));
        long? total = spaceOk && tot <= long.MaxValue ? (long)tot : null;
        long? free = spaceOk && totFree <= long.MaxValue ? (long)totFree : null;
        return new VolumeProbe(guidPath, DisplayLabel(pathNames ?? [], guidPath), true, total, free, ReadDriveNumbers(guidPath));
    }

    /// <summary>The drives a volume has space on, ascending; null when they could not be read (the volume is then on no drive that is known).</summary>
    private static List<int>? ReadDriveNumbers(string guidPath)
    {
        // The volume is opened by its GUID path without the trailing backslash (with it, the root folder would be opened), with access 0 as the drives are.
        using var handle = Kernel32.OpenDeviceNoAccess(guidPath.TrimEnd('\\'));
        if (handle.IsInvalid)
        {
            return null;
        }

        var raw = Ioctl.Call(handle, Ioctl.GetVolumeDiskExtents, null, 8 + (24 * 16), out _);
        var extents = raw is null ? [] : StorageProtocol.ParseVolumeExtents(raw);
        return extents.Count == 0 ? null : extents.Select(static e => e.Disk).Distinct().Order().ToList();
    }

    /// <summary>The drive letters and mount folders of a volume (empty when it has none); null when they could not be read.</summary>
    private static List<string>? PathNames(string guidPath)
    {
        var buffer = new char[260];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (Kernel32.GetVolumePathNamesForVolumeNameW(guidPath, buffer, (uint)buffer.Length, out var length))
            {
                return SplitMultiString(buffer.AsSpan(0, (int)Math.Min(length, (uint)buffer.Length)));
            }

            if (Marshal.GetLastPInvokeError() != Kernel32.ErrorMoreData || length is 0 or > 32768)
            {
                break;
            }

            buffer = new char[length];
        }

        return null;
    }
}
