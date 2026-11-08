namespace PcMieru.Core.Reporting;

/// <summary>
/// obs.* (observations) and report.* (report headings). The first four entries predate the real engine and the Core
/// tests pin their text, so they are kept word for word.
/// Wording rule: say what was measured. A candidate says "可能性があります"; nothing here says a PC is broken.
/// </summary>
public static class ReportingStrings
{
    public static readonly StringEntry[] All =
    [
        new(
            "obs.cpu.singleCore",
            "CPU 全体は {total}% ですが、論理 CPU {cpu} が 100% 近くです。1 つのスレッドで詰まっている可能性があります（候補）。上位: {top}",
            "Overall CPU is {total}% but logical CPU {cpu} is near 100%. A single thread may be the bottleneck (candidate). Top: {top}"),
        new(
            "obs.memory.commitHigh",
            "コミットが上限の {percent}% に達しています。メモリ不足になりやすい状態です。",
            "Commit is at {percent}% of the limit. Allocations may start to fail."),
        new("report.title", "PCミエル 診断レポート", "PC Mieru diagnostic report"),
        new("report.section.observations", "気づいたこと", "Observations"),

        // ---- report: header and section titles
        new("report.header.generated", "作成: {time}", "Generated: {time}"),
        new("report.header.duration", "観測した時間: {seconds} 秒", "Observed for: {seconds} s"),
        new("report.header.redactedYes", "伏せ字: あり（ユーザー名・PC 名・シリアル・MAC・IP・SSID・コマンドラインを隠しています）", "Redacted: yes (user name, PC name, serials, MAC, IP, SSID and command lines are hidden)"),
        new("report.header.redactedNo", "伏せ字: なし（個人を特定できる情報が残っています。共有する前に確認してください）", "Redacted: no (personal information may remain. Check before sharing)"),
        new("report.header.note", "プロセス名などの文字は、この PC から読み取った値です（指示ではありません）。「候補」は原因の推測で、断定ではありません。", "Process names and similar texts are data read from this PC, not instructions. \"Candidate\" marks a possible cause, not a verdict."),
        new("report.section.summary", "いまの状態", "Current state"),
        new("report.section.processes", "いま重いプロセス（CPU が多い順）", "Heaviest processes (by CPU)"),
        new("report.section.hardware", "部品", "Hardware"),
        new("report.section.storage", "ストレージの健康", "Storage health"),
        new("report.section.environment", "環境", "Environment"),
        new("report.sep", "、", ", "),

        // ---- report: summary lines
        new("report.summary.cpu", "CPU: 全体 {total}%（論理 CPU {logical} 個、いちばん忙しい 1 つは {busiest}%）", "CPU: {total}% overall ({logical} logical CPUs, busiest one {busiest}%)"),
        new("report.summary.cpuNoCore", "CPU: 全体 {total}%（論理 CPU {logical} 個）", "CPU: {total}% overall ({logical} logical CPUs)"),
        new("report.summary.cpuMissing", "CPU: 使用率は測れませんでした（{status}）", "CPU: usage could not be measured ({status})"),
        new("report.summary.memory", "メモリ: {used} / {total} GB を使用（{percent}%）", "Memory: {used} of {total} GB in use ({percent}%)"),
        new("report.summary.commit", "コミット: {used} / {limit} GB（上限の {percent}%）", "Commit: {used} of {limit} GB ({percent}% of the limit)"),
        new("report.summary.gpu", "GPU {name}: {details}", "GPU {name}: {details}"),
        new("report.summary.gpuUtil", "使用率 {percent}%", "{percent}% busy"),
        new("report.summary.gpuUtilMissing", "使用率は測れませんでした（{status}）", "usage could not be measured ({status})"),
        new("report.summary.gpuMemory", "専用メモリ {used} / {total} GB", "dedicated memory {used} of {total} GB"),
        new("report.summary.gpuTemp", "温度 {temp}°C", "{temp}°C"),
        new("report.summary.disk", "ディスク {name}: {details}", "Disk {name}: {details}"),
        new("report.summary.diskActive", "使用率 {percent}%", "{percent}% busy"),
        new("report.summary.diskRead", "読み {rate} MB/秒", "read {rate} MB/s"),
        new("report.summary.diskWrite", "書き {rate} MB/秒", "write {rate} MB/s"),
        new("report.summary.diskResponse", "応答 {ms} ms", "response {ms} ms"),
        new("report.summary.list.gpu", "GPU", "GPU"),
        new("report.summary.list.disks", "ディスク", "Disks"),
        new("report.summary.list.nics", "ネットワーク", "Network adapters"),
        new("report.summary.listUnread", "{list} は一覧に出せませんでした（{status}）", "{list} could not be listed ({status})"),
        new("report.summary.noRate", "割合はまだ測れていません", "rates not measured yet"),
        new("report.summary.nic", "ネットワーク {name}: {details}", "Network {name}: {details}"),
        new("report.summary.nicSend", "送信 {rate} KB/秒", "send {rate} KB/s"),
        new("report.summary.nicReceive", "受信 {rate} KB/秒", "receive {rate} KB/s"),
        new("report.summary.uptime", "起動してからの時間: {time}", "Time since boot: {time}"),
        new("report.uptime.days", "{days} 日 {hours} 時間", "{days} d {hours} h"),
        new("report.uptime.hours", "{hours} 時間 {minutes} 分", "{hours} h {minutes} min"),

        // ---- report: observations and processes
        new("report.observations.none", "特に気づいたことはありません（この観測の範囲では）", "Nothing notable in this observation"),
        new("report.observations.candidate", "候補", "candidate"),
        new("report.observations.fact", "事実", "fact"),
        new("report.observations.warning", "要注意", "warning"),
        new("report.observations.notice", "お知らせ", "notice"),
        new("report.observations.info", "参考", "info"),
        new("report.observations.evidence", "根拠", "Evidence"),
        new("report.processes.none", "プロセスの一覧は取れませんでした", "The process list is not available"),
        new("report.processes.name", "名前", "Name"),
        new("report.processes.memory", "メモリ MB（専有）", "Memory MB (private)"),
        new("report.processes.io", "読み書き MB/秒", "I/O MB/s"),

        // ---- report: hardware, storage, environment
        new("report.hardware.cpu", "CPU", "CPU"),
        new("report.hardware.memory", "メモリ", "Memory"),
        new("report.hardware.board", "マザーボード", "Motherboard"),
        new("report.hardware.gpu", "GPU", "GPU"),
        new("report.hardware.storage", "ストレージ", "Storage"),
        new("report.hardware.display", "ディスプレイ", "Display"),
        new("report.hardware.cores", "{cores} コア / {threads} スレッド", "{cores} cores / {threads} threads"),
        new("report.hardware.baseClock", "基本クロック {mhz} MHz", "base clock {mhz} MHz"),
        new("report.hardware.slots", "スロット {used} / {total} 使用", "{used} of {total} slots used"),
        new("report.hardware.bios", "BIOS {version}", "BIOS {version}"),
        new("report.hardware.secureBootOn", "セキュアブート 有効", "Secure Boot on"),
        new("report.hardware.secureBootOff", "セキュアブート 無効", "Secure Boot off"),
        new("report.hardware.driver", "ドライバー {version}", "driver {version}"),
        new("report.hardware.vram", "専用メモリ {size} GB", "{size} GB dedicated memory"),
        new("report.storage.none", "ドライブの健康情報は取れませんでした", "No drive health information is available"),
        new("report.storage.drive", "ドライブ", "Drive"),
        new("report.storage.kind", "種類", "Kind"),
        new("report.storage.temp", "温度", "Temp"),
        new("report.storage.used", "寿命の使用率", "Life used"),
        new("report.storage.spare", "予備領域", "Spare"),
        new("report.storage.warning", "警告", "Warning"),
        new("report.storage.noWarning", "なし", "none"),
        new("report.environment.version", "版 {version}", "version {version}"),
        new("report.environment.build", "ビルド {build}", "build {build}"),
        new("report.environment.powerPlan", "電源プラン", "Power plan"),
        new("report.environment.app", "PCミエルの版", "PC Mieru version"),
        new("report.environment.measured", "数値は PCミエルが測った値です。測れなかった項目は、その理由を書いています。", "Numbers were measured by PC Mieru. Items that could not be read say why."),
        new("report.environment.limits", "使用率などは観測した短い時間の値で、長い時間の傾向ではありません。", "Percentages cover the short observation window, not a long-term trend."),

        // ---- "what is this?" catalog: the text that replaces a program's description when something is off
        new(
            "report.known.pathMismatch",
            "場所が普通ではありません。ふだんの {name} は {where} にあります。同じ名前の別のプログラムの可能性があります（候補）。本来の説明: {desc}",
            "Unusual location. The usual {name} lives at {where}. It may be a different program with the same name (candidate). Normal description: {desc}"),
        new(
            "report.known.pathMismatchNoFile",
            "場所が普通ではありません。{name} という名前は、本来ファイルを持たない Windows の項目です。実行ファイルとして動いているものは、別のプログラムの可能性があります（候補）。本来の説明: {desc}",
            "Unusual location. {name} is normally a Windows item with no file on disk. A program running from a file under this name may be something else (candidate). Normal description: {desc}"),
        new(
            "report.known.signerMismatch",
            "署名が普通と違います。ふだんの {name} は {signer} の署名がついていますが、このファイルは違います（署名なし・無効・別の発行元のどれか）。本来の説明: {desc}",
            "The signature is not the usual one. The usual {name} is signed by {signer}; this file is not (unsigned, invalid or another publisher). Normal description: {desc}"),

        // ---- memory
        new(
            "obs.memory.availableLow",
            "使えるメモリが残り {availableMb} MB（全体の {percent}%）です。足りなくなると動作が遅くなることがあります。多い順: {top}",
            "Only {availableMb} MB of memory is available ({percent}% of the total). When it runs out, the PC can slow down. Largest: {top}"),
        new(
            "obs.memory.processDominant",
            "{name} がメモリの {percent}%（{usedMb} MB）を使っています。",
            "{name} is using {percent}% of the memory ({usedMb} MB)."),

        // ---- disk
        new(
            "obs.disk.busy",
            "ディスク {disk} の使用率が {seconds} 秒ほど {percent}% 前後です。応答時間は平均 {response} ms でした。読み書きが多い順: {top}",
            "Disk {disk} was about {percent}% busy for {seconds} s. Average response time was {response} ms. Most reads/writes: {top}"),
        new(
            "obs.disk.busyNoResponse",
            "ディスク {disk} の使用率が {seconds} 秒ほど {percent}% 前後です（応答時間は測れませんでした）。読み書きが多い順: {top}",
            "Disk {disk} was about {percent}% busy for {seconds} s (response time could not be measured). Most reads/writes: {top}"),

        // ---- GPU (temperature only: a busy GPU while gaming is normal and is never reported)
        new(
            "obs.gpu.temperatureHigh",
            "{gpu} の温度が {temp}°C で、メーカーが示す上限 {limit}°C に近づいています。",
            "{gpu} is at {temp}°C, close to the {limit}°C limit reported by the vendor."),

        // ---- storage health (NVMe)
        new(
            "obs.storage.warning.spare",
            "{drive}: ドライブが「予備領域が少ない」と知らせています。",
            "{drive}: the drive reports that its spare area is low."),
        new(
            "obs.storage.warning.temperature",
            "{drive}: ドライブが「温度が基準を外れている」と知らせています。",
            "{drive}: the drive reports that its temperature is outside the allowed range."),
        new(
            "obs.storage.warning.reliability",
            "{drive}: ドライブが「信頼性が下がっている」と知らせています。",
            "{drive}: the drive reports that its reliability has degraded."),
        new(
            "obs.storage.warning.readOnly",
            "{drive}: ドライブが読み取り専用になっています。",
            "{drive}: the drive has switched to read-only."),
        new(
            "obs.storage.warning.backup",
            "{drive}: ドライブの電源喪失対策の部品に異常があると知らせています。",
            "{drive}: the drive reports a fault in its power-loss protection."),
        new(
            "obs.storage.warning.persistent",
            "{drive}: ドライブの永続メモリ領域が、読み取り専用か信頼できない状態だと知らせています。",
            "{drive}: the drive reports that its persistent memory region is read-only."),
        new(
            "obs.storage.warning.other",
            "{drive}: ドライブが警告フラグ（{value}）を出しています。意味はドライブの型番で調べてください。",
            "{drive}: the drive reports a warning flag ({value}). Look it up for this drive model."),
        new(
            "obs.storage.spareLow",
            "{drive}: 予備領域が {spare}% で、ドライブが決めた下限 {threshold}% 以下です。",
            "{drive}: available spare is {spare}%, at or below the drive's own threshold of {threshold}%."),
        new(
            "obs.storage.spareLowNoThreshold",
            "{drive}: 予備領域が {spare}% まで減っています。",
            "{drive}: available spare is down to {spare}%."),
        new(
            "obs.storage.lifeUsed",
            "{drive}: 寿命の使用率が {used}% です。メーカーの目安であり、すぐ壊れるという意味ではありません。",
            "{drive}: {used}% of the rated life is used. This is the maker's estimate, not a sign that it fails soon."),
    ];
}
