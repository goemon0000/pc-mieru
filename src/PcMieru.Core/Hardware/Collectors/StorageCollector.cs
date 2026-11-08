using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;
using PcMieru.Core.Hardware.Storage;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>
/// Physical disks: identity from IOCTL_STORAGE_QUERY_PROPERTY (access 0 handle, works for a standard user), partitions and volumes
/// from the disk/volume IOCTLs, driver and PCIe link from the PnP database.
/// </summary>
internal static class StorageCollector
{
    private const string Source = "ioctl:StorageDeviceProperty";
    private const int MaxDisks = 64;
    private static readonly Guid DiskInterface = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    private sealed record VolumeInfo(string Letter, string Label, string FileSystem, Value<long> Total, Value<long> Free, IReadOnlyList<(int Disk, long Start, long Length)> Extents, string? Style);

    public static Value<IReadOnlyList<StorageDevice>> Collect()
    {
        try
        {
            var volumes = ReadVolumes();
            var nodes = DiskDevNodes();
            var list = new List<StorageDevice>();
            for (var n = 0; n < MaxDisks; n++)
            {
                var device = ReadDrive(n, volumes, nodes);
                if (device is not null)
                {
                    list.Add(device);
                }
            }

            return Value.Ok<IReadOnlyList<StorageDevice>>(list, "composite:storage");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<IReadOnlyList<StorageDevice>>(ex, "composite:storage");
        }
    }

    /// <summary>Highest-numbered physical drive that exists; the Helper and health reader use the same walk.</summary>
    public static IEnumerable<int> PhysicalDriveNumbers()
    {
        for (var n = 0; n < MaxDisks; n++)
        {
            using var h = Kernel32.OpenDeviceNoAccess($@"\\.\PhysicalDrive{n}");
            if (!h.IsInvalid)
            {
                yield return n;
            }
        }
    }

    private static Dictionary<int, uint> DiskDevNodes()
    {
        var map = new Dictionary<int, uint>();
        try
        {
            using var set = DeviceInfoSet.ForInterface(DiskInterface);
            if (set is null)
            {
                return map;
            }

            foreach (var (path, node) in set.Interfaces(DiskInterface))
            {
                using var h = Kernel32.OpenDeviceNoAccess(path);
                if (h.IsInvalid)
                {
                    continue;
                }

                var number = Ioctl.Call(h, Ioctl.GetDeviceNumber, null, 12, out _);
                if (number is { Length: >= 8 })
                {
                    map[(int)BinaryPrimitives.ReadUInt32LittleEndian(number.AsSpan(4))] = node.DevInst;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The PnP details are optional; identity from the IOCTLs below does not depend on them.
        }

        return map;
    }

    private static List<VolumeInfo> ReadVolumes()
    {
        var result = new List<VolumeInfo>();
        var mask = Kernel32.GetLogicalDrives();
        for (var i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) == 0)
            {
                continue;
            }

            var letter = $"{(char)('A' + i)}:";
            var root = letter + "\\";
            var type = Kernel32Errors.Quiet(() => Kernel32.GetDriveTypeW(root));
            if (type is not (2 or 3))
            {
                continue;
            }

            using var handle = Kernel32.OpenDeviceNoAccess($@"\\.\{letter}");
            if (handle.IsInvalid)
            {
                continue;
            }

            var extentsRaw = Ioctl.Call(handle, Ioctl.GetVolumeDiskExtents, null, 8 + (24 * 16), out _);
            var extents = extentsRaw is null ? [] : StorageProtocol.ParseVolumeExtents(extentsRaw);
            if (extents.Count == 0)
            {
                continue;
            }

            var infoRaw = Ioctl.Call(handle, Ioctl.GetPartitionInfoEx, null, 144, out _);
            var style = infoRaw is null ? null : DriveLayout.ParseSingle(infoRaw)?.Style;

            var label = new char[261];
            var fs = new char[261];
            var ready = Kernel32Errors.Quiet(() => Kernel32.GetVolumeInformationW(root, label, (uint)label.Length, out _, out _, out _, fs, (uint)fs.Length));
            ulong avail = 0;
            ulong tot = 0;
            ulong totFree = 0;
            var (spaceOk, spaceError) = Kernel32Errors.Quiet(() => (Kernel32.GetDiskFreeSpaceExW(root, out avail, out tot, out totFree), Marshal.GetLastPInvokeError()));
            var total = spaceOk ? Value.Ok((long)tot, "winapi:GetDiskFreeSpaceExW") : Probe.FromWin32<long>(spaceError, "winapi:GetDiskFreeSpaceExW", "volume size");
            var free = spaceOk ? Value.Ok((long)totFree, "winapi:GetDiskFreeSpaceExW") : Probe.FromWin32<long>(spaceError, "winapi:GetDiskFreeSpaceExW", "free space");

            result.Add(new VolumeInfo(
                letter,
                ready ? new string(label).TrimEnd('\0') : string.Empty,
                ready ? new string(fs).TrimEnd('\0') : string.Empty,
                total,
                free,
                extents,
                style));
        }

        return result;
    }

    private static StorageBus MapBus(int busType) => busType switch
    {
        17 => StorageBus.Nvme,
        3 or 11 => StorageBus.Sata,
        7 => StorageBus.Usb,
        8 => StorageBus.Raid,
        1 or 10 => StorageBus.Scsi,
        0 => StorageBus.Unknown,
        _ => StorageBus.Other,
    };

    private static StorageDevice? ReadDrive(int number, List<VolumeInfo> volumes, Dictionary<int, uint> nodes)
    {
        var path = $@"\\.\PhysicalDrive{number}";
        using var handle = Kernel32.OpenDeviceNoAccess(path);
        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err is Probe.ErrorFileNotFound or 3)
            {
                return null;
            }

            return Unreadable(number, Probe.FromWin32<string>(err, Source, "the drive"));
        }

        var descRaw = Ioctl.Call(handle, Ioctl.QueryProperty, StorageProtocol.BuildPropertyQuery(StorageProtocol.StorageDeviceProperty), 1024, out var descError);
        var desc = descRaw is null ? null : StorageProtocol.ParseDeviceDescriptor(descRaw);
        if (desc is null)
        {
            return Unreadable(number, descRaw is null ? Probe.FromWin32<string>(descError, Source, "the device descriptor") : Value.Failed<string>(Source, "malformed device descriptor"));
        }

        var bus = MapBus(desc.BusType);
        var geometry = Ioctl.Call(handle, Ioctl.GetDriveGeometryEx, null, 256, out var geoError);
        var size = geometry is null ? null : StorageProtocol.ParseGeometrySize(geometry);

        Value<bool> Flag(int property, string name, Func<byte[], bool?> parse, string whatIfFalse)
        {
            var source = "ioctl:" + name;
            var raw = Ioctl.Call(handle, Ioctl.QueryProperty, StorageProtocol.BuildPropertyQuery(property), 64, out var error);
            if (raw is null)
            {
                return Probe.FromWin32<bool>(error, source, whatIfFalse);
            }

            return parse(raw) is { } b ? Value.Ok(b, source) : Value.Failed<bool>(source, "malformed descriptor");
        }

        var trim = Flag(StorageProtocol.StorageDeviceTrimProperty, "StorageDeviceTrimProperty (Windows TrimEnabled)", raw => StorageProtocol.ParseTrimEnabled(raw), "TRIM state");
        var seek = Flag(StorageProtocol.StorageDeviceSeekPenaltyProperty, "StorageDeviceSeekPenaltyProperty", raw => StorageProtocol.ParseSeekPenalty(raw), "seek penalty");

        var alignRaw = Ioctl.Call(handle, Ioctl.QueryProperty, StorageProtocol.BuildPropertyQuery(StorageProtocol.StorageAccessAlignmentProperty), 64, out var alignError);
        var align = alignRaw is null ? null : StorageProtocol.ParseAlignment(alignRaw);

        var layoutRaw = Ioctl.Call(handle, Ioctl.GetDriveLayoutEx, null, 64 * 1024, out var layoutError);
        var layout = layoutRaw is null ? null : DriveLayout.Parse(layoutRaw);

        var myVolumes = volumes.Where(v => v.Extents.Any(e => e.Disk == number)).ToList();
        Value<string> style;
        if (layout is { } l)
        {
            style = Value.Ok(l.Style, "ioctl:IOCTL_DISK_GET_DRIVE_LAYOUT_EX");
        }
        else if (myVolumes.Select(v => v.Style).FirstOrDefault(s => s is not null) is { } fromVolume)
        {
            style = Value.Ok(fromVolume, "ioctl:IOCTL_DISK_GET_PARTITION_INFO_EX (from a volume on this disk)");
        }
        else
        {
            style = layoutRaw is null
                ? Probe.FromWin32<string>(layoutError, "ioctl:IOCTL_DISK_GET_DRIVE_LAYOUT_EX", "partition style")
                : Value.Failed<string>("ioctl:IOCTL_DISK_GET_DRIVE_LAYOUT_EX", "malformed layout");
        }

        // Without the layout IOCTL, the volumes with drive letters are the partitions we can see.
        IReadOnlyList<StoragePartition> partitions = layout?.Partitions
            ?? myVolumes
                .SelectMany(v => v.Extents.Where(e => e.Disk == number).Select(e => new StoragePartition(0, e.Start, e.Length, v.FileSystem.Length > 0 ? v.FileSystem : "volume", v.Style ?? "unknown")))
                .OrderBy(p => p.StartBytes)
                .ToList();

        // Controller driver and (NVMe only) PCIe link, from the PnP database.
        Value<string> driver = Value.Unsupported<string>("setupapi:DEVPKEY_Device_DriverVersion", "no PnP node found for this disk");
        Value<int> gen = Value.Unsupported<int>("setupapi:DEVPKEY_PciDevice_CurrentLinkSpeed", "not an NVMe drive");
        Value<int> width = gen;
        Value<int> maxGen = gen;
        Value<int> maxWidth = gen;
        if (nodes.TryGetValue(number, out var devInst))
        {
            var pci = PciNode.FindFrom(devInst);
            if (pci?.DriverVersion is { } version)
            {
                driver = Value.Ok(version, "setupapi:DEVPKEY_Device_DriverVersion (" + (pci.DriverService ?? "controller") + ")");
            }

            if (bus == StorageBus.Nvme)
            {
                if (pci is null)
                {
                    gen = width = maxGen = maxWidth = Value.Unsupported<int>("setupapi:DEVPKEY_PciDevice_CurrentLinkSpeed", "no PCI function found above this disk");
                }
                else
                {
                    gen = pci.CurrentGeneration;
                    width = pci.CurrentWidth;
                    maxGen = pci.MaxGeneration;
                    maxWidth = pci.MaxWidth;
                }
            }
        }

        var serial = StorageProtocol.CleanSerial(desc.Serial);
        var model = StorageProtocol.JoinVendorModel(desc.Vendor, desc.Product);
        var media = seek.Status == ValueStatus.Ok
            ? Value.Ok(seek.Data ? "HDD" : "SSD", "ioctl:StorageDeviceSeekPenaltyProperty")
            : new Value<string>(null, seek.Status, seek.Source, seek.Detail);

        var details = new StorageDetails(
            media,
            align is { } a ? Value.Ok(a.Logical, "ioctl:StorageAccessAlignmentProperty") : Probe.FromWin32<int>(alignError, "ioctl:StorageAccessAlignmentProperty", "sector size"),
            align is { } b ? Value.Ok(b.Physical, "ioctl:StorageAccessAlignmentProperty") : Probe.FromWin32<int>(alignError, "ioctl:StorageAccessAlignmentProperty", "sector size"),
            Value.Ok(desc.Removable, Source),
            partitions,
            driver,
            gen,
            width,
            maxGen,
            maxWidth);

        return new StorageDevice(
            number,
            Probe.Text(Source, model, "the drive reports no model"),
            serial.Length > 0 ? Value.Ok(serial, Source) : Value.Unsupported<string>(Source, "the drive reports no serial number"),
            Probe.Text(Source, desc.Revision, "the drive reports no firmware revision"),
            Value.Ok(bus, Source + " BusType"),
            size is { } s and > 0 ? Value.Ok(s, "ioctl:IOCTL_DISK_GET_DRIVE_GEOMETRY_EX") : Probe.FromWin32<long>(geoError, "ioctl:IOCTL_DISK_GET_DRIVE_GEOMETRY_EX", "capacity"),
            trim,
            style,
            myVolumes.Select(v => new StorageVolume(v.Letter, v.Label, v.FileSystem, v.Total, v.Free)).ToList(),
            details);
    }

    private static StorageDevice Unreadable(int number, Value<string> why) =>
        new(
            number,
            why,
            why,
            why,
            new Value<StorageBus>(default, why.Status, why.Source, why.Detail),
            new Value<long>(default, why.Status, why.Source, why.Detail),
            new Value<bool>(default, why.Status, why.Source, why.Detail),
            why,
            [],
            null);
}
