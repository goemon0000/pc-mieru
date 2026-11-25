using System.Text;
using System.Text.Json.Nodes;
using PcMieru.Party.Import;
using static PcMieru.Party.Core.Tests.Import.ImportRig;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>
/// Sources that must NOT be converted. A hold writes nothing; the verdict still carries the hash of the source and a stable
/// reason code, and never any converted bytes.
/// </summary>
public class StateImportHoldTests
{
    private static ImportVerdict Raw(string json, out byte[] bytes)
    {
        bytes = Encoding.UTF8.GetBytes(json);
        return StateImport.Inspect(bytes);
    }

    // ---- size, depth, syntax ------------------------------------------------------------------------------------------

    [Fact]
    public void One_byte_over_8_MiB_is_held_before_parsing()
    {
        var big = new byte[StateImport.MaxBytes + 1];
        Array.Fill(big, (byte)' ');
        var v = StateImport.Inspect(big);
        AssertHold(v, ImportReason.TooLarge, big);
    }

    [Fact]
    public void Depth_65_is_held()
    {
        var v = Raw("""{"A":""" + new string('[', 64) + new string(']', 64) + "}", out var b);
        AssertHold(v, ImportReason.TooDeep, b);

        v = Raw(new string('[', 65) + new string(']', 65), out b);
        AssertHold(v, ImportReason.TooDeep, b);
    }

    [Fact]
    public void Absurd_nesting_is_held_without_blowing_the_stack()
    {
        var v = Raw("""{"A":""" + new string('[', 300_000), out var b);
        AssertHold(v, ImportReason.TooDeep, b);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("{\"Experience\":{\"memory\":1},")]
    [InlineData("{\"Journey\":1,}")]
    [InlineData("{\"Journey\":1} x")]
    [InlineData("{\"Journey\":1}{\"Journey\":2}")]
    [InlineData("{'Journey':1}")]
    [InlineData("{\"Journey\":NaN}")]
    public void Broken_json_is_held(string json)
    {
        AssertHold(Raw(json, out var b), ImportReason.BrokenJson, b);
    }

    [Fact]
    public void Bytes_that_are_not_utf8_are_held()
    {
        byte[] b = [.. Encoding.UTF8.GetBytes("{\"Lang\":\""), 0xFF, 0xFE, .. Encoding.UTF8.GetBytes("\"}")];
        AssertHold(StateImport.Inspect(b), ImportReason.BrokenJson, b);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("5")]
    [InlineData("\"x\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void A_root_that_is_not_an_object_is_held(string json)
    {
        AssertHold(Raw(json, out var b), ImportReason.NotAnObject, b);
    }

    // ---- duplicate keys ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_repeated_key_at_the_root_is_held()
    {
        var v = Raw("""{"Experience":{"memory":1},"Journey":1,"Chapter":1,"Scene":1,"Chapter":9}""", out var b);
        AssertHold(v, ImportReason.DuplicateKey, b);
        Assert.Contains("Chapter", v.Detail);
    }

    [Theory]
    [InlineData("""{"Experience":{"memory":1,"memory":2},"Journey":1,"Chapter":1,"Scene":1}""")]                      // inside the level table
    [InlineData("""{"Experience":{"memory":1},"Journey":1,"Chapter":1,"Scene":1,"Adventure":{"Gold":1,"Gold":2}}""")]   // inside Adventure
    [InlineData("""{"Experience":{"memory":1},"Journey":1,"Chapter":1,"Scene":1,"X":[{"a":1,"a":1}]}""")]               // inside an unknown field
    [InlineData("""{"Experience":{"memory":1},"Journey":1,"Chapter":1,"Scene":1,"Chapter":1}""")]                       // same value twice is still a repeat
    [InlineData("""{"Experience":{"memory":1},"Journey":1,"Chapter":1,"Chapt\u0065r":2,"Scene":1}""")]                  // a spelling by escape is the same key
    public void A_repeated_key_anywhere_is_held(string json)
    {
        AssertHold(Raw(json, out var b), ImportReason.DuplicateKey, b);
    }

    [Fact]
    public void The_same_key_in_two_different_objects_is_fine()
    {
        var v = Raw("""{"Experience":{"memory":1},"Journey":1,"Chapter":1,"Scene":1,"X":{"a":1},"Y":{"a":2},"Z":[{"a":1},{"a":1}]}""", out _);
        Assert.Equal(ImportStatus.Acceptable, v.Status);
    }

    // ---- SaveVersion ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("{\"v\":1}")]
    [InlineData("1.5")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("0.0")]
    public void A_version_that_is_not_an_integer_is_held(string value)
    {
        var src = Minimal();
        src["SaveVersion"] = JsonNode.Parse(value);
        var b = Bytes(src);
        AssertHold(StateImport.Inspect(b), ImportReason.VersionType, b);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-2147483648")]
    [InlineData("-99999999999999999999999")]
    public void A_negative_version_is_held(string value)
    {
        var src = Minimal();
        src["SaveVersion"] = JsonNode.Parse(value);
        var b = Bytes(src);
        AssertHold(StateImport.Inspect(b), ImportReason.VersionNegative, b);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("99")]
    [InlineData("2147483647")]
    [InlineData("12345678901234567890123")]
    public void A_version_newer_than_this_program_knows_is_held(string value)
    {
        var src = Minimal();
        src["SaveVersion"] = JsonNode.Parse(value);
        var b = Bytes(src);
        var v = StateImport.Inspect(b);
        AssertHold(v, ImportReason.VersionFuture, b);
    }

    [Fact]
    public void A_version_with_a_million_digits_is_held_quickly_and_not_parsed_as_a_number()
    {
        string json = """{"Experience":{"memory":1},"Journey":1,"Chapter":1,"Scene":1,"SaveVersion":""" + new string('9', 1_000_000) + "}";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var v = Raw(json, out var b);
        AssertHold(v, ImportReason.VersionFuture, b);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
    }

    [Fact]
    public void A_future_version_is_held_even_when_everything_else_is_good()
    {
        // a save from a newer app: converting it would drop what only the newer app understands
        var src = V1();
        src["SaveVersion"] = 2;
        var b = Bytes(src);
        var v = StateImport.Inspect(b);
        AssertHold(v, ImportReason.VersionFuture, b);
        Assert.Contains("2", v.Detail);
    }

    // ---- the fields that make a save a continuation --------------------------------------------------------------------

    [Theory]
    [InlineData("Experience", "missing")]
    [InlineData("Experience", "string")]
    [InlineData("Experience", "array")]
    [InlineData("Experience", "null")]
    [InlineData("Experience", "nothing numeric")]
    [InlineData("Journey", "missing")]
    [InlineData("Journey", "string")]
    [InlineData("Journey", "null")]
    [InlineData("Chapter", "missing")]
    [InlineData("Chapter", "string")]
    [InlineData("Chapter", "bool")]
    [InlineData("Scene", "missing")]
    [InlineData("Scene", "object")]
    public void Unreadable_main_fields_are_held_not_repaired(string field, string how)
    {
        foreach (int version in new[] { 1, 0 })
        {
            var src = Minimal(version);
            switch (how)
            {
                case "missing": src.Remove(field); break;
                case "string": src[field] = "seven"; break;
                case "array": src[field] = new JsonArray(1, 2); break;
                case "null": src[field] = null; break;
                case "bool": src[field] = true; break;
                case "object": src[field] = new JsonObject { ["a"] = 1 }; break;
                case "nothing numeric": src[field] = new JsonObject { ["memory"] = "a", ["ssd"] = null }; break;
                default: throw new InvalidOperationException(how);
            }
            var b = Bytes(src);
            var v = StateImport.Inspect(b);
            AssertHold(v, ImportReason.MainFieldsUnreadable, b);
            Assert.Contains(field, v.Detail);
            Assert.Equal(version, v.SourceVersion);
        }
    }

    [Fact]
    public void Every_unreadable_main_field_is_named()
    {
        var v = Raw("""{"Experience":"x","Journey":"y","Chapter":{},"Scene":[]}""", out var b);
        AssertHold(v, ImportReason.MainFieldsUnreadable, b);
        foreach (var f in new[] { "Experience", "Journey", "Chapter", "Scene" }) Assert.Contains(f, v.Detail);
    }

    [Fact]
    public void An_empty_level_table_is_a_fresh_game_not_a_hold()
    {
        var src = Minimal();
        src["Experience"] = new JsonObject();
        var v = Inspect(src);
        Assert.Equal(ImportStatus.Acceptable, v.Status);
        Assert.Empty(Read(v).Experience);
    }

    [Fact]
    public void Some_unreadable_entries_in_the_level_table_do_not_hold_when_one_is_good()
    {
        var src = Minimal();
        src["Experience"] = new JsonObject { ["memory"] = 5.5, ["ssd"] = "bad" };
        var v = Inspect(src);
        Assert.Equal(ImportStatus.NeedsRepair, v.Status);
        Assert.Contains("Experience[ssd]", v.RepairedFields);
        Assert.Equal(5.5, Read(v).Experience["memory"]);
    }

    // ---- what a hold looks like ----------------------------------------------------------------------------------------

    [Fact]
    public void A_hold_carries_a_reason_a_hash_and_no_bytes()
    {
        var v = Raw("{", out var b);
        Assert.Equal(ImportStatus.Hold, v.Status);
        Assert.Equal("hold", v.Code);
        Assert.False(string.IsNullOrEmpty(v.Reason));
        Assert.False(string.IsNullOrEmpty(v.Detail));
        Assert.Null(v.Converted);
        Assert.Equal(Sha(b), v.SourceSha256);
        Assert.Empty(v.RepairedFields);
    }

    [Fact]
    public void Reason_codes_are_distinct_and_stable()
    {
        // The strings are the contract with whatever shows or logs them; renaming one is a breaking change.
        string[] all =
        [
            ImportReason.TooLarge, ImportReason.BrokenJson, ImportReason.TooDeep, ImportReason.DuplicateKey, ImportReason.NotAnObject,
            ImportReason.VersionType, ImportReason.VersionNegative, ImportReason.VersionFuture, ImportReason.MainFieldsUnreadable,
            ImportReason.ConversionFailed, ImportReason.InternalError, ImportReason.OutputTooLarge,
        ];
        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.Equal(
            ["too-large", "broken-json", "too-deep", "duplicate-key", "not-an-object", "version-type", "version-negative",
             "version-future", "main-fields-unreadable", "conversion-failed", "internal-error", "output-too-large"],
            all);
    }

    [Fact]
    public void A_lone_surrogate_in_a_text_field_never_produces_converted_bytes_that_cannot_be_read_back()
    {
        // JSON allows a lone surrogate escape in a string; it is not valid text. Whatever the importer decides,
        // converted bytes (if any) must be readable again by the importer itself.
        var v = Raw("""{"Experience":{"memory":1},"Journey":1,"Chapter":1,"Scene":1,"Lang":"\ud800"}""", out _);
        if (v.Status == ImportStatus.Hold) return;
        Assert.NotNull(v.Converted);
        Assert.Equal(ImportStatus.Acceptable, StateImport.Inspect(v.Converted).Status);
    }

    // ---- purity --------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_importer_sources_never_name_a_file_or_the_old_load_and_save_paths()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "PcMieru.Party.Core", "Import")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "repository root not found above " + AppContext.BaseDirectory);

        string[] forbidden =
        [
            "PartyState.Load", "PartyState.Save", "System.IO", "File.", "Directory.", "Path.", "FileStream", "StreamReader", "StreamWriter",
            "Environment.", "DateTime", "Registry", "Process", "Console.", "HttpClient", "Random",
        ];
        foreach (var file in Directory.GetFiles(Path.Combine(dir!, "src", "PcMieru.Party.Core", "Import"), "*.cs"))
        {
            // comments may talk about them; code may not
            var code = string.Join('\n', File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//")));
            foreach (var word in forbidden)
                Assert.False(code.Contains(word, StringComparison.Ordinal), $"{Path.GetFileName(file)} mentions {word}");
        }
    }
}
