using System.Text;
using System.Text.Json.Nodes;
using PcMieru.Checks.Build;
using PcMieru.Checks.Tests.SystemInfo;

namespace PcMieru.Checks.Tests.Build;

/// <summary>
/// The configuration code PCB1 against the files the OLD code made (parts-life 0.7.4, pl-cloud 25e4571; tools/pcb1-golden). The code is
/// read by the /my-config page of web-autoai.com, so it must stay **byte for byte** what the old version wrote: this is the contract, and
/// these files are the proof. The material is synthetic (no real name, serial, path or volume label); regenerate only with the harness.
/// </summary>
public sealed class Pcb1GoldenTests
{
    private static readonly string[] CaseNames =
    [
        "jp-and-en-names",
        "same-model-several",
        "xmp-configured-speed",
        "vram-over-4gib",
        "usb-and-virtual-disk",
        "optical-drive",
        "every-category-missing",
        "throwing-category",
        "length-cap-exceeded",
        "length-cap-memory-first-kind",
        "length-cap-last-resort",
    ];

    public static TheoryData<string> Cases
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in CaseNames)
            {
                data.Add(name);
            }

            return data;
        }
    }

    private static string Dir => Path.Combine(Fixture.Dir, "pcb1");

    private static string FilePath(string name, string extension) => Path.Combine(Dir, name + extension);

    // The same way in as the app has for a machine: the sources, the collector and Make, through the provider.
    private static Task<BuildCodeResult> MakeAsync(Pcb1Material material) =>
        new SourcesBuildCodeProvider(material.ToSources).CreateAsync(default);

    private static Task<BuildCodeResult> MakeAsync(string name) =>
        MakeAsync(Pcb1Material.Load(FilePath(name, ".material.json")));

    [Fact]
    public void The_fixture_folder_holds_exactly_the_agreed_cases_and_all_their_files()
    {
        var found = Directory.GetFiles(Dir, "*.material.json")
            .Select(f => Path.GetFileName(f)[..^".material.json".Length])
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(CaseNames.Order(StringComparer.Ordinal).ToArray(), found);

        foreach (var name in CaseNames)
        {
            foreach (var ext in new[] { ".code.txt", ".json", ".notes.txt" })
            {
                Assert.True(File.Exists(FilePath(name, ext)), $"{name}{ext} is missing");
            }
        }

        // Nothing else lies there (a stray file would be a golden nobody checks).
        Assert.Equal(CaseNames.Length * 4, Directory.GetFiles(Dir).Length);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_code_is_byte_equal_to_the_one_the_old_version_made(string name)
    {
        var expected = File.ReadAllBytes(FilePath(name, ".code.txt"));
        var result = await MakeAsync(name);

        Assert.Equal(Encoding.UTF8.GetString(expected), result.Code);
        Assert.True(expected.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(result.Code)), $"{name}: the UTF-8 bytes of the code differ");
        Assert.StartsWith("PCB1.", result.Code, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_saved_json_is_content_equal_to_the_one_the_old_version_made(string name)
    {
        var expected = File.ReadAllText(FilePath(name, ".json"), Encoding.UTF8);
        var result = await MakeAsync(name);

        // On Windows the saved text has CRLF line ends; the goldens are LF (the line end is not part of the content).
        Assert.Equal(Fixture.Normalize(expected), Fixture.Normalize(result.Json));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_number_of_notes_is_the_number_of_the_old_ones(string name)
    {
        // The old notes were bilingual sentences; the new ones are codes (BuildNote). One note stands for one sentence.
        int expected = int.Parse(File.ReadAllText(FilePath(name, ".notes.txt"), Encoding.UTF8).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var result = await MakeAsync(name);

        Assert.Equal(expected, result.Notes.Count);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_golden_code_and_the_golden_json_describe_the_same_parts(string name)
    {
        // A check on the files themselves (they came from one run of the old code): a hand edit of one of them is caught here.
        var code = File.ReadAllText(FilePath(name, ".code.txt"), Encoding.UTF8);
        Assert.DoesNotContain("\n", code, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", code, StringComparison.Ordinal);

        var fromCode = JsonNode.Parse(BuildCode.Decode(code));
        var fromJson = JsonNode.Parse(File.ReadAllText(FilePath(name, ".json"), Encoding.UTF8));
        Assert.True(JsonNode.DeepEquals(fromCode, fromJson), $"{name}: the code and the saved json differ");
        Assert.Equal(1, fromCode!["v"]!.GetValue<int>());
        Assert.Equal("parts-life", fromCode["src"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Every_category_is_in_the_agreed_order(string name)
    {
        var result = await MakeAsync(name);

        // The order of the categories in the code is the order of BuildConfig.Categories, whatever order the sources were read in.
        var inCode = JsonNode.Parse(BuildCode.Decode(result.Code))!["p"]!.AsObject().Select(kv => kv.Key).ToArray();
        Assert.Equal(BuildConfig.Categories.Where(inCode.Contains).ToArray(), inCode);
        Assert.Equal(new[] { "cpu", "gpu", "motherboard", "ram", "ssd", "hdd", "monitor", "optical-drive" }, BuildConfig.Categories.ToArray());
    }

    // ---- what each case stands for: the golden would still match if the material no longer showed it, so the material is held too

    [Fact]
    public async Task Japanese_names_stay_readable_and_a_character_outside_the_bmp_is_a_surrogate_pair()
    {
        var result = await MakeAsync("jp-and-en-names");

        Assert.Contains("インテル(R) Core(TM) i7-14700K プロセッサー", result.Json, StringComparison.Ordinal);
        Assert.Contains("\\uD842\\uDFB7", result.Json, StringComparison.Ordinal);
        Assert.Contains("\\u003CTest\\u003E", result.Json, StringComparison.Ordinal);
        // Full-width spaces become ordinary spaces.
        Assert.DoesNotContain("\u3000", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Equal_parts_are_merged_into_q_and_q_stops_at_16()
    {
        var c = (await MakeAsync("same-model-several")).Config;

        Assert.Equal(16, c["monitor"].Single(p => p.Name == "UG27PJ").Q);
        Assert.Equal(2, c["monitor"].Count);
        Assert.Equal(4, c["ram"].Single(p => p.Name == "CMK64GX5M2B5600Z40").Q);
        Assert.Equal(2, c["gpu"].Single().Q);
        Assert.Equal(3, c["ssd"].Single().Q);
    }

    [Fact]
    public async Task The_configured_speed_is_used_when_it_is_above_the_rated_one_or_the_rated_one_is_missing()
    {
        var c = (await MakeAsync("xmp-configured-speed")).Config;

        int? Speed(string name) => c["ram"].Single(p => p.Name == name).Speed;
        Assert.Equal(5600, Speed("CMK64GX5M2B5600Z40")); // rated 4800, configured 5600 (XMP/EXPO)
        Assert.Equal(3200, Speed("F4-3200C16-16GVK")); // configured below the rated one: the rated one
        Assert.Equal(2666, Speed("HX426C16FB3/16")); // configured unknown
        Assert.Equal(3000, Speed("RATED UNKNOWN 8GB")); // rated unknown
        Assert.Equal(7, c["ram"].Count);
        Assert.Equal(9, c.Count("ram"));
    }

    [Fact]
    public async Task Video_memory_over_4_gib_is_counted_in_gb_and_a_device_off_the_pci_bus_is_not_a_second_gpu()
    {
        var c = (await MakeAsync("vram-over-4gib")).Config;

        Assert.Contains(c["gpu"], p => p.Name == "AMD Radeon RX 7900 XTX" && p.Vram == 24);
        Assert.Contains(c["gpu"], p => p.Name.StartsWith("Intel(R) Arc", StringComparison.Ordinal) && p.Vram == 16);
        Assert.Contains(c["gpu"], p => p.Name.Contains("RTX 5070", StringComparison.Ordinal) && p.Vram == 12);
        Assert.Equal(1, c["gpu"].Count(p => p.Name.Contains("RTX 5070", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_usb_disk_and_a_virtual_device_are_not_in_the_storage_lists()
    {
        var c = (await MakeAsync("usb-and-virtual-disk")).Config;

        var all = c["ssd"].Concat(c["hdd"]).Select(p => p.Name).ToArray();
        Assert.DoesNotContain(all, n => n.Contains("Cruzer", StringComparison.Ordinal));
        Assert.DoesNotContain(all, n => n.Contains("External", StringComparison.Ordinal));
        Assert.DoesNotContain(all, n => n.Contains("Virtual", StringComparison.Ordinal));
        Assert.DoesNotContain(all, n => n.Contains("E823_", StringComparison.Ordinal));
        Assert.Equal(4, c["ssd"].Count);
        Assert.Equal(2, c["hdd"].Count);
    }

    [Fact]
    public async Task Optical_drives_keep_the_real_ones_and_drop_the_virtual_ones()
    {
        var c = (await MakeAsync("optical-drive")).Config;

        Assert.Equal(new[] { "optical-drive" }, c.NonEmpty.ToArray());
        Assert.DoesNotContain(c["optical-drive"], p => p.Name.Contains("Virtual", StringComparison.Ordinal));
        Assert.Equal(3, c.Count("optical-drive"));
    }

    [Fact]
    public async Task With_every_category_missing_the_code_has_no_parts_and_still_has_its_header()
    {
        var r = await MakeAsync("every-category-missing");

        Assert.True(r.Config.IsEmpty);
        Assert.Equal("{\"v\":1,\"src\":\"parts-life\",\"p\":{}}", BuildCode.Decode(r.Code));
        Assert.Empty(r.Notes);
    }

    [Fact]
    public async Task A_category_whose_source_throws_is_empty_and_the_others_are_kept()
    {
        var material = Pcb1Material.Load(FilePath("throwing-category", ".material.json"));
        Assert.Equal(new[] { "board", "gpus", "monitors" }, material.Throws.ToArray());

        var r = await MakeAsync("throwing-category");

        Assert.Equal(new[] { "cpu", "ram", "ssd", "optical-drive" }, r.Config.NonEmpty.ToArray());
        Assert.Empty(r.Config["motherboard"]);
        Assert.Empty(r.Config["gpu"]);
        Assert.Empty(r.Config["monitor"]);
    }

    [Fact]
    public async Task Over_the_length_limit_the_optical_drive_the_monitor_and_the_hdd_go_first()
    {
        var r = await MakeAsync("length-cap-exceeded");

        Assert.Equal(
            new[]
            {
                new BuildNote(BuildNoteKind.CategoryLeftOutForLength, "optical-drive"),
                new BuildNote(BuildNoteKind.CategoryLeftOutForLength, "monitor"),
                new BuildNote(BuildNoteKind.CategoryLeftOutForLength, "hdd"),
            },
            r.Notes);
        Assert.Equal(new[] { "cpu", "gpu", "motherboard", "ram", "ssd" }, r.Config.NonEmpty.ToArray());
        Assert.True(r.Code.Length - BuildCode.Prefix.Length <= BuildCode.MaxLength);
    }

    [Fact]
    public async Task Still_over_the_limit_memory_is_cut_to_its_first_kind()
    {
        var r = await MakeAsync("length-cap-memory-first-kind");

        Assert.Equal(
            new[]
            {
                new BuildNote(BuildNoteKind.CategoryLeftOutForLength, "optical-drive"),
                new BuildNote(BuildNoteKind.CategoryLeftOutForLength, "monitor"),
                new BuildNote(BuildNoteKind.CategoryLeftOutForLength, "hdd"),
                new BuildNote(BuildNoteKind.MemoryFirstKindOnly, "ram"),
            },
            r.Notes);
        Assert.Single(r.Config["ram"]);
        Assert.Equal(8, r.Config["ssd"].Count);
        Assert.True(r.Code.Length - BuildCode.Prefix.Length <= BuildCode.MaxLength);
    }

    [Fact]
    public async Task As_a_last_resort_one_part_at_a_time_is_cut_from_ssd_first_then_gpu()
    {
        var r = await MakeAsync("length-cap-last-resort");

        var oneByOne = r.Notes.Where(n => n.Kind == BuildNoteKind.OnePartLeftOutForLength).Select(n => n.Category).ToArray();
        Assert.Equal(new[] { "ssd", "ssd", "ssd", "ssd", "ssd", "ssd", "ssd", "gpu", "gpu" }, oneByOne);
        Assert.Equal(4, r.Notes.Count - oneByOne.Length);
        Assert.Single(r.Config["ssd"]);
        Assert.Equal(6, r.Config["gpu"].Count);
        Assert.Equal(8, r.Config["cpu"].Count);
        Assert.True(r.Code.Length - BuildCode.Prefix.Length <= BuildCode.MaxLength);
    }

    [Fact]
    public async Task A_source_that_throws_is_listed_as_failed_and_the_code_is_still_the_golden_one()
    {
        var r = await MakeAsync("throwing-category");

        // Listed in the order the code writes the categories, not the order the collector read them (the board is read before the gpu).
        Assert.Equal(
            new[] { new BuildUnread("gpu", CheckReason.Error), new BuildUnread("motherboard", CheckReason.Error), new BuildUnread("monitor", CheckReason.Error) },
            r.Unread.ToArray());
        Assert.Equal(File.ReadAllText(FilePath("throwing-category", ".code.txt"), Encoding.UTF8), r.Code);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_golden_machine_has_an_unread_category_only_when_its_source_throws(string name)
    {
        var material = Pcb1Material.Load(FilePath(name, ".material.json"));
        var r = await MakeAsync(material);

        // A category that is empty because the machine has nothing in it was read: it is not unread.
        Assert.Equal(material.Throws.Count, r.Unread.Count);
        Assert.All(r.Unread, u => Assert.Equal(CheckReason.Error, u.Reason));
    }

    // ---- the temporary material of the real-machine comparison: no USB serial in the file, and a null is read as the harness reads it

    private const string OpticalName = "HL-DT-ST BD-RE WH16NS60";

    // Real ids end in a number the device made up, and for a USB drive that can be its serial number. These are invented.
    public static TheoryData<string> OpticalIds => new()
    {
        @"SCSI\CDROM&VEN_HL-DT-ST&PROD_BD-RE_WH16NS60\4&2A5B3C1D&0&010000", // an ordinary internal drive: counted
        @"IDE\CDROMHL-DT-ST_BD-RE__WH16NS60_______\5&1F0E2D3C&0&0.0.0",
        @"USBSTOR\CDROM&VEN_ASUS&PROD_SDRW-08D2S-U&REV_1.00\0123456789ABCDEF&0", // a USB drive (prefix USB): not counted
        @"usb\vid_0e8d&pid_1806\serial-synthetic-0001",                          // the prefix is not case sensitive
        @"SCSI\CDROM&VEN_MSFT&PROD_VIRTUAL_DVD-ROM\2&1F4ADFCF&0&000002",          // the virtual drive of an opened ISO: not counted
        @"scsi\cdrom&ven_msft&prod_virtual_dvd-rom\1",                            // MSFT is not case sensitive either
        @"USBSTOR\CDROM&VEN_MSFT&PROD_X\SERIAL1",                                 // both
        @"xxxUSB\CDROM\1",                                                        // USB inside, not at the start: counted
        @"SCSI\CDROM&VEN_USB&PROD_ONLY_INSIDE\1&MSF\T",                           // pieces of the words that do not make them: counted
        "",                                                                       // no id: counted
    };

    [Theory]
    [MemberData(nameof(OpticalIds))]
    public async Task A_reduced_optical_id_gives_the_same_code_as_the_full_one_under_the_old_rule(string pnpDeviceId)
    {
        static Pcb1Material With(string id)
        {
            var m = new Pcb1Material();
            m.Cpus.Add("AMD Ryzen 7 5800X 8-Core Processor");
            m.Opticals.Add(new RawOptical(OpticalName, id));
            return m;
        }

        var reducedId = Pcb1Material.ReduceOpticalId(pnpDeviceId);
        var full = await MakeAsync(With(pnpDeviceId));
        var reduced = await MakeAsync(With(reducedId));

        Assert.Equal(full.Code, reduced.Code);
        Assert.Equal(full.Json, reduced.Json);

        // The old rule, written out again here (parts-life 0.7.4: not "MSFT" anywhere and not "USB" at the start, both ignoring case).
        bool counted = !(pnpDeviceId.Contains("MSFT", StringComparison.OrdinalIgnoreCase) || pnpDeviceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(counted ? 1 : 0, full.Config["optical-drive"].Count);
        Assert.Equal(counted ? 1 : 0, reduced.Config["optical-drive"].Count);

        // What is left of the id is at most the two words the rule looks for, and reducing twice changes nothing.
        Assert.Contains(reducedId, new[] { "", "USB", "MSFT", "USBMSFT" });
        Assert.Equal(reducedId, Pcb1Material.ReduceOpticalId(reducedId));
    }

    [Fact]
    public void A_missing_optical_id_is_reduced_to_nothing()
    {
        Assert.Equal("", Pcb1Material.ReduceOpticalId(null));
    }

    [Fact]
    public async Task The_material_read_from_windows_has_no_usb_serial_and_makes_the_same_code_as_the_full_ids()
    {
        const string serial = "0123456789ABCDEF"; // invented
        const string usbName = "ASUS SDRW-08D2S-U";
        var usbId = @"USBSTOR\CDROM&VEN_ASUS&PROD_SDRW-08D2S-U\" + serial + "&0";
        var internalId = @"SCSI\CDROM&VEN_HL-DT-ST&PROD_BD-RE_WH16NS60\4&" + serial + "&0&010000";
        var wmi = new ScriptedWmi().Rows(
            WindowsBuildReaders.OpticalQuery.Text,
            ScriptedWmi.Row(("Name", usbName), ("PNPDeviceID", usbId)),
            ScriptedWmi.Row(("Name", OpticalName), ("PNPDeviceID", internalId)));
        var readers = new WindowsBuildReaders(wmi, ScriptedGpu.Ok(), new ManualTimeProvider());

        var material = await Pcb1Material.ReadAsync(readers);

        Assert.DoesNotContain(serial, material.ToJson(), StringComparison.Ordinal);
        Assert.Equal(new[] { "USB", "" }, material.Opticals.Select(o => o.PnpDeviceId).ToArray());
        // The names are as they were read (the rules clean them later).
        Assert.Equal(new[] { usbName, OpticalName }, material.Opticals.Select(o => o.Name).ToArray());

        var full = new Pcb1Material();
        full.Opticals.Add(new RawOptical(usbName, usbId));
        full.Opticals.Add(new RawOptical(OpticalName, internalId));
        Assert.Equal((await MakeAsync(full)).Code, (await MakeAsync(material)).Code);
    }

    [Fact]
    public async Task A_null_in_a_material_is_read_the_way_the_harness_reads_it()
    {
        // A null list is an empty list, a null text of a row is "", the board may stay null (tools/pcb1-golden/Program.cs, Make).
        const string json = """
            {
              "cpus": null,
              "board": null,
              "memory": [ { "partNumber": null, "manufacturer": null, "capacity": 17179869184, "speed": 3200, "smbiosType": 26, "memoryType": 0, "configuredSpeed": 0 } ],
              "gpus": null,
              "disks": [ { "name": null, "busType": 11, "mediaType": 3, "size": 2000398934016 } ],
              "monitors": [ { "name": null, "active": true } ],
              "opticals": [ { "name": "HL-DT-ST BD-RE WH16NS60", "pnpDeviceId": null }, { "name": null, "pnpDeviceId": "SCSI\\CDROM\\1" } ],
              "throws": null
            }
            """;

        var material = Pcb1Material.Parse(json);

        Assert.Empty(material.Cpus);
        Assert.Empty(material.Gpus);
        Assert.Empty(material.Throws);
        Assert.Null(material.Board);
        var stick = Assert.Single(material.Memory);
        Assert.Equal(("", ""), (stick.PartNumber, stick.Manufacturer));
        Assert.Equal("", Assert.Single(material.Disks).Name);
        Assert.Equal("", Assert.Single(material.Monitors).Name);
        Assert.Equal(new[] { "", @"SCSI\CDROM\1" }, material.Opticals.Select(o => o.PnpDeviceId).ToArray());
        Assert.Equal("", material.Opticals[1].Name);

        // It can be turned into a code (a null id is an id with nothing in it, so the drive is counted), and no category is unread.
        var r = await MakeAsync(material);
        Assert.Empty(r.Unread);
        Assert.Equal(OpticalName, Assert.Single(r.Config["optical-drive"]).Name);

        // And it survives being saved and read again.
        Assert.Equal(material.ToJson(), Pcb1Material.Parse(material.ToJson()).ToJson());
    }

    [Theory]
    [InlineData("""{ "memory": [ null ] }""")]
    [InlineData("""{ "opticals": [ null ] }""")]
    [InlineData("null")]
    public void A_row_that_is_itself_null_is_refused(string json)
    {
        Assert.Throws<InvalidDataException>(() => Pcb1Material.Parse(json));
    }
}
