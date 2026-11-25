using System.Text;
using System.Text.Json.Nodes;
using PcMieru.Party.Adventure;
using PcMieru.Party.Game;
using PcMieru.Party.Import;
using static PcMieru.Party.Core.Tests.Import.ImportRig;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>
/// What the importer makes of sources it accepts. Each test compares the converted bytes, read back, with values the test
/// states itself (or with what the old app held in memory), field by field. "It parses" proves nothing.
/// </summary>
public class StateImportConvertTests
{
    // ---- version 0: no SaveVersion (v0.3 / v0.6.0 saves) ----------------------------------------------------------

    [Fact]
    public void V03_fixture_moves_the_one_machine_keys_and_creates_a_default_adventure()
    {
        byte[] src = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "state-v0.3.json"));
        var v = StateImport.Inspect(src);

        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Equal("accept", v.Code);
        Assert.Null(v.Reason);
        Assert.Empty(v.RepairedFields);
        Assert.Equal(0, v.SourceVersion);
        Assert.Equal(Sha(src), v.SourceSha256);

        var s = Read(v);
        Assert.Equal(PartyState.CurrentVersion, s.SaveVersion);
        Assert.Equal(
            new SortedDictionary<string, double>(StringComparer.Ordinal)
            {
                ["cpu#0"] = 5000, ["cpu-cooler#0"] = 4800, ["gpu#0"] = 7000, ["memory"] = 1000, ["ssd"] = 800,
            },
            new SortedDictionary<string, double>(s.Experience, StringComparer.Ordinal));
        Assert.Equal(900, s.Journey);
        Assert.Equal(2, s.Chapter);
        Assert.Equal(9, s.Scene);
        Assert.Equal(5, s.Boots);
        Assert.Equal("en", s.Lang);
        Assert.Equal(2.0, s.Zoom);
        Assert.Null(s.WindowLeft);
        Assert.Null(s.WindowTop);
        Assert.True(s.Topmost);
        Assert.False(s.Expanded);
        Assert.Equal("level", s.Labels);

        // no adventure in the source: a fresh one, from the first town with nothing in hand
        var a = s.Adventure!;
        Assert.Equal(AdventureMode.Town, a.Mode);
        Assert.Equal(TownPhase.Arrive, a.Phase);
        Assert.Equal(0, a.Town);
        Assert.Equal(AdventureState.StartingGold, a.Gold);
        Assert.Empty(a.Inventory);
        Assert.Empty(a.Equipped);
        Assert.Equal(0, a.Expeditions);
    }

    [Fact]
    public void V060_fixture_without_a_version_is_carried_and_gets_a_default_adventure()
    {
        // state-v0.6.0.json has no SaveVersion at all, so by the rule it is a version 0 source whose keys are already numbered.
        var src = V060();
        var v = Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Equal(0, v.SourceVersion);

        var s = Read(v);
        var expected = new Dictionary<string, double>
        {
            ["cpu#0"] = 98765.4, ["cpu-cooler#0"] = 97000.1, ["memory"] = 41234.5, ["gpu#0"] = 152000,
            ["gpu#1"] = 3100.25, ["m2"] = 12345, ["hdd"] = 2222, ["psu"] = 60001,
        };
        Assert.Equal(expected.OrderBy(k => k.Key, StringComparer.Ordinal), s.Experience.OrderBy(k => k.Key, StringComparer.Ordinal));
        Assert.Equal(30512.75, s.Journey);
        Assert.Equal(13, s.Chapter);
        Assert.Equal(4, s.Scene);
        Assert.Equal(["── 第十三章  電気の来ない場所 ──", "灯がともった。盤の上に、めいめいの受け口がある。"], s.Log);
        Assert.Equal(1_790_000_000L, s.LastEventAt["boot"]);
        Assert.Equal(1_789_990_000L, s.LastEventAt["gpu-very-high"]);
        Assert.Equal(["夜。羽根の音だけが残っている。誰もそれを音だとは思っていない。"], s.RecentVignettes);
        Assert.Equal(512_345.5, s.UptimeSeconds);
        Assert.Equal(1_790_000_000L, s.LastSeenUnix);
        Assert.Equal(41, s.Boots);
        Assert.Equal("ja", s.Lang);
        Assert.Equal(2.5, s.Zoom);
        Assert.Equal(-1600.0, s.WindowLeft);   // a monitor left of the primary one has negative coordinates
        Assert.Equal(40.0, s.WindowTop);
        Assert.True(s.Topmost);
        Assert.False(s.Expanded);
        Assert.Equal("name", s.Labels);
        Assert.Equal(AdventureMode.Town, s.Adventure!.Mode);
        Assert.Equal(AdventureState.StartingGold, s.Adventure.Gold);
    }

    [Fact]
    public void An_explicit_version_0_is_treated_like_a_missing_one()
    {
        var src = V03();
        src["SaveVersion"] = 0;
        var v = Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Equal(0, v.SourceVersion);
        var s = Read(v);
        Assert.Equal(5000, s.Experience["cpu#0"]);
        Assert.False(s.Experience.ContainsKey("cpu"));
    }

    [Fact]
    public void A_negative_zero_version_is_version_0()
    {
        var v = Inspect("""{"SaveVersion": -0, "Experience": {"cpu": 5}, "Journey": 1, "Chapter": 1, "Scene": 1}""");
        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Equal(5, Read(v).Experience["cpu#0"]);
    }

    [Fact]
    public void An_adventure_already_present_in_a_version_0_source_is_kept()
    {
        // a hand-edited or tool-written save that lost only the version number must not lose the journey
        var src = V060();
        src["Adventure"] = V1()["Adventure"]!.DeepClone();
        var v = Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);

        var a = Read(v).Adventure!;
        Assert.Equal(AdventureMode.Out, a.Mode);
        Assert.Equal(2, a.Town);
        Assert.Equal(1234, a.Gold);
        Assert.Equal(17, a.Expeditions);
    }

    [Fact]
    public void Old_and_numbered_names_together_keep_the_numbered_one_and_drop_the_old()
    {
        var src = V03();
        src["Experience"] = new JsonObject
        {
            ["cpu"] = 111.0, ["cpu#0"] = 222.0,            // both: the current name wins
            ["cpu-cooler"] = 333.0,                         // only the old name: moved
            ["gpu"] = 444.0, ["gpu#0"] = 555.0,             // both again
            ["memory"] = 666.0,
        };
        var v = Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);   // choosing the current name is the old rule, not a repair

        var e = Read(v).Experience;
        Assert.Equal(222, e["cpu#0"]);
        Assert.Equal(333, e["cpu-cooler#0"]);
        Assert.Equal(555, e["gpu#0"]);
        Assert.Equal(666, e["memory"]);
        Assert.Equal(4, e.Count);
        Assert.False(e.ContainsKey("cpu"));
        Assert.False(e.ContainsKey("cpu-cooler"));
        Assert.False(e.ContainsKey("gpu"));
    }

    // ---- version 1 -----------------------------------------------------------------------------------------------------

    [Fact]
    public void V1_fixture_is_carried_field_by_field_including_the_adventure_and_display_settings()
    {
        byte[] src = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "state-v1.json"));
        var v = StateImport.Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Equal(1, v.SourceVersion);
        Assert.Equal(Sha(src), v.SourceSha256);
        Assert.Empty(v.RepairedFields);

        var s = Read(v);
        Assert.Equal(1, s.SaveVersion);
        Assert.Equal(
            new Dictionary<string, double>
            {
                ["cpu#0"] = 98765.4, ["cpu-cooler#0"] = 97000.1, ["memory"] = 41234.5, ["gpu#0"] = 152000,
                ["gpu#1"] = 3100.25, ["m2"] = 12345, ["hdd"] = 2222, ["psu"] = 60001,
            }.OrderBy(k => k.Key, StringComparer.Ordinal),
            s.Experience.OrderBy(k => k.Key, StringComparer.Ordinal));
        Assert.Equal(30512.75, s.Journey);
        Assert.Equal(13, s.Chapter);
        Assert.Equal(4, s.Scene);
        Assert.Equal(2, s.Log.Count);
        Assert.Equal(2, s.LastEventAt.Count);
        Assert.Single(s.RecentVignettes);
        Assert.Equal(512_345.5, s.UptimeSeconds);
        Assert.Equal(1_790_000_000L, s.LastSeenUnix);
        Assert.Equal(41, s.Boots);

        // display settings, negative coordinates included
        Assert.Equal("ja", s.Lang);
        Assert.Equal(1.75, s.Zoom);
        Assert.Equal(-1600.0, s.WindowLeft);
        Assert.Equal(-40.0, s.WindowTop);
        Assert.False(s.Topmost);
        Assert.True(s.Expanded);
        Assert.Equal("none", s.Labels);

        // adventure: town, trip, gear, items, record
        var a = s.Adventure!;
        Assert.Equal(AdventureMode.Out, a.Mode);
        Assert.Equal(TownPhase.Depart, a.Phase);
        Assert.Equal(2, a.Town);
        Assert.Equal("battery-hollow", a.Place);
        Assert.Equal("dark-plain", a.LastPlace);
        Assert.Equal(3, a.Depth);
        Assert.Equal(5, a.Planned);
        Assert.Equal(0, a.BackSteps);
        Assert.Equal(Pending.Fight, a.Pending);
        Assert.Equal(0.625, a.Hp);
        Assert.Equal(0.25, a.Fatigue);
        Assert.Equal(-2, a.Streak);
        Assert.Equal(1234L, a.Gold);
        Assert.Equal(7, a.Loot);
        Assert.Equal(["copper-spatula", "cotton-wrap", "small-pouch"], a.Inventory);
        Assert.Equal(3, a.Equipped.Count);
        Assert.Equal(["steel-tweezers", "heat-coat", "anti-static-strap"], a.Equipped["cpu#0"]);
        Assert.Equal(["spare-screw", "thin-sheet", ""], a.Equipped["gpu#0"]);
        Assert.Equal(["glowing-chip", "", "rubber-foot"], a.Equipped["gpu#1"]);
        Assert.Equal(0.4375, a.Progress);
        Assert.Equal(1, a.Rests);
        Assert.Equal(2, a.Bought);
        Assert.Equal(Stance.Bold, a.Stance);
        Assert.Equal(17, a.Expeditions);
        Assert.Equal(16, a.Returns);
        Assert.Equal(41, a.Wins);
        Assert.Equal(9, a.Losses);
    }

    [Fact]
    public void V1_does_not_rename_keys()
    {
        // key migration belongs to version 0 only; a v1 save that has a plain "cpu" keeps it (the old Migrate did the same)
        var src = V1();
        src["Experience"]!.AsObject()["cpu"] = 7.0;
        var v = Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);
        var e = Read(v).Experience;
        Assert.Equal(7, e["cpu"]);
        Assert.Equal(98765.4, e["cpu#0"]);
    }

    [Fact]
    public void V1_without_an_adventure_gets_the_default_one_and_is_still_accepted()
    {
        var src = V1();
        src.Remove("Adventure");
        var v = Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Equal(AdventureState.StartingGold, Read(v).Adventure!.Gold);
        // Read() would supply a default Adventure on its own, so look at the bytes: the object must really be there
        Assert.IsType<JsonObject>(ConvertedJson(v)["Adventure"]);
        Assert.Equal(AdventureState.StartingGold, (long?)ConvertedJson(v)["Adventure"]!["Gold"]);

        src["Adventure"] = null;   // null means "no adventure" for this field
        v = Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Equal(AdventureMode.Town, Read(v).Adventure!.Mode);
        Assert.IsType<JsonObject>(ConvertedJson(v)["Adventure"]);
    }

    [Fact]
    public void Out_of_range_numbers_are_carried_as_they_are()
    {
        // AdventureState.Sanitize clamps them when the adventure starts, as it always did; the importer only judges KIND of value
        var src = V1();
        src["Adventure"]!["Gold"] = -5;
        src["Adventure"]!["Hp"] = 7.5;
        var v = Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Equal(-5L, Read(v).Adventure!.Gold);
        Assert.Equal(7.5, Read(v).Adventure!.Hp);
    }

    [Fact]
    public void State_written_by_the_real_engine_comes_through_unchanged()
    {
        // two days of play produce a populated adventure; the old writer's own bytes are the source
        var sim = new Sim(seed: 5);
        for (int t = 0; t < 2 * 86400; t++) sim.Tick(Profile.Gaming);
        Assert.True(sim.Adv.Expeditions > 0);
        Assert.NotEmpty(sim.Adv.Equipped);
        sim.State.Lang = "en";
        sim.State.WindowLeft = -2560;
        sim.State.WindowTop = 120;
        sim.State.Labels = "none";
        sim.State.Expanded = true;

        byte[] src = Encoding.UTF8.GetBytes(sim.State.ToJson());
        var v = StateImport.Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Equal(1, v.SourceVersion);
        AssertSameProgress(sim.State, Read(v));
    }

    // ---- unknown fields ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Unknown_fields_at_the_root_and_inside_Adventure_round_trip(int version)
    {
        var src = version == 1 ? V1() : V060();
        if (version == 0) src["Adventure"] = V1()["Adventure"]!.DeepClone();
        var rootExtra = JsonNode.Parse("""{"a":[1,2,{"b":null}],"n":12345678901234567890,"f":1.50,"s":"日本語 <&>","t":true,"deep":{"x":{"y":[[],{}]}}}""")!;
        var advExtra = JsonNode.Parse("""{"x":[0.1,-0,1e3],"y":"z","k":{"m":false}}""")!;
        src["FutureRoot"] = rootExtra.DeepClone();
        src["Note"] = "kept";
        src["Adventure"]!["FutureAdventure"] = advExtra.DeepClone();
        src["Adventure"]!["ShipHp"] = 3;

        var v = Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);

        var conv = ConvertedJson(v);
        Assert.Equal(rootExtra.ToJsonString(), conv["FutureRoot"]!.ToJsonString());
        Assert.Equal("kept", (string?)conv["Note"]);
        Assert.Equal(advExtra.ToJsonString(), conv["Adventure"]!["FutureAdventure"]!.ToJsonString());
        Assert.Equal(3, (int?)conv["Adventure"]!["ShipHp"]);

        // and the known fields next to them are still right
        Assert.Equal(1234L, Read(v).Adventure!.Gold);
        Assert.Equal(13, Read(v).Chapter);
    }

    [Fact]
    public void Unknown_fields_survive_a_second_import_of_the_converted_bytes()
    {
        var src = V1();
        src["FutureRoot"] = new JsonObject { ["q"] = 1 };
        src["Adventure"]!["FutureAdventure"] = new JsonArray(1, 2, 3);
        var first = Inspect(src);
        var second = StateImport.Inspect(first.Converted);
        Assert.Equal(ImportStatus.Acceptable, second.Status);
        Assert.Equal(first.Converted, second.Converted);
    }

    // ---- UpdateMode ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("\"off\"")]
    [InlineData("\"notify\"")]
    [InlineData("\"auto\"")]
    [InlineData("5")]            // malformed: it is dropped without being looked at, so it cannot block or flag the import
    [InlineData("{\"x\":1}")]
    [InlineData("null")]
    public void UpdateMode_is_dropped_and_never_becomes_a_consent(string value)
    {
        var src = V1();
        src["UpdateMode"] = JsonNode.Parse(value);
        var v = Inspect(src);

        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Empty(v.RepairedFields);
        Assert.Equal(["UpdateMode"], v.DroppedFields);
        Assert.False(ConvertedJson(v).ContainsKey("UpdateMode"));
        Assert.DoesNotContain("UpdateMode", Encoding.UTF8.GetString(v.Converted!));
        AssertSameProgress(PartyState.FromJson(src.ToJsonString()), Read(v));
    }

    [Fact]
    public void No_UpdateMode_in_the_source_means_nothing_is_dropped()
    {
        var v = Inspect(V03());
        Assert.Empty(v.DroppedFields);
        Assert.False(ConvertedJson(v).ContainsKey("UpdateMode"));
    }

    // ---- the output ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("state-v0.3.json")]
    [InlineData("state-v0.6.0.json")]
    [InlineData("state-v1.json")]
    public void Converted_bytes_are_utf8_without_bom_with_version_1_and_pascal_case_keys(string fixture)
    {
        var v = Inspect(Fixture(fixture));
        var bytes = v.Converted!;
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        var json = ConvertedJson(v);
        Assert.Equal(1, (int?)json["SaveVersion"]);
        foreach (var key in new[] { "Experience", "Journey", "Chapter", "Scene", "Adventure", "Lang", "Zoom", "Topmost", "Labels" })
            Assert.True(json.ContainsKey(key), key);
        Assert.False(json.ContainsKey("experience"));
        Assert.All(json["Adventure"]!.AsObject().Select(p => p.Key), k => Assert.True(char.IsUpper(k[0]), k));
    }

    [Theory]
    [InlineData("state-v0.3.json")]
    [InlineData("state-v0.6.0.json")]
    [InlineData("state-v1.json")]
    public void Importing_the_converted_bytes_again_changes_nothing(string fixture)
    {
        var first = Inspect(Fixture(fixture));
        var second = StateImport.Inspect(first.Converted);
        Assert.Equal(ImportStatus.Acceptable, second.Status);
        Assert.Equal(1, second.SourceVersion);
        Assert.Equal(first.Converted, second.Converted);
        Assert.Equal(Sha(first.Converted!), second.SourceSha256);
    }

    [Fact]
    public void The_verdict_hashes_the_source_bytes_exactly_as_given()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Bytes(V1())];
        var v = StateImport.Inspect(withBom);
        Assert.Equal(ImportStatus.Acceptable, v.Status);   // a BOM is harmless
        Assert.Equal(Sha(withBom), v.SourceSha256);
        Assert.NotEqual(Sha(withBom[3..]), v.SourceSha256);
        Assert.Equal(13, Read(v).Chapter);
    }

    [Fact]
    public void Whitespace_around_the_document_is_fine()
    {
        var v = Inspect("\r\n\t  " + V1().ToJsonString() + "  \r\n");
        Assert.Equal(ImportStatus.Acceptable, v.Status);
    }

    // ---- the limits ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_source_of_exactly_8_MiB_is_accepted()
    {
        byte[] json = Bytes(V1());
        var padded = new byte[StateImport.MaxBytes];
        json.CopyTo(padded, 0);
        Array.Fill(padded, (byte)' ', json.Length, padded.Length - json.Length);

        var v = StateImport.Inspect(padded);
        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Equal(Sha(padded), v.SourceSha256);
    }

    [Fact]
    public void Nesting_of_exactly_64_levels_is_accepted()
    {
        // the top-level object is level 1, so the unknown field holds 63 more
        var v = Inspect("""{"Experience":{"memory":1},"Journey":1,"Chapter":1,"Scene":1,"X":""" + new string('[', 63) + new string(']', 63) + "}");
        Assert.Equal(ImportStatus.Acceptable, v.Status);

        // the unknown field comes back with all 63 levels (count them in the tree)
        JsonNode? n = ConvertedJson(v)["X"];
        int levels = 0;
        while (n is JsonArray a) { levels++; n = a.Count > 0 ? a[0] : null; }
        Assert.Equal(63, levels);
        Assert.Equal(ImportStatus.Acceptable, StateImport.Inspect(v.Converted).Status);   // 64 levels in, 64 levels out: importable again
    }

    [Fact]
    public void Converted_bytes_are_compact_so_no_line_ending_depends_on_the_platform()
    {
        // a pure importer must give the same bytes on every machine, and indentation would only make deep data bigger
        var v = Inspect(V1());
        Assert.DoesNotContain((byte)'\r', v.Converted!);
        Assert.DoesNotContain((byte)'\n', v.Converted!);
        Assert.Equal((byte)'{', v.Converted![0]);
    }
}
