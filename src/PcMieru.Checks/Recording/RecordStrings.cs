using PcMieru.Core;

namespace PcMieru.Checks;

/// <summary>
/// Every word of the recording and its summary, ja and en side by side. The Checks library gives IDs and codes, never text (SPEC-unify §3), so
/// each code the recorder, the store or the summary can produce has a <c>gui.rec.*</c> entry here. Step 8 adds <see cref="All"/> to the GUI's
/// table; a test registers it by itself and checks both languages and the placeholders of every entry. The codes are the ones of
/// <see cref="RunCodes"/>, with "-" and "_" kept as they are written in the files (<c>gui.rec.reason.host_restart</c>, <c>gui.rec.limit.sw-power-cap</c>).
/// </summary>
public static class RecordStrings
{
    public const string NotePrefix = "gui.rec.note.";
    public const string StatePrefix = "gui.rec.state.";
    public const string ReasonPrefix = "gui.rec.reason.";
    public const string StatusPrefix = "gui.rec.status.";
    public const string StopPrefix = "gui.rec.stop.";
    public const string MarkerPrefix = "gui.rec.marker.";
    public const string MetricPrefix = "gui.rec.metric.";
    public const string LimitPrefix = "gui.rec.limit.";
    public const string ActivityPrefix = "gui.rec.activity.";
    public const string StartPrefix = "gui.rec.start.";
    public const string DeletePrefix = "gui.rec.delete.";
    public const string ProblemPrefix = "gui.rec.problem.";
    public const string SectionPrefix = "gui.rec.section.";

    /// <summary>The codes of <see cref="RunNote.Code"/> a summary can carry (each has <c>gui.rec.note.&lt;code&gt;</c>).</summary>
    public static IReadOnlyList<string> NoteCodes { get; } =
    [
        "legacy-estimated", "no-samples", "too-short", "no-data", "sparse", "no-sustained-busy", "device-idle", "battery-used",
        "cpu-clock-drop-no-temp", "dropped-ticks", "not-completed", "gaps", "marker-unpaired",
    ];

    /// <summary>The reasons a list can give for a file it skipped (<see cref="RunListProblem.Reason"/>).</summary>
    public static IReadOnlyList<string> ProblemCodes { get; } = ["not_a_run", "bad_format", "unreadable"];

    /// <summary>The keys of the status, stop reason, marker kind, state, activity, start failure and delete outcome, by their enums.</summary>
    public static string KeyOf(RunStatus status) => StatusPrefix + RunCodes.Of(status);

    public static string KeyOf(RunStopReason reason) => StopPrefix + RunCodes.Of(reason);

    public static string KeyOf(MarkerKind kind) => MarkerPrefix + RunCodes.Of(kind);

    public static string KeyOf(RunActivityState state) => ActivityPrefix + state switch
    {
        RunActivityState.Preparing => "preparing",
        RunActivityState.Recording => "recording",
        RunActivityState.Stopping => "stopping",
        _ => "idle",
    };

    public static string KeyOf(RunStartFailure failure) => StartPrefix + failure switch
    {
        RunStartFailure.AlreadyRunning => "already_running",
        RunStartFailure.LowSpace => "low_space",
        RunStartFailure.IoError => "io_error",
        _ => "sampler_unavailable",
    };

    public static string KeyOf(RunDeleteOutcome outcome) => DeletePrefix + outcome switch
    {
        RunDeleteOutcome.Deleted => "deleted",
        RunDeleteOutcome.NotFound => "not_found",
        RunDeleteOutcome.Recording => "recording",
        RunDeleteOutcome.ReadOnlyLegacy => "read_only_legacy",
        _ => "failed",
    };

    /// <summary>The key of a <c>fresh</c>, <c>repeat</c> or <c>missing</c> cell.</summary>
    public static string StateKey(string state) => StatePrefix + state;

    /// <summary>The key of a <c>&lt;src&gt;.reason</c> code.</summary>
    public static string ReasonKey(string reason) => ReasonPrefix + reason;

    /// <summary>The key of a limit-reason name (<see cref="RunLimitBits"/>).</summary>
    public static string LimitKey(string name) => LimitPrefix + name;

    /// <summary>The key of a list problem's reason.</summary>
    public static string ProblemKey(string reason) => ProblemPrefix + reason;

    /// <summary>
    /// The label of a metric column key (<c>core.cpu_usage_pct</c>, <c>core.gpu1_util_pct</c>, <c>gpu1.temp_c</c>): the device first for a GPU
    /// column. A key this version does not know (an old run's, a newer one's) is returned as it is, never made up.
    /// </summary>
    public static string MetricLabel(string key, Language language)
    {
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == key.Length - 1)
        {
            return key;
        }

        var source = key[..dot];
        var tail = key[(dot + 1)..];
        var device = (string?)null;
        if (source == RunMetrics.Core && RunMetrics.TryGpuPrefix(tail, out var rest))
        {
            device = tail[..tail.IndexOf('_', StringComparison.Ordinal)];
            tail = rest;
        }
        else if (RunMetrics.IsGpuSource(source))
        {
            device = source;
        }

        var text = Strings.Get(MetricPrefix + tail, language);
        if (text == "[" + MetricPrefix + tail + "]")
        {
            return key;
        }

        return device is null ? text : DeviceLabel(device, language) + " " + text;
    }

    /// <summary>"CPU", "GPU 0" ... for the device names the summary uses (<c>cpu</c>, <c>gpu&lt;N&gt;</c>); anything else as it is.</summary>
    public static string DeviceLabel(string device, Language language)
    {
        if (device == "cpu")
        {
            return Strings.Get("gui.rec.device.cpu", language);
        }

        return RunMetrics.IsGpuSource(device)
            ? Strings.Format("gui.rec.device.gpu", new Dictionary<string, string> { ["n"] = device[3..] }, language)
            : device;
    }

    /// <summary>
    /// The arguments of a note's sentence: <c>{subject}</c> (a device, a metric, a status or a marker, already in words), <c>{detail}</c> (the note's
    /// own detail, with its codes in words) and, for a detail that is two numbers written "a/b", <c>{a}</c> and <c>{b}</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NoteArgs(RunNote note, Language language)
    {
        ArgumentNullException.ThrowIfNull(note);
        var subject = note.Code switch
        {
            "not-completed" when RunCodes.TryParse(note.Subject, out RunStatus status) => Strings.Get(KeyOf(status), language),
            "marker-unpaired" when RunCodes.TryParse(note.Subject, out MarkerKind kind) => Strings.Get(KeyOf(kind), language),
            "no-data" or "sparse" => MetricLabel(note.Subject, language),
            "no-sustained-busy" or "device-idle" or "cpu-clock-drop-no-temp" => DeviceLabel(note.Subject, language),
            _ => note.Subject,
        };
        var detail = note.Code switch
        {
            "not-completed" when RunCodes.TryParse(note.Detail, out RunStopReason reason) => Strings.Get(KeyOf(reason), language),
            "no-data" => string.Join(", ", note.Detail.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(code => Strings.Get(ReasonKey(code), language))),
            _ => note.Detail,
        };
        var args = new Dictionary<string, string>(StringComparer.Ordinal) { ["subject"] = subject, ["detail"] = detail };
        var slash = note.Detail.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            args["a"] = note.Detail[..slash];
            args["b"] = note.Detail[(slash + 1)..];
        }

        return args;
    }

    /// <summary>The sentence of a note in one language.</summary>
    public static string NoteText(RunNote note, Language language) =>
        Strings.Format(NotePrefix + note.Code, NoteArgs(note, language), language);

    public static readonly StringEntry[] All =
    [
        // The summary's notes (DESIGN-B §7.2). {subject}, {detail}, {a}, {b} are filled by NoteArgs.
        new(NotePrefix + "legacy-estimated", "旧形式の記録です。取得時刻は行の時刻、間隔は当時の設定値からの推定です", "This is a record in the old format. The acquire time is taken from the row time and the interval from the old setting, so the seconds are estimates"),
        new(NotePrefix + "no-samples", "記録に行がありません", "The recording has no rows"),
        new(NotePrefix + "too-short", "記録が {detail} 秒と短いため、持続値（終盤の負荷時の中央値）は出していません", "The recording is only {detail} s, so no sustained value (the median of the loaded part near the end) is given"),
        new(NotePrefix + "no-data", "{subject}: 値が一度も取れませんでした（{detail}）", "{subject}: no value was ever obtained ({detail})"),
        new(NotePrefix + "sparse", "{subject}: 値が分かっていた時間は記録の {detail}% だけです", "{subject}: a value was known for only {detail}% of the recording"),
        new(NotePrefix + "no-sustained-busy", "{subject}: 終盤に負荷がかかった時間が足りず、持続値は出していません", "{subject}: there was not enough loaded time near the end, so no sustained value is given"),
        new(NotePrefix + "device-idle", "{subject}: 負荷はほとんどかかりませんでした（最大 {detail}%）", "{subject}: the load stayed low (peak {detail}%)"),
        new(NotePrefix + "battery-used", "記録の途中で電源が外れ、バッテリーで動いた時間があります", "The PC ran on the battery for part of the recording"),
        new(NotePrefix + "cpu-clock-drop-no-temp", "{subject}: 終盤のクロックが最大より下がっています（{a} / {b} MHz）が、CPU 温度が記録にないため、温度との関係は分かりません", "{subject}: the clock near the end is below its maximum ({a} / {b} MHz), but the recording has no CPU temperature, so any link to temperature cannot be told"),
        new(NotePrefix + "dropped-ticks", "取りこぼした測定が {detail} 回あります（その分の行はありません）", "{detail} readings were dropped (there is no row for them)"),
        new(NotePrefix + "not-completed", "この記録は正常に終わっていません（{subject}、止まった理由: {detail}）。最後に残っていた値までで集計しています", "This recording did not end normally ({subject}, reason: {detail}). It is summarized up to the last value that was kept"),
        new(NotePrefix + "gaps", "値が取れなかった時間が合計 {a} 秒、最長 {b} 秒あります", "Values were missing for {a} s in total, {b} s at the longest"),
        new(NotePrefix + "marker-unpaired", "対になる開始・終了の印がありません（{subject}）", "A start or end mark has no partner ({subject})"),

        // A cell's state and the reason of a missing one.
        new(StatePrefix + "fresh", "新しい値", "New value"),
        new(StatePrefix + "repeat", "前と同じ値", "Same as before"),
        new(StatePrefix + "missing", "欠け", "Missing"),
        new(ReasonPrefix + "host_restart", "計測の補助プロセスが再起動した", "The sensor helper restarted"),
        new(ReasonPrefix + "read_failed", "読み取りに失敗した", "The read failed"),
        new(ReasonPrefix + "not_started", "まだ始まっていない", "Not started yet"),
        new(ReasonPrefix + "unsupported", "この PC では取れない", "Not available on this PC"),
        new(ReasonPrefix + "device_gone", "デバイスが無くなった", "The device is gone"),
        new(ReasonPrefix + "no_frame", "値が届かなかった", "No value arrived"),

        // How a run ended.
        new(StatusPrefix + "recording", "記録中", "Recording"),
        new(StatusPrefix + "completed", "完了", "Completed"),
        new(StatusPrefix + "interrupted", "途中で中断", "Interrupted"),
        new(StatusPrefix + "failed", "失敗", "Failed"),
        new(StopPrefix + "user", "止める操作", "Stopped by the user"),
        new(StopPrefix + "window_close", "ウィンドウを閉じた", "The window was closed"),
        new(StopPrefix + "low_space", "空き容量が少なくなった", "Free space ran low"),
        new(StopPrefix + "io_error", "書き込みに失敗した", "Writing failed"),
        new(StopPrefix + "process_exit", "アプリが終了した", "The app exited"),
        new(StopPrefix + "size_limit", "記録のサイズが上限に達した", "The recording reached its size limit"),
        new(StopPrefix + "unknown", "不明", "Unknown"),

        // Marks.
        new(MarkerPrefix + "bench-start", "ベンチ開始", "Bench start"),
        new(MarkerPrefix + "bench-end", "ベンチ終了", "Bench end"),
        new(MarkerPrefix + "stress-start", "ストレス開始", "Stress start"),
        new(MarkerPrefix + "stress-end", "ストレス終了", "Stress end"),
        new(MarkerPrefix + "mark", "印", "Mark"),
        new(MarkerPrefix + "warning", "注意", "Warning"),
        new(SectionPrefix + "bench", "ベンチ", "Bench"),
        new(SectionPrefix + "stress", "ストレス", "Stress"),

        // Columns (the device is put in front by MetricLabel).
        new("gui.rec.device.cpu", "CPU", "CPU"),
        new("gui.rec.device.gpu", "GPU {n}", "GPU {n}"),
        new(MetricPrefix + "cpu_usage_pct", "CPU 使用率", "CPU usage"),
        new(MetricPrefix + "cpu_mhz", "CPU クロック", "CPU clock"),
        new(MetricPrefix + "cpu_package_w", "CPU 消費電力", "CPU package power"),
        new(MetricPrefix + "mem_total_bytes", "メモリ 全体", "Memory total"),
        new(MetricPrefix + "mem_available_bytes", "メモリ 空き", "Memory available"),
        new(MetricPrefix + "mem_commit_bytes", "コミット済み", "Memory committed"),
        new(MetricPrefix + "mem_commit_limit_bytes", "コミット上限", "Commit limit"),
        new(MetricPrefix + "mem_cache_bytes", "システムキャッシュ", "System cache"),
        new(MetricPrefix + "mem_paged_pool_bytes", "ページプール", "Paged pool"),
        new(MetricPrefix + "mem_nonpaged_pool_bytes", "非ページプール", "Non-paged pool"),
        new(MetricPrefix + "ac_connected", "電源接続", "AC connected"),
        new(MetricPrefix + "battery_pct", "バッテリー残量", "Battery level"),
        new(MetricPrefix + "battery_state", "バッテリーの状態", "Battery state"),
        new(MetricPrefix + "util_pct", "使用率", "usage"),
        new(MetricPrefix + "vram_used_bytes", "専用メモリ使用量", "dedicated memory used"),
        new(MetricPrefix + "temp_c", "温度", "temperature"),
        new(MetricPrefix + "power_w", "消費電力", "power"),
        new(MetricPrefix + "fan_pct", "ファン", "fan"),
        new(MetricPrefix + "clock_mhz", "クロック", "clock"),
        new(MetricPrefix + "temp_limit_c", "温度の上限", "temperature limit"),
        new(MetricPrefix + "limit_reasons", "クロックを抑えた理由", "clock limit reasons"),

        // The GPU driver's clock-event reasons. They say a mark was set, not that anything is wrong.
        new(LimitPrefix + "gpu-idle", "GPU が空いている", "GPU idle"),
        new(LimitPrefix + "applications-clocks-setting", "アプリのクロック設定による制限", "Limited by an application's clock setting"),
        new(LimitPrefix + "sw-power-cap", "電力による制限", "Power limit"),
        new(LimitPrefix + "hw-slowdown", "ハードウェア側の判断による速度の制限", "Speed limited by the hardware itself"),
        new(LimitPrefix + "sync-boost", "複数の GPU でクロックをそろえる設定", "Clocks matched across several GPUs"),
        new(LimitPrefix + "sw-thermal-slowdown", "温度による制限", "Temperature limit"),
        new(LimitPrefix + "hw-thermal-slowdown", "ハードウェアが判断した温度による速度の制限", "Speed limited by temperature (decided by the hardware)"),
        new(LimitPrefix + "hw-power-brake-slowdown", "電源側の電力ブレーキによる速度の制限", "Speed limited by the power brake"),
        new(LimitPrefix + "display-clock-setting", "画面表示のためのクロック設定", "Clock set for the display"),
        new(LimitPrefix + RunLimitBits.UnknownName, "この版が名前を知らない理由", "Reasons this version has no name for"),

        // A temperature rise with a clock fall in the same window. Only "together"; never a cause.
        new("gui.rec.co.title", "同じ時間帯に起きたこと", "Things that happened in the same time window"),
        new("gui.rec.co.line", "{device}: {start}〜{end} 秒に温度が {tempFrom}→{tempTo} ℃ に上がり、クロックが {clockFrom}→{clockTo} MHz に下がりました（平均負荷 {load}%）。同じ時間帯に起きたことを示すだけで、原因は分かりません", "{device}: from {start} to {end} s the temperature rose from {tempFrom} to {tempTo} C and the clock fell from {clockFrom} to {clockTo} MHz (average load {load}%). This only says they happened in the same time window; the cause cannot be told"),

        // How exact the seconds are.
        new("gui.rec.fresh.exact", "秒数は取得時刻から数えています", "Seconds are counted from the acquire times"),
        new("gui.rec.fresh.legacy_estimated", "旧形式の記録のため、秒数は推定です", "Old format, estimated"),

        // The summary's headings.
        new("gui.rec.sum.duration", "記録の長さ", "Length"),
        new("gui.rec.sum.rows", "行数", "Rows"),
        new("gui.rec.sum.dropped", "取りこぼし", "Dropped readings"),
        new("gui.rec.sum.observed", "値が分かっていた時間", "Time a value was known"),
        new("gui.rec.sum.missing", "値が欠けていた時間", "Time a value was missing"),
        new("gui.rec.sum.min", "最小", "Min"),
        new("gui.rec.sum.avg", "平均", "Average"),
        new("gui.rec.sum.max", "最大", "Max"),
        new("gui.rec.sum.sustained", "持続値", "Sustained"),
        new("gui.rec.sum.limits", "クロックを抑えた理由", "Clock limit reasons"),
        new("gui.rec.sum.sections", "区間", "Sections"),
        new("gui.rec.sum.notes", "注記", "Notes"),

        // What the recorder is doing.
        new(ActivityPrefix + "idle", "記録していません", "Not recording"),
        new(ActivityPrefix + "preparing", "記録の準備中", "Preparing to record"),
        new(ActivityPrefix + "recording", "記録中", "Recording"),
        new(ActivityPrefix + "stopping", "記録を止めて保存中", "Stopping and saving"),

        new(StartPrefix + "already_running", "すでに記録中です（記録は 1 つだけです）", "A recording is already in progress (only one at a time)"),
        new(StartPrefix + "low_space", "保存先の空きが 200 MB 未満のため、記録を始められません", "There is less than 200 MB free where recordings are kept, so it cannot start"),
        new(StartPrefix + "io_error", "保存先のフォルダやファイルを作れませんでした", "The folder or the files for the recording could not be created"),
        new(StartPrefix + "sampler_unavailable", "計測が止まっているため、記録を始められません", "Measuring has stopped, so there is nothing to record"),

        new(DeletePrefix + "deleted", "記録を削除しました", "The recording was deleted"),
        new(DeletePrefix + "not_found", "その記録は見つかりません", "That recording was not found"),
        new(DeletePrefix + "recording", "記録中のため削除できません", "It is being recorded and cannot be deleted"),
        new(DeletePrefix + "read_only_legacy", "旧形式の記録は読み取り専用のため、ここからは削除できません", "A recording in the old format is read only and is not deleted here"),
        new(DeletePrefix + "failed", "ファイルを削除できませんでした", "A file could not be deleted"),

        new(ProblemPrefix + "not_a_run", "記録のファイルではありません", "Not a recording file"),
        new(ProblemPrefix + "bad_format", "形式が読めません", "The format cannot be read"),
        new(ProblemPrefix + "unreadable", "ファイルを読めません", "The file cannot be read"),
    ];
}
