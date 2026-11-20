using PcMieru.Checks.Storage;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Tests.Storage;

/// <summary>
/// The import of the v0.7.4 folder (DESIGN-A §5.3). The old files are the real shapes (indented, camelCase, local offsets, no acq); the
/// old folder is compared before and after to show nothing in it was written, renamed or deleted.
/// </summary>
public class BaselineMigrationTests
{
    private static readonly DateTimeOffset T0 = Drives.T0;

    private const string SecondSerial = "S2SECOND0001";
    private const string SecondUid = "eui.0025385b21a05678";
    private const string Model = "Samsung SSD 980 PRO 2TB";

    private static string NvmeFile => Drives.FileNameOf(Drives.NvmeKey, "S6B1NJ0R123456");

    private static string SataFile => Drives.FileNameOf(Drives.SataKey, "WD-WX12A");

    private static string SecondKey => $"v2|sn={SecondSerial}|uid={SecondUid}|model={Model}|size=2000398934016|bus=Nvme";

    private static string SecondFile => Drives.FileNameOf(SecondKey, SecondSerial);

    private static DateTimeOffset H(double hours) => T0.AddHours(hours);

    private static int Entries(string text) => text.Split("\"at\":").Length - 1;

    private static IReadOnlyDictionary<string, ulong> SataCounters(ulong hours = 900, ulong reallocated = 0, ulong pending = 0, ulong crc = 0) =>
        new SortedDictionary<string, ulong>(StringComparer.Ordinal)
        {
            ["ata.5"] = reallocated,
            ["ata.9"] = hours,
            ["ata.194"] = 33,
            ["ata.197"] = pending,
            ["ata.199"] = crc,
        };

    private static StorageInspectionDrive SecondNvme(DateTimeOffset? at = null, ulong media = 0, ulong poh = 100) =>
        Drives.Nvme(serial: SecondSerial, uniqueId: SecondUid, at: at, media: media, poh: poh, number: 2);

    private static void OldNvme(BaselineRig rig, params OldEntry[] entries) =>
        OldFiles.Put(rig.Legacy, NvmeFile, OldFiles.History(Drives.NvmeKey, Model, entries));

    private static void OldSata(BaselineRig rig, params OldEntry[] entries) =>
        OldFiles.Put(rig.Legacy, SataFile, OldFiles.History(Drives.SataKey, "WDC WD40EZRZ-00GXCB0", entries));

    private static void OldSecond(BaselineRig rig, params OldEntry[] entries) =>
        OldFiles.Put(rig.Legacy, SecondFile, OldFiles.History(SecondKey, Model, entries));

    // ---- the shapes of a real old folder ----

    [Fact]
    public void A_real_old_folder_is_imported_and_the_first_run_compares_with_the_last_old_record()
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-3), OldFiles.Nvme(media: 0, poh: 97)), new OldEntry(H(-2), OldFiles.Nvme(media: 1, poh: 98)));
        OldSata(rig, new OldEntry(H(-1), SataCounters(900)));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(media: 4, poh: 100), Drives.Sata(hours: 905)));

        Assert.Equal(BaselineMigrationStatus.Complete, result.Migration.Status);
        Assert.Equal(2, result.Migration.Histories);
        Assert.Equal(0, result.Migration.AlreadyThere);
        Assert.Equal(0, result.Migration.Skipped);
        Assert.False(result.ReadFailed);
        var media = BaselineAssert.Counter(result.Disks[0], "nvme.media_errors");
        Assert.Equal(1UL, media.Before);
        Assert.Equal(4UL, media.After);
        Assert.Equal(H(-2), media.SinceUtc);
        var hours = BaselineAssert.Counter(result.Disks[1], "ata.9");
        Assert.Equal(900UL, hours.Before);
        Assert.Equal(905UL, hours.After);
        Assert.Equal(H(-1), hours.SinceUtc);
        BaselineAssert.ValidRows(result);

        // The copy keeps both old records (acq 0, the time now in UTC), and this run's record follows.
        var text = rig.Read(NvmeFile);
        Assert.Equal(3, Entries(text));
        Assert.Equal(2, text.Split("\"acq\": 0").Length - 1);
        Assert.Contains("\"acq\": 1", text);
        Assert.Contains("2026-11-20T06:00:00.1230000Z", text); // H(-3), written as UTC
    }

    [Fact]
    public void The_completion_mark_holds_counts_and_no_key_serial_or_content()
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-2), OldFiles.Nvme()));
        OldSata(rig, new OldEntry(H(-2), SataCounters()));
        OldFiles.Put(rig.Legacy, "twins.json", OldFiles.Twins("v2|sn=TWIN|uid=|model=M|size=1|bus=Sata"));

        var result = rig.Run(Drives.Inspect(Drives.Nvme()));

        Assert.Equal(BaselineMigrationStatus.Complete, result.Migration.Status);
        var mark = rig.Read("imported-v074.json");
        Assert.Contains("\"histories\": 2", mark);
        Assert.Contains("\"twins\": 1", mark);
        Assert.Contains("completedAtUtc", mark);
        foreach (var secret in new[] { "S6B1NJ0R123456", "WD-WX12A", "TWIN", "v2|", "eui.", "Samsung", "nvme.", "ata." })
        {
            Assert.DoesNotContain(secret, mark);
        }
    }

    [Fact]
    public void Nothing_in_the_old_folder_is_written_renamed_or_deleted()
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-2), OldFiles.Nvme(media: 1)));
        OldSata(rig, new OldEntry(H(-2), SataCounters()));
        OldFiles.Put(rig.Legacy, "twins.json", OldFiles.Twins(Drives.SataKey));
        OldFiles.Put(rig.Legacy, "disk-S6B1NJ0R123456.json", OldFiles.History("S6B1NJ0R123456", Model, new OldEntry(H(-9), OldFiles.Nvme())));
        OldFiles.Put(rig.Legacy, "disk-0123456789abcdef-0123456789abcdef.json", "{ nope");
        OldFiles.Put(rig.Legacy, "disk-halfway.json.tmp", "{");
        OldFiles.Put(rig.Legacy, "readme.txt", "notes");
        var before = OldFiles.Snapshot(rig.Legacy);

        rig.Run(Drives.Inspect(Drives.Nvme(media: 2), Drives.Sata()));
        rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), media: 3, poh: 101), Drives.Sata(at: H(1))));

        Assert.Equal(before, OldFiles.Snapshot(rig.Legacy));
    }

    [Fact]
    public void A_history_that_the_new_folder_already_has_is_kept_and_never_overwritten()
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-5), OldFiles.Nvme(media: 1, poh: 95)));
        // The new folder already holds a history of the same disk (a run of the new tool, or an earlier partial import).
        OldFiles.Put(rig.Baseline, NvmeFile, OldFiles.History(Drives.NvmeKey, Model, new OldEntry(H(-1), OldFiles.Nvme(media: 7, poh: 99, written: 900))));
        var existing = File.ReadAllBytes(rig.PathOf(NvmeFile));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(media: 9, poh: 100)));

        Assert.Equal(BaselineMigrationStatus.Complete, result.Migration.Status);
        Assert.Equal(0, result.Migration.Histories);
        Assert.Equal(1, result.Migration.AlreadyThere);
        var media = BaselineAssert.Counter(result.Disks[0], "nvme.media_errors");
        Assert.Equal(7UL, media.Before);
        Assert.Equal(H(-1), media.SinceUtc);
        var text = rig.Read(NvmeFile);
        Assert.Equal(2, Entries(text)); // the one that was there, and this run's; the old record of the old folder was not mixed in
        Assert.DoesNotContain("\"nvme.power_on_hours\": 95", text);
        Assert.NotEqual(existing, File.ReadAllBytes(rig.PathOf(NvmeFile)));
    }

    [Fact]
    public void A_history_that_appears_between_the_check_and_the_copy_is_not_overwritten()
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-5), OldFiles.Nvme(media: 1, poh: 95)));
        var late = OldFiles.History(Drives.NvmeKey, Model, new OldEntry(H(-1), OldFiles.Nvme(media: 7, poh: 99)));
        rig.Faults.MoveFault = (_, destination) =>
        {
            if (Path.GetFileName(destination) == NvmeFile && !File.Exists(destination))
            {
                OldFiles.Put(rig.Baseline, NvmeFile, late); // another process wins the race
            }

            return null;
        };

        var result = rig.Run(Drives.Inspect(Drives.Nvme(media: 9, poh: 100)));

        Assert.Equal(1, result.Migration.AlreadyThere);
        Assert.Equal(0, result.Migration.Histories);
        Assert.Equal(7UL, BaselineAssert.Counter(result.Disks[0], "nvme.media_errors").Before);
    }

    [Fact]
    public void Old_entries_that_were_held_back_are_never_the_base()
    {
        using var rig = new BaselineRig();
        OldNvme(
            rig,
            new OldEntry(H(-4), OldFiles.Nvme(media: 0, poh: 96)),
            new OldEntry(H(-1), OldFiles.Nvme(media: 9, poh: 5000), Withheld: true));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(media: 2, poh: 100)));

        var media = BaselineAssert.Counter(result.Disks[0], "nvme.media_errors");
        Assert.Equal(0UL, media.Before);
        Assert.Equal(H(-4), media.SinceUtc);
        Assert.Contains("\"withheld\": true", rig.Read(NvmeFile));
    }

    [Fact]
    public void A_history_recorded_under_a_location_is_imported_and_a_past_twin_is_still_never_compared()
    {
        using var rig = new BaselineRig();
        var located = (string location, ulong media) => OldFiles.History(Drives.NvmeKey + "|loc=" + location, Model, new OldEntry(H(-2), OldFiles.Nvme(media: media)));
        OldFiles.Put(rig.Legacy, Drives.FileNameOf(Drives.NvmeKey + "|loc=0:0:0:0", "S6B1NJ0R123456"), located("0:0:0:0", 1));
        OldFiles.Put(rig.Legacy, Drives.FileNameOf(Drives.NvmeKey + "|loc=1:0:0:0", "S6B1NJ0R123456"), located("1:0:0:0", 3));
        OldFiles.Put(rig.Legacy, "twins.json", OldFiles.Twins(Drives.NvmeKey));

        var result = rig.Run(Drives.Inspect(
            Drives.Nvme(location: "0:0:0:0", media: 2, number: 0),
            Drives.Nvme(location: "1:0:0:0", media: 4, number: 1)));

        Assert.Equal(BaselineMigrationStatus.Complete, result.Migration.Status);
        Assert.Equal(2, result.Migration.Histories);
        Assert.All(result.Disks, static d => Assert.Equal(CheckEvidenceCode.BaselineDuplicate, d.Code));
        var first = rig.Read(Drives.FileNameOf(Drives.NvmeKey + "|loc=0:0:0:0", "S6B1NJ0R123456"));
        Assert.Equal(2, Entries(first)); // the imported record, then this run's value: kept apart, still never compared
        Assert.False(rig.Has(NvmeFile));
        Assert.Contains(Drives.NvmeKey, rig.Read("imported-twins.json"));
        Assert.False(rig.Has("twins.json"));
    }

    // ---- the twins first ----

    [Fact]
    public void The_old_twins_are_copied_before_any_history_and_a_lone_past_twin_is_not_compared()
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-2), OldFiles.Nvme(media: 1)));
        OldFiles.Put(rig.Legacy, "twins.json", OldFiles.Twins(Drives.NvmeKey));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(media: 5)));

        Assert.Equal(CheckEvidenceCode.BaselineDuplicate, result.Disks[0].Code);
        Assert.Equal(1, result.Migration.Twins);
        Assert.Equal(1, result.Migration.Histories);
        Assert.Equal(1, Entries(rig.Read(NvmeFile))); // only the imported record: a twin is not recorded without a location
        var log = rig.Faults.Log;
        var twinsAt = log.ToList().FindIndex(static e => e.EndsWith("-> imported-twins.json", StringComparison.Ordinal));
        var historyAt = log.ToList().FindIndex(static e => e.EndsWith("-> " + NvmeFile, StringComparison.Ordinal));
        Assert.True(twinsAt >= 0 && historyAt >= 0 && twinsAt < historyAt, string.Join(" | ", log));
        Assert.True(rig.Has("imported-twins.json"));
        Assert.False(rig.Has("twins.json"));
    }

    [Fact]
    public void The_marker_is_written_last_when_every_history_is_in_the_new_folder()
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-2), OldFiles.Nvme()));
        OldSata(rig, new OldEntry(H(-2), SataCounters()));
        OldFiles.Put(rig.Legacy, "twins.json", OldFiles.Twins());
        IReadOnlyList<string>? seenAtMark = null;
        rig.Files.ReplaceFault = (_, destination) =>
        {
            if (Path.GetFileName(destination) == "imported-v074.json")
            {
                seenAtMark = rig.Names();
            }

            return null;
        };

        rig.Run(Drives.Inspect(Drives.Nvme()));

        Assert.NotNull(seenAtMark);
        Assert.Contains(NvmeFile, seenAtMark!);
        Assert.Contains(SataFile, seenAtMark!); // the SATA history is imported too, although this run does not see that drive
        Assert.Contains("imported-twins.json", seenAtMark!);
    }

    [Fact]
    public void The_union_of_the_old_and_the_new_twins_is_used_and_each_file_stays_what_it_was()
    {
        using var rig = new BaselineRig();
        OldFiles.Put(rig.Legacy, "twins.json", OldFiles.Twins(Drives.SataKey));
        OldFiles.Put(rig.Baseline, "twins.json", OldFiles.Twins(Drives.NvmeKey));
        var own = File.ReadAllBytes(rig.PathOf("twins.json"));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(), Drives.Sata()));

        Assert.All(result.Disks, static d => Assert.Equal(CheckEvidenceCode.BaselineDuplicate, d.Code));
        Assert.Equal(own, File.ReadAllBytes(rig.PathOf("twins.json")));
        var imported = rig.Read("imported-twins.json");
        Assert.Contains(Drives.SataKey, imported);
        Assert.DoesNotContain(Drives.NvmeKey, imported);
    }

    [Fact]
    public void An_imported_twins_file_that_is_already_there_is_reused_not_replaced()
    {
        using var rig = new BaselineRig();
        OldFiles.Put(rig.Legacy, "twins.json", OldFiles.Twins(Drives.SataKey));
        OldFiles.Put(rig.Baseline, "imported-twins.json", OldFiles.Twins(Drives.NvmeKey));
        var existing = File.ReadAllBytes(rig.PathOf("imported-twins.json"));

        var result = rig.Run(Drives.Inspect(Drives.Nvme()));

        Assert.Equal(BaselineMigrationStatus.Complete, result.Migration.Status);
        Assert.Equal(existing, File.ReadAllBytes(rig.PathOf("imported-twins.json")));
        Assert.Equal(CheckEvidenceCode.BaselineDuplicate, result.Disks[0].Code); // the key that is in the file that was there
    }

    [Fact]
    public void An_imported_twins_file_that_cannot_be_written_stops_the_comparison_and_the_next_run_resumes()
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-2), OldFiles.Nvme(media: 1)));
        OldFiles.Put(rig.Legacy, "twins.json", OldFiles.Twins(Drives.SataKey));
        rig.Faults.CreateNewFault = path => Path.GetFileName(path).StartsWith("imported-twins.json.", StringComparison.Ordinal) ? new IOException("disk full") : null;

        var blocked = rig.Run(Drives.Inspect(Drives.Nvme(media: 4)));

        Assert.Equal(BaselineMigrationStatus.Blocked, blocked.Migration.Status);
        Assert.True(blocked.ReadFailed);
        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, blocked.Disks[0].Code);
        Assert.False(rig.Has("imported-twins.json"));
        Assert.False(rig.Has("imported-v074.json"));
        Assert.Empty(rig.DiskFiles()); // no history was copied and nothing was recorded without the twins

        rig.Faults.CreateNewFault = null;
        var resumed = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), media: 4, poh: 101)));

        Assert.Equal(BaselineMigrationStatus.Complete, resumed.Migration.Status);
        Assert.Equal(1UL, BaselineAssert.Counter(resumed.Disks[0], "nvme.media_errors").Before);
        Assert.True(rig.Has("imported-v074.json"));
    }

    [Theory]
    [InlineData("[ \"v2|sn=")]
    [InlineData("[ \"just some text\" ]")]
    [InlineData("{ \"a\": 1 }")]
    [InlineData("[ 5 ]")]
    [InlineData("")]
    public void A_set_of_twins_that_cannot_be_trusted_stops_the_comparison_with_no_mark_and_leaves_the_old_file_alone(string body)
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-2), OldFiles.Nvme(media: 1)));
        OldFiles.Put(rig.Legacy, "twins.json", body);
        var before = OldFiles.Snapshot(rig.Legacy);

        var blocked = rig.Run(Drives.Inspect(Drives.Nvme(media: 4)));

        Assert.Equal(BaselineMigrationStatus.Blocked, blocked.Migration.Status);
        Assert.True(blocked.StopsComparisonForTest());
        Assert.True(blocked.ReadFailed);
        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, blocked.Disks[0].Code);
        BaselineAssert.ValidRows(blocked);
        Assert.False(rig.Has("imported-v074.json"));
        Assert.False(rig.Has("imported-twins.json"));
        Assert.Empty(rig.DiskFiles());
        Assert.Equal(before, OldFiles.Snapshot(rig.Legacy));

        // The old file is made whole again: the import resumes.
        OldFiles.Put(rig.Legacy, "twins.json", OldFiles.Twins());
        var resumed = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), media: 4, poh: 101)));
        Assert.Equal(BaselineMigrationStatus.Complete, resumed.Migration.Status);
        Assert.Equal(BaselineDiskState.Compared, resumed.Disks[0].State);
    }

    [Fact]
    public void An_old_twins_file_that_cannot_be_read_now_blocks_the_comparison()
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-2), OldFiles.Nvme(media: 1)));
        OldFiles.Put(rig.Legacy, "twins.json", OldFiles.Twins());
        rig.OldSide.ReadFault = path => Path.GetFileName(path) == "twins.json" ? new IOException("sharing violation") : null;

        var blocked = rig.Run(Drives.Inspect(Drives.Nvme()));

        Assert.Equal(BaselineMigrationStatus.Blocked, blocked.Migration.Status);
        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, blocked.Disks[0].Code);
        Assert.False(rig.Has("imported-v074.json"));

        rig.OldSide.ReadFault = null;
        Assert.Equal(BaselineMigrationStatus.Complete, rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 101))).Migration.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_old_folder_that_cannot_be_listed_blocks_the_comparison_until_it_can(bool denied)
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-2), OldFiles.Nvme(media: 1)));
        rig.OldSide.ListFault = _ => denied ? new UnauthorizedAccessException("denied") : new IOException("sharing violation");

        var blocked = rig.Run(Drives.Inspect(Drives.Nvme()));

        Assert.Equal(BaselineMigrationStatus.Blocked, blocked.Migration.Status);
        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, blocked.Disks[0].Code);
        Assert.True(blocked.ReadFailed);
        Assert.Empty(rig.DiskFiles());

        rig.OldSide.ListFault = null;
        var resumed = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 101)));
        Assert.Equal(BaselineDiskState.Compared, resumed.Disks[0].State);
        Assert.Equal(1UL, BaselineAssert.Counter(resumed.Disks[0], "nvme.media_errors").Before);
    }

    // ---- interrupted halfway, then resumed ----

    private static StorageInspection ThreeDrives(DateTimeOffset at, ulong sataHours) => Drives.Inspect(
        at,
        [
            Drives.Nvme(at: at, media: 4, poh: 100 + (ulong)(at - T0).TotalHours),
            SecondNvme(at, media: 0, poh: 100 + (ulong)(at - T0).TotalHours),
            Drives.Sata(at: at, hours: sataHours),
        ]);

    private static BaselineRig ThreeOldHistories()
    {
        var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-2), OldFiles.Nvme(media: 1, poh: 98)));
        OldSecond(rig, new OldEntry(H(-2), OldFiles.Nvme(media: 0, poh: 98)));
        OldSata(rig, new OldEntry(H(-1), SataCounters(900)));
        return rig;
    }

    private static void InterruptedThenResumed(Action<BaselineRig> breakIt, Action<BaselineRig> repair)
    {
        using var rig = ThreeOldHistories();
        var before = OldFiles.Snapshot(rig.Legacy);
        breakIt(rig);

        var halfway = rig.Run(ThreeDrives(T0, 1000));

        Assert.Equal(BaselineMigrationStatus.Incomplete, halfway.Migration.Status);
        Assert.Equal(2, halfway.Migration.Histories);
        Assert.False(rig.Has("imported-v074.json"));
        Assert.True(halfway.ReadFailed);
        // The histories that were copied are used; the one that was not is not compared and not recorded.
        Assert.Equal(BaselineDiskState.Compared, halfway.Disks[0].State);
        Assert.Equal(H(-2), BaselineAssert.Counter(halfway.Disks[0], "nvme.media_errors").SinceUtc);
        Assert.Equal(BaselineDiskState.Compared, halfway.Disks[1].State);
        Assert.Equal(CheckEvidenceCode.BaselineReadFailed, halfway.Disks[2].Code);
        BaselineAssert.ValidRows(halfway);
        Assert.False(rig.Has(SataFile));
        Assert.Equal(2, rig.DiskFiles().Count);
        Assert.Equal(before, OldFiles.Snapshot(rig.Legacy));

        repair(rig);
        var resumed = rig.Run(ThreeDrives(H(1), 1001));

        Assert.Equal(BaselineMigrationStatus.Complete, resumed.Migration.Status);
        Assert.Equal(1, resumed.Migration.Histories); // only the one that was missing is copied now
        Assert.Equal(2, resumed.Migration.AlreadyThere);
        Assert.False(resumed.ReadFailed);
        Assert.True(rig.Has("imported-v074.json"));
        var sata = BaselineAssert.Counter(resumed.Disks[2], "ata.9");
        Assert.Equal(900UL, sata.Before);
        Assert.Equal(H(-1), sata.SinceUtc);
        // The two that were already compared continue from the record of the interrupted run, not from the old folder.
        var media = BaselineAssert.Counter(resumed.Disks[0], "nvme.media_errors");
        Assert.Equal(4UL, media.Before);
        Assert.Equal(T0, media.SinceUtc);
        Assert.Equal(3, Entries(rig.Read(NvmeFile)));
        Assert.Equal(2, Entries(rig.Read(SataFile)));
        Assert.Equal(before, OldFiles.Snapshot(rig.Legacy));

        var third = rig.Run(ThreeDrives(H(2), 1002));
        Assert.Equal(BaselineMigrationStatus.NotNeeded, third.Migration.Status);
    }

    [Fact]
    public void A_copy_that_could_not_be_written_is_picked_up_by_the_next_run()
    {
        InterruptedThenResumed(
            rig => rig.Faults.CreateNewFault = path =>
            {
                var name = Path.GetFileName(path);
                return name.StartsWith("disk-WD-WX12A-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal) ? new IOException("disk full") : null;
            },
            rig => rig.Faults.CreateNewFault = null);
    }

    [Fact]
    public void An_old_file_that_the_old_tool_is_holding_is_picked_up_by_the_next_run()
    {
        InterruptedThenResumed(
            rig => rig.OldSide.ReadFault = path => Path.GetFileName(path) == SataFile ? new IOException("sharing violation") : null,
            rig => rig.OldSide.ReadFault = null);
    }

    [Fact]
    public void A_marker_that_could_not_be_written_is_written_by_the_next_run_and_the_comparison_goes_on_meanwhile()
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-2), OldFiles.Nvme(media: 1)));
        rig.Files.ReplaceFault = (_, destination) => Path.GetFileName(destination) == "imported-v074.json" ? new IOException("disk full") : null;

        var first = rig.Run(Drives.Inspect(Drives.Nvme(media: 3)));

        Assert.Equal(BaselineMigrationStatus.Incomplete, first.Migration.Status);
        Assert.Equal(BaselineDiskState.Compared, first.Disks[0].State); // every history is in: nothing is held back
        Assert.False(first.ReadFailed);
        Assert.False(rig.Has("imported-v074.json"));
        Assert.DoesNotContain(rig.Names(), static n => n.EndsWith(".tmp", StringComparison.Ordinal));

        rig.Files.ReplaceFault = null;
        var second = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), media: 3, poh: 101)));

        Assert.Equal(BaselineMigrationStatus.Complete, second.Migration.Status);
        Assert.Equal(1, second.Migration.AlreadyThere);
        Assert.True(rig.Has("imported-v074.json"));
    }

    [Fact]
    public void An_old_file_that_is_gone_when_it_is_read_is_not_an_error()
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-2), OldFiles.Nvme(media: 1)));
        rig.OldSide.ReadFault = path => Path.GetFileName(path) == NvmeFile ? new FileNotFoundException() : null;

        var result = rig.Run(Drives.Inspect(Drives.Nvme()));

        Assert.Equal(BaselineMigrationStatus.Complete, result.Migration.Status);
        Assert.Equal(BaselineDiskState.NotComparable, result.Disks[0].State);
        Assert.Equal(CheckEvidenceCode.BaselineFirst, result.Disks[0].Code);
    }

    // ---- files that are not used ----

    [Theory]
    [InlineData("{ nope")]
    [InlineData("[]")]
    [InlineData("{'key':'{KEY}','model':'m'}")]
    [InlineData("{'key':'{KEY}','model':'m','entries':[{'at':'yesterday','counters':{}}]}")]
    [InlineData("{'key':'{KEY}','model':'m','entries':[{'at':'2026-11-19T09:00:00+09:00','counters':{'nvme.media_errors':-1}}]}")]
    [InlineData("{'key':'{KEY}','model':'m','entries':[{'at':'2026-11-19T09:00:00+09:00','counters':{'nvme.media_errors':'5'}}]}")]
    [InlineData("{'key':'{KEY}','model':'m','entries':[{'at':'2026-11-19T09:00:00+09:00','counters':{'Bad Name':5}}]}")]
    [InlineData("{'key':'{KEY}','model':'m','entries':[{'at':'2026-11-19T09:00:00+09:00','counters':{'nvme.media_errors':18446744073709551616}}]}")]
    [InlineData("{'key':'not a key','model':'m','entries':[]}")]
    public void A_damaged_old_history_is_skipped_and_left_alone_and_the_import_still_completes(string body)
    {
        using var rig = new BaselineRig();
        OldFiles.Put(rig.Legacy, NvmeFile, body.Replace("{KEY}", Drives.NvmeKey, StringComparison.Ordinal).Replace('\'', '"'));
        OldSata(rig, new OldEntry(H(-1), SataCounters(900)));
        var before = OldFiles.Snapshot(rig.Legacy);

        var result = rig.Run(Drives.Inspect(Drives.Nvme(), Drives.Sata(hours: 905)));

        Assert.Equal(BaselineMigrationStatus.Complete, result.Migration.Status);
        Assert.Equal(1, result.Migration.Skipped);
        Assert.Equal(1, result.Migration.Histories);
        Assert.Equal(CheckEvidenceCode.BaselineFirst, result.Disks[0].Code);
        Assert.Equal(BaselineDiskState.Compared, result.Disks[1].State);
        Assert.Equal(1, Entries(rig.Read(NvmeFile))); // this run's record only: the bad file was not copied
        Assert.True(rig.Has("imported-v074.json"));
        Assert.Equal(before, OldFiles.Snapshot(rig.Legacy));
    }

    [Fact]
    public void A_history_whose_key_does_not_match_the_hash_in_its_file_name_is_skipped()
    {
        using var rig = new BaselineRig();
        // The NVMe history under the name of the SATA key's hash: someone renamed or copied it.
        var misnamed = $"disk-S6B1NJ0R123456-{Drives.HashOf(Drives.SataKey)}.json";
        OldFiles.Put(rig.Legacy, misnamed, OldFiles.History(Drives.NvmeKey, Model, new OldEntry(H(-2), OldFiles.Nvme(media: 1))));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(), Drives.Sata()));

        Assert.Equal(1, result.Migration.Skipped);
        Assert.Equal(0, result.Migration.Histories);
        Assert.False(rig.Has(misnamed));
        Assert.Equal(CheckEvidenceCode.BaselineFirst, result.Disks[0].Code);
        Assert.Equal(CheckEvidenceCode.BaselineFirst, result.Disks[1].Code);
    }

    [Fact]
    public void A_history_with_more_than_the_limit_of_entries_is_not_taken()
    {
        using var rig = new BaselineRig();
        var entries = Enumerable.Range(0, 201).Select(i => new OldEntry(T0.AddMinutes(-1000 + i), OldFiles.Nvme())).ToArray();
        OldNvme(rig, entries);

        var result = rig.Run(Drives.Inspect(Drives.Nvme()));

        Assert.Equal(1, result.Migration.Skipped);
        Assert.Equal(CheckEvidenceCode.BaselineFirst, result.Disks[0].Code);
    }

    // ---- v1 ----

    private static OldEntry V1Entry() => new(H(-9), OldFiles.Nvme(media: 5));

    [Fact]
    public void A_v1_file_is_refused_not_carried_over_and_the_next_run_compares_with_the_new_record()
    {
        using var rig = new BaselineRig();
        OldFiles.Put(rig.Legacy, "disk-S6B1NJ0R123456.json", OldFiles.History("S6B1NJ0R123456", Model, V1Entry()));
        var before = OldFiles.Snapshot(rig.Legacy);

        var first = rig.Run(Drives.Inspect(Drives.Nvme(media: 6)));
        var second = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), media: 6, poh: 101)));

        Assert.Equal(CheckEvidenceCode.BaselineLegacyRefused, first.Disks[0].Code);
        Assert.All(first.Disks[0].Counters, static c => Assert.Equal(CheckEvidenceCode.BaselineLegacyRefused, c.Code));
        BaselineAssert.ValidRows(first);
        Assert.Equal(BaselineMigrationStatus.NothingToImport, first.Migration.Status);
        Assert.False(rig.Has("imported-v074.json"));
        Assert.Equal(before, OldFiles.Snapshot(rig.Legacy)); // the old file is kept, byte for byte
        Assert.Equal(BaselineDiskState.Compared, second.Disks[0].State);
        Assert.Equal(6UL, BaselineAssert.Counter(second.Disks[0], "nvme.media_errors").Before);
        Assert.Equal(T0, BaselineAssert.Counter(second.Disks[0], "nvme.media_errors").SinceUtc);
        Assert.Equal(2, Entries(rig.Read(NvmeFile)));
    }

    [Fact]
    public void A_v1_file_with_another_model_is_not_that_disk()
    {
        using var rig = new BaselineRig();
        OldFiles.Put(rig.Legacy, "disk-S6B1NJ0R123456.json", OldFiles.History("S6B1NJ0R123456", "Some Other Model", V1Entry()));

        Assert.Equal(CheckEvidenceCode.BaselineFirst, rig.Run(Drives.Inspect(Drives.Nvme())).Disks[0].Code);
    }

    [Fact]
    public void A_v1_file_with_another_key_or_no_entries_is_not_that_disk()
    {
        using var rig = new BaselineRig();
        OldFiles.Put(rig.Legacy, "disk-S6B1NJ0R123456.json", OldFiles.History("SOMETHING-ELSE", Model, V1Entry()));
        Assert.Equal(CheckEvidenceCode.BaselineFirst, rig.Run(Drives.Inspect(Drives.Nvme())).Disks[0].Code);

        using var other = new BaselineRig();
        OldFiles.Put(other.Legacy, "disk-S6B1NJ0R123456.json", OldFiles.History("S6B1NJ0R123456", Model));
        Assert.Equal(CheckEvidenceCode.BaselineFirst, other.Run(Drives.Inspect(Drives.Nvme())).Disks[0].Code);
    }

    [Fact]
    public void A_v1_file_is_not_used_when_two_drives_share_the_same_name_for_it()
    {
        using var rig = new BaselineRig();
        // "AB.CD" and "AB_CD" are both disk-AB_CD.json in v1: which drive the file belonged to cannot be said.
        OldFiles.Put(rig.Legacy, "disk-AB_CD.json", OldFiles.History("AB.CD", Model, V1Entry()));

        var result = rig.Run(Drives.Inspect(
            Drives.Nvme(serial: "AB.CD", uniqueId: "eui.0000000000000001", number: 0),
            Drives.Nvme(serial: "AB_CD", uniqueId: "eui.0000000000000002", number: 1)));

        Assert.All(result.Disks, static d => Assert.Equal(CheckEvidenceCode.BaselineFirst, d.Code));
    }

    [Fact]
    public void A_v1_file_that_cannot_be_read_is_not_an_error_of_the_run()
    {
        using var rig = new BaselineRig();
        OldFiles.Put(rig.Legacy, "disk-S6B1NJ0R123456.json", OldFiles.History("S6B1NJ0R123456", Model, V1Entry()));
        rig.OldSide.ReadFault = path => Path.GetFileName(path) == "disk-S6B1NJ0R123456.json" ? new IOException("sharing violation") : null;

        var result = rig.Run(Drives.Inspect(Drives.Nvme()));

        Assert.Equal(CheckEvidenceCode.BaselineFirst, result.Disks[0].Code);
        Assert.False(result.ReadFailed);
    }

    // ---- when nothing is imported ----

    [Fact]
    public void A_valid_marker_ends_the_import_and_the_old_folder_is_not_looked_at_again()
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-2), OldFiles.Nvme(media: 1)));
        rig.Run(Drives.Inspect(Drives.Nvme(media: 2)));
        var reads = rig.OldSide.Reads.Count;
        rig.OldSide.ListFault = _ => new InvalidOperationException("the old folder must not be listed again");
        OldSata(rig, new OldEntry(H(-2), SataCounters())); // a file that shows up later is not imported any more

        var next = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), media: 2, poh: 101), Drives.Sata()));

        Assert.Equal(BaselineMigrationStatus.NotNeeded, next.Migration.Status);
        // The only look at the old side is the v1 lookup of a disk seen for the first time (it is what makes baseline_legacy_refused possible).
        var later = rig.OldSide.Reads.Skip(reads).ToList();
        Assert.All(later, static name => Assert.Equal(BaselineNames.LegacyV1FileName("WD-WX12A"), name));
        Assert.Equal(CheckEvidenceCode.BaselineFirst, next.Disks[1].Code);
        Assert.False(rig.Has(SataFile) && Entries(rig.Read(SataFile)) > 1);
    }

    [Fact]
    public void A_damaged_marker_is_replaced_by_a_real_one_after_the_import_is_done_again()
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-2), OldFiles.Nvme(media: 1)));
        OldFiles.Put(rig.Baseline, "imported-v074.json", "{ nope");

        var result = rig.Run(Drives.Inspect(Drives.Nvme(media: 2)));

        Assert.Equal(BaselineMigrationStatus.Complete, result.Migration.Status);
        Assert.Contains("completedAtUtc", rig.Read("imported-v074.json"));
        Assert.Equal(1, result.Migration.Histories);
    }

    [Fact]
    public void Without_an_old_folder_there_is_no_import_and_no_mark()
    {
        using var rig = new BaselineRig(withLegacy: false);

        var result = rig.Run(Drives.Inspect(Drives.Nvme()));

        Assert.Equal(BaselineMigrationStatus.NotNeeded, result.Migration.Status);
        Assert.False(rig.Has("imported-v074.json"));
        Assert.Empty(rig.OldSide.Reads);
    }

    [Fact]
    public void An_old_folder_that_does_not_exist_or_has_nothing_of_ours_leaves_no_mark_so_a_later_arrival_is_still_imported()
    {
        using var rig = new BaselineRig();

        var missing = rig.Run(Drives.Inspect(Drives.Nvme()));
        Assert.Equal(BaselineMigrationStatus.NothingToImport, missing.Migration.Status);
        Assert.False(rig.Has("imported-v074.json"));

        OldFiles.Put(rig.Legacy, "readme.txt", "not ours");
        OldFiles.Put(rig.Legacy, "disk-ordinals.json", "{}");
        var empty = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 101)));
        Assert.Equal(BaselineMigrationStatus.NothingToImport, empty.Migration.Status);

        // Histories arrive later (a roaming profile that syncs late): the next run imports them where there is no history yet.
        OldSata(rig, new OldEntry(H(-1), SataCounters(900)));
        var later = rig.Run(Drives.Inspect(Drives.Nvme(at: H(2), poh: 102), Drives.Sata(at: H(2), hours: 903)));
        Assert.Equal(BaselineMigrationStatus.Complete, later.Migration.Status);
        Assert.Equal(900UL, BaselineAssert.Counter(later.Disks[1], "ata.9").Before);
    }

    [Fact]
    public void An_old_folder_with_only_twins_is_imported_and_marked()
    {
        using var rig = new BaselineRig();
        OldFiles.Put(rig.Legacy, "twins.json", OldFiles.Twins(Drives.SataKey));

        var result = rig.Run(Drives.Inspect(Drives.Nvme(), Drives.Sata()));

        Assert.Equal(BaselineMigrationStatus.Complete, result.Migration.Status);
        Assert.Equal(1, result.Migration.Twins);
        Assert.Equal(CheckEvidenceCode.BaselineFirst, result.Disks[0].Code);
        Assert.Equal(CheckEvidenceCode.BaselineDuplicate, result.Disks[1].Code);
    }

    [Fact]
    public void The_report_carries_counts_only()
    {
        using var rig = new BaselineRig();
        OldNvme(rig, new OldEntry(H(-2), OldFiles.Nvme()));

        var report = rig.Run(Drives.Inspect(Drives.Nvme())).Migration;

        // Everything a caller can read from the report is a number or a status: no key, serial, path or file name.
        var properties = typeof(BaselineMigrationReport).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.All(properties, static p => Assert.True(p.PropertyType == typeof(int) || p.PropertyType == typeof(bool) || p.PropertyType == typeof(BaselineMigrationStatus), p.Name));
        Assert.Equal(1, report.Histories);
    }
}

internal static class BaselineRunResultExtensions
{
    /// <summary>The comparison was stopped by the import (not by a single file): the result's flag and the report agree.</summary>
    public static bool StopsComparisonForTest(this BaselineRunResult result) => result.Migration.StopsComparison;
}
