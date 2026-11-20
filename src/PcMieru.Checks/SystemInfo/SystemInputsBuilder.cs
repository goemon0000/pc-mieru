using System.Globalization;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.SystemInfo;

/// <summary>
/// Puts what the system check read (Core's categories, the WMI questions, the event counts) into the readings the judge takes. It decides
/// nothing about the verdicts. A leaf that did not come stays a missing reading with its reason; a list that did not come is a missing list
/// (no children), never an empty one. The numbering of the subjects (cpu-1, disk-1...) is the order made here, and it is a fixed one.
/// </summary>
internal static class SystemInputsBuilder
{
    public static SystemInputs Build(HardwareFacts hardware, SystemWmiFacts wmi, IReadOnlyDictionary<string, CheckReading<long>> events)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(events);

        var disks = Disks(hardware.Storage, wmi.DiskHealth);
        return new SystemInputs(
            OsVersion(hardware.Os),
            Uptime(hardware.Os),
            Cpu(hardware.Cpu, static c => CoreReadings.Text(c.Name)),
            Cpu(hardware.Cpu, static c => Positive(CoreReadings.Of(c.PhysicalCores))),
            Cpu(hardware.Cpu, static c => Positive(CoreReadings.Of(c.LogicalProcessors))),
            Cpu(hardware.Cpu, static c => Positive(CoreReadings.Of(c.BaseClockMhz)).Map(static mhz => (double)mhz)),
            Memory(hardware.Memory),
            Gpus(hardware.Gpus, wmi.VideoControllers),
            disks.Disks,
            Volumes(hardware.Storage, disks.Order),
            PageFiles(wmi.PageFiles),
            wmi.PageFileAutomatic,
            events);
    }

    // ---- OS, CPU ----

    // The name and the build number are the two things the version cannot do without; the feature-update name and the revision may be null.
    private static CheckReading<OsVersionValue> OsVersion(CheckReading<OsHardware> os)
    {
        if (!os.HasValue)
        {
            return CheckReading<OsVersionValue>.Missing(os.Reason!.Value);
        }

        var name = CoreReadings.Text(os.Value.Name);
        if (!name.HasValue)
        {
            return CheckReading<OsVersionValue>.Missing(name.Reason!.Value);
        }

        var build = CoreReadings.Of(os.Value.Build);
        if (!build.HasValue)
        {
            return CheckReading<OsVersionValue>.Missing(build.Reason!.Value);
        }

        // "major.minor.build": the number after the last dot.
        var text = build.Value;
        var number = text[(text.LastIndexOf('.') + 1)..];
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var buildNumber))
        {
            return CheckReading<OsVersionValue>.Failed();
        }

        var details = os.Value.Details;
        var display = details is null ? null : CoreReadings.Text(details.DisplayVersion) is { HasValue: true } shown ? shown.Value : null;
        int? ubr = details is not null && CoreReadings.Of(details.Ubr) is { HasValue: true } revision && revision.Value >= 0 ? revision.Value : null;
        return CheckReading<OsVersionValue>.Ok(new OsVersionValue(name.Value, display, buildNumber, ubr));
    }

    private static CheckReading<double> Uptime(CheckReading<OsHardware> os)
    {
        if (!os.HasValue)
        {
            return CheckReading<double>.Missing(os.Reason!.Value);
        }

        var uptime = CoreReadings.Of(os.Value.UptimeSec);
        return !uptime.HasValue ? CheckReading<double>.Missing(uptime.Reason!.Value)
            : uptime.Value < 0 ? CheckReading<double>.Failed()
            : CheckReading<double>.Ok(uptime.Value);
    }

    private static CheckReading<T> Cpu<T>(CheckReading<CpuHardware> cpu, Func<CpuHardware, CheckReading<T>> pick) =>
        cpu.HasValue ? pick(cpu.Value) : CheckReading<T>.Missing(cpu.Reason!.Value);

    // A count or a clock of 0 is Windows not telling, not a machine with none.
    private static CheckReading<int> Positive(CheckReading<int> reading) =>
        reading.HasValue && reading.Value <= 0 ? CheckReading<int>.Missing(CheckReason.Unsupported) : reading;

    // ---- memory ----

    // Only the installed modules: an empty slot is a module with the capacity 0, which is not a module. A slot whose capacity could not be
    // read is kept (something may be in it) with the capacity missing.
    private static CheckReading<IReadOnlyList<RamModuleInput>> Memory(CheckReading<MemoryHardware> memory)
    {
        if (!memory.HasValue)
        {
            return CheckReading<IReadOnlyList<RamModuleInput>>.Missing(memory.Reason!.Value);
        }

        var modules = new List<RamModuleInput>();
        foreach (var module in memory.Value.Modules)
        {
            if (module.CapacityBytes is { Status: ValueStatus.Ok, Data: 0 })
            {
                continue;
            }

            modules.Add(new RamModuleInput(
                CoreReadings.Of(module.CapacityBytes),
                CoreReadings.Of(module.SpeedMtPerSec),
                CoreReadings.Of(module.ConfiguredSpeedMtPerSec)));
        }

        return CheckReading<IReadOnlyList<RamModuleInput>>.Ok(modules);
    }

    // ---- GPUs ----

    // The adapters of Core, in the order Core lists them. Each one's device status comes from the video controller of the same PCI vendor and
    // device (the PNP id of WMI has them as VEN_xxxx&DEV_xxxx; Core has them as "VVVV:DDDD"). A status is only taken when the pairing is
    // certain: as many controllers as Core adapters with that id, all saying the same code. An adapter that cannot be paired at all (no PCI
    // id or place, or no controller of a PCI id like it: the Basic Render Driver, a Hyper-V synthetic adapter, an indirect or virtual display)
    // has no status because Windows has none to give: unsupported, not an error (DESIGN-A §3.1). Only a real ambiguity (some controllers
    // match, but not as many as adapters, or they disagree, or one of them has no code) is an error, never a guess.
    private static CheckReading<IReadOnlyList<GpuInput>> Gpus(CheckReading<IReadOnlyList<GpuHardware>> gpus, CheckReading<IReadOnlyList<VideoControllerRow>> controllers)
    {
        if (!gpus.HasValue)
        {
            return CheckReading<IReadOnlyList<GpuInput>>.Missing(gpus.Reason!.Value);
        }

        var keys = gpus.Value.Select(static g => CoreReadings.Of(g.PciId) is { HasValue: true } id ? id.Value.Trim().ToUpperInvariant() : null).ToList();
        var list = new List<GpuInput>(gpus.Value.Count);
        for (var i = 0; i < gpus.Value.Count; i++)
        {
            var gpu = gpus.Value[i];
            list.Add(new GpuInput(
                CoreReadings.Text(gpu.Name),
                CoreReadings.Text(gpu.DriverVersion),
                DriverDate(gpu.DriverDate),
                DeviceStatus(gpu, keys[i], keys, controllers)));
        }

        return CheckReading<IReadOnlyList<GpuInput>>.Ok(list);
    }

    private static CheckReading<DateOnly> DriverDate(Value<string> value)
    {
        var text = CoreReadings.Text(value);
        if (!text.HasValue)
        {
            return CheckReading<DateOnly>.Missing(text.Reason!.Value);
        }

        return DateOnly.TryParseExact(text.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? CheckReading<DateOnly>.Ok(date)
            : CheckReading<DateOnly>.Failed();
    }

    private static CheckReading<int> DeviceStatus(GpuHardware gpu, string? key, IReadOnlyList<string?> allKeys, CheckReading<IReadOnlyList<VideoControllerRow>> controllers)
    {
        // An adapter with no PCI id (a software adapter) or no PCI place (a virtual or remote copy of one) has no controller of its own to
        // ask: that is the same whatever WMI answered, so it comes before the controllers.
        var pciId = CoreReadings.Of(gpu.PciId);
        if (pciId.Reason == CheckReason.Unsupported
            || key is { Length: 0 }
            || gpu.Details?.PciLocation is { Status: ValueStatus.Unsupported })
        {
            return CheckReading<int>.Missing(CheckReason.Unsupported);
        }

        if (!controllers.HasValue)
        {
            return CheckReading<int>.Missing(controllers.Reason!.Value);
        }

        if (key is null)
        {
            // Core's own read of the id failed or was denied: the reason is Core's.
            return CheckReading<int>.Missing(pciId.Reason!.Value);
        }

        var sameAdapters = allKeys.Count(k => string.Equals(k, key, StringComparison.Ordinal));
        var codes = new List<int?>();
        foreach (var row in controllers.Value)
        {
            if (string.Equals(PciKey(row.PnpId), key, StringComparison.Ordinal))
            {
                codes.Add(row.ErrorCode);
            }
        }

        if (codes.Count == 0)
        {
            // No controller of this vendor and device on a PCI id: the adapter is not one WMI lists as a PCI device.
            return CheckReading<int>.Missing(CheckReason.Unsupported);
        }

        if (codes.Count != sameAdapters || codes.Any(static c => c is null) || codes.Distinct().Count() != 1)
        {
            return CheckReading<int>.Failed();
        }

        return CheckReading<int>.Ok(codes[0]!.Value);
    }

    // "PCI\VEN_10DE&DEV_2704&SUBSYS_..." -> "10DE:2704". Any other PNP id (a virtual adapter) is not a PCI one: null.
    internal static string? PciKey(string? pnpId)
    {
        if (pnpId is null)
        {
            return null;
        }

        var text = pnpId.Trim();
        const string Prefix = "PCI\\VEN_";
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || text.Length < Prefix.Length + 4 + 5 + 4)
        {
            return null;
        }

        var vendor = text.Substring(Prefix.Length, 4);
        var middle = text.Substring(Prefix.Length + 4, 5);
        var device = text.Substring(Prefix.Length + 9, 4);
        return middle.Equals("&DEV_", StringComparison.OrdinalIgnoreCase) && IsHex(vendor) && IsHex(device)
            ? vendor.ToUpperInvariant() + ":" + device.ToUpperInvariant()
            : null;
    }

    private static bool IsHex(string text) => text.All(static c => Uri.IsHexDigit(c));

    // ---- disks, volumes ----

    private sealed record DiskOrder(CheckReading<IReadOnlyList<DiskInput>> Disks, IReadOnlyList<StorageDevice> Order);

    // The drives in the order of their numbers (the numbers are only of this run; they are never written, and "disk-1" is the place in this order).
    // A slot with no media is not a drive of this machine (DESIGN-A §3.1): it is left out here, so it is not counted, has no health row, and
    // its volumes (if the slot says it has any) have no free-space row; the places of "disk-N" are those of the drives that are counted.
    private static DiskOrder Disks(CheckReading<IReadOnlyList<StorageDevice>> storage, DiskHealthReading health)
    {
        if (!storage.HasValue)
        {
            return new DiskOrder(CheckReading<IReadOnlyList<DiskInput>>.Missing(storage.Reason!.Value), []);
        }

        var order = storage.Value.Where(static d => !HasNoMedia(d)).OrderBy(static d => d.PhysicalDriveNumber).ToList();
        var disks = new List<DiskInput>(order.Count);
        foreach (var drive in order)
        {
            var model = CoreReadings.Text(drive.Model);
            var serial = CoreReadings.Text(drive.SerialNumber);
            var unreadable = !model.HasValue && !serial.HasValue && !CoreReadings.Of(drive.CapacityBytes).HasValue;
            disks.Add(new DiskInput(model.HasValue ? model.Value : null, health.Of(drive.PhysicalDriveNumber, serial.HasValue ? serial.Value : null), unreadable));
        }

        return new DiskOrder(CheckReading<IReadOnlyList<DiskInput>>.Ok(disks), order);
    }

    /// <summary>
    /// A removable drive that was opened and read, and answered "no size": an empty card-reader slot. It is told by three of Core's own
    /// readings together: the device descriptor was read (<see cref="StorageDevice.Details"/> is there; a slot Core could not open or read has none
    /// and is "unreadable", which is not this), the descriptor says the media is removable (<see cref="StorageDetails.Removable"/> is true), and the
    /// capacity is zero or did not come (<see cref="StorageDevice.CapacityBytes"/>: 0, or not read because Windows says there is no medium).
    /// A capacity that was denied or timed out is not "no medium": it is a drive that could not be asked, so it stays in the list.
    /// </summary>
    internal static bool HasNoMedia(StorageDevice drive)
    {
        if (drive.Details is not { } details || details.Removable is not { Status: ValueStatus.Ok, Data: true })
        {
            return false;
        }

        return drive.CapacityBytes is { Status: ValueStatus.Ok, Data: 0 } || drive.CapacityBytes.Status is ValueStatus.Error or ValueStatus.Unsupported;
    }

    // The volumes in the order of their letters (a letter is only used to sort and to join; it is never written). A volume that sits on
    // several drives is listed once, and says no drive (the drive of a volume is only given when there is one).
    private static CheckReading<IReadOnlyList<VolumeInput>> Volumes(CheckReading<IReadOnlyList<StorageDevice>> storage, IReadOnlyList<StorageDevice> order)
    {
        if (!storage.HasValue)
        {
            return CheckReading<IReadOnlyList<VolumeInput>>.Missing(storage.Reason!.Value);
        }

        var byLetter = new SortedDictionary<string, (StorageVolume Volume, List<int> Disks)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++)
        {
            foreach (var volume in order[i].Volumes)
            {
                if (!byLetter.TryGetValue(volume.Letter, out var known))
                {
                    known = (volume, new List<int>());
                    byLetter[volume.Letter] = known;
                }

                if (!known.Disks.Contains(i + 1))
                {
                    known.Disks.Add(i + 1);
                }
            }
        }

        var list = new List<VolumeInput>(byLetter.Count);
        foreach (var (volume, disks) in byLetter.Values)
        {
            list.Add(new VolumeInput(CoreReadings.Of(volume.TotalBytes), CoreReadings.Of(volume.FreeBytes), disks.Count == 1 ? disks[0] : null));
        }

        return CheckReading<IReadOnlyList<VolumeInput>>.Ok(list);
    }

    // ---- page files ----

    private static CheckReading<IReadOnlyList<PageFileInput>> PageFiles(CheckReading<IReadOnlyList<PageFileRow>> files)
    {
        if (!files.HasValue)
        {
            return CheckReading<IReadOnlyList<PageFileInput>>.Missing(files.Reason!.Value);
        }

        // By name, to number them in a fixed order; the name is dropped here.
        var sorted = files.Value
            .OrderBy(static f => f.Name is null ? 1 : 0)
            .ThenBy(static f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static f => new PageFileInput(f.AllocatedMb, f.CurrentMb, f.PeakMb))
            .ToList();
        return CheckReading<IReadOnlyList<PageFileInput>>.Ok(sorted);
    }
}
