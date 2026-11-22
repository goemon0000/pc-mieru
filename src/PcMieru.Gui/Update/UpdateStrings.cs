using System.Globalization;
using PcMieru.Update.Feed;

namespace PcMieru.Gui;

/// <summary>
/// The words of the update check (DESIGN-B 3.1, 4): the first-run choice, the settings section, the status-bar notice and the reason of every refusal
/// (one string per <see cref="UpdateRefusal"/>, kept in step with the enum by a test).
/// </summary>
internal static class UpdateStrings
{
    public static readonly StringEntry[] All =
    [
        // --- the four modes (one key per UpdateMode member: a test keeps the two in step) ---
        new("gui.update.mode.ask", "あとで選ぶ（確認しません）", "Choose later (no checks)"),
        new("gui.update.mode.automatic", "自動", "Automatic"),
        new("gui.update.mode.notify", "知らせるだけ", "Notify only"),
        new("gui.update.mode.off", "切る", "Off"),

        // --- the first-run choice ---
        new("gui.update.dialog.title", "更新の確認", "Checking for updates"),
        new(
            "gui.update.dialog.lead",
            "PCミエルの新しい版が出ていないか、GitHub に問い合わせてよいか選んでください。問い合わせで送るのはアプリの名前と版だけです。このPCの情報や測定値は送りません。問い合わせると、GitHub にはこのPCのIPアドレスが分かります。",
            "Choose whether PC Mieru may ask GitHub if a new version is out. The question carries only the app name and its version, nothing about this PC and no measurements. GitHub does see this PC's IP address when it is asked."),
        new(
            "gui.update.dialog.automatic",
            "新しい版が見つかったら、確認なしで入れ替えます。署名のない版も、確認なしで入れ替えます。",
            "When a new version is found it is put in place without asking. Versions that are not signed are also installed without asking."),
        new(
            "gui.update.dialog.notify",
            "新しい版があることを知らせます。入れ替えは、あなたが選ぶまでしません。",
            "Tells you when a new version exists. Nothing is put in place until you choose to."),
        new(
            "gui.update.dialog.off",
            "更新の確認をしません。GitHub には何も問い合わせません。",
            "Never checks for updates. GitHub is not asked anything."),
        new("gui.update.dialog.ok", "決定", "Confirm"),
        new(
            "gui.update.dialog.later",
            "何も選ばずに閉じると、確認はしません。次に開いたときにまた聞きます。設定の「更新」で、いつでも変えられます。",
            "If you close this without choosing, nothing is checked, and you are asked again the next time the app opens. You can change it any time under Updates in Settings."),
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
        new("gui.update.placement.label", "この配置", "This install"),
        new("gui.update.placement.canswap", "書き込めます", "Writable"),
        new("gui.update.placement.readonly", "通知だけ（この場所には書き込めません）", "Notify only (this folder is not writable)"),
        new("gui.update.placement.elevated", "通知だけ（管理者として開いています）", "Notify only (running as administrator)"),
        new("gui.update.placement.notfixeddrive", "通知だけ（固定ディスクではない場所にあります）", "Notify only (not on a fixed disk)"),
        new("gui.update.placement.note", "この配置には書き込めないため、更新を通知します", "This location cannot be written to, so updates are only announced"),
        new("gui.update.candidate.label", "見つかった新しい版", "Newer version"),
        new("gui.update.candidate.none", "なし", "None"),
        new("gui.update.status.label", "状態", "Status"),
        new("gui.update.checknow", "今すぐ確認", "Check now"),
        new("gui.update.notes", "更新内容を開く", "Open the release notes"),
        new("gui.update.checknow.tip", "いま GitHub に問い合わせます。「知らせるだけ」か「自動」のときだけ使えます", "Asks GitHub now. Available only when the check is set to Notify only or Automatic"),
        new("gui.update.notes.tip", "見つかった版のリリースのページを、ブラウザーで開きます", "Opens the release page of the version found in your browser"),

        // --- what the status line says ---
        new("gui.update.state.checking", "確認しています…", "Checking…"),
        new("gui.update.state.ask", "まだ選んでいないため、確認しません。上の「更新の確認」で選べます。", "Nothing is checked until you choose above."),
        new("gui.update.state.off", "更新の確認は切ってあります。", "The update check is off."),
        new("gui.update.state.waiting", "GitHub から待つよう求められたため、{time} まで確認しません。", "GitHub asked to wait, so nothing is checked until {time}."),
        new("gui.update.state.refused", "新しい版を受け付けませんでした: {reason}", "A newer version was not accepted: {reason}"),
        new("gui.update.state.candidate", "新しい版 {version} があります。", "Version {version} is available."),
        new("gui.update.state.current", "最新の版です。次の確認は {time} 以降です。", "You have the newest version. The next check is after {time}."),
        new("gui.update.state.scheduled", "最初の確認は、起動の少しあとに行います。", "The first check is made a little after the app starts."),
        new("gui.update.savefailed", "更新の設定を保存できませんでした。確認はしません。", "The update setting could not be saved. Nothing is checked."),

        // --- the quiet notice on the status bar ---
        new("gui.update.notice", "新しい版 {version} があります。内容は設定の「更新」で開けます。", "Version {version} is available. Open its notes under Updates in Settings."),

        // --- one reason per UpdateRefusal (except None) ---
        new("gui.update.refusal.feedunreadable", "GitHub の答えを読み取れませんでした", "GitHub's answer could not be read"),
        new("gui.update.refusal.draftrelease", "下書きの版は受け付けません", "A draft release is not accepted"),
        new("gui.update.refusal.prereleaserelease", "先行公開（プレリリース）の版は受け付けません", "A pre-release is not accepted"),
        new("gui.update.refusal.tagnotstable", "版の名前が X.Y.Z の形ではありません", "The version tag is not in the plain X.Y.Z form"),
        new("gui.update.refusal.releasepagemismatch", "リリースのページが、公式の場所と合いません", "The release page does not match the official repository"),
        new("gui.update.refusal.blockedtag", "このPCでうまく動かなかった版なので、もう勧めません", "This version failed on this PC, so it is not offered again"),
        new("gui.update.refusal.assetmissing", "必要なファイルが、リリースに足りません", "A required file is missing from the release"),
        new("gui.update.refusal.assetduplicate", "同じ名前のファイルが、リリースに 2 つ以上あります", "A required file appears more than once in the release"),
        new("gui.update.refusal.assetnotready", "リリースのファイルが、まだ揃っていません", "A file of the release is not ready yet"),
        new("gui.update.refusal.asseturlmismatch", "ファイルの置き場所が、公式の場所と合いません", "A file's address is not the official one"),
        new("gui.update.refusal.assettoolarge", "ファイルが、決めた大きさを超えています", "A file is larger than allowed"),
        new("gui.update.refusal.hashlistline", "ハッシュの一覧（SHA256.txt）に、必要な行が 1 つずつありません", "SHA256.txt does not have exactly one line for each required file"),
        new("gui.update.refusal.digestmismatch", "ファイルのハッシュ値が、一覧と合いません", "A file's hash does not match the list"),
        new("gui.update.refusal.manifesthashmismatch", "マニフェストが、ハッシュの一覧にあるものと違います", "The manifest is not the one the hash list names"),
        new("gui.update.refusal.manifestinvalid", "マニフェストの形式が正しくありません", "The manifest is not valid"),
        new("gui.update.refusal.manifestversionmismatch", "マニフェストの版が、リリースの版と違います", "The manifest is for another version than the release"),
        new("gui.update.refusal.bodytoolarge", "受け取ったデータが、決めた大きさを超えています", "The data received is larger than allowed"),
        new("gui.update.refusal.redirecthostnotallowed", "想定外の場所への転送があったため、止めました", "A redirect to an unexpected address was stopped"),
        new("gui.update.refusal.redirectdowngrade", "暗号化されない通信への転送があったため、止めました", "A redirect to an unencrypted address was stopped"),
        new("gui.update.refusal.redirecttoomany", "転送が多すぎるため、止めました", "Too many redirects, so it was stopped"),
        new("gui.update.refusal.redirectcredentials", "ユーザー名やパスワード付きの転送があったため、止めました", "A redirect carrying a user name or password was stopped"),
        new("gui.update.refusal.redirectmissinglocation", "転送先が示されていないため、止めました", "A redirect without a destination was stopped"),
        new("gui.update.refusal.httperror", "GitHub がエラーを返しました", "GitHub returned an error"),
        new("gui.update.refusal.networkfailure", "GitHub に接続できませんでした", "Could not connect to GitHub"),
        new("gui.update.refusal.stateunavailable", "確認の記録を書き込めないため、確認できません", "The check cannot run because its record cannot be written"),
        new("gui.update.refusal.anothercheckrunning", "別の確認が動いています", "Another check is running"),
        new("gui.update.refusal.ownversionunknown", "このアプリの版が読み取れないため、確認しません", "This app's own version cannot be read, so nothing is checked"),
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

    public static string PlacementText(Placement placement) => Strings.Get("gui.update.placement." + placement.Kind.ToString().ToLowerInvariant());

    /// <summary>A moment as the person's own settings write it (local time, the short date and time).</summary>
    public static string When(DateTimeOffset moment) => moment.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>
    /// The one line that says where the check stands. <paramref name="allowed"/> is false when the mode would allow a check but this run does not
    /// (a choice that could not be saved). Order: a check in progress, the mode (Ask, Off), a wait GitHub asked for, a refusal, a candidate, the last check.
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
            return new UpdateLine(Strings.Get("gui.update.savefailed"), CellTone.Accent);
        }

        if (last is { Kind: UpdateCheckKind.Waiting, WaitUntil: { } held } && held > now)
        {
            return new UpdateLine(Strings.Format("gui.update.state.waiting", Fmt.Dict(("time", When(held)))), CellTone.Accent);
        }

        if (status.BackoffUntilUtc is { } until && until > now)
        {
            return new UpdateLine(Strings.Format("gui.update.state.waiting", Fmt.Dict(("time", When(until)))), CellTone.Accent);
        }

        if (last is { Kind: UpdateCheckKind.Refused })
        {
            return Refused(last.Refusal);
        }

        if (last is { Kind: UpdateCheckKind.Candidate, Version: { } found } && found > running)
        {
            return Candidate(found);
        }

        if (status.CandidateVersion is { } stored && stored > running)
        {
            return Candidate(stored);
        }

        if (status.LastRefusal != UpdateRefusal.None)
        {
            return Refused(status.LastRefusal);
        }

        if (status.LastCheckUtc is { } checkedAt)
        {
            var next = checkedAt + UpdateSchedule.Interval;
            return new UpdateLine(Strings.Format("gui.update.state.current", Fmt.Dict(("time", When(next > now ? next : now)))), CellTone.Normal);
        }

        return new UpdateLine(Strings.Get("gui.update.state.scheduled"), CellTone.Dim);
    }

    private static UpdateLine Refused(UpdateRefusal reason) =>
        new(Strings.Format("gui.update.state.refused", Fmt.Dict(("reason", RefusalText(reason)))), CellTone.Accent);

    private static UpdateLine Candidate(AppVersion version) =>
        new(Strings.Format("gui.update.state.candidate", Fmt.Dict(("version", version.ToString()))), CellTone.Accent);
}
