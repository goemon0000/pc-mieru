using System.Globalization;

namespace PcMieru.Checks;

/// <summary>Whether an ID is judged or observation-only (DESIGN-A §3.0). Fixed per ID.</summary>
public enum ItemClass
{
    /// <summary>Gets ok, caution, check or unavailable, and counts in the overall verdict. Two exceptions (see <see cref="CheckItemSpec.MayBeInfo"/>).</summary>
    Judged,

    /// <summary>Always info. Never counts in the overall verdict, with or without a value.</summary>
    Info,
}

/// <summary>What a repeated item is about. The subject id of that item is "&lt;prefix&gt;-&lt;number&gt;" (DESIGN-A §3).</summary>
public enum SubjectKind
{
    /// <summary>Not repeated: the item has no subject.</summary>
    None,

    /// <summary>A logical CPU: "cpu-N" (N from 1).</summary>
    Cpu,

    /// <summary>A physical disk: "disk-N".</summary>
    Disk,

    /// <summary>A display adapter: "gpu-N" (from 1).</summary>
    Gpu,

    /// <summary>A page file: "pagefile-N" (from 1).</summary>
    Pagefile,

    /// <summary>A memory module: "ram-N" (from 1).</summary>
    Ram,

    /// <summary>A local volume: "volume-N" (from 1).</summary>
    Volume,
}

public static class SubjectKinds
{
    public static string? Prefix(SubjectKind kind) => kind switch
    {
        SubjectKind.None => null,
        SubjectKind.Cpu => "cpu",
        SubjectKind.Disk => "disk",
        SubjectKind.Gpu => "gpu",
        SubjectKind.Pagefile => "pagefile",
        SubjectKind.Ram => "ram",
        SubjectKind.Volume => "volume",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// "prefix-N" with N a canonical positive integer (no sign, no leading zeros). Every kind starts at 1, "cpu-N" too
    /// (DESIGN-A §3.5-4).
    /// </summary>
    public static bool IsValidId(SubjectKind kind, string? id)
    {
        var prefix = Prefix(kind);
        if (prefix is null || id is null || id.Length <= prefix.Length + 1 || !id.StartsWith(prefix, StringComparison.Ordinal) || id[prefix.Length] != '-')
        {
            return false;
        }

        var number = id.AsSpan(prefix.Length + 1);
        if (number.Length > 9 || number[0] == '0')
        {
            return false;
        }

        foreach (var c in number)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One registered item ID (DESIGN-A §3.1-§3.3, §14). The ID, the kind of result it belongs to, the value type, the
/// unit and the judged/info class are fixed. A repeated item (per RAM module, GPU, disk, ...) is the same ID with a
/// subject of the given <see cref="Subject"/> kind. The 15 counters that are compared with the last run also have an
/// ID of their own with ".delta" added.
/// </summary>
public sealed record CheckItemSpec(
    string Id,
    CheckKind Kind,
    CheckValueType ValueType,
    string? Unit,
    ItemClass Class,
    SubjectKind Subject = SubjectKind.None,
    string? DeltaOf = null,
    IReadOnlyList<string>? AllowedText = null,
    bool InfoWhenGpuStatusNonZero = false)
{
    public bool IsDelta => DeltaOf is not null;

    public bool IsRepeated => Subject != SubjectKind.None;

    /// <summary>
    /// Whether a judged ID may carry verdict info. Only the two DESIGN-A §14.0 exceptions: a .delta without a value
    /// (not comparable), and the GPU driver rows when the device status is not 0. The validator enforces both
    /// directions of each (a .delta is info exactly when it has no value; a GPU driver row is info exactly when the same
    /// GPU's device status is non-zero). An info ID is always info.
    /// </summary>
    public bool MayBeInfo => Class == ItemClass.Info || IsDelta || InfoWhenGpuStatusNonZero;
}

/// <summary>
/// The item-ID registry. The single place that knows which IDs exist: the writer, the reader and the validator all
/// refuse an ID that is not here, so an addition is a deliberate change to this table (and to the design table).
/// </summary>
public static class CheckItemRegistry
{
    private static readonly string[] HealthValues = ["healthy", "warning", "critical"];
    private static readonly string[] ThresholdValues = ["within", "past_breach", "current_breach"];

    private static readonly Dictionary<string, CheckItemSpec> ById = new(StringComparer.Ordinal);
    private static readonly List<CheckItemSpec> Ordered = [];
    private static readonly Dictionary<CheckEvidenceCode, HashSet<string>> CodeRows = [];

    static CheckItemRegistry()
    {
        // System (DESIGN-A §3.1 / §14.1)
        const CheckKind sys = CheckKind.System;
        Add(sys, "system.os.version", CheckValueType.OsVersion, null, ItemClass.Info);
        Add(sys, "system.os.uptime", CheckValueType.Number, "s", ItemClass.Judged);
        Add(sys, "system.cpu.model", CheckValueType.Text, null, ItemClass.Info);
        Add(sys, "system.cpu.cores", CheckValueType.Int, "count", ItemClass.Info);
        Add(sys, "system.cpu.logical", CheckValueType.Int, "count", ItemClass.Info);
        Add(sys, "system.cpu.rated_clock", CheckValueType.Number, "MHz", ItemClass.Info);
        Add(sys, "system.memory.modules", CheckValueType.Int, "count", ItemClass.Judged);
        Add(sys, "system.memory.capacity", CheckValueType.Dec, "B", ItemClass.Info, SubjectKind.Ram);
        Add(sys, "system.memory.rated_speed", CheckValueType.Int, "MT/s", ItemClass.Info, SubjectKind.Ram);
        Add(sys, "system.memory.configured_speed", CheckValueType.Int, "MT/s", ItemClass.Judged, SubjectKind.Ram);
        Add(sys, "system.gpu.adapters", CheckValueType.Int, "count", ItemClass.Info);
        Add(sys, "system.gpu.model", CheckValueType.Text, null, ItemClass.Info, SubjectKind.Gpu);
        Add(sys, "system.gpu.driver_version", CheckValueType.Text, null, ItemClass.Info, SubjectKind.Gpu);
        Add(sys, "system.gpu.driver_age", CheckValueType.Int, "d", ItemClass.Judged, SubjectKind.Gpu, gpuStatus: true);
        Add(sys, "system.gpu.device_status", CheckValueType.Int, "code", ItemClass.Judged, SubjectKind.Gpu);
        Add(sys, "system.gpu.generic_driver", CheckValueType.Bool, null, ItemClass.Judged, SubjectKind.Gpu, gpuStatus: true);
        Add(sys, "system.storage.devices", CheckValueType.Int, "count", ItemClass.Info);
        Add(sys, "system.storage.health", CheckValueType.Text, null, ItemClass.Judged, SubjectKind.Disk, allowedText: HealthValues);
        Add(sys, "system.storage.free", CheckValueType.Dec, "B", ItemClass.Judged, SubjectKind.Volume);
        Add(sys, "system.pagefile.count", CheckValueType.Int, "count", ItemClass.Judged);
        Add(sys, "system.pagefile.automatic", CheckValueType.Bool, null, ItemClass.Info, SubjectKind.Pagefile);
        Add(sys, "system.pagefile.allocated", CheckValueType.Dec, "B", ItemClass.Info, SubjectKind.Pagefile);
        Add(sys, "system.pagefile.current", CheckValueType.Dec, "B", ItemClass.Info, SubjectKind.Pagefile);
        Add(sys, "system.pagefile.peak", CheckValueType.Dec, "B", ItemClass.Judged, SubjectKind.Pagefile);
        Add(sys, "system.eventlog.whea7d", CheckValueType.Int, "count", ItemClass.Judged);
        Add(sys, "system.eventlog.whea_warning7d", CheckValueType.Int, "count", ItemClass.Judged);
        foreach (var log in new[] { "disk", "ntfs", "stornvme", "volmgr" })
        {
            Add(sys, $"system.eventlog.{log}7d", CheckValueType.Int, "count", ItemClass.Judged);
            Add(sys, $"system.eventlog.{log}_warning7d", CheckValueType.Int, "count", ItemClass.Info);
        }

        Add(sys, "system.eventlog.kp41_7d", CheckValueType.Int, "count", ItemClass.Judged);

        // CPU (DESIGN-A §3.2 / §14.2). No temperature item: it is a note (cpu.temperature_not_measured).
        const CheckKind cpu = CheckKind.Cpu;
        Add(cpu, "cpu.duration", CheckValueType.Number, "s", ItemClass.Info);
        Add(cpu, "cpu.chunks", CheckValueType.Int, "count", ItemClass.Info);
        Add(cpu, "cpu.compute_errors", CheckValueType.Int, "count", ItemClass.Judged);
        Add(cpu, "cpu.throughput", CheckValueType.Number, "chunks/s", ItemClass.Info);
        Add(cpu, "cpu.steadiness", CheckValueType.Number, "%", ItemClass.Judged);
        Add(cpu, "cpu.variation", CheckValueType.Number, "%", ItemClass.Info);
        Add(cpu, "cpu.logical_throughput", CheckValueType.Number, "chunks/s", ItemClass.Info, SubjectKind.Cpu);
        Add(cpu, "cpu.peer_ratio", CheckValueType.Number, "%", ItemClass.Judged, SubjectKind.Cpu);
        Add(cpu, "cpu.effective_clock", CheckValueType.Number, "MHz", ItemClass.Judged);
        Add(cpu, "cpu.utilization", CheckValueType.Number, "%", ItemClass.Info);
        Add(cpu, "cpu.package_power", CheckValueType.Number, "W", ItemClass.Info);

        // Memory (DESIGN-A §3.2 / §14.3)
        const CheckKind mem = CheckKind.Memory;
        Add(mem, "memory.duration", CheckValueType.Number, "s", ItemClass.Info);
        Add(mem, "memory.total", CheckValueType.Dec, "B", ItemClass.Info);
        Add(mem, "memory.available_at_start", CheckValueType.Dec, "B", ItemClass.Info);
        Add(mem, "memory.borrowed", CheckValueType.Dec, "B", ItemClass.Info);
        Add(mem, "memory.passes", CheckValueType.Int, "count", ItemClass.Info);
        Add(mem, "memory.verified", CheckValueType.Dec, "B", ItemClass.Info);
        Add(mem, "memory.mismatches", CheckValueType.Int, "count", ItemClass.Judged);
        Add(mem, "memory.write_rate", CheckValueType.Number, "MiB/s", ItemClass.Info);
        Add(mem, "memory.verify_rate", CheckValueType.Number, "MiB/s", ItemClass.Info);

        // Storage (DESIGN-A §3.3 / §14.4 / §14.5). Everything but the device count is per disk.
        const CheckKind sto = CheckKind.Storage;
        const SubjectKind disk = SubjectKind.Disk;
        Add(sto, "storage.devices", CheckValueType.Int, "count", ItemClass.Info);
        Add(sto, "storage.health", CheckValueType.Text, null, ItemClass.Judged, disk, allowedText: HealthValues);
        Add(sto, "storage.capacity", CheckValueType.Dec, "B", ItemClass.Info, disk);
        Add(sto, "storage.temperature", CheckValueType.Number, "°C", ItemClass.Judged, disk);
        Add(sto, "storage.nvme.critical_warning", CheckValueType.Int, "bits", ItemClass.Judged, disk);
        // The source rows of a .delta are all integers (DESIGN-A §3.5-5), so the spare and the wear are whole percents.
        Add(sto, "storage.nvme.available_spare", CheckValueType.Int, "%", ItemClass.Judged, disk);
        Add(sto, "storage.nvme.spare_threshold", CheckValueType.Number, "%", ItemClass.Info, disk);
        Add(sto, "storage.nvme.percentage_used", CheckValueType.Int, "%", ItemClass.Judged, disk);
        Add(sto, "storage.nvme.media_errors", CheckValueType.Dec, "count", ItemClass.Judged, disk);
        Add(sto, "storage.nvme.error_log_entries", CheckValueType.Dec, "count", ItemClass.Judged, disk);
        Add(sto, "storage.nvme.unsafe_shutdowns", CheckValueType.Dec, "count", ItemClass.Info, disk);
        Add(sto, "storage.nvme.power_cycles", CheckValueType.Dec, "count", ItemClass.Info, disk);
        Add(sto, "storage.nvme.power_on_hours", CheckValueType.Dec, "h", ItemClass.Info, disk);
        Add(sto, "storage.nvme.data_units_written", CheckValueType.Dec, "nvme_data_unit", ItemClass.Info, disk);
        Add(sto, "storage.ata.predict_failure", CheckValueType.Bool, null, ItemClass.Judged, disk);
        Add(sto, "storage.ata.threshold_status", CheckValueType.Text, null, ItemClass.Judged, disk, allowedText: ThresholdValues);
        foreach (var attribute in new[] { 5, 187, 196, 197, 198, 199 })
        {
            Add(sto, $"storage.ata.{attribute.ToString(CultureInfo.InvariantCulture)}", CheckValueType.Dec, "smart_raw", ItemClass.Judged, disk);
        }

        Add(sto, "storage.ata.9", CheckValueType.Dec, "smart_raw", ItemClass.Info, disk);

        // The 15 compared counters get a ".delta" ID: a signed decimal string, the unit of the row it belongs to.
        // Judged when the old GrowthVerdict table has the counter (DESIGN-A §14.5); the others are info.
        AddDelta("storage.nvme.critical_warning", judged: true);
        AddDelta("storage.nvme.available_spare", judged: false);
        AddDelta("storage.nvme.percentage_used", judged: false);
        AddDelta("storage.nvme.media_errors", judged: true);
        AddDelta("storage.nvme.error_log_entries", judged: true);
        AddDelta("storage.nvme.unsafe_shutdowns", judged: false);
        AddDelta("storage.nvme.power_on_hours", judged: false);
        AddDelta("storage.nvme.data_units_written", judged: false);
        foreach (var attribute in new[] { 5, 187, 196, 197, 198, 199 })
        {
            AddDelta($"storage.ata.{attribute.ToString(CultureInfo.InvariantCulture)}", judged: true);
        }

        AddDelta("storage.ata.9", judged: false);

        BuildEvidenceCodeRows();
    }

    /// <summary>The ID whose value (the device status code of one GPU) decides whether that GPU's driver rows are info (DESIGN-A §14.0).</summary>
    public const string GpuDeviceStatusId = "system.gpu.device_status";

    /// <summary>Every registered ID, in the order of the design tables.</summary>
    public static IReadOnlyList<CheckItemSpec> All => Ordered;

    /// <summary>
    /// Whether an evidence code may be carried by this item ID (DESIGN-A §3 "根拠のコードを置ける行", §3.5-8, §14). A code
    /// that is not placed anywhere never passes. The reader of an older version that meets an ID or a code it does not
    /// know skips the row (it never asks this about it).
    /// </summary>
    public static bool AllowsEvidenceCode(string? id, CheckEvidenceCode code) =>
        id is not null && CodeRows.TryGetValue(code, out var rows) && rows.Contains(id);

    /// <summary>The IDs that may carry an evidence code, sorted. Empty for a code that is not placed anywhere.</summary>
    public static IReadOnlyList<string> RowsFor(CheckEvidenceCode code) =>
        CodeRows.TryGetValue(code, out var rows) ? rows.Order(StringComparer.Ordinal).ToArray() : [];

    public static bool TryGet(string? id, out CheckItemSpec spec)
    {
        if (id is not null && ById.TryGetValue(id, out var found))
        {
            spec = found;
            return true;
        }

        spec = null!;
        return false;
    }

    /// <summary>The spec of an ID; throws for an ID that is not registered.</summary>
    public static CheckItemSpec Get(string id) =>
        TryGet(id, out var spec) ? spec : throw new ArgumentException($"'{id}' is not a registered check item ID.", nameof(id));

    public static bool IsKnown(string? id) => id is not null && ById.ContainsKey(id);

    public static IEnumerable<CheckItemSpec> ForKind(CheckKind kind) => Ordered.Where(s => s.Kind == kind);

    /// <summary>The ".delta" ID of a counter ID.</summary>
    public static string DeltaId(string counterId) => counterId + ".delta";

    private static void Add(
        CheckKind kind,
        string id,
        CheckValueType type,
        string? unit,
        ItemClass cls,
        SubjectKind subject = SubjectKind.None,
        string[]? allowedText = null,
        bool gpuStatus = false)
    {
        var spec = new CheckItemSpec(id, kind, type, unit, cls, subject, DeltaOf: null, allowedText, gpuStatus);
        ById.Add(id, spec);
        Ordered.Add(spec);
    }

    // The row of each evidence code, from DESIGN-A §3 ("根拠のコードを置ける行"), §3.3, §14.1-§14.5. A code is placed only
    // on the rows the design uses it for; a new row or a new use is a change to this table (and to the design).
    private static void BuildEvidenceCodeRows()
    {
        foreach (var spec in Ordered.Where(static s => s.IsDelta).ToArray())
        {
            // baseline_*: the .delta rows only (why this counter was not compared).
            foreach (var code in Enum.GetValues<CheckEvidenceCode>().Where(CheckEvidenceCodes.IsBaseline))
            {
                Place(code, spec.Id);
            }

            // counter_increased / counter_decreased: the .delta row and the absolute row it belongs to.
            Place(CheckEvidenceCode.CounterIncreased, spec.Id, spec.DeltaOf!);
            Place(CheckEvidenceCode.CounterDecreased, spec.Id, spec.DeltaOf!);
        }

        // new_bits: the warning bits that newly came up (DESIGN-A §14.5), on the delta and on the row it belongs to.
        Place(CheckEvidenceCode.NewBits, "storage.nvme.critical_warning.delta", "storage.nvme.critical_warning");

        // "likely cable" is the ATA 199 finding, on the absolute row (§14.4) and on its delta (§3.3).
        Place(CheckEvidenceCode.LikelyCable, "storage.ata.199", "storage.ata.199.delta");

        Place(CheckEvidenceCode.MixedCapacity, "system.memory.modules");
        Place(CheckEvidenceCode.BelowRated, "system.memory.configured_speed");
        Place(CheckEvidenceCode.AboveRated, "system.memory.configured_speed");
        Place(CheckEvidenceCode.DisabledCode22, GpuDeviceStatusId);
        Place(CheckEvidenceCode.DeviceError, GpuDeviceStatusId);
        Place(CheckEvidenceCode.TooShort, "cpu.steadiness");
        Place(CheckEvidenceCode.PinUnknown, "cpu.peer_ratio");
        Place(CheckEvidenceCode.PatternMismatch, "memory.mismatches");
        Place(CheckEvidenceCode.ComputeMismatch, "cpu.compute_errors");
        Place(CheckEvidenceCode.PastThresholdBreach, "storage.ata.threshold_status");
        Place(CheckEvidenceCode.CurrentThresholdBreach, "storage.ata.threshold_status");
        Place(CheckEvidenceCode.TempOverCriticalBefore, "storage.temperature");
        Place(CheckEvidenceCode.MediaErrorsInLog, "storage.nvme.error_log_entries");
    }

    private static void Place(CheckEvidenceCode code, params string[] ids)
    {
        if (!CodeRows.TryGetValue(code, out var rows))
        {
            CodeRows.Add(code, rows = new HashSet<string>(StringComparer.Ordinal));
        }

        foreach (var id in ids)
        {
            if (!ById.ContainsKey(id))
            {
                throw new InvalidOperationException($"Evidence code {code} is placed on '{id}', which is not a registered item ID.");
            }

            rows.Add(id);
        }
    }

    private static void AddDelta(string counterId, bool judged)
    {
        var counter = ById[counterId];
        var id = DeltaId(counterId);
        var spec = new CheckItemSpec(
            id,
            counter.Kind,
            CheckValueType.SignedDec,
            counter.Unit,
            judged ? ItemClass.Judged : ItemClass.Info,
            counter.Subject,
            DeltaOf: counterId);
        ById.Add(id, spec);
        Ordered.Add(spec);
    }
}
