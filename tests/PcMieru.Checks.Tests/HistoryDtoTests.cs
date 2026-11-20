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

    [Theory]
    [InlineData("{\"entries\":[{\"runId\":\"89914ef9-5781-4cf7-a4f8-6d58003ddf86\",\"kind\":\"memory\",\"startedAtUtc\":\"2026-11-20T03:00:00Z\",\"endedAtUtc\":\"2026-11-20T03:00:04.200Z\",\"outcome\":\"stopped\",\"verdict\":\"check\",\"counts\":{\"ok\":0,\"caution\":0,\"check\":1,\"unavailable\":0,\"info\":8}}],\"warnings\":[]}")]
    [InlineData("{\"entries\":[{\"runId\":\"89914ef9-5781-4cf7-a4f8-6d58003ddf86\",\"kind\":\"Memory\",\"startedAtUtc\":\"2026-11-20T03:00:00.000Z\",\"endedAtUtc\":\"2026-11-20T03:00:04.200Z\",\"outcome\":\"stopped\",\"verdict\":\"check\",\"counts\":{\"ok\":0,\"caution\":0,\"check\":1,\"unavailable\":0,\"info\":8}}],\"warnings\":[]}")]
    [InlineData("{\"entries\":[{\"runId\":\"89914ef9-5781-4cf7-a4f8-6d58003ddf86\",\"kind\":\"memory\",\"startedAtUtc\":\"2026-11-20T03:00:00.000Z\",\"endedAtUtc\":\"2026-11-20T03:00:04.200Z\",\"outcome\":\"stopped\",\"verdict\":\"info\",\"counts\":{\"ok\":0,\"caution\":0,\"check\":1,\"unavailable\":0,\"info\":8}}],\"warnings\":[]}")]
    [InlineData("{\"entries\":[{\"runId\":\"89914ef9-5781-4cf7-a4f8-6d58003ddf86\",\"kind\":\"gpu\",\"startedAtUtc\":\"2026-11-20T03:00:00.000Z\",\"endedAtUtc\":\"2026-11-20T03:00:04.200Z\",\"outcome\":\"stopped\",\"verdict\":\"check\",\"counts\":{\"ok\":0,\"caution\":0,\"check\":1,\"unavailable\":0,\"info\":8}}],\"warnings\":[]}")]
    [InlineData("{\"entries\":[],")]
    [InlineData("[]")]
    [InlineData("null")]
    public void A_broken_page_is_refused_not_guessed_at(string json) =>
        Assert.Throws<CheckFormatException>(() => CheckJson.DeserializeHistory(json));
}
