using PcMieru.Checks.Storage;
using PcMieru.Checks.Tests.Storage;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;
using static PcMieru.Checks.Tests.Storage.J;

namespace PcMieru.Checks.Tests.Run;

/// <summary>
/// The storage check end to end: a scripted source in place of the drives, a baseline and a history store on a fresh temp folder, the run
/// framework in between. The expectations are DESIGN-A §3.3, §14.0, §14.4 and §14.5 written out, not computed with the code under test.
/// </summary>
public sealed class StorageCheckRunTests
{
    private static readonly DateTimeOffset T0 = Drives.T0;

    private static DateTimeOffset H(double hours) => T0.AddHours(hours);

    private static CheckItem Row(CheckRun run, string id, string? subject = null) =>
        run.Result.Items.Single(i => i.Id == id && i.Subject?.Id == subject);

    private static IEnumerable<CheckItem> RowsOf(CheckRun run, string subject) => run.Result.Items.Where(i => i.Subject?.Id == subject);

    // The decimal text of an integer-like value (the evidence keeps numbers as text).
    private static string TextOf(CheckValue value) =>
        value.Type == CheckValueType.Int ? value.AsInt.ToString(System.Globalization.CultureInfo.InvariantCulture) : value.AsText;

    private static async Task<CheckRun> RunOnce(RunRig rig, params StorageInspectionDrive[] drives)
    {
        rig.Source.Returns(Drives.Inspect(drives));
        return await rig.RunAsync();
    }

    // ---- the first run ----

    [Fact]
    public async Task The_first_run_of_an_nvme_and_a_sata_disk_has_every_row_and_every_delta_says_first()
    {
        using var rig = new RunRig();

        var run = await RunOnce(rig, Nvme(), HealthyAta());

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Equal(CheckKind.Storage, run.Result.Kind);
        Assert.Equal(Samples.App, run.Result.App);
        Assert.Equal(Kit.Id(1), run.Result.RunId);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Empty(run.Result.Notes);

        var devices = Row(run, "storage.devices");
        Assert.Equal(CheckValue.Int(2), devices.Value);
        Assert.Equal(ItemVerdict.Info, devices.Verdict);
        Assert.Null(devices.Reason);
        Assert.Null(devices.Subject);

        // Two disks, numbered in the order the baseline met them.
        Assert.Equal(["disk-1", "disk-2"], run.Result.Items.Where(i => i.Subject is not null).Select(i => i.Subject!.Id).Distinct());
        Assert.Equal("Samsung SSD 980 PRO 2TB", Row(run, "storage.capacity", "disk-1").Subject!.Model);
        Assert.Equal("WDC WD40EZRZ-00GXCB0", Row(run, "storage.capacity", "disk-2").Subject!.Model);

        // DESIGN-A §14.4 / §14.1 (step 5b): Windows' own status of each disk is ONE row of that disk, the first of its rows.
        var health = run.Result.Items.Where(static i => i.Id == "storage.health").ToList();
        Assert.Equal(["disk-1", "disk-2"], health.Select(static i => i.Subject!.Id));
        Assert.All(health, static h =>
        {
            Assert.Equal(ItemVerdict.Ok, h.Verdict);
            Assert.Equal(CheckValue.Text("healthy"), h.Value);
            Assert.Equal(CheckSource.Wmi, h.Source);
        });
        Assert.Equal("storage.health", RowsOf(run, "disk-1").First().Id);
        Assert.Equal("storage.health", RowsOf(run, "disk-2").First().Id);

        // The NVMe disk: its 8 compared counters, all first.
        var nvmeDeltas = RowsOf(run, "disk-1").Where(static i => i.Id.EndsWith(".delta", StringComparison.Ordinal)).ToList();
        Assert.Equal(8, nvmeDeltas.Count);
        var sataDeltas = RowsOf(run, "disk-2").Where(static i => i.Id.EndsWith(".delta", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "storage.ata.197.delta", "storage.ata.199.delta", "storage.ata.5.delta", "storage.ata.9.delta" }, sataDeltas.Select(static i => i.Id).Order(StringComparer.Ordinal));
        Assert.All(
            nvmeDeltas.Concat(sataDeltas),
            static d =>
            {
                Assert.Equal(ItemVerdict.Info, d.Verdict);
                Assert.Null(d.Value);
                Assert.Equal(CheckReason.Partial, d.Reason);
                Assert.Equal(CheckSource.Baseline, d.Source);
                Assert.Equal(CheckEvidenceCode.BaselineFirst, d.Evidence!.Code);
            });

        // Nothing is wrong with these two disks, so every judged row is ok and the verdict is ok (§14.0).
        Assert.Equal(CheckVerdict.Ok, run.Result.Verdict);
        Assert.Equal(0, run.Result.Counts.Caution);
        Assert.Equal(0, run.Result.Counts.Check);
        Assert.Equal(0, run.Result.Counts.Unavailable);
        Assert.Equal(run.Result.Items.Count, run.Result.Counts.Total);
        Assert.Equal(run.Result.Items.Count(static i => i.Verdict == ItemVerdict.Info), run.Result.Counts.Info);
    }

    [Fact]
    public async Task The_first_run_is_recorded_by_the_baseline_and_saved_as_a_file_that_reads_back_the_same()
    {
        using var rig = new RunRig();

        var run = await RunOnce(rig, Nvme());

        Assert.Equal(CheckSaveStatus.Saved, run.Save.Status);
        Assert.Equal(Kit.NameOf(run.Result), run.Save.FileName);
        Assert.Equal([Kit.NameOf(run.Result)], Kit.Names(rig.HistoryRoot));
        Assert.Single(rig.Baseline.DiskFiles());

        var back = await rig.ReadBackAsync(run.Result.RunId);
        Assert.NotNull(back);
        Assert.Equal(Kit.Bytes(run.Result), Kit.Bytes(back!));
    }

    [Fact]
    public async Task The_run_times_come_from_the_clock_cut_to_milliseconds()
    {
        using var rig = new RunRig();
        rig.Time.Now = new DateTimeOffset(2026, 11, 20, 9, 0, 0, 123, TimeSpan.Zero).AddTicks(4567);
        rig.Source.Returns(Drives.Inspect(Nvme()), during: () => rig.Time.Now = rig.Time.Now.AddSeconds(2));

        var run = await rig.RunAsync();

        Assert.Equal(new DateTimeOffset(2026, 11, 20, 9, 0, 0, 123, TimeSpan.Zero), run.Result.StartedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 9, 0, 2, 123, TimeSpan.Zero), run.Result.EndedAtUtc);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
    }

    // ---- the second run ----

    [Fact]
    public async Task The_second_run_compares_and_the_growth_decides_the_verdicts()
    {
        using var rig = new RunRig();
        await RunOnce(rig, Drives.Nvme(at: T0, media: 0, errorLog: 0, critical: 0), HealthyAta());
        rig.Time.Now = H(1);

        var run = await RunOnce(rig, Drives.Nvme(at: H(1), media: 2, errorLog: 3, poh: 101, critical: 0), HealthyAta(Attr(5, 4), Attr(199, 3)) with { SampledAt = H(1) });

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Empty(CheckResultValidator.Validate(run.Result));

        // NVMe: media errors 0 -> 2 is a check, with the two numbers; the error log growing is a caution.
        var media = Row(run, "storage.nvme.media_errors", "disk-1");
        Assert.Equal(ItemVerdict.Check, media.Verdict);
        Assert.Equal(CheckEvidenceCode.CounterIncreased, media.Evidence!.Code);
        Assert.Equal("0", media.Evidence.Before);
        Assert.Equal("2", media.Evidence.After);
        Assert.Equal(T0, media.Evidence.SinceUtc);

        var mediaDelta = Row(run, "storage.nvme.media_errors.delta", "disk-1");
        Assert.Equal(CheckValue.SignedDec(2), mediaDelta.Value);
        Assert.Equal(ItemVerdict.Check, mediaDelta.Verdict);
        Assert.Null(mediaDelta.Reason);
        Assert.Equal(CheckEvidenceCode.CounterIncreased, mediaDelta.Evidence!.Code);

        Assert.Equal(ItemVerdict.Caution, Row(run, "storage.nvme.error_log_entries", "disk-1").Verdict);
        Assert.Equal(ItemVerdict.Caution, Row(run, "storage.nvme.error_log_entries.delta", "disk-1").Verdict);

        // The counters that move all the time are information, with the difference.
        var hours = Row(run, "storage.nvme.power_on_hours.delta", "disk-1");
        Assert.Equal(ItemVerdict.Info, hours.Verdict);
        Assert.Equal(CheckValue.SignedDec(1), hours.Value);

        // SATA: reallocated 0 -> 4 grew (check); CRC 0 -> 3 grew (caution, cable); pending is 0 (ok).
        var reallocated = Row(run, "storage.ata.5", "disk-2");
        Assert.Equal(ItemVerdict.Check, reallocated.Verdict);
        var crc = Row(run, "storage.ata.199", "disk-2");
        Assert.Equal(ItemVerdict.Caution, crc.Verdict);
        Assert.Equal(CheckEvidenceCode.LikelyCable, crc.Evidence!.Code);
        Assert.Equal(CheckEvidenceCode.LikelyCable, Row(run, "storage.ata.199.delta", "disk-2").Evidence!.Code);
        Assert.Equal(ItemVerdict.Ok, Row(run, "storage.ata.197", "disk-2").Verdict);

        // The overall verdict is the worst of all (check > caution > unavailable > ok), from the rows themselves: the health is one row of
        // each disk (healthy here) and no roll-up row is added, so nothing is counted twice. The checks are the media errors and the
        // reallocated sectors, each with its delta.
        Assert.Equal(2, run.Result.Items.Count(static i => i.Id == "storage.health"));
        Assert.All(run.Result.Items.Where(static i => i.Id == "storage.health"), static h => Assert.Equal(ItemVerdict.Ok, h.Verdict));
        Assert.Equal(CheckVerdict.Check, run.Result.Verdict);
        Assert.Equal(4, run.Result.Counts.Check);
        Assert.Equal(run.Result.Items.Count(static i => i.Verdict == ItemVerdict.Check), run.Result.Counts.Check);

        // No row of a compared disk says first any more.
        Assert.DoesNotContain(run.Result.Items, static i => i.Evidence?.Code == CheckEvidenceCode.BaselineFirst);
    }

    [Fact]
    public async Task The_second_run_without_growth_is_ok_and_the_deltas_are_zero_or_small_and_never_first()
    {
        using var rig = new RunRig();
        await RunOnce(rig, Drives.Nvme(at: T0, media: 3));
        rig.Time.Now = H(1);

        var run = await RunOnce(rig, Drives.Nvme(at: H(1), media: 3, poh: 101));

        var media = Row(run, "storage.nvme.media_errors", "disk-1");
        Assert.Equal(ItemVerdict.Ok, media.Verdict); // the 3 errors are old: they did not grow
        Assert.Null(media.Evidence!.Code);
        Assert.Equal(CheckValue.SignedDec(0), Row(run, "storage.nvme.media_errors.delta", "disk-1").Value);
        Assert.Equal(CheckVerdict.Ok, run.Result.Verdict);
    }

    [Fact]
    public async Task A_base_row_and_its_delta_always_agree_on_the_after_value()
    {
        using var rig = new RunRig();
        await RunOnce(rig, Drives.Nvme(at: T0, media: 1), HealthyAta(Attr(5, 7), Attr(9, 100)));
        rig.Time.Now = H(1);

        var run = await RunOnce(rig, Drives.Nvme(at: H(1), media: 4, poh: 102), HealthyAta(Attr(5, 9), Attr(9, 130)) with { SampledAt = H(1) });

        foreach (var delta in run.Result.Items.Where(static i => i.Id.EndsWith(".delta", StringComparison.Ordinal) && i.Value is not null))
        {
            var baseId = delta.Id[..^".delta".Length];
            var baseRow = Row(run, baseId, delta.Subject!.Id);
            Assert.NotNull(baseRow.Value);
            Assert.Equal(TextOf(baseRow.Value!), delta.Evidence!.After);
        }
    }

    // ---- unreadable slots with strong and weak disks ----

    [Fact]
    public async Task An_unreadable_slot_beside_a_strong_and_a_weak_disk_keeps_the_strong_one_compared_and_the_weak_one_incomplete()
    {
        using var rig = new RunRig();
        var weak = Drives.Nvme(serial: "", uniqueId: "", number: 1, model: "Unknown NVMe");
        await RunOnce(rig, Drives.Nvme(at: T0), weak, Drives.Unreadable(5, T0));
        rig.Time.Now = H(1);

        var run = await RunOnce(rig, Drives.Nvme(at: H(1), media: 2, poh: 101), weak with { SampledAt = H(1) }, Drives.Unreadable(5, H(1)));

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal(CheckValue.Int(3), Row(run, "storage.devices").Value);
        Assert.Equal(CheckReason.Partial, Row(run, "storage.devices").Reason); // one of the three slots could not be read

        // Strong disk: compared, growth judged.
        Assert.Equal(ItemVerdict.Check, Row(run, "storage.nvme.media_errors", "disk-1").Verdict);
        Assert.Equal(CheckValue.SignedDec(2), Row(run, "storage.nvme.media_errors.delta", "disk-1").Value);

        // Weak disk: judged by its absolute values, and the deltas say that the inventory is incomplete.
        Assert.Equal(ItemVerdict.Ok, Row(run, "storage.nvme.media_errors", "disk-2").Verdict);
        var weakDelta = Row(run, "storage.nvme.media_errors.delta", "disk-2");
        Assert.Null(weakDelta.Value);
        Assert.Equal(CheckEvidenceCode.BaselineInventoryIncomplete, weakDelta.Evidence!.Code);

        // The slot: Windows' health of its drive number (one row, first), and the one row that is unavailable.
        var slotRows = RowsOf(run, "disk-3").ToList();
        Assert.Equal(["storage.health", "storage.temperature"], slotRows.Select(static r => r.Id));
        Assert.Equal(ItemVerdict.Ok, slotRows[0].Verdict);
        var slot = slotRows[1];
        Assert.Equal("storage.temperature", slot.Id); // the row that keeps the slot in the result and the verdict from reading "ok"
        Assert.Equal(CheckReason.Error, slot.Reason);
        Assert.Equal(ItemVerdict.Unavailable, slot.Verdict);
        Assert.Null(slot.Value);
        Assert.Null(slot.Subject!.Model);

        // §3: check beats unavailable.
        Assert.Equal(CheckVerdict.Check, run.Result.Verdict);
    }

    [Fact]
    public async Task An_unreadable_slot_with_only_healthy_disks_makes_the_whole_result_unavailable_and_the_count_partial()
    {
        using var rig = new RunRig();

        var run = await RunOnce(rig, Nvme(), Drives.Unreadable(5));

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Equal(CheckVerdict.Unavailable, run.Result.Verdict);
        Assert.Equal(CheckReason.Partial, Row(run, "storage.devices").Reason);
        Assert.Equal(1, run.Result.Counts.Unavailable);
    }

    [Fact]
    public async Task A_disk_the_system_does_not_tell_the_health_of_is_unavailable_with_its_reason()
    {
        using var rig = new RunRig();
        var raid = Drives.NoHealth() with { Health = Value.Unsupported<StorageHealth>("test"), BusType = 8, Model = "RAID Volume" };

        var run = await RunOnce(rig, raid);

        Assert.Equal(["storage.devices", "storage.health", "storage.capacity", "storage.temperature"], run.Result.Items.Select(static i => i.Id).Where(static id => !id.EndsWith(".delta", StringComparison.Ordinal)));
        Assert.Equal(ItemVerdict.Ok, Row(run, "storage.health", "disk-1").Verdict); // Windows tells the health of a disk the SMART read could not
        var temperature = Row(run, "storage.temperature", "disk-1");
        Assert.Equal(CheckReason.Unsupported, temperature.Reason);
        Assert.Equal(ItemVerdict.Unavailable, temperature.Verdict);
        Assert.Equal(CheckVerdict.Unavailable, run.Result.Verdict);
    }

    // ---- the strong and the weak, and the counts ----

    [Fact]
    public async Task The_counts_and_the_verdict_follow_the_ranking_for_a_mix_of_disks()
    {
        using var rig = new RunRig();

        var run = await RunOnce(rig, Nvme(used: 95), HealthyAta(), Drives.NoHealth("OTHER-SERIAL") with { Health = Value.Denied<StorageHealth>("test") });

        Assert.Equal(CheckVerdict.Caution, run.Result.Verdict);
        var counts = run.Result.Counts;
        Assert.Equal(run.Result.Items.Count(static i => i.Verdict == ItemVerdict.Caution), counts.Caution);
        Assert.Equal(run.Result.Items.Count(static i => i.Verdict == ItemVerdict.Unavailable), counts.Unavailable);
        Assert.Equal(run.Result.Items.Count(static i => i.Verdict == ItemVerdict.Ok), counts.Ok);
        Assert.Equal(0, counts.Check);
        Assert.True(counts.Caution >= 1);
        Assert.Equal(1, counts.Unavailable); // the denied disk's temperature: its health is a row of its own (ok), never counted twice
    }

    // ---- elevation ----

    [Fact]
    public async Task The_source_is_asked_without_elevation_unless_the_request_allows_it()
    {
        using var rig = new RunRig();
        rig.Source.Returns(Drives.Inspect(Nvme()));
        await rig.RunAsync();
        rig.Source.Returns(Drives.Inspect(Nvme()));
        await rig.RunAsync(new CheckRequest(CheckKind.Storage, AllowElevation: false));

        Assert.Equal([false, false], rig.Source.Elevations);
    }

    [Fact]
    public async Task With_elevation_allowed_the_denied_disk_is_read_and_without_it_it_stays_denied()
    {
        using var rig = new RunRig();
        var denied = Drives.NoHealth() with { Health = Value.Denied<StorageHealth>("test") };
        rig.Source.ByElevation(normal: Drives.Inspect(denied), elevated: Drives.Inspect(Drives.Nvme()));
        rig.Source.ByElevation(normal: Drives.Inspect(denied), elevated: Drives.Inspect(Drives.Nvme()));

        var plain = await rig.RunAsync();
        var elevated = await rig.RunAsync(new CheckRequest(CheckKind.Storage, AllowElevation: true));

        Assert.Equal([false, true], rig.Source.Elevations);
        Assert.Equal(CheckReason.Denied, Row(plain, "storage.temperature", "disk-1").Reason);
        Assert.Equal(CheckVerdict.Unavailable, plain.Result.Verdict);
        Assert.Equal(ItemVerdict.Ok, Row(elevated, "storage.temperature", "disk-1").Verdict);
        Assert.Equal(CheckVerdict.Ok, elevated.Result.Verdict);
    }

    // ---- baseline failure ----

    [Fact]
    public async Task A_baseline_that_could_not_be_read_gives_the_note_and_every_delta_says_read_failed()
    {
        using var rig = new RunRig();
        rig.Baseline.Lock.Refuse = true;

        var run = await RunOnce(rig, Nvme(), HealthyAta());

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Equal([CheckNotes.StorageBaselineReadFailed], run.Result.Notes);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        var deltas = run.Result.Items.Where(static i => i.Id.EndsWith(".delta", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(deltas);
        Assert.All(deltas, static d =>
        {
            Assert.Null(d.Value);
            Assert.Equal(ItemVerdict.Info, d.Verdict);
            Assert.Equal(CheckEvidenceCode.BaselineReadFailed, d.Evidence!.Code);
        });

        // The absolute rows are judged all the same: a damaged baseline does not take the health away.
        Assert.Equal(ItemVerdict.Ok, Row(run, "storage.temperature", "disk-1").Verdict);
        Assert.Equal(CheckVerdict.Ok, run.Result.Verdict);
        Assert.True(run.IsSaved);
        Assert.Empty(rig.Baseline.Names());
    }

    [Fact]
    public async Task Without_a_baseline_problem_there_is_no_note()
    {
        using var rig = new RunRig();

        var run = await RunOnce(rig, Nvme());

        Assert.Empty(run.Result.Notes);
    }

    // ---- the import of the v0.7.4 folder was abandoned (DESIGN-A §5.3-2, note storage.legacy_import_abandoned) ----

    // An old twins file nobody can read: the import is abandoned, with an old history beside it that is then not used as a base.
    private static RunRig RigWithAbandonedImport()
    {
        var rig = new RunRig(withLegacy: true);
        OldFiles.Put(
            rig.Baseline.Legacy,
            Drives.FileNameOf(Drives.NvmeKey, "S6B1NJ0R123456"),
            OldFiles.History(Drives.NvmeKey, "Samsung SSD 980 PRO 2TB", [new OldEntry(H(-2), OldFiles.Nvme(media: 1))]));
        OldFiles.Put(rig.Baseline.Legacy, "twins.json", "[ \"just some text\" ]");
        return rig;
    }

    [Fact]
    public async Task The_run_that_abandons_the_old_import_has_the_note_and_every_delta_says_first()
    {
        using var rig = RigWithAbandonedImport();

        var run = await RunOnce(rig, Nvme());

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Equal([CheckNotes.StorageLegacyImportAbandoned], run.Result.Notes);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.True(run.IsSaved);
        var deltas = run.Result.Items.Where(static i => i.Id.EndsWith(".delta", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(deltas);
        Assert.All(deltas, static d => Assert.Equal(CheckEvidenceCode.BaselineFirst, d.Evidence!.Code));
        Assert.Contains("\"status\": \"abandoned\"", File.ReadAllText(Path.Combine(rig.Baseline.Baseline, "imported-v074.json")));
    }

    [Fact]
    public async Task A_later_run_keeps_the_note_while_no_disk_has_been_compared_and_drops_it_with_the_first_comparison()
    {
        using var rig = RigWithAbandonedImport();

        // A disk whose counters cannot be read has no baseline: the comparison cannot start, and the note stays for as long.
        var unread = await RunOnce(rig, Drives.NoHealth());
        rig.Time.Now = H(1);
        var unreadAgain = await RunOnce(rig, Drives.NoHealth(at: H(1)));

        Assert.Equal([CheckNotes.StorageLegacyImportAbandoned], unread.Result.Notes);
        Assert.Equal([CheckNotes.StorageLegacyImportAbandoned], unreadAgain.Result.Notes);
        Assert.Empty(rig.Baseline.DiskFiles());

        // The first run with the counters records this disk (every delta says first): still no comparison, still the note.
        rig.Time.Now = H(2);
        var recorded = await RunOnce(rig, Drives.Nvme(at: H(2)));
        Assert.Equal([CheckNotes.StorageLegacyImportAbandoned], recorded.Result.Notes);
        Assert.Single(rig.Baseline.DiskFiles());

        // The next run compares with it: the result stands on a baseline of this version, and there is no note.
        rig.Time.Now = H(3);
        var compared = await RunOnce(rig, Drives.Nvme(at: H(3), poh: 101));
        Assert.Empty(compared.Result.Notes);
        Assert.Empty(CheckResultValidator.Validate(compared.Result));
        Assert.Equal(CheckValue.SignedDec(1), Row(compared, "storage.nvme.power_on_hours.delta", "disk-1").Value);

        // And it stays gone: the marker still says abandoned, but a disk was compared.
        rig.Time.Now = H(4);
        var later = await RunOnce(rig, Drives.Nvme(at: H(4), poh: 102));
        Assert.Empty(later.Result.Notes);
    }

    [Fact]
    public async Task A_run_after_a_complete_or_absent_import_never_has_the_note()
    {
        using var rig = new RunRig(withLegacy: true); // an old folder with nothing in it: there is nothing to abandon

        var first = await RunOnce(rig, Nvme());
        rig.Time.Now = H(1);
        var second = await RunOnce(rig, Drives.Nvme(at: H(1), poh: 101));

        Assert.DoesNotContain(CheckNotes.StorageLegacyImportAbandoned, first.Result.Notes);
        Assert.DoesNotContain(CheckNotes.StorageLegacyImportAbandoned, second.Result.Notes);
    }

    // ---- stop ----

    [Fact]
    public async Task A_run_stopped_in_the_middle_of_the_sample_is_stopped_and_still_saved()
    {
        using var rig = new RunRig();
        rig.Source.Hangs();
        using var cts = new CancellationTokenSource();

        var task = rig.RunAsync(ct: cts.Token);
        await rig.Source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        var run = await task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal(CheckKind.Storage, run.Result.Kind);
        Assert.Equal([run.Result.RunId], rig.History.Saved.Select(static r => r.RunId));
        Assert.NotNull(await rig.ReadBackAsync(run.Result.RunId));
        Assert.Empty(rig.Baseline.DiskFiles()); // nothing was recorded
    }

    [Fact]
    public async Task A_stop_that_arrives_just_after_the_sample_is_still_a_stop_and_records_nothing()
    {
        using var rig = new RunRig();
        using var cts = new CancellationTokenSource();
        rig.Source.Returns(Drives.Inspect(Nvme()), during: cts.Cancel);

        var run = await rig.RunAsync(ct: cts.Token);

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.True(run.IsSaved);
        Assert.Empty(rig.Baseline.DiskFiles());
    }

    [Fact]
    public async Task A_token_that_is_cancelled_before_the_run_starts_makes_no_run_at_all()
    {
        using var rig = new RunRig();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.RunAsync(ct: cts.Token));

        Assert.Equal(0, rig.Source.Calls);
        Assert.Empty(rig.History.Saved);
        Assert.Empty(Kit.Names(rig.HistoryRoot));
    }

    // ---- failure ----

    [Fact]
    public async Task A_source_that_throws_fails_the_run_and_the_failed_run_is_saved()
    {
        using var rig = new RunRig();
        rig.Source.Throws(new InvalidOperationException("the drive list could not be read"));

        var run = await rig.RunAsync();

        Assert.Equal(CheckOutcome.Failed, run.Result.Outcome);
        Assert.Equal(CheckRunFailureCodes.InspectionFailed, run.FailureCode);
        Assert.Contains("InvalidOperationException", run.FailureDetail, StringComparison.Ordinal);
        Assert.True(run.IsSaved);
        Assert.Empty(run.Result.Items);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal([run.Result.RunId], rig.History.Saved.Select(static r => r.RunId));
        Assert.Empty(rig.Baseline.DiskFiles());
    }

    [Fact]
    public async Task A_failed_run_does_not_break_the_next_one()
    {
        using var rig = new RunRig();
        rig.Source.Throws(new IOException("boom"));
        rig.Source.Returns(Drives.Inspect(Nvme()));

        var failed = await rig.RunAsync();
        var ok = await rig.RunAsync();

        Assert.Equal(CheckOutcome.Failed, failed.Result.Outcome);
        Assert.Equal(CheckOutcome.Completed, ok.Result.Outcome);
        Assert.Equal(2, Kit.Names(rig.HistoryRoot).Length);
    }

    // ---- the save ----

    [Fact]
    public async Task A_save_that_fails_keeps_the_result_and_reports_why()
    {
        using var rig = new RunRig();
        rig.HistoryFaults.CreateNewFault = static _ => Kit.DiskFull();

        var run = await RunOnce(rig, Nvme(media: 0));

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.False(run.IsSaved);
        Assert.Equal(CheckSaveStatus.Failed, run.Save.Status);
        Assert.Equal(CheckSaveFailureCodes.DiskFull, run.Save.FailureCode);
        Assert.Null(run.Save.FileName);
        Assert.NotEmpty(run.Result.Items);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.DoesNotContain(Kit.Names(rig.HistoryRoot), static n => n.EndsWith(".json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_save_that_fails_does_not_undo_the_baseline_record()
    {
        using var rig = new RunRig();
        rig.HistoryFaults.CreateNewFault = static _ => Kit.DiskFull();

        await RunOnce(rig, Nvme());

        Assert.Single(rig.Baseline.DiskFiles());
    }

    [Fact]
    public async Task A_history_that_throws_is_a_failed_save_and_not_a_lost_result()
    {
        using var rig = new RunRig();
        rig.History.Override = static _ => throw new InvalidOperationException("the history is broken");

        var run = await RunOnce(rig, Nvme());

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Equal(CheckSaveStatus.Failed, run.Save.Status);
        Assert.Equal(CheckSaveFailureCodes.IoError, run.Save.FailureCode);
        Assert.NotEmpty(run.Result.Items);
    }

    // ---- progress ----

    [Fact]
    public async Task The_stages_are_reported_in_order_and_end_with_done()
    {
        using var rig = new RunRig();
        var log = new ProgressLog();
        rig.Source.Returns(Drives.Inspect(Nvme()));

        var run = await rig.RunAsync(progress: log);

        Assert.True(run.IsSaved);
        Assert.Equal([CheckStage.Starting, CheckStage.Collecting, CheckStage.Judging, CheckStage.Saving, CheckStage.Done], log.Stages);
        Assert.All(log.Items, p => Assert.Equal(run.Result.RunId, p.RunId));
    }

    [Fact]
    public async Task A_progress_handler_that_never_returns_does_not_hold_up_the_run()
    {
        using var rig = new RunRig();
        using var release = new ManualResetEventSlim();
        var log = new ProgressLog { OnReport = _ => release.Wait(TimeSpan.FromSeconds(30)) };
        rig.Source.Returns(Drives.Inspect(Nvme()));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var run = await rig.RunAsync(progress: log).WaitAsync(TimeSpan.FromSeconds(20));
        watch.Stop();
        release.Set();

        Assert.True(run.IsSaved);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the run took {watch.Elapsed}");
    }

    // ---- the request ----

    [Fact]
    public async Task A_request_for_another_kind_is_not_supported_yet()
    {
        using var rig = new RunRig();

        await Assert.ThrowsAsync<NotSupportedException>(() => rig.Runner.RunAsync(new CheckRequest(CheckKind.Memory), null, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => rig.Runner.RunAsync(new CheckRequest(CheckKind.Cpu, TimeSpan.FromSeconds(30)), null, CancellationToken.None));
        Assert.Empty(rig.History.Saved);
    }

    [Fact]
    public async Task A_storage_request_with_a_duration_is_refused()
    {
        using var rig = new RunRig();

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Runner.RunAsync(new CheckRequest(CheckKind.Storage, TimeSpan.FromSeconds(30)), null, CancellationToken.None));
        Assert.Equal(0, rig.Source.Calls);
    }

    [Fact]
    public async Task Two_runs_one_after_the_other_have_their_own_ids_files_and_baseline_entries()
    {
        using var rig = new RunRig();
        await RunOnce(rig, Nvme());
        rig.Time.Now = H(1);

        var second = await RunOnce(rig, Drives.Nvme(at: H(1), poh: 101));

        Assert.Equal(Kit.Id(2), second.Result.RunId);
        Assert.Equal(2, Kit.Names(rig.HistoryRoot).Length);
        Assert.Equal(CheckValue.SignedDec(1), Row(second, "storage.nvme.power_on_hours.delta", "disk-1").Value);
    }
}
