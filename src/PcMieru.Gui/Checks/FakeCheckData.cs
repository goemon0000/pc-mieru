using PcMieru.Checks;

namespace PcMieru.Gui;

/// <summary>
/// The sample results of the fake checks (<c>--fakes</c>, and the screens' tests): small, valid pcm-check/1 results that show every verdict, an
/// observation row, a subject with its own rows, a storage counter that grew (<c>1,204 → 1,227（+23）</c>), a delta that could not be compared and
/// a disk that Windows refused under normal rights. Made through the public API, so each one passes <see cref="CheckResultValidator"/>. Nothing here
/// touches the machine.
/// </summary>
internal static class FakeCheckData
{
    public const string App = "PCMieru 0.0.0 (sample)";

    /// <summary>The first sample run's start (the history lists the samples from here backwards).</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);

    public static readonly Guid SystemRun = Guid.Parse("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");
    public static readonly Guid CpuRun = Guid.Parse("1b2c3d4e-5f6a-4b7c-8d9e-0f1a2b3c4d5e");
    public static readonly Guid MemoryRun = Guid.Parse("89914ef9-5781-4cf7-a4f8-6d58003ddf86");
    public static readonly Guid StorageRun = Guid.Parse("3d4e5f6a-7b8c-4d9e-8f0a-1b2c3d4e5f6a");

    /// <summary>The runs the in-memory history starts with, newest first.</summary>
    public static IReadOnlyList<CheckResult> Seed { get; } =
    [
        Storage(StorageRun, Start, elevated: false),
        SystemCheck(SystemRun, Start.AddDays(-1)),
        StoppedMemory(MemoryRun, Start.AddDays(-2)),
        Cpu(CpuRun, Start.AddDays(-3), 30),
    ];

    /// <summary>The result a completed run of this request gives. <paramref name="seconds"/> is the load's length (ignored by the others).</summary>
    public static CheckResult For(CheckRequest request, Guid runId, DateTimeOffset start, double seconds) => request.Kind switch
    {
        CheckKind.System => SystemCheck(runId, start),
        CheckKind.Cpu => Cpu(runId, start, seconds),
        CheckKind.Memory => Memory(runId, start, seconds),
        CheckKind.Storage => Storage(runId, start, request.AllowElevation),
        _ => throw new NotSupportedException(request.Kind.ToString()),
    };

    /// <summary>What a run that was stopped leaves: a load keeps the little it had counted, the others have nothing yet.</summary>
    public static CheckResult Stopped(CheckKind kind, Guid runId, DateTimeOffset start, double elapsedSeconds) => kind switch
    {
        CheckKind.Cpu => StoppedCpu(runId, start, elapsedSeconds),
        CheckKind.Memory => StoppedMemory(runId, start, elapsedSeconds),
        _ => CheckResult.Create(App, runId, kind, start, start.AddMilliseconds(Millis(elapsedSeconds)), CheckOutcome.Stopped, []),
    };

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

    private static int Millis(double seconds) => (int)Math.Max(0, Math.Round(seconds * 1000));

    public static CheckResult SystemCheck(Guid runId, DateTimeOffset start)
    {
        var ram1 = new CheckSubject("ram-1");
        var ram2 = new CheckSubject("ram-2");
        var gpu1 = new CheckSubject("gpu-1", "NVIDIA GeForce RTX 5070");
        var disk1 = new CheckSubject("disk-1", "Samsung SSD 980 PRO 2TB");
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
            I("system.gpu.adapters", Int(1), ItemVerdict.Info, CheckSource.Dxgi),
            I("system.gpu.model", T("NVIDIA GeForce RTX 5070"), ItemVerdict.Info, CheckSource.Dxgi, subject: gpu1),
            I("system.gpu.driver_version", T("32.0.15.8180"), ItemVerdict.Info, CheckSource.Setupapi, subject: gpu1),
            I("system.gpu.driver_age", Int(41), ItemVerdict.Ok, CheckSource.Setupapi, subject: gpu1),
            I("system.gpu.device_status", Int(0), ItemVerdict.Ok, CheckSource.Setupapi, subject: gpu1),
            I("system.gpu.generic_driver", B(false), ItemVerdict.Ok, CheckSource.Dxgi, subject: gpu1),
            I("system.storage.devices", Int(1), ItemVerdict.Info, CheckSource.StorageQuery),
            I("system.storage.health", T("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: disk1),
            I("system.storage.free", D("812345678901"), ItemVerdict.Ok, CheckSource.Win32, subject: vol1, evidence: new CheckEvidence { TotalBytes = "2000398934016", DiskId = "disk-1" }),
            I("system.storage.free", D("40012345678"), ItemVerdict.Caution, CheckSource.Win32, subject: vol2, evidence: new CheckEvidence { TotalBytes = "500107862016" }),
            I("system.pagefile.count", Int(1), ItemVerdict.Ok, CheckSource.Wmi),
            I("system.pagefile.automatic", B(true), ItemVerdict.Info, CheckSource.Wmi),
            I("system.pagefile.allocated", D("4294967296"), ItemVerdict.Info, CheckSource.Wmi, subject: pagefile),
            I("system.pagefile.current", D("1073741824"), ItemVerdict.Info, CheckSource.Wmi, subject: pagefile),
            I("system.pagefile.peak", D("2147483648"), ItemVerdict.Ok, CheckSource.Wmi, subject: pagefile),
            I("system.eventlog.whea7d", Int(0), ItemVerdict.Ok, CheckSource.Eventlog),
            I("system.eventlog.disk7d", Int(0), ItemVerdict.Ok, CheckSource.Eventlog),
            I("system.eventlog.disk_warning7d", Int(2), ItemVerdict.Info, CheckSource.Eventlog),

            // The event log could not be read for this provider (no permission): a judged item without a value is "not measured".
            I("system.eventlog.volmgr7d", null, ItemVerdict.Unavailable, CheckSource.Eventlog, CheckReason.Denied),
            I("system.eventlog.volmgr_warning7d", null, ItemVerdict.Info, CheckSource.Eventlog, CheckReason.Denied),
            I("system.eventlog.kp41_7d", Int(1), ItemVerdict.Caution, CheckSource.Eventlog),
            I("system.eventlog.ntfs7d", Int(3), ItemVerdict.Check, CheckSource.Eventlog),
        ];

        return CheckResult.Create(App, runId, CheckKind.System, start, start.AddMilliseconds(1250), CheckOutcome.Completed, items);
    }

    public static CheckResult Cpu(Guid runId, DateTimeOffset start, double seconds)
    {
        var cpu1 = new CheckSubject("cpu-1");
        var cpu2 = new CheckSubject("cpu-2");

        List<CheckItem> items =
        [
            I("cpu.duration", N(seconds), ItemVerdict.Info, CheckSource.Load),
            I("cpu.chunks", Int((long)(seconds * 40)), ItemVerdict.Info, CheckSource.Load),
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

        return CheckResult.Create(App, runId, CheckKind.Cpu, start, start.AddMilliseconds(Millis(seconds)), CheckOutcome.Completed, items, [CheckNotes.CpuTemperatureNotMeasured]);
    }

    public static CheckResult Memory(Guid runId, DateTimeOffset start, double seconds)
    {
        List<CheckItem> items =
        [
            I("memory.duration", N(seconds), ItemVerdict.Info, CheckSource.Load),
            I("memory.total", D("34359738368"), ItemVerdict.Info, CheckSource.Win32),
            I("memory.available_at_start", D("12884901888"), ItemVerdict.Info, CheckSource.Win32),
            I("memory.borrowed", D("3221225472"), ItemVerdict.Info, CheckSource.Load),
            I("memory.passes", Int(3), ItemVerdict.Info, CheckSource.Load),
            I("memory.verified", D("9663676416"), ItemVerdict.Info, CheckSource.Load),
            I("memory.mismatches", Int(0), ItemVerdict.Ok, CheckSource.Load),
            I("memory.write_rate", N(6400), ItemVerdict.Info, CheckSource.Load),
            I("memory.verify_rate", N(5300), ItemVerdict.Info, CheckSource.Load),
        ];

        return CheckResult.Create(App, runId, CheckKind.Memory, start, start.AddMilliseconds(Millis(seconds)), CheckOutcome.Completed, items, [CheckNotes.MemoryBorrowedRegionOnly]);
    }

    private static CheckResult StoppedCpu(Guid runId, DateTimeOffset start, double seconds)
    {
        List<CheckItem> items =
        [
            I("cpu.duration", N(seconds), ItemVerdict.Info, CheckSource.Load),
            I("cpu.chunks", Int((long)(seconds * 40)), ItemVerdict.Info, CheckSource.Load),
        ];

        return CheckResult.Create(App, runId, CheckKind.Cpu, start, start.AddMilliseconds(Millis(seconds)), CheckOutcome.Stopped, items, [CheckNotes.CpuTemperatureNotMeasured]);
    }

    /// <summary>The stopped memory run of DESIGN-A §3.4: what was verified by then is kept, and a mismatch it found says so.</summary>
    public static CheckResult StoppedMemory(Guid runId, DateTimeOffset start, double seconds = 4.2)
    {
        List<CheckItem> items =
        [
            I("memory.duration", N(seconds), ItemVerdict.Info, CheckSource.Load),
            I("memory.total", D("34359738368"), ItemVerdict.Info, CheckSource.Win32),
            I("memory.available_at_start", D("12884901888"), ItemVerdict.Info, CheckSource.Win32),
            I("memory.borrowed", D("3221225472"), ItemVerdict.Info, CheckSource.Load),
            I("memory.passes", null, ItemVerdict.Info, CheckSource.Load, CheckReason.Partial),
            I("memory.verified", D("134217728"), ItemVerdict.Info, CheckSource.Load, CheckReason.Partial),
            I("memory.mismatches", Int(1), ItemVerdict.Check, CheckSource.Load, CheckReason.Partial, evidence: CheckEvidence.OfCode(CheckEvidenceCode.PatternMismatch)),
            I("memory.write_rate", N(6400), ItemVerdict.Info, CheckSource.Load),
            I("memory.verify_rate", N(5300), ItemVerdict.Info, CheckSource.Load),
        ];

        return CheckResult.Create(App, runId, CheckKind.Memory, start, start.AddMilliseconds(Millis(seconds)), CheckOutcome.Stopped, items, [CheckNotes.MemoryBorrowedRegionOnly]);
    }

    /// <summary>
    /// The storage check. Under normal rights (<paramref name="elevated"/> false) the second disk is refused and says so; read again as the
    /// administrator it is read like the first. The first disk shows a counter that grew (12 → 15 and 44 → 45), one that did not, and one delta
    /// that could not be compared.
    /// </summary>
    public static CheckResult Storage(Guid runId, DateTimeOffset start, bool elevated)
    {
        var disk1 = new CheckSubject("disk-1", "Samsung SSD 980 PRO 2TB");
        var disk2 = new CheckSubject("disk-2", "CT500MX500SSD1");
        var since = start.AddDays(-3);

        CheckEvidence Delta(string before, string after, CheckEvidenceCode? code = null) =>
            new() { Code = code, Before = before, After = after, SinceUtc = since };

        List<CheckItem> items =
        [
            I("storage.devices", Int(2), ItemVerdict.Info, CheckSource.StorageQuery),

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
            I("storage.nvme.power_on_hours", D("1227"), ItemVerdict.Info, CheckSource.Nvme, subject: disk1),
            I("storage.nvme.critical_warning.delta", SD("0"), ItemVerdict.Ok, CheckSource.Baseline, subject: disk1, evidence: Delta("0", "0")),
            I("storage.nvme.available_spare.delta", null, ItemVerdict.Info, CheckSource.Baseline, CheckReason.Partial, disk1, CheckEvidence.OfCode(CheckEvidenceCode.BaselineMissingCounter)),
            I("storage.nvme.media_errors.delta", SD("0"), ItemVerdict.Ok, CheckSource.Baseline, subject: disk1, evidence: Delta("0", "0")),
            I("storage.nvme.error_log_entries.delta", SD("3"), ItemVerdict.Caution, CheckSource.Baseline, subject: disk1, evidence: Delta("12", "15", CheckEvidenceCode.CounterIncreased)),
            I("storage.nvme.unsafe_shutdowns.delta", SD("1"), ItemVerdict.Info, CheckSource.Baseline, subject: disk1, evidence: Delta("44", "45")),
            I("storage.nvme.power_on_hours.delta", SD("23"), ItemVerdict.Info, CheckSource.Baseline, subject: disk1, evidence: Delta("1204", "1227")),
        ];

        if (elevated)
        {
            items.AddRange(
            [
                I("storage.health", T("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: disk2),
                I("storage.capacity", D("500107862016"), ItemVerdict.Info, CheckSource.StorageQuery, subject: disk2),
                I("storage.temperature", N(35.5), ItemVerdict.Ok, CheckSource.Ata, subject: disk2),
                I("storage.ata.predict_failure", B(false), ItemVerdict.Ok, CheckSource.Ata, subject: disk2),
                I("storage.ata.5", D("0"), ItemVerdict.Ok, CheckSource.Ata, subject: disk2),
                I("storage.ata.199", D("5"), ItemVerdict.Caution, CheckSource.Ata, subject: disk2, evidence: CheckEvidence.OfCode(CheckEvidenceCode.LikelyCable)),
                I("storage.ata.5.delta", null, ItemVerdict.Info, CheckSource.Baseline, CheckReason.Partial, disk2, CheckEvidence.OfCode(CheckEvidenceCode.BaselineFirst)),
                I("storage.ata.199.delta", null, ItemVerdict.Info, CheckSource.Baseline, CheckReason.Partial, disk2, CheckEvidence.OfCode(CheckEvidenceCode.BaselineFirst)),
            ]);
        }
        else
        {
            items.AddRange(
            [
                I("storage.health", null, ItemVerdict.Unavailable, CheckSource.StorageQuery, CheckReason.Denied, disk2),
                I("storage.capacity", D("500107862016"), ItemVerdict.Info, CheckSource.StorageQuery, subject: disk2),
                I("storage.temperature", null, ItemVerdict.Unavailable, CheckSource.Ata, CheckReason.Denied, disk2),
            ]);
        }

        return CheckResult.Create(App, runId, CheckKind.Storage, start, start.AddMilliseconds(820), CheckOutcome.Completed, items);
    }
}
