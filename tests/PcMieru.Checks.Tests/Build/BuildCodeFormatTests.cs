// Provenance: BuildCodeFormatTests of PartsLife.Checks.Tests (pl-cloud 25e4571, tests/PartsLife.Checks.Tests/BuildCodeTests.cs), MIT, same owner.
// The cases and their expected values are the old ones; the names are new, the comments are in English, and a note is now a structured value
// (a kind, a category and a count) instead of a bilingual sentence.
using System.Text;
using System.Text.Json;
using PcMieru.Checks.Build;

namespace PcMieru.Checks.Tests.Build;

/// <summary>
/// The configuration code (the string pasted into /my-config of web-autoai.com). **It is a contract made together with the receiver, so
/// the form itself is tested.** The byte-for-byte match with the old version is in <see cref="Pcb1GoldenTests"/>.
/// </summary>
public class BuildCodeFormatTests
{
    private static BuildConfig Config(params (string Cat, BuildPart Part)[] parts)
    {
        var c = new BuildConfig();
        foreach (var g in parts.GroupBy(p => p.Cat))
        {
            c.Set(g.Key, g.Select(p => p.Part));
        }

        return c;
    }

    private static JsonElement Parse(string code) => JsonDocument.Parse(BuildCode.Decode(code)).RootElement;

    [Fact]
    public void Code_is_prefix_plus_base64url_of_utf8_json_and_round_trips()
    {
        // The run of ~ and ? is + and / in plain base64 (so that base64url, - and _, is really what is used).
        var c = Config(
            ("cpu", new BuildPart("AMD Ryzen 7 5800X 8-Core Processor ~~~~~~ ??????")),
            ("ram", new BuildPart("F4-3200C16-16GVK", Cap: 16, Speed: 3200, Type: "DDR4", Mfr: "G.Skill", Q: 2)),
            ("monitor", new BuildPart("ＤＥＬＬ 日本語の名前")));
        var r = BuildCode.Make(c);

        Assert.StartsWith("PCB1.", r.Code);
        string body = r.Code["PCB1.".Length..];
        Assert.Matches("^[A-Za-z0-9_-]+$", body); // no padding (=), no + and /

        string json = BuildCode.ToJson(r.Config, indented: false);
        string standard = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        Assert.Contains("+", standard);
        Assert.Contains("/", standard);
        Assert.Equal(standard.TrimEnd('=').Replace('+', '-').Replace('/', '_'), body);

        Assert.Equal(json, BuildCode.Decode(r.Code));
        var root = Parse(r.Code);
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("parts-life", root.GetProperty("src").GetString());
        var ram = root.GetProperty("p").GetProperty("ram")[0];
        Assert.Equal("F4-3200C16-16GVK", ram.GetProperty("n").GetString());
        Assert.Equal(16, ram.GetProperty("cap").GetInt32());
        Assert.Equal(3200, ram.GetProperty("spd").GetInt32());
        Assert.Equal("DDR4", ram.GetProperty("t").GetString());
        Assert.Equal("G.Skill", ram.GetProperty("mfr").GetString());
        Assert.Equal(2, ram.GetProperty("q").GetInt32());
        Assert.Equal("ＤＥＬＬ 日本語の名前", root.GetProperty("p").GetProperty("monitor")[0].GetProperty("n").GetString());
    }

    [Fact]
    public void Json_has_only_known_keys_omits_empty_categories_and_q_of_one()
    {
        var c = Config(("gpu", new BuildPart("NVIDIA GeForce RTX 3080 Ti", Vram: 12)));
        c.Set("ssd", Array.Empty<BuildPart>());
        string json = BuildCode.ToJson(BuildCode.Normalize(c), indented: false);
        Assert.Equal("""{"v":1,"src":"parts-life","p":{"gpu":[{"n":"NVIDIA GeForce RTX 3080 Ti","vram":12}]}}""", json);
    }

    [Fact]
    public void Categories_are_written_in_the_fixed_order_and_the_fields_of_a_part_in_the_fixed_order()
    {
        var c = new BuildConfig();
        c.Set("optical-drive", [new BuildPart("Synthetic Optical")]);
        c.Set("monitor", [new BuildPart("Synthetic Monitor")]);
        c.Set("hdd", [new BuildPart("Synthetic Hard Disk", Cap: 2000, Type: "sata")]);
        c.Set("ssd", [new BuildPart("Synthetic Solid State", Cap: 1000, Type: "nvme")]);
        c.Set("ram", [new BuildPart("Synthetic Memory", Cap: 16, Speed: 3200, Type: "DDR4", Mfr: "Corsair", Q: 2)]);
        c.Set("motherboard", [new BuildPart("Synthetic Board", Mfr: "Synthetic Maker")]);
        c.Set("gpu", [new BuildPart("Synthetic Graphics", Vram: 8)]);
        c.Set("cpu", [new BuildPart("Synthetic Processor")]);

        string json = BuildCode.ToJson(BuildCode.Normalize(c), indented: false);

        Assert.Equal(
            """{"v":1,"src":"parts-life","p":{"cpu":[{"n":"Synthetic Processor"}],"gpu":[{"n":"Synthetic Graphics","vram":8}],"motherboard":[{"n":"Synthetic Board","mfr":"Synthetic Maker"}],"ram":[{"n":"Synthetic Memory","cap":16,"spd":3200,"t":"DDR4","mfr":"Corsair","q":2}],"ssd":[{"n":"Synthetic Solid State","cap":1000,"t":"nvme"}],"hdd":[{"n":"Synthetic Hard Disk","cap":2000,"t":"sata"}],"monitor":[{"n":"Synthetic Monitor"}],"optical-drive":[{"n":"Synthetic Optical"}]}}""",
            json);
    }

    [Fact]
    public void Saved_json_is_the_same_content_pretty_printed()
    {
        var r = BuildCode.Make(Config(("cpu", new BuildPart("AMD Ryzen 9 7950X 16-Core Processor"))));
        Assert.Contains("\n", r.Json);
        Assert.Equal(BuildCode.Decode(r.Code), CompactOf(r.Json));
    }

    private static string CompactOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var buf = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) }))
        {
            doc.WriteTo(w);
        }

        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    [Fact]
    public void Decode_rejects_other_strings()
    {
        Assert.Throws<FormatException>(() => BuildCode.Decode("PCB2.abc"));
        Assert.Throws<FormatException>(() => BuildCode.Decode("PCB1.!!!"));
    }

    [Fact]
    public void Identical_parts_aggregate_with_q_capped_at_16()
    {
        var stick = new BuildPart("CMK64GX5M2B5600Z40", Cap: 32, Speed: 4800, Type: "DDR5", Mfr: "Corsair");
        var c = new BuildConfig();
        c.Set("ram", [stick, stick, stick with { Cap = 16 }]);
        c.Set("ssd", Enumerable.Repeat(new BuildPart("Samsung SSD 980 PRO 1TB", Cap: 1000, Type: "nvme"), 20));
        var n = BuildCode.Normalize(c);

        Assert.Equal(2, n["ram"].Count);
        Assert.Equal(2, n["ram"][0].Q);
        Assert.Equal(1, n["ram"][1].Q);
        Assert.Equal(3, n.Count("ram"));
        Assert.Equal(16, Assert.Single(n["ssd"]).Q);
    }

    [Fact]
    public void Names_are_trimmed_collapsed_and_capped()
    {
        var c = Config(
            ("cpu", new BuildPart("  AMD   Ryzen 9\t7950X 16-Core Processor            \0")),
            ("monitor", new BuildPart(new string('M', 100))),
            ("ram", new BuildPart("X", Type: "LPDDR5XLONG", Mfr: new string('m', 60))));
        var n = BuildCode.Normalize(c);
        Assert.Equal("AMD Ryzen 9 7950X 16-Core Processor", n["cpu"][0].Name);
        Assert.Equal(80, n["monitor"][0].Name.Length);
        Assert.Equal(8, n["ram"][0].Type!.Length);
        Assert.Equal(40, n["ram"][0].Mfr!.Length);
    }

    [Fact]
    public void At_most_eight_kinds_per_category_with_a_note()
    {
        var c = new BuildConfig();
        c.Set("ssd", Enumerable.Range(1, 10).Select(i => new BuildPart($"SSD model {i}", Cap: 1000, Type: "nvme")));
        var r = BuildCode.Make(c);
        Assert.Equal(8, r.Config["ssd"].Count);
        Assert.Equal("SSD model 8", r.Config["ssd"][^1].Name);
        Assert.Equal(new BuildNote(BuildNoteKind.KindsLeftOut, "ssd", 2), Assert.Single(r.Notes));
    }

    // --- The limit of 6,000 characters ---

    private static IEnumerable<BuildPart> Long(string tag, int count) =>
        Enumerable.Range(1, count).Select(i => new BuildPart($"{tag} {i:00} ".PadRight(80, 'x'), Cap: 2000, Type: "sata", Mfr: new string('m', 40)));

    private static int Length(BuildConfig c) => BuildCode.Encode(c).Length - BuildCode.Prefix.Length;

    /// <summary>A configuration far bigger than any machine: parts with 80-character names and 40-character makers (about 210 characters of code per part).</summary>
    private static BuildConfig Big(int cpus, int monitors, int hdds, int rams)
    {
        var c = new BuildConfig();
        c.Set("cpu", Long("cpu", cpus));
        c.Set("gpu", Long("gpu", 8));
        c.Set("ssd", Long("ssd", 8));
        c.Set("ram", Long("ram", rams));
        c.Set("hdd", Long("hdd", hdds));
        c.Set("monitor", Long("monitor", monitors));
        return c;
    }

    [Fact]
    public void Over_the_limit_monitors_go_first()
    {
        var c = Big(cpus: 1, monitors: 8, hdds: 8, rams: 2);
        var withoutMonitors = c.Clone();
        withoutMonitors.Remove("monitor");
        Assert.True(Length(c) > BuildCode.MaxLength); // premise: too long as it is
        Assert.True(Length(withoutMonitors) <= BuildCode.MaxLength); // premise: it fits without the monitors

        var r = BuildCode.Make(c);
        Assert.True(r.Code.Length - BuildCode.Prefix.Length <= BuildCode.MaxLength);
        Assert.Empty(r.Config["monitor"]);
        Assert.Equal(8, r.Config["hdd"].Count);
        Assert.Equal(2, r.Config["ram"].Count);
        Assert.Equal(new BuildNote(BuildNoteKind.CategoryLeftOutForLength, "monitor"), Assert.Single(r.Notes));
        Assert.DoesNotContain("monitor", BuildCode.Decode(r.Code));
    }

    [Fact]
    public void Then_hdds_then_extra_ram_kinds()
    {
        var c = Big(cpus: 8, monitors: 8, hdds: 8, rams: 8);
        var keptRamOnly = c.Clone();
        keptRamOnly.Remove("monitor");
        keptRamOnly.Remove("hdd");
        Assert.True(Length(keptRamOnly) > BuildCode.MaxLength); // premise: too long even without the monitors and the HDDs
        keptRamOnly.Set("ram", keptRamOnly["ram"].Take(1));
        Assert.True(Length(keptRamOnly) <= BuildCode.MaxLength); // premise: it fits with one kind of memory

        var r = BuildCode.Make(c);
        Assert.True(r.Code.Length - BuildCode.Prefix.Length <= BuildCode.MaxLength);
        Assert.Empty(r.Config["monitor"]);
        Assert.Empty(r.Config["hdd"]);
        Assert.Single(r.Config["ram"]);
        Assert.Equal(8, r.Config["gpu"].Count);
        Assert.Equal(8, r.Config["ssd"].Count);
        Assert.Equal(
            new[]
            {
                new BuildNote(BuildNoteKind.CategoryLeftOutForLength, "monitor"),
                new BuildNote(BuildNoteKind.CategoryLeftOutForLength, "hdd"),
                new BuildNote(BuildNoteKind.MemoryFirstKindOnly, "ram"),
            },
            r.Notes);
    }

    [Fact]
    public void A_normal_machine_is_far_below_the_limit()
    {
        var c = Big(cpus: 1, monitors: 3, hdds: 2, rams: 2);
        var r = BuildCode.Make(c);
        Assert.Empty(r.Notes);
        Assert.Equal(3, r.Config["monitor"].Count);
    }

    [Fact]
    public void The_limit_is_checked_on_the_code_after_the_prefix_and_is_6000()
    {
        Assert.Equal(6000, BuildCode.MaxLength);
        Assert.Equal(8, BuildCode.MaxPerCategory);
        Assert.Equal(16, BuildCode.MaxQuantity);
        Assert.Equal("PCB1.", BuildCode.Prefix);
    }
}
