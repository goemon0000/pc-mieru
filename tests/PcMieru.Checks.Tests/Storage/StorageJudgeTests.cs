using PcMieru.Checks.Storage;
using PcMieru.Checks.SystemInfo;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;
using static PcMieru.Checks.Tests.Storage.J;

namespace PcMieru.Checks.Tests.Storage;

/// <summary>DESIGN-A §14.4, row by row and boundary by boundary. Expected verdicts are the document's, written out.</summary>
public sealed class StorageJudgeTests
{
    private static NvmeHealthDetails Details(long? warningMinutes = null, long? criticalMinutes = null, ulong? errorLog = null) =>
        new(
            Value.NotCollected<U128>("t"),
            Value.NotCollected<U128>("t"),
            Value.NotCollected<U128>("t"),
            errorLog is { } entries ? Wide(entries) : Value.NotCollected<U128>("t"),
            warningMinutes is { } w ? Ok(w) : Value.NotCollected<long>("t"),
            criticalMinutes is { } c ? Ok(c) : Value.NotCollected<long>("t"),
            Value.NotCollected<IReadOnlyList<double>>("t"),
            Value.NotCollected<IReadOnlyList<string>>("t"));

    // ---- the shape of a disk's rows ----

    [Fact]
    public void A_healthy_nvme_disk_has_the_rows_of_the_table_in_order()
    {
        var rows = Judged(Nvme(), First(), Subject);

        Assert.Equal(
            [
                "storage.health", "storage.capacity", "storage.temperature", "storage.nvme.critical_warning", "storage.nvme.available_spare",
                "storage.nvme.spare_threshold", "storage.nvme.percentage_used", "storage.nvme.media_errors", "storage.nvme.error_log_entries",
                "storage.nvme.unsafe_shutdowns", "storage.nvme.power_cycles", "storage.nvme.power_on_hours", "storage.nvme.data_units_written",
            ],
            rows.Select(static r => r.Id));
        AssertValid(rows);
        Assert.All(rows, static r => Assert.Equal(Subject, r.Subject));
        Assert.Equal(CheckVerdict.Ok, Overall(rows));
    }

    [Fact]
    public void A_healthy_sata_disk_has_the_rows_of_the_table_and_none_for_what_it_does_not_report()
    {
        var rows = Judged(HealthyAta(), First(), Subject);

        // No predict_failure (the window was not read), no 187/196/198 (the drive does not list them).
        Assert.Equal(
            ["storage.health", "storage.capacity", "storage.temperature", "storage.ata.threshold_status", "storage.ata.5", "storage.ata.197", "storage.ata.199", "storage.ata.9"],
            rows.Select(static r => r.Id));
        AssertValid(rows);
        Assert.Equal(CheckVerdict.Ok, Overall(rows));
    }

    [Fact]
    public void The_sources_are_the_ones_of_the_table()
    {
        var nvme = Rows(Nvme());
        Assert.Equal(CheckSource.StorageQuery, nvme["storage.capacity"].Source);
        Assert.Equal(CheckSource.Nvme, nvme["storage.temperature"].Source);
        Assert.Equal(CheckSource.Nvme, nvme["storage.nvme.media_errors"].Source);

        var ata = Rows(HealthyAta());
        Assert.Equal(CheckSource.Ata, ata["storage.temperature"].Source);
        Assert.Equal(CheckSource.Ata, ata["storage.ata.5"].Source);

        var delta = Rows(Nvme(media: 2), Compared(Counter("nvme.media_errors", 1, 2)));
        Assert.Equal(CheckSource.Baseline, delta["storage.nvme.media_errors.delta"].Source);
    }

    [Fact]
    public void The_delta_rows_come_last_and_are_the_baselines()
    {
        var rows = Judged(Nvme(media: 2), Compared(Counter("nvme.media_errors", 1, 2), Counter("nvme.power_on_hours", 99, 100)), Subject);

        Assert.Equal(["storage.nvme.media_errors.delta", "storage.nvme.power_on_hours.delta"], rows.Skip(rows.Count - 2).Select(static r => r.Id));
        AssertValid(rows);
    }

    [Fact]
    public void The_subject_is_the_disk_number_and_the_model_as_read_never_a_serial()
    {
        var drive = Nvme();
        var subject = StorageJudge.SubjectOf(drive, "disk-3");

        Assert.Equal("disk-3", subject.Id);
        Assert.Equal(drive.Model, subject.Model);
        Assert.DoesNotContain(drive.SerialNumber, subject.Model!, StringComparison.Ordinal);
        Assert.Null(StorageJudge.SubjectOf(Drives.Unreadable(), "disk-4").Model);
        Assert.Equal("Foo SSD", StorageJudge.SubjectOf(Nvme() with { Model = "  Foo SSD " }, "disk-5").Model);
    }

    [Fact]
    public void Capacity_is_the_size_in_bytes_and_an_observation()
    {
        var capacity = Rows(Nvme())["storage.capacity"];

        IsDec(2000398934016UL, capacity);
        Is(ItemVerdict.Info, capacity);
        Assert.Equal("B", capacity.Unit);

        var unknown = Rows(Nvme() with { SizeBytes = 0 })["storage.capacity"];
        IsMissing(CheckReason.Error, unknown, ItemVerdict.Info);
    }

    // ---- storage.temperature, NVMe ----

    // The value and the verdict are the page's whole Kelvin minus 273 (v0.7.4, DESIGN-A §14.4), so 343 K is 70 and a caution, though the
    // screens show 69.85 for it (Kelvin minus 273.15).
    [Theory]
    [InlineData(313, 40, ItemVerdict.Ok)]
    [InlineData(341, 68, ItemVerdict.Ok)]
    [InlineData(342, 69, ItemVerdict.Ok)]
    [InlineData(343, 70, ItemVerdict.Caution)]
    [InlineData(344, 71, ItemVerdict.Caution)]
    [InlineData(352, 79, ItemVerdict.Caution)]
    [InlineData(353, 80, ItemVerdict.Check)]
    [InlineData(354, 81, ItemVerdict.Check)]
    [InlineData(368, 95, ItemVerdict.Check)]
    public void Nvme_temperature_boundaries_are_the_kelvin_minus_273(int kelvin, int degrees, ItemVerdict expected)
    {
        var row = Rows(Nvme(kelvin: kelvin))["storage.temperature"];

        Is(expected, row);
        IsNumber(degrees, row);
        Assert.Equal("°C", row.Unit);
        Assert.Null(row.Evidence);
    }

    [Fact]
    public void The_nvme_temperature_is_not_judged_from_the_rounded_celsius_the_screens_show()
    {
        // 343 K shows as 69.85 (Core: kelvin - 273.15) and is 70 here: a caution. Judging the shown value would be one step too low.
        var drive = Nvme(kelvin: 343);

        Assert.Equal(69.85, drive.Health.Data!.TemperatureC.Data);
        Is(ItemVerdict.Caution, Rows(drive)["storage.temperature"]);

        var hot = Nvme(kelvin: 353);
        Assert.Equal(79.85, hot.Health.Data!.TemperatureC.Data);
        Is(ItemVerdict.Check, Rows(hot)["storage.temperature"]);
    }

    [Fact]
    public void A_nvme_temperature_without_the_raw_kelvin_is_an_error_not_a_guess_from_the_rounded_value()
    {
        var withoutCounters = Nvme() with { Counters = null };
        IsMissing(CheckReason.Error, Rows(withoutCounters)["storage.temperature"]);

        var noKelvin = Nvme().EditCounters(static c => c with { NvmeCompositeKelvin = null });
        IsMissing(CheckReason.Error, Rows(noKelvin)["storage.temperature"]);

        var zero = Nvme().EditCounters(static c => c with { NvmeCompositeKelvin = 0 });
        IsMissing(CheckReason.Error, Rows(zero)["storage.temperature"]);
    }

    [Fact]
    public void Nvme_temperature_that_cannot_be_read_is_unavailable_whatever_the_minutes_say()
    {
        var drive = Nvme().Edit(h => h with { TemperatureC = Value.Denied<double>("t"), Nvme = Details(warningMinutes: 30, criticalMinutes: 12) });

        var row = Rows(drive)["storage.temperature"];

        IsMissing(CheckReason.Denied, row);
        Assert.Null(row.Evidence);
    }

    [Theory]
    [InlineData(ValueStatus.Unsupported, CheckReason.Unsupported)]
    [InlineData(ValueStatus.Denied, CheckReason.Denied)]
    [InlineData(ValueStatus.Timeout, CheckReason.Timeout)]
    [InlineData(ValueStatus.Error, CheckReason.Error)]
    [InlineData(ValueStatus.Gone, CheckReason.Error)]
    [InlineData(ValueStatus.NotCollected, CheckReason.Error)]
    public void A_missing_reading_carries_the_reason_of_its_status(ValueStatus status, CheckReason expected)
    {
        var drive = Nvme().Edit(h => h with { TemperatureC = Value.Missing<double>(status, "t") });

        IsMissing(expected, Rows(drive)["storage.temperature"]);
    }

    [Fact]
    public void Time_over_the_critical_temperature_makes_an_ok_temperature_a_caution_with_the_minutes()
    {
        var drive = Nvme(temperature: 40).Edit(h => h with { Nvme = Details(warningMinutes: 30, criticalMinutes: 12) });

        var row = Rows(drive)["storage.temperature"];

        Is(ItemVerdict.Caution, row);
        IsNumber(40, row);
        Assert.Equal(CheckEvidenceCode.TempOverCriticalBefore, row.Evidence!.Code);
        Assert.Equal("12", row.Evidence.CriticalTempMinutes);
        Assert.Equal("30", row.Evidence.WarningTempMinutes);
    }

    [Fact]
    public void Time_over_the_critical_temperature_never_lowers_a_check_and_the_warning_minutes_may_be_missing()
    {
        var hot = Nvme(temperature: 85).Edit(h => h with { Nvme = Details(criticalMinutes: 5) });

        var row = Rows(hot)["storage.temperature"];

        Is(ItemVerdict.Check, row);
        Assert.Equal("5", row.Evidence!.CriticalTempMinutes);
        Assert.Null(row.Evidence.WarningTempMinutes);
    }

    [Fact]
    public void No_time_over_the_critical_temperature_is_no_evidence()
    {
        Assert.Null(Rows(Nvme(temperature: 40).Edit(h => h with { Nvme = Details(warningMinutes: 100, criticalMinutes: 0) }))["storage.temperature"].Evidence);
        Is(ItemVerdict.Ok, Rows(Nvme(temperature: 40).Edit(h => h with { Nvme = Details(warningMinutes: 100) }))["storage.temperature"]);
    }

    // ---- storage.temperature, ATA ----

    [Theory]
    [InlineData(1UL, ItemVerdict.Ok)]
    [InlineData(59UL, ItemVerdict.Ok)]
    [InlineData(60UL, ItemVerdict.Caution)]
    [InlineData(69UL, ItemVerdict.Caution)]
    [InlineData(70UL, ItemVerdict.Check)]
    [InlineData(127UL, ItemVerdict.Check)]
    public void Ata_temperature_boundaries_and_the_range_one_to_127(ulong degrees, ItemVerdict expected)
    {
        var row = Rows(Ata([Attr(194, degrees)]))["storage.temperature"];

        Is(expected, row);
        IsNumber(degrees, row);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(128UL)]
    [InlineData(255UL)]
    [InlineData(0x100UL)]
    public void An_ata_temperature_outside_one_to_127_is_not_a_temperature(ulong raw)
    {
        // 0x100 has a low byte of 0: the upper bytes are never read as degrees.
        IsMissing(CheckReason.Error, Rows(Ata([Attr(194, raw)]))["storage.temperature"]);
    }

    [Fact]
    public void Only_the_low_byte_of_the_temperature_attribute_counts()
    {
        // Many models put the lowest and the highest temperature above the current one.
        var raw = (55UL << 32) | (20UL << 16) | 45;

        var row = Rows(Ata([Attr(194, raw)]))["storage.temperature"];

        IsNumber(45, row);
        Is(ItemVerdict.Ok, row);
    }

    [Fact]
    public void Attribute_190_stands_in_when_the_drive_has_no_194()
    {
        var row = Rows(Ata([Attr(190, 65)]))["storage.temperature"];

        IsNumber(65, row);
        Is(ItemVerdict.Caution, row);
        Assert.Equal(CheckSource.Ata, row.Source);
    }

    [Fact]
    public void Attribute_194_wins_over_190_and_an_unusable_194_is_not_replaced_by_the_190()
    {
        IsNumber(40, Rows(Ata([Attr(190, 65), Attr(194, 40)]))["storage.temperature"]);
        IsMissing(CheckReason.Error, Rows(Ata([Attr(190, 50), Attr(194, 0)]))["storage.temperature"]);
    }

    [Fact]
    public void A_sata_drive_without_a_temperature_attribute_is_unavailable_and_unsupported()
    {
        IsMissing(CheckReason.Unsupported, Rows(Ata([Attr(5, 0)]))["storage.temperature"]);
    }

    // ---- storage.nvme.available_spare ----

    [Theory]
    [InlineData(10.0, 9.0, ItemVerdict.Check)]
    [InlineData(10.0, 10.0, ItemVerdict.Caution)]
    [InlineData(10.0, 19.0, ItemVerdict.Caution)]
    [InlineData(10.0, 20.0, ItemVerdict.Ok)]
    [InlineData(5.0, 4.0, ItemVerdict.Check)]
    [InlineData(5.0, 5.0, ItemVerdict.Caution)]
    [InlineData(5.0, 14.0, ItemVerdict.Caution)]
    [InlineData(5.0, 15.0, ItemVerdict.Ok)]
    [InlineData(0.0, 9.0, ItemVerdict.Caution)]
    [InlineData(0.0, 10.0, ItemVerdict.Ok)]
    [InlineData(10.0, 100.0, ItemVerdict.Ok)]
    public void Spare_against_the_threshold_and_ten_points(double threshold, double spare, ItemVerdict expected)
    {
        var row = Rows(Nvme(spare: spare, threshold: threshold))["storage.nvme.available_spare"];

        Is(expected, row);
        IsInt((long)spare, row);
        Assert.Equal("%", row.Unit);
    }

    [Theory]
    [InlineData(9.0, ItemVerdict.Caution)]
    [InlineData(10.0, ItemVerdict.Ok)]
    public void A_threshold_that_cannot_be_read_is_taken_as_zero(double spare, ItemVerdict expected)
    {
        var drive = Nvme(spare: spare).Edit(h => h with { AvailableSpareThresholdPercent = Value.Unsupported<double>("t") });

        var rows = Rows(drive);

        Is(expected, rows["storage.nvme.available_spare"]);
        IsMissing(CheckReason.Unsupported, rows["storage.nvme.spare_threshold"], ItemVerdict.Info);
    }

    [Fact]
    public void The_spare_threshold_is_an_observation_with_its_value()
    {
        var row = Rows(Nvme(threshold: 10))["storage.nvme.spare_threshold"];

        IsNumber(10, row);
        Is(ItemVerdict.Info, row);
    }

    [Fact]
    public void A_spare_that_is_not_a_whole_number_or_not_there_is_unavailable()
    {
        IsMissing(CheckReason.Error, Rows(Nvme(spare: 99.5))["storage.nvme.available_spare"]);
        IsMissing(CheckReason.Error, Rows(Nvme(spare: -1))["storage.nvme.available_spare"]);
        IsMissing(CheckReason.Error, Rows(Nvme(spare: double.NaN))["storage.nvme.available_spare"]);
        IsMissing(CheckReason.Denied, Rows(Nvme().Edit(h => h with { AvailableSparePercent = Value.Denied<double>("t") }))["storage.nvme.available_spare"]);
    }

    // ---- storage.nvme.percentage_used ----

    [Theory]
    [InlineData(0.0, ItemVerdict.Ok)]
    [InlineData(89.0, ItemVerdict.Ok)]
    [InlineData(90.0, ItemVerdict.Caution)]
    [InlineData(99.0, ItemVerdict.Caution)]
    [InlineData(100.0, ItemVerdict.Check)]
    [InlineData(255.0, ItemVerdict.Check)]
    public void Wear_boundaries(double used, ItemVerdict expected)
    {
        var row = Rows(Nvme(used: used))["storage.nvme.percentage_used"];

        Is(expected, row);
        IsInt((long)used, row);
    }

    [Fact]
    public void A_wear_that_is_not_a_whole_number_is_unavailable()
    {
        IsMissing(CheckReason.Error, Rows(Nvme(used: 3.5))["storage.nvme.percentage_used"]);
    }

    // ---- storage.nvme.critical_warning ----

    [Theory]
    [InlineData(0, ItemVerdict.Ok)]
    [InlineData(1, ItemVerdict.Check)]
    [InlineData(2, ItemVerdict.Check)]
    [InlineData(31, ItemVerdict.Check)]
    public void Critical_warning_zero_is_ok_and_anything_else_a_check(int bits, ItemVerdict expected)
    {
        var row = Rows(Nvme(critical: bits))["storage.nvme.critical_warning"];

        Is(expected, row);
        IsInt(bits, row);
        Assert.Equal("bits", row.Unit);
        Assert.Null(row.Evidence);
    }

    [Fact]
    public void Newly_set_bits_are_named_in_the_evidence()
    {
        var row = Rows(Nvme(critical: 3), Compared(Counter("nvme.critical_warning", 1, 3)))["storage.nvme.critical_warning"];

        Is(ItemVerdict.Check, row);
        Assert.Equal(CheckEvidenceCode.NewBits, row.Evidence!.Code);
        Assert.Equal(2, row.Evidence.NewBits);
        Assert.Equal("1", row.Evidence.Before);
        Assert.Equal("3", row.Evidence.After);
        Assert.Equal(Since, row.Evidence.SinceUtc);
    }

    [Fact]
    public void A_warning_that_was_set_before_and_still_is_a_check_without_new_bits()
    {
        var row = Rows(Nvme(critical: 1), Compared(Counter("nvme.critical_warning", 1, 1)))["storage.nvme.critical_warning"];

        Is(ItemVerdict.Check, row);
        Assert.Null(row.Evidence!.Code);
        Assert.Null(row.Evidence.NewBits);
    }

    [Fact]
    public void A_warning_that_cleared_is_ok_and_says_the_counter_went_down()
    {
        var row = Rows(Nvme(critical: 0), Compared(Counter("nvme.critical_warning", 2, 0)))["storage.nvme.critical_warning"];

        Is(ItemVerdict.Ok, row);
        Assert.Equal(CheckEvidenceCode.CounterDecreased, row.Evidence!.Code);
    }

    [Fact]
    public void A_critical_warning_that_cannot_be_read_is_unavailable()
    {
        var drive = Nvme().Edit(h => h with { CriticalWarning = Value.Failed<int>("t") });

        IsMissing(CheckReason.Error, Rows(drive)["storage.nvme.critical_warning"]);
    }

    // ---- growth rows: media errors and the ATA counters (the table at the top of §14.4) ----

    public static TheoryData<string, string, int> StandardCounters => new()
    {
        { "storage.nvme.media_errors", "nvme.media_errors", -1 },
        { "storage.ata.5", "ata.5", 5 },
        { "storage.ata.187", "ata.187", 187 },
        { "storage.ata.196", "ata.196", 196 },
    };

    private static StorageInspectionDrive DriveWith(int attribute, ulong value) =>
        attribute < 0
            ? Nvme(media: value)
            : HealthyAta(Attr(attribute, value));

    [Theory]
    [MemberData(nameof(StandardCounters))]
    public void A_standard_counter_at_zero_is_ok(string rowId, string counter, int attribute)
    {
        var row = Rows(DriveWith(attribute, 0), Compared(Counter(counter, 0, 0)))[rowId];

        Is(ItemVerdict.Ok, row);
        Assert.Null(row.Evidence);
    }

    [Theory]
    [MemberData(nameof(StandardCounters))]
    public void A_standard_counter_that_grew_is_a_check_with_the_two_numbers(string rowId, string counter, int attribute)
    {
        var row = Rows(DriveWith(attribute, 5), Compared(Counter(counter, 3, 5)))[rowId];

        Is(ItemVerdict.Check, row);
        IsDec(5, row);
        Assert.Equal(CheckEvidenceCode.CounterIncreased, row.Evidence!.Code);
        Assert.Equal("3", row.Evidence.Before);
        Assert.Equal("5", row.Evidence.After);
        Assert.Equal(Since, row.Evidence.SinceUtc);
    }

    [Theory]
    [MemberData(nameof(StandardCounters))]
    public void A_standard_counter_that_did_not_grow_is_ok(string rowId, string counter, int attribute)
    {
        var row = Rows(DriveWith(attribute, 3), Compared(Counter(counter, 3, 3)))[rowId];

        Is(ItemVerdict.Ok, row);
        Assert.Null(row.Evidence!.Code);
        Assert.Equal("3", row.Evidence.Before);
        Assert.Equal("3", row.Evidence.After);
    }

    [Theory]
    [MemberData(nameof(StandardCounters))]
    public void A_standard_counter_that_went_down_is_ok_and_says_so(string rowId, string counter, int attribute)
    {
        var row = Rows(DriveWith(attribute, 3), Compared(Counter(counter, 5, 3)))[rowId];

        Is(ItemVerdict.Ok, row);
        Assert.Equal(CheckEvidenceCode.CounterDecreased, row.Evidence!.Code);
    }

    [Theory]
    [MemberData(nameof(StandardCounters))]
    public void A_standard_counter_above_zero_that_could_not_be_compared_is_a_caution(string rowId, string counter, int attribute)
    {
        _ = counter;

        var row = Rows(DriveWith(attribute, 3), First())[rowId];

        Is(ItemVerdict.Caution, row);
        IsDec(3, row);
        Assert.Null(row.Evidence);
    }

    [Theory]
    [MemberData(nameof(StandardCounters))]
    public void A_standard_counter_at_zero_that_could_not_be_compared_is_ok(string rowId, string counter, int attribute)
    {
        _ = counter;

        Is(ItemVerdict.Ok, Rows(DriveWith(attribute, 0), First())[rowId]);
    }

    [Theory]
    [InlineData(197)]
    [InlineData(198)]
    public void Unreadable_sectors_are_a_check_when_they_grew_when_they_did_not_and_when_never_compared(int id)
    {
        var name = $"ata.{id}";
        var rowId = $"storage.ata.{id}";

        Is(ItemVerdict.Ok, Rows(HealthyAta(Attr(id, 0)), Compared(Counter(name, 0, 0)))[rowId]);

        var grown = Rows(HealthyAta(Attr(id, 8)), Compared(Counter(name, 2, 8)))[rowId];
        Is(ItemVerdict.Check, grown);
        Assert.Equal(CheckEvidenceCode.CounterIncreased, grown.Evidence!.Code);

        var steady = Rows(HealthyAta(Attr(id, 8)), Compared(Counter(name, 8, 8)))[rowId];
        Is(ItemVerdict.Check, steady);
        Assert.Null(steady.Evidence!.Code);

        Is(ItemVerdict.Check, Rows(HealthyAta(Attr(id, 8)), First())[rowId]);
    }

    [Fact]
    public void Sectors_that_dropped_but_are_still_there_stay_a_check_and_say_the_counter_went_down()
    {
        var row = Rows(HealthyAta(Attr(197, 6)), Compared(Counter("ata.197", 8, 6)))["storage.ata.197"];

        Is(ItemVerdict.Check, row);
        Assert.Equal(CheckEvidenceCode.CounterDecreased, row.Evidence!.Code);
    }

    [Fact]
    public void Crc_errors_are_a_caution_with_the_cable_code_when_they_grew_or_cannot_be_compared()
    {
        var grown = Rows(HealthyAta(Attr(199, 7)), Compared(Counter("ata.199", 3, 7)))["storage.ata.199"];
        Is(ItemVerdict.Caution, grown);
        Assert.Equal(CheckEvidenceCode.LikelyCable, grown.Evidence!.Code);
        Assert.Equal("3", grown.Evidence.Before);
        Assert.Equal("7", grown.Evidence.After);

        var first = Rows(HealthyAta(Attr(199, 3)), First())["storage.ata.199"];
        Is(ItemVerdict.Caution, first);
        Assert.Equal(CheckEvidenceCode.LikelyCable, first.Evidence!.Code);
        Assert.Null(first.Evidence.Before);
    }

    [Fact]
    public void Crc_errors_that_did_not_grow_are_ok_and_zero_is_ok_even_when_never_compared()
    {
        var steady = Rows(HealthyAta(Attr(199, 3)), Compared(Counter("ata.199", 3, 3)))["storage.ata.199"];
        Is(ItemVerdict.Ok, steady);
        Assert.Null(steady.Evidence!.Code);

        Is(ItemVerdict.Ok, Rows(HealthyAta(Attr(199, 0)), First())["storage.ata.199"]);
        Is(ItemVerdict.Ok, Rows(HealthyAta(Attr(199, 0)), Compared(Counter("ata.199", 2, 0)))["storage.ata.199"]);
    }

    [Fact]
    public void A_counter_the_baseline_could_not_compare_for_this_row_alone_is_judged_as_never_compared()
    {
        var outcome = Compared(Uncompared("ata.5", CheckEvidenceCode.BaselineMissingCounter));

        var rows = Rows(HealthyAta(Attr(5, 4)), outcome);

        Is(ItemVerdict.Caution, rows["storage.ata.5"]);
        Is(ItemVerdict.Info, rows["storage.ata.5.delta"]);
        Assert.Equal(CheckEvidenceCode.BaselineMissingCounter, rows["storage.ata.5.delta"].Evidence!.Code);
    }

    [Fact]
    public void Media_errors_that_cannot_be_read_are_unavailable()
    {
        var drive = Nvme().Edit(h => h with { MediaErrors = Value.Unsupported<U128>("t") });

        IsMissing(CheckReason.Unsupported, Rows(drive)["storage.nvme.media_errors"]);
    }

    [Fact]
    public void A_counter_wider_than_64_bits_is_shown_in_full()
    {
        var drive = Drives.WithWideMedia(Nvme(), UInt128.Parse("340282366920938463463374607431768211455", System.Globalization.CultureInfo.InvariantCulture));

        var row = Rows(drive)["storage.nvme.media_errors"];

        Assert.Equal(CheckValue.Dec(UInt128.MaxValue), row.Value);
        Is(ItemVerdict.Caution, row);
    }

    [Fact]
    public void A_sata_attribute_is_shown_as_the_baseline_keeps_it()
    {
        // 9 keeps its low 32 bits; the others the full raw value. The row and the ".delta" evidence must say the same number.
        var drive = HealthyAta(Attr(9, (7UL << 32) | 1234), Attr(5, 0x1_0000_0001));

        var rows = Rows(drive);

        IsDec(1234, rows["storage.ata.9"]);
        IsDec(0x1_0000_0001, rows["storage.ata.5"]);
    }

    [Fact]
    public void A_repeated_attribute_is_read_as_the_last_one_like_the_baseline_does()
    {
        var drive = Ata([Attr(194, 30), Attr(5, 1), Attr(5, 9)]);

        IsDec(9, Rows(drive)["storage.ata.5"]);
    }

    // ---- storage.nvme.error_log_entries ----

    [Fact]
    public void Media_errors_in_the_recent_log_are_a_caution_with_the_two_counts()
    {
        var drive = Nvme(errorLog: 6).EditCounters(c => c with { RecentErrorLog = new StorageErrorLogCounts(EntriesRead: 4, MediaErrors: 2) });

        var row = Rows(drive)["storage.nvme.error_log_entries"];

        Is(ItemVerdict.Caution, row);
        IsDec(6, row);
        Assert.Equal(CheckEvidenceCode.MediaErrorsInLog, row.Evidence!.Code);
        Assert.Equal(2, row.Evidence.RecentMediaErrors);
        Assert.Equal(4, row.Evidence.RecentEntries);
    }

    [Fact]
    public void A_recent_log_with_no_media_error_says_nothing_and_does_not_make_a_caution()
    {
        var drive = Nvme(errorLog: 6).EditCounters(c => c with { RecentErrorLog = new StorageErrorLogCounts(EntriesRead: 3, MediaErrors: 0) });

        var row = Rows(drive)["storage.nvme.error_log_entries"];

        Is(ItemVerdict.Ok, row);
        Assert.Null(row.Evidence);
    }

    [Fact]
    public void An_unreadable_recent_log_is_judged_by_the_growth_alone_and_never_filled_with_zeros()
    {
        var first = Rows(Nvme(errorLog: 6))["storage.nvme.error_log_entries"];
        Is(ItemVerdict.Ok, first);
        Assert.Null(first.Evidence);

        var grown = Rows(Nvme(errorLog: 9), Compared(Counter("nvme.error_log_entries", 6, 9)))["storage.nvme.error_log_entries"];
        Is(ItemVerdict.Caution, grown);
        Assert.Equal(CheckEvidenceCode.CounterIncreased, grown.Evidence!.Code);
        Assert.Null(grown.Evidence.RecentMediaErrors);
        Assert.Null(grown.Evidence.RecentEntries);
    }

    [Fact]
    public void A_total_that_grew_is_a_caution_even_with_a_clean_recent_log()
    {
        var drive = Nvme(errorLog: 9).EditCounters(c => c with { RecentErrorLog = new StorageErrorLogCounts(0, 0) });

        var row = Rows(drive, Compared(Counter("nvme.error_log_entries", 6, 9)))["storage.nvme.error_log_entries"];

        Is(ItemVerdict.Caution, row);
        Assert.Equal(CheckEvidenceCode.CounterIncreased, row.Evidence!.Code);
    }

    [Fact]
    public void A_total_that_did_not_grow_or_went_down_is_ok()
    {
        Is(ItemVerdict.Ok, Rows(Nvme(errorLog: 6), Compared(Counter("nvme.error_log_entries", 6, 6)))["storage.nvme.error_log_entries"]);

        var down = Rows(Nvme(errorLog: 4), Compared(Counter("nvme.error_log_entries", 6, 4)))["storage.nvme.error_log_entries"];
        Is(ItemVerdict.Ok, down);
        Assert.Equal(CheckEvidenceCode.CounterDecreased, down.Evidence!.Code);
    }

    [Fact]
    public void The_total_comes_from_the_counters_and_else_from_the_health_details()
    {
        var both = Nvme(errorLog: 6).Edit(h => h with { Nvme = Details(errorLog: 99) });
        IsDec(6, Rows(both)["storage.nvme.error_log_entries"]);

        var details = (Nvme() with { Counters = null }).Edit(h => h with { Nvme = Details(errorLog: 7) });
        IsDec(7, Rows(details)["storage.nvme.error_log_entries"]);
    }

    [Fact]
    public void An_error_log_total_that_nobody_gave_is_unavailable()
    {
        var none = Nvme() with { Counters = null };
        IsMissing(CheckReason.Unsupported, Rows(none)["storage.nvme.error_log_entries"]);

        var denied = none.Edit(h => h with { Nvme = new NvmeHealthDetails(Value.NotCollected<U128>("t"), Value.NotCollected<U128>("t"), Value.NotCollected<U128>("t"), Value.Denied<U128>("t"), Value.NotCollected<long>("t"), Value.NotCollected<long>("t"), Value.NotCollected<IReadOnlyList<double>>("t"), Value.NotCollected<IReadOnlyList<string>>("t")) });
        IsMissing(CheckReason.Denied, Rows(denied)["storage.nvme.error_log_entries"]);
    }

    // ---- the observations of an NVMe drive ----

    [Fact]
    public void The_nvme_observations_carry_their_values_and_units()
    {
        var rows = Rows(Nvme());

        IsDec(5, rows["storage.nvme.unsafe_shutdowns"]);
        IsDec(11, rows["storage.nvme.power_cycles"]);
        IsDec(100, rows["storage.nvme.power_on_hours"]);
        IsDec(1000, rows["storage.nvme.data_units_written"]);
        Assert.Equal("h", rows["storage.nvme.power_on_hours"].Unit);
        Assert.Equal("nvme_data_unit", rows["storage.nvme.data_units_written"].Unit);
        Assert.All(
            ["storage.nvme.unsafe_shutdowns", "storage.nvme.power_cycles", "storage.nvme.power_on_hours", "storage.nvme.data_units_written"],
            id => Is(ItemVerdict.Info, rows[id]));
    }

    [Fact]
    public void An_observation_that_cannot_be_read_is_an_info_row_with_the_reason()
    {
        var drive = Nvme().Edit(h => h with { PowerCycles = Value.Denied<U128>("t"), PowerOnHours = Value.Failed<U128>("t") });

        var rows = Rows(drive);

        IsMissing(CheckReason.Denied, rows["storage.nvme.power_cycles"], ItemVerdict.Info);
        IsMissing(CheckReason.Error, rows["storage.nvme.power_on_hours"], ItemVerdict.Info);
    }

    [Fact]
    public void Data_units_written_come_from_the_counters_alone()
    {
        var drive = Nvme() with { Counters = null };

        IsMissing(CheckReason.Unsupported, Rows(drive)["storage.nvme.data_units_written"], ItemVerdict.Info);
    }

    // ---- storage.ata.threshold_status ----

    [Fact]
    public void Thresholds_that_were_not_read_are_unavailable()
    {
        var row = Rows(Ata([Attr(194, 33), Attr(5, 0, current: 1, threshold: 36)], thresholdsRead: false))["storage.ata.threshold_status"];

        IsMissing(CheckReason.Unsupported, row);
    }

    [Fact]
    public void A_current_value_at_or_below_its_threshold_is_a_check()
    {
        foreach (var current in new[] { 30, 36 })
        {
            var row = Rows(Ata([Attr(194, 33), Attr(5, 0, current: current, worst: 30, threshold: 36)]))["storage.ata.threshold_status"];

            Is(ItemVerdict.Check, row);
            Assert.Equal(CheckValue.Text("current_breach"), row.Value);
            Assert.Equal(CheckEvidenceCode.CurrentThresholdBreach, row.Evidence!.Code);
        }
    }

    [Fact]
    public void A_worst_value_at_or_below_its_threshold_is_a_caution_when_the_current_one_is_fine()
    {
        foreach (var worst in new[] { 30, 36 })
        {
            var row = Rows(Ata([Attr(194, 33), Attr(5, 0, current: 50, worst: worst, threshold: 36)]))["storage.ata.threshold_status"];

            Is(ItemVerdict.Caution, row);
            Assert.Equal(CheckValue.Text("past_breach"), row.Value);
            Assert.Equal(CheckEvidenceCode.PastThresholdBreach, row.Evidence!.Code);
        }
    }

    [Fact]
    public void A_current_breach_outranks_a_past_one_on_another_attribute()
    {
        var row = Rows(Ata([Attr(194, 33), Attr(5, 0, current: 50, worst: 30, threshold: 36), Attr(3, 0, current: 10, worst: 10, threshold: 20)]))["storage.ata.threshold_status"];

        Assert.Equal(CheckValue.Text("current_breach"), row.Value);
    }

    [Fact]
    public void Values_just_above_the_threshold_are_within_it()
    {
        var row = Rows(Ata([Attr(194, 33), Attr(5, 0, current: 37, worst: 37, threshold: 36)]))["storage.ata.threshold_status"];

        Is(ItemVerdict.Ok, row);
        Assert.Equal(CheckValue.Text("within"), row.Value);
        Assert.Null(row.Evidence);
    }

    [Fact]
    public void An_attribute_with_a_threshold_of_zero_is_not_compared()
    {
        var row = Rows(Ata([Attr(194, 33), Attr(5, 0, current: 0, worst: 0, threshold: 0)]))["storage.ata.threshold_status"];

        Is(ItemVerdict.Ok, row);
        Assert.Equal(CheckValue.Text("within"), row.Value);
    }

    // ---- storage.ata.predict_failure ----

    [Fact]
    public void Predicted_failure_true_is_a_check_false_is_ok_and_unread_is_no_row()
    {
        var yes = Rows(Ata([Attr(194, 33)], predictFailure: true))["storage.ata.predict_failure"];
        Is(ItemVerdict.Check, yes);
        Assert.Equal(CheckValue.Bool(true), yes.Value);

        var no = Rows(Ata([Attr(194, 33)], predictFailure: false))["storage.ata.predict_failure"];
        Is(ItemVerdict.Ok, no);
        Assert.Equal(CheckValue.Bool(false), no.Value);

        Assert.DoesNotContain("storage.ata.predict_failure", Rows(Ata([Attr(194, 33)], predictFailure: null)).Keys);
    }

    // ---- storage.health ----

    // DESIGN-A §14.4: the row is Windows' own status (MSFT_PhysicalDisk), one per disk and the first of its rows. It is never a roll-up of the
    // disk's other rows, so each judged row is counted once in the overall verdict.

    [Fact]
    public void Every_disk_has_one_health_row_first_and_the_rows_are_counted_once()
    {
        var critical = Rows(Nvme(critical: 1));
        Assert.Equal("storage.health", critical.Keys.First());
        Is(ItemVerdict.Ok, critical["storage.health"]);
        Is(ItemVerdict.Check, critical["storage.nvme.critical_warning"]);
        Assert.Equal(1, critical.Values.Count(static r => r.Verdict == ItemVerdict.Check)); // the critical warning alone, not it and a summary of it

        var worn = Judged(Nvme(used: 95), First(), Subject);
        Assert.Equal(1, worn.Count(static r => r.Id == "storage.health"));
        Assert.Equal(1, worn.Count(static r => r.Verdict == ItemVerdict.Caution)); // the wear row alone
        Assert.Equal(CheckVerdict.Caution, Overall(worn));

        var both = Judged(Nvme(used: 95, critical: 4), First(), Subject);
        Assert.Equal(1, both.Count(static r => r.Verdict == ItemVerdict.Check));
        Assert.Equal(1, both.Count(static r => r.Verdict == ItemVerdict.Caution));

        Assert.Equal("storage.health", Rows(HealthyAta()).Keys.First());
    }

    [Fact]
    public void The_health_row_is_the_windows_reading_with_the_source_wmi_and_adds_exactly_one_item_to_the_verdict()
    {
        var healthy = Judged(Nvme(), First(), Subject, DiskHealthVerdict.Healthy);
        Assert.Equal(CheckValue.Text("healthy"), healthy[0].Value);
        Assert.Equal(CheckSource.Wmi, healthy[0].Source);
        Assert.Equal(Subject, healthy[0].Subject);
        Is(ItemVerdict.Ok, healthy[0]);

        var warning = Judged(Nvme(), First(), Subject, DiskHealthVerdict.Warning);
        Assert.Equal(CheckValue.Text("warning"), warning[0].Value);
        Is(ItemVerdict.Caution, warning[0]);
        Assert.Equal(1, warning.Count(static r => r.Verdict == ItemVerdict.Caution));
        Assert.Equal(CheckVerdict.Caution, Overall(warning));

        var critical = Judged(Nvme(), First(), Subject, DiskHealthVerdict.Critical);
        Assert.Equal(CheckValue.Text("critical"), critical[0].Value);
        Is(ItemVerdict.Check, critical[0]);
        Assert.Equal(1, critical.Count(static r => r.Verdict == ItemVerdict.Check)); // the SMART rows of this disk are fine: one check, from Windows
        Assert.Equal(CheckVerdict.Check, Overall(critical));
        AssertValid(critical);
    }

    [Theory]
    [InlineData(CheckReason.Denied)]
    [InlineData(CheckReason.Timeout)]
    [InlineData(CheckReason.Error)]
    [InlineData(CheckReason.Unsupported)]
    public void A_health_that_windows_did_not_give_is_unavailable_with_its_reason_and_does_not_hide_the_other_rows(CheckReason reason)
    {
        var rows = Judged(Nvme(), First(), Subject, DiskHealthVerdict.Unavailable(reason));

        IsMissing(reason, rows[0]);
        Assert.Equal("storage.health", rows[0].Id);
        Assert.Equal(CheckSource.Wmi, rows[0].Source);
        Is(ItemVerdict.Ok, rows.Single(static r => r.Id == "storage.nvme.critical_warning"));
        AssertValid(rows);
        Assert.Equal(CheckVerdict.Unavailable, Overall(rows)); // the ranking: an unavailable item outweighs the ok ones
    }

    [Fact]
    public void A_sata_drive_with_nothing_readable_has_unavailable_rows_only()
    {
        var drive = Ata([], thresholdsRead: false);

        var rows = Rows(drive);

        Is(ItemVerdict.Ok, rows["storage.health"]); // Windows' reading does not need SMART
        IsMissing(CheckReason.Unsupported, rows["storage.temperature"]);
        IsMissing(CheckReason.Unsupported, rows["storage.ata.threshold_status"]);
    }

    // ---- storage.ata.threshold_status when the table was not read ----

    [Fact]
    public void An_unread_threshold_table_of_a_drive_that_gave_its_smart_data_is_unsupported()
    {
        IsMissing(CheckReason.Unsupported, Rows(Ata([Attr(194, 33)], thresholdsRead: false))["storage.ata.threshold_status"]);
    }

    [Fact]
    public void An_unread_threshold_table_without_the_bytes_behind_the_health_is_an_error_not_unsupported()
    {
        // Health came out, the evidence it was read from did not: whether the drive has a table is not known.
        var drive = Ata([Attr(194, 33)]) with { Counters = null };

        IsMissing(CheckReason.Error, Rows(drive)["storage.ata.threshold_status"]);
    }

    [Theory]
    [InlineData(ValueStatus.Denied, CheckReason.Denied)]
    [InlineData(ValueStatus.Timeout, CheckReason.Timeout)]
    [InlineData(ValueStatus.Error, CheckReason.Error)]
    [InlineData(ValueStatus.Unsupported, CheckReason.Unsupported)]
    [InlineData(ValueStatus.NotCollected, CheckReason.Error)]
    public void An_unread_threshold_table_has_the_reason_the_read_of_the_thresholds_gave(ValueStatus status, CheckReason reason)
    {
        // The IOCTL that reads the thresholds said no for this reason: the row says the same, it is not "unsupported" because it is missing.
        var drive = Ata([Attr(194, 33)], thresholdsRead: false).EditCounters(c => c with { AtaThresholdsStatus = status });

        var row = Rows(drive)["storage.ata.threshold_status"];

        IsMissing(reason, row);
        Assert.Equal(CheckSource.Ata, row.Source);
    }

    [Fact]
    public void The_status_of_the_thresholds_is_not_used_when_the_table_was_read()
    {
        var drive = Ata([Attr(194, 33)]).EditCounters(static c => c with { AtaThresholdsStatus = ValueStatus.Denied });

        Is(ItemVerdict.Ok, Rows(drive)["storage.ata.threshold_status"]);
    }

    // ---- disks that cannot be read ----

    [Theory]
    [InlineData(ValueStatus.Unsupported, CheckReason.Unsupported)]
    [InlineData(ValueStatus.Denied, CheckReason.Denied)]
    [InlineData(ValueStatus.Timeout, CheckReason.Timeout)]
    [InlineData(ValueStatus.Error, CheckReason.Error)]
    [InlineData(ValueStatus.NotCollected, CheckReason.Error)]
    [InlineData(ValueStatus.Gone, CheckReason.Error)]
    public void A_disk_without_health_has_capacity_and_temperature_and_nothing_judged(ValueStatus status, CheckReason reason)
    {
        // A USB bridge or a RAID volume: the disk is there, its health is not.
        var drive = Drives.NoHealth() with { Health = Value.Missing<StorageHealth>(status, "t") };

        var rows = Judged(drive, J.First(), Subject);

        AssertValid(rows);
        Assert.Equal(["storage.health", "storage.capacity", "storage.temperature"], rows.Select(static r => r.Id)); // Windows still says how the disk is
        IsDec(2000398934016UL, rows[1]);
        IsMissing(reason, rows[2]);
        Assert.Equal(CheckVerdict.Unavailable, Overall(rows));
    }

    [Fact]
    public void An_unreadable_slot_has_the_health_row_and_one_unavailable_temperature_row_and_nothing_else()
    {
        var slot = Drives.Unreadable();

        var rows = Judged(slot, First(), StorageJudge.SubjectOf(slot, "disk-2"));

        AssertValid(rows);
        Assert.Equal(["storage.health", "storage.temperature"], rows.Select(static r => r.Id)); // Windows' reading, and the one row that says the slot was not read
        var only = rows[1];
        IsMissing(CheckReason.Error, only);
        Assert.Null(only.Subject!.Model);
        Assert.Equal(CheckVerdict.Unavailable, Overall(rows)); // the slot keeps the overall verdict from reading "ok"
    }

    [Fact]
    public void A_disk_that_is_not_comparable_still_gets_every_absolute_row()
    {
        var rows = Rows(Nvme(media: 3), new BaselineDiskOutcome(0, "disk-1", BaselineDiskState.NotComparable, CheckEvidenceCode.BaselineDeviceChanged, [Uncompared("nvme.media_errors", CheckEvidenceCode.BaselineDeviceChanged)]));

        Is(ItemVerdict.Caution, rows["storage.nvme.media_errors"]);
        Is(ItemVerdict.Info, rows["storage.nvme.media_errors.delta"]);
        Assert.Null(rows["storage.nvme.media_errors.delta"].Value);
        Assert.Equal(CheckEvidenceCode.BaselineDeviceChanged, rows["storage.nvme.media_errors.delta"].Evidence!.Code);
    }

    // ---- the boundaries as constants ----

    [Fact]
    public void The_limits_are_the_ones_of_the_design()
    {
        Assert.Equal(80, StorageJudge.NvmeTemperatureCheck);
        Assert.Equal(70, StorageJudge.NvmeTemperatureCaution);
        Assert.Equal(273, StorageJudge.NvmeKelvinOffset);
        Assert.Equal(70, StorageJudge.AtaTemperatureCheck);
        Assert.Equal(60, StorageJudge.AtaTemperatureCaution);
        Assert.Equal(1, StorageJudge.AtaTemperatureMin);
        Assert.Equal(127, StorageJudge.AtaTemperatureMax);
        Assert.Equal(10, StorageJudge.SpareMargin);
        Assert.Equal(100, StorageJudge.WearCheck);
        Assert.Equal(90, StorageJudge.WearCaution);
    }
}
