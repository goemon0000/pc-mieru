using System.Text.RegularExpressions;

namespace PcMieru.Checks.Tests;

/// <summary>The item-ID registry against the DESIGN-A tables (§3.0 info set, §3.1-§3.3 types and units, §14.0 vocabularies).</summary>
public partial class RegistryTests
{
    // DESIGN-A §3.0, copied as it is written there. The ".delta" members of the list are checked separately.
    private static readonly string[] InfoIds =
    [
        "system.eventlog.disk_warning7d", "system.eventlog.ntfs_warning7d", "system.eventlog.stornvme_warning7d", "system.eventlog.volmgr_warning7d",
        "system.os.version", "system.cpu.model", "system.cpu.cores", "system.cpu.logical", "system.cpu.rated_clock",
        "system.memory.capacity", "system.memory.rated_speed", "system.gpu.adapters", "system.gpu.model", "system.gpu.driver_version",
        "system.storage.devices", "system.pagefile.automatic", "system.pagefile.allocated", "system.pagefile.current",
        "cpu.duration", "cpu.chunks", "cpu.throughput", "cpu.variation", "cpu.logical_throughput", "cpu.utilization", "cpu.package_power",
        "memory.duration", "memory.total", "memory.available_at_start", "memory.borrowed", "memory.passes", "memory.verified", "memory.write_rate", "memory.verify_rate",
        "storage.devices", "storage.capacity", "storage.nvme.spare_threshold", "storage.nvme.unsafe_shutdowns", "storage.nvme.power_cycles",
        "storage.nvme.power_on_hours", "storage.nvme.data_units_written", "storage.ata.9",
    ];

    // DESIGN-A §3.3 / §14.5: the counters with a ".delta", and which of those deltas are judged (the old GrowthVerdict table).
    private static readonly string[] JudgedDeltaBases =
    [
        "storage.nvme.critical_warning", "storage.nvme.media_errors", "storage.nvme.error_log_entries",
        "storage.ata.5", "storage.ata.187", "storage.ata.196", "storage.ata.197", "storage.ata.198", "storage.ata.199",
    ];

    private static readonly string[] InfoDeltaBases =
    [
        "storage.nvme.available_spare", "storage.nvme.percentage_used", "storage.nvme.unsafe_shutdowns",
        "storage.nvme.power_on_hours", "storage.nvme.data_units_written", "storage.ata.9",
    ];

    private static void Same(string[] expected, IEnumerable<string> actual) => Assert.Equal(expected, actual.ToArray());

    [Fact]
    public void Observation_only_ids_are_exactly_the_section_3_0_list_plus_the_info_deltas()
    {
        var infoNonDelta = CheckItemRegistry.All.Where(s => !s.IsDelta && s.Class == ItemClass.Info).Select(s => s.Id).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(InfoIds.Order(StringComparer.Ordinal).ToList(), infoNonDelta);

        var infoDeltas = CheckItemRegistry.All.Where(s => s.IsDelta && s.Class == ItemClass.Info).Select(s => s.DeltaOf!).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(InfoDeltaBases.Order(StringComparer.Ordinal).ToList(), infoDeltas);
    }

    [Fact]
    public void Judged_ids_are_every_other_id()
    {
        var judgedNonDelta = CheckItemRegistry.All.Where(s => !s.IsDelta && s.Class == ItemClass.Judged).Select(s => s.Id).ToHashSet();
        string[] expected =
        [
            "system.os.uptime", "system.memory.modules", "system.memory.configured_speed",
            "system.gpu.driver_age", "system.gpu.device_status", "system.gpu.generic_driver",
            "system.storage.health", "system.storage.free", "system.pagefile.count", "system.pagefile.peak",
            "system.eventlog.whea7d", "system.eventlog.whea_warning7d", "system.eventlog.disk7d", "system.eventlog.ntfs7d",
            "system.eventlog.stornvme7d", "system.eventlog.volmgr7d", "system.eventlog.kp41_7d",
            "cpu.compute_errors", "cpu.steadiness", "cpu.peer_ratio", "cpu.effective_clock",
            "memory.mismatches",
            "storage.health", "storage.temperature", "storage.nvme.critical_warning", "storage.nvme.available_spare",
            "storage.nvme.percentage_used", "storage.nvme.media_errors", "storage.nvme.error_log_entries",
            "storage.ata.predict_failure", "storage.ata.threshold_status",
            "storage.ata.5", "storage.ata.187", "storage.ata.196", "storage.ata.197", "storage.ata.198", "storage.ata.199",
        ];
        Assert.True(judgedNonDelta.SetEquals(expected), "judged: " + string.Join(", ", judgedNonDelta.Except(expected).Concat(expected.Except(judgedNonDelta))));
    }

    [Fact]
    public void There_are_fifteen_deltas_one_per_compared_counter()
    {
        var deltas = CheckItemRegistry.All.Where(s => s.IsDelta).ToList();
        Assert.Equal(15, deltas.Count);
        Assert.Equal(15, JudgedDeltaBases.Length + InfoDeltaBases.Length);

        foreach (var baseId in JudgedDeltaBases.Concat(InfoDeltaBases))
        {
            var deltaId = baseId + ".delta";
            Assert.Equal(deltaId, CheckItemRegistry.DeltaId(baseId));
            var delta = CheckItemRegistry.Get(deltaId);
            var counter = CheckItemRegistry.Get(baseId);

            Assert.Equal(baseId, delta.DeltaOf);
            Assert.Equal(CheckValueType.SignedDec, delta.ValueType);
            Assert.Equal(counter.Unit, delta.Unit); // "the unit of the row it belongs to"
            Assert.Equal(CheckKind.Storage, delta.Kind);
            Assert.Equal(SubjectKind.Disk, delta.Subject);
            Assert.Equal(JudgedDeltaBases.Contains(baseId) ? ItemClass.Judged : ItemClass.Info, delta.Class);
            Assert.True(delta.MayBeInfo, "a delta without a value is info (DESIGN-A §14.0, exception 1)");
        }

        // The ATA temperature rows (190/194) and the temperature are not compared: no delta.
        Assert.False(CheckItemRegistry.IsKnown("storage.temperature.delta"));
        Assert.False(CheckItemRegistry.IsKnown("storage.nvme.spare_threshold.delta"));
        Assert.False(CheckItemRegistry.IsKnown("storage.nvme.power_cycles.delta"));
    }

    [Fact]
    public void Only_the_gpu_driver_rows_and_the_deltas_may_turn_info_while_judged()
    {
        var mayBeInfoJudged = CheckItemRegistry.All
            .Where(s => s.Class == ItemClass.Judged && s.MayBeInfo && !s.IsDelta)
            .Select(s => s.Id)
            .Order(StringComparer.Ordinal)
            .ToList();
        Same(["system.gpu.driver_age", "system.gpu.generic_driver"], mayBeInfoJudged);

        // device_status itself is judged whatever the status is.
        Assert.False(CheckItemRegistry.Get("system.gpu.device_status").MayBeInfo);

        // Every judged .delta may be info; a judged non-delta may not (apart from the two above).
        Assert.All(CheckItemRegistry.All.Where(s => s.IsDelta), s => Assert.True(s.MayBeInfo));
    }

    [Fact]
    public void Ids_are_unique_well_formed_and_belong_to_the_kind_their_prefix_says()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in CheckItemRegistry.All)
        {
            Assert.True(seen.Add(spec.Id), $"duplicate {spec.Id}");
            Assert.Matches(IdPattern(), spec.Id);

            var prefix = spec.Id[..spec.Id.IndexOf('.', StringComparison.Ordinal)];
            Assert.Equal(prefix, spec.Kind.ToString().ToLowerInvariant());
        }

        Assert.Equal(seen.Count, CheckItemRegistry.All.Count);
        Assert.All(Enum.GetValues<CheckKind>(), kind => Assert.NotEmpty(CheckItemRegistry.ForKind(kind)));
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$")]
    private static partial Regex IdPattern();

    [Fact]
    public void Units_follow_the_design_tables()
    {
        (string Id, CheckValueType Type, string? Unit)[] expected =
        [
            ("system.os.version", CheckValueType.OsVersion, null),
            ("system.os.uptime", CheckValueType.Number, "s"),
            ("system.cpu.model", CheckValueType.Text, null),
            ("system.cpu.cores", CheckValueType.Int, "count"),
            ("system.cpu.rated_clock", CheckValueType.Number, "MHz"),
            ("system.memory.capacity", CheckValueType.Dec, "B"),
            ("system.memory.rated_speed", CheckValueType.Int, "MT/s"),
            ("system.memory.configured_speed", CheckValueType.Int, "MT/s"),
            ("system.gpu.driver_age", CheckValueType.Int, "d"),
            ("system.gpu.device_status", CheckValueType.Int, "code"),
            ("system.gpu.generic_driver", CheckValueType.Bool, null),
            ("system.storage.free", CheckValueType.Dec, "B"),
            ("system.pagefile.automatic", CheckValueType.Bool, null),
            ("cpu.duration", CheckValueType.Number, "s"),
            ("cpu.chunks", CheckValueType.Int, "count"),
            ("cpu.throughput", CheckValueType.Number, "chunks/s"),
            ("cpu.steadiness", CheckValueType.Number, "%"),
            ("cpu.effective_clock", CheckValueType.Number, "MHz"),
            ("cpu.package_power", CheckValueType.Number, "W"),
            ("memory.total", CheckValueType.Dec, "B"),
            ("memory.passes", CheckValueType.Int, "count"),
            ("memory.write_rate", CheckValueType.Number, "MiB/s"),
            ("storage.temperature", CheckValueType.Number, "°C"),
            ("storage.nvme.critical_warning", CheckValueType.Int, "bits"),
            ("storage.nvme.available_spare", CheckValueType.Int, "%"),
            ("storage.nvme.percentage_used", CheckValueType.Int, "%"),
            ("storage.nvme.media_errors", CheckValueType.Dec, "count"),
            ("storage.nvme.power_on_hours", CheckValueType.Dec, "h"),
            ("storage.nvme.data_units_written", CheckValueType.Dec, "nvme_data_unit"),
            ("storage.ata.predict_failure", CheckValueType.Bool, null),
            ("storage.ata.threshold_status", CheckValueType.Text, null),
            ("storage.ata.5", CheckValueType.Dec, "smart_raw"),
            ("storage.ata.199", CheckValueType.Dec, "smart_raw"),
        ];

        foreach (var (id, type, unit) in expected)
        {
            var spec = CheckItemRegistry.Get(id);
            Assert.Equal(type, spec.ValueType);
            Assert.Equal(unit, spec.Unit);
        }
    }

    [Fact]
    public void Per_instance_ids_carry_the_subject_kind_of_the_design()
    {
        (string Id, SubjectKind Subject)[] repeated =
        [
            ("system.memory.capacity", SubjectKind.Ram), ("system.memory.rated_speed", SubjectKind.Ram), ("system.memory.configured_speed", SubjectKind.Ram),
            ("system.gpu.model", SubjectKind.Gpu), ("system.gpu.driver_version", SubjectKind.Gpu), ("system.gpu.driver_age", SubjectKind.Gpu),
            ("system.gpu.device_status", SubjectKind.Gpu), ("system.gpu.generic_driver", SubjectKind.Gpu),
            ("system.storage.health", SubjectKind.Disk), ("system.storage.free", SubjectKind.Volume),
            ("system.pagefile.automatic", SubjectKind.Pagefile), ("system.pagefile.allocated", SubjectKind.Pagefile),
            ("system.pagefile.current", SubjectKind.Pagefile), ("system.pagefile.peak", SubjectKind.Pagefile),
            ("cpu.logical_throughput", SubjectKind.Cpu), ("cpu.peer_ratio", SubjectKind.Cpu),
            ("storage.health", SubjectKind.Disk), ("storage.capacity", SubjectKind.Disk), ("storage.temperature", SubjectKind.Disk),
            ("storage.nvme.media_errors", SubjectKind.Disk), ("storage.ata.199", SubjectKind.Disk),
        ];

        foreach (var (id, subject) in repeated)
        {
            Assert.Equal(subject, CheckItemRegistry.Get(id).Subject);
        }

        // The totals (a count) are one row, not repeated.
        foreach (var id in new[] { "system.memory.modules", "system.gpu.adapters", "system.storage.devices", "system.pagefile.count", "storage.devices", "memory.total", "cpu.duration" })
        {
            Assert.Equal(SubjectKind.None, CheckItemRegistry.Get(id).Subject);
        }

        Assert.Equal("ram", SubjectKinds.Prefix(SubjectKind.Ram));
        Assert.Equal("volume", SubjectKinds.Prefix(SubjectKind.Volume));
    }

    [Theory]
    [InlineData(SubjectKind.Ram, "ram-1", true)]
    [InlineData(SubjectKind.Ram, "ram-12", true)]
    [InlineData(SubjectKind.Disk, "disk-1", true)]
    [InlineData(SubjectKind.Disk, "disk-0", false)]
    [InlineData(SubjectKind.Ram, "ram-0", false)]
    [InlineData(SubjectKind.Cpu, "cpu-0", false)]
    [InlineData(SubjectKind.Cpu, "cpu-1", true)]
    [InlineData(SubjectKind.Cpu, "cpu-2", true)]
    [InlineData(SubjectKind.Gpu, "gpu-0", false)]
    [InlineData(SubjectKind.Volume, "volume-0", false)]
    [InlineData(SubjectKind.Pagefile, "pagefile-0", false)]
    [InlineData(SubjectKind.Disk, "disk-10", true)]
    [InlineData(SubjectKind.Disk, "disk-00", false)]
    [InlineData(SubjectKind.Ram, "gpu-1", false)]
    [InlineData(SubjectKind.Ram, "ram-", false)]
    [InlineData(SubjectKind.Ram, "ram-01", false)]
    [InlineData(SubjectKind.Ram, "ram--1", false)]
    [InlineData(SubjectKind.Ram, "ram-1a", false)]
    [InlineData(SubjectKind.Ram, "ram1", false)]
    [InlineData(SubjectKind.Ram, "RAM-1", false)]
    [InlineData(SubjectKind.Ram, "ram-1234567890", false)]
    [InlineData(SubjectKind.Disk, "disk-\\\\.\\PHYSICALDRIVE0", false)]
    [InlineData(SubjectKind.None, "none-1", false)]
    public void Subject_ids_are_prefix_dash_number(SubjectKind kind, string id, bool valid) =>
        Assert.Equal(valid, SubjectKinds.IsValidId(kind, id));

    [Fact]
    public void Sources_are_exactly_the_fixed_list_of_section_14_0()
    {
        Same(
            ["os", "win32", "smbios", "setupapi", "dxgi", "wmi", "eventlog", "pdh", "energy_meter", "load", "nvme", "ata", "storage_query", "baseline"],
            SnakeCaseEnum<CheckSource>.Names);
    }

    [Fact]
    public void Evidence_codes_are_exactly_the_fixed_list_of_section_14_0()
    {
        Same(
            [
                "baseline_inventory_incomplete", "baseline_first", "baseline_no_identity", "baseline_duplicate", "baseline_device_changed", "baseline_counter_reset",
                "baseline_clock", "baseline_concurrent", "baseline_missing_counter", "baseline_read_failed", "baseline_legacy_refused",
                "counter_increased", "counter_decreased", "new_bits", "likely_cable", "mixed_capacity", "below_rated", "above_rated",
                "disabled_code22", "device_error", "too_short", "pin_unknown", "pattern_mismatch", "compute_mismatch",
                "past_threshold_breach", "current_threshold_breach", "temp_over_critical_before", "media_errors_in_log",
            ],
            SnakeCaseEnum<CheckEvidenceCode>.Names);

        // Exactly the eleven baseline_* codes say "not comparable".
        var baseline = Enum.GetValues<CheckEvidenceCode>().Where(CheckEvidenceCodes.IsBaseline).Select(SnakeCaseEnum<CheckEvidenceCode>.Name).ToList();
        Assert.Equal(11, baseline.Count);
        Assert.All(baseline, name => Assert.StartsWith("baseline_", name, StringComparison.Ordinal));
        Assert.Equal(11, SnakeCaseEnum<CheckEvidenceCode>.Names.Count(n => n.StartsWith("baseline_", StringComparison.Ordinal)));
    }

    [Fact]
    public void Other_vocabularies_are_lowercase_strings()
    {
        Same(["system", "cpu", "memory", "storage"], SnakeCaseEnum<CheckKind>.Names);
        Same(["completed", "stopped", "failed"], SnakeCaseEnum<CheckOutcome>.Names);
        Same(["ok", "caution", "check", "unavailable"], SnakeCaseEnum<CheckVerdict>.Names);
        Same(["ok", "caution", "check", "unavailable", "info"], SnakeCaseEnum<ItemVerdict>.Names);
        Same(["unsupported", "denied", "timeout", "error", "partial"], SnakeCaseEnum<CheckReason>.Names);
    }

    [Fact]
    public void An_unknown_id_is_not_known()
    {
        Assert.False(CheckItemRegistry.IsKnown("system.cpu.temperature"));
        Assert.False(CheckItemRegistry.IsKnown("cpu.temperature"));
        Assert.False(CheckItemRegistry.IsKnown(null));
        Assert.False(CheckItemRegistry.TryGet("", out _));
        Assert.Throws<ArgumentException>(() => CheckItemRegistry.Get("nope"));
    }
}
