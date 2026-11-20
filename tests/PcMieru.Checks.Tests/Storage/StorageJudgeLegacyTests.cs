using PcMieru.Checks.Storage;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;
using static PcMieru.Checks.Tests.Storage.J;

namespace PcMieru.Checks.Tests.Storage;

/// <summary>
/// The cases of the v0.7.4 storage judge, re-expressed over <see cref="StorageInspectionDrive"/> and the new rows. They are not the old
/// tests word for word: the old judge returned one verdict and a list of findings per disk, the new one a row per reading. Where the two
/// differ, DESIGN-A §14.4 wins and the test says so.
/// </summary>
public sealed class StorageJudgeLegacyTests
{
    private static CheckVerdict WorstOf(StorageInspectionDrive drive, BaselineDiskOutcome? baseline = null) =>
        Overall(StorageJudge.Rows(drive, baseline ?? First(), Subject));

    // ---- NVMe ----

    [Fact]
    public void A_critical_warning_that_is_not_zero_is_a_check_for_the_disk()
    {
        var rows = Rows(Nvme(critical: 1));

        Is(ItemVerdict.Check, rows["storage.nvme.critical_warning"]);
        Assert.DoesNotContain("storage.health", rows.Keys); // no roll-up row (DESIGN-A §13): the verdict comes from the rows themselves
        Assert.Equal(CheckVerdict.Check, WorstOf(Nvme(critical: 1)));
    }

    [Fact]
    public void A_disk_with_nothing_wrong_is_ok_overall()
    {
        Assert.Equal(CheckVerdict.Ok, WorstOf(Nvme()));
    }

    [Theory]
    [InlineData(5.0, 4.0, ItemVerdict.Check)]   // spare below the threshold
    [InlineData(5.0, 5.0, ItemVerdict.Caution)] // not below it, but within ten points
    [InlineData(5.0, 14.0, ItemVerdict.Caution)]
    [InlineData(5.0, 15.0, ItemVerdict.Ok)]
    [InlineData(0.0, 9.0, ItemVerdict.Caution)] // a threshold of 0 is no threshold: only the ten points count
    [InlineData(0.0, 10.0, ItemVerdict.Ok)]
    public void Spare_boundaries_of_the_old_judge(double threshold, double spare, ItemVerdict expected)
    {
        Is(expected, Rows(Nvme(spare: spare, threshold: threshold))["storage.nvme.available_spare"]);
    }

    [Theory]
    [InlineData(89.0, ItemVerdict.Ok)]
    [InlineData(90.0, ItemVerdict.Caution)]
    [InlineData(99.0, ItemVerdict.Caution)]
    [InlineData(100.0, ItemVerdict.Check)]
    public void Wear_boundaries_of_the_old_judge(double used, ItemVerdict expected)
    {
        Is(expected, Rows(Nvme(used: used))["storage.nvme.percentage_used"]);
    }

    [Fact]
    public void Media_errors_seen_for_the_first_time_are_a_caution()
    {
        // The old judge: "errors, and nothing to say whether they are new": caution. §14.4: not comparable gives caution.
        var rows = Rows(Nvme(media: 3), First());

        Is(ItemVerdict.Caution, rows["storage.nvme.media_errors"]);
        Assert.Equal(CheckVerdict.Caution, WorstOf(Nvme(media: 3)));
    }

    [Fact]
    public void Media_errors_that_grew_since_the_last_run_are_a_check()
    {
        var rows = Rows(Nvme(media: 5), Compared(Counter("nvme.media_errors", 3, 5)));

        Is(ItemVerdict.Check, rows["storage.nvme.media_errors"]);
        Is(ItemVerdict.Check, rows["storage.nvme.media_errors.delta"]);
        Assert.Equal(CheckVerdict.Check, Overall(StorageJudge.Rows(Nvme(media: 5), Compared(Counter("nvme.media_errors", 3, 5)), Subject)));
    }

    [Fact]
    public void Media_errors_that_are_the_same_as_the_last_run_are_historical_and_ok()
    {
        var rows = Rows(Nvme(media: 3), Compared(Counter("nvme.media_errors", 3, 3)));

        Is(ItemVerdict.Ok, rows["storage.nvme.media_errors"]);
        Is(ItemVerdict.Ok, rows["storage.nvme.media_errors.delta"]);
        Assert.Equal(CheckVerdict.Ok, Overall(StorageJudge.Rows(Nvme(media: 3), Compared(Counter("nvme.media_errors", 3, 3)), Subject)));
    }

    [Fact]
    public void A_media_error_in_the_error_log_is_a_caution_even_when_nothing_grew()
    {
        var drive = Nvme(errorLog: 4, media: 0).EditCounters(c => c with { RecentErrorLog = new StorageErrorLogCounts(EntriesRead: 4, MediaErrors: 1) });

        var rows = Rows(drive, Compared(Counter("nvme.error_log_entries", 4, 4)));

        Is(ItemVerdict.Caution, rows["storage.nvme.error_log_entries"]);
        Assert.Equal(CheckEvidenceCode.MediaErrorsInLog, rows["storage.nvme.error_log_entries"].Evidence!.Code);
        Assert.Equal(CheckVerdict.Caution, Overall(StorageJudge.Rows(drive, Compared(Counter("nvme.error_log_entries", 4, 4)), Subject)));
    }

    // ---- SATA ----

    private static StorageInspectionDrive SataWithEightPending() =>
        Ata(
            [
                Attr(5, 0),
                Attr(9, 12000),
                Attr(194, 38),
                Attr(197, 8),
                Attr(199, 3),
            ]);

    [Fact]
    public void A_sata_disk_with_eight_pending_sectors_and_three_crc_errors_is_judged_row_by_row()
    {
        var rows = Rows(SataWithEightPending(), First());

        Is(ItemVerdict.Check, rows["storage.ata.197"]);
        Is(ItemVerdict.Ok, rows["storage.ata.5"]);
        Is(ItemVerdict.Caution, rows["storage.ata.199"]);
        Assert.Equal(CheckEvidenceCode.LikelyCable, rows["storage.ata.199"].Evidence!.Code);
        Is(ItemVerdict.Ok, rows["storage.ata.threshold_status"]);
        IsNumber(38, rows["storage.temperature"]);
        Is(ItemVerdict.Ok, rows["storage.temperature"]);
        IsDec(12000, rows["storage.ata.9"]);

        Assert.DoesNotContain("storage.health", rows.Keys);
        Assert.Equal(CheckVerdict.Check, WorstOf(SataWithEightPending()));
    }

    [Fact]
    public void Pending_sectors_that_did_not_grow_are_still_a_check()
    {
        var rows = Rows(SataWithEightPending(), Compared(Counter("ata.197", 8, 8)));

        Is(ItemVerdict.Check, rows["storage.ata.197"]);
        Assert.Null(rows["storage.ata.197"].Evidence!.Code);
        Is(ItemVerdict.Ok, rows["storage.ata.197.delta"]);
    }

    [Fact]
    public void A_threshold_failure_is_a_check()
    {
        var drive = Ata([Attr(194, 38), Attr(5, 0, current: 20, worst: 20, threshold: 36)]);

        var rows = Rows(drive);

        Is(ItemVerdict.Check, rows["storage.ata.threshold_status"]);
        Assert.Equal(CheckVerdict.Check, WorstOf(drive));
    }

    [Fact]
    public void Predicted_failure_of_the_drive_is_a_check()
    {
        Assert.Equal(CheckVerdict.Check, WorstOf(Ata([Attr(194, 38)], predictFailure: true)));
    }

    // ---- disks that cannot be judged, and the overall verdict ----

    [Fact]
    public void A_usb_disk_without_health_is_unavailable()
    {
        var usb = Drives.NoHealth() with { BusType = 7 };

        var rows = StorageJudge.Rows(usb, First(), Subject);

        AssertValid(rows);
        Assert.DoesNotContain(rows, static r => r.Id == "storage.health");
        var temperature = Assert.Single(rows, static r => r.Id == "storage.temperature");
        Assert.Equal(ItemVerdict.Unavailable, temperature.Verdict);
        Assert.Equal(CheckVerdict.Unavailable, Overall(rows));
    }

    [Fact]
    public void The_overall_verdict_follows_the_ranking_check_caution_unavailable_ok()
    {
        // v0.7.4 left an unavailable disk out of the overall verdict, so a healthy disk and a USB disk were "ok". DESIGN-A §3 ranks
        // unavailable above ok: a result with a disk nobody could look at is not "ok". This is an intended change.
        var healthy = StorageJudge.Rows(Nvme(), First(), Subject);
        var usb = StorageJudge.Rows(Drives.NoHealth() with { BusType = 7 }, First(), new CheckSubject("disk-2", null));

        Assert.Equal(CheckVerdict.Ok, Overall(healthy));
        Assert.Equal(CheckVerdict.Unavailable, Overall(healthy, usb));

        var caution = StorageJudge.Rows(Nvme(used: 95), First(), new CheckSubject("disk-3", null));
        Assert.Equal(CheckVerdict.Caution, Overall(healthy, usb, caution));

        var check = StorageJudge.Rows(Nvme(critical: 1), First(), new CheckSubject("disk-4", null));
        Assert.Equal(CheckVerdict.Check, Overall(healthy, usb, caution, check));
    }

    [Fact]
    public void An_unreadable_slot_next_to_a_strong_healthy_disk_is_unavailable_overall()
    {
        var slot = Drives.Unreadable();

        var rows = StorageJudge.Rows(slot, First(1), new CheckSubject("disk-2", null));

        Assert.Equal(CheckVerdict.Unavailable, Overall(StorageJudge.Rows(Nvme(), First(), Subject), rows));
    }
}
