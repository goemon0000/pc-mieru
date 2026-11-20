using System.Globalization;

namespace PcMieru.Checks.Tests;

/// <summary>
/// The results the fixtures are made of, built through the public API. Fixed time, fixed UUIDs: the output never
/// changes unless the format does.
/// </summary>
internal static class Samples
{
    public const string App = "PCMieru 0.4.0";

    public static readonly DateTimeOffset Start = new(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);

    public static readonly Guid SystemRun = Guid.Parse("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");
    public static readonly Guid CpuRun = Guid.Parse("1b2c3d4e-5f6a-4b7c-8d9e-0f1a2b3c4d5e");
    public static readonly Guid MemoryRun = Guid.Parse("89914ef9-5781-4cf7-a4f8-6d58003ddf86");
    public static readonly Guid StorageRun = Guid.Parse("3d4e5f6a-7b8c-4d9e-8f0a-1b2c3d4e5f6a");

    private static CheckItem I(
        string id,
        CheckValue? value,
        ItemVerdict verdict,
        CheckSource source,
        CheckReason? reason = null,
        CheckSubject? subject = null,
        CheckEvidence? evidence = null) =>
        CheckItem.Create(id, value, verdict, source, reason, subject, evidence);

    private static CheckValue N(double value) => CheckValue.Number(value);

    private static CheckValue Int(long value) => CheckValue.Int(value);

    private static CheckValue D(string value) => CheckValue.Dec(value);

    private static CheckValue SD(string value) => CheckValue.SignedDec(value);

    private static CheckValue T(string value) => CheckValue.Text(value);

    private static CheckValue B(bool value) => CheckValue.Bool(value);

    public static CheckResult SystemCheck()
    {
        var ram1 = new CheckSubject("ram-1");
        var ram2 = new CheckSubject("ram-2");
        var gpu1 = new CheckSubject("gpu-1", "NVIDIA GeForce RTX 5070");
        var gpu2 = new CheckSubject("gpu-2", "AMD Radeon(TM) Graphics");
        var disk1 = new CheckSubject("disk-1", "Samsung SSD 980 PRO 2TB");
        var disk2 = new CheckSubject("disk-2", "CT500MX500SSD1");
        var vol1 = new CheckSubject("volume-1");
        var vol2 = new CheckSubject("volume-2");
        var pagefile = new CheckSubject("pagefile-1");
        var belowRated = CheckEvidence.OfCode(CheckEvidenceCode.BelowRated);

        List<CheckItem> items =
        [
            I("system.os.version", CheckValue.OsVersion(new OsVersionValue("Windows 11 Pro", "25H2", 26300, 7171)), ItemVerdict.Info, CheckSource.Os),
            I("system.os.uptime", N(273600.5), ItemVerdict.Ok, CheckSource.Os),
            I("system.cpu.model", T("AMD Ryzen 7 7800X3D 8-Core Processor"), ItemVerdict.Info, CheckSource.Wmi),
            I("system.cpu.cores", Int(8), ItemVerdict.Info, CheckSource.Win32),
            I("system.cpu.logical", Int(16), ItemVerdict.Info, CheckSource.Win32),
            I("system.cpu.rated_clock", N(4200), ItemVerdict.Info, CheckSource.Wmi),
            I("system.memory.modules", Int(2), ItemVerdict.Ok, CheckSource.Smbios),
            I("system.memory.capacity", D("17179869184"), ItemVerdict.Info, CheckSource.Smbios, subject: ram1),
            I("system.memory.capacity", D("17179869184"), ItemVerdict.Info, CheckSource.Smbios, subject: ram2),
            I("system.memory.rated_speed", Int(6000), ItemVerdict.Info, CheckSource.Smbios, subject: ram1),
            I("system.memory.rated_speed", Int(6000), ItemVerdict.Info, CheckSource.Smbios, subject: ram2),
            I("system.memory.configured_speed", Int(4800), ItemVerdict.Caution, CheckSource.Smbios, subject: ram1, evidence: belowRated),
            I("system.memory.configured_speed", Int(4800), ItemVerdict.Caution, CheckSource.Smbios, subject: ram2, evidence: belowRated),
            I("system.gpu.adapters", Int(2), ItemVerdict.Info, CheckSource.Dxgi),
            I("system.gpu.model", T("NVIDIA GeForce RTX 5070"), ItemVerdict.Info, CheckSource.Dxgi, subject: gpu1),
            I("system.gpu.driver_version", T("32.0.15.8180"), ItemVerdict.Info, CheckSource.Setupapi, subject: gpu1),
            I("system.gpu.driver_age", Int(41), ItemVerdict.Ok, CheckSource.Setupapi, subject: gpu1),
            I("system.gpu.device_status", Int(0), ItemVerdict.Ok, CheckSource.Setupapi, subject: gpu1),
            I("system.gpu.generic_driver", B(false), ItemVerdict.Ok, CheckSource.Dxgi, subject: gpu1),

            // A disabled adapter (configuration code 22): the status row is ok, and the two driver rows are info (DESIGN-A §14.0, exception 2).
            I("system.gpu.model", T("AMD Radeon(TM) Graphics"), ItemVerdict.Info, CheckSource.Dxgi, subject: gpu2),
            I("system.gpu.driver_version", T("32.0.21025.1042"), ItemVerdict.Info, CheckSource.Setupapi, subject: gpu2),
            I("system.gpu.driver_age", Int(900), ItemVerdict.Info, CheckSource.Setupapi, subject: gpu2),
            I("system.gpu.device_status", Int(22), ItemVerdict.Ok, CheckSource.Setupapi, subject: gpu2, evidence: CheckEvidence.OfCode(CheckEvidenceCode.DisabledCode22)),
            I("system.gpu.generic_driver", B(false), ItemVerdict.Info, CheckSource.Dxgi, subject: gpu2),

            I("system.storage.devices", Int(2), ItemVerdict.Info, CheckSource.StorageQuery),
            I("system.storage.health", T("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: disk1),
            I("system.storage.health", T("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: disk2),
            I("system.storage.free", D("812345678901"), ItemVerdict.Ok, CheckSource.Win32, subject: vol1, evidence: new CheckEvidence { TotalBytes = "2000398934016", DiskId = "disk-1" }),
            I("system.storage.free", D("40012345678"), ItemVerdict.Caution, CheckSource.Win32, subject: vol2, evidence: new CheckEvidence { TotalBytes = "500107862016" }),
            I("system.pagefile.count", Int(1), ItemVerdict.Ok, CheckSource.Wmi),
            I("system.pagefile.automatic", B(true), ItemVerdict.Info, CheckSource.Wmi, subject: pagefile),
            I("system.pagefile.allocated", D("4294967296"), ItemVerdict.Info, CheckSource.Wmi, subject: pagefile),
            I("system.pagefile.current", D("1073741824"), ItemVerdict.Info, CheckSource.Wmi, subject: pagefile),
            I("system.pagefile.peak", D("2147483648"), ItemVerdict.Ok, CheckSource.Wmi, subject: pagefile),
            I("system.eventlog.whea7d", Int(0), ItemVerdict.Ok, CheckSource.Eventlog),
            I("system.eventlog.whea_warning7d", Int(0), ItemVerdict.Ok, CheckSource.Eventlog),
            I("system.eventlog.disk7d", Int(0), ItemVerdict.Ok, CheckSource.Eventlog),
            I("system.eventlog.disk_warning7d", Int(2), ItemVerdict.Info, CheckSource.Eventlog),
            I("system.eventlog.ntfs7d", Int(0), ItemVerdict.Ok, CheckSource.Eventlog),
            I("system.eventlog.ntfs_warning7d", Int(0), ItemVerdict.Info, CheckSource.Eventlog),
            I("system.eventlog.stornvme7d", Int(0), ItemVerdict.Ok, CheckSource.Eventlog),
            I("system.eventlog.stornvme_warning7d", Int(0), ItemVerdict.Info, CheckSource.Eventlog),

            // The event log could not be read for this provider (no permission): a judged item without a value is unavailable.
            I("system.eventlog.volmgr7d", null, ItemVerdict.Unavailable, CheckSource.Eventlog, CheckReason.Denied),
            I("system.eventlog.volmgr_warning7d", null, ItemVerdict.Info, CheckSource.Eventlog, CheckReason.Denied),
            I("system.eventlog.kp41_7d", Int(1), ItemVerdict.Caution, CheckSource.Eventlog),
        ];

        return CheckResult.Create(App, SystemRun, CheckKind.System, Start, Start.AddMilliseconds(1250), CheckOutcome.Completed, items);
    }

    public static CheckResult CpuCheck()
    {
        var cpu1 = new CheckSubject("cpu-1");
        var cpu2 = new CheckSubject("cpu-2");

        List<CheckItem> items =
        [
            I("cpu.duration", N(30.02), ItemVerdict.Info, CheckSource.Load),
            I("cpu.chunks", Int(1200), ItemVerdict.Info, CheckSource.Load),
            I("cpu.compute_errors", Int(0), ItemVerdict.Ok, CheckSource.Load),
            I("cpu.throughput", N(39.97), ItemVerdict.Info, CheckSource.Load),
            I("cpu.steadiness", N(96.25), ItemVerdict.Ok, CheckSource.Load, evidence: new CheckEvidence { Cv = 2.5 }),
            I("cpu.variation", N(2.5), ItemVerdict.Info, CheckSource.Load),
            I("cpu.logical_throughput", N(2.5), ItemVerdict.Info, CheckSource.Load, subject: cpu1),
            I("cpu.logical_throughput", N(1.5), ItemVerdict.Info, CheckSource.Load, subject: cpu2),
            I("cpu.peer_ratio", N(100), ItemVerdict.Ok, CheckSource.Load, subject: cpu1, evidence: new CheckEvidence { EfficiencyClass = 0 }),
            I("cpu.peer_ratio", N(60), ItemVerdict.Caution, CheckSource.Load, subject: cpu2, evidence: new CheckEvidence { EfficiencyClass = 0 }),
            I("cpu.effective_clock", N(4100.5), ItemVerdict.Ok, CheckSource.Pdh, evidence: new CheckEvidence { ClockSteadiness = 98.5 }),
            I("cpu.utilization", N(99.1), ItemVerdict.Info, CheckSource.Pdh),
            I("cpu.package_power", null, ItemVerdict.Info, CheckSource.EnergyMeter, CheckReason.Unsupported),
        ];

        return CheckResult.Create(App, CpuRun, CheckKind.Cpu, Start, Start.AddMilliseconds(30500), CheckOutcome.Completed, items, [CheckNotes.CpuTemperatureNotMeasured]);
    }

    /// <summary>The DESIGN-A §3.4 example, built through the API.</summary>
    public static CheckResult MemoryCheck()
    {
        List<CheckItem> items =
        [
            I("memory.duration", N(4.2), ItemVerdict.Info, CheckSource.Load),
            I("memory.total", D("34359738368"), ItemVerdict.Info, CheckSource.Win32),
            I("memory.available_at_start", D("12884901888"), ItemVerdict.Info, CheckSource.Win32),
            I("memory.borrowed", D("3221225472"), ItemVerdict.Info, CheckSource.Load),
            I("memory.passes", null, ItemVerdict.Info, CheckSource.Load, CheckReason.Partial),
            I("memory.verified", D("134217728"), ItemVerdict.Info, CheckSource.Load, CheckReason.Partial),
            I("memory.mismatches", Int(1), ItemVerdict.Check, CheckSource.Load, CheckReason.Partial, evidence: CheckEvidence.OfCode(CheckEvidenceCode.PatternMismatch)),
            I("memory.write_rate", N(6400), ItemVerdict.Info, CheckSource.Load),
            I("memory.verify_rate", N(5300), ItemVerdict.Info, CheckSource.Load),
        ];

        return CheckResult.Create(App, MemoryRun, CheckKind.Memory, Start, Start.AddMilliseconds(4200), CheckOutcome.Stopped, items, [CheckNotes.MemoryBorrowedRegionOnly]);
    }

    public static CheckResult StorageCheck()
    {
        var disk1 = new CheckSubject("disk-1", "Samsung SSD 980 PRO 2TB");
        var disk2 = new CheckSubject("disk-2", "CT500MX500SSD1");
        var since = Start.AddDays(-3);

        CheckEvidence Delta(string before, string after, CheckEvidenceCode? code = null) =>
            new() { Code = code, Before = before, After = after, SinceUtc = since };

        CheckEvidence NotComparable(CheckEvidenceCode code) => CheckEvidence.OfCode(code);

        List<CheckItem> items =
        [
            I("storage.devices", Int(2), ItemVerdict.Info, CheckSource.StorageQuery),

            // disk-1: NVMe. baseline_missing_counter is about one row (the last record had no spare), so the other deltas are compared (DESIGN-A §3.5-7).
            I("storage.health", T("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: disk1),
            I("storage.capacity", D("2000398934016"), ItemVerdict.Info, CheckSource.StorageQuery, subject: disk1),
            I("storage.temperature", N(41), ItemVerdict.Ok, CheckSource.Nvme, subject: disk1),
            I("storage.nvme.critical_warning", Int(0), ItemVerdict.Ok, CheckSource.Nvme, subject: disk1),
            I("storage.nvme.available_spare", Int(100), ItemVerdict.Ok, CheckSource.Nvme, subject: disk1),
            I("storage.nvme.spare_threshold", N(10), ItemVerdict.Info, CheckSource.Nvme, subject: disk1),
            I("storage.nvme.percentage_used", Int(3), ItemVerdict.Ok, CheckSource.Nvme, subject: disk1),
            I("storage.nvme.media_errors", D("0"), ItemVerdict.Ok, CheckSource.Nvme, subject: disk1),
            I("storage.nvme.error_log_entries", D("15"), ItemVerdict.Caution, CheckSource.Nvme, subject: disk1, evidence: CheckEvidence.OfCode(CheckEvidenceCode.CounterIncreased)),
            I("storage.nvme.unsafe_shutdowns", D("45"), ItemVerdict.Info, CheckSource.Nvme, subject: disk1),
            I("storage.nvme.power_cycles", D("812"), ItemVerdict.Info, CheckSource.Nvme, subject: disk1),
            I("storage.nvme.power_on_hours", D("9100"), ItemVerdict.Info, CheckSource.Nvme, subject: disk1),
            I("storage.nvme.data_units_written", D("123456789"), ItemVerdict.Info, CheckSource.Nvme, subject: disk1),
            I("storage.nvme.critical_warning.delta", SD("0"), ItemVerdict.Ok, CheckSource.Baseline, subject: disk1, evidence: Delta("0", "0")),
            I("storage.nvme.available_spare.delta", null, ItemVerdict.Info, CheckSource.Baseline, CheckReason.Partial, disk1, NotComparable(CheckEvidenceCode.BaselineMissingCounter)),
            I("storage.nvme.percentage_used.delta", SD("0"), ItemVerdict.Info, CheckSource.Baseline, subject: disk1, evidence: Delta("3", "3")),
            I("storage.nvme.media_errors.delta", SD("0"), ItemVerdict.Ok, CheckSource.Baseline, subject: disk1, evidence: Delta("0", "0")),
            I("storage.nvme.error_log_entries.delta", SD("3"), ItemVerdict.Caution, CheckSource.Baseline, subject: disk1, evidence: Delta("12", "15", CheckEvidenceCode.CounterIncreased)),
            I("storage.nvme.unsafe_shutdowns.delta", SD("1"), ItemVerdict.Info, CheckSource.Baseline, subject: disk1, evidence: Delta("44", "45")),
            I("storage.nvme.power_on_hours.delta", SD("12"), ItemVerdict.Info, CheckSource.Baseline, subject: disk1, evidence: Delta("9088", "9100")),
            I("storage.nvme.data_units_written.delta", SD("89"), ItemVerdict.Info, CheckSource.Baseline, subject: disk1, evidence: Delta("123456700", "123456789")),

            // disk-2: SATA, never recorded before. baseline_first is about the whole disk: every .delta of it is null with the same code (DESIGN-A §3.5-7).
            I("storage.health", T("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: disk2),
            I("storage.capacity", D("500107862016"), ItemVerdict.Info, CheckSource.StorageQuery, subject: disk2),
            I("storage.temperature", N(35.5), ItemVerdict.Ok, CheckSource.Ata, subject: disk2),
            I("storage.ata.predict_failure", B(false), ItemVerdict.Ok, CheckSource.Ata, subject: disk2),
            I("storage.ata.threshold_status", T("within"), ItemVerdict.Ok, CheckSource.Ata, subject: disk2),
            I("storage.ata.5", D("0"), ItemVerdict.Ok, CheckSource.Ata, subject: disk2),
            I("storage.ata.197", D("0"), ItemVerdict.Ok, CheckSource.Ata, subject: disk2),
            I("storage.ata.199", D("5"), ItemVerdict.Caution, CheckSource.Ata, subject: disk2, evidence: CheckEvidence.OfCode(CheckEvidenceCode.LikelyCable)),
            I("storage.ata.9", D("21000"), ItemVerdict.Info, CheckSource.Ata, subject: disk2),
            I("storage.ata.5.delta", null, ItemVerdict.Info, CheckSource.Baseline, CheckReason.Partial, disk2, NotComparable(CheckEvidenceCode.BaselineFirst)),
            I("storage.ata.197.delta", null, ItemVerdict.Info, CheckSource.Baseline, CheckReason.Partial, disk2, NotComparable(CheckEvidenceCode.BaselineFirst)),
            I("storage.ata.199.delta", null, ItemVerdict.Info, CheckSource.Baseline, CheckReason.Partial, disk2, NotComparable(CheckEvidenceCode.BaselineFirst)),
            I("storage.ata.9.delta", null, ItemVerdict.Info, CheckSource.Baseline, CheckReason.Partial, disk2, NotComparable(CheckEvidenceCode.BaselineFirst)),
        ];

        return CheckResult.Create(App, StorageRun, CheckKind.Storage, Start, Start.AddMilliseconds(820), CheckOutcome.Completed, items);
    }

    public static IReadOnlyList<(string Name, CheckResult Result)> All { get; } =
    [
        ("system.json", SystemCheck()),
        ("cpu.json", CpuCheck()),
        ("memory.json", MemoryCheck()),
        ("storage.json", StorageCheck()),
    ];

    /// <summary>One valid item for every registered ID of a kind (a subject "xxx-1" where the ID is repeated). Catches a type, a unit or a subject that the registry and the converter disagree on.</summary>
    public static CheckResult EveryId(CheckKind kind)
    {
        var items = new List<CheckItem>();
        foreach (var spec in CheckItemRegistry.ForKind(kind))
        {
            var verdict = spec.Class == ItemClass.Info ? ItemVerdict.Info : ItemVerdict.Ok;
            CheckSubject? subject = spec.IsRepeated ? new CheckSubject(SubjectKinds.Prefix(spec.Subject) + "-1") : null;
            CheckEvidence? evidence = spec.IsDelta ? new CheckEvidence { Before = "1", After = "3", SinceUtc = Start } : null;
            CheckValue value = spec.ValueType switch
            {
                // A device status of 0, so the same GPU's driver rows are judged (DESIGN-A §14.0).
                CheckValueType.Int => CheckValue.Int(spec.Id == CheckItemRegistry.GpuDeviceStatusId ? 0 : 1),
                CheckValueType.Number => CheckValue.Number(1.5),
                CheckValueType.Dec => CheckValue.Dec("1"),
                CheckValueType.SignedDec => CheckValue.SignedDec("2"),
                CheckValueType.Text => CheckValue.Text(spec.AllowedText?[0] ?? "text"),
                CheckValueType.Bool => CheckValue.Bool(true),
                CheckValueType.OsVersion => CheckValue.OsVersion(new OsVersionValue("Windows 11", null, 26100, null)),
                _ => throw new InvalidOperationException(spec.ValueType.ToString()),
            };
            items.Add(CheckItem.Create(spec.Id, value, verdict, CheckSource.Os, null, subject, evidence));
        }

        return CheckResult.Create(App, Guid.Parse("5f6a7b8c-9d0e-4f1a-8b2c-3d4e5f6a7b8c"), kind, Start, Start.AddSeconds(1), CheckOutcome.Completed, items);
    }

    public static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH':'mm':'ss'.'fff'Z'", CultureInfo.InvariantCulture);
}
