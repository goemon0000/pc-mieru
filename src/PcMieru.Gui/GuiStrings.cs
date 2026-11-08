namespace PcMieru.Gui;

/// <summary>Every "gui." string, ja and en side by side. Registered once at start-up (Program.cs).</summary>
internal static class GuiStrings
{
    private static readonly StringEntry[] Legacy =
    [
        new("gui.nav.label", "ナビゲーション", "Navigation"),
        new("gui.nav.processes", "プロセス", "Processes"),
        new("gui.nav.performance", "パフォーマンス", "Performance"),
        new("gui.nav.services", "サービス", "Services"),
        new("gui.nav.startup", "スタートアップ", "Start-up"),
        new("gui.nav.settings", "設定", "Settings"),

        new("gui.status.updated", "更新 {time}", "Updated {time}"),
        new("gui.status.failed", "失敗しました: {error}", "Failed: {error}"),
        new("gui.fmt.uptime", "{days} 日 {time}", "{days} d {time}"),


        new("gui.perf.col.metric", "項目", "Item"),
        new("gui.perf.col.value", "値", "Value"),
        new("gui.perf.observations", "いま気になる点", "What stands out right now"),
        new("gui.perf.cpu", "CPU", "CPU"),
        new("gui.perf.memory", "メモリ", "Memory"),
        new("gui.perf.commit", "コミット済みメモリ", "Committed memory"),
        new("gui.perf.read", "読み取り", "Read"),
        new("gui.perf.write", "書き込み", "Write"),
        new("gui.perf.send", "送信", "Send"),
        new("gui.perf.receive", "受信", "Receive"),
        new("gui.perf.uptime", "稼働時間", "Uptime"),
        new("gui.perf.none", "いまのところ目立った点はありません", "Nothing stands out right now"),
        new("gui.perf.certainty.fact", "[事実]", "[Fact]"),
        new("gui.perf.certainty.candidate", "[可能性]", "[Possible]"),

        new("gui.hw.refresh", "再読み込み", "Reload"),
        new("gui.hw.loading", "読み込み中...", "Loading..."),

        new("gui.svc.col.name", "名前", "Name"),
        new("gui.svc.col.display", "表示名", "Display name"),
        new("gui.svc.col.state", "状態", "Status"),
        new("gui.svc.col.start", "開始の種類", "Start type"),
        new("gui.svc.col.pid", "PID", "PID"),
        new("gui.svc.refresh", "再読み込み", "Reload"),
        new("gui.svc.state.stopped", "停止", "Stopped"),
        new("gui.svc.state.startpending", "開始中", "Starting"),
        new("gui.svc.state.stoppending", "停止中", "Stopping"),
        new("gui.svc.state.running", "実行中", "Running"),
        new("gui.svc.state.continuepending", "再開中", "Resuming"),
        new("gui.svc.state.pausepending", "一時停止中", "Pausing"),
        new("gui.svc.state.paused", "一時停止", "Paused"),
        new("gui.svc.state.unknown", "不明", "Unknown"),
        new("gui.svc.start.automatic", "自動", "Automatic"),
        new("gui.svc.start.automaticdelayed", "自動（遅延開始）", "Automatic (delayed)"),
        new("gui.svc.start.manual", "手動", "Manual"),
        new("gui.svc.start.disabled", "無効", "Disabled"),
        new("gui.svc.start.boot", "ブート", "Boot"),
        new("gui.svc.start.system", "システム", "System"),
        new("gui.svc.start.unknown", "不明", "Unknown"),

        new("gui.startup.col.name", "名前", "Name"),
        new("gui.startup.col.provider", "場所", "Location"),
        new("gui.startup.col.state", "状態", "Status"),
        new("gui.startup.col.command", "コマンド", "Command"),
        new("gui.startup.refresh", "再読み込み", "Reload"),
        new("gui.startup.state.enabled", "有効", "Enabled"),
        new("gui.startup.state.disabled", "無効", "Disabled"),
        new("gui.startup.state.unknown", "不明", "Unknown"),

        new("gui.diag.seconds", "観察する秒数", "Seconds to watch"),
        new("gui.diag.start", "診断を始める", "Start"),
        new("gui.diag.cancel", "中止", "Cancel"),
        new("gui.diag.copy", "レポートをコピー", "Copy report"),
        new("gui.diag.running", "観察しています...", "Watching..."),
        new("gui.diag.done", "{seconds} 秒間の観察が終わりました", "Finished watching for {seconds} s"),
        new("gui.diag.cancelled", "中止しました", "Cancelled"),
        new("gui.diag.hint", "「診断を始める」を押すと、PC の動きをしばらく見て気になる点を挙げます", "Press Start to watch the PC for a while and list what stands out"),
        new("gui.diag.copied", "レポートをコピーしました（個人情報は伏せています）", "Report copied (personal data is hidden)"),

        new("gui.settings.language", "表示言語", "Language"),
        new("gui.settings.theme", "テーマ", "Theme"),
        new("gui.settings.theme.system", "Windows の設定に合わせる", "Follow Windows"),
        new("gui.settings.theme.light", "ライト", "Light"),
        new("gui.settings.theme.dark", "ダーク", "Dark"),
        new("gui.settings.about", "PCミエル {version}", "PC Mieru {version}"),
    ];

    public static readonly StringEntry[] All = [.. Legacy, .. ChromeStrings.All, .. ProcessStrings.All];
}
