using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PcMieru.Checks.Tests;

/// <summary>pcm-check/1 JSON: the golden files, byte-identical round trips, the DESIGN-A §3.4 example, and strict reading.</summary>
public class SerializationTests
{
    // DESIGN-A §3.4, word for word.
    private const string DesignExample = """
        {
          "format": "pcm-check/1",
          "app": "PCMieru 0.4.0",
          "runId": "89914ef9-5781-4cf7-a4f8-6d58003ddf86",
          "kind": "memory",
          "startedAtUtc": "2026-11-20T03:00:00.000Z",
          "endedAtUtc": "2026-11-20T03:00:04.200Z",
          "outcome": "stopped",
          "verdict": "check",
          "counts": {"ok": 0, "caution": 0, "check": 1, "unavailable": 0, "info": 8},
          "items": [
            {"id":"memory.duration","value":4.2,"unit":"s","verdict":"info","source":"load","reason":null},
            {"id":"memory.total","value":"34359738368","unit":"B","verdict":"info","source":"win32","reason":null},
            {"id":"memory.available_at_start","value":"12884901888","unit":"B","verdict":"info","source":"win32","reason":null},
            {"id":"memory.borrowed","value":"3221225472","unit":"B","verdict":"info","source":"load","reason":null},
            {"id":"memory.passes","value":null,"unit":"count","verdict":"info","source":"load","reason":"partial"},
            {"id":"memory.verified","value":"134217728","unit":"B","verdict":"info","source":"load","reason":"partial"},
            {"id":"memory.mismatches","value":1,"unit":"count","verdict":"check","source":"load","reason":"partial","evidence":{"code":"pattern_mismatch"}},
            {"id":"memory.write_rate","value":6400,"unit":"MiB/s","verdict":"info","source":"load","reason":null},
            {"id":"memory.verify_rate","value":5300,"unit":"MiB/s","verdict":"info","source":"load","reason":null}
          ],
          "notes": ["memory.borrowed_region_only"]
        }
        """;

    private static string Base => CheckJson.Serialize(Samples.MemoryCheck());

    private static string Mutate(string find, string replace)
    {
        var json = Base;
        var index = json.IndexOf(find, StringComparison.Ordinal);
        Assert.True(index >= 0, $"test bug: '{find}' is not in the base JSON");
        return json[..index] + replace + json[(index + find.Length)..];
    }

    private static void Rejects(string json, string fragment)
    {
        var ex = Assert.Throws<CheckFormatException>(() => CheckJson.Deserialize(json));
        Assert.True(
            ex.Problems.Any(p => p.Contains(fragment, StringComparison.Ordinal)),
            $"expected a problem containing '{fragment}', got: {string.Join(" | ", ex.Problems)}");
    }

    public static TheoryData<string> FixtureNames => new() { "system.json", "cpu.json", "memory.json", "storage.json" };

    private static CheckResult Sample(string fixtureName) => Samples.All.Single(s => s.Name == fixtureName).Result;

    // ---- golden files ----

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void The_sample_matches_its_golden_file(string name) =>
        Fixture.AssertMatches(name, CheckJson.Serialize(Sample(name), indented: true));

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void The_golden_file_reads_back_to_the_same_bytes_indented(string name)
    {
        var text = Fixture.Read(name);
        var result = CheckJson.Deserialize(text);
        Assert.Equal(Fixture.Normalize(text), Fixture.Normalize(CheckJson.Serialize(result, indented: true)));
        Assert.Equal(Encoding.UTF8.GetBytes(Fixture.Normalize(text)), Encoding.UTF8.GetBytes(Fixture.Normalize(CheckJson.Serialize(result, indented: true))));
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Compact_round_trip_is_byte_identical(string name)
    {
        var first = CheckJson.SerializeToUtf8(Sample(name));
        var again = CheckJson.SerializeToUtf8(CheckJson.Deserialize(first));
        Assert.Equal(first, again);

        // Indented and compact are the same data.
        var viaIndented = CheckJson.SerializeToUtf8(CheckJson.Deserialize(CheckJson.Serialize(Sample(name), indented: true)));
        Assert.Equal(first, viaIndented);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Output_is_deterministic(string name)
    {
        var a = CheckJson.Serialize(Sample(name), indented: true);
        var b = CheckJson.Serialize(Samples.All.Single(s => s.Name == name).Result, indented: true);
        Assert.Equal(a, b);
        Assert.DoesNotContain('\r', a);
        Assert.False(a.StartsWith('﻿'));
        Assert.False(a.EndsWith('\n'));
    }

    [Theory]
    [InlineData(CheckKind.System)]
    [InlineData(CheckKind.Cpu)]
    [InlineData(CheckKind.Memory)]
    [InlineData(CheckKind.Storage)]
    public void Every_registered_id_writes_validates_and_reads_back(CheckKind kind)
    {
        var result = Samples.EveryId(kind);
        Assert.Empty(CheckResultValidator.Validate(result));

        var json = CheckJson.Serialize(result);
        var back = CheckJson.Deserialize(json);
        Assert.Equal(json, CheckJson.Serialize(back));
        Assert.Equal(result.Items.Count, back.Items.Count);
        Assert.Equal(result.Counts, back.Counts);
        Assert.Equal(result.Verdict, back.Verdict);
        Assert.Equal(CheckItemRegistry.ForKind(kind).Count(), back.Items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public void The_four_kinds_are_what_the_fixtures_cover()
    {
        Assert.Equal(
            [CheckKind.System, CheckKind.Cpu, CheckKind.Memory, CheckKind.Storage],
            Samples.All.Select(s => s.Result.Kind).ToArray());
    }

    // ---- DESIGN-A §3.4 ----

    [Fact]
    public void The_design_example_reads_with_info_8_check_1_and_overall_check()
    {
        var result = CheckJson.Deserialize(DesignExample);

        Assert.Equal("pcm-check/1", result.Format);
        Assert.Equal(CheckKind.Memory, result.Kind);
        Assert.Equal(CheckOutcome.Stopped, result.Outcome);
        Assert.Equal(CheckVerdict.Check, result.Verdict);
        Assert.Equal(new CheckCounts(Ok: 0, Caution: 0, Check: 1, Unavailable: 0, Info: 8), result.Counts);
        Assert.Equal(9, result.Items.Count);
        Assert.Equal(["memory.borrowed_region_only"], result.Notes.ToArray());

        var mismatches = result.Items.Single(i => i.Id == "memory.mismatches");
        Assert.Equal(ItemVerdict.Check, mismatches.Verdict);
        Assert.Equal(CheckReason.Partial, mismatches.Reason);
        Assert.Equal(1, mismatches.Value!.AsInt);
        Assert.Equal(CheckEvidenceCode.PatternMismatch, mismatches.Evidence!.Code);
        Assert.Equal("34359738368", result.Items.Single(i => i.Id == "memory.total").Value!.AsText);
        Assert.Null(result.Items.Single(i => i.Id == "memory.passes").Value);
    }

    [Fact]
    public void The_design_example_is_written_back_exactly_apart_from_whitespace()
    {
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var minified = JsonNode.Parse(DesignExample)!.ToJsonString(options);
        Assert.Equal(minified, CheckJson.Serialize(CheckJson.Deserialize(DesignExample)));
    }

    [Fact]
    public void The_memory_sample_is_the_design_example()
    {
        Assert.Equal(CheckJson.Serialize(CheckJson.Deserialize(DesignExample)), Base);
    }

    // ---- the exact form ----

    [Fact]
    public void Values_have_the_form_of_their_type()
    {
        using var doc = JsonDocument.Parse(CheckJson.Serialize(Samples.StorageCheck()));
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();

        JsonElement Value(string id, string subject) =>
            items.Single(i => i.GetProperty("id").GetString() == id && i.GetProperty("subject").GetProperty("id").GetString() == subject).GetProperty("value");

        Assert.Equal(JsonValueKind.String, Value("storage.capacity", "disk-1").ValueKind); // dec
        Assert.Equal("2000398934016", Value("storage.capacity", "disk-1").GetString());
        Assert.Equal(JsonValueKind.String, Value("storage.nvme.error_log_entries.delta", "disk-1").ValueKind); // signed dec
        Assert.Equal(JsonValueKind.Number, Value("storage.nvme.critical_warning", "disk-1").ValueKind); // int
        Assert.Equal(JsonValueKind.Number, Value("storage.temperature", "disk-2").ValueKind); // number
        Assert.Equal(JsonValueKind.False, Value("storage.ata.predict_failure", "disk-2").ValueKind); // bool
        Assert.Equal(JsonValueKind.String, Value("storage.health", "disk-1").ValueKind); // text
        Assert.Equal(JsonValueKind.Null, Value("storage.nvme.data_units_written.delta", "disk-1").ValueKind); // not comparable
    }

    [Fact]
    public void Numbers_are_written_in_their_shortest_form_and_minus_zero_is_zero()
    {
        var json = Base;
        Assert.Contains("\"value\":4.2,", json, StringComparison.Ordinal);
        Assert.Contains("\"value\":6400,", json, StringComparison.Ordinal);
        Assert.DoesNotContain("6400.0", json, StringComparison.Ordinal);

        var negativeZero = CheckItem.Create("memory.duration", CheckValue.Number(-0.0), ItemVerdict.Info, CheckSource.Load);
        var result = CheckResult.Create(Samples.App, Samples.MemoryRun, CheckKind.Memory, Samples.Start, Samples.Start, CheckOutcome.Completed, [negativeZero]);
        Assert.Contains("\"value\":0,", CheckJson.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void A_dec_holds_128_bits_and_a_signed_dec_a_sign()
    {
        var since = Samples.Start;
        var counter = CheckItem.Create("storage.nvme.media_errors", CheckValue.Dec(UInt128.MaxValue), ItemVerdict.Ok, CheckSource.Nvme, subject: new CheckSubject("disk-1"));
        var delta = CheckItem.Create(
            "storage.nvme.media_errors.delta",
            CheckValue.SignedDec("-5"),
            ItemVerdict.Ok,
            CheckSource.Baseline,
            subject: new CheckSubject("disk-1"),
            evidence: new CheckEvidence { Before = "10", After = "5", SinceUtc = since });
        var result = CheckResult.Create(Samples.App, Samples.StorageRun, CheckKind.Storage, Samples.Start, Samples.Start, CheckOutcome.Completed, [counter, delta]);

        var json = CheckJson.Serialize(result);
        Assert.Contains("\"value\":\"340282366920938463463374607431768211455\"", json, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"-5\"", json, StringComparison.Ordinal);
        Assert.Equal(json, CheckJson.Serialize(CheckJson.Deserialize(json)));
    }

    [Fact]
    public void Item_keys_come_in_the_fixed_order_and_nulls_follow_section_3()
    {
        var json = CheckJson.Serialize(Samples.SystemCheck());

        // value, unit and reason are always there (explicit null); subject and evidence only when there is one.
        Assert.Contains(
            "{\"id\":\"system.memory.capacity\",\"subject\":{\"id\":\"ram-1\"},\"value\":\"17179869184\",\"unit\":\"B\",\"verdict\":\"info\",\"source\":\"smbios\",\"reason\":null}",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "{\"id\":\"system.gpu.model\",\"subject\":{\"id\":\"gpu-1\",\"model\":\"NVIDIA GeForce RTX 5070\"},\"value\":\"NVIDIA GeForce RTX 5070\",\"unit\":null,\"verdict\":\"info\",\"source\":\"dxgi\",\"reason\":null}",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "{\"id\":\"system.eventlog.volmgr7d\",\"value\":null,\"unit\":\"count\",\"verdict\":\"unavailable\",\"source\":\"eventlog\",\"reason\":\"denied\"}",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"evidence\":{\"totalBytes\":\"2000398934016\",\"diskId\":\"disk-1\"}",
            json,
            StringComparison.Ordinal);
        Assert.StartsWith("{\"format\":\"pcm-check/1\",\"app\":\"PCMieru 0.4.0\",\"runId\":\"", json, StringComparison.Ordinal);
        Assert.EndsWith("],\"notes\":[]}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_degree_sign_is_written_as_itself()
    {
        var json = CheckJson.Serialize(Samples.StorageCheck(), indented: true);
        Assert.Contains("\"unit\": \"°C\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u00B0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Times_are_utc_with_milliseconds_and_a_z()
    {
        var json = Base;
        Assert.Contains("\"startedAtUtc\":\"2026-11-20T03:00:00.000Z\"", json, StringComparison.Ordinal);
        Assert.Contains("\"endedAtUtc\":\"2026-11-20T03:00:04.200Z\"", json, StringComparison.Ordinal);

        // A clock reading with 7 digits is cut to milliseconds before it goes into a result (the file name keeps all 7, §4).
        var local = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.FromHours(9)).AddTicks(1_234_567);
        var cut = CheckTime.TruncateToMilliseconds(local);
        Assert.Equal("2026-11-20T03:00:00.123Z", CheckTime.Format(cut));
        Assert.True(CheckTime.IsCanonical(cut));
        Assert.False(CheckTime.IsCanonical(local));
    }

    [Theory]
    [InlineData("2026-11-20T03:00:00.000Z", true)]
    [InlineData("2026-11-20T03:00:00Z", false)]
    [InlineData("2026-11-20T03:00:00.0000000Z", false)]
    [InlineData("2026-11-20T03:00:00.000+00:00", false)]
    [InlineData("2026-11-20T12:00:00.000+09:00", false)]
    [InlineData("2026-11-20 03:00:00.000Z", false)]
    [InlineData("2026-11-20T03:00:00.000", false)]
    [InlineData("2026-13-20T03:00:00.000Z", false)]
    [InlineData("2026-11-20T24:00:00.000Z", false)]
    [InlineData("", false)]
    public void Time_parsing_is_strict(string text, bool valid) =>
        Assert.Equal(valid, CheckTime.TryParse(text, out _));

    // ---- strict reading ----

    [Fact]
    public void The_base_json_is_valid()
    {
        var result = CheckJson.Deserialize(Base);
        Assert.Equal(CheckVerdict.Check, result.Verdict);
    }

    [Theory]
    // an unknown source / evidence code / id / key is rejected, never skipped
    [InlineData("\"source\":\"load\"", "\"source\":\"gpu\"", "unknown source 'gpu'")]
    [InlineData("\"source\":\"load\"", "\"source\":\"Load\"", "unknown source 'Load'")]
    [InlineData("\"source\":\"load\"", "\"source\":5", "must be a string")]
    [InlineData("\"code\":\"pattern_mismatch\"", "\"code\":\"bogus_code\"", "'bogus_code'")]
    [InlineData("\"code\":\"pattern_mismatch\"", "\"code\":\"PatternMismatch\"", "'PatternMismatch'")]
    [InlineData("\"reason\":\"partial\",\"evidence\"", "\"reason\":\"mystery\",\"evidence\"", "unknown reason 'mystery'")]
    [InlineData("\"verdict\":\"check\",\"source\"", "\"verdict\":\"fail\",\"source\"", "unknown itemverdict 'fail'")]
    [InlineData("\"id\":\"memory.duration\"", "\"id\":\"memory.latency\"", "unknown item ID")]
    [InlineData("{\"format\"", "{\"extra\":1,\"format\"", "unknown key 'extra'")]
    [InlineData("\"info\":8}", "\"info\":8,\"extra\":1}", "unknown key 'extra'")]
    [InlineData("\"reason\":null}", "\"reason\":null,\"note\":\"x\"}", "unknown key 'note'")]
    [InlineData("{\"code\":\"pattern_mismatch\"}", "{\"code\":\"pattern_mismatch\",\"detail\":\"x\"}", "unknown key 'detail'")]
    // a duplicate key is rejected
    [InlineData("\"kind\":\"memory\",", "\"kind\":\"memory\",\"kind\":\"memory\",", "duplicate key 'kind'")]
    [InlineData("\"value\":4.2,", "\"value\":4.2,\"value\":4.3,", "duplicate key 'value'")]
    // a missing key is rejected
    [InlineData("\"source\":\"load\",\"reason\":null},{\"id\":\"memory.total\"", "\"source\":\"load\"},{\"id\":\"memory.total\"", "missing key 'reason'")]
    [InlineData("\"app\":\"PCMieru 0.4.0\",", "", "missing key 'app'")]
    // enum strings are lowercase
    [InlineData("\"kind\":\"memory\"", "\"kind\":\"Memory\"", "unknown kind 'Memory'")]
    [InlineData("\"outcome\":\"stopped\"", "\"outcome\":\"STOPPED\"", "unknown outcome 'STOPPED'")]
    [InlineData("\"outcome\":\"stopped\"", "\"outcome\":2", "must be a string")]
    // dec and signed dec are canonical strings
    [InlineData("\"value\":\"34359738368\"", "\"value\":\"034359738368\"", "canonical unsigned decimal string")]
    [InlineData("\"value\":\"34359738368\"", "\"value\":\"-5\"", "canonical unsigned decimal string")]
    [InlineData("\"value\":\"34359738368\"", "\"value\":\"+5\"", "canonical unsigned decimal string")]
    [InlineData("\"value\":\"34359738368\"", "\"value\":\"1e3\"", "canonical unsigned decimal string")]
    [InlineData("\"value\":\"34359738368\"", "\"value\":\"\"", "canonical unsigned decimal string")]
    [InlineData("\"value\":\"34359738368\"", "\"value\":34359738368", "canonical unsigned decimal string")]
    [InlineData("\"value\":\"34359738368\"", "\"value\":\"340282366920938463463374607431768211456\"", "canonical unsigned decimal string")]
    // int is a JSON integer, number is a finite JSON number, nothing is a string in disguise
    [InlineData("\"value\":1,\"unit\":\"count\"", "\"value\":\"1\",\"unit\":\"count\"", "JSON integer")]
    [InlineData("\"value\":1,\"unit\":\"count\"", "\"value\":1.5,\"unit\":\"count\"", "JSON integer")]
    [InlineData("\"value\":1,\"unit\":\"count\"", "\"value\":true,\"unit\":\"count\"", "JSON integer")]
    [InlineData("\"value\":4.2,", "\"value\":\"4.2\",", "finite JSON number")]
    // times
    [InlineData("\"startedAtUtc\":\"2026-11-20T03:00:00.000Z\"", "\"startedAtUtc\":\"2026-11-20T03:00:00Z\"", "UTC time")]
    [InlineData("\"startedAtUtc\":\"2026-11-20T03:00:00.000Z\"", "\"startedAtUtc\":\"2026-11-20T12:00:00.000+09:00\"", "UTC time")]
    [InlineData("\"startedAtUtc\":\"2026-11-20T03:00:00.000Z\"", "\"startedAtUtc\":\"2026-11-20T03:00:00.0000000Z\"", "UTC time")]
    [InlineData("\"startedAtUtc\":\"2026-11-20T03:00:00.000Z\"", "\"startedAtUtc\":1763607600", "UTC time")]
    [InlineData("\"startedAtUtc\":\"2026-11-20T03:00:00.000Z\"", "\"startedAtUtc\":\"2026-11-20T03:00:05.000Z\"", "endedAtUtc is before startedAtUtc")]
    // counts and verdict are the items' arithmetic, not a claim
    [InlineData("\"info\":8", "\"info\":9", "counts")]
    [InlineData("\"check\":1,", "\"check\":2,", "counts")]
    [InlineData("\"info\":8", "\"info\":-8", "counts")]
    [InlineData("\"verdict\":\"check\",\"counts\"", "\"verdict\":\"ok\",\"counts\"", "does not match the items")]
    [InlineData("\"verdict\":\"check\",\"counts\"", "\"verdict\":\"info\",\"counts\"", "unknown verdict 'info'")]
    // a value with a reason is allowed for partial only; a missing value needs a reason
    [InlineData("\"reason\":\"partial\",\"evidence\"", "\"reason\":\"error\",\"evidence\"", "reason must be null or partial")]
    [InlineData("\"reason\":\"partial\",\"evidence\"", "\"reason\":\"timeout\",\"evidence\"", "reason must be null or partial")]
    [InlineData("\"value\":null,\"unit\":\"count\",\"verdict\":\"info\",\"source\":\"load\",\"reason\":\"partial\"", "\"value\":null,\"unit\":\"count\",\"verdict\":\"info\",\"source\":\"load\",\"reason\":null", "no value, so a reason is required")]
    // unit, kind, subject, evidence shape
    [InlineData("\"unit\":\"s\"", "\"unit\":\"ms\"", "unit must be 's'")]
    [InlineData("\"unit\":\"s\"", "\"unit\":null", "unit must be 's'")]
    [InlineData("\"kind\":\"memory\"", "\"kind\":\"cpu\"", "belongs to a Memory result, not Cpu")]
    [InlineData("{\"id\":\"memory.duration\",", "{\"id\":\"memory.duration\",\"subject\":{\"id\":\"ram-1\"},", "must not have a subject")]
    [InlineData("{\"code\":\"pattern_mismatch\"}", "{}", "evidence must be omitted rather than empty")]
    [InlineData("{\"code\":\"pattern_mismatch\"}", "{\"code\":\"pattern_mismatch\",\"before\":5}", "must be a string")]
    [InlineData("{\"code\":\"pattern_mismatch\"}", "{\"code\":\"baseline_first\"}", "baseline evidence codes belong to storage items")]
    [InlineData("{\"code\":\"pattern_mismatch\"}", "{\"code\":\"pattern_mismatch\",\"newBits\":1}", "newBits is evidence of storage.nvme.critical_warning.delta only")]
    [InlineData("\"memory.duration\",\"value\":4.2,\"unit\":\"s\",\"verdict\":\"info\"", "\"memory.duration\",\"value\":4.2,\"unit\":\"s\",\"verdict\":\"ok\"", "observation-only item must have verdict info")]
    // result-level
    [InlineData("\"runId\":\"89914ef9-5781-4cf7-a4f8-6d58003ddf86\"", "\"runId\":\"89914EF9-5781-4CF7-A4F8-6D58003DDF86\"", "lowercase UUID")]
    [InlineData("\"runId\":\"89914ef9-5781-4cf7-a4f8-6d58003ddf86\"", "\"runId\":\"89914ef957814cf7a4f86d58003ddf86\"", "lowercase UUID")]
    [InlineData("\"runId\":\"89914ef9-5781-4cf7-a4f8-6d58003ddf86\"", "\"runId\":\"00000000-0000-0000-0000-000000000000\"", "empty UUID")]
    [InlineData("\"runId\":\"89914ef9-5781-4cf7-a4f8-6d58003ddf86\"", "\"runId\":7", "must be a string")]
    [InlineData("\"app\":\"PCMieru 0.4.0\"", "\"app\":\"\"", "app must not be empty")]
    [InlineData("\"notes\":[\"memory.borrowed_region_only\"]", "\"notes\":[\"Bad Note\"]", "not of the form area.snake_code")]
    [InlineData("\"notes\":[\"memory.borrowed_region_only\"]", "\"notes\":[\"memory.borrowed_region_only\",\"memory.borrowed_region_only\"]", "appears twice")]
    [InlineData("\"notes\":[\"memory.borrowed_region_only\"]", "\"notes\":\"memory.borrowed_region_only\"", "must be an array")]
    [InlineData("\"notes\":[\"memory.borrowed_region_only\"]}", "\"notes\":[\"memory.borrowed_region_only\"],}", "Not valid JSON")]
    public void A_broken_result_is_rejected(string find, string replace, string fragment) =>
        Rejects(Mutate(find, replace), fragment);

    [Theory]
    [InlineData("items", "{}")]
    [InlineData("items", "\"x\"")]
    [InlineData("items", "null")]
    [InlineData("counts", "[]")]
    [InlineData("notes", "null")]
    [InlineData("notes", "[5]")]
    public void A_key_of_the_wrong_json_type_is_rejected(string key, string replacement)
    {
        var node = JsonNode.Parse(Base)!.AsObject();
        node[key] = JsonNode.Parse(replacement);
        Assert.Throws<CheckFormatException>(() => CheckJson.Deserialize(node.ToJsonString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"pcm-check/1\"")]
    [InlineData("{}")]
    [InlineData("{\"format\":5}")]
    [InlineData("{\"app\":\"x\"}")]
    public void Input_that_is_not_a_result_is_rejected(string json)
    {
        var ex = Assert.Throws<CheckFormatException>(() => CheckJson.Deserialize(json));
        Assert.Null(ex.UnknownFormat);
    }

    [Fact]
    public void Text_after_the_json_and_comments_are_rejected()
    {
        Rejects(Base + " x", "Not valid JSON");
        Rejects(Base + "{}", "Not valid JSON");
        Rejects("/* c */" + Base, "Not valid JSON");
    }

    [Theory]
    [InlineData("pcm-check/2")]
    [InlineData("pcm-check/10")]
    [InlineData("pcm-check/1.1")]
    [InlineData("PCM-CHECK/1")]
    [InlineData("other/1")]
    public void An_unknown_format_is_told_apart_from_a_broken_file(string format)
    {
        // A newer version may add keys and items; the reader only says "future format" (keep the file), it does not call it broken.
        var json = Mutate("\"format\":\"pcm-check/1\"", $"\"format\":\"{format}\",\"brand_new_key\":{{\"x\":1}}");
        var ex = Assert.Throws<CheckFormatException>(() => CheckJson.Deserialize(json));
        Assert.Equal(format, ex.UnknownFormat);

        Assert.False(CheckJson.TryDeserialize(json, out var result, out var error));
        Assert.Null(result);
        Assert.Equal(format, error!.UnknownFormat);
    }

    [Fact]
    public void TryDeserialize_returns_the_result_or_the_reason()
    {
        Assert.True(CheckJson.TryDeserialize(Base, out var result, out var error));
        Assert.Null(error);
        Assert.Equal(CheckKind.Memory, result!.Kind);

        Assert.False(CheckJson.TryDeserialize("{", out var none, out var why));
        Assert.Null(none);
        Assert.NotNull(why);
        Assert.Null(why.UnknownFormat);
    }

    [Fact]
    public void Reading_from_bytes_gives_the_same_result()
    {
        var bytes = Encoding.UTF8.GetBytes(Base);
        Assert.Equal(Base, CheckJson.Serialize(CheckJson.Deserialize(bytes)));
        Assert.Throws<CheckFormatException>(() => CheckJson.Deserialize("{"u8));
    }

    // ---- the writer holds the same rules ----

    [Fact]
    public void The_writer_refuses_a_result_that_breaks_a_rule()
    {
        var ok = Samples.MemoryCheck();

        Assert.Throws<CheckFormatException>(() => CheckJson.Serialize(ok with { Counts = new CheckCounts(0, 0, 0, 0, 9) }));
        Assert.Throws<CheckFormatException>(() => CheckJson.Serialize(ok with { Verdict = CheckVerdict.Ok }));
        Assert.Throws<CheckFormatException>(() => CheckJson.Serialize(ok with { Format = "pcm-check/2" }));
        Assert.Throws<CheckFormatException>(() => CheckJson.Serialize(ok with { StartedAtUtc = ok.StartedAtUtc.AddTicks(1) }));
        Assert.Throws<CheckFormatException>(() => CheckJson.Serialize(ok with { StartedAtUtc = ok.StartedAtUtc.ToOffset(TimeSpan.FromHours(9)) }));
        Assert.Throws<CheckFormatException>(() => CheckJson.Serialize(ok with { Kind = CheckKind.Cpu }));
        Assert.Throws<CheckFormatException>(() => CheckJson.Serialize(ok with { Notes = ["BAD"] }));

        // Counts and verdict are recomputed for the items below, so only the item itself can be what is refused.
        CheckResult WithItem(CheckItem item) =>
            CheckResult.Create(Samples.App, Samples.MemoryRun, CheckKind.Memory, Samples.Start, Samples.Start, CheckOutcome.Completed, [item]);

        var bogusId = new CheckItem("memory.bogus", null, CheckValue.Int(1), "count", ItemVerdict.Ok, CheckSource.Load, null);
        var ex = Assert.Throws<CheckFormatException>(() => CheckJson.Serialize(WithItem(bogusId)));
        Assert.Contains("unknown item ID", ex.Problems.Single(), StringComparison.Ordinal);

        var unknownSource = ok.Items[0] with { Source = (CheckSource)99 };
        ex = Assert.Throws<CheckFormatException>(() => CheckJson.Serialize(WithItem(unknownSource)));
        Assert.Contains("unknown source", ex.Problems.Single(), StringComparison.Ordinal);

        var unknownEvidenceCode = ok.Items[6] with { Evidence = new CheckEvidence { Code = (CheckEvidenceCode)99 } };
        ex = Assert.Throws<CheckFormatException>(() => CheckJson.Serialize(WithItem(unknownEvidenceCode)));
        Assert.Contains("unknown evidence code", ex.Problems.Single(), StringComparison.Ordinal);

        // A value with a reason other than partial.
        var reasoned = ok.Items[6] with { Reason = CheckReason.Error };
        ex = Assert.Throws<CheckFormatException>(() => CheckJson.Serialize(WithItem(reasoned)));
        Assert.Contains("reason must be null or partial", ex.Problems.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_result_that_failed_validation_names_every_broken_rule()
    {
        var ok = Samples.MemoryCheck();
        var ex = Assert.Throws<CheckFormatException>(() => CheckJson.Serialize(ok with { Counts = new CheckCounts(1, 1, 1, 1, 1), Verdict = CheckVerdict.Ok, App = " " }));
        Assert.True(ex.Problems.Count >= 3, string.Join(" | ", ex.Problems));
        Assert.Contains("and ", ex.Message, StringComparison.Ordinal);
    }
}
