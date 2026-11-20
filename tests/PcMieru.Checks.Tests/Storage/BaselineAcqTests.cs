using PcMieru.Checks.Storage;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Tests.Storage;

/// <summary>
/// Two runs of one user that overlap (DESIGN-A §14.5): the acquisition number taken at the start decides which sample is the newer one,
/// whatever order they finish in. Also the lock around it, and the twins set that must be stored before anything is compared.
/// </summary>
public class BaselineAcqTests
{
    private static readonly DateTimeOffset T0 = Drives.T0;

    private static DateTimeOffset H(double hours) => T0.AddHours(hours);

    private static string NvmeFile => Drives.FileNameOf(Drives.NvmeKey, "S6B1NJ0R123456");

    private static int Entries(string text) => text.Split("\"at\":").Length - 1;

    private static StorageInspection Sample(double hours, ulong media) =>
        Drives.Inspect(Drives.Nvme(at: H(hours), poh: 100 + (ulong)hours, media: media));

    // ---- the interleaving ----

    [Fact]
    public void A_run_that_started_first_and_finished_last_is_concurrent_and_records_nothing()
    {
        using var rig = new BaselineRig();
        rig.Run(Sample(0, 0));
        var other = rig.NewEngine();

        var older = rig.Engine.BeginRun();   // sampling of the older run begins
        var newer = other.BeginRun();        // the newer run begins later ...
        var newerResult = other.Complete(newer, Sample(2, 5));  // ... and finishes first
        var olderResult = rig.Engine.Complete(older, Sample(1, 3));

        Assert.True(newer.Acq > older.Acq);
        Assert.Equal(BaselineDiskState.Compared, newerResult.Disks[0].State);
        Assert.Equal(CheckEvidenceCode.BaselineConcurrent, olderResult.Disks[0].Code);
        Assert.All(olderResult.Disks[0].Counters, static c => Assert.Equal(CheckEvidenceCode.BaselineConcurrent, c.Code));
        Assert.False(olderResult.ReadFailed);
        BaselineAssert.ValidRows(olderResult);
        // Neither compared nor recorded: the file holds the first run and the newer one only.
        var text = rig.Read(NvmeFile);
        Assert.Equal(2, Entries(text));
        Assert.DoesNotContain("\"nvme.media_errors\": 3", text);

        // The run after both compares with the newer record, not with the older sample that was thrown away.
        var next = rig.Run(Sample(3, 6));
        var media = BaselineAssert.Counter(next.Disks[0], "nvme.media_errors");
        Assert.Equal(5UL, media.Before);
        Assert.Equal(H(2), media.SinceUtc);
    }

    [Fact]
    public void Two_overlapping_runs_that_finish_in_the_order_they_started_are_both_compared()
    {
        using var rig = new BaselineRig();
        rig.Run(Sample(0, 0));
        var other = rig.NewEngine();

        var first = rig.Engine.BeginRun();
        var second = other.BeginRun();
        var firstResult = rig.Engine.Complete(first, Sample(1, 3));
        var secondResult = other.Complete(second, Sample(2, 5));

        Assert.Equal(BaselineDiskState.Compared, firstResult.Disks[0].State);
        Assert.Equal(BaselineDiskState.Compared, secondResult.Disks[0].State);
        Assert.Equal(3UL, BaselineAssert.Counter(secondResult.Disks[0], "nvme.media_errors").Before);
        Assert.Equal(H(1), BaselineAssert.Counter(secondResult.Disks[0], "nvme.media_errors").SinceUtc);
        Assert.Equal(3, Entries(rig.Read(NvmeFile)));
    }

    [Fact]
    public void The_interleaving_found_by_taking_the_lock_again_at_record_time_is_the_same_one()
    {
        using var rig = new BaselineRig();
        rig.Run(Sample(0, 0));
        var other = rig.NewEngine();
        var olderLock = new FakeBaselineLock();
        var older = rig.NewEngine(olderLock);
        var ticket = older.BeginRun(); // the first lock of the older run

        BaselineRunResult? newerResult = null;
        // Just before the older run takes the lock to record, the newer run (begun and finished) gets in.
        olderLock.BeforeAcquire = taken =>
        {
            if (taken == 1)
            {
                newerResult = BaselineRig.Run(other, Sample(2, 5));
            }
        };
        var olderResult = older.Complete(ticket, Sample(1, 3));

        Assert.NotNull(newerResult);
        Assert.Equal(BaselineDiskState.Compared, newerResult!.Disks[0].State);
        Assert.Equal(CheckEvidenceCode.BaselineConcurrent, olderResult.Disks[0].Code);
        Assert.Equal(2, olderLock.Taken);
        Assert.Equal(2, olderLock.Released);
        Assert.Equal(2, Entries(rig.Read(NvmeFile)));
    }

    [Fact]
    public void A_disk_that_the_newer_run_did_not_record_is_not_concurrent()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0), Drives.Sata(at: T0)));
        var other = rig.NewEngine();

        var older = rig.Engine.BeginRun();
        var newer = other.BeginRun();
        other.Complete(newer, Drives.Inspect(Drives.Nvme(at: H(2), poh: 102))); // the newer run saw only the NVMe drive
        var result = rig.Engine.Complete(older, Drives.Inspect(Drives.Nvme(at: H(1), poh: 101), Drives.Sata(at: H(1))));

        Assert.Equal(CheckEvidenceCode.BaselineConcurrent, result.Disks[0].Code);
        Assert.Equal(BaselineDiskState.Compared, result.Disks[1].State);
    }

    // ---- a late sample: numbered after its base, read before it (§14.5) ----

    private static StorageInspection NvmeAt(double hours, ulong poh, ulong written = 1000, ulong media = 0) =>
        Drives.Inspect(Drives.Nvme(at: H(hours), poh: poh, written: written, media: media));

    /// <summary>
    /// The run that began first (acq 2) reads its drive late and records first; the run that began after it (acq 3) read the drive
    /// earlier and comes second: its base has the smaller acq and the later time. Returns the second run's result.
    /// </summary>
    private static BaselineRunResult LateSampleAgainst(BaselineRig rig, StorageInspection firstSample, StorageInspection lateSample)
    {
        var other = rig.NewEngine();
        var first = rig.Engine.BeginRun();
        var second = other.BeginRun();
        Assert.True(second.Acq > first.Acq);
        var firstResult = rig.Engine.Complete(first, firstSample);
        Assert.False(firstResult.ReadFailed);
        return other.Complete(second, lateSample);
    }

    [Theory]
    [InlineData(102UL, 1500UL, 101UL, 1500UL)] // power-on hours went back
    [InlineData(102UL, 1500UL, 102UL, 1400UL)] // data units written went back
    public void An_nvme_sample_that_is_earlier_than_its_base_and_whose_counters_went_back_is_concurrent_and_records_nothing(
        ulong basePoh, ulong baseWritten, ulong latePoh, ulong lateWritten)
    {
        using var rig = new BaselineRig();
        rig.Run(NvmeAt(0, 100));

        var late = LateSampleAgainst(rig, NvmeAt(2, basePoh, baseWritten, media: 5), NvmeAt(1, latePoh, lateWritten, media: 3));

        Assert.Equal(CheckEvidenceCode.BaselineConcurrent, late.Disks[0].Code);
        Assert.Equal(BaselineDiskState.NotComparable, late.Disks[0].State);
        Assert.All(late.Disks[0].Counters, static c => Assert.Equal(CheckEvidenceCode.BaselineConcurrent, c.Code));
        Assert.False(late.ReadFailed);
        BaselineAssert.ValidRows(late);
        // Not a counter reset, and nothing recorded: the file holds the first run and the run that read the drive later, and no more.
        var text = rig.Read(NvmeFile);
        Assert.Equal(2, Entries(text));
        Assert.DoesNotContain("\"nvme.media_errors\": 3", text);
        Assert.DoesNotContain("withheld", text);

        // The next run compares with the sample that was taken last, not with the one thrown away.
        var next = rig.Run(NvmeAt(3, 103, 1600, media: 6));
        var media = BaselineAssert.Counter(next.Disks[0], "nvme.media_errors");
        Assert.Equal(BaselineDiskState.Compared, next.Disks[0].State);
        Assert.Equal(5UL, media.Before);
        Assert.Equal(H(2), media.SinceUtc);
    }

    [Fact]
    public void A_sata_sample_that_is_earlier_than_its_base_and_whose_hours_went_back_is_concurrent()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Sata(at: T0, hours: 1000)));

        var late = LateSampleAgainst(
            rig,
            Drives.Inspect(Drives.Sata(at: H(2), hours: 1002)),
            Drives.Inspect(Drives.Sata(at: H(1), hours: 1001)));

        Assert.Equal(CheckEvidenceCode.BaselineConcurrent, late.Disks[0].Code);
        Assert.All(late.Disks[0].Counters, static c => Assert.Equal(CheckEvidenceCode.BaselineConcurrent, c.Code));
        Assert.Equal(2, Entries(rig.Read(Drives.FileNameOf(Drives.SataKey, "WD-WX12A"))));
    }

    [Fact]
    public void A_sample_that_is_earlier_than_its_base_with_no_counter_going_back_follows_the_clock_path()
    {
        using var rig = new BaselineRig();
        rig.Run(NvmeAt(0, 100));

        // Time went back by an hour, the hours went forward by 8: no counter went back, so this is the clock, not an older sample.
        var late = LateSampleAgainst(rig, NvmeAt(2, 102, media: 5), NvmeAt(1, 110, media: 3));

        Assert.Equal(CheckEvidenceCode.BaselineClock, late.Disks[0].Code);
        Assert.Equal(3, Entries(rig.Read(NvmeFile)));
        Assert.Contains("\"withheld\": true", rig.Read(NvmeFile)); // recorded, but never the base of a comparison

        var next = rig.Run(NvmeAt(3, 103, media: 6));
        Assert.Equal(5UL, BaselineAssert.Counter(next.Disks[0], "nvme.media_errors").Before);
    }

    [Fact]
    public void A_sample_that_is_earlier_than_its_base_with_counters_that_did_not_move_back_is_compared_as_before()
    {
        using var rig = new BaselineRig();
        rig.Run(NvmeAt(0, 100));

        var late = LateSampleAgainst(rig, NvmeAt(2, 102, media: 5), NvmeAt(1, 102, media: 5));

        Assert.Equal(BaselineDiskState.Compared, late.Disks[0].State);
        Assert.NotEqual(CheckEvidenceCode.BaselineConcurrent, late.Disks[0].Code);
        Assert.Equal(3, Entries(rig.Read(NvmeFile)));
    }

    [Fact]
    public void A_true_counter_reset_with_time_moving_forward_is_still_a_counter_reset()
    {
        using var rig = new BaselineRig();
        rig.Run(NvmeAt(0, 100));
        rig.Run(NvmeAt(1, 101, 1100));

        var reset = rig.Run(NvmeAt(2, 5, 10)); // the hours and the data written went back, a later time than the base

        Assert.Equal(CheckEvidenceCode.BaselineCounterReset, reset.Disks[0].Code);
        Assert.All(reset.Disks[0].Counters, static c => Assert.Equal(CheckEvidenceCode.BaselineCounterReset, c.Code));
        Assert.Equal(3, Entries(rig.Read(NvmeFile))); // and the new values are recorded, the base from now on
        var next = rig.Run(NvmeAt(3, 6, 20));
        Assert.Equal(BaselineDiskState.Compared, next.Disks[0].State);
        Assert.Equal(5UL, BaselineAssert.Counter(next.Disks[0], "nvme.power_on_hours").Before);
    }

    [Fact]
    public void A_true_counter_reset_in_overlapping_runs_that_finish_in_the_order_they_started_is_a_counter_reset()
    {
        using var rig = new BaselineRig();
        rig.Run(NvmeAt(0, 100));
        var other = rig.NewEngine();

        var first = rig.Engine.BeginRun();
        var second = other.BeginRun();
        rig.Engine.Complete(first, NvmeAt(1, 101));
        var result = other.Complete(second, NvmeAt(2, 3, 5)); // read later than the base and reset

        Assert.Equal(CheckEvidenceCode.BaselineCounterReset, result.Disks[0].Code);
    }

    [Fact]
    public void A_record_of_the_old_tool_is_never_the_base_of_a_late_sample_even_when_it_is_dated_in_the_future()
    {
        using var rig = new BaselineRig();
        // A clock that was ahead when v0.7.4 wrote this: its time is later than every sample of now, and the hours are more than now.
        OldFiles.Put(rig.Baseline, NvmeFile, OldFiles.History(Drives.NvmeKey, "Samsung SSD 980 PRO 2TB", new OldEntry(H(5), OldFiles.Nvme(poh: 200))));

        var result = rig.Run(NvmeAt(0, 100));

        // It was no run that was going on at the same time (acq 0): the drive is not stopped for as long as the clock is behind that
        // record, it is a counter reset like any other and the new record becomes the base.
        Assert.Equal(CheckEvidenceCode.BaselineCounterReset, result.Disks[0].Code);
        Assert.Equal(BaselineDiskState.Compared, rig.Run(NvmeAt(1, 101)).Disks[0].State);
    }

    [Fact]
    public void Records_from_the_old_tool_count_as_acq_zero_and_never_make_a_run_concurrent()
    {
        using var rig = new BaselineRig();
        OldFiles.Put(rig.Baseline, NvmeFile, OldFiles.History(Drives.NvmeKey, "Samsung SSD 980 PRO 2TB", new OldEntry(T0.AddHours(-1), OldFiles.Nvme())));

        var result = rig.Run(Sample(0, 0));

        Assert.Equal(BaselineDiskState.Compared, result.Disks[0].State);
        Assert.Contains("\"acq\": 1", rig.Read(NvmeFile));
    }

    // ---- the lock ----

    [Fact]
    public void The_lock_is_taken_twice_for_a_run_each_time_for_one_call_and_never_nested_or_held_in_between()
    {
        using var rig = new BaselineRig();

        var ticket = rig.Engine.BeginRun();
        Assert.Equal(1, rig.Lock.Taken);
        Assert.Equal(1, rig.Lock.Released);
        Assert.False(rig.Lock.IsHeld);

        rig.Engine.Complete(ticket, Sample(0, 0));

        Assert.Equal(2, rig.Lock.Taken);
        Assert.Equal(2, rig.Lock.Released);
        Assert.Equal(1, rig.Lock.MaxDepth);
        Assert.False(rig.Lock.IsHeld);
    }

    [Fact]
    public void The_lock_is_released_even_when_the_run_throws()
    {
        using var rig = new BaselineRig();
        var ticket = rig.Engine.BeginRun();
        // A failure the code does not expect (not an I/O error) inside the locked part.
        rig.Faults.ReadFault = path => Path.GetFileName(path) == "disk-ordinals.json" ? new NotSupportedException("unexpected") : null;

        Assert.Throws<NotSupportedException>(() => rig.Engine.Complete(ticket, Sample(0, 0)));

        Assert.Equal(2, rig.Lock.Taken);
        Assert.False(rig.Lock.IsHeld);
        Assert.Equal(2, rig.Lock.Released);
    }

    [Fact]
    public void Every_run_takes_the_next_number_and_the_number_is_stored()
    {
        using var rig = new BaselineRig();

        var one = rig.Engine.BeginRun();
        var two = rig.Engine.BeginRun();

        Assert.Equal(new BaselineRunTicket(1, false), one);
        Assert.Equal(new BaselineRunTicket(2, false), two);
        Assert.Contains("\"seq\": 2", rig.Read("acq-seq.json"));
        // Another engine on the same folder continues the same sequence.
        Assert.Equal(3, rig.NewEngine().BeginRun().Acq);
    }

    [Fact]
    public void The_acq_of_the_run_is_in_the_record_it_wrote()
    {
        using var rig = new BaselineRig();
        rig.Engine.BeginRun();
        rig.Engine.BeginRun();
        var third = rig.Engine.BeginRun();

        rig.Engine.Complete(third, Sample(0, 0));

        Assert.Contains("\"acq\": 3", rig.Read(NvmeFile));
    }

    [Fact]
    public void A_lock_that_cannot_be_taken_at_the_start_means_nothing_is_compared_or_recorded()
    {
        using var rig = new BaselineRig();
        rig.Lock.Refuse = true;
        var ticket = rig.Engine.BeginRun();
        rig.Lock.Refuse = false;

        var result = rig.Engine.Complete(ticket, Drives.Inspect(Drives.Nvme(), Weak()));

        Assert.True(ticket.Failed);
        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, result.Disks[0].Code);
        Assert.Equal(CheckEvidenceCode.BaselineNoIdentity, result.Disks[1].Code); // a weak key is judged without the baseline
        Assert.True(result.ReadFailed);
        BaselineAssert.ValidRows(result);
        Assert.Empty(rig.Names());
        Assert.Equal(0, rig.Lock.Taken); // the refused call took nothing, and a failed ticket never asks again
    }

    [Fact]
    public void A_lock_that_cannot_be_taken_at_record_time_means_nothing_is_compared_or_recorded()
    {
        using var rig = new BaselineRig();
        rig.Run(Sample(0, 0));
        var before = rig.Read(NvmeFile);
        var ticket = rig.Engine.BeginRun();
        rig.Lock.Refuse = true;

        var result = rig.Engine.Complete(ticket, Sample(1, 4));

        Assert.False(ticket.Failed);
        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, result.Disks[0].Code);
        Assert.True(result.ReadFailed);
        Assert.Equal(before, rig.Read(NvmeFile));
    }

    [Fact]
    public void A_failed_ticket_never_takes_the_lock_in_complete()
    {
        using var rig = new BaselineRig();
        rig.Lock.Refuse = true;
        var ticket = rig.Engine.BeginRun();
        var tried = rig.Lock.Taken;
        rig.Lock.Refuse = false;

        rig.Engine.Complete(ticket, Sample(0, 0));

        Assert.Equal(tried, rig.Lock.Taken);
    }

    // ---- the sequence file ----

    [Fact]
    public void A_missing_sequence_file_is_rebuilt_from_the_largest_acq_on_disk()
    {
        using var rig = new BaselineRig();
        rig.Run(Sample(0, 0));
        rig.Run(Sample(1, 0));
        File.Delete(rig.PathOf("acq-seq.json"));

        var ticket = rig.Engine.BeginRun();

        Assert.Equal(3, ticket.Acq);
        Assert.Contains("\"seq\": 3", rig.Read("acq-seq.json"));
    }

    [Fact]
    public void A_damaged_sequence_file_is_rebuilt_the_same_way()
    {
        using var rig = new BaselineRig();
        rig.Run(Sample(0, 0));
        File.WriteAllText(rig.PathOf("acq-seq.json"), "{ nope");

        var ticket = rig.Engine.BeginRun();

        Assert.Equal(2, ticket.Acq);
        Assert.False(ticket.Failed);
    }

    [Fact]
    public void With_no_records_and_no_sequence_file_the_first_number_is_one()
    {
        using var rig = new BaselineRig();

        Assert.Equal(1, rig.Engine.BeginRun().Acq);
    }

    [Fact]
    public void A_sequence_file_that_cannot_be_read_or_stored_makes_the_ticket_failed()
    {
        using var rig = new BaselineRig();
        rig.Run(Sample(0, 0));
        rig.Faults.ReadFault = path => Path.GetFileName(path) == "acq-seq.json" ? new IOException("sharing violation") : null;
        var unreadable = rig.Engine.BeginRun();
        rig.Faults.ReadFault = null;
        rig.Files.ReplaceFault = (_, destination) => Path.GetFileName(destination) == "acq-seq.json" ? new IOException("disk full") : null;
        var unwritable = rig.Engine.BeginRun();
        rig.Files.ReplaceFault = null;

        Assert.True(unreadable.Failed);
        Assert.True(unwritable.Failed);
        var result = rig.Engine.Complete(unwritable, Sample(1, 0));
        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, result.Disks[0].Code);
        Assert.Equal(1, Entries(rig.Read(NvmeFile)));
        // A failed number is not handed out again as if it had been used: the next good run still gets 2.
        Assert.Equal(2, rig.Engine.BeginRun().Acq);
    }

    // ---- the twins set is stored first ----

    private static StorageInspectionDrive Weak() => Drives.Nvme(serial: "", uniqueId: "", number: 5);

    private static StorageInspection Twins(DateTimeOffset at, ulong sataHours = 1000) => Drives.Inspect(
        at,
        [
            Drives.Nvme(at: at, location: "0:0:0:0", number: 0),
            Drives.Nvme(at: at, location: "1:0:0:0", number: 1),
            Drives.Sata(at: at, hours: sataHours),
        ]);

    [Fact]
    public void New_twins_are_stored_before_anything_else_is_compared_or_recorded()
    {
        using var rig = new BaselineRig();

        rig.Run(Twins(T0));

        var replaces = rig.Files.Replaces.ToList();
        var twins = replaces.IndexOf("twins.json");
        Assert.True(twins >= 0, "twins.json was written");
        Assert.True(twins < replaces.FindIndex(static n => n.StartsWith("disk-", StringComparison.Ordinal)), string.Join(", ", replaces));
        Assert.Single(replaces, static n => n == "twins.json");
        rig.Run(Twins(H(1), 1001));
        Assert.Single(rig.Files.Replaces, static n => n == "twins.json"); // nothing new to store the second time
    }

    [Fact]
    public void When_the_twins_cannot_be_stored_nothing_is_compared_and_nothing_is_recorded_in_that_run()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Sata(at: T0)));
        var sataBefore = rig.Read(Drives.FileNameOf(Drives.SataKey, "WD-WX12A"));
        rig.Files.ReplaceFault = (_, destination) => Path.GetFileName(destination) == "twins.json" ? new IOException("disk full") : null;

        var failed = rig.Run(Twins(H(1), 1001));

        Assert.True(failed.ReadFailed);
        Assert.Equal(CheckEvidenceCode.BaselineDuplicate, failed.Disks[0].Code);
        Assert.Equal(CheckEvidenceCode.BaselineDuplicate, failed.Disks[1].Code);
        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, failed.Disks[2].Code);
        BaselineAssert.ValidRows(failed);
        Assert.False(rig.Has("twins.json"));
        Assert.DoesNotContain(rig.Names(), static n => n.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Equal(sataBefore, rig.Read(Drives.FileNameOf(Drives.SataKey, "WD-WX12A")));
        Assert.Single(rig.DiskFiles()); // the located records of the twins were not written either

        // The fault is gone: the same inspection now works, and the disk is compared with the record from before the failed run.
        rig.Files.ReplaceFault = null;
        var healed = rig.Run(Twins(H(2), 1002));
        Assert.False(healed.ReadFailed);
        Assert.Equal(BaselineDiskState.Compared, healed.Disks[2].State);
        Assert.Equal(T0, BaselineAssert.Counter(healed.Disks[2], "ata.9").SinceUtc);
        Assert.Equal(new[] { Drives.NvmeKey }, System.Text.Json.JsonSerializer.Deserialize<string[]>(rig.Read("twins.json")));
        Assert.Equal(3, rig.DiskFiles().Count);
    }

    [Fact]
    public void A_twins_file_that_is_damaged_blocks_the_comparison_and_is_not_overwritten()
    {
        using var rig = new BaselineRig();
        rig.Run(Sample(0, 0));
        File.WriteAllText(rig.PathOf("twins.json"), "[ \"v2|sn=");
        var before = File.ReadAllBytes(rig.PathOf("twins.json"));
        var nvmeBefore = rig.Read(NvmeFile);

        var blocked = rig.Run(Sample(1, 3));
        var pair = rig.Run(Twins(H(2)));

        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, blocked.Disks[0].Code);
        Assert.True(blocked.ReadFailed);
        Assert.Equal(before, File.ReadAllBytes(rig.PathOf("twins.json")));
        Assert.Equal(nvmeBefore, rig.Read(NvmeFile));
        Assert.Equal(CheckEvidenceCode.BaselineDuplicate, pair.Disks[0].Code);
        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, pair.Disks[2].Code);
        Assert.Equal(before, File.ReadAllBytes(rig.PathOf("twins.json")));
        Assert.DoesNotContain(rig.Names(), static n => n.Contains(".bad-", StringComparison.Ordinal));

        // Once the file is whole again the comparison resumes.
        File.WriteAllText(rig.PathOf("twins.json"), "[]");
        Assert.Equal(BaselineDiskState.Compared, rig.Run(Sample(3, 4)).Disks[0].State);
    }

    [Theory]
    [InlineData("twins.json")]
    [InlineData("imported-twins.json")]
    public void A_twins_set_with_a_value_that_is_not_a_key_is_not_trusted(string file)
    {
        using var rig = new BaselineRig(withLegacy: false);
        rig.Run(Sample(0, 0));
        OldFiles.Put(rig.Baseline, file, "[ \"just some text\" ]");

        var result = rig.Run(Sample(1, 3));

        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, result.Disks[0].Code);
        Assert.True(result.ReadFailed);
    }

    [Fact]
    public void A_twins_set_that_cannot_be_read_now_blocks_the_comparison_like_a_damaged_one()
    {
        using var rig = new BaselineRig();
        rig.Run(Sample(0, 0));
        OldFiles.Put(rig.Baseline, "twins.json", "[]");
        var ticket = rig.Engine.BeginRun();
        rig.Faults.ReadFault = path => Path.GetFileName(path) == "twins.json" ? new IOException("sharing violation") : null;

        var result = rig.Engine.Complete(ticket, Sample(1, 3));

        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, result.Disks[0].Code);
        Assert.Equal(1, Entries(rig.Read(NvmeFile)));
    }

    [Fact]
    public void The_twins_of_this_run_are_added_to_those_already_stored()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(
            Drives.Sata(serial: "TWINSSD", location: "0:0:0:0", number: 0),
            Drives.Sata(serial: "TWINSSD", location: "1:0:0:0", number: 1)));

        rig.Run(Twins(H(1)));

        var stored = System.Text.Json.JsonSerializer.Deserialize<string[]>(rig.Read("twins.json"))!;
        Assert.Equal(2, stored.Length);
        Assert.Contains(Drives.NvmeKey, stored);
        Assert.Contains("v2|sn=TWINSSD|uid=|model=WDC WD40EZRZ-00GXCB0|size=4000787030016|bus=Sata", stored);
    }
}
