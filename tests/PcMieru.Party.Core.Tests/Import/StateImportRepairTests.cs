using System.Text;
using System.Text.Json.Nodes;
using PcMieru.Party.Adventure;
using PcMieru.Party.Game;
using PcMieru.Party.Import;
using static PcMieru.Party.Core.Tests.Import.ImportRig;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>
/// Known fields that cannot be read as written: the importer replaces them with defaults, says so (needs-repair) and lists
/// each one by path. The fields next to them must come through untouched.
/// </summary>
public class StateImportRepairTests
{
    [Fact]
    public void One_broken_adventure_field_is_listed_and_its_neighbours_are_carried()
    {
        var src = V1();
        src["Adventure"]!["Gold"] = "lots";
        var v = Inspect(src);

        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        Assert.Equal("needs-repair", v.Code);
        Assert.Null(v.Reason);
        Assert.Equal(["Adventure.Gold"], v.RepairedFields);

        var expected = PartyState.FromJson(V1().ToJsonString());
        var actual = Read(v);
        Assert.Equal(AdventureState.StartingGold, actual.Adventure!.Gold);   // the default, not the lost 1234
        actual.Adventure.Gold = expected.Adventure!.Gold;
        AssertSameProgress(expected, actual);
    }

    [Fact]
    public void Several_broken_fields_are_all_listed_and_the_rest_is_carried()
    {
        var src = V1();
        src["Zoom"] = "big";
        src["Lang"] = 5;
        src["WindowLeft"] = "left";
        src["Topmost"] = "yes";
        src["Adventure"]!["Mode"] = "sideways";          // an unknown enum name: the tolerant converter would hide it
        src["Adventure"]!["Inventory"] = "a bag";
        src["Adventure"]!["Hp"] = new JsonObject();
        src["Log"]!.AsArray()[1] = null;

        var v = Inspect(src);
        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        string[] want = ["Zoom", "Lang", "WindowLeft", "Topmost", "Adventure.Mode", "Adventure.Inventory", "Adventure.Hp", "Log[1]"];
        Assert.Equal(want.OrderBy(x => x, StringComparer.Ordinal), v.RepairedFields.OrderBy(x => x, StringComparer.Ordinal));

        var good = PartyState.FromJson(V1().ToJsonString());
        var s = Read(v);
        // what was not broken
        Assert.Equal(good.Experience.OrderBy(k => k.Key, StringComparer.Ordinal), s.Experience.OrderBy(k => k.Key, StringComparer.Ordinal));
        Assert.Equal(good.Journey, s.Journey);
        Assert.Equal(good.Chapter, s.Chapter);
        Assert.Equal(good.Scene, s.Scene);
        Assert.Equal(good.WindowTop, s.WindowTop);
        Assert.Equal(good.Expanded, s.Expanded);
        Assert.Equal(good.Labels, s.Labels);
        Assert.Equal(good.Boots, s.Boots);
        Assert.Equal(good.Adventure!.Gold, s.Adventure!.Gold);
        Assert.Equal(good.Adventure.Town, s.Adventure.Town);
        Assert.Equal(good.Adventure.Equipped["gpu#0"], s.Adventure.Equipped["gpu#0"]);
        Assert.Equal(good.Adventure.Expeditions, s.Adventure.Expeditions);
        Assert.Equal(good.Adventure.Wins, s.Adventure.Wins);
        // what was broken is a default
        Assert.Equal(new PartyState().Zoom, s.Zoom);
        Assert.Null(s.WindowLeft);
    }

    [Fact]
    public void An_unknown_enum_name_is_reported_as_a_repair_not_silently_swallowed()
    {
        var src = V1();
        src["Adventure"]!["Stance"] = "reckless";
        var v = Inspect(src);
        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        Assert.Equal(["Adventure.Stance"], v.RepairedFields);
        Assert.Equal(new AdventureState().Stance, Read(v).Adventure!.Stance);   // the default, not the lost Bold
    }

    [Fact]
    public void An_Adventure_of_the_wrong_kind_is_replaced_by_a_default_one_and_listed()
    {
        var src = V1();
        src["Adventure"] = "gone";
        var v = Inspect(src);
        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        Assert.Equal(["Adventure"], v.RepairedFields);
        var s = Read(v);
        Assert.Equal(AdventureMode.Town, s.Adventure!.Mode);
        Assert.Equal(13, s.Chapter);
    }

    [Fact]
    public void A_broken_entry_in_the_level_table_is_listed_by_its_key()
    {
        var src = V1();
        src["Experience"]!["ssd"] = "fast";
        src["Experience"]!["nvme"] = null;
        var v = Inspect(src);
        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        Assert.Contains("Experience[ssd]", v.RepairedFields);
        Assert.Contains("Experience[nvme]", v.RepairedFields);
        Assert.Equal(2, v.RepairedFields.Count);
        Assert.Equal(98765.4, Read(v).Experience["cpu#0"]);
    }

    [Fact]
    public void Repair_and_key_migration_work_together_on_a_version_0_source()
    {
        var src = V03();
        src["Zoom"] = "x";
        var v = Inspect(src);
        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        Assert.Equal(["Zoom"], v.RepairedFields);
        Assert.Equal(0, v.SourceVersion);
        var s = Read(v);
        Assert.Equal(5000, s.Experience["cpu#0"]);
        Assert.Equal(7000, s.Experience["gpu#0"]);
        Assert.Equal(1, s.SaveVersion);
    }

    [Fact]
    public void Converted_bytes_of_a_repaired_import_import_cleanly_a_second_time()
    {
        var src = V1();
        src["Adventure"]!["Gold"] = "lots";
        src["Zoom"] = "big";
        src["Unknown"] = 1;
        var first = Inspect(src);
        Assert.Equal(ImportStatus.NeedsRepair, first.Status);

        var second = StateImport.Inspect(first.Converted);
        Assert.Equal(ImportStatus.Acceptable, second.Status);
        Assert.Empty(second.RepairedFields);
        Assert.Equal(first.Converted, second.Converted);
    }

    [Fact]
    public void Unknown_fields_are_never_called_broken()
    {
        var src = V1();
        src["Zoomm"] = "big";                 // not a known field: it is just carried
        src["Adventure"]!["Golld"] = "lots";
        var v = Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Empty(v.RepairedFields);
        Assert.Equal("big", (string?)ConvertedJson(v)["Zoomm"]);
        Assert.Equal("lots", (string?)ConvertedJson(v)["Adventure"]!["Golld"]);
    }

    [Fact]
    public void Null_is_fine_where_the_field_allows_it_and_a_hole_where_it_does_not()
    {
        var src = V1();
        src["WindowLeft"] = null;             // nullable: "no remembered position"
        var v = Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Null(Read(v).WindowLeft);

        src["Lang"] = null;                   // not nullable
        v = Inspect(src);
        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        Assert.Equal(["Lang"], v.RepairedFields);
    }

    [Fact]
    public void A_repair_never_hides_a_hold()
    {
        var src = V1();
        src["Zoom"] = "big";
        src["SaveVersion"] = -3;
        var b = Bytes(src);
        AssertHold(StateImport.Inspect(b), ImportReason.VersionNegative, b);
    }

    // ---- fuzz ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("state-v0.3.json", 11)]
    [InlineData("state-v0.6.0.json", 22)]
    [InlineData("state-v1.json", 33)]
    public void Byte_level_damage_never_throws_and_every_non_hold_converts_to_something_that_imports_again(string fixture, int seed)
    {
        byte[] original = Encoding.UTF8.GetBytes(TestData.Fixture(fixture));
        var rng = new Random(seed);
        int accepted = 0, repaired = 0, held = 0;

        // every 10th round is not random at all: a known field of the wrong kind, so some rounds are always repairs
        var damaged = Fixture(fixture);
        damaged["Zoom"] = "big";
        byte[] repairable = Bytes(damaged);

        for (int i = 0; i < 1500; i++)
        {
            bool deterministicRepair = i % 10 == 0;
            var b = (byte[])(deterministicRepair ? repairable : original).Clone();
            int edits = deterministicRepair ? 0 : 1 + rng.Next(3);
            for (int e = 0; e < edits; e++)
            {
                int pos = rng.Next(b.Length);
                switch (rng.Next(4))
                {
                    case 0: b[pos] = (byte)rng.Next(256); break;                       // any byte
                    case 1: b[pos] = (byte)"{}[]\",:0-9.eE tfnul"[rng.Next(19)]; break; // structural / literal characters
                    case 2: b[pos] ^= (byte)(1 << rng.Next(8)); break;                  // one bit
                    default: b = [.. b[..pos], .. b[Math.Min(b.Length, pos + 1 + rng.Next(4))..]]; break; // delete a few bytes
                }
                if (b.Length == 0) break;
            }

            var v = StateImport.Inspect(b);
            Assert.Equal(Sha(b), v.SourceSha256);
            if (deterministicRepair) Assert.Equal(ImportStatus.NeedsRepair, v.Status);
            switch (v.Status)
            {
                case ImportStatus.Hold:
                    held++;
                    Assert.Null(v.Converted);
                    Assert.False(string.IsNullOrEmpty(v.Reason));
                    break;
                case ImportStatus.Acceptable:
                case ImportStatus.NeedsRepair:
                    if (v.Status == ImportStatus.Acceptable) accepted++; else repaired++;
                    Assert.NotNull(v.Converted);
                    var again = StateImport.Inspect(v.Converted);
                    Assert.True(again.Status == ImportStatus.Acceptable,
                        $"seed {seed} round {i}: converted bytes were {again.Status} ({again.Reason} {again.Detail} {string.Join(',', again.RepairedFields)})");
                    Assert.Equal(v.Converted, again.Converted);
                    break;
            }
        }
        // the fuzz must exercise all three outcomes, or it proves little
        Assert.True(held > 0 && accepted > 0 && repaired > 0, $"held {held}, accepted {accepted}, repaired {repaired}");
    }
}
