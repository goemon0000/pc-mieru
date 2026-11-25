using System.Text;
using System.Text.Json.Nodes;
using PcMieru.Party.Adventure;
using PcMieru.Party.Game;
using PcMieru.Party.Import;
using static PcMieru.Party.Core.Tests.Import.ImportRig;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>
/// Hostile or odd sources: the memory the importer uses (for its list of repairs and for its output) stays bounded,
/// a runtime-only field name is not lost, repairs reach the converted bytes, and numbers that are not finite never get through.
/// </summary>
public class StateImportBoundsTests
{
    private const string Head = """{"Experience":{"memory":1},"Journey":1,"Chapter":1,"Scene":1""";

    // ---- the list of repairs is bounded -----------------------------------------------------------------------------

    [Fact]
    public void A_very_long_key_with_many_broken_slots_gives_a_short_bounded_list_and_no_exception()
    {
        // one 64 KiB key, and 16,384 null slots under it: a path per slot would have copied the whole key every time
        string key = new('k', 64 * 1024);
        var sb = new StringBuilder(Head);
        sb.Append(",\"Adventure\":{\"Equipped\":{\"").Append(key).Append("\":[");
        for (int i = 0; i < 16384; i++) sb.Append(i == 0 ? "null" : ",null");
        sb.Append("]}}}");
        byte[] src = Encoding.UTF8.GetBytes(sb.ToString());
        Assert.True(src.Length < 150 * 1024);

        var v = StateImport.Inspect(src);   // must not throw

        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        Assert.True(v.RepairedFields.Count <= 101, v.RepairedFields.Count.ToString());
        Assert.Equal("+16284 more", v.RepairedFields[^1]);
        Assert.All(v.RepairedFields, p => Assert.True(p.Length < 200, p.Length.ToString()));
        Assert.True(v.RepairedFields.Sum(p => p.Length) < 20_000);
        Assert.NotNull(v.Converted);
        Assert.True(v.Converted!.Length < src.Length * 2);
        Assert.Equal(ImportStatus.Acceptable, StateImport.Inspect(v.Converted).Status);   // the repairs are in the bytes
    }

    [Fact]
    public void Many_broken_fields_list_the_first_hundred_and_count_the_rest()
    {
        var sb = new StringBuilder(Head);
        sb.Append(",\"Log\":[");
        for (int i = 0; i < 250; i++) sb.Append(i == 0 ? "5" : ",6");   // numbers where text is wanted
        sb.Append("]}");
        var v = StateImport.Inspect(Encoding.UTF8.GetBytes(sb.ToString()));

        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        Assert.Equal(101, v.RepairedFields.Count);
        Assert.Equal("Log[0]", v.RepairedFields[0]);
        Assert.Equal("+150 more", v.RepairedFields[^1]);
    }

    // ---- the output is bounded ------------------------------------------------------------------------------------------

    [Fact]
    public void Deep_sparse_data_does_not_grow_in_the_output()
    {
        // 63 levels of arrays, then rows of zeros at the deepest level: indenting this would make it hundreds of times larger
        var sb = new StringBuilder(Head);
        sb.Append(",\"X\":").Append('[', 62).Append('[');
        for (int i = 0; i < 400_000; i++) sb.Append(i == 0 ? "0" : ",0");
        sb.Append(']').Append(']', 62).Append('}');
        byte[] src = Encoding.UTF8.GetBytes(sb.ToString());
        Assert.True(src.Length <= StateImport.MaxBytes);

        var v = StateImport.Inspect(src);

        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.True(v.Converted!.Length <= src.Length + 4096, $"{src.Length} bytes in, {v.Converted.Length} out");
    }

    [Fact]
    public void A_result_larger_than_the_cap_is_held_with_its_own_reason()
    {
        // characters outside the BMP are written as 12-byte escapes (3 times their UTF-8 size): the cap must catch growth like that
        string emoji = string.Concat(Enumerable.Repeat("\U0001F600", 40_000));   // about 480,000 bytes once escaped
        var src = V1();
        src["Unknown"] = emoji;
        byte[] b = Bytes(src);

        // the real cap does not trip ...
        Assert.Equal(ImportStatus.Acceptable, StateImport.Inspect(b).Status);
        // ... a small one does, and says so without producing any bytes
        var v = StateImport.Inspect(b, 300_000);
        AssertHold(v, ImportReason.OutputTooLarge, b);
        Assert.Null(v.Converted);
    }

    [Fact]
    public void The_cap_has_a_sensible_size()
    {
        Assert.Equal(16 * 1024 * 1024, StateImport.MaxOutputBytes);
        Assert.True(StateImport.MaxOutputBytes > StateImport.MaxBytes);
    }

    // ---- a root key that has the name of a runtime-only property -----------------------------------------------------

    [Fact]
    public void SaveBlocked_at_the_root_is_kept_as_an_unknown_field_and_never_sets_the_runtime_flag()
    {
        var src = V1();
        src["SaveBlocked"] = new JsonObject { ["note"] = 1 };
        var v = Inspect(src);

        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Equal("""{"note":1}""", ConvertedJson(v)["SaveBlocked"]!.ToJsonString());
        Assert.False(Read(v).SaveBlocked);

        // even when the source says true, it is data of some other writer, not this program's flag
        src["SaveBlocked"] = true;
        v = Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.True((bool?)ConvertedJson(v)["SaveBlocked"]);
        Assert.False(Read(v).SaveBlocked);
    }

    // ---- repairs reach the converted bytes ---------------------------------------------------------------------------

    [Fact]
    public void Null_entries_in_the_inventory_are_listed_and_removed_so_the_result_imports_clean()
    {
        var src = V1();
        src["Adventure"]!["Inventory"] = new JsonArray(null, "item");
        var v = Inspect(src);

        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        Assert.Equal(["Adventure.Inventory[0]"], v.RepairedFields);
        Assert.Equal(["item"], Read(v).Adventure!.Inventory);

        var again = StateImport.Inspect(v.Converted);
        Assert.Equal(ImportStatus.Acceptable, again.Status);
        Assert.Empty(again.RepairedFields);
    }

    [Fact]
    public void Null_equipment_slots_become_empty_slots_in_the_same_place()
    {
        var src = V1();
        src["Adventure"]!["Equipped"] = new JsonObject { ["gpu#0"] = new JsonArray(null, "sword", null) };
        var v = Inspect(src);

        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        Assert.Equal(["", "sword", ""], Read(v).Adventure!.Equipped["gpu#0"]);
        Assert.Equal(ImportStatus.Acceptable, StateImport.Inspect(v.Converted).Status);
    }

    [Fact]
    public void A_null_dictionary_value_is_dropped_and_the_result_imports_clean()
    {
        var src = V1();
        src["Adventure"]!["Equipped"] = new JsonObject { ["gpu#0"] = null, ["cpu#0"] = new JsonArray("a", "b", "c") };
        var v = Inspect(src);

        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        Assert.Equal(["Adventure.Equipped[gpu#0]"], v.RepairedFields);
        var equipped = Read(v).Adventure!.Equipped;
        Assert.False(equipped.ContainsKey("gpu#0"));
        Assert.Equal(["a", "b", "c"], equipped["cpu#0"]);
        Assert.Equal(ImportStatus.Acceptable, StateImport.Inspect(v.Converted).Status);
    }

    // ---- numbers that are not finite --------------------------------------------------------------------------------

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    public void A_number_beyond_the_range_of_a_double_in_an_optional_field_is_repaired_to_its_default(string number)
    {
        string zoomDefault = new PartyState().Zoom.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        string json = Head + ",\"Zoom\":" + number + "}";
        var v = Inspect(json);

        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        Assert.Contains("Zoom", v.RepairedFields);
        Assert.NotNull(v.Converted);
        Assert.Equal(new PartyState().Zoom, Read(v).Zoom);
        Assert.Equal(ImportStatus.Acceptable, StateImport.Inspect(v.Converted).Status);
        Assert.False(string.IsNullOrEmpty(zoomDefault));
    }

    [Fact]
    public void A_non_finite_number_in_an_adventure_field_is_repaired_too()
    {
        var src = V1();
        string json = src.ToJsonString().Replace("\"Progress\":", "\"Progress\":1e400,\"ProgressWas\":");
        var v = Inspect(json);

        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        Assert.Contains("Adventure.Progress", v.RepairedFields);
        Assert.True(double.IsFinite(Read(v).Adventure!.Progress));
        Assert.Equal(ImportStatus.Acceptable, StateImport.Inspect(v.Converted).Status);
    }

    [Theory]
    [InlineData("Journey")]
    [InlineData("Chapter")]
    [InlineData("Scene")]
    public void A_non_finite_story_position_is_held_not_repaired(string field)
    {
        var src = V1();
        string json = src.ToJsonString();
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove(field);
        string text = node.ToJsonString();
        text = text[..^1] + ",\"" + field + "\":1e400}";
        var b = Encoding.UTF8.GetBytes(text);

        AssertHold(StateImport.Inspect(b), ImportReason.MainFieldsUnreadable, b);
    }

    [Fact]
    public void A_non_finite_experience_value_is_not_carried()
    {
        var src = V1();
        string text = src.ToJsonString().Replace("\"Experience\":{", "\"Experience\":{\"zzz\":1e400,");
        var v = Inspect(text);

        Assert.NotEqual(ImportStatus.Hold, v.Status);
        Assert.False(Read(v).Experience.ContainsKey("zzz"));
        Assert.Equal(ImportStatus.Acceptable, StateImport.Inspect(v.Converted).Status);
    }
}
