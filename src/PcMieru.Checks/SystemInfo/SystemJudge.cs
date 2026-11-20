// Provenance: the thresholds and verdict rules (uptime over 30 days, mixed capacities, speed below the rated one, GPU code 22, generic
// driver, driver older than 730 days, free space under 3% and 10%, no page file, page file peak over 80%, the event counts) are those of the
// owner's pl-cloud SystemCheck (src/PartsLife.Checks/SystemInfo/SystemCheck.cs at 25e4571, MIT, same owner), ported as DESIGN-A 14.1 states
// them. What is new: every value is a reading that may be missing (and then an unavailable row with its reason), the rows are per item and
// per subject, and the event counts are split into errors and warnings.

using System.Globalization;

namespace PcMieru.Checks.SystemInfo;

/// <summary>One installed memory module as the judge sees it: a capacity and the two speeds, each of which may be missing.</summary>
internal sealed record RamModuleInput(CheckReading<long> CapacityBytes, CheckReading<int> RatedMtPerSec, CheckReading<int> ConfiguredMtPerSec);

/// <summary>One display adapter: its name, the driver, and the Windows configuration error code of the device (0 = no error).</summary>
internal sealed record GpuInput(CheckReading<string> Name, CheckReading<string> DriverVersion, CheckReading<DateOnly> DriverDate, CheckReading<int> DeviceStatus);

/// <summary>One physical disk: its model as Core read it and what Windows says of its health. No serial, no drive number, no device id. <paramref name="Unreadable"/>: Core could not look at the slot at all.</summary>
internal sealed record DiskInput(string? Model, DiskHealthVerdict Health, bool Unreadable = false);

/// <summary>One local volume. <paramref name="DiskOrdinal"/> is the 1-based place of its disk in the disk list, only when that is the only disk it is known to live on.</summary>
internal sealed record VolumeInput(CheckReading<long> TotalBytes, CheckReading<long> FreeBytes, int? DiskOrdinal);

/// <summary>One page file, the sizes in MB as Windows gives them (null: that size did not come). No path.</summary>
internal sealed record PageFileInput(long? AllocatedMb, long? CurrentMb, long? PeakMb);

/// <summary>
/// Everything the system check read, as readings: a leaf that did not come is a missing reading with its reason, never a default value.
/// A list that is missing (the enumeration failed) is not an empty list: it has no children rows (DESIGN-A 3).
/// </summary>
internal sealed record SystemInputs(
    CheckReading<OsVersionValue> OsVersion,
    CheckReading<double> UptimeSeconds,
    CheckReading<string> CpuModel,
    CheckReading<int> CpuCores,
    CheckReading<int> CpuLogical,
    CheckReading<double> CpuRatedMhz,
    CheckReading<IReadOnlyList<RamModuleInput>> Memory,
    CheckReading<IReadOnlyList<GpuInput>> Gpus,
    CheckReading<IReadOnlyList<DiskInput>> Disks,
    CheckReading<IReadOnlyList<VolumeInput>> Volumes,
    CheckReading<IReadOnlyList<PageFileInput>> PageFiles,
    CheckReading<bool> PageFileAutomatic,
    IReadOnlyDictionary<string, CheckReading<long>> Events);

/// <summary>
/// The verdicts of the system check (DESIGN-A 3.1, 14.1): a pure function of what was read and of the time. It makes the rows; it reads
/// nothing and decides nothing about the run. The boundaries are the old code's (a value exactly on one is on the milder side where the
/// old code said "over" or "under").
/// </summary>
internal static class SystemJudge
{
    /// <summary>Uptime over this many seconds is a caution (30 days).</summary>
    public const double UptimeCautionSeconds = 30 * 24 * 3600;

    /// <summary>A driver older than this many days is a caution.</summary>
    public const double DriverAgeCautionDays = 730;

    /// <summary>Free space under this share of the volume is a check, and under <see cref="FreeCautionRatio"/> a caution.</summary>
    public const double FreeCheckRatio = 0.03;

    public const double FreeCautionRatio = 0.10;

    /// <summary>A page file whose peak is over this share of its allocation is a caution.</summary>
    public const double PagefilePeakRatio = 0.8;

    /// <summary>The error counts at which a log's row is a check (below it, any is a caution).</summary>
    public const long EventCheckCount = 10;

    private const ulong BytesPerMegabyte = 1048576;

    /// <summary>The rows, in the order of DESIGN-A 3.1.</summary>
    public static IReadOnlyList<CheckItem> Judge(SystemInputs inputs, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var items = new List<CheckItem>(64);
        Os(items, inputs);
        Cpu(items, inputs);
        Memory(items, inputs.Memory);
        Gpus(items, inputs.Gpus, nowUtc);
        Storage(items, inputs);
        PageFiles(items, inputs);
        Events(items, inputs.Events);
        return items;
    }

    // ---- OS, CPU ----

    private static void Os(List<CheckItem> items, SystemInputs inputs)
    {
        items.Add(Info("system.os.version", inputs.OsVersion, CheckValue.OsVersion, CheckSource.Os));

        // DESIGN-A 14.1: over 30 days is a caution. The value is the seconds as read.
        var uptime = inputs.UptimeSeconds;
        items.Add(uptime.HasValue
            ? CheckItem.Create("system.os.uptime", CheckValue.Number(uptime.Value), uptime.Value > UptimeCautionSeconds ? ItemVerdict.Caution : ItemVerdict.Ok, CheckSource.Os)
            : Missing("system.os.uptime", uptime.Reason, CheckSource.Os, ItemVerdict.Unavailable));
    }

    private static void Cpu(List<CheckItem> items, SystemInputs inputs)
    {
        items.Add(Info("system.cpu.model", inputs.CpuModel, CheckValue.Text, CheckSource.Os));
        items.Add(Info("system.cpu.cores", inputs.CpuCores, static v => CheckValue.Int(v), CheckSource.Win32));
        items.Add(Info("system.cpu.logical", inputs.CpuLogical, static v => CheckValue.Int(v), CheckSource.Win32));
        items.Add(Info("system.cpu.rated_clock", inputs.CpuRatedMhz, CheckValue.Number, CheckSource.Smbios));
    }

    // ---- memory ----

    private static void Memory(List<CheckItem> items, CheckReading<IReadOnlyList<RamModuleInput>> memory)
    {
        const string ModulesId = "system.memory.modules";
        if (!memory.HasValue)
        {
            items.Add(Missing(ModulesId, memory.Reason, CheckSource.Smbios, ItemVerdict.Unavailable));
            return;
        }

        var modules = memory.Value;
        if (modules.Count == 0)
        {
            // No module is no answer: a machine has memory, so a list with none means the table did not say.
            items.Add(Missing(ModulesId, CheckReason.Unsupported, CheckSource.Smbios, ItemVerdict.Unavailable));
            return;
        }

        var known = modules.Where(static m => m.CapacityBytes.HasValue).Select(static m => m.CapacityBytes.Value).ToList();
        var mixed = known.Distinct().Count() > 1;
        var allKnown = known.Count == modules.Count;
        var count = CheckValue.Int(modules.Count);
        if (mixed)
        {
            var evidence = new CheckEvidence
            {
                Code = CheckEvidenceCode.MixedCapacity,
                Capacities = allKnown ? modules.Select(static m => Bytes(m.CapacityBytes.Value)).ToArray() : null,
            };
            items.Add(CheckItem.Create(ModulesId, count, ItemVerdict.Caution, CheckSource.Smbios, null, null, evidence));
        }
        else if (!allKnown)
        {
            // Every capacity that is known is the same, but one is not known: "not mixed" cannot be said.
            items.Add(CheckItem.Create(ModulesId, count, ItemVerdict.Unavailable, CheckSource.Smbios, CheckReason.Partial));
        }
        else
        {
            items.Add(CheckItem.Create(ModulesId, count, ItemVerdict.Ok, CheckSource.Smbios));
        }

        for (var i = 0; i < modules.Count; i++)
        {
            var module = modules[i];
            var subject = new CheckSubject("ram-" + (i + 1).ToString(CultureInfo.InvariantCulture));
            items.Add(Info("system.memory.capacity", module.CapacityBytes, static v => CheckValue.Dec(Bytes(v)), CheckSource.Smbios, subject, static v => v >= 0));
            items.Add(Info("system.memory.rated_speed", module.RatedMtPerSec, static v => CheckValue.Int(v), CheckSource.Smbios, subject, static v => v > 0));
            items.Add(ConfiguredSpeed(module, subject));
        }
    }

    // DESIGN-A 14.1: a speed of 0 or a missing one cannot be measured; below the rated one is a caution (below_rated), above it is ok (above_rated).
    private static CheckItem ConfiguredSpeed(RamModuleInput module, CheckSubject subject)
    {
        const string Id = "system.memory.configured_speed";
        if (!module.ConfiguredMtPerSec.HasValue)
        {
            return Missing(Id, module.ConfiguredMtPerSec.Reason, CheckSource.Smbios, ItemVerdict.Unavailable, subject);
        }

        if (!module.RatedMtPerSec.HasValue)
        {
            return Missing(Id, module.RatedMtPerSec.Reason, CheckSource.Smbios, ItemVerdict.Unavailable, subject);
        }

        var configured = module.ConfiguredMtPerSec.Value;
        var rated = module.RatedMtPerSec.Value;
        if (configured <= 0 || rated <= 0)
        {
            return Missing(Id, CheckReason.Unsupported, CheckSource.Smbios, ItemVerdict.Unavailable, subject);
        }

        var value = CheckValue.Int(configured);
        if (configured < rated)
        {
            return CheckItem.Create(Id, value, ItemVerdict.Caution, CheckSource.Smbios, null, subject, CheckEvidence.OfCode(CheckEvidenceCode.BelowRated));
        }

        return configured > rated
            ? CheckItem.Create(Id, value, ItemVerdict.Ok, CheckSource.Smbios, null, subject, CheckEvidence.OfCode(CheckEvidenceCode.AboveRated))
            : CheckItem.Create(Id, value, ItemVerdict.Ok, CheckSource.Smbios, null, subject);
    }

    // ---- GPUs ----

    private static void Gpus(List<CheckItem> items, CheckReading<IReadOnlyList<GpuInput>> gpus, DateTime nowUtc)
    {
        items.Add(Info("system.gpu.adapters", gpus, static list => CheckValue.Int(list.Count), CheckSource.Dxgi));
        if (!gpus.HasValue)
        {
            return;
        }

        var list = gpus.Value;
        for (var i = 0; i < list.Count; i++)
        {
            var gpu = list[i];
            var subject = new CheckSubject("gpu-" + (i + 1).ToString(CultureInfo.InvariantCulture), gpu.Name.HasValue ? NullIfBlank(gpu.Name.Value) : null);
            items.Add(Info("system.gpu.model", gpu.Name, CheckValue.Text, CheckSource.Dxgi, subject));
            items.Add(Info("system.gpu.driver_version", gpu.DriverVersion, CheckValue.Text, CheckSource.Setupapi, subject));

            // The two driver rows are info exactly when this GPU's device status is not 0 (DESIGN-A 14.0): a device with an error has no
            // driver to judge. An unreadable status is not "not 0".
            var statusNonZero = gpu.DeviceStatus.HasValue && gpu.DeviceStatus.Value != 0;
            items.Add(DriverAge(gpu, subject, nowUtc, statusNonZero));
            items.Add(DeviceStatus(gpu.DeviceStatus, subject));
            items.Add(GenericDriver(gpu, subject, statusNonZero));
        }
    }

    // DESIGN-A 14.1: the fractional days from the driver date to now over 730 is a caution; the value is the whole days.
    private static CheckItem DriverAge(GpuInput gpu, CheckSubject subject, DateTime nowUtc, bool statusNonZero)
    {
        const string Id = "system.gpu.driver_age";
        var infoOrJudged = statusNonZero ? ItemVerdict.Info : ItemVerdict.Unavailable;
        if (!gpu.DriverDate.HasValue)
        {
            return Missing(Id, gpu.DriverDate.Reason, CheckSource.Setupapi, infoOrJudged, subject);
        }

        var date = DateTime.SpecifyKind(gpu.DriverDate.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var days = (nowUtc - date).TotalDays;
        var value = CheckValue.Int(days <= 0 ? 0 : (long)Math.Truncate(days));
        var verdict = statusNonZero ? ItemVerdict.Info : days > DriverAgeCautionDays ? ItemVerdict.Caution : ItemVerdict.Ok;
        return CheckItem.Create(Id, value, verdict, CheckSource.Setupapi, null, subject);
    }

    // DESIGN-A 14.1: 22 (disabled by the user or the settings) is not a fault, and is looked at before the other non-zero codes.
    private static CheckItem DeviceStatus(CheckReading<int> status, CheckSubject subject)
    {
        const string Id = "system.gpu.device_status";
        if (!status.HasValue)
        {
            return Missing(Id, status.Reason, CheckSource.Wmi, ItemVerdict.Unavailable, subject);
        }

        var code = status.Value;
        var value = CheckValue.Int(code);
        return code switch
        {
            22 => CheckItem.Create(Id, value, ItemVerdict.Ok, CheckSource.Wmi, null, subject, CheckEvidence.OfCode(CheckEvidenceCode.DisabledCode22)),
            0 => CheckItem.Create(Id, value, ItemVerdict.Ok, CheckSource.Wmi, null, subject),
            _ => CheckItem.Create(Id, value, ItemVerdict.Check, CheckSource.Wmi, null, subject, CheckEvidence.OfCode(CheckEvidenceCode.DeviceError)),
        };
    }

    // DESIGN-A 14.1: a name with "Basic Display" (ignoring case) or the Japanese "基本ディスプレイ" is the generic driver (a caution).
    private static CheckItem GenericDriver(GpuInput gpu, CheckSubject subject, bool statusNonZero)
    {
        const string Id = "system.gpu.generic_driver";
        if (!gpu.Name.HasValue)
        {
            return Missing(Id, gpu.Name.Reason, CheckSource.Dxgi, statusNonZero ? ItemVerdict.Info : ItemVerdict.Unavailable, subject);
        }

        var name = gpu.Name.Value;
        var generic = name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("基本ディスプレイ", StringComparison.Ordinal);
        var verdict = statusNonZero ? ItemVerdict.Info : generic ? ItemVerdict.Caution : ItemVerdict.Ok;
        return CheckItem.Create(Id, CheckValue.Bool(generic), verdict, CheckSource.Dxgi, null, subject);
    }

    // ---- disks, volumes ----

    private static void Storage(List<CheckItem> items, SystemInputs inputs)
    {
        // The count is of the drives seen; a slot that could not be looked at is counted, and the row says it is "partial" (as the storage check does).
        items.Add(inputs.Disks.HasValue && inputs.Disks.Value.Any(static d => d.Unreadable)
            ? CheckItem.Create("system.storage.devices", CheckValue.Int(inputs.Disks.Value.Count), ItemVerdict.Info, CheckSource.Win32, CheckReason.Partial)
            : Info("system.storage.devices", inputs.Disks, static list => CheckValue.Int(list.Count), CheckSource.Win32));
        if (inputs.Disks.HasValue)
        {
            var disks = inputs.Disks.Value;
            for (var i = 0; i < disks.Count; i++)
            {
                var subject = new CheckSubject("disk-" + (i + 1).ToString(CultureInfo.InvariantCulture), NullIfBlank(disks[i].Model));
                items.Add(disks[i].Health.ToItem("system.storage.health", subject));
            }
        }

        if (inputs.Volumes.HasValue)
        {
            var volumes = inputs.Volumes.Value;
            for (var i = 0; i < volumes.Count; i++)
            {
                items.Add(Free(volumes[i], new CheckSubject("volume-" + (i + 1).ToString(CultureInfo.InvariantCulture))));
            }
        }
    }

    // DESIGN-A 14.1: the free share under 3% is a check, under 10% a caution; a volume of size 0 has the ratio 1. The value is the free bytes.
    private static CheckItem Free(VolumeInput volume, CheckSubject subject)
    {
        const string Id = "system.storage.free";
        if (!volume.FreeBytes.HasValue || volume.FreeBytes.Value < 0)
        {
            return Missing(Id, volume.FreeBytes.Reason ?? CheckReason.Error, CheckSource.Win32, ItemVerdict.Unavailable, subject);
        }

        var free = (ulong)volume.FreeBytes.Value;
        if (!volume.TotalBytes.HasValue || volume.TotalBytes.Value < 0)
        {
            // The free bytes came, but the share cannot be worked out: the value is kept as a partial one, and no verdict is claimed.
            return CheckItem.Create(Id, CheckValue.Dec(free), ItemVerdict.Unavailable, CheckSource.Win32, CheckReason.Partial, subject);
        }

        var total = (ulong)volume.TotalBytes.Value;
        var ratio = total == 0 ? 1.0 : (double)free / total;
        var verdict = ratio < FreeCheckRatio ? ItemVerdict.Check : ratio < FreeCautionRatio ? ItemVerdict.Caution : ItemVerdict.Ok;
        var evidence = new CheckEvidence
        {
            TotalBytes = Bytes(volume.TotalBytes.Value),
            DiskId = volume.DiskOrdinal is { } ordinal and > 0 ? "disk-" + ordinal.ToString(CultureInfo.InvariantCulture) : null,
        };
        return CheckItem.Create(Id, CheckValue.Dec(free), verdict, CheckSource.Win32, null, subject, evidence);
    }

    // ---- page files ----

    private static void PageFiles(List<CheckItem> items, SystemInputs inputs)
    {
        const string CountId = "system.pagefile.count";
        var files = inputs.PageFiles;
        if (!files.HasValue)
        {
            // The list did not come: how many there are is not known, and no file row can be made.
            items.Add(Missing(CountId, files.Reason, CheckSource.Wmi, ItemVerdict.Unavailable));
            return;
        }

        var list = files.Value;
        items.Add(CheckItem.Create(CountId, CheckValue.Int(list.Count), list.Count == 0 ? ItemVerdict.Caution : ItemVerdict.Ok, CheckSource.Wmi));
        for (var i = 0; i < list.Count; i++)
        {
            var file = list[i];
            var subject = new CheckSubject("pagefile-" + (i + 1).ToString(CultureInfo.InvariantCulture));
            items.Add(Info("system.pagefile.automatic", inputs.PageFileAutomatic, static v => CheckValue.Bool(v), CheckSource.Wmi, subject));
            items.Add(SizeRow("system.pagefile.allocated", file.AllocatedMb, subject));
            items.Add(SizeRow("system.pagefile.current", file.CurrentMb, subject));
            items.Add(Peak(file, subject));
        }
    }

    private static CheckItem SizeRow(string id, long? megabytes, CheckSubject subject) =>
        ToBytes(megabytes) is { } bytes
            ? CheckItem.Create(id, CheckValue.Dec(bytes), ItemVerdict.Info, CheckSource.Wmi, null, subject)
            : Missing(id, CheckReason.Error, CheckSource.Wmi, ItemVerdict.Info, subject);

    // DESIGN-A 14.1: only a file with a positive allocation is judged, and its peak over 80% of the allocation is a caution.
    private static CheckItem Peak(PageFileInput file, CheckSubject subject)
    {
        const string Id = "system.pagefile.peak";
        if (ToBytes(file.PeakMb) is not { } peakBytes)
        {
            return Missing(Id, CheckReason.Error, CheckSource.Wmi, ItemVerdict.Unavailable, subject);
        }

        if (file.AllocatedMb is not { } allocated || allocated < 0)
        {
            return CheckItem.Create(Id, CheckValue.Dec(peakBytes), ItemVerdict.Unavailable, CheckSource.Wmi, CheckReason.Partial, subject);
        }

        var tight = allocated > 0 && file.PeakMb!.Value > allocated * PagefilePeakRatio;
        return CheckItem.Create(Id, CheckValue.Dec(peakBytes), tight ? ItemVerdict.Caution : ItemVerdict.Ok, CheckSource.Wmi, null, subject);
    }

    private static ulong? ToBytes(long? megabytes) =>
        megabytes is { } mb && mb >= 0 && (ulong)mb <= ulong.MaxValue / BytesPerMegabyte ? (ulong)mb * BytesPerMegabyte : null;

    // ---- event log ----

    private static void Events(List<CheckItem> items, IReadOnlyDictionary<string, CheckReading<long>> events)
    {
        foreach (var kind in EventCountQuery.AllKinds)
        {
            var id = IdOf(kind);
            var reading = events.TryGetValue(id, out var found) ? found : CheckReading<long>.Failed();
            var judged = IsJudged(kind);
            if (!reading.HasValue || reading.Value < 0)
            {
                items.Add(Missing(id, reading.Reason ?? CheckReason.Error, CheckSource.Eventlog, judged ? ItemVerdict.Unavailable : ItemVerdict.Info));
                continue;
            }

            var count = reading.Value;
            items.Add(CheckItem.Create(id, CheckValue.Int(count), judged ? EventVerdict(kind, count) : ItemVerdict.Info, CheckSource.Eventlog));
        }
    }

    /// <summary>The verdict of one event count (DESIGN-A 14.1). The warning rows of disk, ntfs, stornvme and volmgr are never judged (<see cref="IsJudged"/>).</summary>
    public static ItemVerdict EventVerdict(EventCountKind kind, long count) => kind switch
    {
        EventCountKind.Whea => count >= 1 ? ItemVerdict.Check : ItemVerdict.Ok,
        EventCountKind.WheaWarning => count >= 1 ? ItemVerdict.Caution : ItemVerdict.Ok,
        EventCountKind.KernelPower41 => count >= 1 ? ItemVerdict.Caution : ItemVerdict.Ok,
        EventCountKind.Disk or EventCountKind.Ntfs or EventCountKind.Stornvme or EventCountKind.Volmgr =>
            count >= EventCheckCount ? ItemVerdict.Check : count >= 1 ? ItemVerdict.Caution : ItemVerdict.Ok,
        _ => ItemVerdict.Info,
    };

    /// <summary>False for the warning rows of disk, ntfs, stornvme and volmgr, which only show a count.</summary>
    public static bool IsJudged(EventCountKind kind) =>
        kind is EventCountKind.Whea or EventCountKind.WheaWarning or EventCountKind.KernelPower41
            or EventCountKind.Disk or EventCountKind.Ntfs or EventCountKind.Stornvme or EventCountKind.Volmgr;

    private static string IdOf(EventCountKind kind) => EventCountQuery.Create(kind, DateTime.UnixEpoch).ItemId;

    // ---- shared helpers ----

    private static string Bytes(long value) => ((ulong)value).ToString(CultureInfo.InvariantCulture);

    private static string? NullIfBlank(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    // An item that only shows what was read: its verdict is info with a value, and info with no value and the reason without one.
    // "accept" lets a value that is not a real reading (a speed of 0) be shown as unsupported instead.
    private static CheckItem Info<T>(string id, CheckReading<T> reading, Func<T, CheckValue> make, CheckSource source, CheckSubject? subject = null, Func<T, bool>? accept = null)
    {
        if (!reading.HasValue)
        {
            return Missing(id, reading.Reason, source, ItemVerdict.Info, subject);
        }

        return accept is not null && !accept(reading.Value)
            ? Missing(id, CheckReason.Unsupported, source, ItemVerdict.Info, subject)
            : CheckItem.Create(id, make(reading.Value), ItemVerdict.Info, source, null, subject);
    }

    // No value: the reason is the reading's (an unreadable one with none is an error), the verdict unavailable for a judged row or info for an info one.
    private static CheckItem Missing(string id, CheckReason? reason, CheckSource source, ItemVerdict verdict, CheckSubject? subject = null) =>
        CheckItem.Create(id, null, verdict, source, reason ?? CheckReason.Error, subject);
}
