using System.Text.Json;

namespace PcMieru.Checks.Tests;

/// <summary>The DESIGN-A §2 history shapes: one entry per stored run, plus the warnings. Same spelling rules as the result.</summary>
public class HistoryDtoTests
{
    private const string MemoryEntryJson =
        "{\"runId\":\"89914ef9-5781-4cf7-a4f8-6d58003ddf86\",\"kind\":\"memory\",\"startedAtUtc\":\"2026-11-20T03:00:00.000Z\",\"endedAtUtc\":\"2026-11-20T03:00:04.200Z\",\"outcome\":\"stopped\",\"verdict\":\"check\",\"counts\":{\"ok\":0,\"caution\":0,\"check\":1,\"unavailable\":0,\"info\":8}}";

    [Fact]
    public void An_entry_is_made_from_a_result_without_adding_or_losing_anything()
    {
        var result = Samples.MemoryCheck();
        var entry = CheckHistoryEntry.From(result);

        Assert.Equal(result.RunId, entry.RunId);
        Assert.Equal(result.Kind, entry.Kind);
        Assert.Equal(result.StartedAtUtc, entry.StartedAtUtc);
        Assert.Equal(result.EndedAtUtc, entry.EndedAtUtc);
        Assert.Equal(result.Outcome, entry.Outcome);
        Assert.Equal(result.Verdict, entry.Verdict);
        Assert.Equal(result.Counts, entry.Counts);
        Assert.Throws<ArgumentNullException>(() => CheckHistoryEntry.From(null!));
    }

    [Fact]
    public void A_page_is_camel_case_lowercase_and_has_the_entries_and_warnings_shape()
    {
        var page = new CheckHistoryPage(
            [CheckHistoryEntry.From(Samples.MemoryCheck())],
            [new CheckHistoryWarning(CheckHistoryWarningCodes.FutureFormat, 2)]);

        var json = CheckJson.SerializeHistory(page);

        Assert.Equal(
            "{\"entries\":[" + MemoryEntryJson + "],\"warnings\":[{\"code\":\"history_future_format\",\"count\":2}]}",
            json);
    }

    [Fact]
    public void An_empty_page_still_has_both_keys()
    {
        var json = CheckJson.SerializeHistory(new CheckHistoryPage([], []));
        Assert.Equal("{\"entries\":[],\"warnings\":[]}", json);
        var back = CheckJson.DeserializeHistory(json);
        Assert.Empty(back.Entries);
        Assert.Empty(back.Warnings);
    }

    [Fact]
    public void The_page_round_trips_to_the_same_bytes_and_the_same_values()
    {
        var page = new CheckHistoryPage(
            Samples.All.Select(s => CheckHistoryEntry.From(s.Result)).ToList(),
            [
                new CheckHistoryWarning(CheckHistoryWarningCodes.Unreadable, 1),
                new CheckHistoryWarning(CheckHistoryWarningCodes.Quarantined, 1),
            ]);

        var json = CheckJson.SerializeHistory(page);
        var back = CheckJson.DeserializeHistory(json);

        Assert.Equal(json, CheckJson.SerializeHistory(back));
        Assert.Equal(page.Entries, back.Entries);
        Assert.Equal(page.Warnings, back.Warnings);
        Assert.Equal(CheckJson.SerializeHistory(page, indented: true), CheckJson.SerializeHistory(back, indented: true));
    }

    [Fact]
    public void Every_entry_field_is_what_the_design_says_in_form()
    {
        var page = new CheckHistoryPage(Samples.All.Select(s => CheckHistoryEntry.From(s.Result)).ToList(), []);
        using var doc = JsonDocument.Parse(CheckJson.SerializeHistory(page));

        foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            Assert.Equal(
                ["runId", "kind", "startedAtUtc", "endedAtUtc", "outcome", "verdict", "counts"],
                entry.EnumerateObject().Select(p => p.Name).ToArray());

            var runId = entry.GetProperty("runId").GetString()!;
            Assert.Equal(runId.ToLowerInvariant(), runId);
            Assert.Equal(36, runId.Length);
            Assert.True(Guid.TryParseExact(runId, "D", out _));

            Assert.Equal(24, entry.GetProperty("startedAtUtc").GetString()!.Length);
            Assert.EndsWith("Z", entry.GetProperty("endedAtUtc").GetString(), StringComparison.Ordinal);

            foreach (var name in new[] { "kind", "outcome", "verdict" })
            {
                var text = entry.GetProperty(name).GetString()!;
                Assert.Equal(text.ToLowerInvariant(), text);
            }

            Assert.Equal(
                ["ok", "caution", "check", "unavailable", "info"],
                entry.GetProperty("counts").EnumerateObject().Select(p => p.Name).ToArray());
        }
    }

    [Fact]
    public void The_overall_verdict_in_the_entry_has_four_values_and_no_info()
    {
        var verdicts = Enum.GetValues<CheckVerdict>().Select(v => SnakeCaseEnum<CheckVerdict>.Name(v)).ToArray();
        Assert.Equal(["ok", "caution", "check", "unavailable"], verdicts);
        Assert.DoesNotContain("info", verdicts);

        // The item verdict has the fifth value.
        Assert.Equal(
            ["ok", "caution", "check", "unavailable", "info"],
            Enum.GetValues<ItemVerdict>().Select(v => SnakeCaseEnum<ItemVerdict>.Name(v)).ToArray());
    }

    [Fact]
    public void The_four_warning_codes_are_the_only_codes()
    {
        Assert.Equal(
            ["history_unreadable", "history_future_format", "history_quarantined", "history_quarantine_failed"],
            CheckHistoryWarningCodes.All.ToArray());

        foreach (var code in CheckHistoryWarningCodes.All)
        {
            Assert.True(CheckHistoryWarningCodes.IsKnown(code));
        }

        Assert.False(CheckHistoryWarningCodes.IsKnown("history_other"));
        Assert.False(CheckHistoryWarningCodes.IsKnown(null));
        Assert.False(CheckHistoryWarningCodes.IsKnown("HISTORY_UNREADABLE"));
    }

    [Fact]
    public void The_query_has_a_kind_and_a_limit_and_both_may_be_absent()
    {
        var all = new CheckHistoryQuery(null, null);
        Assert.Null(all.Kind);
        Assert.Null(all.Limit);

        var some = new CheckHistoryQuery(CheckKind.Storage, 20);
        Assert.Equal(CheckKind.Storage, some.Kind);
        Assert.Equal(20, some.Limit);
    }


    // ---- reading: what is refused, what is skipped (DESIGN-A §3 "読み手の寛容さ", §3.5-1, §3.5-11) ----

    private static string Entry(
        string kind = "memory",
        string runId = "89914ef9-5781-4cf7-a4f8-6d58003ddf86",
        string started = "2026-11-20T03:00:00.000Z",
        string outcome = "stopped",
        string verdict = "check",
        string counts = "{\"ok\":0,\"caution\":0,\"check\":1,\"unavailable\":0,\"info\":8}",
        string extra = "") =>
        "{\"runId\":\"" + runId + "\",\"kind\":\"" + kind + "\",\"startedAtUtc\":\"" + started + "\",\"endedAtUtc\":\"2026-11-20T03:00:04.200Z\",\"outcome\":\"" + outcome + "\",\"verdict\":\"" + verdict + "\",\"counts\":" + counts + extra + "}";

    private static string Page(string entries, string warnings = "") => "{\"entries\":[" + entries + "],\"warnings\":[" + warnings + "]}";

    [Theory]
    [InlineData("{\"entries\":[{\"runId\":\"89914ef9-5781-4cf7-a4f8-6d58003ddf86\",\"kind\":\"memory\",\"startedAtUtc\":\"2026-11-20T03:00:00Z\",\"endedAtUtc\":\"2026-11-20T03:00:04.200Z\",\"outcome\":\"stopped\",\"verdict\":\"check\",\"counts\":{\"ok\":0,\"caution\":0,\"check\":1,\"unavailable\":0,\"info\":8}}],\"warnings\":[]}")]
    [InlineData("{\"entries\":[{\"runId\":\"89914ef9-5781-4cf7-a4f8-6d58003ddf86\",\"kind\":\"Memory\",\"startedAtUtc\":\"2026-11-20T03:00:00.000Z\",\"endedAtUtc\":\"2026-11-20T03:00:04.200Z\",\"outcome\":\"stopped\",\"verdict\":\"check\",\"counts\":{\"ok\":0,\"caution\":0,\"check\":1,\"unavailable\":0,\"info\":8}}],\"warnings\":[]}")]
    [InlineData("{\"entries\":[{\"runId\":\"89914ef9-5781-4cf7-a4f8-6d58003ddf86\",\"kind\":\"memory\",\"startedAtUtc\":\"2026-11-20T03:00:00.000Z\",\"endedAtUtc\":\"2026-11-20T03:00:04.200Z\",\"outcome\":\"stopped\",\"verdict\":\"info\",\"counts\":{\"ok\":0,\"caution\":0,\"check\":1,\"unavailable\":0,\"info\":8}}],\"warnings\":[]}")]
    [InlineData("{\"entries\":[],")]
    [InlineData("[]")]
    [InlineData("null")]
    public void A_broken_page_is_refused_not_guessed_at(string json) =>
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(json));

    [Fact]
    public void An_entry_of_a_kind_this_version_does_not_know_is_skipped_and_counted_not_fatal()
    {
        // "gpu" is what a later version may add. One such row must not take the whole page down (§3 "読み手の寛容さ").
        var page = CheckJson.DeserializeHistory(Page(Entry(kind: "gpu") + "," + Entry() + "," + Entry(kind: "network")));

        var entry = Assert.Single(page.Entries);
        Assert.Equal(CheckKind.Memory, entry.Kind);
        var warning = Assert.Single(page.Warnings);
        Assert.Equal(CheckHistoryWarningCodes.FutureFormat, warning.Code);
        Assert.Equal(2, warning.Count);
    }

    [Fact]
    public void The_skipped_future_rows_are_added_to_a_future_format_warning_that_is_already_there()
    {
        var page = CheckJson.DeserializeHistory(
            Page(Entry(kind: "gpu"), "{\"code\":\"history_future_format\",\"count\":3},{\"code\":\"history_unreadable\",\"count\":1}"));

        Assert.Empty(page.Entries);
        Assert.Equal(
            [new CheckHistoryWarning(CheckHistoryWarningCodes.FutureFormat, 4), new CheckHistoryWarning(CheckHistoryWarningCodes.Unreadable, 1)],
            page.Warnings);
    }

    [Fact]
    public void A_page_with_only_known_rows_gets_no_extra_warning()
    {
        var page = CheckJson.DeserializeHistory(Page(Entry()));
        Assert.Single(page.Entries);
        Assert.Empty(page.Warnings);
    }

    [Fact]
    public void An_unknown_key_in_a_page_an_entry_a_counts_object_or_a_warning_is_ignored()
    {
        var counts = "{\"ok\":0,\"caution\":0,\"check\":1,\"unavailable\":0,\"info\":8,\"later\":1}";
        var json = "{\"entries\":[" + Entry(counts: counts, extra: ",\"title\":\"x\"") + "],\"warnings\":[{\"code\":\"history_quarantined\",\"count\":2,\"hint\":\"y\"}],\"next\":null}";

        var page = CheckJson.DeserializeHistory(json);

        var entry = Assert.Single(page.Entries);
        Assert.Equal(new CheckCounts(0, 0, 1, 0, 8), entry.Counts);
        Assert.Equal([new CheckHistoryWarning(CheckHistoryWarningCodes.Quarantined, 2)], page.Warnings);
    }

    [Theory]
    [InlineData("runId")]
    [InlineData("kind")]
    [InlineData("startedAtUtc")]
    [InlineData("endedAtUtc")]
    [InlineData("outcome")]
    [InlineData("verdict")]
    [InlineData("counts")]
    public void A_missing_required_field_makes_the_row_unreadable_nothing_is_defaulted(string field)
    {
        using var doc = JsonDocument.Parse(Entry());
        var kept = string.Join(",", doc.RootElement.EnumerateObject().Where(p => p.Name != field).Select(p => "\"" + p.Name + "\":" + p.Value.GetRawText()));
        var ex = Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page("{" + kept + "}")));
        Assert.Contains("missing key '" + field + "'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("caution")]
    [InlineData("check")]
    [InlineData("unavailable")]
    [InlineData("info")]
    public void A_missing_count_is_not_read_as_zero(string count)
    {
        var all = new[] { "ok", "caution", "check", "unavailable", "info" };
        var counts = "{" + string.Join(",", all.Where(n => n != count).Select(n => "\"" + n + "\":0")) + "}";
        var ex = Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page(Entry(counts: counts))));
        Assert.Contains("missing key '" + count + "'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"entries\":[]}")]
    [InlineData("{\"warnings\":[]}")]
    [InlineData("{}")]
    public void A_page_without_entries_or_warnings_is_refused(string json) =>
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(json));

    [Fact]
    public void A_new_verdict_outcome_or_a_wrong_type_still_fails_because_those_mean_pcm_check_2()
    {
        // "unavailable" is the overall verdict; "stale" and "partial_info" are not values of version 1.
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page(Entry(verdict: "stale"))));
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page(Entry(outcome: "paused"))));
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page(Entry(counts: "{\"ok\":\"0\",\"caution\":0,\"check\":1,\"unavailable\":0,\"info\":8}"))));
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page(Entry(counts: "{\"ok\":0,\"caution\":0,\"check\":-1,\"unavailable\":0,\"info\":8}"))));
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page(Entry(counts: "{\"ok\":0.0,\"caution\":0,\"check\":1,\"unavailable\":0,\"info\":8}"))));
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page(Entry(runId: "00000000-0000-0000-0000-000000000000"))));
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page(Entry(runId: "89914EF9-5781-4CF7-A4F8-6D58003DDF86"))));
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page(Entry(started: "2026-11-20 03:00:00"))));
    }

    [Fact]
    public void A_row_of_an_unknown_kind_is_still_checked_for_the_rest_and_a_malformed_kind_is_not_a_future_one()
    {
        // Unknown kind + a broken field: the row is unreadable (it is not silently skipped).
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page(Entry(kind: "gpu", verdict: "stale"))));
        // A kind that is not even a lowercase name is a broken row, not a future one.
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page(Entry(kind: "GPU"))));
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page(Entry(kind: ""))));
    }

    [Fact]
    public void An_unknown_warning_code_is_refused_and_a_negative_count_too()
    {
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page("", "{\"code\":\"history_other\",\"count\":1}")));
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page("", "{\"code\":\"history_unreadable\",\"count\":-1}")));
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(Page("", "{\"code\":\"history_unreadable\"}")));
    }
}
