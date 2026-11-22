using System.Globalization;
using System.Numerics;
using System.Text;
using PcMieru.Checks;

namespace PcMieru.Gui;

/// <summary>
/// Turns the IDs, codes and values of a check result into the words and numbers of the screen. All text is looked up in
/// <see cref="CheckStrings"/> (ja and en); the Checks library supplies none. Numbers use the invariant culture on purpose: the same
/// result reads the same on every machine and in every test ("1,204 → 1,227（+23）").
/// </summary>
internal static class CheckFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The size of one NVMe "data unit" in bytes (1,000 sectors of 512 bytes: DESIGN-A §14.4).</summary>
    public const long NvmeDataUnitBytes = 512_000;

    /// <summary>The largest uptime written as days and a clock (about 9,500 years); a value beyond it is written as a plain number of seconds.</summary>
    private const double UptimeLimitSeconds = 3e11;

    // ---- names of the enums and codes ----

    /// <summary>"BaselineFirst" becomes "baseline_first" (the spelling of the codes in the files). Digits stay attached to the word.</summary>
    public static string Snake(string pascal)
    {
        var text = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c) && i > 0)
            {
                text.Append('_');
            }

            text.Append(char.ToLowerInvariant(c));
        }

        return text.ToString();
    }

    /// <summary>The text of a key, or null when the key is not in the table (the table's own answer for a missing key is "[key]").</summary>
    public static string? Label(string key)
    {
        var text = Strings.Get(key);
        return text.Length == key.Length + 2 && text[0] == '[' && text[^1] == ']' && string.CompareOrdinal(text, 1, key, 0, key.Length) == 0 ? null : text;
    }

    public static string VerdictWord(ItemVerdict verdict) => Strings.Get("gui.chk.verdict." + Snake(verdict.ToString()));

    public static string VerdictWord(CheckVerdict verdict) => Strings.Get("gui.chk.verdict." + Snake(verdict.ToString()));

    /// <summary>
    /// How a verdict is drawn, each in a colour of its own (always with its word next to it): ok is green, caution is amber, needs review
    /// is red (and bold), not measured is grey. Info is an observation, not a verdict: plain text, whatever the value says.
    /// </summary>
    public static CellTone Tone(ItemVerdict verdict) => verdict switch
    {
        ItemVerdict.Ok => CellTone.Good,
        ItemVerdict.Caution => CellTone.Warning,
        ItemVerdict.Check => CellTone.Danger,
        ItemVerdict.Unavailable => CellTone.Dim,
        _ => CellTone.Normal,
    };

    public static CellTone Tone(CheckVerdict verdict) => Tone(ToItem(verdict));

    public static ItemVerdict ToItem(CheckVerdict verdict) => verdict switch
    {
        CheckVerdict.Check => ItemVerdict.Check,
        CheckVerdict.Caution => ItemVerdict.Caution,
        CheckVerdict.Unavailable => ItemVerdict.Unavailable,
        _ => ItemVerdict.Ok,
    };

    public static string KindName(CheckKind kind) => Strings.Get("gui.chk.kind." + Snake(kind.ToString()));

    public static string OutcomeName(CheckOutcome outcome) => Strings.Get("gui.chk.outcome." + Snake(outcome.ToString()));

    public static string ReasonText(CheckReason reason) => Strings.Get("gui.chk.reason." + Snake(reason.ToString()));

    public static string EvidenceCodeText(CheckEvidenceCode code) => Strings.Get("gui.chk.ev." + Snake(code.ToString()));

    /// <summary>The label of an item ID. An ID this version has no label for shows as itself (the coverage test makes sure none is missing).</summary>
    public static string ItemLabel(string id) => Label(CheckStrings.ItemPrefix + id) ?? id;

    public static string NoteText(string code) =>
        Label("gui.chk.note." + code) ?? Strings.Format("gui.chk.note.unknown", Fmt.Dict(("code", code)));

    public static string FailureText(string? code) =>
        code is null ? Strings.Get("gui.chk.fail.unknown") : Label("gui.chk.fail." + code) ?? Strings.Get("gui.chk.fail.unknown");

    public static string SaveFailureText(string? code) =>
        code is null ? Strings.Get("gui.chk.savefail.unknown") : Label("gui.chk.savefail." + code) ?? Strings.Get("gui.chk.savefail.unknown");

    /// <summary>One line of the listing's warnings: what happened and to how many files.</summary>
    public static string WarningText(CheckHistoryWarning warning) =>
        Label("gui.chk.warn." + warning.Code) is not null
            ? Strings.Format("gui.chk.warn." + warning.Code, Fmt.Dict(("count", warning.Count.ToString(Inv))))
            : Strings.Format("gui.chk.warn.unknown", Fmt.Dict(("code", warning.Code), ("count", warning.Count.ToString(Inv))));

    /// <summary>Why one file of the history could not be opened (the answer of a get that came back unreadable).</summary>
    public static string WarningOneText(string? code) =>
        code is not null && Label("gui.chk.warnone." + code) is { } text ? text : Strings.Get("gui.chk.warnone.unknown");

    // ---- time ----

    public static string LocalTime(DateTimeOffset time) => time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public static string Seconds(DateTimeOffset start, DateTimeOffset end) =>
        Math.Max(0, (long)Math.Round((end - start).TotalSeconds)).ToString(Inv);

    public static string Clock(double seconds)
    {
        var whole = (long)Math.Max(0, Math.Floor(seconds));
        return (whole / 60).ToString(Inv) + ":" + (whole % 60).ToString("00", Inv);
    }

    // ---- subjects ----

    /// <summary>"ディスク 2", with the model after it when the result has one (a model name as read, never a serial).</summary>
    public static string SubjectText(CheckSubject subject)
    {
        var name = SubjectName(subject.Id);
        return string.IsNullOrWhiteSpace(subject.Model)
            ? name
            : Strings.Format("gui.chk.subject.model", Fmt.Dict(("subject", name), ("model", subject.Model.Trim())));
    }

    /// <summary>"ram-2" becomes "メモリ 2". An id of a kind this version does not know shows as it is.</summary>
    public static string SubjectName(string id)
    {
        var dash = id.LastIndexOf('-');
        if (dash <= 0 || dash == id.Length - 1)
        {
            return id;
        }

        return Label("gui.chk.subject." + id[..dash]) is not null
            ? Strings.Format("gui.chk.subject." + id[..dash], Fmt.Dict(("n", id[(dash + 1)..])))
            : id;
    }

    // ---- values ----

    public static string ValueText(CheckItem item) =>
        item.Value is { } value ? ValueText(item.Id, value, item.Unit) : Fmt.Dash;

    /// <summary>
    /// The text of a cell, or <paramref name="raw"/> when writing it throws. A result that did not pass validation is shown anyway
    /// (<see cref="CheckRun.UnsavedResult"/>), so no value may be assumed to be in range: one cell that cannot be written must not take the
    /// other rows of the table with it.
    /// </summary>
    public static string Safe(Func<string> format, Func<string> raw)
    {
        try
        {
            return format();
        }
        catch (Exception)
        {
            return raw();
        }
    }

    /// <summary>A value as it is held, with no unit and no words (what a cell shows when the formatted text could not be made).</summary>
    public static string RawValueText(CheckItem item)
    {
        if (item.Value is not { } value)
        {
            return Fmt.Dash;
        }

        return value.Type switch
        {
            CheckValueType.Int => value.AsInt.ToString(Inv),
            CheckValueType.Number => value.AsNumber.ToString("R", Inv),
            CheckValueType.Bool => value.AsBool ? "true" : "false",
            CheckValueType.OsVersion => value.AsOsVersion.Name,
            _ => value.AsText,
        };
    }

    /// <summary>The evidence of an item as it is held: the code, the before and after, with no words (the fallback of the evidence cell).</summary>
    public static string RawEvidenceText(CheckItem item)
    {
        var parts = new List<string>();
        if (item.Reason is { } reason)
        {
            parts.Add(Snake(reason.ToString()));
        }

        if (item.Evidence is { } evidence)
        {
            if (evidence.Code is { } code)
            {
                parts.Add(Snake(code.ToString()));
            }

            if (evidence.Before is not null || evidence.After is not null)
            {
                parts.Add(evidence.Before + " → " + evidence.After);
            }
        }

        return string.Join(" ・ ", parts);
    }

    /// <summary>
    /// What an exception is, as the screen may say it: its type and its HRESULT ("InvalidOperationException 0x80131509"). Never its message,
    /// which can carry a path, a user name or the sentence of the OS language. The form is the one of the history tab and of the Checks library.
    /// </summary>
    public static string ExceptionDetail(Exception ex) =>
        ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", Inv);

    public static string ValueText(string id, CheckValue value, string? unit)
    {
        switch (value.Type)
        {
            case CheckValueType.OsVersion:
                return OsText(value.AsOsVersion);
            case CheckValueType.Bool:
                return Strings.Get(value.AsBool ? "gui.chk.bool.true" : "gui.chk.bool.false");
            case CheckValueType.Text:
                return Label("gui.chk.text." + value.AsText) is { } word && CheckItemRegistry.TryGet(id, out var spec) && spec.AllowedText is not null
                    ? word
                    : value.AsText;
        }

        var signed = value.Type == CheckValueType.SignedDec;
        if (value.Type == CheckValueType.Number)
        {
            return UnitText(id, unit, Decimal(value.AsNumber), value.AsNumber, null, signed: false);
        }

        var whole = value.Type == CheckValueType.Int ? new BigInteger(value.AsInt) : ParseBig(value.AsText) ?? BigInteger.Zero;
        return UnitText(id, unit, Whole(whole, signed), (double)whole, whole, signed);
    }

    private static string UnitText(string id, string? unit, string number, double asDouble, BigInteger? whole, bool signed)
    {
        switch (unit)
        {
            case null:
                return number;
            case "B":
                // A number that is NaN or infinite has no byte count: it stays the plain number.
                return whole is { } bytes ? Size(bytes) : double.IsFinite(asDouble) ? Size(new BigInteger(asDouble)) : number;
            case "bits":
                return whole is { } bits && HexText(bits) is { } hex ? hex : number;
            case "s" when id == "system.os.uptime" && Math.Abs(asDouble) <= UptimeLimitSeconds:
                return Fmt.Uptime((long)asDouble);
            case "nvme_data_unit" when !signed && whole is { } units:
                return Strings.Format("gui.chk.unit.nvme", Fmt.Dict(("value", number), ("size", Size(units * NvmeDataUnitBytes))));
            case "nvme_data_unit":
                return Strings.Format("gui.chk.unit.nvmeplain", Fmt.Dict(("value", number)));
        }

        return UnitKey(unit) is { } key ? Strings.Format(key, Fmt.Dict(("value", number))) : number + " " + unit;
    }

    /// <summary>The key of the template that writes a number with the registry unit, or null for a unit this version does not know.</summary>
    internal static string? UnitKey(string unit) => unit switch
    {
        "count" => "gui.chk.unit.count",
        "s" => "gui.chk.unit.s",
        "MHz" => "gui.chk.unit.mhz",
        "MT/s" => "gui.chk.unit.mts",
        "d" => "gui.chk.unit.d",
        "code" => "gui.chk.unit.code",
        "chunks/s" => "gui.chk.unit.chunks",
        "%" => "gui.chk.unit.percent",
        "W" => "gui.chk.unit.w",
        "MiB/s" => "gui.chk.unit.mibs",
        "°C" => "gui.chk.unit.celsius",
        "bits" => "gui.chk.unit.bits",
        "h" => "gui.chk.unit.h",
        "nvme_data_unit" => "gui.chk.unit.nvme",
        "smart_raw" => "gui.chk.unit.raw",
        "B" => "gui.chk.unit.b",
        _ => null,
    };

    private static string OsText(OsVersionValue os)
    {
        var build = os.Build.ToString(Inv) + (os.Ubr is { } ubr ? "." + ubr.ToString(Inv) : string.Empty);
        return Strings.Format("gui.chk.os.value", Fmt.Dict(("name", os.Name), ("version", os.DisplayVersion ?? string.Empty), ("build", build)))
            .Replace("  ", " ", StringComparison.Ordinal);
    }

    /// <summary>
    /// "0x" and the digits of a non-negative whole number, two at least ("0x05", "0xFF", "0x1FFFFFFFFFFFFFFFF"), written from the number itself with no
    /// cast to a smaller type (a value of 128 bits is a valid dec). Null for a negative number, which has no mask to show.
    /// </summary>
    internal static string? HexText(BigInteger value)
    {
        if (value.Sign < 0)
        {
            return null;
        }

        // BigInteger writes a leading zero when the top bit of the first digit is set ("0FF"): the digits are taken as they are, trimmed and padded.
        var digits = value.ToString("X", Inv).TrimStart('0');
        return "0x" + digits.PadLeft(2, '0');
    }

    /// <summary>"1,204"; with the sign for a difference ("+23", "-4", "0").</summary>
    public static string Whole(BigInteger value, bool signed = false) =>
        (signed && value.Sign > 0 ? "+" : string.Empty) + value.ToString("N0", Inv);

    private static string Decimal(double value)
    {
        var text = value.ToString("N1", Inv);
        return text.EndsWith(".0", StringComparison.Ordinal) ? text[..^2] : text;
    }

    /// <summary>A byte count with its 1024 step ("931.5 GB"). Exact below a kilobyte.</summary>
    public static string Size(BigInteger bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var size = (double)bytes;
        var unit = 0;
        while (Math.Abs(size) >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? bytes.ToString("N0", Inv) + " B"
            : size.ToString(Math.Abs(size) >= 100 ? "N0" : "N1", Inv) + " " + units[unit];
    }

    private static BigInteger? ParseBig(string? text) =>
        text is not null && BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, Inv, out var value) ? value : null;

    // ---- evidence ----

    /// <summary>
    /// The evidence column of a row: why the value is missing, the counter's change (1,204 → 1,227（+23）) and the fixed evidence the row
    /// carries, joined with " ・ ". Empty when there is nothing to say.
    /// </summary>
    /// <param name="withChange">False for a counter row that has its change row right under it (DESIGN-A §7): the change is written on that row only.</param>
    public static string EvidenceText(CheckItem item, bool withChange = true)
    {
        var parts = new List<string>();
        var evidence = item.Evidence;
        var baseline = evidence?.Code is { } c && CheckEvidenceCodes.IsBaseline(c);
        if (item.Reason is { } reason && !baseline)
        {
            parts.Add(ReasonText(reason));
        }

        if (evidence is not null)
        {
            AddEvidence(parts, evidence, withChange);
        }

        return Join(parts);
    }

    /// <summary>The change of a compared counter ("1,204 → 1,227（+23）"), or null when the evidence has no before and after.</summary>
    public static string? ChangeText(CheckEvidence evidence)
    {
        if (evidence.Before is null || evidence.After is null)
        {
            return null;
        }

        var before = ParseBig(evidence.Before);
        var after = ParseBig(evidence.After);
        if (before is null || after is null)
        {
            return evidence.Before + " → " + evidence.After;
        }

        return Strings.Format("gui.chk.evf.change", Fmt.Dict(("before", Whole(before.Value)), ("after", Whole(after.Value)), ("delta", Whole(after.Value - before.Value, signed: true))));
    }

    private static void AddEvidence(List<string> parts, CheckEvidence e, bool withChange)
    {
        if (withChange && ChangeText(e) is { } change)
        {
            parts.Add(change);
        }

        if (e.Code is { } code)
        {
            parts.Add(EvidenceCodeText(code));
        }

        if (e.SinceUtc is { } since)
        {
            parts.Add(One("gui.chk.evf.since", LocalTime(since)));
        }

        if (e.NewBits is { } bits)
        {
            parts.Add(One("gui.chk.evf.newbits", "0x" + bits.ToString("X2", Inv)));
        }

        if (e.TotalBytes is not null)
        {
            parts.Add(One("gui.chk.evf.total", ParseBig(e.TotalBytes) is { } total ? Size(total) : e.TotalBytes));
        }

        if (e.DiskId is not null)
        {
            parts.Add(One("gui.chk.evf.disk", SubjectName(e.DiskId)));
        }

        if (e.EfficiencyClass is { } eff)
        {
            parts.Add(One("gui.chk.evf.effclass", eff.ToString(Inv)));
        }

        if (e.Cv is { } cv)
        {
            parts.Add(One("gui.chk.evf.cv", Decimal(cv) + " %"));
        }

        if (e.ClockSteadiness is { } steady)
        {
            parts.Add(One("gui.chk.evf.steadiness", Decimal(steady) + " %"));
        }

        if (e.WarningTempMinutes is not null)
        {
            parts.Add(One("gui.chk.evf.warntemp", Minutes(e.WarningTempMinutes)));
        }

        if (e.CriticalTempMinutes is not null)
        {
            parts.Add(One("gui.chk.evf.crittemp", Minutes(e.CriticalTempMinutes)));
        }

        if (e.RecentMediaErrors is { } media)
        {
            parts.Add(One("gui.chk.evf.mediaerrors", media.ToString("N0", Inv)));
        }

        if (e.RecentEntries is { } entries)
        {
            parts.Add(One("gui.chk.evf.entries", entries.ToString("N0", Inv)));
        }

        if (e.Capacities is { Count: > 0 } capacities)
        {
            parts.Add(One("gui.chk.evf.capacities", string.Join(", ", capacities.Select(static c => ParseBig(c) is { } size ? Size(size) : c))));
        }
    }

    private static string Minutes(string text) => ParseBig(text) is { } minutes ? Whole(minutes) : text;

    private static string One(string key, string value) => Strings.Format(key, Fmt.Dict(("value", value)));

    /// <summary>Two phrases on one line, with the separator of the language ("完了しました ・ 結果を保存できませんでした").</summary>
    public static string Pair(string first, string second) =>
        first.Length == 0 ? second : second.Length == 0 ? first : Strings.Format("gui.chk.evf.join", Fmt.Dict(("first", first), ("second", second)));

    /// <summary>
    /// The result a table shows for a run: what the check built when the strict validation refused it (it is never saved, the screen still
    /// shows it), else the run's own result.
    /// </summary>
    public static CheckResult? DisplayResult(CheckRun? run) => run is null ? null : run.UnsavedResult ?? run.Result;

    private static string Join(List<string> parts)
    {
        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var text = parts[0];
        for (var i = 1; i < parts.Count; i++)
        {
            text = Strings.Format("gui.chk.evf.join", Fmt.Dict(("first", text), ("second", parts[i])));
        }

        return text;
    }

    // ---- results ----

    /// <summary>A disk was refused under normal rights: this storage result can be read again with the administrator's permission.</summary>
    public static bool HasDeniedDisk(CheckResult? result) =>
        result is { Kind: CheckKind.Storage }
        && result.Items.Any(static item => item.Reason == CheckReason.Denied && item.Subject is { } subject && subject.Id.StartsWith("disk-", StringComparison.Ordinal));

    /// <summary>The summary line under a table: the overall verdict and how many rows carry each verdict.</summary>
    public static string SummaryText(CheckResult result)
    {
        // A count of zero is not written: a run with no row (a stopped CPU run) reads "Overall: Not measured", not "Not measured (… Not measured 0 …)".
        var counts = new List<string>();
        foreach (var (verdict, count) in new[]
                 {
                     (ItemVerdict.Check, result.Counts.Check),
                     (ItemVerdict.Caution, result.Counts.Caution),
                     (ItemVerdict.Ok, result.Counts.Ok),
                     (ItemVerdict.Unavailable, result.Counts.Unavailable),
                     (ItemVerdict.Info, result.Counts.Info),
                 })
        {
            if (count > 0)
            {
                counts.Add(VerdictWord(verdict) + " " + count.ToString(Inv));
            }
        }

        return Strings.Format(
            counts.Count == 0 ? "gui.chk.summary.none" : "gui.chk.summary",
            Fmt.Dict(("verdict", VerdictWord(result.Verdict)), ("counts", string.Join(" / ", counts))));
    }

    public static string RanAtText(CheckResult result) =>
        Strings.Format("gui.chk.ranat", Fmt.Dict(("time", LocalTime(result.StartedAtUtc)), ("seconds", Seconds(result.StartedAtUtc, result.EndedAtUtc))));

    /// <summary>When it ran, the overall verdict with the counts, and "nothing to show" for a result that has no item (a stopped or failed run can be empty).</summary>
    public static string ResultLine(CheckResult result)
    {
        var line = Pair(RanAtText(result), SummaryText(result));
        return result.Items.Count == 0 ? Pair(line, Strings.Get("gui.chk.nothing")) : line;
    }

    // ---- state and progress ----

    /// <summary>
    /// How one kind of check stands, in words: not run, running, stopping, completed, stopped, failed (with why). "The result could not be
    /// saved" is appended after a run that is over, whichever way it ended (a result that did not save is not on the history tab).
    /// </summary>
    public static string StateText(CheckKindState state)
    {
        var text = state.State == CheckRunState.Failed
            ? Strings.Format(StateKey(state.State), Fmt.Dict(("reason", FailureReason(state))))
            : Strings.Get(StateKey(state.State));
        if (state.SaveFailed && state.State is not (CheckRunState.Running or CheckRunState.Stopping) && state.Run is { } run)
        {
            var saved = Strings.Format("gui.chk.state.savefailed", Fmt.Dict(("reason", SaveFailureText(run.Save.FailureCode))));
            text = Strings.Format("gui.chk.evf.join", Fmt.Dict(("first", text), ("second", saved)));
        }

        return text;
    }

    /// <summary>
    /// Why a run failed, in words: the fixed sentence of its failure code, and, for a runner that threw, the type and the HRESULT after it
    /// ("An internal error occurred (InvalidOperationException 0x80131509)"). The exception's own message is never part of it.
    /// </summary>
    private static string FailureReason(CheckKindState state)
    {
        var text = FailureText(state.FailureCode ?? state.Run?.FailureCode);
        return string.IsNullOrEmpty(state.Error) ? text : Strings.Format("gui.chk.state.detail", Fmt.Dict(("text", text), ("detail", state.Error)));
    }

    /// <summary>
    /// The key of the words of a run state: the one place that names them. A state this version does not know reads as "not run" rather than
    /// as nothing (the coverage test walks <see cref="CheckRunState"/> through this and finds a member without words).
    /// </summary>
    public static string StateKey(CheckRunState state) => state switch
    {
        CheckRunState.Running => "gui.chk.state.running",
        CheckRunState.Stopping => "gui.chk.state.stopping",
        CheckRunState.Completed => "gui.chk.state.completed",
        CheckRunState.Stopped => "gui.chk.state.stopped",
        CheckRunState.Failed => "gui.chk.state.failed",
        _ => "gui.chk.state.notrun",
    };

    /// <summary>
    /// The key of the words of a stage of a run: the one place that names them. <see cref="CheckStage.Loading"/> is a template ("0:12 / 0:30"),
    /// the others plain words. A stage this version does not know has words of its own ("Working") and is never empty.
    /// </summary>
    public static string StageKey(CheckStage stage) => stage switch
    {
        CheckStage.Starting or CheckStage.Collecting => "gui.chk.load.stage.collecting",
        CheckStage.Loading => "gui.chk.load.elapsed",
        CheckStage.Judging => "gui.chk.load.stage.judging",
        CheckStage.Saving => "gui.chk.load.stage.saving",
        CheckStage.Done => "gui.chk.load.stage.done",
        _ => "gui.chk.load.stage.unknown",
    };

    /// <summary>Where a running check is: preparing, "0:12 / 0:30" while it loads, judging, saving, done. Empty only before the first report.</summary>
    public static string ProgressText(CheckProgress? progress)
    {
        if (progress is null)
        {
            return string.Empty;
        }

        var key = StageKey(progress.Stage);
        return progress.Stage == CheckStage.Loading
            ? Strings.Format(key, Fmt.Dict(("elapsed", Clock(progress.ElapsedSeconds)), ("total", Clock(progress.TargetSeconds))))
            : Strings.Get(key);
    }

    /// <summary>The share of a load that is done (0 to 1), or null when the stage has no target (the bar then does not move).</summary>
    public static double? Fraction(CheckProgress? progress) =>
        progress is { Stage: CheckStage.Loading, TargetSeconds: > 0 }
            ? Math.Clamp(progress.ElapsedSeconds / progress.TargetSeconds, 0, 1)
            : progress is { Stage: CheckStage.Judging or CheckStage.Saving or CheckStage.Done } ? 1 : null;

    /// <summary>"Current rate: 1,234 chunks/s", or empty when the report has no rate yet.</summary>
    public static string RateText(CheckProgress? progress)
    {
        if (progress is not { RecentRate: { } rate, RateUnit: { } unit } || double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0)
        {
            return string.Empty;
        }

        var number = rate >= 100 ? rate.ToString("N0", Inv) : Decimal(rate);
        var value = UnitKey(unit) is { } key ? Strings.Format(key, Fmt.Dict(("value", number))) : number + " " + unit;
        return Strings.Format("gui.chk.load.rate", Fmt.Dict(("rate", value)));
    }
}
