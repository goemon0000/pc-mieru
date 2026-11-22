using System.Text.Json;
using PcMieru.Checks;

namespace PcMieru.Cli.Tests;

/// <summary>
/// <c>pcmi check history</c> (DESIGN-A 8): every kept run, newest first, as one <c>{ entries, warnings }</c> document or as tables. A history
/// that is not there is an empty one (exit 0, nothing made); a file that cannot be used is a warning with a count (exit 0); a folder that
/// cannot be opened is exit 1. A damaged file is moved aside here (this command is the one place that does it: the history of the app and
/// of the MCP server never moves anything); nothing else is written.
/// </summary>
public class CheckHistoryCommandTests
{
    private static readonly string[] EntryProperties = ["runId", "kind", "startedAtUtc", "endedAtUtc", "outcome", "verdict", "counts"];

    private static string HistoryFileName(CheckKind kind, int n)
    {
        var end = CheckSamples.Start.AddMinutes(n).AddMilliseconds(1250);
        return kind.ToString().ToLowerInvariant() + "-" + end.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture) + "-" + CheckSamples.Id(n).ToString("N") + ".json";
    }

    private static void WriteBroken(TempFolder data, int n)
    {
        Directory.CreateDirectory(data.Checks);
        File.WriteAllText(Path.Combine(data.Checks, HistoryFileName(CheckKind.System, n)), "{ this is not json");
    }

    private static void AssertInOrder(string text, params string[] parts)
    {
        var from = 0;
        foreach (var part in parts)
        {
            var at = text.IndexOf(part, from, StringComparison.Ordinal);
            Assert.True(at >= 0, $"'{part}' not found (in order) in:\n{text}");
            from = at + part.Length;
        }
    }

    // ---- nothing there -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_history_folder_that_is_not_there_is_an_empty_history_exit_0_and_nothing_is_made()
    {
        using var data = new TempFolder();
        var runner = FakeCheckRunner.Returning(CheckSamples.SystemOk());

        var json = await CliRun.CheckAsync(["check", "history", "--json"], data, runner);
        var human = await CliRun.CheckAsync(["check", "history"], data, runner);

        Assert.Equal(0, json.ExitCode);
        Assert.Equal("{\"entries\":[],\"warnings\":[]}\n", json.Out);
        Assert.Equal("", json.Err);
        Assert.Equal(0, human.ExitCode);
        Assert.Equal(CliWords.En("cli.check.hist.empty") + "\n", human.Out);
        Assert.Equal("", human.Err);
        Assert.False(Directory.Exists(data.Checks));
        Assert.False(Directory.Exists(data.Path));
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task An_empty_folder_is_the_same_empty_history()
    {
        using var data = new TempFolder(create: true);
        Directory.CreateDirectory(data.Checks);

        var json = await CliRun.CheckAsync(["check", "history", "--json"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        Assert.Equal(0, json.ExitCode);
        Assert.Equal("{\"entries\":[],\"warnings\":[]}\n", json.Out);
    }

    [Fact]
    public async Task The_empty_history_in_japanese()
    {
        using var data = new TempFolder();

        var r = await CliRun.CheckAsync(["check", "history"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()), language: Language.Ja);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("確認の履歴はまだありません\n", r.Out);
    }

    // ---- the list ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_run_is_kept_newest_first_and_the_rows_have_exactly_the_fields_of_the_spec()
    {
        using var data = new TempFolder();
        var results = Enumerable.Range(1, 25)
            .Select(n => n % 2 == 0 ? CheckSamples.SystemOk(n) : CheckSamples.StorageRead(n))
            .ToArray();
        // Saved in a scrambled order (7 and 25 have no common divisor): the listing must sort, not echo.
        await CheckSamples.SeedAsync(data.Checks, Enumerable.Range(0, 25).Select(i => results[(i * 7) % 25]).ToArray());

        var r = await CliRun.CheckAsync(["check", "history", "--json"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("", r.Err);
        Assert.Single(r.OutLines);
        var root = JsonDocument.Parse(r.Out).RootElement;
        Assert.Equal(["entries", "warnings"], root.EnumerateObject().Select(p => p.Name).ToArray());
        var entries = root.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(25, entries.Length);
        Assert.Equal(
            Enumerable.Range(1, 25).Reverse().Select(n => CheckSamples.Id(n).ToString("D")).ToArray(),
            entries.Select(e => e.GetProperty("runId").GetString()).ToArray());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());

        var first = entries[0];
        Assert.Equal(EntryProperties.Order().ToArray(), first.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("storage", first.GetProperty("kind").GetString());
        Assert.Equal("system", entries[1].GetProperty("kind").GetString());
        Assert.Equal("completed", first.GetProperty("outcome").GetString());
        Assert.Equal("ok", first.GetProperty("verdict").GetString());
        Assert.Equal(["ok", "caution", "check", "unavailable", "info"], first.GetProperty("counts").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(2, first.GetProperty("counts").GetProperty("ok").GetInt32());
        Assert.Equal(1, first.GetProperty("counts").GetProperty("info").GetInt32());
        Assert.Equal(CheckSamples.Start.AddMinutes(25).UtcDateTime, first.GetProperty("startedAtUtc").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(CheckSamples.Start.AddMinutes(25).AddMilliseconds(1250).UtcDateTime, first.GetProperty("endedAtUtc").GetDateTimeOffset().UtcDateTime);
    }

    [Fact]
    public async Task Stdout_is_what_the_library_serializer_makes_of_the_page_the_library_lists()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemCaution(1), CheckSamples.StorageDenied(2), CheckSamples.SystemStopped(3), CheckSamples.Failed(CheckKind.Storage, 4));
        var page = await CheckHistory.OpenReadOnly(data.Checks).ListAsync(new CheckHistoryQuery(Kind: null, Limit: null), CancellationToken.None);

        var r = await CliRun.CheckAsync(["check", "history", "--json"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        Assert.Equal(CheckJson.SerializeHistory(page) + "\n", r.Out);
        Assert.Equal(4, page.Entries.Count);
        Assert.Equal(
            [CheckOutcome.Failed, CheckOutcome.Stopped, CheckOutcome.Completed, CheckOutcome.Completed],
            page.Entries.Select(e => e.Outcome).ToArray());
    }

    [Fact]
    public async Task The_human_table_in_english_has_a_row_per_run_newest_first()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemCaution(1), CheckSamples.StorageRead(2), CheckSamples.SystemStopped(3));

        var r = await CliRun.CheckAsync(["check", "history"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("", r.Err);
        var lines = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        AssertInOrder(lines[0], "Started (UTC)", "Kind", "Outcome", "Verdict", "Counts", "Run ID");
        AssertInOrder(lines[1], "2026-11-20 03:03:00", "System", "Stopped", "OK", "OK 1", CheckSamples.Id(3).ToString("D"));
        AssertInOrder(lines[2], "2026-11-20 03:02:00", "Storage", "Completed", "OK", "OK 2 Info 1", CheckSamples.Id(2).ToString("D"));
        AssertInOrder(lines[3], "2026-11-20 03:01:00", "System", "Completed", "Caution", "OK 1 Caution 1 Info 1", CheckSamples.Id(1).ToString("D"));
    }

    [Fact]
    public async Task The_human_table_in_japanese_uses_the_japanese_words()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemCaution(1), CheckSamples.StorageRead(2));

        var r = await CliRun.CheckAsync(["check", "history"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()), language: Language.Ja);

        Assert.Equal(0, r.ExitCode);
        var lines = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        AssertInOrder(lines[0], "開始（UTC）", "種類", "経過", "判定", "件数", "実行 ID");
        AssertInOrder(lines[1], "2026-11-20 03:02:00", "ストレージ", "完了", "OK", "OK 2 参考 1", CheckSamples.Id(2).ToString("D"));
        AssertInOrder(lines[2], "2026-11-20 03:01:00", "システム", "完了", "注意", "OK 1 注意 1 参考 1", CheckSamples.Id(1).ToString("D"));
    }

    [Fact]
    public async Task A_run_with_no_rows_has_a_dash_for_its_counts_and_the_verdict_word_of_a_failed_run()
    {
        using var data = new TempFolder();
        var failed = CheckSamples.Failed(CheckKind.System, 1);
        await CheckSamples.SeedAsync(data.Checks, failed);

        var r = await CliRun.CheckAsync(["check", "history"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        var row = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1];
        AssertInOrder(row, "System", "Failed", CliWords.En("cli.check.verdict." + failed.Verdict.ToString().ToLowerInvariant()), " - ", CheckSamples.Id(1).ToString("D"));
    }

    [Fact]
    public async Task Listing_healthy_files_does_not_save_move_or_make_anything()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1), CheckSamples.StorageRead(2));
        string[] Snapshot() => Directory.GetFileSystemEntries(data.Path, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(data.Path, p) + "|" + (File.Exists(p) ? new FileInfo(p).Length : -1) + "|" + File.GetLastWriteTimeUtc(p).Ticks)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var before = Snapshot();

        var r = await CliRun.CheckAsync(["check", "history"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(before, Snapshot());
    }

    // ---- warnings keep exit 0 ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_that_is_not_json_is_a_warning_with_a_count_and_the_other_runs_are_listed_exit_0()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1));
        WriteBroken(data, 90);
        WriteBroken(data, 91);

        var json = await CliRun.CheckAsync(["check", "history", "--json"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        Assert.Equal(0, json.ExitCode);
        Assert.Equal("", json.Err);
        var root = JsonDocument.Parse(json.Out).RootElement;
        Assert.Single(root.GetProperty("entries").EnumerateArray());

        // Each broken file is counted as unreadable and, as it is moved aside, as quarantined (the store's own counting).
        var counts = WarningCounts(root);
        Assert.Equal(2, counts["history_unreadable"]);
        Assert.Equal(2, counts["history_quarantined"]);
        Assert.Equal(2, counts.Count);

        // The human table of the same situation (a folder of its own: the first listing moved those files aside).
        using var other = new TempFolder();
        await CheckSamples.SeedAsync(other.Checks, CheckSamples.SystemOk(1));
        WriteBroken(other, 90);
        WriteBroken(other, 91);

        var human = await CliRun.CheckAsync(["check", "history"], other, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        Assert.Equal(0, human.ExitCode);
        Assert.Equal("", human.Err);
        AssertInOrder(human.Out, CheckSamples.Id(1).ToString("D"), "\n\n", CliWords.En("cli.check.hist.warnings"), "Code", "Files", "Meaning", "history_unreadable", "2", CliWords.En("cli.check.warn.history_unreadable"));
    }

    [Fact]
    public async Task A_history_with_only_warnings_says_it_is_empty_and_then_lists_them()
    {
        using var data = new TempFolder();
        WriteBroken(data, 90);

        var r = await CliRun.CheckAsync(["check", "history"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        Assert.Equal(0, r.ExitCode);
        Assert.StartsWith(CliWords.En("cli.check.hist.empty") + "\n\n" + CliWords.En("cli.check.hist.warnings") + "\n", r.Out, StringComparison.Ordinal);
        Assert.Contains("history_unreadable", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_warning_table_in_japanese()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1));
        WriteBroken(data, 90);

        var r = await CliRun.CheckAsync(["check", "history"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()), language: Language.Ja);

        Assert.Equal(0, r.ExitCode);
        AssertInOrder(r.Out, "読めなかった履歴ファイル", "コード", "件数", "意味", "history_unreadable", "1", CliWords.Ja("cli.check.warn.history_unreadable"));
    }

    [Fact]
    public async Task A_file_a_newer_version_wrote_is_a_future_format_warning_exit_0()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1));
        var newer = CheckSamples.SystemOk(5);
        var text = CheckJson.Serialize(newer);
        Assert.Contains("\"format\":\"pcm-check/1\"", text, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(data.Checks, CheckSamples.FileName(newer)), text.Replace("\"format\":\"pcm-check/1\"", "\"format\":\"pcm-check/2\"", StringComparison.Ordinal));

        var r = await CliRun.CheckAsync(["check", "history", "--json"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        Assert.Equal(0, r.ExitCode);
        var root = JsonDocument.Parse(r.Out).RootElement;
        Assert.Single(root.GetProperty("entries").EnumerateArray());
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal("history_future_format", warning.GetProperty("code").GetString());
        Assert.Equal(1, warning.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_file_another_process_holds_is_a_locked_warning_exit_0()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1), CheckSamples.SystemOk(2));
        var held = Directory.GetFiles(data.Checks, "*.json").Order(StringComparer.Ordinal).First();

        CliResult r;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            r = await CliRun.CheckAsync(["check", "history", "--json"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));
        }

        Assert.Equal(0, r.ExitCode);
        var root = JsonDocument.Parse(r.Out).RootElement;
        Assert.Single(root.GetProperty("entries").EnumerateArray());
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal("history_locked", warning.GetProperty("code").GetString());
        Assert.Equal(1, warning.GetProperty("count").GetInt32());
        Assert.Empty(Directory.GetFiles(data.Checks, "*.bad-*"));
        Assert.True(File.Exists(held));
    }

    // ---- the one place a damaged file is moved aside (DESIGN-A 4, 8) -------------------------------------------------------------------

    private static string[] BadCopies(TempFolder data, CheckKind kind, int n) =>
        Directory.GetFiles(data.Checks, HistoryFileName(kind, n) + ".bad-*");

    private static Dictionary<string, int> WarningCounts(JsonElement root) =>
        root.GetProperty("warnings").EnumerateArray().ToDictionary(w => w.GetProperty("code").GetString()!, w => w.GetProperty("count").GetInt32());

    [Fact]
    public async Task A_broken_file_is_moved_aside_and_reported_as_quarantined_exit_0_and_the_next_listing_is_clean()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1));
        WriteBroken(data, 90);
        var broken = Path.Combine(data.Checks, HistoryFileName(CheckKind.System, 90));

        var first = await CliRun.CheckAsync(["check", "history", "--json"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        Assert.Equal(0, first.ExitCode);
        Assert.Equal("", first.Err);
        var root = JsonDocument.Parse(first.Out).RootElement;
        Assert.Single(root.GetProperty("entries").EnumerateArray());
        var counts = WarningCounts(root);
        Assert.Equal(1, counts["history_quarantined"]);
        Assert.Equal(1, counts["history_unreadable"]);
        Assert.DoesNotContain("history_quarantine_failed", counts.Keys);
        Assert.False(File.Exists(broken), "the original is gone");
        var moved = Assert.Single(BadCopies(data, CheckKind.System, 90));
        Assert.Equal("{ this is not json", File.ReadAllText(moved));
        Assert.True(File.Exists(Path.Combine(data.Checks, CheckSamples.FileName(CheckSamples.SystemOk(1)))), "a valid file is not touched");

        var second = await CliRun.CheckAsync(["check", "history", "--json"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        Assert.Equal(0, second.ExitCode);
        Assert.Empty(JsonDocument.Parse(second.Out).RootElement.GetProperty("warnings").EnumerateArray());
        Assert.Single(BadCopies(data, CheckKind.System, 90));
    }

    [Fact]
    public async Task The_human_listing_says_the_broken_file_was_set_aside()
    {
        using var data = new TempFolder();
        WriteBroken(data, 90);

        var en = await CliRun.CheckAsync(["check", "history"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        Assert.Equal(0, en.ExitCode);
        AssertInOrder(en.Out, CliWords.En("cli.check.hist.warnings"), "history_quarantined", "1", CliWords.En("cli.check.warn.history_quarantined"));
        Assert.Single(BadCopies(data, CheckKind.System, 90));

        using var other = new TempFolder();
        WriteBroken(other, 90);

        var ja = await CliRun.CheckAsync(["check", "history"], other, FakeCheckRunner.Returning(CheckSamples.SystemOk()), language: Language.Ja);

        Assert.Equal(0, ja.ExitCode);
        AssertInOrder(ja.Out, "history_quarantined", "1", CliWords.Ja("cli.check.warn.history_quarantined"));
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_because_another_process_holds_it_is_never_moved_aside()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1));
        WriteBroken(data, 90);
        var broken = Path.Combine(data.Checks, HistoryFileName(CheckKind.System, 90));
        var valid = Path.Combine(data.Checks, CheckSamples.FileName(CheckSamples.SystemOk(1)));
        var validBytes = File.ReadAllBytes(valid);

        CliResult r;
        using (new FileStream(broken, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            r = await CliRun.CheckAsync(["check", "history", "--json"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));
        }

        Assert.Equal(0, r.ExitCode);
        var root = JsonDocument.Parse(r.Out).RootElement;
        Assert.Single(root.GetProperty("entries").EnumerateArray());
        var counts = WarningCounts(root);
        Assert.Equal(1, counts["history_locked"]);
        Assert.DoesNotContain("history_quarantined", counts.Keys);
        Assert.DoesNotContain("history_unreadable", counts.Keys);
        Assert.True(File.Exists(broken), "a file that could not be read is left exactly where it is");
        Assert.Empty(BadCopies(data, CheckKind.System, 90));
        Assert.Equal("{ this is not json", File.ReadAllText(broken));
        Assert.Equal(validBytes, File.ReadAllBytes(valid));
    }

    [Fact]
    public async Task A_file_a_newer_version_wrote_is_left_where_it_is()
    {
        using var data = new TempFolder();
        var newer = CheckSamples.SystemOk(5);
        Directory.CreateDirectory(data.Checks);
        var path = Path.Combine(data.Checks, CheckSamples.FileName(newer));
        var text = CheckJson.Serialize(newer).Replace("\"format\":\"pcm-check/1\"", "\"format\":\"pcm-check/2\"", StringComparison.Ordinal);
        File.WriteAllText(path, text);

        var r = await CliRun.CheckAsync(["check", "history", "--json"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(1, WarningCounts(JsonDocument.Parse(r.Out).RootElement)["history_future_format"]);
        Assert.Equal(text, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(data.Checks));
    }

    [Fact]
    public async Task The_listing_holds_the_shutdown_from_the_scan_to_the_last_byte_so_that_ctrl_c_cannot_cut_a_rename_short()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1));
        var output = new HoldProbeWriter { NewLine = "\n" };
        var env = new CliEnvironment(
            new StringReader(string.Empty),
            output,
            new StringWriter { NewLine = "\n" },
            o => CliRun.Services(o),
            Checks: new CliChecks(data.Path, LegacyDirectory: null));
        Assert.False(CliShutdown.IsHeld);

        var code = await CliApp.RunAsync(["check", "history", "--json"], env, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.NotEmpty(output.HeldAtWrite);
        Assert.All(output.HeldAtWrite, held => Assert.True(held, "the output is written, and flushed, while the shutdown is held"));
        Assert.False(CliShutdown.IsHeld, "and let go when the command is over");
    }

    private sealed class HoldProbeWriter : StringWriter
    {
        public List<bool> HeldAtWrite { get; } = [];

        public override void Write(string? value)
        {
            HeldAtWrite.Add(CliShutdown.IsHeld);
            base.Write(value);
        }

        public override Task FlushAsync()
        {
            HeldAtWrite.Add(CliShutdown.IsHeld);
            return base.FlushAsync();
        }
    }

    // ---- the folder cannot be opened: exit 1 --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_where_the_history_folder_should_be_is_exit_1_with_nothing_on_stdout()
    {
        using var data = new TempFolder(create: true);
        File.WriteAllText(data.Checks, "this is a file, not a folder");

        var json = await CliRun.CheckAsync(["check", "history", "--json"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));
        var human = await CliRun.CheckAsync(["check", "history"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()));

        Assert.Equal(1, json.ExitCode);
        Assert.Equal("", json.Out);
        Assert.StartsWith("pcmi check: " + CliWords.En("cli.check.hist.folderFail") + " [", json.Err, StringComparison.Ordinal);
        Assert.Equal(1, human.ExitCode);
        Assert.Equal("", human.Out);
        Assert.StartsWith("pcmi check: " + CliWords.En("cli.check.hist.folderFail") + " [", human.Err, StringComparison.Ordinal);
        Assert.True(File.Exists(data.Checks), "the file is left as it was");
    }

    [Fact]
    public async Task A_stop_before_listing_is_exit_1()
    {
        using var data = new TempFolder();
        await CheckSamples.SeedAsync(data.Checks, CheckSamples.SystemOk(1));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var r = await CliRun.CheckAsync(["check", "history"], data, FakeCheckRunner.Returning(CheckSamples.SystemOk()), ct: cts.Token);

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("", r.Out);
    }
}
