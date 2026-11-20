namespace PcMieru.Checks.Tests;

/// <summary>The rules of CheckResultValidator: what DESIGN-A §3 and §14 fix about one item and about a result.</summary>
public class ValidatorTests
{
    private static readonly CheckSubject Disk1 = new("disk-1", "Samsung SSD 980 PRO 2TB");
    private static readonly CheckSubject Gpu1 = new("gpu-1", "NVIDIA GeForce RTX 5070");
    private static readonly DateTimeOffset Since = Samples.Start.AddDays(-1);

    private static CheckResult Result(CheckKind kind, params CheckItem[] items) =>
        CheckResult.Create(Samples.App, Samples.StorageRun, kind, Samples.Start, Samples.Start.AddSeconds(1), CheckOutcome.Completed, items);

    private static IReadOnlyList<string> Problems(CheckKind kind, params CheckItem[] items) =>
        CheckResultValidator.Validate(Result(kind, items));

    private static void IsValid(CheckKind kind, params CheckItem[] items) => Assert.Empty(Problems(kind, items));

    private static void Fails(string fragment, CheckKind kind, params CheckItem[] items)
    {
        var problems = Problems(kind, items);
        Assert.True(
            problems.Any(p => p.Contains(fragment, StringComparison.Ordinal)),
            $"expected a problem containing '{fragment}', got: {(problems.Count == 0 ? "(none)" : string.Join(" | ", problems))}");
    }

    private static CheckItem Delta(string counter, CheckValue? value, ItemVerdict verdict, CheckEvidence? evidence, CheckReason? reason = null) =>
        CheckItem.Create(counter + ".delta", value, verdict, CheckSource.Baseline, reason, Disk1, evidence);

    private static CheckEvidence Compared(string before, string after, CheckEvidenceCode? code = null) =>
        new() { Code = code, Before = before, After = after, SinceUtc = Since };

    // ---- DESIGN-A §14.0: judged and info, and the two info exceptions ----

    [Fact]
    public void An_observation_only_item_must_be_info()
    {
        IsValid(CheckKind.Memory, CheckItem.Create("memory.total", CheckValue.Dec("1"), ItemVerdict.Info, CheckSource.Win32));
        Fails("must have verdict info", CheckKind.Memory, CheckItem.Create("memory.total", CheckValue.Dec("1"), ItemVerdict.Ok, CheckSource.Win32));
        Fails("must have verdict info", CheckKind.Memory, CheckItem.Create("memory.total", CheckValue.Dec("1"), ItemVerdict.Caution, CheckSource.Win32));
    }

    [Fact]
    public void An_observation_only_item_may_be_missing_and_stays_info()
    {
        IsValid(CheckKind.Memory, CheckItem.Create("memory.passes", null, ItemVerdict.Info, CheckSource.Load, CheckReason.Unsupported));
        Fails("must have verdict info", CheckKind.Memory, CheckItem.Create("memory.passes", null, ItemVerdict.Unavailable, CheckSource.Load, CheckReason.Unsupported));
    }

    [Theory]
    [InlineData("memory.mismatches", CheckKind.Memory)]
    [InlineData("cpu.compute_errors", CheckKind.Cpu)]
    [InlineData("cpu.steadiness", CheckKind.Cpu)]
    [InlineData("system.os.uptime", CheckKind.System)]
    [InlineData("system.eventlog.whea7d", CheckKind.System)]
    public void A_judged_item_cannot_be_info(string id, CheckKind kind)
    {
        var spec = CheckItemRegistry.Get(id);
        var value = spec.ValueType == CheckValueType.Number ? CheckValue.Number(1) : CheckValue.Int(1);
        Fails("a judged item cannot have verdict info", kind, CheckItem.Create(id, value, ItemVerdict.Info, CheckSource.Load));
        Fails("a judged item cannot have verdict info", kind, CheckItem.Create(id, null, ItemVerdict.Info, CheckSource.Load, CheckReason.Error));
    }

    [Theory]
    [InlineData(ItemVerdict.Ok)]
    [InlineData(ItemVerdict.Caution)]
    [InlineData(ItemVerdict.Check)]
    public void A_judged_item_with_a_value_takes_ok_caution_or_check(ItemVerdict verdict) =>
        IsValid(CheckKind.Memory, CheckItem.Create("memory.mismatches", CheckValue.Int(1), verdict, CheckSource.Load));

    [Fact]
    public void A_judged_item_with_no_value_must_be_unavailable()
    {
        IsValid(CheckKind.Memory, CheckItem.Create("memory.mismatches", null, ItemVerdict.Unavailable, CheckSource.Load, CheckReason.Denied));
        Fails("without a value must be unavailable", CheckKind.Memory, CheckItem.Create("memory.mismatches", null, ItemVerdict.Ok, CheckSource.Load, CheckReason.Denied));
        Fails("without a value must be unavailable", CheckKind.Memory, CheckItem.Create("memory.mismatches", null, ItemVerdict.Check, CheckSource.Load, CheckReason.Denied));
    }

    [Fact]
    public void The_gpu_driver_rows_are_info_exactly_when_the_device_status_of_the_same_gpu_is_not_0_exception_2()
    {
        CheckItem Status(int code, CheckSubject? gpu = null) =>
            CheckItem.Create("system.gpu.device_status", CheckValue.Int(code), code == 0 ? ItemVerdict.Ok : ItemVerdict.Check, CheckSource.Setupapi, subject: gpu ?? Gpu1);
        CheckItem Age(ItemVerdict verdict, CheckSubject? gpu = null) =>
            CheckItem.Create("system.gpu.driver_age", CheckValue.Int(900), verdict, CheckSource.Win32, subject: gpu ?? Gpu1);
        CheckItem Generic(ItemVerdict verdict, CheckSubject? gpu = null) =>
            CheckItem.Create("system.gpu.generic_driver", CheckValue.Bool(true), verdict, CheckSource.Win32, subject: gpu ?? Gpu1);

        // Status 43 (not 0): the two driver rows are info.
        IsValid(CheckKind.System, Status(43), Age(ItemVerdict.Info), Generic(ItemVerdict.Info));

        // Status 0: the same rows are judged.
        IsValid(CheckKind.System, Status(0), Age(ItemVerdict.Caution), Generic(ItemVerdict.Ok));

        // Status not 0 but the rows are judged, and status 0 but the rows are info: both are refused.
        Fails("the device status of this GPU is not 0, so this row must be info", CheckKind.System, Status(43), Age(ItemVerdict.Caution), Generic(ItemVerdict.Info));
        Fails("the device status of this GPU is not 0, so this row must be info", CheckKind.System, Status(22), Age(ItemVerdict.Info), Generic(ItemVerdict.Ok));
        Fails("this row is info only when the device status of the same GPU is not 0", CheckKind.System, Status(0), Age(ItemVerdict.Info), Generic(ItemVerdict.Ok));
        Fails("this row is info only when the device status of the same GPU is not 0", CheckKind.System, Status(0), Age(ItemVerdict.Caution), Generic(ItemVerdict.Info));

        // With no status row at all there is nothing that is "not 0", so info is refused.
        Fails("this row is info only when the device status of the same GPU is not 0", CheckKind.System, Age(ItemVerdict.Info));
        Fails("this row is info only when the device status of the same GPU is not 0", CheckKind.System, Generic(ItemVerdict.Info));

        // A status that could not be read (unavailable, no value) is not "not 0" either.
        var unreadable = CheckItem.Create("system.gpu.device_status", null, ItemVerdict.Unavailable, CheckSource.Setupapi, CheckReason.Denied, Gpu1);
        Fails("this row is info only when the device status of the same GPU is not 0", CheckKind.System, unreadable, Age(ItemVerdict.Info));
        IsValid(CheckKind.System, unreadable, Age(ItemVerdict.Caution));

        // The decision is per GPU: gpu-2 failing says nothing about gpu-1.
        var gpu2 = new CheckSubject("gpu-2", "Another GPU");
        IsValid(CheckKind.System, Status(0), Status(43, gpu2), Age(ItemVerdict.Ok), Age(ItemVerdict.Info, gpu2));
        Fails("this row is info only when the device status of the same GPU is not 0", CheckKind.System, Status(0), Status(43, gpu2), Age(ItemVerdict.Info), Age(ItemVerdict.Info, gpu2));

        // The status row itself is never info.
        Fails(
            "a judged item cannot have verdict info",
            CheckKind.System,
            CheckItem.Create("system.gpu.device_status", CheckValue.Int(0), ItemVerdict.Info, CheckSource.Setupapi, subject: Gpu1));
        Fails(
            "a judged item cannot have verdict info",
            CheckKind.System,
            CheckItem.Create("system.gpu.device_status", CheckValue.Int(43), ItemVerdict.Info, CheckSource.Setupapi, subject: Gpu1));
    }

    [Fact]
    public void No_other_judged_item_may_be_info_even_next_to_a_failing_gpu()
    {
        var status = CheckItem.Create("system.gpu.device_status", CheckValue.Int(43), ItemVerdict.Check, CheckSource.Setupapi, subject: Gpu1);
        Fails(
            "a judged item cannot have verdict info",
            CheckKind.System,
            status,
            CheckItem.Create("system.os.uptime", CheckValue.Number(1), ItemVerdict.Info, CheckSource.Os));
    }

    [Fact]
    public void Only_the_two_gpu_ids_and_the_deltas_may_be_info_among_the_judged()
    {
        var mayBeInfo = CheckItemRegistry.All.Where(s => s.Class == ItemClass.Judged && s.MayBeInfo).Select(s => s.Id).ToList();
        var expected = new[] { "system.gpu.driver_age", "system.gpu.generic_driver" }
            .Concat(CheckItemRegistry.All.Where(s => s.IsDelta && s.Class == ItemClass.Judged).Select(s => s.Id))
            .OrderBy(id => id, StringComparer.Ordinal);
        Assert.Equal(expected, mayBeInfo.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Info_never_changes_the_overall_verdict_even_for_a_check_looking_row()
    {
        // A GPU driver row downgraded to info (exception 2) must not move the overall verdict: the failing device status is
        // the only "check", and taking the info row away changes nothing.
        var status = CheckItem.Create("system.gpu.device_status", CheckValue.Int(43), ItemVerdict.Caution, CheckSource.Setupapi, subject: Gpu1);
        var uptime = CheckItem.Create("system.os.uptime", CheckValue.Number(10), ItemVerdict.Ok, CheckSource.Os);
        var driver = CheckItem.Create("system.gpu.driver_age", CheckValue.Int(9000), ItemVerdict.Info, CheckSource.Win32, subject: Gpu1);
        var result = Result(CheckKind.System, uptime, status, driver);
        Assert.Equal(CheckVerdict.Caution, result.Verdict);
        Assert.Equal(new CheckCounts(1, 1, 0, 0, 1), result.Counts);
        Assert.Empty(CheckResultValidator.Validate(result));
        Assert.Equal(CheckVerdict.Caution, CheckVerdicts.Overall([uptime, status]));

        // With nothing else judged, an info row alone leaves "unavailable" (no judged item), never ok.
        Assert.Equal(CheckVerdict.Unavailable, CheckVerdicts.Overall([driver]));
    }

    // ---- DESIGN-A §3 / §14.0: value and reason ----

    [Fact]
    public void A_value_with_a_reason_is_allowed_for_partial_only()
    {
        var verdict = ItemVerdict.Check;
        IsValid(CheckKind.Memory, CheckItem.Create("memory.mismatches", CheckValue.Int(1), verdict, CheckSource.Load, CheckReason.Partial));

        foreach (var reason in new[] { CheckReason.Unsupported, CheckReason.Denied, CheckReason.Timeout, CheckReason.Error })
        {
            Fails("reason must be null or partial", CheckKind.Memory, CheckItem.Create("memory.mismatches", CheckValue.Int(1), verdict, CheckSource.Load, reason));
        }
    }

    [Fact]
    public void A_missing_value_needs_a_reason_and_any_reason_will_do()
    {
        foreach (var reason in Enum.GetValues<CheckReason>())
        {
            IsValid(CheckKind.Memory, CheckItem.Create("memory.mismatches", null, ItemVerdict.Unavailable, CheckSource.Load, reason));
        }

        Fails("no value, so a reason is required", CheckKind.Memory, CheckItem.Create("memory.mismatches", null, ItemVerdict.Unavailable, CheckSource.Load));
    }

    [Fact]
    public void An_ok_item_with_partial_is_rejected_not_counted_as_unavailable()
    {
        // The old rule let "ok + partial" through and read it as unavailable. §3.5-2: a value that did not finish is caution,
        // check or unavailable; ok means a complete reading, so ok + a reason is a producer bug and the writer refuses it.
        var item = CheckItem.Create("memory.mismatches", CheckValue.Int(0), ItemVerdict.Ok, CheckSource.Load, CheckReason.Partial);
        Fails("ok cannot carry a reason", CheckKind.Memory, item);

        var result = Result(CheckKind.Memory, item);
        Assert.Equal(CheckVerdict.Ok, result.Verdict); // Overall is a plain function of the verdicts; it does not repair the row
        Assert.Throws<CheckFormatException>(() => CheckJson.Serialize(result));
    }

    [Fact]
    public void The_value_has_the_type_the_registry_fixes()
    {
        Fails("must be of type Dec", CheckKind.Memory, CheckItem.Create("memory.total", CheckValue.Int(1), ItemVerdict.Info, CheckSource.Win32));
        Fails("must be of type Int", CheckKind.Memory, CheckItem.Create("memory.mismatches", CheckValue.Number(1), ItemVerdict.Ok, CheckSource.Load));
        Fails("must be of type Number", CheckKind.Memory, CheckItem.Create("memory.duration", CheckValue.Dec("4"), ItemVerdict.Info, CheckSource.Load));
        Fails("must be of type Bool", CheckKind.Storage, CheckItem.Create("storage.ata.predict_failure", CheckValue.Int(0), ItemVerdict.Ok, CheckSource.Ata, subject: Disk1));
        Fails("must be of type Text", CheckKind.Storage, CheckItem.Create("storage.health", CheckValue.Int(0), ItemVerdict.Ok, CheckSource.StorageQuery, subject: Disk1));
    }

    [Fact]
    public void The_unit_is_the_one_the_registry_fixes()
    {
        var ok = CheckItem.Create("memory.total", CheckValue.Dec("1"), ItemVerdict.Info, CheckSource.Win32);
        IsValid(CheckKind.Memory, ok);
        Fails("unit must be 'B'", CheckKind.Memory, ok with { Unit = "MiB" });
        Fails("unit must be 'B'", CheckKind.Memory, ok with { Unit = null });

        var noUnit = CheckItem.Create("storage.health", CheckValue.Text("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: Disk1);
        Assert.Null(noUnit.Unit);
        Fails("unit must be null", CheckKind.Storage, noUnit with { Unit = "B" });
    }

    [Fact]
    public void Number_and_count_values_are_checked()
    {
        Fails("a value in 'count' cannot be negative", CheckKind.Memory, CheckItem.Create("memory.mismatches", CheckValue.Int(-1), ItemVerdict.Ok, CheckSource.Load));

        // A number that is not finite cannot be made at all (the factory refuses), so it never reaches a result.
        Assert.Throws<ArgumentOutOfRangeException>(() => CheckValue.Number(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => CheckValue.Number(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => CheckValue.Number(double.NegativeInfinity));
        IsValid(CheckKind.Memory, CheckItem.Create("memory.duration", CheckValue.Number(-1.5), ItemVerdict.Info, CheckSource.Load)); // a number may be negative; only counts may not
    }

    [Fact]
    public void Text_values_come_from_the_fixed_lists_where_there_is_one()
    {
        foreach (var health in new[] { "healthy", "warning", "critical" })
        {
            IsValid(CheckKind.Storage, CheckItem.Create("storage.health", CheckValue.Text(health), ItemVerdict.Ok, CheckSource.StorageQuery, subject: Disk1));
        }

        Fails("must be one of healthy|warning|critical", CheckKind.Storage, CheckItem.Create("storage.health", CheckValue.Text("fine"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: Disk1));
        Fails("must be one of within|past_breach|current_breach", CheckKind.Storage, CheckItem.Create("storage.ata.threshold_status", CheckValue.Text("ok"), ItemVerdict.Ok, CheckSource.Ata, subject: Disk1));
        Fails("must not be empty", CheckKind.System, CheckItem.Create("system.cpu.model", CheckValue.Text(" "), ItemVerdict.Info, CheckSource.Wmi));
    }

    [Fact]
    public void The_os_version_value_has_a_name_a_build_and_optional_parts()
    {
        IsValid(CheckKind.System, CheckItem.Create("system.os.version", CheckValue.OsVersion(new OsVersionValue("Windows 11 Pro", "25H2", 26300, 7171)), ItemVerdict.Info, CheckSource.Os));
        IsValid(CheckKind.System, CheckItem.Create("system.os.version", CheckValue.OsVersion(new OsVersionValue("Windows 10", null, 19045, null)), ItemVerdict.Info, CheckSource.Os));
        Fails("os version needs a name", CheckKind.System, CheckItem.Create("system.os.version", CheckValue.OsVersion(new OsVersionValue("", null, 19045, null)), ItemVerdict.Info, CheckSource.Os));
        Fails("os version needs a name", CheckKind.System, CheckItem.Create("system.os.version", CheckValue.OsVersion(new OsVersionValue("Windows", " ", 19045, null)), ItemVerdict.Info, CheckSource.Os));
        Fails("os version needs a name", CheckKind.System, CheckItem.Create("system.os.version", CheckValue.OsVersion(new OsVersionValue("Windows", null, -1, null)), ItemVerdict.Info, CheckSource.Os));
    }

    // ---- DESIGN-A §3.3 / §14.5: the .delta items ----

    [Fact]
    public void A_compared_delta_is_after_minus_before_and_is_judged()
    {
        IsValid(CheckKind.Storage, Delta("storage.nvme.media_errors", CheckValue.SignedDec("0"), ItemVerdict.Ok, Compared("4", "4")));
        IsValid(CheckKind.Storage, Delta("storage.nvme.media_errors", CheckValue.SignedDec("3"), ItemVerdict.Caution, Compared("4", "7")));
        IsValid(CheckKind.Storage, Delta("storage.nvme.media_errors", CheckValue.SignedDec("-4"), ItemVerdict.Ok, Compared("8", "4"))); // a counter that went down (signed)
        IsValid(CheckKind.Storage, Delta("storage.nvme.media_errors", CheckValue.SignedDec("1"), ItemVerdict.Ok, Compared("340282366920938463463374607431768211454", "340282366920938463463374607431768211455")));

        Fails("must be after minus before (3)", CheckKind.Storage, Delta("storage.nvme.media_errors", CheckValue.SignedDec("4"), ItemVerdict.Caution, Compared("4", "7")));
        Fails("must be after minus before (-4)", CheckKind.Storage, Delta("storage.nvme.media_errors", CheckValue.SignedDec("4"), ItemVerdict.Caution, Compared("8", "4")));
    }

    [Fact]
    public void A_compared_delta_needs_before_after_and_since()
    {
        var value = CheckValue.SignedDec("3");
        const string need = "needs evidence before, after and sinceUtc";
        Fails(need, CheckKind.Storage, Delta("storage.nvme.media_errors", value, ItemVerdict.Caution, null));
        Fails(need, CheckKind.Storage, Delta("storage.nvme.media_errors", value, ItemVerdict.Caution, new CheckEvidence { Before = "4", After = "7" }));
        Fails(need, CheckKind.Storage, Delta("storage.nvme.media_errors", value, ItemVerdict.Caution, new CheckEvidence { After = "7", SinceUtc = Since }));
        Fails(need, CheckKind.Storage, Delta("storage.nvme.media_errors", value, ItemVerdict.Caution, new CheckEvidence { Before = "4", SinceUtc = Since }));
        Fails(need, CheckKind.Storage, Delta("storage.nvme.media_errors", value, ItemVerdict.Caution, CheckEvidence.OfCode(CheckEvidenceCode.CounterIncreased)));
    }

    [Fact]
    public void A_compared_delta_cannot_carry_a_baseline_code_and_may_not_be_info_when_judged()
    {
        Fails("cannot carry a baseline_* code", CheckKind.Storage, Delta("storage.nvme.media_errors", CheckValue.SignedDec("0"), ItemVerdict.Ok, Compared("1", "1", CheckEvidenceCode.BaselineFirst)));
        Fails("a compared delta is judged", CheckKind.Storage, Delta("storage.nvme.media_errors", CheckValue.SignedDec("0"), ItemVerdict.Info, Compared("1", "1")));
    }

    [Theory]
    [InlineData(CheckEvidenceCode.BaselineFirst)]
    [InlineData(CheckEvidenceCode.BaselineCounterReset)]
    [InlineData(CheckEvidenceCode.BaselineNoIdentity)]
    [InlineData(CheckEvidenceCode.BaselineLegacyRefused)]
    public void A_delta_that_cannot_be_compared_has_no_value_a_baseline_code_and_is_info_exception_1(CheckEvidenceCode code)
    {
        // Both a judged delta and an info delta may be missing; either way the row is info, never unavailable.
        IsValid(CheckKind.Storage, Delta("storage.nvme.media_errors", null, ItemVerdict.Info, CheckEvidence.OfCode(code), CheckReason.Partial));
        IsValid(CheckKind.Storage, Delta("storage.nvme.power_on_hours", null, ItemVerdict.Info, CheckEvidence.OfCode(code), CheckReason.Partial));
    }

    [Fact]
    public void A_missing_delta_without_a_baseline_code_is_refused()
    {
        const string need = "needs evidence.code of the form baseline_*";
        Fails(need, CheckKind.Storage, Delta("storage.nvme.media_errors", null, ItemVerdict.Info, null, CheckReason.Partial));
        Fails(need, CheckKind.Storage, Delta("storage.nvme.media_errors", null, ItemVerdict.Info, CheckEvidence.OfCode(CheckEvidenceCode.CounterIncreased), CheckReason.Partial));
        Fails(need, CheckKind.Storage, Delta("storage.nvme.media_errors", null, ItemVerdict.Info, new CheckEvidence { Before = "1", After = "2", SinceUtc = Since }, CheckReason.Partial));
    }

    [Fact]
    public void A_missing_delta_must_be_info_not_unavailable()
    {
        Fails(
            "a delta without a value (not comparable) must have verdict info",
            CheckKind.Storage,
            Delta("storage.nvme.media_errors", null, ItemVerdict.Unavailable, CheckEvidence.OfCode(CheckEvidenceCode.BaselineFirst), CheckReason.Partial));
        Fails(
            "a delta without a value (not comparable) must have verdict info",
            CheckKind.Storage,
            Delta("storage.nvme.media_errors", null, ItemVerdict.Ok, CheckEvidence.OfCode(CheckEvidenceCode.BaselineFirst), CheckReason.Partial));
    }

    [Fact]
    public void A_missing_delta_does_not_move_the_overall_verdict()
    {
        var result = Result(
            CheckKind.Storage,
            CheckItem.Create("storage.nvme.media_errors", CheckValue.Dec("0"), ItemVerdict.Ok, CheckSource.Nvme, subject: Disk1),
            Delta("storage.nvme.media_errors", null, ItemVerdict.Info, CheckEvidence.OfCode(CheckEvidenceCode.BaselineFirst), CheckReason.Partial));
        Assert.Empty(CheckResultValidator.Validate(result));
        Assert.Equal(CheckVerdict.Ok, result.Verdict);
    }

    [Fact]
    public void A_delta_of_an_info_counter_is_always_info_even_with_a_value()
    {
        IsValid(CheckKind.Storage, Delta("storage.nvme.power_on_hours", CheckValue.SignedDec("12"), ItemVerdict.Info, Compared("9088", "9100")));
        Fails("must have verdict info", CheckKind.Storage, Delta("storage.nvme.power_on_hours", CheckValue.SignedDec("12"), ItemVerdict.Caution, Compared("9088", "9100")));
    }

    [Fact]
    public void A_delta_value_is_a_signed_dec_with_a_disk_subject_and_the_unit_of_its_counter()
    {
        var ok = Delta("storage.nvme.media_errors", CheckValue.SignedDec("0"), ItemVerdict.Ok, Compared("1", "1"));
        Assert.Equal("count", ok.Unit);
        Assert.Equal("smart_raw", CheckItemRegistry.Get("storage.ata.5.delta").Unit);
        Assert.Equal("nvme_data_unit", CheckItemRegistry.Get("storage.nvme.data_units_written.delta").Unit);

        Fails("must be of type SignedDec", CheckKind.Storage, ok with { Value = CheckValue.Dec("0") });
        Fails("must be of type SignedDec", CheckKind.Storage, ok with { Value = CheckValue.Int(0) });
        Fails("a subject 'disk-N' is required", CheckKind.Storage, ok with { Subject = null });
        Fails("unit must be 'count'", CheckKind.Storage, ok with { Unit = "smart_raw" });
    }

    [Fact]
    public void Every_registered_delta_belongs_to_a_registered_counter_of_the_same_unit()
    {
        foreach (var delta in CheckItemRegistry.All.Where(s => s.IsDelta))
        {
            var counter = CheckItemRegistry.Get(delta.DeltaOf!);
            Assert.Equal(counter.Unit, delta.Unit);
            Assert.Equal(counter.Subject, delta.Subject);
            Assert.Equal(counter.Kind, delta.Kind);
            Assert.Equal(CheckValueType.SignedDec, delta.ValueType); // whatever the counter's own type is (a bit mask, a percentage, a 128-bit count)
        }
    }

    // ---- evidence ----

    [Fact]
    public void Evidence_is_omitted_rather_than_empty()
    {
        var item = CheckItem.Create("memory.mismatches", CheckValue.Int(1), ItemVerdict.Check, CheckSource.Load, evidence: new CheckEvidence());
        Fails("evidence must be omitted rather than empty", CheckKind.Memory, item);
        IsValid(CheckKind.Memory, item with { Evidence = null });
    }

    [Fact]
    public void Baseline_codes_belong_to_storage_items_only()
    {
        Fails(
            "cannot be carried by this item",
            CheckKind.Memory,
            CheckItem.Create("memory.mismatches", CheckValue.Int(1), ItemVerdict.Check, CheckSource.Load, evidence: CheckEvidence.OfCode(CheckEvidenceCode.BaselineFirst)));
        IsValid(
            CheckKind.Memory,
            CheckItem.Create("memory.mismatches", CheckValue.Int(1), ItemVerdict.Check, CheckSource.Load, evidence: CheckEvidence.OfCode(CheckEvidenceCode.PatternMismatch)));
    }

    [Fact]
    public void New_bits_are_evidence_of_the_critical_warning_row_and_its_delta_only()
    {
        var evidence = new CheckEvidence { Before = "0", After = "2", SinceUtc = Since, NewBits = 2 };
        IsValid(CheckKind.Storage, Delta("storage.nvme.critical_warning", CheckValue.SignedDec("2"), ItemVerdict.Check, evidence));
        IsValid(
            CheckKind.Storage,
            CheckItem.Create("storage.nvme.critical_warning", CheckValue.Int(2), ItemVerdict.Check, CheckSource.Nvme, subject: Disk1, evidence: new CheckEvidence { Code = CheckEvidenceCode.NewBits, NewBits = 2 }));
        Fails(
            "newBits is evidence of storage.nvme.critical_warning and its .delta only",
            CheckKind.Storage,
            CheckItem.Create("storage.nvme.media_errors", CheckValue.Dec("2"), ItemVerdict.Caution, CheckSource.Nvme, subject: Disk1, evidence: new CheckEvidence { NewBits = 2 }));
        Fails("newBits is evidence of storage.nvme.critical_warning and its .delta only", CheckKind.Storage, Delta("storage.nvme.media_errors", CheckValue.SignedDec("2"), ItemVerdict.Caution, evidence));
        Fails("evidence integers cannot be negative", CheckKind.Storage, Delta("storage.nvme.critical_warning", CheckValue.SignedDec("2"), ItemVerdict.Check, evidence with { NewBits = -1 }));
    }

    [Fact]
    public void Evidence_values_have_the_type_the_design_fixes()
    {
        CheckItem Item(CheckEvidence e) => CheckItem.Create("memory.mismatches", CheckValue.Int(1), ItemVerdict.Check, CheckSource.Load, evidence: e);

        IsValid(
            CheckKind.Memory,
            Item(new CheckEvidence
            {
                Code = CheckEvidenceCode.PatternMismatch,
                TotalBytes = "1000204886016",
                DiskId = "disk-3",
                EfficiencyClass = 1,
                Cv = 2.5,
                ClockSteadiness = 91.25,
                WarningTempMinutes = "10",
                CriticalTempMinutes = "0",
                RecentMediaErrors = 0,
                RecentEntries = 4,
                Capacities = ["8589934592", "8589934592"],
            }));

        Fails("evidence.totalBytes must be an unsigned decimal string", CheckKind.Memory, Item(new CheckEvidence { TotalBytes = "-1" }));
        Fails("evidence.totalBytes must be an unsigned decimal string", CheckKind.Memory, Item(new CheckEvidence { TotalBytes = "01" }));
        Fails("evidence.before must be an unsigned decimal string", CheckKind.Memory, Item(new CheckEvidence { Before = "1.0" }));
        Fails("evidence.warningTempMinutes must be an unsigned decimal string", CheckKind.Memory, Item(new CheckEvidence { WarningTempMinutes = "" }));
        Fails("evidence.diskId must look like 'disk-N'", CheckKind.Memory, Item(new CheckEvidence { DiskId = "C:" }));
        Fails("evidence.diskId must look like 'disk-N'", CheckKind.Memory, Item(new CheckEvidence { DiskId = "disk-01" }));
        Fails("evidence.sinceUtc must be UTC with millisecond precision", CheckKind.Memory, Item(new CheckEvidence { SinceUtc = Since.AddTicks(1) }));
        Fails("evidence.sinceUtc must be UTC with millisecond precision", CheckKind.Memory, Item(new CheckEvidence { SinceUtc = Since.ToOffset(TimeSpan.FromHours(9)) }));
        Fails("evidence numbers must be finite", CheckKind.Memory, Item(new CheckEvidence { Cv = double.NaN }));
        Fails("evidence numbers must be finite", CheckKind.Memory, Item(new CheckEvidence { ClockSteadiness = double.PositiveInfinity }));
        Fails("evidence.capacities must not be empty", CheckKind.Memory, Item(new CheckEvidence { Capacities = [] }));
        Fails("evidence.capacities must be unsigned decimal strings", CheckKind.Memory, Item(new CheckEvidence { Capacities = ["8GiB"] }));
        Fails("evidence integers cannot be negative", CheckKind.Memory, Item(new CheckEvidence { RecentEntries = -1 }));
    }

    // ---- subjects ----

    [Fact]
    public void A_repeated_item_needs_a_subject_of_its_kind_and_a_single_item_must_not_have_one()
    {
        IsValid(CheckKind.Storage, CheckItem.Create("storage.health", CheckValue.Text("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: Disk1));
        Fails("a subject 'disk-N' is required", CheckKind.Storage, CheckItem.Create("storage.health", CheckValue.Text("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery));
        Fails("subject id must look like 'disk-N'", CheckKind.Storage, CheckItem.Create("storage.health", CheckValue.Text("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: new CheckSubject("gpu-1")));
        Fails("subject id must look like 'disk-N'", CheckKind.Storage, CheckItem.Create("storage.health", CheckValue.Text("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: new CheckSubject("\\\\.\\PHYSICALDRIVE0")));
        Fails("subject id must look like 'disk-N'", CheckKind.Storage, CheckItem.Create("storage.health", CheckValue.Text("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: new CheckSubject("disk-1 ")));
        Fails("must not have a subject", CheckKind.Storage, CheckItem.Create("storage.devices", CheckValue.Int(1), ItemVerdict.Info, CheckSource.StorageQuery, subject: Disk1));
    }

    [Fact]
    public void A_subject_model_is_omitted_rather_than_empty_and_never_checked_further()
    {
        IsValid(CheckKind.Storage, CheckItem.Create("storage.health", CheckValue.Text("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: new CheckSubject("disk-1")));
        Fails("subject.model must be omitted rather than empty", CheckKind.Storage, CheckItem.Create("storage.health", CheckValue.Text("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: new CheckSubject("disk-1", " ")));
    }

    [Fact]
    public void The_same_id_and_subject_cannot_appear_twice_but_another_subject_can()
    {
        CheckItem Health(string subject) => CheckItem.Create("storage.health", CheckValue.Text("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery, subject: new CheckSubject(subject));

        IsValid(CheckKind.Storage, Health("disk-1"), Health("disk-2"));
        Fails("is not unique in the result", CheckKind.Storage, Health("disk-1"), Health("disk-1"));

        var single = CheckItem.Create("storage.devices", CheckValue.Int(1), ItemVerdict.Info, CheckSource.StorageQuery);
        Fails("is not unique in the result", CheckKind.Storage, single, single);
    }

    // ---- ids and kinds ----

    [Fact]
    public void An_unknown_id_or_an_id_of_another_kind_is_refused()
    {
        Fails("unknown item ID", CheckKind.Memory, new CheckItem("memory.latency", null, CheckValue.Int(1), "count", ItemVerdict.Ok, CheckSource.Load, null));
        Fails("unknown item ID", CheckKind.Memory, new CheckItem("memory.mismatches.delta", null, CheckValue.Int(1), "count", ItemVerdict.Ok, CheckSource.Load, null));
        Fails("unknown item ID", CheckKind.Storage, new CheckItem("storage.nvme.temperature", null, CheckValue.Number(1), "C", ItemVerdict.Ok, CheckSource.Nvme, null));
        Fails("unknown item ID", CheckKind.Storage, new CheckItem("storage.ata.2.delta", Disk1, CheckValue.SignedDec("1"), "smart_raw", ItemVerdict.Ok, CheckSource.Baseline, null));
        Fails("belongs to a Memory result, not Cpu", CheckKind.Cpu, CheckItem.Create("memory.mismatches", CheckValue.Int(1), ItemVerdict.Ok, CheckSource.Load));
    }

    [Fact]
    public void Free_text_cannot_stand_in_for_a_source_or_a_code()
    {
        // The enums are closed: a number outside the list is an unknown value, not a new vocabulary word.
        var item = CheckItem.Create("memory.mismatches", CheckValue.Int(0), ItemVerdict.Ok, CheckSource.Load);
        Fails("unknown source", CheckKind.Memory, item with { Source = (CheckSource)(Enum.GetValues<CheckSource>().Length + 5) });
        Fails("unknown source", CheckKind.Memory, item with { Source = (CheckSource)(-1) });
        Fails("unknown reason", CheckKind.Memory, item with { Reason = (CheckReason)99, Value = null });
        Fails("unknown evidence code", CheckKind.Memory, item with { Evidence = new CheckEvidence { Code = (CheckEvidenceCode)(Enum.GetValues<CheckEvidenceCode>().Length + 5) } });

        // An unknown item verdict cannot even be counted, so build the result by hand: Validate must report, not throw.
        var raw = Samples.MemoryCheck() with { Items = [item with { Verdict = (ItemVerdict)9 }] };
        var problems = CheckResultValidator.Validate(raw);
        Assert.Contains(problems, p => p.Contains("unknown verdict", StringComparison.Ordinal));

        var withNull = Samples.MemoryCheck() with { Items = [null!] };
        Assert.Contains(CheckResultValidator.Validate(withNull), p => p.Contains("items[0] is null", StringComparison.Ordinal));
    }

    // ---- result level ----

    [Fact]
    public void A_result_of_the_wrong_shape_is_refused()
    {
        var ok = Samples.MemoryCheck();
        Assert.Empty(CheckResultValidator.Validate(ok));

        bool Has(CheckResult result, string fragment) => CheckResultValidator.Validate(result).Any(p => p.Contains(fragment, StringComparison.Ordinal));

        Assert.True(Has(ok with { Format = "pcm-check/2" }, "format must be 'pcm-check/1'"));
        Assert.True(Has(ok with { App = "" }, "app must not be empty"));
        Assert.True(Has(ok with { RunId = Guid.Empty }, "empty UUID"));
        Assert.True(Has(ok with { Kind = (CheckKind)9 }, "kind is not a known kind"));
        Assert.True(Has(ok with { Outcome = (CheckOutcome)9 }, "outcome is not a known outcome"));
        Assert.True(Has(ok with { Verdict = (CheckVerdict)9 }, "verdict is not a known verdict"));
        Assert.True(Has(ok with { EndedAtUtc = ok.StartedAtUtc.AddMilliseconds(-1) }, "endedAtUtc is before startedAtUtc"));
        Assert.True(Has(ok with { StartedAtUtc = ok.StartedAtUtc.AddTicks(5) }, "startedAtUtc must be UTC with millisecond precision"));
        Assert.True(Has(ok with { EndedAtUtc = ok.EndedAtUtc.ToOffset(TimeSpan.FromHours(1)) }, "endedAtUtc must be UTC with millisecond precision"));
        Assert.True(Has(ok with { Counts = new CheckCounts(1, 0, 1, 0, 8) }, "do not match the items"));
        Assert.True(Has(ok with { Counts = new CheckCounts(0, 0, 1, 0, -8) }, "five non-negative values"));
        Assert.True(Has(ok with { Verdict = CheckVerdict.Caution }, "does not match the items"));
        Assert.True(Has(ok with { Notes = ["memory.borrowed_region_only", "memory.borrowed_region_only"] }, "appears twice"));
        Assert.True(Has(ok with { Notes = ["memory"] }, "not of the form area.snake_code"));
    }

    [Theory]
    [InlineData("cpu.temperature_not_measured", true)]
    [InlineData("memory.borrowed_region_only", true)]
    [InlineData("memory.pressure", true)]
    [InlineData("storage.baseline_read_failed", true)]
    [InlineData("a.b", true)]
    [InlineData("a1.b_2", true)]
    [InlineData("", false)]
    [InlineData("cpu", false)]
    [InlineData(".x", false)]
    [InlineData("cpu.", false)]
    [InlineData("cpu.a.b", false)]
    [InlineData("Cpu.a", false)]
    [InlineData("cpu.A", false)]
    [InlineData("cpu.a b", false)]
    [InlineData("1cpu.a", false)]
    [InlineData("cpu._a", false)]
    [InlineData("cpu.a-b", false)]
    public void A_note_is_area_dot_snake_code(string note, bool expected) =>
        Assert.Equal(expected, CheckNotes.IsWellFormed(note));

    [Fact]
    public void The_four_named_notes_are_well_formed()
    {
        foreach (var note in new[] { CheckNotes.CpuTemperatureNotMeasured, CheckNotes.MemoryBorrowedRegionOnly, CheckNotes.MemoryPressure, CheckNotes.StorageBaselineReadFailed })
        {
            Assert.True(CheckNotes.IsWellFormed(note), note);
        }
    }

    [Fact]
    public void Validate_lists_every_problem_and_EnsureValid_throws_with_them()
    {
        var ok = Samples.MemoryCheck();
        var broken = ok with { App = "", RunId = Guid.Empty, Verdict = CheckVerdict.Ok };
        var problems = CheckResultValidator.Validate(broken);
        Assert.Equal(3, problems.Count);

        var ex = Assert.Throws<CheckFormatException>(() => CheckResultValidator.EnsureValid(broken));
        Assert.Equal(problems, ex.Problems);
        Assert.Contains("(and 2 more)", ex.Message, StringComparison.Ordinal);

        CheckResultValidator.EnsureValid(ok);
        Assert.Throws<ArgumentNullException>(() => CheckResultValidator.Validate(null!));
    }

    [Fact]
    public void Every_sample_and_every_registered_id_is_valid()
    {
        foreach (var (name, result) in Samples.All)
        {
            Assert.True(CheckResultValidator.Validate(result).Count == 0, $"{name}: {string.Join(" | ", CheckResultValidator.Validate(result))}");
        }

        foreach (var kind in Enum.GetValues<CheckKind>())
        {
            var result = Samples.EveryId(kind);
            Assert.True(CheckResultValidator.Validate(result).Count == 0, $"{kind}: {string.Join(" | ", CheckResultValidator.Validate(result))}");
        }
    }
}
