using System.Text.Json;
using PcMieru.Checks.Storage;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Tests.Storage;

/// <summary>
/// Whose record is whose (DESIGN-A §5.2, §11): the key, its file name, drives that cannot be told apart, twins that are never compared,
/// unreadable slots, and the numbers "disk-N". Ported from v0.7.4's BaselineIdentityTests with the same serials and numbers.
/// </summary>
public class BaselineIdentityTests
{
    private static readonly DateTimeOffset T0 = Drives.T0;

    private static DateTimeOffset H(double hours) => T0.AddHours(hours);

    private static string[] TwinsOf(BaselineRig rig) => JsonSerializer.Deserialize<string[]>(rig.Read("twins.json"))!;

    private static Dictionary<string, int> OrdinalsOf(BaselineRig rig)
    {
        using var document = JsonDocument.Parse(rig.Read("disk-ordinals.json"));
        return document.RootElement.GetProperty("ordinals").EnumerateObject().ToDictionary(static p => p.Name, static p => p.Value.GetInt32());
    }

    private static int Entries(string text) => text.Split("\"at\":").Length - 1;

    private static string LocatedFile(string location, string serial = "S6B1NJ0R123456") =>
        Drives.FileNameOf(Drives.NvmeKey + "|loc=" + location, serial);

    private static StorageInspectionDrive Weak(DateTimeOffset? at = null, int number = 0, ulong media = 0) =>
        Drives.Nvme(serial: "", at: at, media: media, uniqueId: "", number: number);

    // ---- the key and its file ----

    [Fact]
    public void The_file_holds_the_exact_legacy_key_and_is_named_after_the_serial_and_the_hash_of_the_key()
    {
        using var rig = new BaselineRig();

        rig.Run(Drives.Inspect(Drives.Nvme()));
        rig.Run(Drives.Inspect(Drives.Sata()));

        Assert.True(rig.Has(Drives.FileNameOf(Drives.NvmeKey, "S6B1NJ0R123456")));
        Assert.True(rig.Has(Drives.FileNameOf(Drives.SataKey, "WD-WX12A")));
        Assert.StartsWith("disk-S6B1NJ0R123456-", Drives.FileNameOf(Drives.NvmeKey, "S6B1NJ0R123456"));
        Assert.Contains("\"key\": \"" + Drives.SataKey + "\"", rig.Read(Drives.FileNameOf(Drives.SataKey, "WD-WX12A")));
        Assert.Equal(2, rig.DiskFiles().Count);
    }

    [Fact]
    public void A_serial_with_a_bar_a_percent_and_a_trailing_underscore_is_escaped_in_the_key_and_cleaned_in_the_name()
    {
        using var rig = new BaselineRig();

        rig.Run(Drives.Inspect(Drives.Nvme(serial: "SN%7|A_")));

        const string key = "v2|sn=SN%257%7CA_|uid=eui.0025385b21a01234|model=Samsung SSD 980 PRO 2TB|size=2000398934016|bus=Nvme";
        var name = Drives.FileNameOf(key, "SN%7|A_");
        Assert.StartsWith("disk-SN_7_A-", name); // "SN_7_A_" without the trailing underscore
        Assert.Equal(new[] { name }, rig.DiskFiles());
        Assert.Contains("\"key\": \"" + key + "\"", rig.Read(name));
    }

    [Fact]
    public void A_long_serial_is_cut_to_forty_characters_in_the_name_only()
    {
        using var rig = new BaselineRig();
        var serial = new string('A', 60);

        rig.Run(Drives.Inspect(Drives.Nvme(serial: serial)));

        var file = Assert.Single(rig.DiskFiles());
        Assert.StartsWith("disk-" + new string('A', 40) + "-", file);
        Assert.DoesNotContain(new string('A', 41), file);
        Assert.Contains("sn=" + serial + "|", rig.Read(file));
    }

    [Fact]
    public void A_placeholder_serial_with_a_unique_id_is_strong_and_the_name_uses_the_model()
    {
        using var rig = new BaselineRig();

        var result = rig.Run(Drives.Inspect(Drives.Nvme(serial: "000000000000")));

        Assert.Equal(CheckEvidenceCode.BaselineFirst, result.Disks[0].Code);
        const string key = "v2|sn=000000000000|uid=eui.0025385b21a01234|model=Samsung SSD 980 PRO 2TB|size=2000398934016|bus=Nvme";
        var name = Drives.FileNameOf(key, "Samsung SSD 980 PRO 2TB");
        Assert.StartsWith("disk-Samsung_SSD_980_PRO_2TB-", name);
        Assert.Equal(new[] { name }, rig.DiskFiles());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("000000000000")]
    [InlineData("..__--")]
    public void A_drive_with_no_usable_serial_and_no_unique_id_is_never_compared_or_recorded(string serial)
    {
        using var rig = new BaselineRig();

        var first = rig.Run(Drives.Inspect(Drives.Nvme(serial: serial, uniqueId: "")));
        var second = rig.Run(Drives.Inspect(Drives.Nvme(serial: serial, uniqueId: "", at: H(1), poh: 101)));

        Assert.Equal(CheckEvidenceCode.BaselineNoIdentity, first.Disks[0].Code);
        Assert.Equal(CheckEvidenceCode.BaselineNoIdentity, second.Disks[0].Code);
        Assert.False(first.ReadFailed);
        Assert.Empty(rig.DiskFiles());
        Assert.False(rig.Has("twins.json"));
        Assert.False(rig.Has("disk-ordinals.json"));
        BaselineAssert.ValidRows(second);
    }

    [Fact]
    public void Two_drives_with_no_serial_are_weak_twins_that_are_neither_recorded_nor_written_to_twins_json()
    {
        using var rig = new BaselineRig();

        var first = rig.Run(Drives.Inspect(Weak(number: 0, media: 0), Weak(number: 1, media: 363_278)));
        var second = rig.Run(Drives.Inspect(Weak(number: 0, media: 0), Weak(number: 1, media: 363_278)));

        Assert.All(first.Disks, static d => Assert.Equal(CheckEvidenceCode.BaselineNoIdentity, d.Code));
        Assert.All(second.Disks, static d => Assert.Equal(CheckEvidenceCode.BaselineNoIdentity, d.Code));
        Assert.Empty(rig.DiskFiles());
        Assert.False(rig.Has("twins.json"));
        Assert.Equal(["disk-1", "disk-2"], second.Disks.Select(static d => d.DiskId));
    }

    [Fact]
    public void Serials_that_the_file_name_cleaning_makes_equal_get_separate_files_and_histories()
    {
        using var rig = new BaselineRig();
        StorageInspectionDrive A(DateTimeOffset at, ulong media) => Drives.Nvme(serial: "AB/CD 12", uniqueId: "", at: at, media: media, number: 0, poh: 100 + (ulong)at.Hour);
        StorageInspectionDrive B(DateTimeOffset at, ulong media) => Drives.Nvme(serial: "AB CD/12", uniqueId: "", at: at, media: media, number: 1, poh: 100 + (ulong)at.Hour);

        rig.Run(Drives.Inspect(A(T0, 0), B(T0, 363_278)));
        var second = rig.Run(Drives.Inspect(A(H(1), 0), B(H(1), 363_301)));

        Assert.Equal(2, rig.DiskFiles().Count);
        Assert.All(rig.DiskFiles(), static f => Assert.StartsWith("disk-AB_CD_12-", f));
        Assert.Equal(0UL, BaselineAssert.Counter(second.Disks[0], "nvme.media_errors").Before);
        Assert.Equal(363_278UL, BaselineAssert.Counter(second.Disks[1], "nvme.media_errors").Before);
        Assert.Equal(363_301UL, BaselineAssert.Counter(second.Disks[1], "nvme.media_errors").After);
        Assert.True(BaselineAssert.Counter(second.Disks[1], "nvme.media_errors").Grew);
        Assert.False(BaselineAssert.Counter(second.Disks[0], "nvme.media_errors").Grew);
    }

    [Fact]
    public void A_bar_in_a_value_cannot_make_one_disks_key_equal_to_anothers()
    {
        using var rig = new BaselineRig();

        var result = rig.Run(Drives.Inspect(
            Drives.Nvme(serial: "A|uid=B", uniqueId: "", number: 0),
            Drives.Nvme(serial: "A", uniqueId: "B", number: 1)));

        Assert.All(result.Disks, static d => Assert.Equal(CheckEvidenceCode.BaselineFirst, d.Code));
        Assert.Equal(2, rig.DiskFiles().Count);
        Assert.False(rig.Has("twins.json"));
    }

    [Fact]
    public void A_different_bus_is_another_key_even_with_the_same_serial()
    {
        using var rig = new BaselineRig();

        var result = rig.Run(Drives.Inspect(
            Drives.Nvme(serial: "SAME1", uniqueId: "", number: 0),
            Drives.Nvme(serial: "SAME1", uniqueId: "", number: 1) with { BusType = 7 }));

        Assert.All(result.Disks, static d => Assert.Equal(CheckEvidenceCode.BaselineFirst, d.Code));
        Assert.False(rig.Has("twins.json"));
        Assert.Equal(2, rig.DiskFiles().Count);
        Assert.Contains(Drives.FileNameOf("v2|sn=SAME1|uid=|model=Samsung SSD 980 PRO 2TB|size=2000398934016|bus=Usb", "SAME1"), rig.DiskFiles());
    }

    // ---- twins ----

    [Fact]
    public void Two_disks_with_the_same_key_are_twins_recorded_by_location_and_never_compared()
    {
        using var rig = new BaselineRig();
        var a1 = Drives.Nvme(at: T0, location: "0:0:0:0", media: 0, number: 0);
        var b1 = Drives.Nvme(at: T0, location: "1:0:0:0", media: 363_278, number: 1);

        var first = rig.Run(Drives.Inspect(a1, b1));
        var second = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 101, location: "0:0:0:0", media: 0, number: 0), Drives.Nvme(at: H(1), poh: 101, location: "1:0:0:0", media: 363_301, number: 1)));

        Assert.All(first.Disks, static d => Assert.Equal(CheckEvidenceCode.BaselineDuplicate, d.Code));
        Assert.All(second.Disks, static d => Assert.Equal(CheckEvidenceCode.BaselineDuplicate, d.Code));
        Assert.All(second.Disks.SelectMany(static d => d.Counters), static c => Assert.Equal(CheckEvidenceCode.BaselineDuplicate, c.Code));
        BaselineAssert.ValidRows(second);
        Assert.Equal([Drives.NvmeKey], TwinsOf(rig));
        // The value of every run is kept under the key with the location; the plain key has no file at all.
        Assert.Equal(new[] { LocatedFile("0:0:0:0"), LocatedFile("1:0:0:0") }.Order(StringComparer.Ordinal), rig.DiskFiles());
        Assert.False(rig.Has(Drives.FileNameOf(Drives.NvmeKey, "S6B1NJ0R123456")));
        Assert.Equal(2, Entries(rig.Read(LocatedFile("1:0:0:0"))));
        Assert.Contains("\"nvme.media_errors\": 363301", rig.Read(LocatedFile("1:0:0:0")));
        Assert.Contains("\"key\": \"" + Drives.NvmeKey + "|loc=1:0:0:0\"", rig.Read(LocatedFile("1:0:0:0")));
        // A twin has no stable number: the pair is not in disk-ordinals.json.
        Assert.False(rig.Has("disk-ordinals.json"));
        Assert.Equal(["disk-1", "disk-2"], second.Disks.Select(static d => d.DiskId));
    }

    [Fact]
    public void Twins_that_swap_places_are_not_compared_and_nothing_says_the_numbers_grew()
    {
        using var rig = new BaselineRig();
        StorageInspectionDrive Twin(string location, double spare, ulong media, DateTimeOffset at, int number) =>
            Drives.Nvme(at: at, location: location, media: media, spare: spare, number: number, poh: 100 + (ulong)(at - T0).TotalHours);
        rig.Run(Drives.Inspect(Twin("0:0:0:0", 100, 0, T0, 0), Twin("1:0:0:0", 90, 363_278, T0, 1)));

        // The cable was swapped: the sick drive is now where the healthy one was.
        var swapped = rig.Run(Drives.Inspect(Twin("0:0:0:0", 90, 363_278, H(24), 0), Twin("1:0:0:0", 100, 0, H(24), 1)));

        Assert.All(swapped.Disks, static d => Assert.Equal(CheckEvidenceCode.BaselineDuplicate, d.Code));
        Assert.All(swapped.Disks.SelectMany(static d => d.Counters), static c => Assert.False(c.IsCompared));
        Assert.All(BaselineAssert.ValidRows(swapped), static r => Assert.Null(r.Value));
    }

    [Fact]
    public void A_twin_seen_once_is_never_compared_again_even_when_alone()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0, location: "0:0:0:0", number: 0), Drives.Nvme(at: T0, location: "1:0:0:0", media: 363_278, number: 1)));

        var moved = rig.Run(Drives.Inspect(Drives.Nvme(at: H(24), poh: 124, location: "2:0:0:0", media: 363_278)));
        var back = rig.Run(Drives.Inspect(Drives.Nvme(at: H(48), poh: 148, location: "0:0:0:0")));

        Assert.Equal(CheckEvidenceCode.BaselineDuplicate, moved.Disks[0].Code);
        Assert.Equal(CheckEvidenceCode.BaselineDuplicate, back.Disks[0].Code);
        // The value is still kept, under the key of the place where it was seen.
        Assert.True(rig.Has(LocatedFile("2:0:0:0")));
        Assert.Equal(2, Entries(rig.Read(LocatedFile("0:0:0:0"))));
        Assert.Equal([Drives.NvmeKey], TwinsOf(rig));
    }

    [Fact]
    public void Twins_without_a_location_are_not_recorded_at_all_and_stay_twins()
    {
        using var rig = new BaselineRig();

        var first = rig.Run(Drives.Inspect(Drives.Nvme(number: 0), Drives.Nvme(media: 363_278, number: 1)));
        var alone = rig.Run(Drives.Inspect(Drives.Nvme(at: H(24), poh: 124, media: 363_278)));

        Assert.All(first.Disks, static d => Assert.Equal(CheckEvidenceCode.BaselineDuplicate, d.Code));
        Assert.Equal(CheckEvidenceCode.BaselineDuplicate, alone.Disks[0].Code);
        Assert.Empty(rig.DiskFiles());
        Assert.Equal([Drives.NvmeKey], TwinsOf(rig));
    }

    [Fact]
    public void Twins_that_report_the_same_location_cannot_be_separated_and_are_not_recorded()
    {
        using var rig = new BaselineRig();

        var result = rig.Run(Drives.Inspect(Drives.Nvme(location: "0:0:0:0", number: 0), Drives.Nvme(location: "0:0:0:0", number: 1)));

        Assert.All(result.Disks, static d => Assert.Equal(CheckEvidenceCode.BaselineDuplicate, d.Code));
        Assert.Empty(rig.DiskFiles());
        Assert.Equal([Drives.NvmeKey], TwinsOf(rig));
    }

    [Fact]
    public void A_lone_disk_beside_twins_is_compared_as_before()
    {
        using var rig = new BaselineRig();
        StorageInspectionDrive Twin(string location, DateTimeOffset at, ulong reallocated, int number) =>
            Drives.Sata(serial: "TWINSSD", at: at, hours: 12_000 + (ulong)(at - T0).TotalHours, reallocated: reallocated, location: location, number: number);
        StorageInspectionDrive Lone(string location, DateTimeOffset at, ulong reallocated) =>
            Drives.Sata(serial: "LONEHDD", at: at, hours: 30_000 + (ulong)(at - T0).TotalHours, reallocated: reallocated, pending: 8, location: location, number: 3);
        rig.Run(Drives.Inspect(Twin("0:0:0:0", T0, 0, 1), Twin("1:0:0:0", T0, 8, 2), Lone("2:0:0:0", T0, 10)));

        // The lone disk moved to another place: one drive of its key is still one drive.
        var result = rig.Run(Drives.Inspect(Twin("0:0:0:0", H(24), 0, 1), Lone("3:0:0:0", H(24), 12)));

        Assert.Equal(CheckEvidenceCode.BaselineDuplicate, result.Disks[0].Code);
        var lone = result.Disks[1];
        Assert.Equal(BaselineDiskState.Compared, lone.State);
        Assert.Equal(2, (int)BaselineAssert.Counter(lone, "ata.5").Change);
        BaselineAssert.ValidRows(result);
        var rows = BaselineDeltaRows.Build(lone, new CheckSubject(lone.DiskId));
        var reallocated = BaselineAssert.Row(rows, "storage.ata.5.delta");
        Assert.Equal(ItemVerdict.Check, reallocated.Verdict);
        Assert.Equal("2", reallocated.Value!.AsText);
        Assert.Equal(ItemVerdict.Ok, BaselineAssert.Row(rows, "storage.ata.197.delta").Verdict);
        Assert.Equal("0", BaselineAssert.Row(rows, "storage.ata.197.delta").Value!.AsText);
    }

    [Fact]
    public void A_twin_pair_found_on_a_later_run_makes_the_earlier_single_record_unusable()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0)));

        // A second drive with the same key appears: from now on neither is compared with the record of "the one".
        var pair = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 101, location: "0:0:0:0", number: 0), Drives.Nvme(at: H(1), poh: 101, location: "1:0:0:0", media: 9, number: 1)));
        var alone = rig.Run(Drives.Inspect(Drives.Nvme(at: H(2), poh: 102, location: "0:0:0:0")));

        Assert.All(pair.Disks, static d => Assert.Equal(CheckEvidenceCode.BaselineDuplicate, d.Code));
        Assert.Equal(CheckEvidenceCode.BaselineDuplicate, alone.Disks[0].Code);
        Assert.Equal(1, Entries(rig.Read(Drives.FileNameOf(Drives.NvmeKey, "S6B1NJ0R123456")))); // untouched since the single run
    }

    // ---- unreadable slots (DESIGN-A §5.2) ----

    [Fact]
    public void A_strong_key_is_still_compared_when_a_slot_could_not_be_read()
    {
        using var rig = new BaselineRig();

        var first = rig.Run(Drives.Inspect(Drives.Nvme(at: T0), Drives.Unreadable(5, T0)));
        var second = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 101, media: 2), Drives.Unreadable(5, H(1))));

        Assert.Equal(CheckEvidenceCode.BaselineFirst, first.Disks[0].Code);
        Assert.Equal(BaselineDiskState.Compared, second.Disks[0].State);
        Assert.True(BaselineAssert.Counter(second.Disks[0], "nvme.media_errors").Grew);
        Assert.Equal(BaselineDiskState.NoCounters, second.Disks[1].State);
        Assert.Equal(2, Entries(rig.Read(Drives.FileNameOf(Drives.NvmeKey, "S6B1NJ0R123456"))));
    }

    [Fact]
    public void A_strong_key_is_still_compared_when_a_slot_could_not_even_be_listed()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(T0, [Drives.Nvme(at: T0)], unreadableSlots: 1));

        var second = rig.Run(Drives.Inspect(H(1), [Drives.Nvme(at: H(1), poh: 101)], unreadableSlots: 1));

        Assert.Equal(BaselineDiskState.Compared, second.Disks[0].State);
    }

    [Fact]
    public void A_weak_key_with_an_unreadable_slot_says_the_inventory_is_incomplete_and_without_one_it_says_no_identity()
    {
        using var rig = new BaselineRig();

        var incomplete = rig.Run(Drives.Inspect(Weak(T0), Drives.Unreadable(5, T0)));
        var complete = rig.Run(Drives.Inspect(Weak(T0)));
        var silent = rig.Run(Drives.Inspect(T0, [Weak(T0)], unreadableSlots: 2));

        Assert.Equal(CheckEvidenceCode.BaselineInventoryIncomplete, incomplete.Disks[0].Code);
        Assert.Equal(CheckEvidenceCode.BaselineNoIdentity, complete.Disks[0].Code);
        Assert.Equal(CheckEvidenceCode.BaselineInventoryIncomplete, silent.Disks[0].Code);
        Assert.All(incomplete.Disks[0].Counters, static c => Assert.Equal(CheckEvidenceCode.BaselineInventoryIncomplete, c.Code));
        BaselineAssert.ValidRows(incomplete);
        Assert.Empty(rig.DiskFiles());
    }

    [Fact]
    public void Twins_with_locations_beside_an_unreadable_slot_are_still_recorded_but_say_the_inventory_is_incomplete()
    {
        using var rig = new BaselineRig();

        var result = rig.Run(Drives.Inspect(
            Drives.Nvme(location: "0:0:0:0", number: 0),
            Drives.Nvme(location: "1:0:0:0", number: 1),
            Drives.Unreadable(5)));

        Assert.Equal(CheckEvidenceCode.BaselineInventoryIncomplete, result.Disks[0].Code);
        Assert.Equal(CheckEvidenceCode.BaselineInventoryIncomplete, result.Disks[1].Code);
        Assert.Equal(BaselineDiskState.NoCounters, result.Disks[2].State);
        BaselineAssert.ValidRows(result);
        Assert.Equal(2, rig.DiskFiles().Count);
        Assert.Equal([Drives.NvmeKey], TwinsOf(rig));
    }

    [Fact]
    public void Twins_that_cannot_be_told_apart_beside_an_unreadable_slot_are_duplicates()
    {
        using var rig = new BaselineRig();

        var result = rig.Run(Drives.Inspect(Drives.Nvme(number: 0), Drives.Nvme(number: 1), Drives.Unreadable(5)));

        Assert.Equal(CheckEvidenceCode.BaselineDuplicate, result.Disks[0].Code);
        Assert.Equal(CheckEvidenceCode.BaselineDuplicate, result.Disks[1].Code);
        Assert.Empty(rig.DiskFiles());
    }

    [Fact]
    public void A_past_twin_beside_an_unreadable_slot_follows_the_same_rule()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(location: "0:0:0:0", number: 0), Drives.Nvme(location: "1:0:0:0", number: 1)));

        var located = rig.Run(Drives.Inspect(Drives.Nvme(at: H(1), poh: 101, location: "0:0:0:0"), Drives.Unreadable(5, H(1))));
        var unlocated = rig.Run(Drives.Inspect(Drives.Nvme(at: H(2), poh: 102), Drives.Unreadable(5, H(2))));

        Assert.Equal(CheckEvidenceCode.BaselineInventoryIncomplete, located.Disks[0].Code);
        Assert.Equal(CheckEvidenceCode.BaselineDuplicate, unlocated.Disks[0].Code);
    }

    [Fact]
    public void Unreadable_slots_never_form_twins_among_themselves_or_with_a_readable_drive()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(at: T0)));

        var result = rig.Run(Drives.Inspect(Drives.Unreadable(5, H(1)), Drives.Unreadable(6, H(1)), Drives.Nvme(at: H(1), poh: 101)));

        Assert.False(rig.Has("twins.json"));
        Assert.Equal(BaselineDiskState.Compared, result.Disks[2].State);
        Assert.Equal(BaselineDiskState.NoCounters, result.Disks[0].State);
        Assert.Equal(BaselineDiskState.NoCounters, result.Disks[1].State);
    }

    // ---- the numbers of the disks ----

    [Fact]
    public void A_strong_key_keeps_its_number_from_the_first_run_whatever_the_order_of_the_drives()
    {
        using var rig = new BaselineRig();

        var first = rig.Run(Drives.Inspect(Drives.Nvme(number: 0), Drives.Sata(number: 1)));
        var reordered = rig.Run(Drives.Inspect(H(1), [Drives.Sata(at: H(1), number: 1), Drives.Nvme(at: H(1), poh: 101, number: 0)]));

        Assert.Equal(["disk-1", "disk-2"], first.Disks.Select(static d => d.DiskId));
        Assert.Equal(["disk-2", "disk-1"], reordered.Disks.Select(static d => d.DiskId));
        var stored = OrdinalsOf(rig);
        Assert.Equal(1, stored[Drives.HashOf(Drives.NvmeKey)]);
        Assert.Equal(2, stored[Drives.HashOf(Drives.SataKey)]);
        Assert.DoesNotContain("S6B1NJ0R123456", rig.Read("disk-ordinals.json")); // hashes only: no serial in it
    }

    [Fact]
    public void A_number_is_never_given_to_another_key_even_when_its_disk_is_gone()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme()));

        var other = rig.Run(Drives.Inspect(Drives.Sata(at: H(1))));
        var back = rig.Run(Drives.Inspect(Drives.Nvme(at: H(2), poh: 102)));

        Assert.Equal("disk-2", other.Disks[0].DiskId);
        Assert.Equal("disk-1", back.Disks[0].DiskId);
    }

    [Fact]
    public void A_new_key_gets_the_largest_number_plus_one()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(number: 0), Drives.Sata(number: 1)));
        var third = Drives.Nvme(serial: "THIRD-DISK-1", uniqueId: "", number: 2);

        var result = rig.Run(Drives.Inspect(H(1), [Drives.Nvme(at: H(1), poh: 101, number: 0), third with { SampledAt = H(1) }]));

        Assert.Equal(["disk-1", "disk-3"], result.Disks.Select(static d => d.DiskId));
    }

    [Fact]
    public void A_drive_without_a_stable_number_takes_the_lowest_number_nobody_in_this_result_has()
    {
        using var rig = new BaselineRig();
        rig.Run(Drives.Inspect(Drives.Nvme(number: 0), Drives.Sata(number: 1)));

        // Weak and unreadable drives come first in the list but never take a number that a stable key has.
        var crowded = rig.Run(Drives.Inspect(H(1), [Weak(H(1)), Drives.Unreadable(4, H(1)), Drives.Nvme(at: H(1), poh: 101), Drives.Sata(at: H(1))]));
        // The number 1 is free in this result (the NVMe drive is away): the weak drive may use it.
        var gap = rig.Run(Drives.Inspect(H(2), [Weak(H(2)), Drives.Sata(at: H(2))]));

        Assert.Equal(["disk-3", "disk-4", "disk-1", "disk-2"], crowded.Disks.Select(static d => d.DiskId));
        Assert.Equal(["disk-1", "disk-2"], gap.Disks.Select(static d => d.DiskId));
        // They are not stored: only the two strong keys are in the file.
        Assert.Equal(2, OrdinalsOf(rig).Count);
    }

    [Fact]
    public void A_number_that_cannot_be_stored_is_temporary_and_the_next_run_stores_it()
    {
        using var rig = new BaselineRig();
        rig.Files.ReplaceFault = (_, destination) => Path.GetFileName(destination) == "disk-ordinals.json" ? new IOException("disk full") : null;

        var first = rig.Run(Drives.Inspect(Drives.Nvme(number: 0), Drives.Sata(number: 1)));
        rig.Files.ReplaceFault = null;
        var second = rig.Run(Drives.Inspect(H(1), [Drives.Sata(at: H(1)), Drives.Nvme(at: H(1), poh: 101)]));

        Assert.Equal(["disk-1", "disk-2"], first.Disks.Select(static d => d.DiskId));
        Assert.Equal(BaselineDiskState.NotComparable, first.Disks[0].State); // the histories were recorded all the same
        Assert.Equal(BaselineDiskState.Compared, second.Disks[1].State);
        Assert.Equal(["disk-1", "disk-2"], second.Disks.Select(static d => d.DiskId)); // in order of first appearance now
        Assert.Equal(2, OrdinalsOf(rig).Count);
    }

    [Fact]
    public void A_damaged_numbering_file_is_kept_aside_and_the_numbering_starts_again()
    {
        using var rig = new BaselineRig();
        OldFiles.Put(rig.Baseline, "disk-ordinals.json", "{ not json");

        var result = rig.Run(Drives.Inspect(Drives.Nvme()));

        Assert.Equal("disk-1", result.Disks[0].DiskId);
        Assert.Equal("{ not json", rig.Read("disk-ordinals.json.bad-20261120T090000123Z"));
        Assert.Single(OrdinalsOf(rig));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_damaged_numbering_file_that_cannot_be_kept_aside_is_not_written_over_and_the_numbers_are_temporary(bool denied)
    {
        using var rig = new BaselineRig();
        OldFiles.Put(rig.Baseline, "disk-ordinals.json", "{ not json");
        var damaged = File.ReadAllBytes(rig.PathOf("disk-ordinals.json"));
        rig.FailQuarantine(denied ? new UnauthorizedAccessException("denied") : null);

        var first = rig.Run(Drives.Inspect(Drives.Nvme(number: 0), Drives.Sata(number: 1)));

        // The numbers are for this run only, like a numbering file that could not be written. The histories are not the numbering's
        // business: they are recorded as usual.
        Assert.Equal(["disk-1", "disk-2"], first.Disks.Select(static d => d.DiskId));
        Assert.False(first.ReadFailed);
        Assert.Equal(2, rig.DiskFiles().Count);
        Assert.Equal(damaged, File.ReadAllBytes(rig.PathOf("disk-ordinals.json"))); // not rewritten: nothing was kept of it
        Assert.DoesNotContain(rig.Names(), static n => n.Contains(".bad-", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Names(), static n => n.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.DoesNotContain("disk-ordinals.json", rig.Files.Replaces);

        // The move works on the next run: the file is kept aside and the numbers are stored.
        rig.Faults.MoveFault = null;
        var second = rig.Run(Drives.Inspect(H(1), [Drives.Nvme(at: H(1), poh: 101, number: 0), Drives.Sata(at: H(1), hours: 1001, number: 1)]));

        Assert.Equal(["disk-1", "disk-2"], second.Disks.Select(static d => d.DiskId));
        Assert.Equal("{ not json", rig.Read("disk-ordinals.json.bad-20261120T090000123Z"));
        Assert.Equal(2, OrdinalsOf(rig).Count);
        Assert.Equal(BaselineDiskState.Compared, second.Disks[0].State);
    }
}
