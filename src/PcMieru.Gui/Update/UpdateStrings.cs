using System.Globalization;
using PcMieru.Update.Feed;

namespace PcMieru.Gui;

/// <summary>
/// The words of the update check (DESIGN-B 3.1, 4): the first-run choice, the settings section, the status-bar notice and the reason of every refusal
/// (one string per <see cref="UpdateRefusal"/>, kept in step with the enum by a test).
///
/// Stage B step 2 review: a reason is a phrase that goes after a colon in a sentence of its own ("確認できませんでした: {reason}。" for a check that did not finish,
/// "新しい版 {version} は、確かめられなかったので使いません: {reason}。" for a release whose own rules did not hold, see <see cref="UpdateRefusals"/>), so it has no
/// full stop and says what happened in the words of the person who runs the app, not the names of the checks.
/// </summary>
internal static class UpdateStrings
{
    public static readonly StringEntry[] All =
    [
        // --- the four modes (one key per UpdateMode member: a test keeps the two in step) ---
        new("gui.update.mode.ask", "あとで選ぶ（確認しません）", "Choose later (no checks)"),
        new("gui.update.mode.automatic", "自動", "Automatic"),
        new("gui.update.mode.notify", "知らせるだけ（おすすめ）", "Notify only (recommended)"),
        new("gui.update.mode.off", "切る", "Off"),

        // --- the first-run choice ---
        new("gui.update.dialog.title", "更新の確認", "Checking for updates"),
        new(
            "gui.update.dialog.lead",
            "PCミエルの新しい版が出ていないか、GitHub に問い合わせてよいか選んでください。送るのはアプリの名前と版だけで、このPCの情報や測定値は送りません。GitHub にはこのPCの IP アドレスが分かります。",
            "Choose whether PC Mieru may ask GitHub if a new version is out. Only the app name and version are sent, nothing about this PC. GitHub sees this PC's IP address."),
        new(
            "gui.update.dialog.automatic",
            "新しい版を確認なしで入れ替えます。発行元を示す電子署名のない版も、確認なしで入れ替えます。",
            "Installs new versions without asking, including versions without a digital signature showing who published them."),
        new(
            "gui.update.dialog.notify",
            "新しい版が出たらお知らせします。入れ替えは自動ではしません。",
            "Tells you when a new version is out. It does not install it by itself."),
        new(
            "gui.update.dialog.off",
            "更新の確認をしません。GitHub には何も問い合わせません。",
            "Never checks for updates. GitHub is not asked anything."),
        new("gui.update.dialog.ok", "決定", "Confirm"),
        new(
            "gui.update.dialog.later",
            "『決定』を押さずに閉じると、確認はせず、次の起動でもう一度お聞きします。",
            "If you close this window without choosing Confirm, nothing is checked and you will be asked again at the next start."),
        new(
            "gui.update.dialog.savefailed",
            "選んだ内容を設定ファイルに保存できませんでした。保存できるまで、確認はしません。",
            "Your choice could not be saved to the settings file. Nothing is checked until it can be saved."),

        // --- the settings section ---
        new("gui.update.section", "更新", "Updates"),
        new("gui.update.version", "いまの版", "This version"),
        new("gui.update.mode.label", "更新の確認", "Update check"),
        new("gui.update.lastcheck", "最後に確認した日時", "Last check"),
        new("gui.update.lastcheck.never", "まだ確認していません", "Not checked yet"),
        new("gui.update.placement.label", "入れ替え先", "Install folder"),
        new("gui.update.placement.canswap", "書き込めます", "Writable"),
        new("gui.update.placement.cannotwrite", "書き込めないため、お知らせだけになります（{reason}）", "Cannot write here, so updates can only be announced ({reason})"),
        new("gui.update.placement.checking", "調べています…", "Checking…"),
        new("gui.update.placement.readonly", "この場所には書き込めません", "this folder is not writable"),
        new("gui.update.placement.elevated", "管理者として開いています", "running as administrator"),
        new("gui.update.placement.notfixeddrive", "固定ディスクではない場所にあります", "not on a fixed disk"),
        new("gui.update.candidate.label", "見つかった新しい版", "Newer version"),
        new("gui.update.candidate.none", "なし", "None"),
        new("gui.update.status.label", "状態", "Status"),
        new("gui.update.checknow", "今すぐ確認", "Check now"),
        new("gui.update.notes", "更新内容をブラウザーで開く", "Open release notes in browser"),
        new("gui.update.checknow.tip", "いま GitHub に問い合わせます。「知らせるだけ」か「自動」のときだけ使えます", "Asks GitHub now. Available only when the check is set to Notify only or Automatic"),
        new("gui.update.notes.tip", "見つかった版のリリースのページを、ブラウザーで開きます", "Opens the release page of the version found in your browser"),

        // --- why a button is off (the line under it) ---
        new("gui.update.reason.choosemode", "上の「更新の確認」で選ぶと使えます", "Choose a mode above to use this"),
        new("gui.update.reason.notsaved", "設定を保存できないため使えません", "Not available: the setting could not be saved"),
        new("gui.update.reason.backoff", "{time} まで確認を控えています", "Available again from {time}"),
        new("gui.update.reason.checking", "確認しているところです", "A check is running"),
        new("gui.update.reason.nocandidate", "新しい版が見つかっていないため、開くものがありません", "No newer version found, so there is nothing to open"),

        // --- what the status line says ---
        new("gui.update.state.checking", "確認しています…", "Checking…"),
        new("gui.update.state.ask", "まだ選んでいないため、確認しません。上の「更新の確認」で選べます。", "Nothing is checked until you choose above."),
        new("gui.update.state.off", "更新の確認は切ってあります。", "The update check is off."),
        new("gui.update.state.waiting", "確認の回数が多いと GitHub に言われたため、{time} まで確認を控えます。", "GitHub asked us to check less often, so the next check waits until {time}."),
        new("gui.update.state.otherrunning", "ほかの確認が終わるのを待っています。終わってからもう一度お試しください。", "Waiting for another check to finish. Please try again once it is done."),
        new("gui.update.state.failed", "確認できませんでした: {reason}。あとでもう一度確かめます。", "The check did not finish: {reason}. It will try again later."),
        new(
            "gui.update.state.refused",
            "新しい版 {version} は、確かめられなかったので使いません: {reason}。次の版をお待ちください。",
            "Version {version} could not be verified, so it will not be used: {reason}. Please wait for the next version."),
        new(
            "gui.update.state.refusednoversion",
            "新しい版は、確かめられなかったので使いません: {reason}。次の版をお待ちください。",
            "A newer version could not be verified, so it will not be used: {reason}. Please wait for the next version."),
        new(
            "gui.update.state.candidate",
            "新しい版 {version} があります。更新内容と入手のしかたはブラウザーで開けます。",
            "Version {version} is available. Open the release notes in your browser to see what changed and how to get it."),
        new("gui.update.state.current", "最新の版です。次の確認は {time} 以降です。", "You have the newest version. The next check is after {time}."),
        new("gui.update.state.scheduled", "最初の確認は、起動の少しあとに行います。", "The first check is made a little after the app starts."),
        new("gui.update.savefailed", "更新の設定を保存できませんでした。確認はしません。", "The update setting could not be saved. Nothing is checked."),

        // --- the quiet notice on the status bar ---
        new("gui.update.notice", "新しい版 {version} があります。内容は設定の「更新」で開けます。", "Version {version} is available. Open its notes under Updates in Settings."),

        // --- one reason per UpdateRefusal (except None). The first group (operational) goes after 「確認できませんでした:」, the second (a release's own rules) after 「確かめられなかったので使いません:」 ---
        new("gui.update.refusal.feedunreadable", "GitHub の答えを読み取れませんでした", "GitHub's answer could not be read"),
        new("gui.update.refusal.bodytoolarge", "GitHub から届いたデータが、想定より大きかったため受け取りを止めました", "The data from GitHub was larger than expected, so it was not accepted"),
        new("gui.update.refusal.redirecthostnotallowed", "GitHub が想定外の場所へ案内したため、止めました", "GitHub pointed to an unexpected address, so the request was stopped"),
        new("gui.update.refusal.redirectdowngrade", "暗号化されない通信へ案内されたため、止めました", "The request was sent to an unencrypted address, so it was stopped"),
        new("gui.update.refusal.redirecttoomany", "案内が何度も続いたため、止めました", "The request was passed on too many times, so it was stopped"),
        new("gui.update.refusal.redirectcredentials", "ユーザー名やパスワード付きの場所へ案内されたため、止めました", "The request was sent to an address with a user name or password, so it was stopped"),
        new("gui.update.refusal.redirectmissinglocation", "案内先が示されなかったため、止めました", "No address to go to was given, so the request was stopped"),
        new("gui.update.refusal.httperror", "GitHub がエラーを返しました", "GitHub returned an error"),
        new("gui.update.refusal.networkfailure", "GitHub に接続できませんでした", "Could not connect to GitHub"),
        new("gui.update.refusal.stateunavailable", "確認の記録をこのPCに書き込めません", "The record of checks cannot be written on this PC"),
        new("gui.update.refusal.anothercheckrunning", "ほかの確認が動いています", "Another check is running"),
        new("gui.update.refusal.ownversionunknown", "このアプリの版を読み取れません", "This app's own version cannot be read"),
        new("gui.update.refusal.gatemissing", "PCミエルのフォルダーに PCMieru.gate.txt（起動用のファイル）がありません", "PCMieru.gate.txt (the start file) is not in the PC Mieru folder"),

        new("gui.update.refusal.draftrelease", "公開前の下書きの版です", "The release is only a draft"),
        new("gui.update.refusal.prereleaserelease", "試験的に公開された版（プレリリース）です", "The release is marked as a pre-release"),
        new("gui.update.refusal.tagnotstable", "版の名前が X.Y.Z の形ではありません", "The version name is not in the plain X.Y.Z form"),
        new("gui.update.refusal.releasepagemismatch", "リリースのページが、PCミエルの公式のページと合いません", "The release page is not PC Mieru's official page for that version"),
        new("gui.update.refusal.blockedtag", "この版は、このPCで以前うまく動かなかったので、もう勧めません", "This version did not work on this PC before, so it is not offered again"),
        new("gui.update.refusal.assetmissing", "必要なファイルが、リリースに足りません", "A required file is missing from the release"),
        new("gui.update.refusal.assetduplicate", "同じ名前のファイルが、リリースに 2 つ以上あります", "The release has the same file more than once"),
        new("gui.update.refusal.assetnotready", "リリースのファイルが、まだ揃っていません", "The files of the release are not all there yet"),
        new("gui.update.refusal.asseturlmismatch", "ファイルの置き場所が、公式の場所と合いません", "A file is not stored at the official address"),
        new("gui.update.refusal.assettoolarge", "確かめるためのファイルが、決めた大きさを超えています", "A file used for verification is larger than allowed"),
        new("gui.update.refusal.hashlistline", "照合用の一覧（SHA256.txt）に、必要な行が 1 つずつ揃っていません", "The checking list (SHA256.txt) does not have exactly one line for each required file"),
        new("gui.update.refusal.digestmismatch", "ファイルの内容が、照合用の一覧と合いません", "A file does not match the checking list"),
        new("gui.update.refusal.manifesthashmismatch", "ファイルの目録（マニフェスト）が、照合用の一覧にあるものと違います", "The file list (manifest) is not the one the checking list names"),
        new("gui.update.refusal.manifestinvalid", "ファイルの目録（マニフェスト）の形式が正しくありません", "The file list (manifest) is not in a valid form"),
        new("gui.update.refusal.manifestversionmismatch", "ファイルの目録（マニフェスト）の版が、リリースの版と違います", "The file list (manifest) is for another version than the release"),
    ];
}

/// <summary>What the status line of the settings page says and in which tone. A pure function of what the controller knows, so a test needs no window.</summary>
internal readonly record struct UpdateLine(string Text, CellTone Tone);

/// <summary>The words of the update check that are built from data (a mode, a refusal, a placement, a time).</summary>
internal static class UpdateText
{
    public static string ModeName(UpdateMode mode) => Strings.Get("gui.update.mode." + mode.ToString().ToLowerInvariant());

    public static string RefusalText(UpdateRefusal reason) =>
        reason == UpdateRefusal.None ? string.Empty : Strings.Get("gui.update.refusal." + reason.ToString().ToLowerInvariant());

    /// <summary>The value of the "Install folder" row: "Writable", or that updates can only be announced here and why.</summary>
    public static string PlacementText(Placement placement) => placement.CanSwap
        ? Strings.Get("gui.update.placement.canswap")
        : Strings.Format("gui.update.placement.cannotwrite", Fmt.Dict(("reason", Strings.Get("gui.update.placement." + placement.Kind.ToString().ToLowerInvariant()))));

    /// <summary>
    /// A moment as the person's own settings write it (local time): without the year when it is this year (<paramref name="now"/>), the short date and time otherwise.
    /// The month-and-day and time patterns are the culture's own, so Japanese reads 「11月22日 14:30」 and English "November 22 2:30 PM".
    /// </summary>
    public static string When(DateTimeOffset moment, DateTimeOffset now)
    {
        var culture = CultureInfo.CurrentCulture;
        var local = moment.ToLocalTime();
        if (local.Year == now.ToLocalTime().Year)
        {
            return local.ToString(culture.DateTimeFormat.MonthDayPattern + " " + culture.DateTimeFormat.ShortTimePattern, culture);
        }

        return local.ToString("g", culture);
    }

    /// <summary>
    /// The end of the wait that GitHub asked for, when one is in force at <paramref name="now"/>: the one this run was told, or the one the state file remembers (which
    /// is not believed when it is further away than <see cref="UpdateSchedule.MaxBackoff"/>, as the schedule does not believe it).
    /// </summary>
    public static DateTimeOffset? BackoffEnd(UpdateCheck? last, UpdateStatus status, DateTimeOffset now)
    {
        DateTimeOffset? end = null;
        if (last is { Kind: UpdateCheckKind.Waiting, WaitUntil: { } held } && held > now)
        {
            end = held;
        }

        if (status.BackoffUntilUtc is { } until && until > now && until - now <= UpdateSchedule.MaxBackoff && (end is null || until > end))
        {
            end = until;
        }

        return end;
    }

    /// <summary>
    /// The one line that says where the check stands. <paramref name="allowed"/> is false when the mode would allow a check but this run does not
    /// (a choice that could not be saved). Order: a check in progress, the mode (Ask, Off), a wait GitHub asked for, the last answer (another check running,
    /// a failure, a release that was refused), a candidate, the last check. A failure that judged no release leaves a remembered candidate on file (the candidate row
    /// and the notice show it), but the line says the newest thing: that the check did not finish. No tone here is <see cref="CellTone.Accent"/>: it is link blue,
    /// and nothing in this line can be clicked.
    /// </summary>
    public static UpdateLine Describe(UpdateMode mode, bool allowed, bool checking, UpdateCheck? last, UpdateStatus status, AppVersion running, DateTimeOffset now)
    {
        if (checking)
        {
            return new UpdateLine(Strings.Get("gui.update.state.checking"), CellTone.Normal);
        }

        if (mode == UpdateMode.Ask)
        {
            return new UpdateLine(Strings.Get("gui.update.state.ask"), CellTone.Dim);
        }

        if (mode == UpdateMode.Off)
        {
            return new UpdateLine(Strings.Get("gui.update.state.off"), CellTone.Dim);
        }

        if (!allowed)
        {
            return new UpdateLine(Strings.Get("gui.update.savefailed"), CellTone.Warning);
        }

        if (BackoffEnd(last, status, now) is { } until)
        {
            return new UpdateLine(Strings.Format("gui.update.state.waiting", Fmt.Dict(("time", When(until, now)))), CellTone.Dim);
        }

        if (last is { Kind: UpdateCheckKind.Refused })
        {
            return Refused(last.Refusal, last.RefusedVersion);
        }

        if (last is { Kind: UpdateCheckKind.Candidate, Version: { } found } && found > running)
        {
            return Candidate(found);
        }

        if (status.LastRefusal is not UpdateRefusal.None and not UpdateRefusal.AnotherCheckRunning)
        {
            return Refused(status.LastRefusal, status.RefusedVersion);
        }

        if (status.CandidateVersion is { } stored && stored > running)
        {
            return Candidate(stored);
        }

        if (status.LastCheckUtc is { } checkedAt)
        {
            var next = checkedAt + UpdateSchedule.Interval;
            return new UpdateLine(Strings.Format("gui.update.state.current", Fmt.Dict(("time", When(next > now ? next : now, now)))), CellTone.Normal);
        }

        return new UpdateLine(Strings.Get("gui.update.state.scheduled"), CellTone.Dim);
    }

    /// <summary>
    /// Why 「今すぐ確認」 is off, as the short line under it; empty when it is on. Ask and Off: choose a mode; a choice that could not be saved; a wait GitHub asked for
    /// (until when); a check already running.
    /// </summary>
    public static string CheckNowReason(UpdateMode mode, bool allowed, bool checking, UpdateCheck? last, UpdateStatus status, DateTimeOffset now)
    {
        if (mode is UpdateMode.Ask or UpdateMode.Off)
        {
            return Strings.Get("gui.update.reason.choosemode");
        }

        if (!allowed)
        {
            return Strings.Get("gui.update.reason.notsaved");
        }

        if (BackoffEnd(last, status, now) is { } until)
        {
            return Strings.Format("gui.update.reason.backoff", Fmt.Dict(("time", When(until, now))));
        }

        return checking ? Strings.Get("gui.update.reason.checking") : string.Empty;
    }

    /// <summary>Why 「更新内容をブラウザーで開く」 is off: there is no newer version to open the notes of; empty when it is on.</summary>
    public static string NotesReason(bool hasCandidate) => hasCandidate ? string.Empty : Strings.Get("gui.update.reason.nocandidate");

    private static UpdateLine Refused(UpdateRefusal reason, AppVersion? version)
    {
        if (reason == UpdateRefusal.AnotherCheckRunning)
        {
            return new UpdateLine(Strings.Get("gui.update.state.otherrunning"), CellTone.Dim);
        }

        var because = Fmt.Dict(("reason", RefusalText(reason)));
        if (UpdateRefusals.JudgedARelease(reason))
        {
            var line = version is { } v
                ? Strings.Format("gui.update.state.refused", Fmt.Dict(("version", v.ToString()), ("reason", RefusalText(reason))))
                : Strings.Format("gui.update.state.refusednoversion", because);
            return new UpdateLine(line, CellTone.Warning);
        }

        return new UpdateLine(Strings.Format("gui.update.state.failed", because), CellTone.Warning);
    }

    private static UpdateLine Candidate(AppVersion version) =>
        new(Strings.Format("gui.update.state.candidate", Fmt.Dict(("version", version.ToString()))), CellTone.Normal);
}
