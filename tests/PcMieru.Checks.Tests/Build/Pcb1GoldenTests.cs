using System.Text;
using System.Text.Json.Nodes;
using PcMieru.Checks.Build;

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

    private static async Task<BuildCodeResult> MakeAsync(string name)
    {
        var material = Pcb1Material.Load(FilePath(name, ".material.json"));
        return BuildCode.Make(await BuildCollector.CollectAsync(material.ToSources()));
    }

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
}
