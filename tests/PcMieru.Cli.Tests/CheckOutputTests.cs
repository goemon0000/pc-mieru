using System.Text.RegularExpressions;
using PcMieru.Checks;
using PcMieru.Cli.Commands;

namespace PcMieru.Cli.Tests;

/// <summary>The human table of one check result, in Japanese and in English: the five verdict words, kinds, outcomes, reasons, value shapes and notes.</summary>
public class CheckOutputTests
{
    private static string Line(string text, string startsWith)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith(startsWith, StringComparison.Ordinal));
        Assert.True(line is not null, $"no line starting with '{startsWith}' in:\n{text}");
        return line;
    }

    private static string RowOf(string text, string id)
    {
        var rows = text.Split('\n').Where(l => l.StartsWith(id + " ", StringComparison.Ordinal)).ToArray();
        Assert.True(rows.Length == 1, $"expected one row for {id}, found {rows.Length} in:\n{text}");
        return rows[0];
    }

    /// <summary>The cells of a table row: split where two or more spaces are (the table pads with two).</summary>
    private static string[] Cells(string row) => Regex.Split(row.Trim(), @"\s{2,}");

    private static CliContext Context(Language language)
    {
        _ = CliApp.Commands;
        Strings.Current = language;
        return new CliContext(new CliEnvironment(TextReader.Null, new StringWriter(), new StringWriter(), CliEnvironment.DefaultFactory), language, CancellationToken.None);
    }

    private static string VerdictEn(CheckVerdict verdict) => CliWords.En("cli.check.verdict." + verdict.ToString().ToLowerInvariant());

    // ---- English -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_english_table_has_a_title_the_facts_one_row_per_item_and_the_notes()
    {
        using var data = new TempFolder();
        var result = CheckSamples.WithNoteAndValues(1);

        var r = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(result));

        Assert.Equal(0, r.ExitCode);
        Assert.StartsWith($"System check: {VerdictEn(result.Verdict)}\n\n", r.Out, StringComparison.Ordinal);
        Assert.Matches(@"^Run ID\s+" + CheckSamples.Id(1).ToString("D") + "$", Line(r.Out, "Run ID"));
        Assert.Matches(@"^Started \(UTC\)\s+2026-11-20 03:01:00$", Line(r.Out, "Started (UTC)"));
        Assert.Matches(@"^Ended \(UTC\)\s+2026-11-20 03:01:01$", Line(r.Out, "Ended (UTC)"));
        Assert.Matches(@"^Outcome\s+Completed$", Line(r.Out, "Outcome"));
        Assert.Matches(@"^Counts\s+OK 2 / Caution 0 / Needs review 0 / Not measured 1 / Info 3$", Line(r.Out, "Counts"));
        Assert.Matches(@"^Saved\s+" + Regex.Escape(CheckSamples.FileName(result)) + "$", Line(r.Out, "Saved"));

        Assert.Equal(["Item", "Subject", "Value", "Verdict", "Reason"], Cells(Line(r.Out, "Item")));
        Assert.Equal(["system.os.version", "-", "Windows 11 Pro 25H2 (26300.7171)", "Info", "-"], Cells(RowOf(r.Out, "system.os.version")));
        Assert.Equal(["system.os.uptime", "-", "273600.5 s", "OK", "-"], Cells(RowOf(r.Out, "system.os.uptime")));
        Assert.Equal(["system.cpu.cores", "-", "8", "Info", "-"], Cells(RowOf(r.Out, "system.cpu.cores")));
        Assert.Equal(["system.memory.capacity", "ram-1", "16.0 GB", "Info", "-"], Cells(RowOf(r.Out, "system.memory.capacity")));
        Assert.Equal(["system.gpu.generic_driver", "gpu-1 (NVIDIA GeForce RTX 5070)", "no", "OK", "-"], Cells(RowOf(r.Out, "system.gpu.generic_driver")));
        Assert.Equal(["system.eventlog.volmgr7d", "-", "-", "Not measured", "Denied"], Cells(RowOf(r.Out, "system.eventlog.volmgr7d")));
        Assert.EndsWith("\nNote: The CPU temperature was not measured\n", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_shows_the_evidence_code_and_the_model_of_its_subject()
    {
        using var data = new TempFolder();
        var result = CheckSamples.SystemCaution(1, "Kingston Fury 4800");

        var r = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(result));

        Assert.StartsWith("System check: Caution\n\n", r.Out, StringComparison.Ordinal);
        Assert.Equal(
            ["system.memory.configured_speed", "ram-1 (Kingston Fury 4800)", "4800 MT/s", "Caution", "[below_rated]"],
            Cells(RowOf(r.Out, "system.memory.configured_speed")));
    }

    [Fact]
    public async Task A_value_that_is_missing_is_a_dash_never_a_zero_and_its_reason_is_in_words()
    {
        using var data = new TempFolder();

        var r = await CliRun.CheckAsync(["check", "storage"], data, FakeCheckRunner.Returning(CheckSamples.StorageDenied()));

        Assert.Equal(
            ["storage.temperature", "disk-1 (Samsung SSD 980 PRO 2TB)", "-", "Not measured", "Denied"],
            Cells(RowOf(r.Out, "storage.temperature")));
    }

    [Fact]
    public async Task A_run_with_no_rows_has_no_table_and_no_notes()
    {
        using var data = new TempFolder();
        var failed = CheckSamples.Failed();
        var run = new CheckRun(failed, CheckSaveState.Saved(CheckSamples.FileName(failed)), FailureCode: "internal_error");

        var r = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(run));

        Assert.DoesNotContain("Item", r.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Note:", r.Out, StringComparison.Ordinal);
        Assert.Matches(@"^Outcome\s+Failed$", Line(r.Out, "Outcome"));
    }

    // ---- Japanese ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_japanese_table_uses_the_japanese_words_for_everything()
    {
        using var data = new TempFolder();
        var result = CheckSamples.WithNoteAndValues(1);

        var r = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(result), language: Language.Ja);

        Assert.Equal(0, r.ExitCode);
        var verdict = CliWords.Ja("cli.check.verdict." + result.Verdict.ToString().ToLowerInvariant());
        Assert.StartsWith($"システムの確認: {verdict}\n\n", r.Out, StringComparison.Ordinal);
        Assert.Matches(@"^実行 ID\s+" + CheckSamples.Id(1).ToString("D") + "$", Line(r.Out, "実行 ID"));
        Assert.Matches(@"^開始（UTC）\s+2026-11-20 03:01:00$", Line(r.Out, "開始（UTC）"));
        Assert.Matches(@"^終了（UTC）\s+2026-11-20 03:01:01$", Line(r.Out, "終了（UTC）"));
        Assert.Matches(@"^経過\s+完了$", Line(r.Out, "経過"));
        Assert.Matches(@"^件数\s+OK 2 / 注意 0 / 要確認 0 / 測れず 1 / 参考 3$", Line(r.Out, "件数"));
        Assert.Matches(@"^保存\s+" + Regex.Escape(CheckSamples.FileName(result)) + "$", Line(r.Out, "保存"));
        Assert.Equal(["項目", "対象", "値", "判定", "理由"], Cells(Line(r.Out, "項目")));
        Assert.Equal(["system.os.uptime", "-", "273600.5 s", "OK", "-"], Cells(RowOf(r.Out, "system.os.uptime")));
        Assert.Equal(["system.memory.capacity", "ram-1", "16.0 GB", "参考", "-"], Cells(RowOf(r.Out, "system.memory.capacity")));
        Assert.Equal(["system.gpu.generic_driver", "gpu-1 (NVIDIA GeForce RTX 5070)", "いいえ", "OK", "-"], Cells(RowOf(r.Out, "system.gpu.generic_driver")));
        Assert.Equal(["system.eventlog.volmgr7d", "-", "-", "測れず", "権限なし"], Cells(RowOf(r.Out, "system.eventlog.volmgr7d")));
        Assert.EndsWith("\n注記: CPU の温度は測れていません\n", r.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ok", "OK", "OK")]
    [InlineData("caution", "注意", "Caution")]
    [InlineData("check", "要確認", "Needs review")]
    [InlineData("unavailable", "測れず", "Not measured")]
    public async Task The_four_overall_verdicts_have_the_words_of_the_spec_in_both_languages(string name, string ja, string en)
    {
        using var data = new TempFolder();
        var result = name switch
        {
            "ok" => CheckSamples.SystemOk(),
            "caution" => CheckSamples.SystemCaution(),
            "check" => CheckSamples.SystemCheck(),
            _ => CheckSamples.SystemUnavailable(),
        };

        var japanese = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(result), language: Language.Ja);
        var english = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(result), language: Language.En);

        Assert.StartsWith($"システムの確認: {ja}\n", japanese.Out, StringComparison.Ordinal);
        Assert.StartsWith($"System check: {en}\n", english.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_info_word_is_used_for_a_row_that_is_not_judged_in_both_languages()
    {
        using var data = new TempFolder();
        var result = CheckSamples.SystemOk();

        var japanese = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(result), language: Language.Ja);
        var english = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(result), language: Language.En);

        Assert.Equal(["system.cpu.cores", "-", "8", "参考", "-"], Cells(RowOf(japanese.Out, "system.cpu.cores")));
        Assert.Equal(["system.cpu.cores", "-", "8", "Info", "-"], Cells(RowOf(english.Out, "system.cpu.cores")));
    }

    // ---- the words ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_note_this_version_does_not_know_is_shown_by_its_code_and_every_known_note_has_words()
    {
        foreach (var language in new[] { Language.En, Language.Ja })
        {
            using var context = Context(language);
            foreach (var code in CheckNotes.Known)
            {
                var text = CheckCommand.NoteText(context, code);
                Assert.False(text.StartsWith('['), code + ": " + text);
                Assert.DoesNotContain('{', text);
            }

            var unknown = CheckCommand.NoteText(context, "memory.from_the_future");
            Assert.Contains("memory.from_the_future", unknown, StringComparison.Ordinal);
            Assert.DoesNotContain('{', unknown);
        }
    }

    [Fact]
    public void The_words_of_every_verdict_kind_and_outcome_come_from_the_tables_in_both_languages()
    {
        foreach (var language in new[] { Language.En, Language.Ja })
        {
            using var context = Context(language);
            foreach (var verdict in Enum.GetValues<ItemVerdict>())
            {
                Assert.Equal(CliWords.Of("cli.check.verdict." + verdict.ToString().ToLowerInvariant(), language), CheckCommand.VerdictWord(context, verdict.ToString()));
            }

            foreach (var kind in Enum.GetValues<CheckKind>())
            {
                Assert.Equal(CliWords.Of("cli.check.kind." + kind.ToString().ToLowerInvariant(), language), CheckCommand.KindWord(context, kind));
            }

            foreach (var outcome in Enum.GetValues<CheckOutcome>())
            {
                Assert.Equal(CliWords.Of("cli.check.outcome." + outcome.ToString().ToLowerInvariant(), language), CheckCommand.OutcomeWord(context, outcome));
            }
        }
    }
}
