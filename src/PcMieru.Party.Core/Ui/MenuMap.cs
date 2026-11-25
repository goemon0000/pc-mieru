namespace PcMieru.Party.Ui;

/// <summary>
/// 窓の上に並べるタブ。<see cref="None"/> はどのタブも開いていない（いつもの盤だけの姿）。
/// **保存しない。** 起動したらいつも閉じた姿から始まる（保存の形を変えないため）。
/// </summary>
public enum SettingsTab { None, View, Startup, Checks, Data }

/// <summary>右クリックの献立の項目。タブの側も同じ物を押す（中身の処理は窓の側に 1 つだけ）。</summary>
public enum MenuCommand
{
    Topmost,
    RunAtLogin,
    Language,
    LabelsLevelOnly,
    LabelsNameAndLevel,
    LabelsNone,
    ResetStory,
    ResetLevels,
    ResetAll,
    /// <summary>「更新」の小献立。中身（いまの版・確認・設定）は UpdateController が持つ。</summary>
    Updates,
    Checks,
    Quit,
}

/// <summary>献立の一段をまとめる名前（小献立）。null ならいちばん上の段に直接並ぶ。</summary>
public enum MenuGroup { None, Labels, Reset }

/// <param name="Tab">どのタブに出すか。<see cref="SettingsTab.None"/> はタブに出さない（窓の ✕ と献立だけ）。</param>
/// <param name="Group">献立のどの小献立に入るか。</param>
/// <param name="SeparatorBefore">献立でこの項目の前に区切りを入れる。</param>
/// <param name="Destructive">取り返しがつかない。**必ず確認を挟む**（献立でもタブでも）。</param>
public sealed record MenuEntry(MenuCommand Command, SettingsTab Tab, MenuGroup Group = MenuGroup.None,
                               bool SeparatorBefore = false, bool Destructive = false);

/// <summary>
/// 右クリックの献立とタブの対応表。**献立もタブもこの表から組み立てる。**
/// 片方にだけ項目を足すと、もう片方から辿れない設定ができる（右クリックに慣れた人とタブから入る人で食い違う）。
/// 並びは v0.7.3 までの献立の並びのまま（右クリックの見た目は変えない）。
/// </summary>
public static class MenuMap
{
    public static IReadOnlyList<MenuEntry> Entries { get; } = new MenuEntry[]
    {
        new(MenuCommand.Topmost,            SettingsTab.View),
        new(MenuCommand.RunAtLogin,         SettingsTab.Startup),
        new(MenuCommand.Language,           SettingsTab.View),
        new(MenuCommand.LabelsLevelOnly,    SettingsTab.View, MenuGroup.Labels),
        new(MenuCommand.LabelsNameAndLevel, SettingsTab.View, MenuGroup.Labels),
        new(MenuCommand.LabelsNone,         SettingsTab.View, MenuGroup.Labels),
        new(MenuCommand.ResetStory,         SettingsTab.Data, MenuGroup.Reset, Destructive: true),
        new(MenuCommand.ResetLevels,        SettingsTab.Data, MenuGroup.Reset, Destructive: true),
        new(MenuCommand.ResetAll,           SettingsTab.Data, MenuGroup.Reset, Destructive: true),
        new(MenuCommand.Updates,            SettingsTab.Startup),
        new(MenuCommand.Checks,             SettingsTab.Checks, SeparatorBefore: true),
        new(MenuCommand.Quit,               SettingsTab.None,   SeparatorBefore: true),
    };

    /// <summary>タブの並び（左から）。<see cref="SettingsTab.None"/> は「ようす」（盤だけの姿に戻す）として先頭に置く。</summary>
    public static IReadOnlyList<SettingsTab> Tabs { get; } = new[]
    {
        SettingsTab.None, SettingsTab.View, SettingsTab.Startup, SettingsTab.Checks, SettingsTab.Data,
    };

    public static MenuEntry Entry(MenuCommand command) => Entries.First(e => e.Command == command);

    public static SettingsTab TabOf(MenuCommand command) => Entry(command).Tab;

    /// <summary>そのタブに出す項目。献立と同じ並び。</summary>
    public static IEnumerable<MenuEntry> InTab(SettingsTab tab) => Entries.Where(e => e.Tab == tab);

    /// <summary>
    /// 押したタブの次の姿。**開いているタブをもう一度押すと畳む**（盤だけの姿に戻る）。
    /// </summary>
    public static SettingsTab Toggle(SettingsTab current, SettingsTab pressed) =>
        pressed == current ? SettingsTab.None : pressed;

    /// <summary>
    /// Ctrl+数字 で選ぶタブ（1 = ようす, 2 = 表示 …）。範囲外なら null。
    /// </summary>
    public static SettingsTab? FromShortcut(int digit) =>
        digit >= 1 && digit <= Tabs.Count ? Tabs[digit - 1] : null;
}
