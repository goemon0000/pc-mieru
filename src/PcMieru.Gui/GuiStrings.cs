namespace PcMieru.Gui;

/// <summary>
/// Every "gui." string, ja and en side by side, gathered from one table per area (window frame, each page). Registered
/// once at start-up (Program.cs). A key may be defined in one place only; a test scans for duplicates and for keys the
/// code uses but nobody defines.
/// </summary>
internal static class GuiStrings
{
    private static readonly StringEntry[] Shell =
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
    ];

    public static readonly StringEntry[] All =
    [
        .. Shell,
        .. ChromeStrings.All,
        .. ProcessStrings.All,
        .. OverviewStrings.All,
        .. PerfStrings.All,
        .. StartupStrings.All,
        .. ServiceStrings.All,
        .. UsersStrings.All,
        .. HardwareStrings.All,
        .. DiagnoseStrings.All,
        .. SettingsStrings.All,
    ];
}
