using System.Globalization;

namespace PcMieru.Party.I18n;

/// <summary>
/// 画面の文言。物語の本文は含まない（あれは story.json 側）。
/// 既定は OS の表示言語。日本語なら ja、それ以外は en。
/// 一度でも切り替えたら、その選択を保存して以後はそれに従う。
/// </summary>
public static class Strings
{
    public static string Lang { get; set; } = DetectDefault();

    public static string DetectDefault() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "ja" : "en";

    private static string T(string ja, string en) => Lang == "en" ? en : ja;

    public static string Close        => T("終了", "Close");
    public static string SizeTip      => T("大きさ（1x / 2x / 3x に揃える）", "Size (snap to 1x / 2x / 3x)");
    public static string TopmostTip   => T("常に前面に表示", "Always on top");
    public static string LangTip      => T("言語を切り替える", "Switch language");
    public static string ExpandTip    => T("各パーツの稼働状況", "Per-part status");
    public static string CollapseTip  => T("閉じる", "Collapse");
    public static string ResizeTip    => T("ドラッグで大きさを変える", "Drag to resize");
    public static string RunAtLogin   => T("Windows起動時に開く", "Start with Windows");
    public static string LabelMenu    => T("名札", "Labels");
    public static string LabelLevelOnly    => T("レベルだけ", "Level only");
    public static string LabelNameAndLevel => T("名前とレベル", "Name and level");
    public static string LabelNone         => T("出さない", "Hide");

    // --- 窓の上のタブ（右クリックの献立と同じ物を、見える所に並べる） ---
    public static string TabBoard     => T("ようす", "Party");
    public static string TabView      => T("表示", "View");
    public static string TabStartup   => T("起動・更新", "Startup");
    public static string TabChecks    => T("点検", "Checkup");
    public static string TabData      => T("データ", "Data");
    public static string TabsTip      => T("Ctrl+1〜5 でも選べます。右クリックの献立も今までどおり使えます",
                                           "Ctrl+1–5 also work. The right-click menu still works as before");
    public static string LanguageHeader => T("言語", "Language");
    public static string ChecksAbout  => T("CPU の負荷テスト・システムチェック・記憶媒体の点検を別の窓で開きます。開いただけでは何も動きません。",
                                           "Opens load tests, the system check and the storage check in a separate window. Nothing runs until you press a button there.");
    public static string ChecksOpen   => T("点検の窓を開く", "Open checkup");
    public static string DataAbout    => T("取り消せません。押すと確認が出ます。窓の位置・言語・更新の設定は残ります。",
                                           "This can't be undone; you'll be asked to confirm. Window position, language and update settings are kept.");

    public static string ResetMenu    => T("最初から", "Start over");
    public static string ChecksMenu   => T("点検…（負荷テスト・システムチェック）", "Checkup… (load test, system check)");
    public static string ResetStory   => T("物語だけ最初から", "Story only");
    public static string ResetLevels  => T("レベルだけ最初から", "Levels only");
    public static string ResetAll     => T("両方とも最初から", "Both");
    public static string ResetTitle   => T("最初からやり直しますか", "Start over?");

    public static string ResetStoryAsk => T(
        "物語を最初の章に戻します。各パーツのレベルはそのまま残ります。\n\nこの操作は取り消せません。",
        "The story goes back to its first chapter. Part levels are kept.\n\nThis cannot be undone.");
    public static string ResetLevelsAsk => T(
        "各パーツのレベルを 1 に戻します。物語の進み具合はそのまま残ります。\n\nこの操作は取り消せません。",
        "Every part goes back to level 1. Story progress is kept.\n\nThis cannot be undone.");
    public static string ResetAllAsk => T(
        "物語もレベルも最初に戻します。窓の位置と言語の設定は残ります。\n\nこの操作は取り消せません。",
        "Both the story and every level go back to the start. Window position and language are kept.\n\nThis cannot be undone.");

    // --- GPU の番号（設定 > データ） ---
    public static string NoticeOk          => T("OK", "OK");
    public static string GpuSlotsRepairedNotice => T(
        "保存の中の GPU の番号の一部が壊れていました。進みは残し、その番号は他のカードに引き継がれないよう取り置きました。設定 > データの「GPU の番号」で手放せます。",
        "Some GPU numbers in the save were damaged. Their progress was kept and those numbers were reserved so no other card takes it over. You can release them in Settings > GPU slots.");
    public static string GpuSlotsHeader    => T("GPU の番号", "GPU slots");
    public static string GpuSlotsAbout     => T(
        "カードごとに番号があり、経験値と装備はその番号につきます。外したカードは「手放す」で番号を閉じ、「リセット」で経験値を消して番号を空けます。",
        "Each card has a number; its experience and gear belong to that number. Forget closes the number of a card you removed; Reset removes its experience and frees the number.");
    public static string GpuSlotsNone      => T("GPU はまだ見つかっていません", "No GPU recorded yet");
    public static string GpuStateInUse     => T("使用中", "in use");
    public static string GpuStateAbsent    => T("見当たらない", "not found");
    public static string GpuStateRetired   => T("取り置き", "reserved");
    public static string GpuForget         => T("手放す", "Forget");
    public static string GpuReset          => T("リセット", "Reset");
    public static string GpuForgetTitle    => T("この番号を手放しますか", "Forget this number?");
    public static string GpuResetTitle     => T("この番号をリセットしますか", "Reset this number?");
    public static string GpuForgetAsk(int slot) => T(
        $"GPU{slot} の番号を閉じます。経験値と装備は残り、新しいカードがこの番号を使うことはありません。\n\n後から「リセット」で消せます。",
        $"GPU{slot}'s number is closed. Its experience and gear are kept, and no new card will take this number.\n\nYou can remove them later with Reset.");
    public static string GpuResetAsk(int slot) => T(
        $"GPU{slot} の経験値を消し、装備は持ち物へ戻します（入りきらない分は売れます）。番号は空き、次に来たカードがこの番号を使います。\n\nこの操作は取り消せません。",
        $"GPU{slot}'s experience is removed and its gear goes to the bag (what doesn't fit is sold). The number is freed for the next card.\n\nThis cannot be undone.");

    public static string CoresSuffix(int n) => T($"{n} 論理コア", $"{n} logical cores");
    public static string NoGpu    => T("GPUが見つかりません", "No GPU detected");
    public static string Vram     => T("VRAM", "VRAM");
    public static string ReadWrite=> T("読み / 書き", "Read / Write");
    public static string M2       => T("M.2", "M.2");
    public static string Ssd      => T("SSD", "SSD");
    public static string Hdd      => T("HDD", "HDD");
    public static string Drives   => T("ドライブ", "Drives");
    public static string Level    => T("Lv", "Lv");
    public static string Gear     => T("装備", "Gear");

    public static string SaveFailedTitle => T("保存できませんでした", "Could not save");
    public static string SaveFailedAsk(string reason) => T(
        $"進みを保存できませんでした。\n\n{reason}\n\n「はい」でもう一度保存します。「いいえ」は保存せずに閉じます（前回の保存からの進みは失われます）。",
        $"Your progress could not be saved.\n\n{reason}\n\nYes tries saving again. No closes without saving (progress since the last save is lost).");

    // 保存を止めてある回（前回の保存が読めなかった・控えを作れなかった）。閉じるとき、その旨を一度だけ伝える（はい／いいえは要らない）
    public static string SaveBlockedTitle => T("この回の進みは保存されません", "This session's progress is not saved");
    public static string SaveBlockedNote => T(
        "前回の保存を読めなかった（他のプログラムが開いている・保存先に入れない・壊れている等）ため、この回の進みは保存されません。\n\n" +
        "空の進みで前回の保存を上書きしないよう、保存を止めています。前回の保存はそのまま残っています。原因を取り除いてから、もう一度起動してください。",
        "The previous save could not be read (another program has it open, the folder is not reachable, or the file is damaged), so this session's progress is not saved.\n\n" +
        "Saving is off so that an empty state never overwrites it. The previous save is left as it was. Fix the cause and start again.");

    public static string Disclaimer => T(
        "※ 規格・相性・物語はすべてお遊びです。購入や設定の判断には使えません。",
        "Note: the specs, compatibility marks and story here are all for fun — not a reference for purchases or settings.");
}
