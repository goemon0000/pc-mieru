using System.Numerics;
using PcMieru.Checks.Storage;

namespace PcMieru.Checks.Tests.Storage;

/// <summary>
/// The ".delta" rows built from a disk's outcome (DESIGN-A §3.3, §3.5-5 to 7, §14.5): every row passes the strict validator, and what a row
/// says (verdict, value, evidence) follows from the outcome alone.
/// </summary>
public class BaselineDeltaRowsTests
{
    private static readonly DateTimeOffset Since = new(2026, 11, 20, 6, 0, 0, 123, TimeSpan.Zero);

    private static readonly CheckSubject Subject = new("disk-1", "Samsung SSD 980 PRO 2TB");

    private static BaselineCounterOutcome Compared(string name, ulong before, ulong after, int? newBits = null) =>
        new("storage." + name, null, before, after, Since, newBits);

    private static BaselineCounterOutcome NotCompared(string name, CheckEvidenceCode code) =>
        new("storage." + name, code, null, null, null, null);

    private static CheckItem RowOf(BaselineCounterOutcome counter) => Validated(BaselineDeltaRows.Row(counter, Subject)).Single();

    private static IReadOnlyList<CheckItem> Validated(params CheckItem[] items)
    {
        var check = CheckResult.Create(Samples.App, Samples.StorageRun, CheckKind.Storage, Samples.Start, Samples.Start.AddSeconds(1), CheckOutcome.Completed, items);
        var problems = CheckResultValidator.Validate(check);
        Assert.True(problems.Count == 0, string.Join(" | ", problems));
        return items;
    }

    private static BaselineDiskOutcome Disk(BaselineDiskState state, CheckEvidenceCode? code, params BaselineCounterOutcome[] counters) =>
        new(0, "disk-1", state, code, counters);

    // ---- not comparable ----

    [Fact]
    public void Every_baseline_code_makes_every_row_of_the_disk_an_info_row_without_a_value_and_with_the_code_only()
    {
        var codes = Enum.GetValues<CheckEvidenceCode>().Where(CheckEvidenceCodes.IsBaseline).ToList();
        Assert.Equal(11, codes.Count);

        foreach (var code in codes)
        {
            var disk = Disk(
                BaselineDiskState.NotComparable,
                code,
                BaselineCounters.DeltaCounters.Select(name => NotCompared(name, code)).ToArray());

            var rows = Validated(BaselineDeltaRows.Build(disk, Subject).ToArray());

            Assert.Equal(15, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.Null(row.Value);
                Assert.Equal(ItemVerdict.Info, row.Verdict);
                Assert.Equal(CheckSource.Baseline, row.Source);
                Assert.Equal(CheckReason.Partial, row.Reason);
                Assert.Equal(code, row.Evidence!.Code);
                Assert.Null(row.Evidence.Before);
                Assert.Null(row.Evidence.After);
                Assert.Null(row.Evidence.SinceUtc);
                Assert.Equal(Subject, row.Subject);
            });
        }
    }

    [Fact]
    public void A_missing_counter_makes_only_its_own_row_not_comparable()
    {
        var disk = Disk(
            BaselineDiskState.Compared,
            null,
            Compared("nvme.media_errors", 0, 0),
            NotCompared("nvme.power_on_hours", CheckEvidenceCode.BaselineMissingCounter),
            Compared("nvme.unsafe_shutdowns", 5, 6));

        var rows = Validated(BaselineDeltaRows.Build(disk, Subject).ToArray());

        Assert.Equal(ItemVerdict.Ok, rows[0].Verdict);
        Assert.Null(rows[1].Value);
        Assert.Equal(CheckEvidenceCode.BaselineMissingCounter, rows[1].Evidence!.Code);
        Assert.Equal(CheckValue.SignedDec(1), rows[2].Value);
    }

    [Fact]
    public void A_disk_without_counters_gives_no_rows_and_the_rows_follow_the_outcomes_order()
    {
        Assert.Empty(BaselineDeltaRows.Build(Disk(BaselineDiskState.NoCounters, null), Subject));

        var disk = Disk(
            BaselineDiskState.Compared,
            null,
            Compared("ata.5", 0, 0),
            Compared("nvme.media_errors", 0, 0),
            Compared("ata.9", 1, 2));
        Assert.Equal(
            new[] { "storage.ata.5.delta", "storage.nvme.media_errors.delta", "storage.ata.9.delta" },
            BaselineDeltaRows.Build(disk, Subject).Select(static r => r.Id).ToArray());
    }

    // ---- compared ----

    [Fact]
    public void A_judged_counter_that_grew_is_check_or_caution_with_the_signed_change_and_what_it_rests_on()
    {
        var media = RowOf(Compared("nvme.media_errors", 2, 7));
        Assert.Equal("storage.nvme.media_errors.delta", media.Id);
        Assert.Equal(ItemVerdict.Check, media.Verdict);
        Assert.Equal(CheckValue.SignedDec(5), media.Value);
        Assert.Null(media.Reason);
        Assert.Equal(CheckSource.Baseline, media.Source);
        Assert.Equal(CheckEvidenceCode.CounterIncreased, media.Evidence!.Code);
        Assert.Equal("2", media.Evidence.Before);
        Assert.Equal("7", media.Evidence.After);
        Assert.Equal(Since, media.Evidence.SinceUtc);
        Assert.Null(media.Evidence.NewBits);

        var errorLog = RowOf(Compared("nvme.error_log_entries", 0, 1));
        Assert.Equal(ItemVerdict.Caution, errorLog.Verdict);
        Assert.Equal(CheckEvidenceCode.CounterIncreased, errorLog.Evidence!.Code);

        foreach (var id in new[] { 5, 187, 196, 197, 198 })
        {
            Assert.Equal(ItemVerdict.Check, RowOf(Compared("ata." + id, 0, 1)).Verdict);
        }
    }

    [Fact]
    public void Crc_errors_that_grew_say_caution_and_likely_cable()
    {
        var row = RowOf(Compared("ata.199", 3, 40));

        Assert.Equal(ItemVerdict.Caution, row.Verdict);
        Assert.Equal(CheckEvidenceCode.LikelyCable, row.Evidence!.Code);
        Assert.Equal(CheckValue.SignedDec(37), row.Value);
    }

    [Fact]
    public void The_critical_warning_says_which_bits_came_up()
    {
        var row = RowOf(Compared("nvme.critical_warning", 1, 3, newBits: 2));

        Assert.Equal(ItemVerdict.Check, row.Verdict);
        Assert.Equal(CheckEvidenceCode.NewBits, row.Evidence!.Code);
        Assert.Equal(2, row.Evidence.NewBits);
        Assert.Equal(CheckValue.SignedDec(2), row.Value);
    }

    [Fact]
    public void A_critical_warning_bit_that_went_away_while_another_came_up_still_says_check()
    {
        var row = RowOf(Compared("nvme.critical_warning", 4, 1, newBits: 1));

        Assert.Equal(ItemVerdict.Check, row.Verdict);
        Assert.Equal(CheckEvidenceCode.NewBits, row.Evidence!.Code);
        Assert.Equal(1, row.Evidence.NewBits);
        Assert.Equal(CheckValue.SignedDec(-3), row.Value);
    }

    [Fact]
    public void A_critical_warning_with_no_new_bit_is_ok_and_says_decreased_when_the_number_went_down()
    {
        var same = RowOf(Compared("nvme.critical_warning", 3, 3, newBits: 0));
        Assert.Equal(ItemVerdict.Ok, same.Verdict);
        Assert.Null(same.Evidence!.Code);
        Assert.Null(same.Evidence.NewBits);

        var down = RowOf(Compared("nvme.critical_warning", 3, 1, newBits: 0));
        Assert.Equal(ItemVerdict.Ok, down.Verdict);
        Assert.Equal(CheckEvidenceCode.CounterDecreased, down.Evidence!.Code);
    }

    [Fact]
    public void A_judged_counter_that_did_not_grow_is_ok_and_says_decreased_only_when_it_went_down()
    {
        var same = RowOf(Compared("nvme.media_errors", 4, 4));
        Assert.Equal(ItemVerdict.Ok, same.Verdict);
        Assert.Equal(CheckValue.SignedDec(0), same.Value);
        Assert.Null(same.Evidence!.Code);
        Assert.Equal("4", same.Evidence.Before);
        Assert.Equal("4", same.Evidence.After);

        var down = RowOf(Compared("ata.5", 9, 2));
        Assert.Equal(ItemVerdict.Ok, down.Verdict);
        Assert.Equal(CheckValue.SignedDec(-7), down.Value);
        Assert.Equal(CheckEvidenceCode.CounterDecreased, down.Evidence!.Code);
    }

    [Theory]
    [InlineData("nvme.available_spare", 100, 90)]
    [InlineData("nvme.percentage_used", 3, 4)]
    [InlineData("nvme.unsafe_shutdowns", 5, 9)]
    [InlineData("nvme.unsafe_shutdowns", 9, 5)]
    [InlineData("nvme.power_on_hours", 100, 140)]
    [InlineData("nvme.data_units_written", 1000, 5000)]
    [InlineData("ata.9", 1000, 1010)]
    public void A_counter_that_only_informs_stays_info_with_its_change_whichever_way_it_goes(string name, ulong before, ulong after)
    {
        var row = RowOf(Compared(name, before, after));

        Assert.Equal(ItemVerdict.Info, row.Verdict);
        Assert.Equal(CheckValue.SignedDec(new BigInteger(after) - new BigInteger(before)), row.Value);
        Assert.Null(row.Evidence!.Code);
        Assert.Equal(before.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Evidence.Before);
        Assert.Equal(after.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Evidence.After);
    }

    [Fact]
    public void The_change_can_be_as_large_as_a_64_bit_counter_in_either_direction()
    {
        var up = RowOf(Compared("nvme.data_units_written", 0, ulong.MaxValue - 1));
        var down = RowOf(Compared("ata.5", ulong.MaxValue - 1, 0));

        Assert.Equal(CheckValue.SignedDec(new BigInteger(ulong.MaxValue - 1)), up.Value);
        Assert.Equal(CheckValue.SignedDec(-new BigInteger(ulong.MaxValue - 1)), down.Value);
    }

    // ---- the outcome itself ----

    [Fact]
    public void An_outcome_is_compared_only_without_a_code_and_grew_means_after_above_before()
    {
        Assert.True(Compared("ata.5", 1, 2).IsCompared);
        Assert.False(NotCompared("ata.5", CheckEvidenceCode.BaselineFirst).IsCompared);
        Assert.True(Compared("ata.5", 1, 2).Grew);
        Assert.False(Compared("ata.5", 2, 2).Grew);
        Assert.False(Compared("ata.5", 3, 2).Grew);
        Assert.False(NotCompared("ata.5", CheckEvidenceCode.BaselineFirst).Grew);
        Assert.Equal(BigInteger.Zero, NotCompared("ata.5", CheckEvidenceCode.BaselineFirst).Change);
        Assert.Equal(new BigInteger(-1), Compared("ata.5", 3, 2).Change);
        Assert.Equal("storage.ata.5.delta", Compared("ata.5", 1, 2).DeltaId);
    }

    [Fact]
    public void A_critical_warning_grew_means_a_new_bit_not_a_bigger_number()
    {
        Assert.True(Compared("nvme.critical_warning", 4, 1, newBits: 1).Grew);
        Assert.False(Compared("nvme.critical_warning", 1, 3, newBits: 0).Grew);
    }

    [Fact]
    public void The_builders_refuse_a_missing_outcome_or_subject()
    {
        Assert.Throws<ArgumentNullException>(() => BaselineDeltaRows.Build(null!, Subject));
        Assert.Throws<ArgumentNullException>(() => BaselineDeltaRows.Build(Disk(BaselineDiskState.NoCounters, null), null!));
        Assert.Throws<ArgumentNullException>(() => BaselineDeltaRows.Row(null!, Subject));
        Assert.Throws<ArgumentNullException>(() => BaselineDeltaRows.Row(Compared("ata.5", 1, 2), null!));
    }
}
