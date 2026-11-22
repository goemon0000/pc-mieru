namespace PcMieru.Gui;

/// <summary>
/// The words of the recording tab (stage B step 8): the buttons, the state line, the list, the summary and the status-bar indicator, ja and en
/// side by side, under <c>gui.rtab.*</c>. The words of what the recorder and the summary say themselves (how a run ended, a mark, a limit
/// reason, a failure to start or to delete) are <see cref="PcMieru.Checks.RecordStrings"/> (<c>gui.rec.*</c>), registered with the rest in
/// <see cref="GuiStrings"/>. The tab's title is <c>gui.chk.tab.record</c> in <see cref="CheckStrings"/>, next to the other tabs'.
/// Plain words for an ordinary PC owner: "recording", "mark", "not measured"; no "lease", no "frame".
/// </summary>
internal static class RecordTabStrings
{
    public static readonly StringEntry[] All =
    [
        // Buttons and their tips. The mark button gets its drop-down arrow in code.
        new("gui.rtab.start", "開始", "Start"),
        new("gui.rtab.stop", "停止", "Stop"),
        new("gui.rtab.mark", "印を付ける", "Add mark"),
        new("gui.rtab.mark.tip", "記録中の、今の時点に印をつけます", "Marks this moment in the recording"),
        new("gui.rtab.openfolder", "保存先を開く", "Open folder"),
        new("gui.rtab.legacy", "旧記録を開く", "Old records"),
        new("gui.rtab.legacy.tip", "以前のアプリ（パーツぐらし）の記録を一覧に加えます。読むだけで、書き換えも移動もしません", "Adds the records of the earlier app (the one before PC Mieru) to the list. They are only read, never changed or moved"),
        new("gui.rtab.csv", "CSV を開く", "Open CSV"),
        new("gui.rtab.delete", "削除", "Delete"),
        new("gui.rtab.pickfirst", "一覧から記録を選んでください", "Pick a recording in the list first"),

        // The marks a person can set (the stored codes are fixed; nothing typed is ever kept).
        new("gui.rtab.mark.benchstart", "ベンチ開始", "Bench start"),
        new("gui.rtab.mark.benchend", "ベンチ終了", "Bench end"),
        new("gui.rtab.mark.loadstart", "負荷開始", "Load start"),
        new("gui.rtab.mark.loadend", "負荷終了", "Load end"),
        new("gui.rtab.mark.plain", "汎用の印", "General mark"),

        // The state line.
        new("gui.rtab.state.idle", "記録していません。「開始」を押すと、今からの測定値を記録します", "Not recording. Press Start to record the readings from now on"),
        new("gui.rtab.state.starting", "記録を始めています…", "Starting the recording…"),
        new("gui.rtab.state.recording", "記録中 {time}　印 {marks} 個", "Recording {time}   Marks: {marks}"),
        new("gui.rtab.state.stopping", "停止して保存中です…", "Stopping and saving…"),
        new("gui.rtab.status.stopping", "停止して保存中", "Stopping and saving"),
        new("gui.rtab.state.saved", "保存しました（{length}）", "Saved ({length})"),
        new("gui.rtab.state.ended", "{reason}ため、記録を止めて保存しました（{length}）", "Stopped and saved: {reason} ({length})"),
        new("gui.rtab.state.failed", "保存に失敗しました。残った記録は確認できます。保存先の空き容量や書き込みの許可を確かめて、もう一度試してください。", "Saving failed. You can view the data that was saved. Check the folder's free space and write permission, then try again."),
        new("gui.rtab.problem.start", "記録を始められませんでした（{error}）", "The recording could not be started ({error})"),
        new("gui.rtab.problem.mark", "印をつけられませんでした（{error}）", "The mark could not be set ({error})"),
        new("gui.rtab.problem.stop", "記録の終わりを確かめられませんでした（{error}）。一覧で確かめてください", "The end of the recording could not be confirmed ({error}). Check the list"),

        // Where it is saved.
        new("gui.rtab.folder", "保存先: {path}", "Saved in: {path}"),
        new("gui.rtab.folder.new", "保存先: {path}（最初の記録を始めるときに作ります）", "Saved in: {path} (made when the first recording starts)"),
        new("gui.rtab.folder.documents", "ドキュメント", "Documents"),
        new("gui.rtab.folder.notyet", "保存先のフォルダはまだありません。最初の記録を始めると作られます", "The folder does not exist yet. It is made when the first recording starts"),
        new("gui.rtab.folder.openfail", "フォルダを開けませんでした（{error}）", "The folder could not be opened ({error})"),

        // The list.
        new("gui.rtab.list.name", "テストの記録の一覧", "Recordings"),
        new("gui.rtab.split.name", "一覧と集計の境目（上下に動かせます）", "Divider between the list and the summary (move it up or down)"),
        new("gui.rtab.col.start", "開始", "Started"),
        new("gui.rtab.col.length", "長さ", "Length"),
        new("gui.rtab.col.status", "状態", "Status"),
        new("gui.rtab.col.note", "補足", "Note"),
        new("gui.rtab.status.recording", "記録中", "Recording"),
        new("gui.rtab.status.completed", "完了", "Completed"),
        new("gui.rtab.status.interrupted", "中断", "Interrupted"),
        new("gui.rtab.status.failed", "失敗", "Failed"),
        new("gui.rtab.len.sec", "{s} 秒", "{s} s"),
        new("gui.rtab.len.min", "{m} 分 {s} 秒", "{m} min {s} s"),
        new("gui.rtab.len.hour", "{h} 時間 {m} 分 {s} 秒", "{h} h {m} min {s} s"),
        new("gui.rtab.len.about.min", "約 {m} 分", "about {m} min"),
        new("gui.rtab.len.about.hour", "約 {h} 時間 {m} 分", "about {h} h {m} min"),
        new("gui.rtab.len.about.hour0", "約 {h} 時間", "about {h} h"),
        new("gui.rtab.len.under", "1 分未満", "under 1 min"),
        new("gui.rtab.reason.unknown", "終わりの記録が残っていません", "no end was saved"),
        new("gui.rtab.legacy.tag", "旧形式・推定", "Old format, estimated"),
        new("gui.rtab.footer.count", "{count} 件の記録", "{count} recordings"),
        new("gui.rtab.footer.count.one", "{count} 件の記録", "{count} recording"),
        new("gui.rtab.footer.loading", "記録の一覧を読んでいます…", "Reading the list…"),
        new("gui.rtab.footer.empty", "まだ記録がありません", "No recordings yet"),
        new("gui.rtab.footer.approx", "『約』は目安です。中断・失敗した記録は、開くと残っているデータの長さを確認できます。", "Lengths marked 'about' are estimates. Open an interrupted or failed recording to see the length of the data saved."),
        new("gui.rtab.notice.problems", "読めなかったファイルが {count} 個あります（一覧には出していません）", "{count} files could not be read and are not in the list"),
        new("gui.rtab.notice.listfail", "記録の一覧を読めませんでした（{error}）", "The list of recordings could not be read ({error})"),

        // The selected run.
        new("gui.rtab.detail.pick", "一覧の記録を選ぶと、ここに集計が出ます", "Pick a recording in the list to see its summary here"),
        new("gui.rtab.detail.empty", "① 開始を押す → ② ベンチや負荷テストを動かし、必要なら印を付ける → ③ 停止して結果を見る", "1. Press Start. 2. Run your benchmark or stress test and add marks if needed. 3. Press Stop to see the summary."),
        new("gui.rtab.detail.stopping", "停止して保存中です。終わると集計が出ます", "Stopping and saving. The summary appears when it is done"),
        new("gui.rtab.detail.opening", "記録を開いています…", "Opening the recording…"),
        new("gui.rtab.detail.running", "記録中です（{time}）。止めると集計が出ます", "Being recorded ({time}). The summary appears when it stops"),
        new("gui.rtab.detail.notfound", "その記録は見つかりません（削除されたのかもしれません）", "That recording was not found (it may have been deleted)"),
        new("gui.rtab.detail.unreadable", "この記録を読めませんでした（{error}）", "This recording could not be read ({error})"),
        new("gui.rtab.detail.legacy", "読み取り専用です。このアプリは元のファイルを変更・移動しません。", "Read-only: this app will not change or move the original file."),
        new("gui.rtab.csv.missing", "この記録の CSV ファイルが見つかりません", "The CSV file of this recording was not found"),
        new("gui.rtab.csv.openfail", "CSV を開けませんでした（{error}）", "The CSV could not be opened ({error})"),
        new("gui.rtab.delete.title", "記録の削除", "Delete this recording"),
        new("gui.rtab.delete.message", "この記録のファイル（CSV と詳細）を削除します。元に戻せません", "This deletes the files of this recording (the CSV and its details). It cannot be undone"),
        new("gui.rtab.delete.confirm", "削除する", "Delete"),
        new("gui.rtab.delete.failed", "削除できませんでした（{error}）", "It could not be deleted ({error})"),

        // The summary table.
        new("gui.rtab.sum.name", "記録の集計", "Recording summary"),
        new("gui.rtab.sum.col.item", "項目", "Item"),
        new("gui.rtab.sum.col.min", "最小", "Min"),
        new("gui.rtab.sum.col.avg", "平均", "Average"),
        new("gui.rtab.sum.col.max", "最大", "Max"),
        new("gui.rtab.sum.col.sustained", "持続値", "Sustained"),
        new("gui.rtab.sum.sustained.tip", "負荷がかかっている間の、中央値です（下の説明を見てください）", "The median while the device was under load (see the note below the table)"),
        new("gui.rtab.sum.sustained.note", "持続値: 使用率が 50 % 以上だった時間だけを、最後に負荷が終わった所から 30〜300 秒（記録の長さの 3 分の 1）さかのぼって集め、その中央値を取った値です。60 秒未満の記録や、負荷の時間が 10 秒に満たないときは「-」です。", "Sustained: the median of the readings taken while usage was 50 % or more, over the last 30 to 300 s (a third of the recording) back from where the load ended. It is \"-\" for a recording under 60 s or when the load lasted under 10 s."),
        new("gui.rtab.sum.col.time", "時間・補足", "Time and notes"),
        new("gui.rtab.sum.head.record", "この記録", "This recording"),
        new("gui.rtab.sum.head.metrics", "測定値ごとの集計", "By reading"),
        new("gui.rtab.sum.head.limits", "クロックを抑えた理由", "Why clocks were held back"),
        new("gui.rtab.sum.head.co", "同時に起きたこと", "Things that happened together"),
        new("gui.rtab.sum.head.sections", "ベンチ・負荷の区間", "Bench and load sections"),
        new("gui.rtab.sum.head.warnings", "注意と補足", "Warnings and notes"),
        new("gui.rtab.sum.fact.end", "終わり方", "How it ended"),
        new("gui.rtab.sum.fact.seconds", "秒数の数え方", "How seconds are counted"),
        new("gui.rtab.sum.fact.saved", "残っているデータの長さ", "Length of the saved data"),
        new("gui.rtab.sum.record", "この記録", "This recording"),
        new("gui.rtab.sum.count.rows", "{count} 行", "{count} rows"),
        new("gui.rtab.sum.count.dropped", "{count} 回", "{count} times"),
        new("gui.rtab.sum.notmeasured", "測れず", "not measured"),
        new("gui.rtab.sum.time", "測れた時間: {seen} 秒／測れなかった時間: {missing} 秒", "Measured: {seen} s / Not measured: {missing} s"),
        new("gui.rtab.sum.time.none", "測れず（{why}）", "Not measured ({why})"),
        new("gui.rtab.sum.time.nothing", "測れず", "Not measured"),
        new("gui.rtab.sum.cputemp", "CPU 温度", "CPU temperature"),
        new("gui.rtab.sum.limit.device", "測れた {seen} 秒のうち", "Out of {seen} s measured"),
        new("gui.rtab.sum.limit.reason", "{s} 秒（負荷時 {l} 秒）", "{s} s ({l} s under load)"),
        new("gui.rtab.sum.limit.none", "印の立った時間はありませんでした", "No reason to hold the clock back was flagged"),
        new("gui.rtab.sum.limit.note", "クロックを抑える理由の印が立っていた時間です。故障を示すものではありません", "How long a reason to hold the clock back was flagged. It does not mean anything is broken"),
        new("gui.rtab.sum.co.note", "温度が上がりクロックが下がったのが同時に起きた時間帯です。一緒に起きたことを示すだけで、なぜそうなったかは分かりません", "Time windows where the temperature rose and the clock fell together. This only says they happened together, not why"),
        new("gui.rtab.sum.co.line", "同時に起きた: 温度 {tfrom}→{tto} ℃・クロック {cfrom}→{cto} MHz（{start}〜{end} 秒、平均負荷 {load}%）", "Happened together: temperature {tfrom}→{tto} C, clock {cfrom}→{cto} MHz ({start}-{end} s, average load {load}%)"),
        new("gui.rtab.sum.section.bench", "ベンチ {n}", "Bench {n}"),
        new("gui.rtab.sum.section.stress", "負荷 {n}", "Load {n}"),
        new("gui.rtab.sum.section.range", "{start}〜{end} 秒（{length} 秒）", "{start}-{end} s ({length} s)"),
        new("gui.rtab.sum.section.open", "{range}・終わりの印がなく、次の開始か記録の終わりまでとしています", "{range}. No end mark: taken up to the next start or the end of the recording"),
        new("gui.rtab.sum.section.metric", "中央値 {median}・測れた {seen} 秒", "median {median}, measured {seen} s"),
        new("gui.rtab.sum.section.nodata", "この区間は値を測れませんでした", "No value was measured in this section"),
        new("gui.rtab.sum.note.label", "補足", "Note"),
        new("gui.rtab.warn.late_gpu.label", "後から現れた GPU", "A GPU that appeared later"),
        new("gui.rtab.warn.late_gpu", "{name}: 記録の途中で現れました。それより前の分は測れずです", "{name}: it appeared during the recording. The time before that is not measured"),
        new("gui.rtab.warn.gpu_rebound.label", "GPU の付け替え", "A GPU came back"),
        new("gui.rtab.warn.gpu_rebound", "{name}: 別の ID で戻ってきたため、元の列に続けて記録しました", "{name}: it came back under another ID and was recorded in its old column"),
        new("gui.rtab.warn.open_sections.label", "終わりの印がない区間", "Sections with no end mark"),
        new("gui.rtab.warn.open_sections", "終わりの印がない区間: {count} 個。次の開始か記録の終わりまでとして集計しています", "Sections with no end mark: {count}. They are taken up to the next start or the end of the recording"),
        new("gui.rtab.warn.size_limit.label", "サイズの上限", "Size limit"),
        new("gui.rtab.warn.size_limit", "記録のサイズが上限に達したため、ここで止まりました", "The recording reached its size limit and stopped here"),
        new("gui.rtab.warn.low_space.label", "空き容量", "Free space"),
        new("gui.rtab.warn.low_space", "保存先の空きが少なくなったため、ここで止まりました", "Free space ran low where recordings are kept, so it stopped here"),

        // Outside the tab.
        new("gui.rtab.indicator", "記録中 {time}", "Recording {time}"),
        new("gui.rtab.indicator.tip", "テストの記録を取っています。クリックすると記録の画面を開きます", "A recording is running. Click to open the Recording tab"),
        new("gui.rtab.saving", "記録を保存しています…", "Saving the recording…"),
    ];
}
