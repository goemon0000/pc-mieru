using System.Globalization;
using System.Text;
using PcMieru.Checks;
using PcMieru.Core;

namespace PcMieru.Cli.Commands;

/// <summary>
/// <c>pcmi check system|storage|history</c> (DESIGN-A 8): the two checks that do not load the machine, and the list of the checks kept so far.
/// A check is saved to the history like the app's window saves it; what is printed is that same result, as one <c>pcm-check/1</c> document
/// (<c>--json</c>) or as a table. Data goes to stdout, every diagnostic (progress, hints, why a run failed) to stderr.
/// <para>
/// Exit codes: 0 = the run completed (any verdict, including caution, check and partial rows) and was saved, or the history was read (warnings
/// about single files keep 0; a history folder that is not there is an empty history); 1 = the run failed, was stopped (Ctrl+C), or could not be
/// saved, or the history folder could not be opened; 2 = the command line is wrong; 4 = <c>--elevate</c> was given and the administrator
/// approval was refused, so no drive that needed it could be read (the results and the reason of every row are still printed).
/// </para>
/// </summary>
public sealed class CheckCommand : ICliCommand
{
    private const string Verbs = "system, storage or history";

    public string Name => "check";

    public string Usage => "pcmi check system [--json] | storage [--elevate] [--json] | history [--json]";

    public IReadOnlyList<OptionSpec> Options { get; } = [new("json"), new("elevate")];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.check", "システム・ストレージの確認と、確認の履歴（system / storage / history）", "System and storage checks, and the history of checks (system / storage / history)"),

        // The words of a verdict (an item may also be "info"), a kind, an outcome, a reason and a stage. One key per enum member (the strings test enumerates them).
        new("cli.check.verdict.ok", "OK", "OK"),
        new("cli.check.verdict.caution", "注意", "Caution"),
        new("cli.check.verdict.check", "要確認", "Needs review"),
        new("cli.check.verdict.unavailable", "測れず", "Not measured"),
        new("cli.check.verdict.info", "参考", "Info"),
        new("cli.check.kind.system", "システム", "System"),
        new("cli.check.kind.cpu", "CPU", "CPU"),
        new("cli.check.kind.memory", "メモリ", "Memory"),
        new("cli.check.kind.storage", "ストレージ", "Storage"),
        new("cli.check.outcome.completed", "完了", "Completed"),
        new("cli.check.outcome.stopped", "中断", "Stopped"),
        new("cli.check.outcome.failed", "失敗", "Failed"),
        new("cli.check.reason.unsupported", "非対応", "Unsupported"),
        new("cli.check.reason.denied", "権限なし", "Denied"),
        new("cli.check.reason.timeout", "時間切れ", "Timed out"),
        new("cli.check.reason.error", "読み取りエラー", "Read error"),
        new("cli.check.reason.partial", "一部のみ", "Partial"),
        new("cli.check.stage.starting", "確認を始めています", "Starting the check"),
        new("cli.check.stage.collecting", "PC の情報を読み取っています", "Reading the PC"),
        new("cli.check.stage.loading", "負荷をかけて測っています", "Measuring under load"),
        new("cli.check.stage.judging", "判定しています", "Judging the results"),
        new("cli.check.stage.saving", "結果を保存しています", "Saving the result"),
        new("cli.check.stage.done", "終わりました", "Done"),

        // The table of one result.
        new("cli.check.title", "{kind}の確認: {verdict}", "{kind} check: {verdict}"),
        new("cli.check.lbl.run", "実行 ID", "Run ID"),
        new("cli.check.lbl.started", "開始（UTC）", "Started (UTC)"),
        new("cli.check.lbl.ended", "終了（UTC）", "Ended (UTC)"),
        new("cli.check.lbl.outcome", "経過", "Outcome"),
        new("cli.check.lbl.counts", "件数", "Counts"),
        new("cli.check.lbl.saved", "保存", "Saved"),
        new("cli.check.col.item", "項目", "Item"),
        new("cli.check.col.subject", "対象", "Subject"),
        new("cli.check.col.value", "値", "Value"),
        new("cli.check.col.verdict", "判定", "Verdict"),
        new("cli.check.col.reason", "理由", "Reason"),
        new("cli.check.noteLine", "注記: {text}", "Note: {text}"),
        new("cli.check.note.cpu.temperature_not_measured", "CPU の温度は測れていません", "The CPU temperature was not measured"),
        new("cli.check.note.memory.borrowed_region_only", "メモリは、借りられた範囲だけを調べています", "Only the memory that could be borrowed was tested"),
        new("cli.check.note.memory.pressure", "ほかの処理がメモリを使っていたため、調べた範囲が狭くなっています", "Other programs were using memory, so the tested range is narrower"),
        new("cli.check.note.storage.baseline_read_failed", "ストレージの前回の記録を読めなかったため、前回との比べは出していません", "The earlier storage record could not be read, so there is no comparison with last time"),
        new("cli.check.note.storage.legacy_import_abandoned", "旧版の記録の取り込みを途中でやめました", "Importing the old version's record was given up"),
        new("cli.check.note.unknown", "{code}（この版では説明を持たない注記）", "{code} (a note this version has no text for)"),

        // Why a run is not a plain success. Printed on stderr.
        new("cli.check.stopped", "確認を途中で止めました。止めた時点の結果を保存しています", "The check was stopped. The result up to that point is saved"),
        new("cli.check.fail.inspection_failed", "ドライブの情報を読み取れず、確認を終えられませんでした", "The drives could not be read, so the check could not finish"),
        new("cli.check.fail.result_invalid", "確認の結果が決まりに合わず、中身のない失敗として記録しました（アプリ側の不具合です）", "The result broke the format rules, so an empty failed result was saved instead (a bug of the app)"),
        new("cli.check.fail.internal_error", "予期しない問題で確認を終えられませんでした", "The check stopped on an unexpected problem"),
        new("cli.check.fail.unknown", "確認を終えられませんでした（{code}）", "The check could not finish ({code})"),
        new("cli.check.savefail.invalid_result", "結果が決まりに合わないため、保存しませんでした", "The result was not saved because it breaks the format rules"),
        new("cli.check.savefail.already_exists", "同じ結果がすでにあるため、保存しませんでした", "The result was not saved because the same result already exists"),
        new("cli.check.savefail.access_denied", "権限がなく、結果を保存できませんでした", "The result could not be saved: access denied"),
        new("cli.check.savefail.disk_full", "空き容量が足りず、結果を保存できませんでした", "The result could not be saved: the disk is full"),
        new("cli.check.savefail.io_error", "書き込みに失敗し、結果を保存できませんでした", "The result could not be saved: a write failed"),
        new("cli.check.savefail.notSaved", "結果は保存されませんでした", "The result was not saved"),
        new("cli.check.savefail.unknown", "結果を保存できませんでした（{code}）", "The result could not be saved ({code})"),
        new("cli.check.elevateMaybe", "--elevate: 管理者の許可が要るドライブがあれば、許可を求めます。表示される確認に答えてください", "--elevate: if a drive needs administrator approval, it will be asked for; answer the prompt that appears"),
        new("cli.check.noElevatedRead", "管理者の許可が得られず、許可が要るドライブは読み取れませんでした。ほかの結果はそのまま示しています", "Administrator approval was not given, so the drives that need it were not read. The other results are shown as they are"),

        // The history.
        new("cli.check.hist.empty", "確認の履歴はまだありません", "There is no check history yet"),
        new("cli.check.hist.warnings", "読めなかった履歴ファイル", "History files that could not be used"),
        new("cli.check.hist.folderFail", "確認の履歴のフォルダーを開けませんでした", "The check history folder could not be opened"),
        new("cli.check.col.started", "開始（UTC）", "Started (UTC)"),
        new("cli.check.col.kind", "種類", "Kind"),
        new("cli.check.col.outcome", "経過", "Outcome"),
        new("cli.check.col.counts", "件数", "Counts"),
        new("cli.check.col.run", "実行 ID", "Run ID"),
        new("cli.check.col.code", "コード", "Code"),
        new("cli.check.col.files", "件数", "Files"),
        new("cli.check.col.meaning", "意味", "Meaning"),
        new("cli.check.warn.history_unreadable", "読めない履歴ファイルがあります", "Some history files could not be read"),
        new("cli.check.warn.history_future_format", "新しい版が作った形式で、この版では読めない履歴があります", "Some history was made by a newer version and cannot be read by this one"),
        new("cli.check.warn.history_quarantined", "壊れた履歴ファイルを脇へ移しました", "Damaged history files were set aside"),
        new("cli.check.warn.history_quarantine_failed", "壊れた履歴ファイルを脇へ移せませんでした", "Damaged history files could not be set aside"),
        new("cli.check.warn.history_locked", "ほかの処理が使っていて読めない履歴ファイルがあります", "Some history files are in use by another process"),
        new("cli.check.warn.history_access_denied", "権限がなく読めない履歴ファイルがあります", "Some history files cannot be read: access denied"),
        new("cli.check.warn.history_read_failed", "読み込みに失敗した履歴ファイルがあります", "Some history files failed to read"),
        new("cli.check.warn.unknown", "この版では説明を持たない警告です", "A warning this version has no text for"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        if (args.Positionals.Count == 0)
        {
            throw new ArgException($"missing verb ({Verbs})");
        }

        args.RequirePositionals(1, 1);
        var verb = args.Positionals[0];
        var json = args.Has("json");
        var elevate = args.Has("elevate");
        switch (verb)
        {
            case "system":
                RejectElevate(elevate, verb);
                return await RunCheckAsync(context, CheckKind.System, elevate: false, json).ConfigureAwait(false);
            case "storage":
                return await RunCheckAsync(context, CheckKind.Storage, elevate, json).ConfigureAwait(false);
            case "history":
                RejectElevate(elevate, verb);
                return await RunHistoryAsync(context, json).ConfigureAwait(false);
            default:
                throw new ArgException($"unknown verb '{CliText.Printable(verb)}' ({Verbs})");
        }
    }

    private static void RejectElevate(bool elevate, string verb)
    {
        if (elevate)
        {
            throw new ArgException($"--elevate belongs to 'check storage' only, not 'check {verb}'");
        }
    }

    // ---- a run -----------------------------------------------------------------------------------------------------------------------

    private static async Task<ExitCode> RunCheckAsync(CliContext context, CheckKind kind, bool elevate, bool json)
    {
        // Held from here to the last byte written: Ctrl+C only cancels the token, and the stopped run still has to be saved and printed.
        using var hold = CliShutdown.Hold();
        var runner = context.CreateCheckRunner();
        if (elevate)
        {
            context.Err.WriteLine("pcmi: " + context.Text("cli.check.elevateMaybe"));
        }

        var stages = context.ErrorIsTerminal ? new StageWriter(context) : null;
        CheckRun run;
        try
        {
            run = await runner.RunAsync(new CheckRequest(kind, Duration: null, AllowElevation: elevate), stages, context.Ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped before the run began (a stop in the middle comes back as a stopped, saved run): nothing was made.
            context.Err.WriteLine("pcmi check: " + context.Text("cli.result.cancelled"));
            return ExitCode.Failure;
        }
        finally
        {
            stages?.Close();
        }

        var result = run.Result;
        if (json)
        {
            context.Out.Write(CheckJson.Serialize(result) + "\n");
        }
        else
        {
            context.Out.Write(RenderResult(context, run));
        }

        var code = Judge(context, run, elevate);

        // Out before the hold goes: a process leaving after Ctrl+C must not find the result still in the buffer.
        await context.Out.FlushAsync().ConfigureAwait(false);
        await context.Err.FlushAsync().ConfigureAwait(false);
        return code;
    }

    /// <summary>
    /// The exit code of a finished run, and the diagnostics on stderr that go with it. Order matters: a failed run is 1 first, a stopped run is
    /// 1, a result that could not be saved is 1; only a completed and saved run can be 4 (elevation refused) or 0.
    /// </summary>
    private static ExitCode Judge(CliContext context, CheckRun run, bool elevate)
    {
        var result = run.Result;
        var code = ExitCode.Success;
        if (result.Outcome == CheckOutcome.Failed)
        {
            context.Err.WriteLine("pcmi check: " + FailureText(context, run));
            code = ExitCode.Failure;
        }
        else if (result.Outcome == CheckOutcome.Stopped)
        {
            context.Err.WriteLine("pcmi check: " + context.Text("cli.check.stopped"));
            code = ExitCode.Failure;
        }

        if (!run.IsSaved)
        {
            context.Err.WriteLine("pcmi check: " + SaveFailureText(context, run.Save));
            code = ExitCode.Failure;
        }

        if (code != ExitCode.Success || result.Kind != CheckKind.Storage)
        {
            return code;
        }

        // A drive that was not read for lack of rights says "denied". Without --elevate that is a hint. With --elevate the exit code is a fact of
        // the inspection (CheckRun.Elevation, DESIGN-A 2): 4 only when drives needed the approval and none was read through it. A run that never
        // inspected (Elevation null) has nothing to refuse.
        if (!elevate)
        {
            if (run.Result.Items.Any(i => i.Reason == CheckReason.Denied))
            {
                context.Err.WriteLine("pcmi check: " + context.Text("cli.hint.elevate"));
            }

            return ExitCode.Success;
        }

        if (run.Elevation is { Needed: > 0, ReRead: 0 })
        {
            context.Err.WriteLine("pcmi check: " + context.Text("cli.check.noElevatedRead"));
            return ExitCode.Denied;
        }

        return ExitCode.Success;
    }

    private static string FailureText(CliContext context, CheckRun run)
    {
        var code = run.FailureCode ?? "unknown";
        var text = CheckRunFailureCodes.All.Contains(code)
            ? context.Text("cli.check.fail." + code)
            : context.Format("cli.check.fail.unknown", ("code", code));
        return string.IsNullOrWhiteSpace(run.FailureDetail) ? text : text + " [" + code + ": " + CliText.Printable(run.FailureDetail) + "]";
    }

    private static string SaveFailureText(CliContext context, CheckSaveState save)
    {
        if (save.Status == CheckSaveStatus.NotSaved)
        {
            return context.Text("cli.check.savefail.notSaved");
        }

        var code = save.FailureCode ?? "unknown";
        return CheckSaveFailureCodes.All.Contains(code)
            ? context.Text("cli.check.savefail." + code)
            : context.Format("cli.check.savefail.unknown", ("code", code));
    }

    /// <summary>Says what the run is doing, on stderr, one line per stage change. Synchronous: nothing arrives after <see cref="Close"/>.</summary>
    private sealed class StageWriter(CliContext context) : IProgress<CheckProgress>
    {
        private readonly Lock _gate = new();
        private bool _closed;
        private CheckStage? _last;

        public void Report(CheckProgress value)
        {
            lock (_gate)
            {
                if (_closed || value.Stage == CheckStage.Done || value.Stage == _last)
                {
                    return;
                }

                _last = value.Stage;
                context.Err.WriteLine("pcmi: " + context.Text("cli.check.stage." + StageKey(value.Stage)));
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
            }
        }
    }

    // ---- the history -----------------------------------------------------------------------------------------------------------------

    private static async Task<ExitCode> RunHistoryAsync(CliContext context, bool json)
    {
        // The one place a damaged history file is moved aside (DESIGN-A 4, 8): the store's listing renames it to name.json.bad-<UTC> and says
        // history_quarantined. The read-only history of the GUI and the MCP server never does. A folder that is not there is an empty
        // page (the store does not create it; only a save does). Held like a run: Ctrl+C must not cut a rename short.
        using var hold = CliShutdown.Hold();
        CheckHistoryPage page;
        try
        {
            page = await new CheckHistoryStore(context.ChecksFolder).ListAsync(new CheckHistoryQuery(Kind: null, Limit: null), context.Ct).ConfigureAwait(false);
        }
        catch (CheckHistoryFolderException ex)
        {
            context.Err.WriteLine("pcmi check: " + context.Text("cli.check.hist.folderFail") + " [" + CliText.Printable(ex.Message) + "]");
            return ExitCode.Failure;
        }

        context.Out.Write(json ? CheckJson.SerializeHistory(page) + "\n" : RenderHistory(context, page));
        await context.Out.FlushAsync().ConfigureAwait(false);
        return ExitCode.Success;
    }

    internal static string RenderHistory(CliContext context, CheckHistoryPage page)
    {
        var sb = new StringBuilder();
        if (page.Entries.Count == 0)
        {
            sb.Append(context.Text("cli.check.hist.empty")).Append('\n');
        }
        else
        {
            var headers = new[]
            {
                context.Text("cli.check.col.started"),
                context.Text("cli.check.col.kind"),
                context.Text("cli.check.col.outcome"),
                context.Text("cli.check.col.verdict"),
                context.Text("cli.check.col.counts"),
                context.Text("cli.check.col.run"),
            };
            var rows = page.Entries
                .Select(e => (IReadOnlyList<string>)
                [
                    Time(e.StartedAtUtc),
                    KindWord(context, e.Kind),
                    OutcomeWord(context, e.Outcome),
                    VerdictWord(context, e.Verdict.ToString()),
                    CountsShort(context, e.Counts),
                    e.RunId.ToString("D", CultureInfo.InvariantCulture),
                ])
                .ToList();
            sb.Append(CliTable.Render(headers, rows, flexColumn: 4, consoleWidth: context.ConsoleWidth));
        }

        if (page.Warnings.Count > 0)
        {
            sb.Append('\n').Append(context.Text("cli.check.hist.warnings")).Append('\n');
            var headers = new[] { context.Text("cli.check.col.code"), context.Text("cli.check.col.files"), context.Text("cli.check.col.meaning") };
            var rows = page.Warnings
                .Select(w => (IReadOnlyList<string>)[w.Code, w.Count.ToString(CultureInfo.InvariantCulture), WarningText(context, w.Code)])
                .ToList();
            sb.Append(CliTable.Render(headers, rows, rightAligned: new HashSet<int> { 1 }, flexColumn: 2, consoleWidth: context.ConsoleWidth));
        }

        return sb.ToString();
    }

    private static string WarningText(CliContext context, string code) =>
        CheckHistoryWarningCodes.IsKnown(code) ? context.Text("cli.check.warn." + code) : context.Text("cli.check.warn.unknown");

    // ---- the table of one result -----------------------------------------------------------------------------------------------------

    internal static string RenderResult(CliContext context, CheckRun run)
    {
        var result = run.Result;
        var sb = new StringBuilder();
        sb.Append(context.Format(
            "cli.check.title",
            ("kind", KindWord(context, result.Kind)),
            ("verdict", VerdictWord(context, result.Verdict.ToString())))).Append("\n\n");

        List<(string Label, string Value)> lines =
        [
            (context.Text("cli.check.lbl.run"), result.RunId.ToString("D", CultureInfo.InvariantCulture)),
            (context.Text("cli.check.lbl.started"), Time(result.StartedAtUtc)),
            (context.Text("cli.check.lbl.ended"), Time(result.EndedAtUtc)),
            (context.Text("cli.check.lbl.outcome"), OutcomeWord(context, result.Outcome)),
            (context.Text("cli.check.lbl.counts"), CountsFull(context, result.Counts)),
        ];
        if (run.IsSaved && run.Save.FileName is { } file)
        {
            lines.Add((context.Text("cli.check.lbl.saved"), file));
        }

        sb.Append(CliFormat.LabelColumn(lines));
        if (result.Items.Count > 0)
        {
            var headers = new[]
            {
                context.Text("cli.check.col.item"),
                context.Text("cli.check.col.subject"),
                context.Text("cli.check.col.value"),
                context.Text("cli.check.col.verdict"),
                context.Text("cli.check.col.reason"),
            };
            var rows = result.Items
                .Select(i => (IReadOnlyList<string>)[i.Id, SubjectText(i.Subject), ValueText(context, i), VerdictWord(context, i.Verdict.ToString()), ReasonText(context, i)])
                .ToList();
            sb.Append('\n').Append(CliTable.Render(headers, rows, flexColumn: 1, consoleWidth: context.ConsoleWidth));
        }

        if (result.Notes.Count > 0)
        {
            sb.Append('\n');
            foreach (var note in result.Notes)
            {
                sb.Append(context.Format("cli.check.noteLine", ("text", NoteText(context, note)))).Append('\n');
            }
        }

        return sb.ToString();
    }

    internal static string NoteText(CliContext context, string code) =>
        CheckNotes.Known.Contains(code) ? context.Text("cli.check.note." + code) : context.Format("cli.check.note.unknown", ("code", code));

    private static string SubjectText(CheckSubject? subject) =>
        subject is null ? "-" : string.IsNullOrEmpty(subject.Model) ? subject.Id : subject.Id + " (" + subject.Model + ")";

    private static string ReasonText(CliContext context, CheckItem item)
    {
        var parts = new List<string>(2);
        if (item.Reason is { } reason)
        {
            parts.Add(context.Text("cli.check.reason." + reason.ToString().ToLowerInvariant()));
        }

        if (item.Evidence?.Code is { } evidence)
        {
            parts.Add("[" + Snake(evidence.ToString()) + "]");
        }

        return parts.Count == 0 ? "-" : string.Join(' ', parts);
    }

    /// <summary>The value with its unit. A value that is missing is "-" (never 0): the reason column says why.</summary>
    internal static string ValueText(CliContext context, CheckItem item)
    {
        if (item.Value is not { } value)
        {
            return "-";
        }

        var text = value.Type switch
        {
            CheckValueType.Int => value.AsInt.ToString(CultureInfo.InvariantCulture),
            CheckValueType.Number => CliText.Num(value.AsNumber, "0.##"),
            CheckValueType.Dec or CheckValueType.SignedDec or CheckValueType.Text => value.AsText,
            CheckValueType.Bool => CliFormat.YesNo(context, value.AsBool),
            CheckValueType.OsVersion => OsText(value.AsOsVersion),
            _ => "-",
        };
        if (item.Unit is null or "count" or "code")
        {
            return text;
        }

        if (item.Unit == "B" && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var bytes))
        {
            return CliFormat.Bytes(bytes);
        }

        return text + " " + item.Unit;
    }

    private static string OsText(OsVersionValue os)
    {
        var text = string.IsNullOrEmpty(os.DisplayVersion) ? os.Name : os.Name + " " + os.DisplayVersion;
        var build = os.Ubr is { } ubr ? $"{os.Build}.{ubr}" : os.Build.ToString(CultureInfo.InvariantCulture);
        return text + " (" + build + ")";
    }

    /// <summary>
    /// "OK 2 / Caution 0 / ...": one part for every verdict of <see cref="ItemVerdict"/>, in the enum's order, with that verdict's own word
    /// (cli.check.verdict.*). Built from the enum, so a verdict added later is a part of the line (or a loud failure in <see cref="CountOf"/>),
    /// never a silent omission.
    /// </summary>
    internal static string CountsFull(CliContext context, CheckCounts counts) =>
        string.Join(" / ", Enum.GetValues<ItemVerdict>().Select(v => VerdictWord(context, v.ToString()) + " " + CountOf(counts, v).ToString(CultureInfo.InvariantCulture)));

    /// <summary>Only the verdicts that have rows ("OK 12 Caution 1"), so a table of many runs stays narrow. Built from the enum like <see cref="CountsFull"/>.</summary>
    internal static string CountsShort(CliContext context, CheckCounts counts)
    {
        var parts = Enum.GetValues<ItemVerdict>()
            .Where(v => CountOf(counts, v) > 0)
            .Select(v => VerdictWord(context, v.ToString()) + " " + CountOf(counts, v).ToString(CultureInfo.InvariantCulture))
            .ToList();
        return parts.Count == 0 ? "-" : string.Join(' ', parts);
    }

    // The one place that maps a verdict to its member of CheckCounts. A verdict with no member throws, and CliStringsTests walks the enum.
    private static int CountOf(CheckCounts counts, ItemVerdict verdict) => verdict switch
    {
        ItemVerdict.Ok => counts.Ok,
        ItemVerdict.Caution => counts.Caution,
        ItemVerdict.Check => counts.Check,
        ItemVerdict.Unavailable => counts.Unavailable,
        ItemVerdict.Info => counts.Info,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "CheckCounts has no member for this verdict"),
    };

    internal static string VerdictWord(CliContext context, string verdictName) => context.Text("cli.check.verdict." + verdictName.ToLowerInvariant());

    internal static string KindWord(CliContext context, CheckKind kind) => context.Text("cli.check.kind." + kind.ToString().ToLowerInvariant());

    internal static string OutcomeWord(CliContext context, CheckOutcome outcome) => context.Text("cli.check.outcome." + outcome.ToString().ToLowerInvariant());

    private static string StageKey(CheckStage stage) => stage.ToString().ToLowerInvariant();

    private static string Time(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"BelowRated" to "below_rated" (the spelling the result file uses for an evidence code).</summary>
    private static string Snake(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(name[i]));
        }

        return sb.ToString();
    }
}
