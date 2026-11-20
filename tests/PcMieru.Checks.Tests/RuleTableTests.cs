namespace PcMieru.Checks.Tests;

/// <summary>
/// The rules DESIGN-A §3.5 fixes as tables, tested as tables: the (verdict, value, reason) combinations of §3.5-2, the row
/// each evidence code may sit on (§3 "根拠のコードを置ける行"), the per-disk baseline of §3.5-7, the shape of a .delta that
/// could not be compared (§3.5-6), non-negative units (§3.5-8), the numbering of subjects (§3.5-3) and the equality of
/// evidence (§3.5-4).
/// </summary>
public class RuleTableTests
{
    private static readonly CheckSubject Disk1 = new("disk-1", "Samsung SSD 980 PRO 2TB");
    private static readonly CheckSubject Disk2 = new("disk-2", "CT500MX500SSD1");
    private static readonly DateTimeOffset Since = Samples.Start.AddDays(-1);

    private static CheckResult Result(CheckKind kind, params CheckItem[] items) =>
        CheckResult.Create(Samples.App, Samples.StorageRun, kind, Samples.Start, Samples.Start.AddSeconds(1), CheckOutcome.Completed, items);

    private static IReadOnlyList<string> Problems(CheckKind kind, params CheckItem[] items) =>
        CheckResultValidator.Validate(Result(kind, items));

    private static void IsValid(CheckKind kind, params CheckItem[] items)
    {
        var problems = Problems(kind, items);
        Assert.True(problems.Count == 0, "expected no problem, got: " + string.Join(" | ", problems));
    }

    private static void Fails(string fragment, CheckKind kind, params CheckItem[] items)
    {
        var problems = Problems(kind, items);
        Assert.True(
            problems.Any(p => p.Contains(fragment, StringComparison.Ordinal)),
            $"expected a problem containing '{fragment}', got: {(problems.Count == 0 ? "(none)" : string.Join(" | ", problems))}");
    }

    private static CheckItem Delta(string counter, CheckValue? value, ItemVerdict verdict, CheckEvidence? evidence, CheckReason? reason = null, CheckSubject? disk = null) =>
        CheckItem.Create(counter + ".delta", value, verdict, CheckSource.Baseline, reason, disk ?? Disk1, evidence);

    private static CheckItem NotComparable(string counter, CheckEvidenceCode code, CheckSubject? disk = null) =>
        Delta(counter, null, ItemVerdict.Info, CheckEvidence.OfCode(code), CheckReason.Partial, disk);

    private static CheckItem Compared(string counter, CheckSubject? disk = null) =>
        Delta(
            counter,
            CheckValue.SignedDec("1"),
            CheckItemRegistry.Get(counter + ".delta").Class == ItemClass.Judged ? ItemVerdict.Caution : ItemVerdict.Info,
            new CheckEvidence { Before = "1", After = "2", SinceUtc = Since },
            disk: disk);

    // ---- §3.5-2: the whole (verdict x value x reason) table, for a judged item and for an observation-only item ----

    private static readonly ItemVerdict[] AllVerdicts = Enum.GetValues<ItemVerdict>();
    private static readonly CheckReason?[] AllReasons = [null, .. Enum.GetValues<CheckReason>().Select(static r => (CheckReason?)r)];

    private static bool JudgedCombinationIsValid(bool hasValue, CheckReason? reason, ItemVerdict verdict) => (hasValue, reason) switch
    {
        (true, null) => verdict is ItemVerdict.Ok or ItemVerdict.Caution or ItemVerdict.Check,
        (true, CheckReason.Partial) => verdict is ItemVerdict.Caution or ItemVerdict.Check or ItemVerdict.Unavailable,
        (false, not null) => verdict == ItemVerdict.Unavailable,
        _ => false,
    };

    private static bool InfoCombinationIsValid(bool hasValue, CheckReason? reason, ItemVerdict verdict) => (hasValue, reason) switch
    {
        (true, null) or (true, CheckReason.Partial) => verdict == ItemVerdict.Info,
        (false, not null) => verdict == ItemVerdict.Info,
        _ => false,
    };

    [Fact]
    public void Every_verdict_value_reason_combination_of_a_judged_item_is_accepted_or_refused_as_the_table_says()
    {
        var wrong = new List<string>();
        foreach (var hasValue in new[] { true, false })
        {
            foreach (var reason in AllReasons)
            {
                foreach (var verdict in AllVerdicts)
                {
                    var item = CheckItem.Create("memory.mismatches", hasValue ? CheckValue.Int(1) : null, verdict, CheckSource.Load, reason);
                    var valid = Problems(CheckKind.Memory, item).Count == 0;
                    if (valid != JudgedCombinationIsValid(hasValue, reason, verdict))
                    {
                        wrong.Add($"value={(hasValue ? "1" : "null")} reason={reason?.ToString() ?? "null"} verdict={verdict}: accepted={valid}");
                    }
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void Every_verdict_value_reason_combination_of_an_observation_only_item_is_accepted_or_refused_as_the_table_says()
    {
        var wrong = new List<string>();
        foreach (var hasValue in new[] { true, false })
        {
            foreach (var reason in AllReasons)
            {
                foreach (var verdict in AllVerdicts)
                {
                    var item = CheckItem.Create("memory.duration", hasValue ? CheckValue.Number(1.5) : null, verdict, CheckSource.Load, reason);
                    var valid = Problems(CheckKind.Memory, item).Count == 0;
                    if (valid != InfoCombinationIsValid(hasValue, reason, verdict))
                    {
                        wrong.Add($"value={(hasValue ? "1.5" : "null")} reason={reason?.ToString() ?? "null"} verdict={verdict}: accepted={valid}");
                    }
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void The_table_itself_has_the_shape_the_design_says()
    {
        // A guard on the test's own oracle: 2 x 6 x 5 = 60 rows, and exactly these are valid.
        var judged = (from hasValue in new[] { true, false } from reason in AllReasons from verdict in AllVerdicts where JudgedCombinationIsValid(hasValue, reason, verdict) select 1).Count();
        var info = (from hasValue in new[] { true, false } from reason in AllReasons from verdict in AllVerdicts where InfoCombinationIsValid(hasValue, reason, verdict) select 1).Count();
        Assert.Equal(3 + 3 + 5, judged); // value+null: 3; value+partial: 3; no value and one of the 5 reasons: 5
        Assert.Equal(1 + 1 + 5, info);
    }

    [Fact]
    public void Ok_with_a_reason_is_a_validation_error_in_every_form()
    {
        foreach (var reason in Enum.GetValues<CheckReason>())
        {
            Assert.NotEmpty(Problems(CheckKind.Memory, CheckItem.Create("memory.mismatches", CheckValue.Int(0), ItemVerdict.Ok, CheckSource.Load, reason)));
            Assert.NotEmpty(Problems(CheckKind.Memory, CheckItem.Create("memory.mismatches", null, ItemVerdict.Ok, CheckSource.Load, reason)));
        }

        Fails("ok cannot carry a reason", CheckKind.Memory, CheckItem.Create("memory.mismatches", CheckValue.Int(0), ItemVerdict.Ok, CheckSource.Load, CheckReason.Partial));
    }

    [Fact]
    public void Unavailable_with_a_value_needs_partial_and_unavailable_with_no_reason_is_refused()
    {
        IsValid(CheckKind.Memory, CheckItem.Create("memory.mismatches", CheckValue.Int(3), ItemVerdict.Unavailable, CheckSource.Load, CheckReason.Partial));
        Fails("must be ok, caution or check", CheckKind.Memory, CheckItem.Create("memory.mismatches", CheckValue.Int(3), ItemVerdict.Unavailable, CheckSource.Load));
        Fails("no value, so a reason is required", CheckKind.Memory, CheckItem.Create("memory.mismatches", null, ItemVerdict.Unavailable, CheckSource.Load));
        Fails("the reason must be null or partial", CheckKind.Memory, CheckItem.Create("memory.mismatches", CheckValue.Int(3), ItemVerdict.Unavailable, CheckSource.Load, CheckReason.Denied));
    }

    // ---- §3 "根拠のコードを置ける行": which row may carry which evidence.code ----

    private static readonly string[] Counters =
    [
        "storage.nvme.critical_warning", "storage.nvme.available_spare", "storage.nvme.percentage_used", "storage.nvme.media_errors",
        "storage.nvme.error_log_entries", "storage.nvme.unsafe_shutdowns", "storage.nvme.power_on_hours", "storage.nvme.data_units_written",
        "storage.ata.5", "storage.ata.187", "storage.ata.196", "storage.ata.197", "storage.ata.198", "storage.ata.199", "storage.ata.9",
    ];

    private static readonly string[] Deltas = [.. Counters.Select(static c => c + ".delta")];

    // Written out here, not taken from the registry: this is what the design says, and the registry is compared with it.
    private static IReadOnlyList<string> ExpectedRows(CheckEvidenceCode code)
    {
        IEnumerable<string> rows = code switch
        {
            CheckEvidenceCode.BaselineInventoryIncomplete or CheckEvidenceCode.BaselineFirst or CheckEvidenceCode.BaselineNoIdentity
                or CheckEvidenceCode.BaselineDuplicate
                or CheckEvidenceCode.BaselineDeviceChanged or CheckEvidenceCode.BaselineCounterReset or CheckEvidenceCode.BaselineClock
                or CheckEvidenceCode.BaselineConcurrent or CheckEvidenceCode.BaselineMissingCounter or CheckEvidenceCode.BaselineReadFailed
                or CheckEvidenceCode.BaselineLegacyRefused => Deltas,
            CheckEvidenceCode.CounterIncreased or CheckEvidenceCode.CounterDecreased => [.. Deltas, .. Counters],
            CheckEvidenceCode.NewBits => ["storage.nvme.critical_warning", "storage.nvme.critical_warning.delta"],
            CheckEvidenceCode.LikelyCable => ["storage.ata.199", "storage.ata.199.delta"],
            CheckEvidenceCode.MixedCapacity => ["system.memory.modules"],
            CheckEvidenceCode.BelowRated or CheckEvidenceCode.AboveRated => ["system.memory.configured_speed"],
            CheckEvidenceCode.DisabledCode22 or CheckEvidenceCode.DeviceError => ["system.gpu.device_status"],
            CheckEvidenceCode.TooShort => ["cpu.steadiness"],
            CheckEvidenceCode.PinUnknown => ["cpu.peer_ratio"],
            CheckEvidenceCode.PatternMismatch => ["memory.mismatches"],
            CheckEvidenceCode.ComputeMismatch => ["cpu.compute_errors"],
            CheckEvidenceCode.PastThresholdBreach or CheckEvidenceCode.CurrentThresholdBreach => ["storage.ata.threshold_status"],
            CheckEvidenceCode.TempOverCriticalBefore => ["storage.temperature"],
            CheckEvidenceCode.MediaErrorsInLog => ["storage.nvme.error_log_entries"],
            _ => throw new InvalidOperationException($"{code} has no placement written in this test."),
        };
        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    public static TheoryData<CheckEvidenceCode> AllCodes()
    {
        var data = new TheoryData<CheckEvidenceCode>();
        foreach (var code in Enum.GetValues<CheckEvidenceCode>())
        {
            data.Add(code);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void The_registry_places_every_code_on_the_rows_the_design_names(CheckEvidenceCode code) =>
        Assert.Equal(ExpectedRows(code), CheckItemRegistry.RowsFor(code));

    [Fact]
    public void There_are_fifteen_counters_and_so_fifteen_deltas()
    {
        Assert.Equal(15, Counters.Length);
        Assert.Equal(Deltas.Order(StringComparer.Ordinal), CheckItemRegistry.All.Where(static s => s.IsDelta).Select(static s => s.Id).Order(StringComparer.Ordinal));
    }

    private static CheckItem ItemOnRow(CheckItemSpec spec, CheckEvidence evidence)
    {
        var subject = spec.IsRepeated ? new CheckSubject(SubjectKinds.Prefix(spec.Subject) + "-1") : null;
        CheckValue value = spec.ValueType switch
        {
            CheckValueType.Int => CheckValue.Int(1),
            CheckValueType.Number => CheckValue.Number(1),
            CheckValueType.Dec => CheckValue.Dec("1"),
            CheckValueType.SignedDec => CheckValue.SignedDec("1"),
            CheckValueType.Bool => CheckValue.Bool(true),
            CheckValueType.Text => CheckValue.Text(spec.AllowedText?[0] ?? "x"),
            CheckValueType.OsVersion => CheckValue.OsVersion(new OsVersionValue("Windows 11 Pro", null, 26300, null)),
            _ => throw new InvalidOperationException(),
        };
        var verdict = spec.Class == ItemClass.Judged ? ItemVerdict.Caution : ItemVerdict.Info;
        if (spec.IsDelta)
        {
            evidence = evidence with { Before = "1", After = "2", SinceUtc = Since };
        }

        return CheckItem.Create(spec.Id, value, verdict, CheckSource.Load, null, subject, evidence);
    }

    [Fact]
    public void A_code_is_refused_on_every_row_that_is_not_one_of_its_rows_and_never_complained_about_on_its_own()
    {
        var wrong = new List<string>();
        foreach (var code in Enum.GetValues<CheckEvidenceCode>())
        {
            var allowed = ExpectedRows(code).ToHashSet(StringComparer.Ordinal);
            foreach (var spec in CheckItemRegistry.All)
            {
                var problems = Problems(spec.Kind, ItemOnRow(spec, CheckEvidence.OfCode(code)));
                var refused = problems.Any(static p => p.Contains("cannot be carried by this item", StringComparison.Ordinal));
                if (refused == allowed.Contains(spec.Id))
                {
                    wrong.Add($"{code} on {spec.Id}: refused={refused} (should be {!allowed.Contains(spec.Id)})");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    // One rejection per code family, written out (the loop above is the exhaustive form of the same rule).
    [Theory]
    [InlineData(CheckEvidenceCode.BaselineInventoryIncomplete, "storage.health")]
    [InlineData(CheckEvidenceCode.BaselineFirst, "storage.temperature")]
    [InlineData(CheckEvidenceCode.BaselineReadFailed, "storage.ata.5")]
    [InlineData(CheckEvidenceCode.CounterIncreased, "storage.temperature")]
    [InlineData(CheckEvidenceCode.CounterDecreased, "storage.health")]
    [InlineData(CheckEvidenceCode.NewBits, "storage.nvme.media_errors")]
    [InlineData(CheckEvidenceCode.LikelyCable, "storage.ata.197")]
    [InlineData(CheckEvidenceCode.MixedCapacity, "system.pagefile.count")]
    [InlineData(CheckEvidenceCode.BelowRated, "system.memory.modules")]
    [InlineData(CheckEvidenceCode.AboveRated, "system.os.uptime")]
    [InlineData(CheckEvidenceCode.DisabledCode22, "system.gpu.driver_age")]
    [InlineData(CheckEvidenceCode.DeviceError, "system.gpu.generic_driver")]
    [InlineData(CheckEvidenceCode.TooShort, "cpu.effective_clock")]
    [InlineData(CheckEvidenceCode.PinUnknown, "cpu.steadiness")]
    [InlineData(CheckEvidenceCode.PatternMismatch, "cpu.compute_errors")]
    [InlineData(CheckEvidenceCode.ComputeMismatch, "memory.mismatches")]
    [InlineData(CheckEvidenceCode.PastThresholdBreach, "storage.health")]
    [InlineData(CheckEvidenceCode.CurrentThresholdBreach, "storage.ata.predict_failure")]
    [InlineData(CheckEvidenceCode.TempOverCriticalBefore, "storage.nvme.percentage_used")]
    [InlineData(CheckEvidenceCode.MediaErrorsInLog, "storage.nvme.media_errors")]
    public void A_code_on_a_row_that_does_not_carry_it_is_refused(CheckEvidenceCode code, string rowId)
    {
        var spec = CheckItemRegistry.Get(rowId);

        Fails("cannot be carried by this item", spec.Kind, ItemOnRow(spec, CheckEvidence.OfCode(code)));
    }

    [Fact]
    public void A_code_on_its_own_row_is_accepted()
    {
        IsValid(CheckKind.Memory, CheckItem.Create("memory.mismatches", CheckValue.Int(2), ItemVerdict.Check, CheckSource.Load, evidence: CheckEvidence.OfCode(CheckEvidenceCode.PatternMismatch)));
        IsValid(CheckKind.Cpu, CheckItem.Create("cpu.compute_errors", CheckValue.Int(2), ItemVerdict.Check, CheckSource.Load, evidence: CheckEvidence.OfCode(CheckEvidenceCode.ComputeMismatch)));
        IsValid(CheckKind.Cpu, CheckItem.Create("cpu.steadiness", CheckValue.Number(40), ItemVerdict.Caution, CheckSource.Load, CheckReason.Partial, evidence: CheckEvidence.OfCode(CheckEvidenceCode.TooShort)));
        IsValid(CheckKind.System, CheckItem.Create("system.gpu.device_status", CheckValue.Int(22), ItemVerdict.Check, CheckSource.Setupapi, subject: new CheckSubject("gpu-1"), evidence: CheckEvidence.OfCode(CheckEvidenceCode.DisabledCode22)));
        IsValid(CheckKind.Storage, CheckItem.Create("storage.ata.199", CheckValue.Dec("5"), ItemVerdict.Check, CheckSource.Ata, subject: Disk1, evidence: CheckEvidence.OfCode(CheckEvidenceCode.LikelyCable)));
        IsValid(CheckKind.Storage, CheckItem.Create("storage.ata.199", CheckValue.Dec("5"), ItemVerdict.Caution, CheckSource.Ata, subject: Disk1, evidence: CheckEvidence.OfCode(CheckEvidenceCode.CounterIncreased)));
    }

    // ---- §3.5-7: the baseline of one disk is one decision ----

    [Fact]
    public void A_disk_whose_baseline_is_missing_has_every_delta_null_with_the_same_code()
    {
        IsValid(
            CheckKind.Storage,
            NotComparable("storage.ata.5", CheckEvidenceCode.BaselineFirst),
            NotComparable("storage.ata.9", CheckEvidenceCode.BaselineFirst),
            NotComparable("storage.nvme.data_units_written", CheckEvidenceCode.BaselineFirst));
    }

    [Theory]
    [InlineData(CheckEvidenceCode.BaselineInventoryIncomplete)]
    [InlineData(CheckEvidenceCode.BaselineFirst)]
    [InlineData(CheckEvidenceCode.BaselineNoIdentity)]
    [InlineData(CheckEvidenceCode.BaselineDuplicate)]
    [InlineData(CheckEvidenceCode.BaselineDeviceChanged)]
    [InlineData(CheckEvidenceCode.BaselineCounterReset)]
    [InlineData(CheckEvidenceCode.BaselineClock)]
    [InlineData(CheckEvidenceCode.BaselineConcurrent)]
    [InlineData(CheckEvidenceCode.BaselineReadFailed)]
    [InlineData(CheckEvidenceCode.BaselineLegacyRefused)]
    public void A_disk_wide_code_on_one_delta_forbids_a_compared_delta_on_the_same_disk(CheckEvidenceCode code)
    {
        Assert.True(CheckEvidenceCodes.IsPerDisk(code));
        Fails(
            "so every .delta of the disk must be null",
            CheckKind.Storage,
            NotComparable("storage.ata.5", code),
            Compared("storage.ata.9"));
    }

    [Theory]
    [InlineData(CheckEvidenceCode.BaselineInventoryIncomplete)]
    [InlineData(CheckEvidenceCode.BaselineFirst)]
    [InlineData(CheckEvidenceCode.BaselineNoIdentity)]
    [InlineData(CheckEvidenceCode.BaselineDuplicate)]
    [InlineData(CheckEvidenceCode.BaselineDeviceChanged)]
    [InlineData(CheckEvidenceCode.BaselineCounterReset)]
    [InlineData(CheckEvidenceCode.BaselineClock)]
    [InlineData(CheckEvidenceCode.BaselineConcurrent)]
    [InlineData(CheckEvidenceCode.BaselineReadFailed)]
    [InlineData(CheckEvidenceCode.BaselineLegacyRefused)]
    public void A_disk_wide_code_means_every_other_delta_of_the_disk_carries_that_same_code(CheckEvidenceCode code)
    {
        // The other row is null too, but for the missing-counter reason (a per-row code): still not the same decision.
        Fails(
            "every .delta of this disk must carry the same baseline code",
            CheckKind.Storage,
            NotComparable("storage.ata.5", code),
            NotComparable("storage.ata.9", CheckEvidenceCode.BaselineMissingCounter));
    }

    [Fact]
    public void Two_different_disk_wide_codes_on_one_disk_are_refused()
    {
        Fails(
            "different disk-wide baseline codes",
            CheckKind.Storage,
            NotComparable("storage.ata.5", CheckEvidenceCode.BaselineFirst),
            NotComparable("storage.ata.9", CheckEvidenceCode.BaselineClock));
    }

    [Fact]
    public void An_incomplete_inventory_is_about_the_disk_it_is_on_so_a_disk_with_a_strong_key_is_still_compared()
    {
        // DESIGN-A §5.2: only weak-key and location-keyed (twin) disks are held back; the others are compared in the same result.
        IsValid(
            CheckKind.Storage,
            NotComparable("storage.ata.5", CheckEvidenceCode.BaselineInventoryIncomplete, Disk1),
            NotComparable("storage.ata.9", CheckEvidenceCode.BaselineInventoryIncomplete, Disk1),
            Compared("storage.ata.5", Disk2),
            Compared("storage.ata.9", Disk2));
        Fails(
            "different disk-wide baseline codes",
            CheckKind.Storage,
            NotComparable("storage.ata.5", CheckEvidenceCode.BaselineInventoryIncomplete),
            NotComparable("storage.ata.9", CheckEvidenceCode.BaselineFirst));
    }

    [Fact]
    public void Missing_counter_is_about_one_row_so_the_other_rows_may_be_compared()
    {
        Assert.False(CheckEvidenceCodes.IsPerDisk(CheckEvidenceCode.BaselineMissingCounter));
        IsValid(
            CheckKind.Storage,
            NotComparable("storage.nvme.available_spare", CheckEvidenceCode.BaselineMissingCounter),
            Compared("storage.nvme.data_units_written"),
            Compared("storage.ata.5"));
        // Several rows of one disk may lack their counter.
        IsValid(
            CheckKind.Storage,
            NotComparable("storage.nvme.available_spare", CheckEvidenceCode.BaselineMissingCounter),
            NotComparable("storage.nvme.percentage_used", CheckEvidenceCode.BaselineMissingCounter),
            Compared("storage.ata.5"));
    }

    [Fact]
    public void The_disks_are_independent_of_each_other()
    {
        IsValid(
            CheckKind.Storage,
            NotComparable("storage.ata.5", CheckEvidenceCode.BaselineFirst, Disk1),
            NotComparable("storage.ata.9", CheckEvidenceCode.BaselineFirst, Disk1),
            Compared("storage.ata.5", Disk2),
            Compared("storage.ata.9", Disk2));
        IsValid(
            CheckKind.Storage,
            NotComparable("storage.ata.5", CheckEvidenceCode.BaselineFirst, Disk1),
            NotComparable("storage.ata.5", CheckEvidenceCode.BaselineDeviceChanged, Disk2));
    }

    [Fact]
    public void A_compared_disk_is_fine_with_every_delta_compared()
    {
        IsValid(CheckKind.Storage, Compared("storage.ata.5"), Compared("storage.ata.9"), Compared("storage.nvme.media_errors"));
    }

    // ---- §3.5-6: a .delta that could not be compared has exactly one shape ----

    [Fact]
    public void A_delta_that_could_not_be_compared_is_null_info_partial_with_only_a_baseline_code()
    {
        IsValid(CheckKind.Storage, Delta("storage.ata.5", null, ItemVerdict.Info, CheckEvidence.OfCode(CheckEvidenceCode.BaselineFirst), CheckReason.Partial));
        // A judged counter's delta and an info counter's delta have the same shape when they are not comparable.
        IsValid(CheckKind.Storage, Delta("storage.ata.9", null, ItemVerdict.Info, CheckEvidence.OfCode(CheckEvidenceCode.BaselineFirst), CheckReason.Partial));
    }

    [Theory]
    [InlineData(ItemVerdict.Ok)]
    [InlineData(ItemVerdict.Caution)]
    [InlineData(ItemVerdict.Check)]
    [InlineData(ItemVerdict.Unavailable)]
    public void A_not_comparable_delta_must_be_info(ItemVerdict verdict) =>
        Fails("must have verdict info", CheckKind.Storage, Delta("storage.ata.5", null, verdict, CheckEvidence.OfCode(CheckEvidenceCode.BaselineFirst), CheckReason.Partial));

    [Theory]
    [InlineData(CheckReason.Unsupported)]
    [InlineData(CheckReason.Denied)]
    [InlineData(CheckReason.Timeout)]
    [InlineData(CheckReason.Error)]
    public void A_not_comparable_delta_must_have_reason_partial(CheckReason reason) =>
        Fails("must have reason partial", CheckKind.Storage, Delta("storage.ata.5", null, ItemVerdict.Info, CheckEvidence.OfCode(CheckEvidenceCode.BaselineFirst), reason));

    [Fact]
    public void A_not_comparable_delta_with_no_reason_is_refused() =>
        Fails("no value, so a reason is required", CheckKind.Storage, Delta("storage.ata.5", null, ItemVerdict.Info, CheckEvidence.OfCode(CheckEvidenceCode.BaselineFirst), null));

    [Fact]
    public void A_not_comparable_delta_needs_a_baseline_code_and_nothing_else_in_evidence()
    {
        Fails("needs evidence.code of the form baseline_*", CheckKind.Storage, Delta("storage.ata.5", null, ItemVerdict.Info, null, CheckReason.Partial));
        Fails("needs evidence.code of the form baseline_*", CheckKind.Storage, Delta("storage.ata.5", null, ItemVerdict.Info, new CheckEvidence { Before = "1" }, CheckReason.Partial));
        Fails("needs evidence.code of the form baseline_*", CheckKind.Storage, Delta("storage.ata.5", null, ItemVerdict.Info, CheckEvidence.OfCode(CheckEvidenceCode.CounterIncreased), CheckReason.Partial));

        var code = CheckEvidenceCode.BaselineFirst;
        Fails("carries only evidence.code", CheckKind.Storage, Delta("storage.ata.5", null, ItemVerdict.Info, new CheckEvidence { Code = code, Before = "1" }, CheckReason.Partial));
        Fails("carries only evidence.code", CheckKind.Storage, Delta("storage.ata.5", null, ItemVerdict.Info, new CheckEvidence { Code = code, After = "2" }, CheckReason.Partial));
        Fails("carries only evidence.code", CheckKind.Storage, Delta("storage.ata.5", null, ItemVerdict.Info, new CheckEvidence { Code = code, SinceUtc = Since }, CheckReason.Partial));
        Fails("carries only evidence.code", CheckKind.Storage, Delta("storage.nvme.critical_warning", null, ItemVerdict.Info, new CheckEvidence { Code = code, NewBits = 1 }, CheckReason.Partial));
        Fails("carries only evidence.code", CheckKind.Storage, Delta("storage.ata.5", null, ItemVerdict.Info, new CheckEvidence { Code = code, DiskId = "disk-1" }, CheckReason.Partial));
    }

    [Fact]
    public void A_compared_delta_is_judged_by_its_counter_and_never_info()
    {
        IsValid(CheckKind.Storage, Compared("storage.ata.5"));
        IsValid(CheckKind.Storage, Compared("storage.ata.9"));
        Fails("a compared delta is judged", CheckKind.Storage, Delta("storage.ata.5", CheckValue.SignedDec("1"), ItemVerdict.Info, new CheckEvidence { Before = "1", After = "2", SinceUtc = Since }));
        Fails("a compared delta cannot carry a baseline_* code", CheckKind.Storage, Delta("storage.ata.5", CheckValue.SignedDec("1"), ItemVerdict.Caution, new CheckEvidence { Code = CheckEvidenceCode.BaselineFirst, Before = "1", After = "2", SinceUtc = Since }));
    }

    // ---- §3.5-8: units that cannot be negative ----

    [Theory]
    [InlineData("memory.mismatches", CheckKind.Memory, "count")]
    [InlineData("system.memory.modules", CheckKind.System, "count")]
    [InlineData("system.gpu.device_status", CheckKind.System, "code")]
    [InlineData("system.gpu.driver_age", CheckKind.System, "d")]
    [InlineData("storage.nvme.critical_warning", CheckKind.Storage, "bits")]
    public void A_negative_integer_is_refused_for_count_code_d_and_bits(string id, CheckKind kind, string unit)
    {
        var spec = CheckItemRegistry.Get(id);
        var subject = spec.IsRepeated ? new CheckSubject(SubjectKinds.Prefix(spec.Subject) + "-1") : null;
        Fails($"a value in '{unit}' cannot be negative", kind, CheckItem.Create(id, CheckValue.Int(-1), ItemVerdict.Caution, CheckSource.Load, subject: subject));
    }

    [Theory]
    [InlineData("memory.mismatches", CheckKind.Memory)]
    [InlineData("storage.nvme.critical_warning", CheckKind.Storage)]
    [InlineData("system.gpu.device_status", CheckKind.System)]
    public void Zero_is_not_negative(string id, CheckKind kind)
    {
        var spec = CheckItemRegistry.Get(id);
        var subject = spec.IsRepeated ? new CheckSubject(SubjectKinds.Prefix(spec.Subject) + "-1") : null;
        var problems = Problems(kind, CheckItem.Create(id, CheckValue.Int(0), ItemVerdict.Ok, CheckSource.Load, subject: subject));
        Assert.DoesNotContain(problems, static p => p.Contains("cannot be negative", StringComparison.Ordinal));
    }

    [Fact]
    public void A_negative_temperature_is_a_number_and_stays_allowed()
    {
        var problems = Problems(CheckKind.Storage, CheckItem.Create("storage.temperature", CheckValue.Number(-5), ItemVerdict.Caution, CheckSource.Nvme, subject: Disk1));
        Assert.DoesNotContain(problems, static p => p.Contains("cannot be negative", StringComparison.Ordinal));
    }

    // ---- §3.5-3: every kind of subject is numbered from 1 ----

    [Theory]
    [InlineData("cpu.logical_throughput", "cpu-0", CheckKind.Cpu, false)]
    [InlineData("cpu.logical_throughput", "cpu-1", CheckKind.Cpu, true)]
    [InlineData("cpu.logical_throughput", "cpu-2", CheckKind.Cpu, true)]
    [InlineData("system.memory.capacity", "ram-0", CheckKind.System, false)]
    [InlineData("system.memory.capacity", "ram-1", CheckKind.System, true)]
    [InlineData("system.storage.free", "volume-0", CheckKind.System, false)]
    [InlineData("system.storage.free", "volume-1", CheckKind.System, true)]
    [InlineData("system.pagefile.allocated", "pagefile-0", CheckKind.System, false)]
    [InlineData("system.pagefile.allocated", "pagefile-1", CheckKind.System, true)]
    [InlineData("storage.capacity", "disk-0", CheckKind.Storage, false)]
    [InlineData("storage.capacity", "disk-1", CheckKind.Storage, true)]
    [InlineData("system.gpu.model", "gpu-0", CheckKind.System, false)]
    [InlineData("system.gpu.model", "gpu-1", CheckKind.System, true)]
    public void A_subject_is_numbered_from_one_for_every_kind(string id, string subjectId, CheckKind kind, bool valid)
    {
        var spec = CheckItemRegistry.Get(id);
        CheckValue value = spec.ValueType switch
        {
            CheckValueType.Number => CheckValue.Number(1),
            CheckValueType.Dec => CheckValue.Dec("1"),
            CheckValueType.Bool => CheckValue.Bool(true),
            CheckValueType.Text => CheckValue.Text("x"),
            _ => CheckValue.Int(1),
        };
        var item = CheckItem.Create(id, value, spec.Class == ItemClass.Judged ? ItemVerdict.Ok : ItemVerdict.Info, CheckSource.Load, subject: new CheckSubject(subjectId));
        var problems = Problems(kind, item);
        var refused = problems.Any(static p => p.Contains("subject id must look like", StringComparison.Ordinal));
        Assert.Equal(!valid, refused);
    }

    // ---- §3.5-4: evidence is compared by value, the module capacities included ----

    [Fact]
    public void Two_evidences_with_the_same_capacities_in_separate_lists_are_equal_and_hash_alike()
    {
        var a = new CheckEvidence { Code = CheckEvidenceCode.MixedCapacity, Capacities = new List<string> { "8589934592", "17179869184" } };
        var b = new CheckEvidence { Code = CheckEvidenceCode.MixedCapacity, Capacities = new[] { "8589934592", "17179869184" } };

        Assert.NotSame(a.Capacities, b.Capacities);
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Single(new HashSet<CheckEvidence> { a, b });
    }

    [Fact]
    public void Evidences_with_other_capacities_or_a_missing_list_are_different()
    {
        var a = new CheckEvidence { Code = CheckEvidenceCode.MixedCapacity, Capacities = ["8589934592", "17179869184"] };

        Assert.NotEqual(a, a with { Capacities = ["17179869184", "8589934592"] });
        Assert.NotEqual(a, a with { Capacities = ["8589934592"] });
        Assert.NotEqual(a, a with { Capacities = ["8589934592", "17179869185"] });
        Assert.NotEqual(a, a with { Capacities = null });
        Assert.NotEqual(a with { Capacities = null }, a);
        Assert.Equal(a with { Capacities = null }, a with { Capacities = null });
    }

    [Fact]
    public void Two_items_that_differ_only_in_which_list_object_holds_the_capacities_are_equal()
    {
        CheckItem Item(IReadOnlyList<string> capacities) =>
            CheckItem.Create(
                "system.memory.modules",
                CheckValue.Int(2),
                ItemVerdict.Caution,
                CheckSource.Smbios,
                evidence: new CheckEvidence { Code = CheckEvidenceCode.MixedCapacity, Capacities = capacities });

        var one = Item(new List<string> { "8589934592", "17179869184" });
        var other = Item(new[] { "8589934592", "17179869184" });

        Assert.Equal(one, other);
        Assert.Equal(one.GetHashCode(), other.GetHashCode());
    }
}
