using PcMieru.Checks.Storage;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Tests.Storage;

/// <summary>
/// The baseline engine on its own folder: what is recorded, what is compared and what each counter says (DESIGN-A §5.2, §14.5). The
/// scenarios of v0.7.4's BaselineAndJudgeTests and BaselineIdentityTests are ported with the same numbers.
/// </summary>
public class BaselineEngineTests
{
    private static readonly DateTimeOffset T0 = Drives.T0;

    private static DateTimeOffset H(double hours) => T0.AddHours(hours);

    private static string NvmeFile => Drives.FileNameOf(Drives.NvmeKey, "S6B1NJ0R123456");

    private static int Entries(string text) => text.Split("\"at\":").Length - 1;

    // ---- recording ----

    [Fact]
    public void The_first_run_of_a_disk_is_recorded_under_the_exact_file_name_and_says_first()
    {
        using var rig = new BaselineRig();

        var result = rig.Run(Drives.Inspect(Drives.Nvme()));

        var disk = Assert.Single(result.Disks);
        Assert.Equal(BaselineDiskState.NotComparable, disk.State);
        Assert.Equal(CheckEvidenceCode.BaselineFirst, disk.Code);
        Assert.Equal("disk-1", disk.DiskId);
        Assert.Equal(
            [
                "storage.nvme.critical_warning", "storage.nvme.available_spare", "storage.nvme.percentage_used", "storage.nvme.media_errors",
                "storage.nvme.error_log_entries", "storage.nvme.unsafe_shutdowns", "storage.nvme.power_on_hours", "storage.nvme.data_units_written",
            ],
            disk.Counters.Select(static c => c.CounterId));
        Assert.All(disk.Counters, static c => Assert.Equal(CheckEvidenceCode.BaselineFirst, c.Code));
        Assert.False(result.ReadFailed);

        Assert.Equal(new[] { "acq-seq.json", NvmeFile, "disk-ordinals.json" }.Order(StringComparer.Ordinal), rig.Names());
        var text = rig.Read(NvmeFile);
        Assert.Contains("\"key\": \"" + Drives.NvmeKey + "\"", text);
        Assert.Contains("\"model\": \"Samsung SSD 980 PRO 2TB\"", text);
        Assert.Contains("\"at\": \"2026-11-20T09:00:00.1230000Z\"", text);
        Assert.Contains("\"nvme.power_on_hours\": 100", text);
        Assert.Contains("\"nvme.critical_warning\": 0", text);
        Assert.Contains("\"acq\": 1", text);
        Assert.DoesNotContain("withheld", text);
        Assert.Equal(1, Entries(text));
    }

    [Fact]
    public void The_second_run_compares_every_counter_with_the_first_and_judges_growth_by_the_old_table()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0, media: 0, errorLog: 0, poh: 100, written: 1000, unsafeShutdowns: 5, used: 3, spare: 100, critical: 0)));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), media: 2, errorLog: 3, poh: 101, written: 1500, unsafeShutdowns: 6, used: 3, spare: 99, critical: 4)));

        var disk = Assert.Single(result.Disks);
        Assert.Equal(BaselineDiskState.Compared, disk.State);
        Assert.Null(disk.Code);
        Assert.All(disk.Counters, static c => Assert.True(c.IsCompared));
        Assert.All(disk.Counters, static c => Assert.Equal(T0, c.SinceUtc));
        var media = BaselineAssert.Counter(disk, "nvme.media_errors");
        Assert.Equal(0UL, media.Before);
        Assert.Equal(2UL, media.After);
        Assert.True(media.Grew);
        Assert.False(BaselineAssert.Counter(disk, "nvme.percentage_used").Grew);

        var rows = BaselineAssert.ValidRows(result, "Samsung SSD 980 PRO 2TB");
        var mediaRow = BaselineAssert.Row(rows, "storage.nvme.media_errors.delta");
        Assert.Equal(ItemVerdict.Check, mediaRow.Verdict);
        Assert.Equal("2", mediaRow.Value!.AsText);
        Assert.Equal(CheckEvidenceCode.CounterIncreased, mediaRow.Evidence!.Code);
        Assert.Equal("0", mediaRow.Evidence.Before);
        Assert.Equal("2", mediaRow.Evidence.After);
        Assert.Equal(T0, mediaRow.Evidence.SinceUtc);
        Assert.Null(mediaRow.Reason);

        var errorLogRow = BaselineAssert.Row(rows, "storage.nvme.error_log_entries.delta");
        Assert.Equal(ItemVerdict.Caution, errorLogRow.Verdict);
        Assert.Equal(CheckEvidenceCode.CounterIncreased, errorLogRow.Evidence!.Code);

        var warningRow = BaselineAssert.Row(rows, "storage.nvme.critical_warning.delta");
        Assert.Equal(ItemVerdict.Check, warningRow.Verdict);
        Assert.Equal("4", warningRow.Value!.AsText);
        Assert.Equal(CheckEvidenceCode.NewBits, warningRow.Evidence!.Code);
        Assert.Equal(4, warningRow.Evidence.NewBits);

        // The counters that grow all the time are information, whatever they did.
        foreach (var id in new[] { "unsafe_shutdowns", "power_on_hours", "data_units_written" })
        {
            var row = BaselineAssert.Row(rows, $"storage.nvme.{id}.delta");
            Assert.Equal(ItemVerdict.Info, row.Verdict);
            Assert.Null(row.Evidence!.Code);
        }

        var spare = BaselineAssert.Row(rows, "storage.nvme.available_spare.delta");
        Assert.Equal(ItemVerdict.Info, spare.Verdict);
        Assert.Equal("-1", spare.Value!.AsText);
        Assert.Null(spare.Evidence!.Code);
        Assert.Equal("0", BaselineAssert.Row(rows, "storage.nvme.percentage_used.delta").Value!.AsText);
    }

    [Fact]
    public void A_table_counter_that_did_not_grow_is_ok_and_one_that_went_down_says_so()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0, media: 4, errorLog: 5)));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), media: 4, errorLog: 3, poh: 101)));

        var rows = BaselineAssert.ValidRows(result);
        var media = BaselineAssert.Row(rows, "storage.nvme.media_errors.delta");
        Assert.Equal(ItemVerdict.Ok, media.Verdict);
        Assert.Equal("0", media.Value!.AsText);
        Assert.Null(media.Evidence!.Code);
        var errorLog = BaselineAssert.Row(rows, "storage.nvme.error_log_entries.delta");
        Assert.Equal(ItemVerdict.Ok, errorLog.Verdict);
        Assert.Equal("-2", errorLog.Value!.AsText);
        Assert.Equal(CheckEvidenceCode.CounterDecreased, errorLog.Evidence!.Code);
    }

    [Fact]
    public void The_critical_warning_grows_only_by_a_newly_set_bit()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0, critical: 0)));

        var up = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), critical: 4, poh: 101)));
        var other = rig.Run(Drives.Inspect(Drives.Nvme(at: H(2), critical: 5, poh: 102)));
        var down = rig.Run(Drives.Inspect(Drives.Nvme(at: H(3), critical: 4, poh: 103)));

        var first = BaselineAssert.Counter(up.Disks[0], "nvme.critical_warning");
        Assert.Equal(4, first.NewBits);
        Assert.True(first.Grew);
        var second = BaselineAssert.Counter(other.Disks[0], "nvme.critical_warning");
        Assert.Equal(1, second.NewBits); // 5 = 4 + 1: only the 1 is new
        Assert.True(second.Grew);
        var third = BaselineAssert.Counter(down.Disks[0], "nvme.critical_warning");
        Assert.Equal(0, third.NewBits);
        Assert.False(third.Grew);

        var downRow = BaselineAssert.Row(BaselineAssert.ValidRows(down), "storage.nvme.critical_warning.delta");
        Assert.Equal(ItemVerdict.Ok, downRow.Verdict);
        Assert.Equal("-1", downRow.Value!.AsText);
        Assert.Equal(CheckEvidenceCode.CounterDecreased, downRow.Evidence!.Code);
        Assert.Null(downRow.Evidence.NewBits);
    }

    [Fact]
    public void A_sata_disk_compares_the_ata_attributes_and_flags_the_cable_counter_as_a_caution()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Sata(at: T0, hours: 1000, reallocated: 0, pending: 0, crc: 0)));

        var result = rig.Run(Drives.Inspect(Drives.Sata(at: H(1), hours: 1001, reallocated: 8, pending: 1, crc: 3)));

        var disk = Assert.Single(result.Disks);
        Assert.Equal(BaselineDiskState.Compared, disk.State);
        Assert.Equal(["storage.ata.5", "storage.ata.197", "storage.ata.199", "storage.ata.9"], disk.Counters.Select(static c => c.CounterId));
        var rows = BaselineAssert.ValidRows(result, "WDC WD40EZRZ-00GXCB0");
        Assert.Equal(ItemVerdict.Check, BaselineAssert.Row(rows, "storage.ata.5.delta").Verdict);
        Assert.Equal(ItemVerdict.Check, BaselineAssert.Row(rows, "storage.ata.197.delta").Verdict);
        var cable = BaselineAssert.Row(rows, "storage.ata.199.delta");
        Assert.Equal(ItemVerdict.Caution, cable.Verdict);
        Assert.Equal(CheckEvidenceCode.LikelyCable, cable.Evidence!.Code);
        Assert.Equal("3", cable.Value!.AsText);
        var hours = BaselineAssert.Row(rows, "storage.ata.9.delta");
        Assert.Equal(ItemVerdict.Info, hours.Verdict);
        Assert.Null(hours.Evidence!.Code);

        // The temperature has no .delta but is kept in the file as v0.7.4 kept it.
        Assert.Contains("\"ata.194\": 33", rig.Read(Drives.FileNameOf(Drives.SataKey, "WD-WX12A")));
    }

    [Fact]
    public void A_drive_whose_health_could_not_be_read_has_no_counters_and_is_not_recorded()
    {
        using var rig = new BaselineRig();

        var result = rig.Run(Drives.Inspect(Drives.NoHealth(), Drives.Unreadable(3)));

        Assert.Equal(2, result.Disks.Count);
        Assert.All(result.Disks, static d => Assert.Equal(BaselineDiskState.NoCounters, d.State));
        Assert.All(result.Disks, static d => Assert.Empty(d.Counters));
        Assert.All(result.Disks, static d => Assert.Null(d.Code));
        Assert.Empty(rig.DiskFiles());
        Assert.Empty(BaselineDeltaRows.Build(result.Disks[0], new CheckSubject("disk-1")));
    }

    [Fact]
    public void The_result_has_one_entry_per_drive_in_the_order_of_the_inspection()
    {
        using var rig = new BaselineRig();

        var result = rig.Run(Drives.Inspect(Drives.Unreadable(9), Drives.Nvme(), Drives.Sata()));

        Assert.Equal([0, 1, 2], result.Disks.Select(static d => d.DriveIndex));
        Assert.Equal(BaselineDiskState.NoCounters, result.Disks[0].State);
        Assert.Equal(BaselineDiskState.NotComparable, result.Disks[1].State);
        Assert.Equal(BaselineDiskState.NotComparable, result.Disks[2].State);
    }

    // ---- who the record belongs to ----

    [Fact]
    public void A_drive_whose_identity_is_not_confirmed_is_neither_compared_nor_recorded()
    {
        using var rig = new BaselineRig();

        var changed = rig.Run(Drives.Inspect(Drives.Nvme(confirmation: StorageIdentityConfirmation.Mismatch)));
        var unconfirmed = rig.Run(Drives.Inspect(Drives.Nvme(confirmation: StorageIdentityConfirmation.Unconfirmed)));

        Assert.Equal(CheckEvidenceCode.BaselineDeviceChanged, changed.Disks[0].Code);
        Assert.Equal(CheckEvidenceCode.BaselineNoIdentity, unconfirmed.Disks[0].Code);
        Assert.All(changed.Disks[0].Counters, static c => Assert.Equal(CheckEvidenceCode.BaselineDeviceChanged, c.Code));
        Assert.Empty(rig.DiskFiles());
        BaselineAssert.ValidRows(changed);
        BaselineAssert.ValidRows(unconfirmed);
    }

    [Fact]
    public void A_different_model_size_or_bus_is_another_disk_with_its_own_record()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0)));

        var model = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), model: "Samsung SSD 990 PRO 2TB", poh: 101)));
        var size = rig.Run(Drives.Inspect(Drives.Nvme(at: H(2), size: 1000204886016, poh: 102)));

        Assert.Equal(CheckEvidenceCode.BaselineFirst, model.Disks[0].Code);
        Assert.Equal(CheckEvidenceCode.BaselineFirst, size.Disks[0].Code);
        Assert.Equal(3, rig.DiskFiles().Count);
    }

    // ---- counters that cannot be compared ----

    [Fact]
    public void A_counter_reset_is_one_code_on_every_row_of_the_disk()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0, poh: 5000)));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 100)));

        var disk = Assert.Single(result.Disks);
        Assert.Equal(BaselineDiskState.NotComparable, disk.State);
        Assert.Equal(CheckEvidenceCode.BaselineCounterReset, disk.Code);
        Assert.Equal(8, disk.Counters.Count);
        Assert.All(disk.Counters, static c => Assert.Equal(CheckEvidenceCode.BaselineCounterReset, c.Code));
        Assert.All(disk.Counters, static c => Assert.False(c.IsCompared));
        BaselineAssert.ValidRows(result);
    }

    [Fact]
    public void The_power_on_hours_going_backwards_is_a_counter_reset_and_the_new_value_becomes_the_base()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0, poh: 5000)));

        var reset = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 100)));
        var after = rig.Run(Drives.Inspect(Drives.Nvme(at: H(2), poh: 101)));

        Assert.Equal(CheckEvidenceCode.BaselineCounterReset, reset.Disks[0].Code);
        Assert.Equal(BaselineDiskState.Compared, after.Disks[0].State);
        var poh = BaselineAssert.Counter(after.Disks[0], "nvme.power_on_hours");
        Assert.Equal(100UL, poh.Before);
        Assert.Equal(H(1), poh.SinceUtc);
        Assert.Equal(3, Entries(rig.Read(NvmeFile)));
        Assert.DoesNotContain("withheld", rig.Read(NvmeFile));
    }

    [Fact]
    public void The_data_units_written_going_backwards_is_a_counter_reset()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0, written: 1000)));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 101, written: 10)));

        Assert.Equal(CheckEvidenceCode.BaselineCounterReset, result.Disks[0].Code);
    }

    [Fact]
    public void The_ata_hours_going_backwards_is_a_counter_reset_but_going_far_forward_is_not_a_clock_problem()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Sata(at: T0, hours: 1000)));

        var forward = rig.Run(Drives.Inspect(Drives.Sata(at: H(1), hours: 900_000)));
        var back = rig.Run(Drives.Inspect(Drives.Sata(at: H(2), hours: 5)));

        // ATA 9 counts minutes, halves or seconds depending on the model: only "backwards" is checked for it.
        Assert.Equal(BaselineDiskState.Compared, forward.Disks[0].State);
        Assert.Equal(CheckEvidenceCode.BaselineCounterReset, back.Disks[0].Code);
    }

    [Theory]
    [InlineData(113UL, false)]
    [InlineData(114UL, true)]
    public void The_power_on_hours_may_advance_by_the_elapsed_hours_plus_three(ulong after, bool ahead)
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0, poh: 100)));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(at: H(10), poh: after)));

        // 10 h elapsed + 2 h of rounding and refresh + 1 h of clock skew = 13 h of allowance.
        Assert.Equal(ahead ? CheckEvidenceCode.BaselineClock : null, result.Disks[0].Code);
        Assert.Equal(ahead ? BaselineDiskState.NotComparable : BaselineDiskState.Compared, result.Disks[0].State);
    }

    [Fact]
    public void A_replacement_with_the_same_serial_and_many_more_hours_is_a_clock_mismatch_and_the_next_run_compares_with_it()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0, poh: 100)));

        var swapped = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 50_000)));
        var next = rig.Run(Drives.Inspect(Drives.Nvme(at: H(2), poh: 50_001)));

        Assert.Equal(CheckEvidenceCode.BaselineClock, swapped.Disks[0].Code);
        Assert.All(swapped.Disks[0].Counters, static c => Assert.Equal(CheckEvidenceCode.BaselineClock, c.Code));
        BaselineAssert.ValidRows(swapped);
        // The time did not go back, so the record is kept as a normal one.
        Assert.DoesNotContain("withheld", rig.Read(NvmeFile));
        Assert.Equal(BaselineDiskState.Compared, next.Disks[0].State);
        Assert.Equal(50_000UL, BaselineAssert.Counter(next.Disks[0], "nvme.power_on_hours").Before);
    }

    [Theory]
    [InlineData(103UL, false)]
    [InlineData(104UL, true)]
    public void When_the_clock_went_back_only_the_three_hours_of_allowance_remain(ulong after, bool ahead)
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0, poh: 100)));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(at: H(-5), poh: after)));

        Assert.Equal(ahead ? CheckEvidenceCode.BaselineClock : null, result.Disks[0].Code);
        // Only an ahead-of-clock record made with an earlier clock is held back.
        Assert.Equal(ahead, rig.Read(NvmeFile).Contains("\"withheld\": true", StringComparison.Ordinal));
    }

    [Fact]
    public void A_record_held_back_by_a_clock_reversal_is_never_the_base_of_the_next_comparison()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0, poh: 100)));
        var reversed = rig.Run(Drives.Inspect(Drives.Nvme(at: H(-5), poh: 200)));

        var next = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 101)));
        var after = rig.Run(Drives.Inspect(Drives.Nvme(at: H(2), poh: 102)));

        Assert.Equal(CheckEvidenceCode.BaselineClock, reversed.Disks[0].Code);
        Assert.Equal(BaselineDiskState.Compared, next.Disks[0].State);
        var poh = BaselineAssert.Counter(next.Disks[0], "nvme.power_on_hours");
        Assert.Equal(100UL, poh.Before); // the record before the held back one
        Assert.Equal(T0, poh.SinceUtc);
        var last = BaselineAssert.Counter(after.Disks[0], "nvme.power_on_hours");
        Assert.Equal(101UL, last.Before);
        Assert.Equal(H(1), last.SinceUtc);
        Assert.Equal(1, rig.Read(NvmeFile).Split("\"withheld\": true").Length - 1);
    }

    [Fact]
    public void Trimming_at_two_hundred_removes_the_oldest_record()
    {
        using var rig = new BaselineRig();
        var entries = Enumerable.Range(0, 200).Select(i => new OldEntry(T0.AddMinutes(i - 200), OldFiles.Nvme(poh: 100))).ToArray();
        OldFiles.Put(rig.Baseline, NvmeFile, OldFiles.History(Drives.NvmeKey, "Samsung SSD 980 PRO 2TB", entries));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(at: T0, poh: 100)));

        Assert.Equal(BaselineDiskState.Compared, result.Disks[0].State);
        var text = rig.Read(NvmeFile);
        Assert.Equal(200, Entries(text));
        Assert.DoesNotContain("2026-11-20T05:40:00.1230000Z", text); // the oldest (200 minutes before) is gone
        Assert.Contains("2026-11-20T05:41:00.1230000Z", text);
        Assert.Contains("2026-11-20T09:00:00.1230000Z", text);
    }

    [Fact]
    public void Trimming_never_removes_the_record_the_next_run_compares_with()
    {
        using var rig = new BaselineRig();
        var held = Enumerable.Range(1, 199).Select(i => new OldEntry(T0.AddDays(-1).AddMinutes(-i), OldFiles.Nvme(poh: 999), Withheld: true));
        OldEntry[] entries = [new(T0, OldFiles.Nvme(poh: 100)), .. held];
        OldFiles.Put(rig.Baseline, NvmeFile, OldFiles.History(Drives.NvmeKey, "Samsung SSD 980 PRO 2TB", entries));

        var reversed = rig.Run(Drives.Inspect(Drives.Nvme(at: H(-5), poh: 200)));

        Assert.Equal(CheckEvidenceCode.BaselineClock, reversed.Disks[0].Code);
        var text = rig.Read(NvmeFile);
        Assert.Equal(200, Entries(text));
        Assert.Equal(199, text.Split("\"withheld\": true").Length - 1);
        Assert.True(text.IndexOf("2026-11-20T09:00:00.1230000Z", StringComparison.Ordinal) < text.IndexOf("\"withheld\"", StringComparison.Ordinal), "the base is still the first record");

        var next = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 101)));
        Assert.Equal(BaselineDiskState.Compared, next.Disks[0].State);
        Assert.Equal(100UL, BaselineAssert.Counter(next.Disks[0], "nvme.power_on_hours").Before);
    }

    // ---- numbers that do not fit ----

    [Fact]
    public void An_old_counter_that_was_saturated_gives_no_delta_for_that_row_only()
    {
        using var rig = new BaselineRig();
        var old = new Dictionary<string, ulong>(OldFiles.Nvme(poh: 100)) { ["nvme.media_errors"] = ulong.MaxValue };
        OldFiles.Put(rig.Baseline, NvmeFile, OldFiles.History(Drives.NvmeKey, "Samsung SSD 980 PRO 2TB", new OldEntry(T0.AddHours(-1), old)));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(at: T0, poh: 101)));

        var disk = Assert.Single(result.Disks);
        Assert.Equal(BaselineDiskState.Compared, disk.State);
        var media = BaselineAssert.Counter(disk, "nvme.media_errors");
        Assert.Equal(CheckEvidenceCode.BaselineMissingCounter, media.Code);
        Assert.Null(media.Before);
        Assert.True(BaselineAssert.Counter(disk, "nvme.power_on_hours").IsCompared);

        var rows = BaselineAssert.ValidRows(result);
        var row = BaselineAssert.Row(rows, "storage.nvme.media_errors.delta");
        Assert.Null(row.Value);
        Assert.Equal(ItemVerdict.Info, row.Verdict);
        Assert.Equal(CheckReason.Partial, row.Reason);
        Assert.Equal(CheckEvidenceCode.BaselineMissingCounter, row.Evidence!.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_counter_that_does_not_fit_in_64_bits_is_kept_as_the_old_tool_kept_it_and_is_never_compared(bool exactlyTheLimit)
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0, media: 3)));
        var wide = Drives.WithWideMedia(Drives.Nvme(at: H(1), poh: 101), exactlyTheLimit ? ulong.MaxValue : UInt128.MaxValue);

        var result = rig.Run(Drives.Inspect(wide));
        var again = rig.Run(Drives.Inspect(Drives.Nvme(at: H(2), media: 4, poh: 102)));

        var media = BaselineAssert.Counter(result.Disks[0], "nvme.media_errors");
        Assert.Equal(CheckEvidenceCode.BaselineMissingCounter, media.Code);
        Assert.True(BaselineAssert.Counter(result.Disks[0], "nvme.power_on_hours").IsCompared);
        Assert.Contains("\"nvme.media_errors\": 18446744073709551615", rig.Read(NvmeFile));
        // The stored value is clipped, so the run after it cannot tell growth either.
        Assert.Equal(CheckEvidenceCode.BaselineMissingCounter, BaselineAssert.Counter(again.Disks[0], "nvme.media_errors").Code);
    }

    [Fact]
    public void A_counter_the_reader_could_not_represent_exactly_is_not_compared()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0, written: 1000)));
        var drive = Drives.WithWrittenUnits(Drives.Nvme(at: H(1), poh: 101), 1500, unrepresentable: true);

        var result = rig.Run(Drives.Inspect(drive));

        Assert.Equal(CheckEvidenceCode.BaselineMissingCounter, BaselineAssert.Counter(result.Disks[0], "nvme.data_units_written").Code);
        Assert.Equal(BaselineDiskState.Compared, result.Disks[0].State);
    }

    [Fact]
    public void A_counter_the_earlier_record_did_not_have_gives_no_delta_for_that_row_only()
    {
        using var rig = new BaselineRig();
        var old = new Dictionary<string, ulong>(OldFiles.Nvme(poh: 100));
        old.Remove("nvme.unsafe_shutdowns");
        OldFiles.Put(rig.Baseline, NvmeFile, OldFiles.History(Drives.NvmeKey, "Samsung SSD 980 PRO 2TB", new OldEntry(T0.AddHours(-1), old)));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(at: T0, poh: 101)));

        var disk = result.Disks[0];
        Assert.Equal(BaselineDiskState.Compared, disk.State);
        Assert.Equal(CheckEvidenceCode.BaselineMissingCounter, BaselineAssert.Counter(disk, "nvme.unsafe_shutdowns").Code);
        Assert.Equal(7, disk.Counters.Count(static c => c.IsCompared));
        BaselineAssert.ValidRows(result);
    }

    // ---- damaged and unavailable files ----

    [Fact]
    public void A_damaged_record_is_kept_aside_and_the_disk_starts_again()
    {
        using var rig = new BaselineRig();
        OldFiles.Put(rig.Baseline, NvmeFile, "{ this is not a history");

        var result = rig.Run(Drives.Inspect(Drives.Nvme()));

        Assert.Equal(CheckEvidenceCode.BaselineFirst, result.Disks[0].Code);
        Assert.False(result.ReadFailed);
        Assert.Equal("{ this is not a history", rig.Read(NvmeFile + ".bad-20261120T090000123Z"));
        Assert.Equal(1, Entries(rig.Read(NvmeFile)));
    }

    [Fact]
    public void A_file_that_holds_another_key_than_its_name_says_is_damaged_too()
    {
        using var rig = new BaselineRig();
        OldFiles.Put(rig.Baseline, NvmeFile, OldFiles.History(Drives.SataKey, "x", new OldEntry(T0.AddHours(-1), OldFiles.Nvme())));

        var result = rig.Run(Drives.Inspect(Drives.Nvme()));

        Assert.Equal(CheckEvidenceCode.BaselineFirst, result.Disks[0].Code);
        Assert.Contains(NvmeFile + ".bad-20261120T090000123Z", rig.Names());
    }

    [Fact]
    public void A_record_that_cannot_be_read_now_fails_the_disk_and_is_left_alone()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0)));
        var before = rig.Read(NvmeFile);
        var ticket = rig.Engine.BeginRun();
        rig.Faults.ReadFault = path => Path.GetFileName(path) == NvmeFile ? new IOException("sharing violation") : null;

        var result = rig.Engine.Complete(ticket, Drives.Inspect(Drives.Nvme(at: H(1), poh: 101)));

        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, result.Disks[0].Code);
        Assert.True(result.ReadFailed);
        Assert.All(result.Disks[0].Counters, static c => Assert.Equal(CheckEvidenceCode.BaselineReadFailed, c.Code));
        BaselineAssert.ValidRows(result);
        rig.Faults.ReadFault = null;
        Assert.Equal(before, rig.Read(NvmeFile));
        Assert.DoesNotContain(rig.Names(), static n => n.Contains(".bad-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_record_that_cannot_be_written_fails_the_disk_and_leaves_the_old_file_and_no_temporary_file()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0)));
        var before = rig.Read(NvmeFile);
        rig.Files.ReplaceFault = (_, destination) => Path.GetFileName(destination) == NvmeFile ? new IOException("disk full") : null;

        var result = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 101)));

        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, result.Disks[0].Code);
        Assert.True(result.ReadFailed);
        Assert.Equal(before, rig.Read(NvmeFile));
        Assert.DoesNotContain(rig.Names(), static n => n.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void A_write_that_fails_a_few_times_is_retried_and_then_succeeds()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0)));
        var failures = 0;
        rig.Files.ReplaceFault = (_, destination) => Path.GetFileName(destination) == NvmeFile && failures++ < 2 ? new IOException("held by a scanner") : null;

        var result = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 101)));

        Assert.Equal(BaselineDiskState.Compared, result.Disks[0].State);
        Assert.False(result.ReadFailed);
        Assert.Equal(2, Entries(rig.Read(NvmeFile)));
    }

    [Fact]
    public void Nothing_is_written_through_a_record_that_is_older_than_the_same_disks_newer_run()
    {
        using var rig = new BaselineRig();
        OldFiles.Put(rig.Baseline, NvmeFile, OldFiles.History(Drives.NvmeKey, "Samsung SSD 980 PRO 2TB", new OldEntry(T0.AddHours(-1), OldFiles.Nvme(), Acq: 50)));

        // The sequence is rebuilt from the largest acq on disk, so a run now is later than the record, never earlier.
        var result = rig.Run(Drives.Inspect(Drives.Nvme(at: T0, poh: 101)));

        Assert.Equal(BaselineDiskState.Compared, result.Disks[0].State);
        Assert.Contains("\"seq\": 51", rig.Read("acq-seq.json"));
        Assert.Contains("\"acq\": 51", rig.Read(NvmeFile));
    }
}
