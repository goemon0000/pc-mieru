using PcMieru.Checks.SystemInfo;

namespace PcMieru.Checks.Tests.SystemInfo;

/// <summary>
/// The verdicts of the system check, DESIGN-A §14.1 row by row and boundary by boundary. The expected values are the document's, written
/// out: nothing is computed with the code under test.
/// </summary>
public sealed class SystemJudgeTests
{
    private const long Gib = 1024L * 1024 * 1024;

    private static CheckItem Row(IEnumerable<CheckItem> items, string id, string? subject = null) => SI.Row(items, id, subject);

    private static IReadOnlyList<CheckItem> Judge(SystemInputs inputs, DateTime? now = null) => SystemJudge.Judge(inputs, now ?? Hw.Now);

    private static CheckItem Uptime(double seconds) =>
        Row(Judge(SI.Healthy() with { UptimeSeconds = CheckReading<double>.Ok(seconds) }), "system.os.uptime");

    private static CheckItem ConfiguredSpeed(int rated, int configured) =>
        Row(Judge(SI.Healthy() with { Memory = CheckReading<IReadOnlyList<RamModuleInput>>.Ok([SI.Module(rated: rated, configured: configured)]) }), "system.memory.configured_speed", "ram-1");

    private static CheckItem Free(long total, long free, int? disk = 1) =>
        Row(Judge(SI.Healthy() with { Volumes = CheckReading<IReadOnlyList<VolumeInput>>.Ok([SI.Volume(total, free, disk)]) }), "system.storage.free", "volume-1");

    private static IReadOnlyList<CheckItem> WithPageFile(PageFileInput file, bool automatic = true) =>
        Judge(SI.Healthy() with { PageFiles = CheckReading<IReadOnlyList<PageFileInput>>.Ok([file]), PageFileAutomatic = CheckReading<bool>.Ok(automatic) });

    private static IReadOnlyList<CheckItem> WithEvent(string id, long count)
    {
        var events = SI.NoEvents.ToDictionary(static p => p.Key, static p => p.Value, StringComparer.Ordinal);
        events[id] = CheckReading<long>.Ok(count);
        return Judge(SI.Healthy() with { Events = events });
    }

    private static IReadOnlyList<CheckItem> WithGpu(GpuInput gpu, DateTime? now = null) =>
        Judge(SI.Healthy() with { Gpus = CheckReading<IReadOnlyList<GpuInput>>.Ok([gpu]) }, now);

    private static void Missing(CheckItem row, CheckReason reason, ItemVerdict verdict = ItemVerdict.Unavailable)
    {
        Assert.Null(row.Value);
        Assert.Equal(reason, row.Reason);
        Assert.Equal(verdict, row.Verdict);
    }

    // ---- the whole result of a healthy machine ----

    [Fact]
    public void A_healthy_machine_has_every_row_in_the_order_of_the_design_and_passes_the_strict_validator()
    {
        var items = Judge(SI.Healthy());

        Assert.Equal(
            [
                "system.os.version", "system.os.uptime",
                "system.cpu.model", "system.cpu.cores", "system.cpu.logical", "system.cpu.rated_clock",
                "system.memory.modules",
                "system.memory.capacity", "system.memory.rated_speed", "system.memory.configured_speed",
                "system.memory.capacity", "system.memory.rated_speed", "system.memory.configured_speed",
                "system.gpu.adapters", "system.gpu.model", "system.gpu.driver_version", "system.gpu.driver_age", "system.gpu.device_status", "system.gpu.generic_driver",
                "system.storage.devices", "system.storage.health", "system.storage.free",
                "system.pagefile.count", "system.pagefile.automatic", "system.pagefile.allocated", "system.pagefile.current", "system.pagefile.peak",
                "system.eventlog.whea7d", "system.eventlog.whea_warning7d",
                "system.eventlog.disk7d", "system.eventlog.disk_warning7d",
                "system.eventlog.ntfs7d", "system.eventlog.ntfs_warning7d",
                "system.eventlog.stornvme7d", "system.eventlog.stornvme_warning7d",
                "system.eventlog.volmgr7d", "system.eventlog.volmgr_warning7d",
                "system.eventlog.kp41_7d",
            ],
            items.Select(static i => i.Id));
        SI.AssertValid(items);
        Assert.Equal(CheckVerdict.Ok, CheckVerdicts.Overall(items));
        Assert.DoesNotContain(items, static i => i.Verdict is ItemVerdict.Caution or ItemVerdict.Check or ItemVerdict.Unavailable);
    }

    [Fact]
    public void The_subjects_are_numbered_from_one_in_each_family_and_the_rows_of_the_machine_have_none()
    {
        var items = Judge(SI.Healthy());

        Assert.Equal(["ram-1", "ram-2"], items.Where(static i => i.Subject?.Id.StartsWith("ram-", StringComparison.Ordinal) == true).Select(static i => i.Subject!.Id).Distinct());
        Assert.Equal("gpu-1", Row(items, "system.gpu.model", "gpu-1").Subject!.Id);
        Assert.Equal("disk-1", Row(items, "system.storage.health", "disk-1").Subject!.Id);
        Assert.Equal("volume-1", Row(items, "system.storage.free", "volume-1").Subject!.Id);
        Assert.Equal("pagefile-1", Row(items, "system.pagefile.peak", "pagefile-1").Subject!.Id);
        Assert.Null(Row(items, "system.os.uptime").Subject);
        Assert.Null(Row(items, "system.eventlog.whea7d").Subject);
    }

    // ---- OS and CPU ----

    [Fact]
    public void The_os_version_is_information_with_its_parts()
    {
        var row = Row(Judge(SI.Healthy()), "system.os.version");

        Assert.Equal(ItemVerdict.Info, row.Verdict);
        Assert.Equal(CheckSource.Os, row.Source);
        Assert.Equal(new OsVersionValue("Windows 11 Pro", "25H2", 26300, 7171), row.Value!.AsOsVersion);
    }

    [Fact]
    public void An_os_version_that_did_not_come_is_information_without_a_value_and_with_its_reason()
    {
        var row = Row(Judge(SI.Healthy() with { OsVersion = CheckReading<OsVersionValue>.Missing(CheckReason.Denied) }), "system.os.version");

        Missing(row, CheckReason.Denied, ItemVerdict.Info);
    }

    [Fact]
    public void The_uptime_is_a_caution_only_over_thirty_days_and_the_value_is_the_seconds()
    {
        // 30 days = 2,592,000 s. Exactly that is still fine (the old code says "over").
        Assert.Equal(ItemVerdict.Ok, Uptime(0).Verdict);
        Assert.Equal(ItemVerdict.Ok, Uptime(2_591_999).Verdict);
        Assert.Equal(ItemVerdict.Ok, Uptime(2_592_000).Verdict);
        var over = Uptime(2_592_001);
        Assert.Equal(ItemVerdict.Caution, over.Verdict);
        Assert.Equal(CheckValue.Number(2_592_001), over.Value);
        Assert.Equal(CheckSource.Os, over.Source);
    }

    [Fact]
    public void An_uptime_that_did_not_come_is_unavailable_with_its_reason()
    {
        var row = Row(Judge(SI.Healthy() with { UptimeSeconds = CheckReading<double>.TimedOut() }), "system.os.uptime");

        Missing(row, CheckReason.Timeout);
    }

    [Fact]
    public void The_cpu_rows_are_information_and_each_one_that_did_not_come_says_why()
    {
        var items = Judge(SI.Healthy());
        Assert.Equal(CheckValue.Text("AMD Ryzen 9 7950X 16-Core Processor"), Row(items, "system.cpu.model").Value);
        Assert.Equal(CheckValue.Int(16), Row(items, "system.cpu.cores").Value);
        Assert.Equal(CheckValue.Int(32), Row(items, "system.cpu.logical").Value);
        Assert.Equal(CheckValue.Number(4500), Row(items, "system.cpu.rated_clock").Value);
        Assert.All(items.Where(static i => i.Id.StartsWith("system.cpu.", StringComparison.Ordinal)), static i => Assert.Equal(ItemVerdict.Info, i.Verdict));

        var gone = Judge(SI.Healthy() with
        {
            CpuModel = CheckReading<string>.Missing(CheckReason.Unsupported),
            CpuCores = CheckReading<int>.TimedOut(),
            CpuLogical = CheckReading<int>.Failed(),
            CpuRatedMhz = CheckReading<double>.Missing(CheckReason.Denied),
        });
        Missing(Row(gone, "system.cpu.model"), CheckReason.Unsupported, ItemVerdict.Info);
        Missing(Row(gone, "system.cpu.cores"), CheckReason.Timeout, ItemVerdict.Info);
        Missing(Row(gone, "system.cpu.logical"), CheckReason.Error, ItemVerdict.Info);
        Missing(Row(gone, "system.cpu.rated_clock"), CheckReason.Denied, ItemVerdict.Info);
        SI.AssertValid(gone);
    }

    // ---- memory ----

    [Fact]
    public void Modules_of_one_size_are_ok_and_each_module_has_its_three_rows()
    {
        var items = Judge(SI.Healthy());

        var modules = Row(items, "system.memory.modules");
        Assert.Equal(CheckValue.Int(2), modules.Value);
        Assert.Equal(ItemVerdict.Ok, modules.Verdict);
        Assert.Null(modules.Evidence);
        Assert.Equal(CheckValue.Dec((ulong)(32 * Gib)), Row(items, "system.memory.capacity", "ram-2").Value);
        Assert.Equal(ItemVerdict.Info, Row(items, "system.memory.capacity", "ram-2").Verdict);
        Assert.Equal(CheckValue.Int(5600), Row(items, "system.memory.rated_speed", "ram-1").Value);
        Assert.Equal(CheckValue.Int(5600), Row(items, "system.memory.configured_speed", "ram-1").Value);
    }

    [Fact]
    public void Modules_of_different_sizes_are_a_caution_with_every_capacity_as_evidence()
    {
        var memory = CheckReading<IReadOnlyList<RamModuleInput>>.Ok([SI.Module(gib: 16), SI.Module(gib: 32)]);

        var modules = Row(Judge(SI.Healthy() with { Memory = memory }), "system.memory.modules");

        Assert.Equal(ItemVerdict.Caution, modules.Verdict);
        Assert.Equal(CheckValue.Int(2), modules.Value);
        Assert.Equal(CheckEvidenceCode.MixedCapacity, modules.Evidence!.Code);
        Assert.Equal(["17179869184", "34359738368"], modules.Evidence.Capacities!);
    }

    [Fact]
    public void A_module_whose_size_is_not_known_beside_one_that_is_cannot_be_called_not_mixed()
    {
        var unknown = new RamModuleInput(CheckReading<long>.Missing(CheckReason.Denied), CheckReading<int>.Ok(5600), CheckReading<int>.Ok(5600));

        var same = Row(Judge(SI.Healthy() with { Memory = CheckReading<IReadOnlyList<RamModuleInput>>.Ok([SI.Module(), unknown]) }), "system.memory.modules");
        Assert.Equal(ItemVerdict.Unavailable, same.Verdict);
        Assert.Equal(CheckReason.Partial, same.Reason);
        Assert.Equal(CheckValue.Int(2), same.Value);

        // Two known sizes that differ are mixed whatever the third is; the list of capacities is left out because it is not complete.
        var mixed = Row(Judge(SI.Healthy() with { Memory = CheckReading<IReadOnlyList<RamModuleInput>>.Ok([SI.Module(gib: 16), SI.Module(gib: 32), unknown]) }), "system.memory.modules");
        Assert.Equal(ItemVerdict.Caution, mixed.Verdict);
        Assert.Equal(CheckEvidenceCode.MixedCapacity, mixed.Evidence!.Code);
        Assert.Null(mixed.Evidence.Capacities);
        SI.AssertValid(Judge(SI.Healthy() with { Memory = CheckReading<IReadOnlyList<RamModuleInput>>.Ok([SI.Module(), unknown]) }));
    }

    [Fact]
    public void A_list_of_no_module_is_unsupported_and_a_list_that_did_not_come_has_no_module_rows()
    {
        var none = Judge(SI.Healthy() with { Memory = CheckReading<IReadOnlyList<RamModuleInput>>.Ok([]) });
        Missing(Row(none, "system.memory.modules"), CheckReason.Unsupported);
        Assert.DoesNotContain(none, static i => i.Id == "system.memory.capacity");

        var late = Judge(SI.Healthy() with { Memory = CheckReading<IReadOnlyList<RamModuleInput>>.TimedOut() });
        Missing(Row(late, "system.memory.modules"), CheckReason.Timeout);
        Assert.DoesNotContain(late, static i => i.Id.StartsWith("system.memory.", StringComparison.Ordinal) && i.Id != "system.memory.modules");
        SI.AssertValid(none);
        SI.AssertValid(late);
    }

    [Fact]
    public void The_configured_speed_is_ok_at_the_rated_speed_and_a_caution_one_step_below_it()
    {
        Assert.Equal(ItemVerdict.Ok, ConfiguredSpeed(5600, 5600).Verdict);
        Assert.Null(ConfiguredSpeed(5600, 5600).Evidence);

        var below = ConfiguredSpeed(5600, 5599);
        Assert.Equal(ItemVerdict.Caution, below.Verdict);
        Assert.Equal(CheckEvidenceCode.BelowRated, below.Evidence!.Code);
        Assert.Equal(CheckValue.Int(5599), below.Value);

        var typical = ConfiguredSpeed(5600, 4800);
        Assert.Equal(ItemVerdict.Caution, typical.Verdict);
        Assert.Equal(CheckEvidenceCode.BelowRated, typical.Evidence!.Code);
    }

    [Fact]
    public void A_speed_over_the_rated_one_is_ok_and_says_so()
    {
        var above = ConfiguredSpeed(5600, 5601);

        Assert.Equal(ItemVerdict.Ok, above.Verdict);
        Assert.Equal(CheckEvidenceCode.AboveRated, above.Evidence!.Code);
    }

    [Fact]
    public void A_configured_speed_of_zero_or_a_missing_one_cannot_be_judged()
    {
        // The old code called a speed of 0 "ok"; §14.1 says it cannot be measured (the one deliberate change of this row).
        Missing(ConfiguredSpeed(5600, 0), CheckReason.Unsupported);
        Missing(ConfiguredSpeed(0, 4800), CheckReason.Unsupported);

        var module = new RamModuleInput(CheckReading<long>.Ok(32 * Gib), CheckReading<int>.Ok(5600), CheckReading<int>.Missing(CheckReason.Denied));
        var items = Judge(SI.Healthy() with { Memory = CheckReading<IReadOnlyList<RamModuleInput>>.Ok([module]) });
        Missing(Row(items, "system.memory.configured_speed", "ram-1"), CheckReason.Denied);
        Assert.Equal(CheckValue.Int(5600), Row(items, "system.memory.rated_speed", "ram-1").Value);

        var noRated = new RamModuleInput(CheckReading<long>.Ok(32 * Gib), CheckReading<int>.TimedOut(), CheckReading<int>.Ok(4800));
        var second = Judge(SI.Healthy() with { Memory = CheckReading<IReadOnlyList<RamModuleInput>>.Ok([noRated]) });
        Missing(Row(second, "system.memory.configured_speed", "ram-1"), CheckReason.Timeout);
        Missing(Row(second, "system.memory.rated_speed", "ram-1"), CheckReason.Timeout, ItemVerdict.Info);
        SI.AssertValid(items);
        SI.AssertValid(second);
    }

    [Fact]
    public void A_rated_speed_of_zero_is_shown_as_unsupported_not_as_a_number()
    {
        var items = Judge(SI.Healthy() with { Memory = CheckReading<IReadOnlyList<RamModuleInput>>.Ok([SI.Module(rated: 0, configured: 4800)]) });

        Missing(Row(items, "system.memory.rated_speed", "ram-1"), CheckReason.Unsupported, ItemVerdict.Info);
        SI.AssertValid(items);
    }

    // ---- GPUs ----

    [Fact]
    public void A_gpu_has_its_five_rows_and_a_healthy_one_is_ok()
    {
        var items = WithGpu(SI.Gpu());

        Assert.Equal(CheckValue.Int(1), Row(items, "system.gpu.adapters").Value);
        Assert.Equal("NVIDIA GeForce RTX 5070", Row(items, "system.gpu.model", "gpu-1").Subject!.Model);
        Assert.Equal(CheckValue.Text("32.0.15.9999"), Row(items, "system.gpu.driver_version", "gpu-1").Value);
        Assert.Equal(ItemVerdict.Ok, Row(items, "system.gpu.driver_age", "gpu-1").Verdict);
        Assert.Equal(ItemVerdict.Ok, Row(items, "system.gpu.device_status", "gpu-1").Verdict);
        Assert.Equal(CheckValue.Bool(false), Row(items, "system.gpu.generic_driver", "gpu-1").Value);
        Assert.Equal(ItemVerdict.Ok, Row(items, "system.gpu.generic_driver", "gpu-1").Verdict);
    }

    [Fact]
    public void The_driver_age_is_a_caution_only_when_it_is_over_730_days_counted_with_fractions()
    {
        // Now is at midnight, the date is 730 days back: exactly 730.0 is fine, one minute more is over.
        var midnight = new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
        var date = new DateOnly(2024, 11, 21);

        var exact = Row(WithGpu(SI.Gpu(driverDate: date), midnight), "system.gpu.driver_age", "gpu-1");
        Assert.Equal(CheckValue.Int(730), exact.Value);
        Assert.Equal(ItemVerdict.Ok, exact.Verdict);

        var over = Row(WithGpu(SI.Gpu(driverDate: date), midnight.AddMinutes(1)), "system.gpu.driver_age", "gpu-1");
        Assert.Equal(CheckValue.Int(730), over.Value); // the value is the whole days; the verdict is of the fractional ones
        Assert.Equal(ItemVerdict.Caution, over.Verdict);

        var young = Row(WithGpu(SI.Gpu(driverDate: date.AddDays(1)), midnight.AddMinutes(1)), "system.gpu.driver_age", "gpu-1");
        Assert.Equal(CheckValue.Int(729), young.Value);
        Assert.Equal(ItemVerdict.Ok, young.Verdict);
    }

    [Fact]
    public void A_driver_dated_in_the_future_is_zero_days_old()
    {
        var row = Row(WithGpu(SI.Gpu(driverDate: new DateOnly(2027, 1, 1))), "system.gpu.driver_age", "gpu-1");

        Assert.Equal(CheckValue.Int(0), row.Value);
        Assert.Equal(ItemVerdict.Ok, row.Verdict);
    }

    [Fact]
    public void A_driver_date_that_did_not_come_is_unavailable_with_its_reason()
    {
        var gpu = SI.Gpu() with { DriverDate = CheckReading<DateOnly>.Failed() };

        var row = Row(WithGpu(gpu), "system.gpu.driver_age", "gpu-1");

        Missing(row, CheckReason.Error);
    }

    [Theory]
    [InlineData(0, ItemVerdict.Ok, null)]
    [InlineData(22, ItemVerdict.Ok, CheckEvidenceCode.DisabledCode22)]
    [InlineData(43, ItemVerdict.Check, CheckEvidenceCode.DeviceError)]
    [InlineData(10, ItemVerdict.Check, CheckEvidenceCode.DeviceError)]
    [InlineData(21, ItemVerdict.Check, CheckEvidenceCode.DeviceError)]
    [InlineData(23, ItemVerdict.Check, CheckEvidenceCode.DeviceError)]
    public void The_device_status_follows_the_codes_of_the_design(int code, ItemVerdict verdict, CheckEvidenceCode? evidence)
    {
        var row = Row(WithGpu(SI.Gpu(status: code)), "system.gpu.device_status", "gpu-1");

        Assert.Equal(CheckValue.Int(code), row.Value);
        Assert.Equal(verdict, row.Verdict);
        Assert.Equal(evidence, row.Evidence?.Code);
        Assert.Equal(CheckSource.Wmi, row.Source);
    }

    [Fact]
    public void A_device_status_that_did_not_come_is_unavailable_and_the_driver_rows_stay_judged()
    {
        var gpu = SI.Gpu(name: "Microsoft Basic Display Adapter") with { DeviceStatus = CheckReading<int>.Missing(CheckReason.Denied) };

        var items = WithGpu(gpu);

        Missing(Row(items, "system.gpu.device_status", "gpu-1"), CheckReason.Denied);
        // An unreadable status is not "not 0": the generic driver is judged all the same.
        Assert.Equal(ItemVerdict.Caution, Row(items, "system.gpu.generic_driver", "gpu-1").Verdict);
        Assert.Equal(ItemVerdict.Ok, Row(items, "system.gpu.driver_age", "gpu-1").Verdict);
        SI.AssertValid(items);
    }

    [Theory]
    [InlineData(22)]
    [InlineData(43)]
    public void The_two_driver_rows_are_information_exactly_when_the_device_status_is_not_zero(int code)
    {
        // A generic name and an old driver would be cautions, but a device with an error code has no driver to judge (DESIGN-A 14.0).
        var gpu = SI.Gpu(name: "Microsoft Basic Display Adapter", driverDate: new DateOnly(2015, 1, 1), status: code);

        var items = WithGpu(gpu);

        Assert.Equal(ItemVerdict.Info, Row(items, "system.gpu.driver_age", "gpu-1").Verdict);
        Assert.Equal(ItemVerdict.Info, Row(items, "system.gpu.generic_driver", "gpu-1").Verdict);
        Assert.Equal(CheckValue.Bool(true), Row(items, "system.gpu.generic_driver", "gpu-1").Value);
        SI.AssertValid(items);
    }

    [Fact]
    public void With_a_status_of_zero_the_driver_rows_are_judged()
    {
        var gpu = SI.Gpu(name: "Microsoft Basic Display Adapter", driverDate: new DateOnly(2015, 1, 1), status: 0);

        var items = WithGpu(gpu);

        Assert.Equal(ItemVerdict.Caution, Row(items, "system.gpu.driver_age", "gpu-1").Verdict);
        Assert.Equal(ItemVerdict.Caution, Row(items, "system.gpu.generic_driver", "gpu-1").Verdict);
    }

    [Theory]
    [InlineData("Microsoft Basic Display Adapter", true)]
    [InlineData("MICROSOFT BASIC DISPLAY ADAPTER", true)]
    [InlineData("microsoft basic display adapter", true)]
    [InlineData("Microsoft Basic Display Driver", true)]
    [InlineData("基本ディスプレイ アダプター", true)]
    [InlineData("Microsoft 基本ディスプレイ アダプター", true)]
    [InlineData("NVIDIA GeForce RTX 5070", false)]
    [InlineData("Basic Render Driver", false)]
    public void A_generic_driver_is_told_by_its_name_in_english_or_japanese_ignoring_case(string name, bool generic)
    {
        var row = Row(WithGpu(SI.Gpu(name: name)), "system.gpu.generic_driver", "gpu-1");

        Assert.Equal(CheckValue.Bool(generic), row.Value);
        Assert.Equal(generic ? ItemVerdict.Caution : ItemVerdict.Ok, row.Verdict);
    }

    [Fact]
    public void A_gpu_name_that_did_not_come_leaves_the_generic_driver_unavailable()
    {
        var gpu = SI.Gpu() with { Name = CheckReading<string>.Missing(CheckReason.Unsupported) };

        var items = WithGpu(gpu);

        Missing(Row(items, "system.gpu.generic_driver", "gpu-1"), CheckReason.Unsupported);
        Missing(Row(items, "system.gpu.model", "gpu-1"), CheckReason.Unsupported, ItemVerdict.Info);
        Assert.Null(Row(items, "system.gpu.model", "gpu-1").Subject!.Model);
    }

    [Fact]
    public void Two_gpus_are_numbered_in_order_and_no_gpu_is_a_count_of_zero()
    {
        var two = Judge(SI.Healthy() with { Gpus = CheckReading<IReadOnlyList<GpuInput>>.Ok([SI.Gpu(), SI.Gpu(name: "AMD Radeon Graphics")]) });
        Assert.Equal(CheckValue.Int(2), Row(two, "system.gpu.adapters").Value);
        Assert.Equal("AMD Radeon Graphics", Row(two, "system.gpu.model", "gpu-2").Subject!.Model);

        var none = Judge(SI.Healthy() with { Gpus = CheckReading<IReadOnlyList<GpuInput>>.Ok([]) });
        Assert.Equal(CheckValue.Int(0), Row(none, "system.gpu.adapters").Value);
        Assert.DoesNotContain(none, static i => i.Id == "system.gpu.model");
        SI.AssertValid(two);
        SI.AssertValid(none);
    }

    [Fact]
    public void A_gpu_list_that_did_not_come_has_no_gpu_rows()
    {
        var items = Judge(SI.Healthy() with { Gpus = CheckReading<IReadOnlyList<GpuInput>>.Missing(CheckReason.Denied) });

        Missing(Row(items, "system.gpu.adapters"), CheckReason.Denied, ItemVerdict.Info);
        Assert.DoesNotContain(items, static i => i.Id.StartsWith("system.gpu.", StringComparison.Ordinal) && i.Id != "system.gpu.adapters");
        SI.AssertValid(items);
    }

    // ---- disks and volumes ----

    [Fact]
    public void The_windows_health_of_a_disk_is_one_row_with_the_verdict_of_its_value()
    {
        DiskInput[] disks =
        [
            new("Healthy Model", DiskHealthVerdict.Healthy),
            new("Warning Model", DiskHealthVerdict.Warning),
            new("Critical Model", DiskHealthVerdict.Critical),
            new("Denied Model", DiskHealthVerdict.Unavailable(CheckReason.Denied)),
        ];

        var items = Judge(SI.Healthy() with { Disks = CheckReading<IReadOnlyList<DiskInput>>.Ok(disks) });

        Assert.Equal(CheckValue.Int(4), Row(items, "system.storage.devices").Value);
        var healthy = Row(items, "system.storage.health", "disk-1");
        Assert.Equal(CheckValue.Text("healthy"), healthy.Value);
        Assert.Equal(ItemVerdict.Ok, healthy.Verdict);
        Assert.Equal(CheckSource.Wmi, healthy.Source);
        Assert.Equal("Healthy Model", healthy.Subject!.Model);
        var warning = Row(items, "system.storage.health", "disk-2");
        Assert.Equal(CheckValue.Text("warning"), warning.Value);
        Assert.Equal(ItemVerdict.Caution, warning.Verdict);
        var critical = Row(items, "system.storage.health", "disk-3");
        Assert.Equal(CheckValue.Text("critical"), critical.Value);
        Assert.Equal(ItemVerdict.Check, critical.Verdict);
        Missing(Row(items, "system.storage.health", "disk-4"), CheckReason.Denied);
        SI.AssertValid(items);
        Assert.Equal(CheckVerdict.Check, CheckVerdicts.Overall(items));
    }

    [Fact]
    public void A_disk_with_a_blank_model_has_no_model_in_its_subject()
    {
        var items = Judge(SI.Healthy() with { Disks = CheckReading<IReadOnlyList<DiskInput>>.Ok([new DiskInput("   ", DiskHealthVerdict.Healthy), new DiskInput(null, DiskHealthVerdict.Healthy)]) });

        Assert.Null(Row(items, "system.storage.health", "disk-1").Subject!.Model);
        Assert.Null(Row(items, "system.storage.health", "disk-2").Subject!.Model);
        SI.AssertValid(items);
    }

    [Fact]
    public void A_slot_that_could_not_be_looked_at_makes_the_count_partial_and_still_has_its_health_row()
    {
        var items = Judge(SI.Healthy() with
        {
            Disks = CheckReading<IReadOnlyList<DiskInput>>.Ok([new DiskInput("Model", DiskHealthVerdict.Healthy), new DiskInput(null, DiskHealthVerdict.Healthy, Unreadable: true)]),
        });

        var devices = Row(items, "system.storage.devices");
        Assert.Equal(CheckValue.Int(2), devices.Value);
        Assert.Equal(CheckReason.Partial, devices.Reason);
        Assert.Equal(ItemVerdict.Info, devices.Verdict);
        Assert.Equal(2, items.Count(static i => i.Id == "system.storage.health"));
        SI.AssertValid(items);
    }

    [Fact]
    public void A_disk_list_that_did_not_come_has_no_disk_rows()
    {
        var items = Judge(SI.Healthy() with { Disks = CheckReading<IReadOnlyList<DiskInput>>.TimedOut(), Volumes = CheckReading<IReadOnlyList<VolumeInput>>.TimedOut() });

        Missing(Row(items, "system.storage.devices"), CheckReason.Timeout, ItemVerdict.Info);
        Assert.DoesNotContain(items, static i => i.Id is "system.storage.health" or "system.storage.free");
        SI.AssertValid(items);
    }

    [Theory]
    [InlineData(1000, 1000, ItemVerdict.Ok)]
    [InlineData(1000, 100, ItemVerdict.Ok)] // 10.0 % is not under 10 %
    [InlineData(1000, 99, ItemVerdict.Caution)]
    [InlineData(1000, 30, ItemVerdict.Caution)] // 3.0 % is not under 3 %
    [InlineData(1000, 29, ItemVerdict.Check)]
    [InlineData(1000, 0, ItemVerdict.Check)]
    [InlineData(0, 0, ItemVerdict.Ok)] // a volume of size 0 has the ratio 1
    public void The_free_space_is_a_check_under_three_percent_and_a_caution_under_ten(long total, long free, ItemVerdict verdict)
    {
        var row = Free(total, free);

        Assert.Equal(verdict, row.Verdict);
        Assert.Equal(CheckValue.Dec((ulong)free), row.Value); // the value is the free bytes
    }

    [Fact]
    public void The_free_space_row_says_the_total_and_the_disk_it_lives_on()
    {
        var row = Free(2_000_000_000_000, 1_000_000_000_000, disk: 2);

        Assert.Equal("2000000000000", row.Evidence!.TotalBytes);
        Assert.Equal("disk-2", row.Evidence.DiskId);
        Assert.Equal(CheckSource.Win32, row.Source);
        Assert.Null(Free(1000, 500, disk: null).Evidence!.DiskId); // a volume on several disks says none
    }

    [Fact]
    public void A_volume_whose_free_bytes_did_not_come_is_unavailable_and_one_whose_total_did_not_come_keeps_the_free_bytes_as_partial()
    {
        var noFree = Row(
            Judge(SI.Healthy() with { Volumes = CheckReading<IReadOnlyList<VolumeInput>>.Ok([new VolumeInput(CheckReading<long>.Ok(1000), CheckReading<long>.Missing(CheckReason.Denied), 1)]) }),
            "system.storage.free",
            "volume-1");
        Missing(noFree, CheckReason.Denied);

        var items = Judge(SI.Healthy() with { Volumes = CheckReading<IReadOnlyList<VolumeInput>>.Ok([new VolumeInput(CheckReading<long>.TimedOut(), CheckReading<long>.Ok(500), 1)]) });
        var noTotal = Row(items, "system.storage.free", "volume-1");
        Assert.Equal(CheckValue.Dec(500UL), noTotal.Value);
        Assert.Equal(ItemVerdict.Unavailable, noTotal.Verdict);
        Assert.Equal(CheckReason.Partial, noTotal.Reason);
        SI.AssertValid(items);
    }

    // ---- page files ----

    [Fact]
    public void No_page_file_is_a_caution_and_one_page_file_is_ok()
    {
        var none = Judge(SI.Healthy() with { PageFiles = CheckReading<IReadOnlyList<PageFileInput>>.Ok([]) });
        var count = Row(none, "system.pagefile.count");
        Assert.Equal(CheckValue.Int(0), count.Value);
        Assert.Equal(ItemVerdict.Caution, count.Verdict);
        Assert.DoesNotContain(none, static i => i.Id == "system.pagefile.peak");
        // With no page file at all the machine-wide automatic row is still there, once, with no subject (DESIGN-A 3.1).
        var automatic = Row(none, "system.pagefile.automatic");
        Assert.Null(automatic.Subject);
        Assert.Equal(CheckValue.Bool(true), automatic.Value);
        Assert.Equal(ItemVerdict.Info, automatic.Verdict);

        var one = Judge(SI.Healthy());
        Assert.Equal(CheckValue.Int(1), Row(one, "system.pagefile.count").Value);
        Assert.Equal(ItemVerdict.Ok, Row(one, "system.pagefile.count").Verdict);
        SI.AssertValid(none);
    }

    [Theory]
    [InlineData(1000, 0, ItemVerdict.Ok)]
    [InlineData(1000, 800, ItemVerdict.Ok)] // 80 % is not over 80 %
    [InlineData(1000, 801, ItemVerdict.Caution)]
    [InlineData(1000, 1000, ItemVerdict.Caution)]
    [InlineData(0, 500, ItemVerdict.Ok)] // a file with no allocation is not judged
    public void The_peak_is_a_caution_only_over_eighty_percent_of_the_allocation(long allocatedMb, long peakMb, ItemVerdict verdict)
    {
        var items = WithPageFile(new PageFileInput(allocatedMb, 10, peakMb));

        var peak = Row(items, "system.pagefile.peak", "pagefile-1");
        Assert.Equal(verdict, peak.Verdict);
        Assert.Equal(CheckValue.Dec((ulong)peakMb * 1048576UL), peak.Value);
    }

    [Fact]
    public void The_sizes_are_in_bytes_and_the_automatic_row_is_one_machine_wide_row_with_no_subject()
    {
        var items = WithPageFile(new PageFileInput(4096, 100, 500), automatic: false);

        Assert.Equal(CheckValue.Dec(4096UL * 1048576UL), Row(items, "system.pagefile.allocated", "pagefile-1").Value);
        Assert.Equal(CheckValue.Dec(100UL * 1048576UL), Row(items, "system.pagefile.current", "pagefile-1").Value);
        var automatic = Row(items, "system.pagefile.automatic");
        Assert.Null(automatic.Subject);
        Assert.Equal(CheckValue.Bool(false), automatic.Value);
        Assert.Equal(ItemVerdict.Info, automatic.Verdict);
        Assert.Single(items, static i => i.Id == "system.pagefile.automatic");
        Assert.Equal(ItemVerdict.Info, Row(items, "system.pagefile.allocated", "pagefile-1").Verdict);
    }

    [Fact]
    public void A_size_that_did_not_come_is_information_without_a_value_and_a_peak_without_an_allocation_is_partial()
    {
        var items = WithPageFile(new PageFileInput(null, null, 500));

        Missing(Row(items, "system.pagefile.allocated", "pagefile-1"), CheckReason.Error, ItemVerdict.Info);
        Missing(Row(items, "system.pagefile.current", "pagefile-1"), CheckReason.Error, ItemVerdict.Info);
        var peak = Row(items, "system.pagefile.peak", "pagefile-1");
        Assert.Equal(CheckValue.Dec(500UL * 1048576UL), peak.Value);
        Assert.Equal(ItemVerdict.Unavailable, peak.Verdict);
        Assert.Equal(CheckReason.Partial, peak.Reason);

        var noPeak = WithPageFile(new PageFileInput(1000, 10, null));
        Missing(Row(noPeak, "system.pagefile.peak", "pagefile-1"), CheckReason.Error);
        SI.AssertValid(items);
        SI.AssertValid(noPeak);
    }

    [Fact]
    public void A_page_file_list_that_did_not_come_is_one_unavailable_count_and_no_file_rows()
    {
        var items = Judge(SI.Healthy() with { PageFiles = CheckReading<IReadOnlyList<PageFileInput>>.Missing(CheckReason.Denied) });

        Missing(Row(items, "system.pagefile.count"), CheckReason.Denied);
        Assert.DoesNotContain(items, static i => i.Id.StartsWith("system.pagefile.", StringComparison.Ordinal) && i.Id != "system.pagefile.count" && i.Id != "system.pagefile.automatic");
        Assert.Null(Row(items, "system.pagefile.automatic").Subject); // the flag comes from another class, so it is there whether the list came or not
        SI.AssertValid(items);
    }

    [Fact]
    public void The_automatic_flag_that_did_not_come_is_one_information_row_with_its_reason()
    {
        var items = Judge(SI.Healthy() with
        {
            PageFiles = CheckReading<IReadOnlyList<PageFileInput>>.Ok([new PageFileInput(1, 1, 1), new PageFileInput(1, 1, 1)]),
            PageFileAutomatic = CheckReading<bool>.Missing(CheckReason.Denied),
        });

        var automatic = Row(items, "system.pagefile.automatic");
        Missing(automatic, CheckReason.Denied, ItemVerdict.Info);
        Assert.Null(automatic.Subject);
        Assert.Single(items, static i => i.Id == "system.pagefile.automatic");
        SI.AssertValid(items);
    }

    // ---- the event log ----

    [Theory]
    [InlineData(0, ItemVerdict.Ok)]
    [InlineData(1, ItemVerdict.Check)]
    [InlineData(5, ItemVerdict.Check)]
    public void A_whea_error_is_a_check(int count, ItemVerdict verdict)
    {
        Assert.Equal(verdict, Row(WithEvent("system.eventlog.whea7d", count), "system.eventlog.whea7d").Verdict);
    }

    [Theory]
    [InlineData("system.eventlog.whea_warning7d")]
    [InlineData("system.eventlog.kp41_7d")]
    public void A_whea_warning_and_a_kernel_power_41_are_a_caution_from_the_first(string id)
    {
        Assert.Equal(ItemVerdict.Ok, Row(WithEvent(id, 0), id).Verdict);
        Assert.Equal(ItemVerdict.Caution, Row(WithEvent(id, 1), id).Verdict);
        Assert.Equal(ItemVerdict.Caution, Row(WithEvent(id, 50), id).Verdict);
    }

    [Theory]
    [InlineData("system.eventlog.disk7d")]
    [InlineData("system.eventlog.ntfs7d")]
    [InlineData("system.eventlog.stornvme7d")]
    [InlineData("system.eventlog.volmgr7d")]
    public void The_storage_logs_are_a_caution_from_one_error_and_a_check_from_ten(string id)
    {
        Assert.Equal(ItemVerdict.Ok, Row(WithEvent(id, 0), id).Verdict);
        Assert.Equal(ItemVerdict.Caution, Row(WithEvent(id, 1), id).Verdict);
        Assert.Equal(ItemVerdict.Caution, Row(WithEvent(id, 9), id).Verdict);
        Assert.Equal(ItemVerdict.Check, Row(WithEvent(id, 10), id).Verdict);
        Assert.Equal(ItemVerdict.Check, Row(WithEvent(id, 11), id).Verdict);
        Assert.Equal(CheckValue.Int(9), Row(WithEvent(id, 9), id).Value);
        Assert.Equal(CheckSource.Eventlog, Row(WithEvent(id, 9), id).Source);
    }

    [Theory]
    [InlineData("system.eventlog.disk_warning7d")]
    [InlineData("system.eventlog.ntfs_warning7d")]
    [InlineData("system.eventlog.stornvme_warning7d")]
    [InlineData("system.eventlog.volmgr_warning7d")]
    public void The_warning_counts_of_the_storage_logs_only_show_a_count(string id)
    {
        foreach (var count in new long[] { 0, 1, 10, 5000 })
        {
            var row = Row(WithEvent(id, count), id);
            Assert.Equal(ItemVerdict.Info, row.Verdict);
            Assert.Equal(CheckValue.Int(count), row.Value);
        }
    }

    [Fact]
    public void A_count_that_did_not_come_is_unavailable_for_a_judged_row_and_information_for_the_others()
    {
        var events = SI.NoEvents.ToDictionary(static p => p.Key, static p => p.Value, StringComparer.Ordinal);
        events["system.eventlog.whea7d"] = CheckReading<long>.TimedOut();
        events["system.eventlog.disk_warning7d"] = CheckReading<long>.TimedOut();
        events["system.eventlog.kp41_7d"] = CheckReading<long>.Missing(CheckReason.Denied);
        events.Remove("system.eventlog.ntfs7d"); // never asked at all

        var items = Judge(SI.Healthy() with { Events = events });

        Missing(Row(items, "system.eventlog.whea7d"), CheckReason.Timeout);
        Missing(Row(items, "system.eventlog.disk_warning7d"), CheckReason.Timeout, ItemVerdict.Info);
        Missing(Row(items, "system.eventlog.kp41_7d"), CheckReason.Denied);
        Missing(Row(items, "system.eventlog.ntfs7d"), CheckReason.Error);
        Assert.Equal(ItemVerdict.Ok, Row(items, "system.eventlog.disk7d").Verdict);
        SI.AssertValid(items);
        Assert.Equal(CheckVerdict.Unavailable, CheckVerdicts.Overall(items));
    }

    [Fact]
    public void A_count_below_zero_is_not_a_count()
    {
        var items = WithEvent("system.eventlog.disk7d", -1);

        Missing(Row(items, "system.eventlog.disk7d"), CheckReason.Error);
    }

    // ---- the overall verdict (DESIGN-A 4.0) ----

    [Fact]
    public void The_overall_verdict_is_the_worst_of_the_judged_rows_in_the_order_check_caution_unavailable_ok()
    {
        var timedOut = SI.Healthy() with { UptimeSeconds = CheckReading<double>.TimedOut() };
        var disk3 = SI.NoEvents.ToDictionary(static p => p.Key, static p => p.Value, StringComparer.Ordinal);
        disk3["system.eventlog.disk7d"] = CheckReading<long>.Ok(3);

        Assert.Equal(CheckVerdict.Ok, CheckVerdicts.Overall(Judge(SI.Healthy())));
        Assert.Equal(CheckVerdict.Caution, CheckVerdicts.Overall(Judge(SI.Healthy() with { UptimeSeconds = CheckReading<double>.Ok(3_000_000) })));
        Assert.Equal(CheckVerdict.Unavailable, CheckVerdicts.Overall(Judge(timedOut)));
        Assert.Equal(CheckVerdict.Caution, CheckVerdicts.Overall(Judge(timedOut with { Events = disk3 }))); // caution beats unavailable
        Assert.Equal(CheckVerdict.Check, CheckVerdicts.Overall(Judge(SI.Healthy() with { Gpus = CheckReading<IReadOnlyList<GpuInput>>.Ok([SI.Gpu(status: 43)]) })));
    }

    [Fact]
    public void Rows_that_only_show_information_never_move_the_overall_verdict()
    {
        var gone = SI.Healthy() with
        {
            OsVersion = CheckReading<OsVersionValue>.Failed(),
            CpuModel = CheckReading<string>.TimedOut(),
            PageFileAutomatic = CheckReading<bool>.Failed(),
        };

        Assert.Equal(CheckVerdict.Ok, CheckVerdicts.Overall(Judge(gone)));
    }
}
