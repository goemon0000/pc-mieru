namespace PcMieru.Gui;

/// <summary>Strings of the window frame (navigation, status bar) and the words every page shares.</summary>
internal static class ChromeStrings
{
    public static readonly StringEntry[] All =
    [
        new("gui.nav.overview", "概要", "Overview"),
        new("gui.nav.users", "ユーザー", "Users"),
        new("gui.nav.hardware", "PCの構成", "PC specs"),
        new("gui.nav.diagnose", "診断レポート", "Report"),

        new("gui.interval.label", "更新間隔", "Update interval"),
        new("gui.interval.500", "0.5 秒", "0.5 s"),
        new("gui.interval.1000", "1 秒", "1 s"),
        new("gui.interval.2000", "2 秒", "2 s"),
        new("gui.interval.5000", "5 秒", "5 s"),
        new("gui.pause.pause", "更新を一時停止", "Pause updates"),
        new("gui.pause.resume", "更新を再開", "Resume updates"),
        new("gui.pause.tip", "数値の更新を止めます（Space キーでも切り替わります）", "Stops the numbers from updating (the Space key does the same)"),
        new("gui.status.paused", "更新を一時停止中", "Updates paused"),

        new("gui.common.ok", "OK", "OK"),
        new("gui.common.cancel", "キャンセル", "Cancel"),
        new("gui.common.close", "閉じる", "Close"),
        new("gui.common.copy", "コピー", "Copy"),
        new("gui.common.copied", "コピーしました", "Copied"),
        new("gui.common.save", "保存", "Save"),
        new("gui.common.reload", "再読み込み", "Reload"),
        new("gui.common.loading", "読み込み中...", "Loading..."),
        new("gui.common.na", "取得できません", "Not available"),
        new("gui.common.na.tip", "取得できません: {status}", "Not available: {status}"),
        new("gui.common.na.source", "取得元: {source}", "Source: {source}"),
        new("gui.common.nothing", "なし", "None"),

        new("gui.op.code.Ok", "完了しました", "Done"),
        new("gui.op.code.Error", "失敗しました", "Failed"),
        new("gui.op.code.InvalidArguments", "指定が正しくありません", "The request is not valid"),
        new("gui.op.code.NotFound", "見つかりません", "Not found"),
        new("gui.op.code.Denied", "権限がありません（管理者の権限が要る操作です）", "Access denied (this needs administrator rights)"),
        new("gui.op.code.Protected", "守られているため操作できません", "Protected, so it cannot be changed"),
        new("gui.op.code.MultipleMatches", "同じ名前が複数あります", "More than one match"),
        new("gui.op.code.PartialSuccess", "一部だけ完了しました", "Only partly done"),
        new("gui.op.code.Gone", "すでに終了していました", "It had already ended"),
        new("gui.op.code.Cancelled", "中止しました", "Cancelled"),
        new("gui.op.code.Unsupported", "この環境では使えません", "Not supported here"),
        new("gui.op.code.NotImplemented", "まだ使えません", "Not available yet"),
        new("gui.op.result", "{target}: {result}", "{target}: {result}"),
        new("gui.op.result.detail", "{target}: {result}（{message}）", "{target}: {result} ({message})"),
    ];
}
