using System.Globalization;
using PcMieru.Party.Import;

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
    public static string ResetStory   => T("物語をリセット", "Reset story");
    public static string ResetLevels  => T("レベルをリセット", "Reset levels");
    public static string ResetAll     => T("物語とレベルをリセット", "Reset story and levels");
    public static string ResetTitle   => T("最初からやり直しますか", "Start over?");

    public static string ResetStoryAsk => T(
        "物語を最初の章に戻し、旅も最初の町からやり直します。物語のログは消えます。\n\n残るもの: 各パーツのレベル、持ち物、装備（GPU の分も）、所持金。\n\nこの操作は取り消せません。",
        "The story goes back to the first chapter and the journey restarts from the first town. The story log is cleared.\n\nKept: part levels, items, gear (GPU gear too) and gold.\n\nThis cannot be undone.");
    public static string ResetLevelsAsk => T(
        "各パーツのレベルを 1 に戻します。\n\n残るもの: 物語の進み具合、持ち物、装備（GPU の分も）、所持金。\n\nこの操作は取り消せません。",
        "Every part goes back to level 1.\n\nKept: story progress, items, gear (GPU gear too) and gold.\n\nThis cannot be undone.");
    public static string ResetAllAsk => T(
        "物語もレベルも最初に戻します。持ち物、装備（GPU の分も）、所持金も消え、所持金は最初の額になります。冒険の記録と、稼働時間・起動回数も 0 に戻ります。\n\n残るもの: 窓の位置、言語・更新などの設定、GPU の番号。\n\nこの操作は取り消せません。",
        "The story and every level go back to the start. Items, gear (GPU gear too) and gold are deleted too, and gold goes back to the starting amount. The adventure record, play time and launch count go back to 0.\n\nKept: window position, language and update settings, GPU slot numbers.\n\nThis cannot be undone.");

    // --- GPU の番号（設定 > データ） ---
    public static string NoticeOk          => T("OK", "OK");
    public static string GpuSlotsRepairedNotice => T(
        "保存の中の GPU の番号の一部が壊れていました。進みは残し、その番号は他のカードに引き継がれないよう取り置きました。設定 > データの「GPU の番号」で手放せます。",
        "Some GPU numbers in the save were damaged. Their progress was kept and those numbers were reserved so no other card takes it over. You can release them in Settings > GPU slots.");
    public static string GpuSlotsHeader    => T("GPU の番号", "GPU slots");
    public static string GpuSlotsAbout     => T(
        "経験値と装備は GPU の番号ごとに残ります。外したカードは「手放す」で番号を閉じます（経験値と装備は残ります）。そのあと「リセット」を押すと、その番号の経験値を消し、装備を袋へ戻して番号を空けます。",
        "Experience and gear are kept per GPU number. For a card you removed, Forget closes its number (its experience and gear are kept). Reset then deletes that number's experience, returns its gear to the bag and frees the number.");
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

    public static string ChecksUnavailable => T("点検を開けませんでした（PCMieru.exe が同じフォルダにありません）", "Could not open the checks (PCMieru.exe is not in the same folder)");
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

    // ---- 旧パーツぐらしの続き（初回起動の引き継ぎ。DESIGN-C 4.1） ----
    public static string ImportTitle        => T("パーツぐらしの続き", "Continue Parts Life");
    public static string ImportBodyLife     => T("旧パーツぐらし（PartsLife）の続きが見つかりました。ここから続けますか。",
                                                 "Progress from the old Parts Life (PartsLife) was found. Continue from it?");
    public static string ImportBodyParty    => T("旧名のパーツぐらし（PartsParty）の続きが見つかりました。ここから続けますか。",
                                                 "Progress from the old Parts Life under its former name (PartsParty) was found. Continue from it?");
    public static string ImportKept         => T("旧版の保存はそのまま残ります。", "The old save is left exactly as it is.");
    public static string ImportRepairHeader => T("次の欄は読み取れませんでした。", "These fields could not be read:");
    public static string ImportRepairNote   => T("直せなかった欄は初期値になります。", "Fields that could not be read start from their defaults.");
    public static string ImportContinue     => T("引き継ぐ", "Continue");
    public static string ImportLater        => T("あとで", "Later");
    public static string ImportStartFresh   => T("新しく始める", "Start fresh");
    public static string ImportRetry        => T("もう一度", "Retry");
    public static string ImportCloseButton  => T("閉じる", "Close");
    public static string ImportOpenReadOnly => T("保存せずに開く", "Open without saving");
    public static string ImportReadOnlyNote => T("保存せずに開いた場合、この回の進みは保存されません。新しい保存はそのまま残ります。",
                                                 "If you open it without saving, this session's progress is not saved. The new save is left as it is.");
    public static string ImportFreshAsk     => T("旧パーツぐらしの続きはこのあと引き継げなくなります。新しく始めますか。",
                                                 "The old progress cannot be brought over afterwards. Start fresh?");
    public static string ImportProblemTitle => T("パーツぐらしを始められません", "Parts Life cannot start yet");
    public static string ImportHoldPrefix   => T("旧パーツぐらしの続きは引き継げません。", "The old progress cannot be brought over. ");

    // ---- ログオン時の起動の切り替え（引き継ぎのあとの 1 問と、その復元の確認。DESIGN-C 4.3） ----
    public static string RunSwitchTitle     => T("ログオン時の起動", "Start at sign-in");
    public static string RunSwitchAsk       => T("ログオン時の起動も、パーツぐらし（PCミエル）に切り替えますか？",
                                                 "Switch sign-in start to Parts Life (PC Mieru) too?");
    public static string RunSwitchCopied    => T("保存の引き継ぎは、すでに完了しています。", "Your save was already copied over successfully.");
    public static string RunSwitchYes       => T("切り替える", "Switch");
    public static string RunSwitchNo        => T("今はしない", "Not now");
    public static string RunSwitchCheck     => T("ログオン設定の復元を確認してください", "Please check the sign-in setting");

    /// <summary>引き継ぎの理由コード（<see cref="ImportReason"/>）に対する文。コードが増えて文が無いときは null（テストが全コードを回して見つける）。</summary>
    public static string? ImportReasonTextOrNull(string reason) => reason switch
    {
        ImportReason.TooLarge            => T("旧版の保存が大きすぎて読み込めません。", "The old save is too large to read."),
        ImportReason.BrokenJson          => T("旧版の保存が壊れていて読み込めません。", "The old save is damaged and cannot be read."),
        ImportReason.TooDeep             => T("旧版の保存の形が想定外で読み込めません。", "The old save has an unexpected structure and cannot be read."),
        ImportReason.DuplicateKey        => T("旧版の保存に同じ項目が二重に書かれていて読み込めません。", "The old save lists the same entry twice and cannot be read."),
        ImportReason.NotAnObject         => T("旧版の保存が保存の形式になっていません。", "The old save is not in the save format."),
        ImportReason.VersionType         => T("旧版の保存の版の欄が読み取れません。", "The version entry of the old save cannot be read."),
        ImportReason.VersionNegative     => T("旧版の保存の版の欄が正しくありません。", "The version entry of the old save is not valid."),
        ImportReason.VersionFuture       => T("旧版の保存は、この版より新しい版で作られています。", "The old save was made by a newer version than this one."),
        ImportReason.MainFieldsUnreadable => T("旧版の保存の主要な欄が読み取れません。", "The main fields of the old save cannot be read."),
        ImportReason.ConversionFailed    => T("旧版の保存を新しい形に変えられませんでした。", "The old save could not be converted to the new form."),
        ImportReason.InternalError       => T("内部の問題が起きました。もう一度お試しください。", "An internal problem occurred. Please try again."),
        ImportReason.OutputTooLarge      => T("引き継ぐと保存が大きくなりすぎます。", "The save would become too large after the import."),
        ImportReason.Accept              => T("そのまま引き継げます。", "It can be brought over as it is."),
        ImportReason.NeedsRepair         => T("一部の欄を初期値にすれば引き継げます。", "It can be brought over if some fields start from their defaults."),
        ImportReason.Imported            => T("引き継ぎました。", "Brought over."),
        ImportReason.StartedFresh        => T("新しく始めました。", "Started fresh."),
        ImportReason.AlreadyHave         => T("すでに保存があります。", "A save is already there."),
        ImportReason.NoCandidate         => T("旧版の続きは見つかりませんでした。", "No old progress was found."),
        ImportReason.DestinationBroken   => T("新しい保存が読み取れません。そのまま残してあります。", "The new save cannot be read. It was left as it is."),
        ImportReason.DestinationTooLarge => T("新しい保存が大きすぎて読み取れません。そのまま残してあります。", "The new save is too large to read. It was left as it is."),
        ImportReason.DestinationDenied   => T("新しい保存の場所にアクセスできません（拒否されました）。", "The place of the new save cannot be accessed (denied)."),
        ImportReason.DestinationSharing  => T("新しい保存がほかで使われています。少し待ってから、もう一度お試しください。", "The new save is in use elsewhere. Wait a moment, then try again."),
        ImportReason.DestinationIoError  => T("新しい保存の場所を読めませんでした。", "The place of the new save could not be read."),
        ImportReason.OldAppRunning       => T("パーツぐらし（旧版）を終了してから、もう一度お試しください", "Close the old Parts Life, then try again"),
        ImportReason.OldAppUnknown       => T("旧版のパーツぐらしが動いているか確かめられませんでした。旧版を終了してから、もう一度お試しください。",
                                              "Could not tell whether the old Parts Life is running. Close it, then try again."),
        ImportReason.SourceDenied        => T("旧版の保存を読む許可がありません。", "There is no permission to read the old save."),
        ImportReason.SourceSharing       => T("旧版の保存が使用中で読めません。旧版を終了してから、もう一度お試しください。", "The old save is in use and cannot be read. Close the old Parts Life, then try again."),
        ImportReason.SourceIoError       => T("旧版の保存を読めませんでした。もう一度お試しください。", "The old save could not be read. Please try again."),
        ImportReason.SourceChanged       => T("旧版の保存が確認のあとで変わりました。もう一度お試しください。", "The old save changed after it was checked. Please try again."),
        ImportReason.CandidateAppeared   => T("旧版の続きが見つかりました。もう一度確かめます。", "Old progress was found. It is being checked again."),
        ImportReason.NeedsConsent        => T("一部の欄が初期値になることの確認が必要です。", "Confirmation is needed that some fields start from their defaults."),
        ImportReason.LockBusy            => T("ほかのパーツぐらしが引き継ぎ中です。少し待ってから、もう一度お試しください。", "Another Parts Life is importing. Wait a moment, then try again."),
        ImportReason.WriteFailed         => T("保存を書き込めませんでした。空き容量や権限を確かめて、もう一度お試しください。", "The save could not be written. Check the free space and permissions, then try again."),
        ImportReason.VerifyFailed        => T("書き込んだ保存の確認に失敗しました。もう一度お試しください。", "The written save did not verify. Please try again."),
        _ => null,
    };

    public static string ImportReasonText(string reason) =>
        ImportReasonTextOrNull(reason) ?? T("引き継ぎを進められませんでした。もう一度お試しください。", "The import could not go on. Please try again.");

    /// <summary>メッセージ窓に出す 1 文。保留（Hold）だけは「引き継げない」を頭に付ける。</summary>
    public static string ImportMessage(ImportOutcome outcome, string reason) =>
        outcome == ImportOutcome.Hold ? ImportHoldPrefix + ImportReasonText(reason) : ImportReasonText(reason);
}
