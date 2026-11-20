# DESIGN-A — 点検と構成コード（PCMieru v0.4.0）

下書き: codex gpt-6.2-sol（2026-11-20）。点検と修正: Opus（司会 2fb51ba0）。直した所は §13。
**項目ごとの判定は §14 が正**（§3 の表の「値・判定」欄と食い違ったら §14 を取る）。

## 0. 版の線

正本は `docs/unify/SPEC-unify.md` u0.4（u0.3 `dcc0b11` に段 A の点検の結果を足した版）。PCミエルの既存コードの行番号は `dcc0b11` を指す。`area/parts-share@9e068c4` と書いた箇所は v0.3.0 側の先行実装を指す。パーツぐらしは `pl-cloud` の `v0.7.4`（`25e4571`）。以下の `PL/` は同 repo の `src/PartsLife.Checks/`、`PL-tests/` は `tests/PartsLife.Checks.Tests/`。

段 A はシステム点検、CPU・メモリ負荷、記憶媒体の健康と増分、履歴、PCB1、計測手順まで。表面スキャン、Helper セッション、GPU 負荷、`run_system_check`、概要への集約、記録、更新、Party は後段。`pcmi parts --code` は §6 の PCB1 の入口として含める。根拠は `docs/unify/SPEC-unify.md:45`、`:115`、`:131`、`:181`、`:197`、`:203`、`:230`。

## 1. プロジェクトと移植

`src/PcMieru.Checks/PcMieru.Checks.csproj` を追加する。`net10.0-windows`、アセンブリ名 `PcMieru.Checks`。参照は `Gui → Checks → Core → Wire` と `Cli → Checks → Core → Wire`。Gui と Cli は Core の直接参照も使う。Helper は既存の `Wire` 参照から点検用の応答を組み立てる。Checks に UI、管理者 exe、ネットワーク依存を入れない。`System.Management` は Checks の WMI 読み取りに置く。現行の参照は `src/PcMieru.Gui/PcMieru.Gui.csproj:25`、`src/PcMieru.Cli/PcMieru.Cli.csproj:14`、`src/PcMieru.Core/PcMieru.Core.csproj:11`、`src/PcMieru.Helper/PcMieru.Helper.csproj:15`、`:26`。旧 WMI 依存は `PL/PartsLife.Checks.csproj:37`。

| 旧ファイル | 行き先と扱い |
|---|---|
| `SystemInfo/SystemCheck.cs` | `PcMieru.Checks.SystemInfo`。判定を移植し、収集と結果組み立てを書き直す。Core の OS、CPU、SMBIOS、GPU、ディスク、ボリュームを使う。WMI はページファイルと Core で不足する GPU の構成状態に絞る。旧収集・判定は `PL/SystemInfo/SystemCheck.cs:31`、`:56`、`:147`、`:182`。 |
| `SystemInfo/EventCounts.cs` | 同名前空間。固定のプロバイダーと XPath の組み立てを移す。列挙、期限、終了確認を書き直す。`PL/SystemInfo/EventCounts.cs:19`、`:41`。 |
| `Load/CpuLoadTest.cs`、`Steadiness.cs`、`MemoryTest.cs`、`Patterns.cs` | `PcMieru.Checks.Load`。計算、メモリパターン、閾値を移す。停止、進捗、判定 DTO への変換を書き直す。`PL/Load/CpuLoadTest.cs:45`、`:166`、`PL/Load/Steadiness.cs:17`、`PL/Load/MemoryTest.cs:61`、`:71`、`PL/Load/Patterns.cs:29`。 |
| `Load/CpuTopology.cs`、`ClockSampler.cs` | Core に論理 CPU の group／番号／効率クラスと、点検専用 PDH セッションを出す。Checks はスレッドの割り当てとサンプルの集計を持つ。Core の集計用トポロジー、PDH 呼び出しと型を共用する。`PL/Load/CpuTopology.cs:7`、`:42`、`:77`、`PL/Load/ClockSampler.cs:22`、`src/PcMieru.Core/Hardware/Collectors/CpuTopology.cs:29`、`:44`、`src/PcMieru.Core/Collection/Pdh.cs:154`。 |
| `Storage/Baseline.cs`、`StorageJudge.cs` | `PcMieru.Checks.Storage`。鍵、カウンター名、重複・連続性・個別判定を移す。ファイル I/O、移行、総合判定を書き直す。旧 `Overall` は「測れず」を順位から除く。段 A は §3 の集約を使う。`PL/Storage/Baseline.cs:45`、`:171`、`:279`、`:407`、`:497`、`PL/Storage/StorageJudge.cs:11`、`:34`。 |
| `Storage/StorageReader.cs`、`DiskReport.cs`、`StorageProtocol.cs`、`NvmeHealth.cs`、`AtaSmart.cs` | Core の既存収集・解析に寄せる。旧版と同じ識別子の抽出、足りない測定フィールド、Checks 内部の判定用入力だけを追加する。`PL/Storage/StorageReader.cs:54`、`PL/Storage/DiskReport.cs:46`、`src/PcMieru.Core/Hardware/Storage/HealthParsers.cs:10`、`:67`、`:145`。 |
| `Build/BuildConfig.cs`、`BuildRules.cs`、`BuildCode.cs`、`BuildCollector.cs` | `PcMieru.Checks.Build`。型、整形、符号化、`BuildSources` の契約を移す。Windows 読み取り部分は出力互換を守って期限付きにする。`PL/Build/BuildConfig.cs:24`、`PL/Build/BuildRules.cs:29`、`PL/Build/BuildCode.cs:27`、`PL/Build/BuildCollector.cs:11`、`:40`、`:72`。 |

旧 `Finding` の翻訳済み文字列は新しい結果の入力にしない。判定を値と根拠コードに変換し、表示時に PCミエルの文字列表で訳す。旧 `Features.cs` の StorageCheck／GpuLoadTest、`Protocol/`、`Scan/`、`Gpu/`、WPF の点検窓、`PartsLife.Check.exe` は対象外。旧ゲートの役割は `PL/Features.cs:8`、`:21`。移植元を `NOTICE.txt` に追記する。既存の表記は `NOTICE.txt:21`。`PcMieru.slnx` に本体と試験を登録し、`tools/publish.ps1` の必須ファイルに `PcMieru.Checks.dll` を加える。現行登録・検査は `PcMieru.slnx:3`、`:12`、`tools/publish.ps1:333`、`:355`。

## 2. 呼び出し口と実行の寿命

公開型は次に絞る。結果、履歴の要約、進捗は不変 DTO。実機の採取器、基準値、ファイル実装は内部型とする。

```csharp
public interface ICheckRunner
{
    Task<CheckRun> RunAsync(CheckRequest request,
        IProgress<CheckProgress>? progress, CancellationToken ct);
}

public interface IReadOnlyCheckHistory
{
    Task<CheckHistoryPage> ListAsync(CheckHistoryQuery query, CancellationToken ct);
    Task<CheckResult?> GetAsync(Guid runId, CancellationToken ct);
}

public interface IBuildCodeProvider
{
    Task<BuildCodeResult> CreateAsync(CancellationToken ct);
}
```

- 履歴の型（§14 の確定に合わせて固定）:

```csharp
public sealed record CheckHistoryQuery(CheckKind? Kind, int? Limit);         // null＝保持している全部（CLI・GUI。種類ごとに最大 200 件×4）。MCP は 1〜200・既定 20
public sealed record CheckHistoryEntry(Guid RunId, CheckKind Kind,
    DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc,
    CheckOutcome Outcome, CheckVerdict Verdict, CheckCounts Counts);       // Verdict は総合の 4 値
public sealed record CheckHistoryWarning(string Code, int Count);
public sealed record CheckHistoryPage(IReadOnlyList<CheckHistoryEntry> Entries,
    IReadOnlyList<CheckHistoryWarning> Warnings);
```

  `Warnings.Code` は `history_unreadable`（壊れている・必須欄の不整合）、`history_future_format`（未知の `format`）、`history_quarantined`（今回 `.bad-` へ退避した）、`history_quarantine_failed`（退避できず残した）の 4 つだけ。同じコードは 1 行にまとめて件数を持つ。MCP は退避しないので最初の 2 つだけを出す。JSON は camelCase、`kind`・`outcome`・`verdict` は小文字の文字列、`counts` は 5 値のキー。
- `CheckRequest` は `CheckKind`、負荷の `Duration`、記憶媒体の `AllowElevation`。長さは 30／120／300 秒。`AllowElevation` は `storage` に限る。`CheckRun` は `CheckResult` と保存状態を返す。保存の成否と測定の `outcome` は別に扱う。
- `CheckProgress` は実行 ID、段階、経過秒、目標秒、直近の速度と単位。CPU は `chunks/s`、メモリは書き込み／照合の `MiB/s`。負荷の通知は最大毎秒 1 回。段階変更と最終結果は別に届ける。通知は結果の確定や停止処理を待たせない。
- `CheckServices` は `IHardwareProvider` と新設する Core の `IStorageInspectionSource`、CPU 点検用測定セッションのファクトリー、時計と保存先を受ける。Windows の依存は組み立て側で差し込む。`IHardwareProvider.GetAsync` と既存 `IStorageHealthProvider` の入口は `src/PcMieru.Core/Contracts/Interfaces.cs:76`、`:84`。現行の採取の振り分けは `src/PcMieru.Core/Hardware/RealHardwareProvider.cs:32`、`:49`。Core の `Value<T>` を採取境界で使い、Checks の結果に変換する。`src/PcMieru.Core/Contracts/Value.cs:14`、`:30`。
- GUI はウィンドウにつき実行を 1 本持つ。実行・採取・WMI・履歴操作・PCB1 の収集と保存はバックグラウンドで行う。UI は進捗と結果の描画、ダイアログ、クリップボードを担当する。`Task.Run` より前に同期 I/O を置かない。明示的な GC を呼ばない。負荷のサンプルは独立したセッションで採り、`SamplerLoop` に設定要求を出さない。GUI の既存接続は `src/PcMieru.Gui/Data/SnapshotPump.cs:37`、`:222`、`:249`。
- 停止は実行ごとの `CancellationTokenSource` を使う。CPU スレッドの join とメモリの解放を待ってから「停止」にする。停止時点までに確認できた異常は残す。途中の集計は `partial`。期限超過で Windows の問い合わせから先に戻るときは結果を封印し、遅れて返った値を捨てる。ネイティブ呼び出しの残存は §6 の実行枠で管理する。
- 結果の確定点は一つ。確定前の停止は `stopped`、実行全体が成立しない障害は `failed`、個別の欠測を含め所定の処理を終えた回は `completed`。開始した回は停止・失敗も保存対象。確定後の書き込みには利用者の停止トークンを渡さない。保存に失敗した場合も画面に結果を残す。

## 3. `pcm-check/1`

UTF-8 の JSON オブジェクト。キーと列挙値は英語。表示用の文、Windows のエラー原文、パス、ユーザー名、PC 名、シリアル、デバイス ID、基準値の鍵は保存しない。モデル名はモデルとして取得した値を許可し、型番欄に入ったシリアルらしい値も落とす。基準値から結果へ出すディスク識別子は `disk-N` のみ。

| 欄 | 型と規則 |
|---|---|
| `format`, `app`, `runId` | `"pcm-check/1"`、`"PCMieru 0.4.0"` のような製品名＋版、ランダム UUID。 |
| `kind`, `startedAtUtc`, `endedAtUtc` | `system\|cpu\|memory\|storage`（段 A は GPU を作らない。`gpu` は門を開く版で足す）。時刻は UTC ISO 8601（`Z`）。経過時間と期限の計算には単調時計を使う。 |
| `outcome`, `verdict`, `counts` | `completed\|stopped\|failed`。結果全体の `verdict` は 4 値 `ok\|caution\|check\|unavailable`。項目の `verdict` は `info` を足した 5 値。`counts` は 5 値それぞれのキーを持つ非負整数（`info` を含む）。 |
| `items` | `{id, subject?, value, unit, verdict, source, reason, evidence?}` の配列。`id` は表の固定値。反復項目の `subject` は `{id, model?}`。同じ結果内で `(id, subject.id)` は一意。ディスク以外の反復にも `ram-1`、`gpu-1`、`volume-1`、`pagefile-1` などの通し番号を使う。ドライブ文字やパスは入れない。 |
| `notes` | 固定コードの配列。例 `memory.borrowed_region_only`、`storage.baseline_read_failed`。表示文は読み手が引く。 |

`reason` は完全に測れたら `null`。途中の値なら値は非 `null` のまま `reason: partial` を併せて持つ（これだけが値と理由の併存を許す）。`value: null` の場合は必ず `unsupported`（対応や公開がない）、`denied`（権限拒否・UAC 拒否）、`timeout`（期限超過）、`error`（読み取り・検証失敗）、`partial`（停止・不足・比較不能）のいずれか。`source` は `smbios`、`setupapi`、`win32`、`wmi`、`eventlog`、`pdh`、`load`、`nvme`、`ata`、`baseline` などの固定コード。`evidence` は項目ごとの固定キーを持つ。例 `code: "counter_increased"`、`code: "baseline_duplicate"`。任意の診断文を通さない。

項目は**判定する項目**と**観測だけの項目**（`verdict: info`）に分かれ、どちらかは ID ごとに固定（§3.0）。総合判定は判定する項目の `check > caution > unavailable > ok` の最大。判定する項目が 1 つも無い・全部欠けたときは `unavailable`。`info` の項目は値が欠けても総合に入れない。判定する項目で `reason: partial` のものは、すでに異常を見つけていなければ `unavailable`（値は途中まで持ってよい）。**項目の `verdict` そのものを `unavailable` にして書く**（`ok` と `reason` の組は不正。総合と `counts` が食い違わないように）。たとえば停止した CPU 点検で確認済みの計算不一致は `check` のまま、集計し終えていない持続性は `partial`。`counts` は全項目から再計算できる。

**読み手の寛容さ（版をまたぐ）。** `pcm-check/1` のまま、後の版で項目 ID・`source`・`evidence.code`・`notes` のコードを**足してよい**（段 B は前の版へ戻せるので、新しい版が書いた履歴を古い版が読む）。書き手は自分の知る値だけを書き、検証は厳しく行う。読み手（履歴の一覧・詳細、MCP）は知らない ID・コードを持つ行を「この版では表示しない行」として飛ばし、総合と `counts` は書かれた値をそのまま使う（数は全行から検算する）。知らない `kind` は将来の版として現物を残し、`.bad` へ退避しない。`verdict`・`reason`・`outcome` の値、欄の型、必須欄を変えるときだけ `pcm-check/2` にする。

**根拠のコードを置ける行。** `baseline_*` は `.delta` の行だけ（値 `null`・`info`）。`counter_increased`・`counter_decreased`・`new_bits` は `.delta` の行と、その元の絶対値の行（基準値の増減を根拠に使うとき）。他のコードは §14 で使う行だけ。

以下で `int` は JSON 整数、`number` は有限の JSON 数、`dec` は符号なし十進整数の文字列。大きな累積値とバイト数は `dec` にする。`–` は `unit: null`。収集不能なら該当項目を `null` で出す。列挙そのものが失敗した場合は一覧の項目を `null` にし、実在を確認できない子の行は作らない。

### 3.0 観測だけの項目（`info`）

次の ID は常に `verdict: info`。それ以外の ID は判定する項目。**判定する項目が `info` になるのは §14.0 の 2 つの例外だけ**。

- システム: `system.eventlog.disk_warning7d`、`system.eventlog.ntfs_warning7d`、`system.eventlog.stornvme_warning7d`、`system.eventlog.volmgr_warning7d`、`system.os.version`、`system.cpu.model`、`system.cpu.cores`、`system.cpu.logical`、`system.cpu.rated_clock`、`system.memory.capacity`、`system.memory.rated_speed`、`system.gpu.adapters`、`system.gpu.model`、`system.gpu.driver_version`、`system.storage.devices`、`system.pagefile.automatic`、`system.pagefile.allocated`、`system.pagefile.current`
- CPU: `cpu.duration`、`cpu.chunks`、`cpu.throughput`、`cpu.variation`、`cpu.logical_throughput`、`cpu.utilization`、`cpu.package_power`
- メモリ: `memory.duration`、`memory.total`、`memory.available_at_start`、`memory.borrowed`、`memory.passes`、`memory.verified`、`memory.write_rate`、`memory.verify_rate`
- 記憶媒体: `storage.devices`、`storage.capacity`、`storage.nvme.spare_threshold`、`storage.nvme.unsafe_shutdowns`、`storage.nvme.power_cycles`、`storage.nvme.power_on_hours`、`storage.nvme.data_units_written`、`storage.ata.9`、および §3.3 の判定表に無い `.delta` と比べられなかった `.delta`

旧 `StorageJudge` が「参考（判定しない）」にしていた不意の電源断・通電時間・読み書き量（`PL/Storage/StorageJudge.cs:142`）と同じ線。判定する項目の閾値は旧判定をそのまま移す。

### 3.1 システム

| ID | 型・単位 | 値・判定 |
|---|---|---|
| `system.os.version` | object・– | `{name, displayVersion, build, ubr}`。版番号は整数。 |
| `system.os.uptime` | number・`s` | 30 日超は注意。 |
| `system.cpu.model`, `system.cpu.cores`, `system.cpu.logical`, `system.cpu.rated_clock` | string・–、int・`count`、int・`count`、number・`MHz` | Core の名称、物理／論理数、定格。取得元の値をそのまま区別して出す。 |
| `system.memory.modules`, `system.memory.capacity`, `system.memory.rated_speed`, `system.memory.configured_speed` | int・`count`、dec・`B`、int・`MT/s`、int・`MT/s` | 最初は搭載枚数。残りは各枚。混在容量は一覧の根拠に出す。定格未満は設定上の注意、定格超えは情報。速度が 0 または欠けていれば測れず。 |
| `system.gpu.adapters`, `system.gpu.model`, `system.gpu.driver_version`, `system.gpu.driver_age`, `system.gpu.device_status`, `system.gpu.generic_driver` | int・`count`、string・–、string・–、int・`d`、int・`code`、bool・– | 最初は枚数。残りは各 GPU。ドライバの古さは 730 日超で注意。汎用ドライバ（Basic Display）は注意。構成コード 22 は無効化の表示で故障に数えない。0 以外の状態ではドライバの 2 行は `info`（§14.0）。 |
| `system.storage.devices`, `system.storage.health`, `system.storage.free` | int・`count`、string・–、dec・`B` | ディスク一覧、台ごとの健康、ローカルのボリュームごとの空き。健康値は `healthy\|warning\|critical`。判定に必要な健康情報が欠ければ測れず。空きの `evidence.totalBytes` も十進文字列。空き率 3% 未満は要確認、10% 未満は注意。ボリュームの所属を一意に確認できるときだけ `evidence.diskId` を付ける。 |
| `system.pagefile.count`, `system.pagefile.automatic`, `system.pagefile.allocated`, `system.pagefile.current`, `system.pagefile.peak` | int・`count`、bool・–、dec・`B`、dec・`B`、dec・`B` | 件数 0 は注意。後三つは各ファイル。割当が正のファイルだけ、ピークが割当の 80% 超なら注意。 |
| `system.eventlog.whea7d`, `system.eventlog.disk7d`, `system.eventlog.ntfs7d`, `system.eventlog.stornvme7d`, `system.eventlog.volmgr7d` | int・`count` | Level 1–2 の件数。対応する Level 3 の ID は順に `system.eventlog.whea_warning7d`、`system.eventlog.disk_warning7d`、`system.eventlog.ntfs_warning7d`、`system.eventlog.stornvme_warning7d`、`system.eventlog.volmgr_warning7d`。同じ型と単位。WHEA エラーありは要確認。警告行と他のエラー行は旧判定を項目別に当てる。 |
| `system.eventlog.kp41_7d` | int・`count` | Kernel-Power 41。1 件以上は注意。原因は断定しない。 |

旧項目と閾値の出所は `PL/SystemInfo/SystemCheck.cs:6`、`:162`、`:182`、`:195`、`:211`、`:229`、`:244`、`:271`、`:286`。コード 22 の回帰試験は `PL-tests/RealMachineAndScanJudgeTests.cs:90`。Core の部品型は `src/PcMieru.Core/Hardware/Model/HardwareModel.cs:28`、`:84`、`:115`、`:139`、`:250`。システム点検の健康は通常権限の結果だけを使い、増分の比較と記録は記憶媒体点検で行う。

### 3.2 CPU とメモリ

| ID | 型・単位 | 値・判定 |
|---|---|---|
| `cpu.duration`, `cpu.chunks`, `cpu.compute_errors` | number・`s`、int・`count`、int・`count` | 実時間、照合済みチャンク、計算不一致。1 件以上は要確認。照合 0 回の不一致欄は測れず。 |
| `cpu.throughput`, `cpu.steadiness`, `cpu.variation` | number・`chunks/s`、number・`%`、number・`%` | 全体の速度、終盤／序盤、変動係数×100。判定は `cpu.steadiness` の 1 行だけ（持続性 70% 未満は要確認、90% 未満または変動係数 10% 超は注意）。`cpu.variation` は材料で `info`。 |
| `cpu.logical_throughput`, `cpu.peer_ratio` | number・`chunks/s`、number・`%` | 論理 CPU ごと。`evidence.efficiencyClass` は整数。同クラス中央値の 75% 未満は注意。pin の成否を確認できない比較は測れず。 |
| `cpu.effective_clock`, `cpu.utilization`, `cpu.package_power` | number・`MHz`、number・`%`、number・`W` | 有効サンプルの平均。実効クロックの持続率が 85% 未満なら注意。使用率と電力は観測（`info`）。温度の項目は置かない（下）。 |
| `memory.duration`, `memory.total`, `memory.available_at_start`, `memory.borrowed` | number・`s`、dec・`B`、dec・`B`、dec・`B` | 実時間、物理容量、開始時の空き、確保した範囲。確保量と検証済み量を区別する。 |
| `memory.passes`, `memory.verified`, `memory.mismatches` | int・`count`、dec・`B`、int・`count` | 全範囲の照合を完了したパス数、パスをまたいだ照合済みバイトの合計、不一致。1 件以上は要確認。照合 0 バイトの不一致欄は測れず。 |
| `memory.write_rate`, `memory.verify_rate` | number・`MiB/s` | 書き込みと照合の実測。転送量 0 の区間から速度 0 を作らない。 |

CPU の持続性、コア比較、クロックの閾値の出所は `PL/Load/Steadiness.cs:17`、`:30`、`PL/Load/CpuLoadTest.cs:166`、`:193`、`:213`。メモリのパスと速度の単位は `PL/Load/MemoryTest.cs:153`、`:180`。CPU の結果には常に note `cpu.temperature_not_measured` を付ける（ドライバを入れないので読めない。項目にすると毎回「測れず」になり総合を汚す）。メモリの結果には常に `memory.borrowed_region_only` を付ける。画面に「借りた範囲だけの検査です。全部のメモリの検査ではありません」と出す。

### 3.3 記憶媒体

| ID | 型・単位 | 内容 |
|---|---|---|
| `storage.devices`, `storage.health`, `storage.capacity`, `storage.temperature` | int・`count`、string・–、dec・`B`、number・`°C` | 一覧の件数、台の健康概要、容量、温度。健康値はシステム点検と共通。 |
| `storage.nvme.critical_warning`, `storage.nvme.available_spare`, `storage.nvme.spare_threshold`, `storage.nvme.percentage_used` | int・`bits`、number・`%`、number・`%`、number・`%` | 警告ビット、予備領域、しきい値、寿命消費。 |
| `storage.nvme.media_errors`, `storage.nvme.error_log_entries`, `storage.nvme.unsafe_shutdowns`, `storage.nvme.power_cycles` | dec・`count` | NVMe の累積値。 |
| `storage.nvme.power_on_hours`, `storage.nvme.data_units_written` | dec・`h`、dec・`nvme_data_unit` | 通電時間、書き込み単位。1 単位は 512,000 B。`docs/SPEC.md:164`。 |
| `storage.ata.predict_failure`, `storage.ata.threshold_status` | bool・–、string・– | ドライブの故障予告と属性のしきい値の状態。しきい値は `within\|past_breach\|current_breach`。 |
| `storage.ata.5`, `storage.ata.187`, `storage.ata.196`, `storage.ata.197`, `storage.ata.198`, `storage.ata.199`, `storage.ata.9` | dec・`smart_raw` | ATA の属性値。9 の表示は「通電カウンター（機種固有の単位）」。190／194 は共通の温度行に出す。 |

前回との差を扱うのは NVMe の `critical_warning`、`available_spare`、`percentage_used`、`media_errors`、`error_log_entries`、`unsafe_shutdowns`、`power_on_hours`、`data_units_written` と、表の ATA 数値属性。比較結果の ID は当該 ID に `.delta` を足した固定集合。値は `dec` と同じ精度を持つ符号付き十進文字列、単位は元の行と同じ。`evidence` に `before`、`after`、`sinceUtc` を持つ。警告ビットの行では新規に立ったビットも根拠にする。温度の旧基準値は移行時に保存するが、増分の判定項目にしない。

比較不能なら `.delta` は `null`。`evidence.code` は `baseline_first`、`baseline_no_identity`、`baseline_duplicate`、`baseline_device_changed`、`baseline_counter_reset`、`baseline_clock`、`baseline_concurrent`、`baseline_missing_counter`、`baseline_read_failed`、`baseline_legacy_refused` のいずれか（決まりは §14.5）。ディスクに適用する方式の項目だけを出す。`.delta` の判定は旧 `Counters.GrowthVerdict`（`PL/Storage/Baseline.cs:59`）の表どおり。増えたとき: `storage.nvme.media_errors`・`storage.nvme.critical_warning`・`storage.ata.5`・`.187`・`.196`・`.197`・`.198` は `check`、`storage.nvme.error_log_entries` と `storage.ata.199` は `caution`（199 は `evidence.code: likely_cable`。旧 `StorageJudge.cs:189` の「ケーブルや差し込みのことが多い」）。表にある項目で増えていなければ `ok`。表に無い項目（不意の電源断・通電・書き込み量・寿命の消費・予備領域）の `.delta` と、比べられなかった `.delta` は `info`（初回は毎回比べられないので「測れず」にしない）。絶対値の行は旧の項目別判定（`GrowthFinding` の初回・増加・横ばい、`StorageJudge.cs:77`、`:186`）をそのまま移し、基準値から得た増減をその根拠にも使う。旧カウンター、個別判定、ATA の値の出所は `PL/Storage/Baseline.cs:45`、`:85`、`PL/Storage/StorageJudge.cs:70`、`:113`、`:159`、`:226`、`PL/Storage/AtaSmart.cs:27`、`:127`。

### 3.4 JSON 例

照合中に停止したメモリ点検。確認済みの不一致 1 件は `check` のまま残す（停止しても見つけた異常は消さない）。不一致が 0 件のまま止まっていれば、その行は `unavailable`・`reason: partial`・値 0 で、総合は `unavailable`。

```json
{
  "format": "pcm-check/1",
  "app": "PCMieru 0.4.0",
  "runId": "89914ef9-5781-4cf7-a4f8-6d58003ddf86",
  "kind": "memory",
  "startedAtUtc": "2026-11-20T03:00:00.000Z",
  "endedAtUtc": "2026-11-20T03:00:04.200Z",
  "outcome": "stopped",
  "verdict": "check",
  "counts": {"ok": 0, "caution": 0, "check": 1, "unavailable": 0, "info": 8},
  "items": [
    {"id":"memory.duration","value":4.2,"unit":"s","verdict":"info","source":"load","reason":null},
    {"id":"memory.total","value":"34359738368","unit":"B","verdict":"info","source":"win32","reason":null},
    {"id":"memory.available_at_start","value":"12884901888","unit":"B","verdict":"info","source":"win32","reason":null},
    {"id":"memory.borrowed","value":"3221225472","unit":"B","verdict":"info","source":"load","reason":null},
    {"id":"memory.passes","value":null,"unit":"count","verdict":"info","source":"load","reason":"partial"},
    {"id":"memory.verified","value":"134217728","unit":"B","verdict":"info","source":"load","reason":"partial"},
    {"id":"memory.mismatches","value":1,"unit":"count","verdict":"check","source":"load","reason":"partial","evidence":{"code":"pattern_mismatch"}},
    {"id":"memory.write_rate","value":6400,"unit":"MiB/s","verdict":"info","source":"load","reason":null},
    {"id":"memory.verify_rate","value":5300,"unit":"MiB/s","verdict":"info","source":"load","reason":null}
  ],
  "notes": ["memory.borrowed_region_only"]
}
```

## 4. 保存と履歴

```text
%LOCALAPPDATA%\PCMieru\checks\
  system-<UTC>-<runId:N>.json
  cpu-<UTC>-<runId:N>.json
  memory-<UTC>-<runId:N>.json
  storage-<UTC>-<runId:N>.json
  baseline\
    disk-<旧方式の接頭辞>-<鍵のハッシュ先頭16桁>.json
    twins.json
    imported-twins.json
    imported-v074.json
    disk-ordinals.json
    acq-seq.json
```

- 履歴の `<UTC>` は `endedAtUtc` の `yyyyMMdd'T'HHmmssfff'Z'`（JSON の時刻と同じミリ秒。**同じ切り捨て済みの値から両方を作る**）。UTC、UUID、種類を本体と照合する。列挙は直下だけ。終了時刻の降順、同時刻なら UUID の順。種類ごとに正常な最新 200 件を残す。基準値は台ごとに最大 200 観測を持ち、時計逆行で保留中なら次の比較元を残す。旧基準値の上限と保留は `PL/Storage/Baseline.cs:223`、`:341`、`:365`。
- 同じディレクトリの一意な `.tmp` に `CreateNew` で書く。`Flush(true)` 後に閉じて、同一ボリューム上で rename／replace。履歴の初回と移行コピーは上書きなし。基準値の更新は読み取りから比較、書き込みまで、ユーザーと保存先を識別するプロセス間ロックの下で行う。長い採取中はロックを持たない。先に採取した回が遅れて到着した場合は古い値で比較元を更新しない。未確定の `.tmp` は一覧から除き、所有を確認できる残骸だけを後で片付ける。
- 壊れた JSON、必須欄の不整合は GUI／CLI が検出したとき `name.json.bad-<UTC>` に退避する。別ファイルを上書きせず、理由コードと件数を表示する。共有違反、権限エラー、将来の `format` は現物を保持する。退避に失敗しても壊れたファイルへ書き戻さない。MCP は読めない履歴を飛ばして件数を返す。退避も削除も行わない。
- 保存 I/O の失敗は `CheckRun` の保存状態に出す。確定した結果ファイルを削除して取り消す処理は作らない。基準値を更新できなかった回は次回までの比較元を進めない。履歴保存に先立って確定した基準値は有効な観測として残す。

## 5. ディスクの採取、鍵、移行

### 5.1 1 台分の入力

Core の `IStorageInspectionSource` は 1 回分のインベントリと、台ごとの `StorageInspectionDrive` を返す。内部入力は `PhysicalDriveNumber`、デバイス記述子由来の型番・シリアル・ファームウェア・数値のバス、容量、`UniqueId`、`Location`、健康の値と状態、採取時刻。台の認識と健康値は別の状態を持つ。健康が読めなかった台も重複判定の対象。

- `StorageCollector` の記述子・容量・列挙を共用する。点検は新しく列挙し、表示キャッシュと健康応答をドライブ番号だけで結合しない。通常権限で健康を試し、結果と同じハンドルで識別子を採る。採取を通じて同一性を確かめられない台は比較と基準値更新を止める。現行の記述子・容量・表示名の組み立ては `src/PcMieru.Core/Hardware/Collectors/StorageCollector.cs:177`、`:184`、`:262`。現行の健康収集は `src/PcMieru.Core/Hardware/Collectors/StorageHealthReader.cs:57`、`:77`、`:124`、`:219`。
- 昇格を許された回は通常権限で `denied` の番号だけを `disk-health-all --drives ...` へ渡す。既存の固定要求、nonce、結果ファイルを使う。1 要求の範囲は番号 0～255、最大 16 台。既存 Reader は分割して起動する。点検は自動で次の UAC を重ねず、許可した 1 要求で取得できなかった台を `denied` として残す。根拠は `src/PcMieru.Wire/HelperOperations.cs:41`、`src/PcMieru.Wire/DriveList.cs:6`、`:13`、`src/PcMieru.Core/Hardware/Collectors/HelperStorageReader.cs:43`、`:59`、`src/PcMieru.Core/Management/HelperClient.cs:153`、`:237`。
- `DiskOperations` のバッチ応答に `drive.<n>.identity.{serial,uniqueId,location,model,sizeBytes,bus}` を追加する。健康を取得したハンドルで素性を読み直し、採取前の素性と照合する。識別子の不一致、応答の欠落、旧 Helper の識別子なし応答は比較不可。健康だけ出せる場合は「前回と比べられません」を併記する。Helper では比較も保存もしない。既存のバッチキー、バス判別、健康ページは `src/PcMieru.Wire/Privileged/ActionResult.cs:113`、`:130`、`src/PcMieru.Wire/Privileged/DiskOperations.cs:32`、`:88`、`:111`、`:157`。既存の `HealthCommand` の応答は Core で従来の `DriveHealth` に投影する。`src/PcMieru.Cli/Commands/HealthCommand.cs:47`、`src/PcMieru.Core/Hardware/Model/StorageHealth.cs:18`。
- Core の NVMe Health と ATA SMART の解析を使う。累積 `ErrorLogEntries`、書き込みの生 Data Units、ATA の予告・しきい値の有無を点検側へ渡す。エラーログのページを取得できれば、旧判定に必要な媒体エラー区分だけを取り出す。LBA や生ページは結果へ出さない。USB／RAID で健康情報が取れない場合は `unsupported`。Core の NVMe カウンターと SMART の表は `src/PcMieru.Core/Hardware/Storage/HealthParsers.cs:67`、`:90`、`:99`、`:145`。旧入力は `PL/Storage/DiskReport.cs:61`、`:86`、`:92`。
- 旧基準値の `Counters` は `ulong`。旧 NVMe パーサーは 128 bit を `ulong.MaxValue` で飽和させる。現在値の表示は精度を保ち、旧値が飽和しているか `ulong` で正確に表せないカウンターは増分を出さない。旧形式は `PL/Storage/Baseline.cs:7`、`PL/Storage/NvmeHealth.cs:90`。

### 5.2 同じ鍵を作る

v0.7.4 のキーは次の文字列。`size` はバイト数の十進表記。`bus` は旧 `BusType` の `ToString()`。例は `Nvme`、`Sata`、`Ata`。未知の値は旧 enum と同じ数値表記。`E` は `% → %25`、次に `| → %7C`。

```text
v2|sn=<E(serial.Trim())>|uid=<E(uniqueId)>|model=<E(model.Trim())>|size=<sizeBytes>|bus=<legacyBus>
v2|sn=<E(serial.Trim())>|uid=<E(uniqueId)>|model=<E(model.Trim())>|size=<sizeBytes>|bus=<legacyBus>|loc=<E(location)>

v2|sn=SN%257%7CA_|uid=eui.0123456789abcdef|model=ATA Example SSD|size=1000204886016|bus=Sata
```

- 記述子の文字列は 旧版と同じ ASCII 抽出と `Trim()`。シリアルは採取時に末尾の `.` を落とす。`_` は残す。型番は製品名がベンダー名で始まらないときベンダー名を前置する。旧方式では `ATA` も前置対象。Core の表示用 `CleanSerial` と `JoinVendorModel` はこの変換と異なるため、鍵には記述子から得た互換用の値を使う。`PL/Storage/DiskReport.cs:26`、`:35`、`PL/Storage/StorageReader.cs:66`、`:73`、`PL/Storage/Baseline.cs:171`、`src/PcMieru.Core/Hardware/Storage/StorageProtocol.cs:118`。
- `UniqueId` は `IOCTL_STORAGE_QUERY_PROPERTY` の `StorageDeviceIdProperty = 2`。デバイス関連の binary ID のみ。旧版のサイズ・型のフィルターを移し、`eui.` を優先し、次に `naa.`。小文字 hex。全 0／全 FF、ポートの識別子、壊れた記述子は採用しない。`Location` は `IOCTL_SCSI_GET_ADDRESS`（`0x00041018`）。`Port:Path:Target:Lun` の十進表記。読めなければ空。`PL/Storage/StorageProtocol.cs:42`、`:67`、`:97`、`PL/Storage/StorageReader.cs:33`、`:81`。
- 強い鍵は、有効なシリアルか空でない `UniqueId` があるもの。シリアルの判定は旧版どおり「`0` 以外の ASCII 英数字が 1 文字でもある」。比較は位置なしの強い鍵の完全一致。同時に同じ鍵が出たら `twins.json` に永続化する。過去の双子も比較不可。全台に異なる位置が取れた双子は位置つき鍵で記録だけ続ける。双子情報の書き込みに失敗した回は比較と基準値の更新を見送る。`PL/Storage/Baseline.cs:175`、`:190`、`:279`、`:308`。
- 連続性は旧版どおり、NVMe の通電時間・書き込み単位、ATA 9 の逆行を見る。NVMe の通電時間だけは経過実時間に 3 時間の余裕を足した上限も見る。時計逆行で `Withheld` になった記録は次の比較元から外す。遅れて届いた回との区別は §14.5 の採取番号 `acq`。今回のインベントリが不完全で重複を除外できないときは差分を出さない。`PL/Storage/Baseline.cs:341`、`:382`、`:407`。
- 強い鍵の表示番号は `disk-ordinals.json` に保持する。弱い鍵や恒久番号をまだ持たない台は、その結果内で未使用の `disk-N` を割り当てる。弱い鍵と番号の対応は保存しない。番号を履歴間で名寄せする用途には使わない。

### 5.3 v0.7.4 からの読み取り専用移行

最初の記憶媒体点検で、比較の前に `%APPDATA%\PartsLife\checks\` を読む。旧版の `BaselineHistory` は camelCase の `{key, model, entries:[{at,counters,withheld?}]}`。`twins.json` はキー文字列の配列。旧ファイル名は `disk-<SafeName(label)の先頭40文字>-<SHA-256(UTF-8 key)の先頭8バイトを小文字hex>.json`。`PL/Storage/Baseline.cs:7`、`:23`、`:196`、`:232`、`:243`、`:474`、`:537`。

1. 旧データは読み取り専用で開く。鍵とファイル名のハッシュ、entries、counters、twins を検証する。読める履歴と双子の集合をステージングする。旧版が書き込み中なら次回に再試行できる状態にする。
2. 先に旧双子集合を `imported-twins.json` へ上書きなしで確定する。比較時は現行 `twins.json` と和集合を取る。既に有効な取り込み先があれば再利用する。双子集合が壊れている場合は移行完了を立てず、比較を止めて理由を出す。
3. 各 v2 履歴を上書きなしで確定する。既存の台の履歴を優先する。中断時は `.tmp` を除いて再試行できる。完了マーカーにはファイルの内容や鍵を載せない。旧側の書き込み、改名、隔離、削除は行わない。v1 のシリアル単独ファイルを見つけた場合は `baseline_legacy_refused` を出す。旧ツール内の v1 移行は元ファイルを `.migrated` に改名する実装。`PL/Storage/Baseline.cs:450`、`:470`。
4. 新しい点検結果の履歴はその回から始まる。旧データから `pcm-check/1` を合成しない。移行元と範囲は `docs/unify/SPEC-unify.md:181`。

## 6. 問い合わせの期限と負荷測定

### 6.1 イベントログと WMI

```csharp
internal interface IEventCounter
{
    Task<CheckReading<long>> CountAsync(
        EventCountQuery query, QueryDeadline deadline, CancellationToken ct);
}

internal interface IWmiReader
{
    Task<CheckReading<IReadOnlyList<T>>> ReadAsync<T>(
        WmiQuery<T> query, QueryDeadline deadline, CancellationToken ct);
}
```

`QueryDeadline` は単調時計の絶対期限。システム点検のイベント群は同じ 5 秒の期限を共有する。開始時に直近 7 日の窓を固定する。`EvtQuery`、列挙、カウント確定までが期限内。固定の単一チャンネル XPath には `EvtQueryChannelPath`（`0x1`）を使う。`TolerateQueryErrors` の値は `0x1000`。単一の式を厳格に評価するため、このフラグは付けない。旧フラグとループは `PL/SystemInfo/EventCounts.cs:19`、`docs/unify/SPEC-unify.md:78`。

`EvtNext` に残り時間以下の待ちを渡し、バッチの前後で期限と停止を確かめる。最後の `ERROR_NO_MORE_ITEMS` まで到達した場合だけ件数を返す。0 件もこの経路で確定する。期限切れ、途中のエラー、停止、打ち切りは途中の件数を破棄する。イベントとクエリのハンドルは所有スレッドで閉じる。

WMI は固定の問い合わせごとに接続、実行、全件列挙・変換を 5 秒で包む。ページファイルの 2 問、PCB1 の互換フォールバックは、それぞれの項目の期限を使い回す。`EnumerationOptions.Timeout` に残り時間を渡し、外側の単調時計でも待ちを切る。Windows 呼び出しが残る場合に備え、イベントログと WMI に独立した有限のバックグラウンド実行枠を置く。枠はネイティブ呼び出しが戻るまで解放しない。連打でブロック中のワーカーを積み増さない。Core から経由する OS の DeviceGuard 問い合わせも、この期限管理で点検結果を待たせない。既存の経路は `src/PcMieru.Core/Hardware/RealHardwareProvider.cs:60`、`:139`、`src/PcMieru.Core/Hardware/Collectors/OsCollector.cs:44`。

### 6.2 CPU とメモリ

- CPU は論理 CPU ごとに `BelowNormal` の専用スレッドを 1 本。group と CPU 番号で pin し、pin の成否を持つ。既知の入力に対するチャンクの答えを照合する。停止はチャンク境界。負荷が残っている間は完了を通知しない。旧チャンク、pin、停止と join は `PL/Load/CpuLoadTest.cs:53`、`:79`、`:92`、`:105`、`:124`、`:147`。
- 点検専用 PDH クエリに `\Processor Information(_Total)\Processor Frequency` と `\Processor Information(_Total)\% Processor Performance` を登録し、同じ収集時点の 2 値で `frequency × performance / 100` を計算する。欠測や不正なサンプルを 0 MHz にしない。現行 `PdhSystemSampler` はすでに両方を登録している（u0.3 までの「片方だけ」は誤りで、u0.4 で直した）。既存の読み取り規則を共用し、クエリは別インスタンスにする。`PL/Load/ClockSampler.cs:22`、`:38`、`src/PcMieru.Core/Collection/PdhSystemSampler.cs:44`、`:224`、`:246`。
- CPU パッケージ電力は `\Energy Meter(*)\Power` のパッケージインスタンスだけを読み、確認済みの mW を合計して W にする。列挙、単位、サンプルが確かめられなければ測れず。参考実装は後段ブランチの `pl-sensors/src/PartsLife.Checks/Recording/Sources.System.cs:12`、`:45`、`:189`。移すのはパッケージの選別と換算規則。CPU 温度は読まない（項目を置かず note だけ。§3.2）。
- メモリは空きの既定 25%、最大 50%。`max(1 GiB, 物理容量の10%)` を空け、64 MiB ごとに確保してページを触る。実行中も空きを監視し、下限に達したら `stopped` と `memory.pressure`。停止・期限は小区画で確認する。確保済み範囲は `finally` で解放する。旧計画、空き監視と解放は `PL/Load/MemoryTest.cs:52`、`:61`、`:91`、`:121`、`:169`。

## 7. GUI

- `PageCatalog` に `checks`／`gui.nav.checks` を `diagnose` の直前に追加。`ChecksPage : PageBase` に `SystemCheckTab`、`LoadCheckTab`、`StorageCheckTab`、`CheckHistoryTab` を置く。`GuiContext` に点検の実行と閲覧を差し込み、実行状態は `CheckController` が持つ。既存のページ順、遅延生成、依存の受け渡しは `src/PcMieru.Gui/Shell/PageCatalog.cs:3`、`:9`、`src/PcMieru.Gui/Shell/MainForm.cs:169`、`src/PcMieru.Gui/Program.cs:48`。
- 「システム」は「点検する」。項目、判定、値、根拠を表示。「負荷」は CPU／メモリ、30 秒／2 分／5 分、進捗、速度、「止める」。「記憶媒体」は台ごとの状態と `1,204 → 1,227（+23）` のような根拠。通常権限で拒否された台があれば「管理者で読み直す」。管理者読み直しは 1 回の新しい採取として保存する。「履歴」は日時、種類、判定、実行結果。選ぶと保存時の値を表示する。
- 状態は未実行、実行中、停止待ち、完了、停止、失敗。完了後に保存失敗を追記できる。履歴は読み込み中、空、一覧、読み取りの警告を持つ。タブと左ナビを移っても実行は続く。閉じるときは確認し、承認後に停止と保存の完了を `await` して閉じ直す。`OnFormClosing` は最初の閉じ要求をキャンセルし、再入を 1 回に抑える。既存の終了処理は `src/PcMieru.Gui/Shell/MainForm.cs:284`。
- `HardwarePage` に「構成コードをコピー（/my-config 用）」を追加。公開する部品一覧とコードのプレビューを表示し、「コピー」と「JSON を保存」を出す。空の構成や欠測の根拠も見える形にする。隣のボタンと配置を合わせる。先行実装のクリックとプレビューは `area/parts-share@9e068c4:src/PcMieru.Gui/Pages/HardwarePage.cs:84`、`:488`、`:513`。
- `--smoke --shots --fakes` は `FakeCheckRunner`、メモリ内履歴、偽 PCB1 を組み立て時に注入する。4 判定、停止待ち、停止済み履歴、増分、双子で比較不能、PCB1 のプレビューを写す。実負荷、実 WMI、UAC、実ユーザーの点検データへの書き込みを伴わない。既存の fake 起動とページ巡回は `src/PcMieru.Gui/Program.cs:48`、`src/PcMieru.Gui/Smoke/SmokeRun.cs:14`、`:280`。発行時の fake smoke は `tools/publish.ps1:298`。

## 8. CLI と MCP

```text
pcmi check system [--json]
pcmi check storage [--elevate] [--json]
pcmi check history [--json]
pcmi parts --code [-o file]
```

`CheckCommand : ICliCommand` を登録する。`--elevate` は `storage` だけ。JSON の system／storage は 1 回分の `pcm-check/1`。history は `{ "entries": [ ... ], "warnings": [ ... ] }`。要約行には `runId, kind, startedAtUtc, endedAtUtc, outcome, verdict, counts` を載せる。既定は時刻降順で保持分を返す。空なら `entries: []`。人向けは同じデータを表にする。stdout にデータ、stderr に診断。`parts --code` の stdout はコード 1 行。`-o` には対応する JSON を書く。`--share` と `--code` の同時指定は引数誤り。既存の登録、出力規約と先行 `parts` の引数は `src/PcMieru.Cli/CliApp.cs:6`、`:101`、`area/parts-share@9e068c4:src/PcMieru.Cli/Commands/PartsCommand.cs:15`、`:30`。

| 終了コード | 段 A での意味 |
|---|---|
| `0` | 正常に完了して履歴を保存した、または履歴・PCB1 の取得が成功した。点検の注意、要確認、一部欠測を含む。 |
| `1` | 実行失敗、停止、結果の保存失敗、履歴を開けない I/O 障害。取得済み結果があれば `--json` で返す。 |
| `2` | コマンド、動詞、引数の誤り。 |
| `4` | `--elevate` が拒否され、必要だった台を 1 台も読み直せなかった。取得済み結果と個別の理由は返す。 |

空の履歴は成功。値は既存の `ExitCode`（`src/PcMieru.Cli/ExitCode.cs:5`。0 Success・1 Failure・2 BadArguments・4 Denied）をそのまま使い、新しい値は足さない（凍結済みの番号）。停止は既存の Ctrl+C トークンを使う。強制終了までの猶予が点検の後処理を打ち切らないように、CLI の終了経路を点検コマンドと MCP の stdin 待ちで確認する。現行は `src/PcMieru.Cli/Program.cs:42`。

MCP `get_check_history` は `IReadOnlyCheckHistory` のみを追加注入する。`tools/list` の `readOnlyHint: true`。引数は `{kind?, limit?}`（既定 20、1～200）、または `{runId}`。前者は CLI と同じ要約、後者は当時の `pcm-check/1` を `{result}` で返す。存在しない ID は tool error。既存のサイズ制限とキャンセルを適用する。出力は共通 redactor へ通す。チェック履歴は起動時に伏せ字解除が指定されても伏せ字済み。採取、移行、昇格、退避を呼べる型をサーバーに渡さない。現行の境界、注釈、出力は `src/PcMieru.Cli/Mcp/McpServer.cs:19`、`:40`、`:256`、`:324`、`:387`、`src/PcMieru.Cli/Mcp/McpTools.cs:23`、`src/PcMieru.Cli/Commands/McpCommand.cs:23`。境界の固定試験は `tests/PcMieru.Cli.Tests/McpBoundaryTests.cs:43`、`:59`、`:68`、`:84`。

## 9. PCB1 の互換試験

- `BuildConfig`、`BuildRules`、`BuildCode`、`BuildCollector` を §1 のとおり移す。`PCB1.`＋UTF-8 の compact JSON の base64url。`v: 1`、`src: "parts-life"`、カテゴリ・プロパティ順、省略、重複集約、長さ上限をそのまま固定。保存版は同じ内容の整形済み JSON。`PL/Build/BuildCode.cs:29`、`:44`、`:138`、`:155`。
- Windows 入力は旧 `BuildSources` の順序と意味を守る。CPU、基板、メモリ、DXGI と WMI の GPU フォールバック、物理ディスク、モニター、光学ドライブを同じ条件で取る。WMI の薄いアダプターに期限を追加する。Core の収集値への差し替えは、同じ入力と同じ実機で文字列一致を証明できたものから。旧ソースとフォールバックは `PL/Build/BuildCollector.cs:11`、`:72`、`:94`、`:126`、`:141`、`:165`、`:174`。
- Golden は `25e4571` を一時ディレクトリに展開し、そのリビジョンの Checks を参照する小さなコンソールハーネスで作る。旧 GUI や Helper を起動せず、`BuildCode.Make(BuildCollector.Collect(sources))` を直接呼ぶ。まず合成の `BuildSources` で、日英名、同型複数、XMP、4 GiB 超 VRAM、USB／仮想機器、光学ドライブ、欠測、長さ超過を固定する。コードの UTF-8 バイト一致と、保存 JSON の内容一致を確認する。移植する旧試験は `PL-tests/BuildCodeTests.cs:23`、`:65`、`:149`、`:223`、`:278`、`:319`、`:353`、`:402`、`:438`。
- 実機比較では同じハーネスで `BuildSources` の材料を一度だけ採り、必要な公開型番と数値だけの試料を作って旧・新に再生する。続けて同じ機器状態で旧・新の Windows 収集経路の一致も確認する。試験用の一時ルートにだけ出力し、オーナーの `%APPDATA%\PartsLife`、基準値、設定には触れない。Golden にシリアル、PC 名、パスを含めない。旧実機テストの入口は `PL-tests/BuildCodeTests.cs:503`。
- `pcm-parts/1` との相互変換は作らない。出荷前に PCB1 と v0.3.0 の `pcm-parts/1` golden をサイトの受け渡し側へ渡し、実パーサーで受理・解釈を確認する。`docs/unify/SPEC-unify.md:131`、`:139`、`:236`。

## 10. `tools/measure.ps1`

`docs/SPEC.md:232` と `docs/unify/SPEC-unify.md:197` に従う。主の窓を測る。

1. 発行済みの実行ファイル、試験種別、出力先を引数で受け取る。7950X／64 GB／Win11 の基準機で、プロセスタブ、1 秒間隔、既定列、同じ窓寸法、GPU 採取あり。OS build、論理 CPU 数、電源プラン、ドライバ、発行設定、比較対象の版を記録する。
2. 外部から 1 秒ごとに CPU 時間、private bytes、working set、ハンドル数を記録する。PID と生成時刻で GUI とその SensorHost を追う。入れ替わり時の差分を別プロセスにまたがせない。GUI 単体と合計を出す。合計 working set は共有ページを二重に数え得る。
3. 最初の 30 秒と後続 5 分の平均・最大を別に出す。`ΔCPU秒 / Δ実秒 / 論理CPU数 × 100` と `ΔCPU秒 / Δ実秒 × 1000`（ms/s）を併記。PCミエルとタスクマネージャーを交互に各 3 回以上。同時に動かさない。
4. 起動の印は「一覧の初回描画」と「2 点目で CPU 値を含む一覧の描画」。測定時だけ有効な GUI の通知を外部で受け、起動時刻から別々に測る。warm は直前の一回を閉じた後。cold は OS 再起動後の最初の回を記録する。印が来なければ欠測。
5. 長時間は 2 時間、全タブを巡回し、試験用プロセスの生成・終了を繰り返す。private bytes とハンドル数の推移を出す。生 CSV と要約 JSON を残す。作業セットの強制削減は行わない。目標は private 100 MB 未満、定常 CPU 平均 0.3% 未満かつ 10 ms/s 未満、warm 起動 1 秒未満。ZIP の実サイズも v0.2.0 の 51.6 MB と比較する。公開値は計測後に決める。

## 11. 試験と作業順

### 試験

| 種別 | 確認すること |
|---|---|
| Unit | イベントの正常終端、0 件、複数バッチで全体期限超過、途中エラー、停止、ハンドル解放。WMI の接続・列挙・フォールバックの期限、遅れて返る値の破棄。CPU のチャンク、クラス別中央値、pin 失敗、join、PDH の組み合わせと欠測。メモリの計画、パターン、一部照合での停止、空き低下、解放。移植元は `PL-tests/LoadTests.cs:13`、`:76`、`:112`、`:148`、`:199`、`PL-tests/ScanGpuSystemTests.cs:189`、`:218`、`:263`。 |
| Fixture | 4 種類の `pcm-check/1`。`info` の項目が欠けても総合が `ok` のままのこと（CPU は温度なしで `ok` になり得る）、`.delta` の判定表（増加・横ばい・比較不能）、全欠測、部分欠測、停止、実行失敗、既知の異常＋停止、個人情報混入を固定。履歴の原子的書き込み、同時 writer、200 件、壊れた履歴と将来版、MCP 閲覧中に更新。旧基準値と twins の実ファイル形、完全一致の鍵、`ATA ` 前置と末尾 `_`、EUI 優先、NAA、SCSI、空識別子、双子、番号再利用、カウンター逆行、時刻逆行、飽和、既存の移行先。移植・拡張元は `PL-tests/BaselineIdentityTests.cs:60`、`:104`、`:230`、`:269`、`:385`、`:531`、`:570`、`:598`、`tests/PcMieru.Core.Tests/Hardware/HelperStorageReaderTests.cs:208`。 |
| CLI／MCP／GUI | CLI の引数、終了コード、標準出力と標準エラー。MCP の list/call、要約と詳細、検索上限、伏せ字、書き込みと採取の不在。fake smoke でナビ、全タブ、4 判定、タブ移動、閉じるときの停止、PCB1 プレビューと保存機能を確認。既存の MCP 境界は `tests/PcMieru.Cli.Tests/McpBoundaryTests.cs:43`、`:97`。 |
| RealMachine | 通常権限のシステム点検。NVMe／SATA／USB の取得元と状態。UAC 許可・拒否・別資格情報、選択中の抜き差し、連続 2 回の比較。CPU とメモリは短時間の実行と途中停止で作業終了を確認。英語カウンターを日本語 OS で確認し、電力の対応・欠測を確認。PCB1 は §9。負荷と UAC の試験は通常 CI から外す。現行の RealMachine 除外は `.github/workflows/ci.yml:29`、`:33`。 |

### 実装の順番

1. **型と形式**。`PcMieru.Checks.csproj`、`Contracts/`、`Serialization/`、`PcMieru.slnx`、`tests/PcMieru.Checks.Tests/`。4 種の schema fixture と総合判定を先に固定。
2. **結果ストア**。`Checks/History/` と試験。原子的な保存、退避、保持、読み取り専用の閲覧。
3. **ディスク識別子と応答**。Core の `StorageCollector.cs`、`StorageHealthReader.cs`、`HelperStorageReader.cs`、`Hardware/Model/`、Wire の `StorageProtocol.cs`、`DiskOperations.cs`、`ActionResult.cs`。旧鍵の入力と差し替わりの fixture。NVMe の Error Information（ログ 0x01、16 件 × 64 B）の読み取りを Core と Helper に足し、件数（媒体の誤り＝Status Code Type 2 の数・読めた件数）だけを返す（§14.4）。Helper の publish も確認。
4. **基準値と移行**。`Checks/Storage/` の識別、連続性、移行、双子、増分（§14.5 の採取番号 `acq`・新しいビット・逆行）の実装と fixtures。3 の採取ポートを接続。
5. **システム点検**。`Checks/SystemInfo/`、期限付き問い合わせ、Core 採取の組み立て、判定の移植。イベント列挙と WMI の故障注入。
6. **CPU 負荷**。`Checks/Load/CpuLoadTest.cs`、`Steadiness.cs`、Core のトポロジーと PDH 点検セッション。サンプル、割り当て、停止の試験。
7. **メモリ負荷**。`Checks/Load/MemoryTest.cs`、`Patterns.cs`、部分結果と解放の試験。CPU と共通の実行・保存境界を接続。
8. **PCB1**。`Checks/Build/`、旧版ハーネス、golden、`NOTICE.txt`。出力の比較を終えてから GUI と CLI に渡す。
9. **GUI の点検**。`Pages/ChecksPage.cs`、各タブ、`CheckController`、`GuiContext.cs`、`PageCatalog.cs`、`MainForm.cs`。終了と停止を先に通す。
10. **PCB1 の UI と smoke**。`HardwarePage.cs`、プレビュー、`SmokeRun.cs` と fake。4 タブの写しとテーマ・言語を確認。
11. **CLI／MCP**。`CheckCommand.cs`、`PartsCommand.cs`、`McpCommand.cs`、`McpServer.cs`、`McpTools.cs`、CLI の試験。MCP 境界試験を通す。
12. **計測・配布**。`tools/measure.ps1`、測定時の描画通知、`tools/publish.ps1`。発行物、計測、実機、サイトの受け入れを確認して出荷判定へ渡す。

## 12. 仕様で決まっていない点

| 問い | 案 |
|---|---|
| §6 の `pcmi parts --code` と段 A の列挙の関係はどうするか | 段 A に入れる。PCB1 の CLI 入口で、先行 `parts` コマンドに追加できる。`pcm-parts/1` の電力の受け渡し改修は別の変更とする。 |
| 移行はいつ実行し、旧 v1 をどう扱うか | 初回の記憶媒体点検で v0.7.4 の v2 と twins を写す。v1 は拒否理由を表示し、旧ファイルを保持する。途中失敗は次回に再試行する。 |
| `disk-health-all` が 1 要求に収まらない場合はどうするか | 段 A の点検ボタン 1 回につき UAC 1 回。16 台を超えた分は個別に表示する。全台を 1 回で昇格する必要が出たら、固定要求の上限を別途検討する。 |
| 旧版で注意だったエラーログ・CRC 増加と、仕様の「増えた数は要確認」をどう両立させるか | **決定（Opus）**: 旧 `GrowthVerdict` の表に従う（§3.3）。一律 `check` にはしない。CRC はケーブルのことが多く、要確認にすると根拠以上に不安にさせる。仕様 u0.4 §4.4 を合わせた。 |
| CPU 温度が測れない場合の総合判定はどうするか | **決定（Opus）**: 温度の行は置かず note。観測だけの項目は `info` で総合に入れない（§3.0）。常設の測れず行があると CPU の総合が毎回「測れず」になり、利用者が判定を読まなくなる。仕様 u0.4 §4.0。 |
| 基準値がない、または記憶媒体が弱い鍵の場合の番号はどうするか | 結果内の一時 `disk-N` を使う。強い鍵の採番だけ保存する。履歴の番号から機器の同一性を推定しない。 |
| WMI の期限に達しても COM 呼び出しが戻らない場合はどうするか | 結果を期限内に返し、バックグラウンドの実行枠を保持したまま戻りを待つ。再実行は空き枠を使い、使い切っていれば期限内に欠測を返す。 |
| PCB1 で旧版と Core の採取値が異なるときはどちらを使うか | PCB1 は旧版と同じ入力の意味を優先する。変更は golden と同一実機での文字列一致を条件にする。 |
| 測定用の起動マーカーをどこへ出すか | `measure.ps1` が指定した試験専用のローカル出力先へ、描画の二つの時刻だけを出す。通常起動では出力先を開かない。 |

この文書の項目 ID、保存名、API、終了コードの割り当て、未決事項の回答は段 A の設計案。参照コミットの読み取りまでを実施した。ファイル編集、golden 採取、実機計測、サイトの受け入れ確認は実施していない。

## 13. Opus の点検で直した所（下書きから）

1. **観測だけの項目を総合判定から外した**（§3・§3.0・例）。下書きは全項目を総合に入れていたため、常に読めない CPU 温度の行と、対応の少ない CPU 電力の行のせいで、CPU 点検の総合が毎回「測れず」になる形だった。`info` を足して、閾値を持たない項目は総合に入れない。仕様 u0.4 §4.0 も同じに直した。
2. **CPU 温度は項目を置かず note**（§3.2・§6.2）。
3. **`.delta` の判定を旧 `GrowthVerdict` の表に揃えた**（§3.3）。下書きは異常系の増加を一律 `check` にしていた。旧版は CRC（ATA 199）とエラーログの増加を「注意」にしている（`PL/Storage/Baseline.cs:59`）。
4. **PDH の記述**（§6.2）。Core は既に `Processor Frequency` と `% Processor Performance` の両方を登録している（`src/PcMieru.Core/Collection/PdhSystemSampler.cs:44`）。仕様 u0.2〜u0.3 の「片方だけ」が誤りで、仕様の側を直した。
5. **終了コード**（§8）。既存の凍結済み `ExitCode` の値（4 = Denied）を使い、新しい値を作らないことを明記。
6. JSON 例の `memory.verified` と `memory.mismatches` に `reason: partial` を付けた（止まった回の照合は途中まで）。
7. **codex の再点検（2 回目）を受けて §14 を足した**。`.delta` の例外（比べられない物は `info`）、GPU の汎用ドライバの行と状態 22、イベントの警告の扱い、`cpu.variation` を `info` にして持続性を 1 判定に、クロックの持続性が出ないときの扱い、NVMe の危険温度の累計とエラーログ 0x01、`predict_failure` の偽と欠け、総合 4 値と項目 5 値、`source`／`evidence` の固定一覧、遅れて届いた回と時計の逆行の区別（採取番号 `acq`）。

8. **点検 1b55576（段 A 手順 1）の後で決めた 3 点**（§3・§4）。読み手は知らない ID・コードを飛ばす（戻した版が新しい版の履歴を `.bad` にしないため）。`baseline_*` は `.delta` の行だけ。ファイル名の時刻は JSON と同じミリ秒。あわせて `ok` と `reason: partial` の組を不正にした。

## 14. 判定表（確定。codex の再点検 2 回目を受けて Opus が旧コードから起こした）

旧コードの行は `PL/` が `pl-cloud/src/PartsLife.Checks/`（v0.7.4 `25e4571`）。閾値の境界（`<` か `<=` か）は旧コードどおりで、fixture に境界の値を直書きする。

### 14.0 共通

- 判定するか（判定する／`info`）は ID ごとに固定。例外は 2 つだけ:
  1. 比べられなかった `.delta`（値 `null`）は `info`（§14.4）。
  2. GPU のデバイス状態が 0 以外のとき、その GPU の `system.gpu.driver_age` と `system.gpu.generic_driver` は `info`（旧は状態のエラーを最優先にして他を言わない。`PL/SystemInfo/SystemCheck.cs:220`）。
- 判定する項目で値が取れない（`null`）ときは `unavailable`。旧は一部で「OK（報告なし）」にしていた物（メモリの速度が 0）も、ここでは `unavailable` にそろえる（OK は測れたときだけ、仕様 §4.0）。
- 旧の総合判定（`PL/Storage/StorageJudge.cs:11`）は「測れず」を順位から除いていた。新しい総合は仕様 §4.0 の順位（要確認＞注意＞測れず＞OK）で、ここは**意図して変える**。
- `source` の固定値: `os`、`win32`（GlobalMemoryStatusEx などの Win32 API）、`smbios`、`setupapi`、`dxgi`、`wmi`、`eventlog`、`pdh`、`energy_meter`、`load`、`nvme`、`ata`、`storage_query`、`baseline`。足すときはこの表を直してから。
- `evidence` の固定キーと型: `code`（下の一覧の文字列）・`before`／`after`（十進の文字列）・`sinceUtc`（UTC ISO 8601）・`newBits`（int）・`totalBytes`（十進の文字列）・`diskId`（`disk-N`）・`efficiencyClass`（int）・`cv`（number。変動係数 ×100）・`clockSteadiness`（number・%）・`warningTempMinutes`／`criticalTempMinutes`（十進の文字列）・`recentMediaErrors`（int）・`recentEntries`（int）・`capacities`（十進の文字列の配列）。
- `evidence.code` の固定値: `baseline_first`、`baseline_no_identity`、`baseline_duplicate`、`baseline_device_changed`、`baseline_counter_reset`、`baseline_clock`、`baseline_concurrent`、`baseline_missing_counter`、`baseline_read_failed`、`baseline_legacy_refused`、`counter_increased`、`counter_decreased`、`new_bits`、`likely_cable`、`mixed_capacity`、`below_rated`、`above_rated`、`disabled_code22`、`device_error`、`too_short`、`pin_unknown`、`pattern_mismatch`、`compute_mismatch`、`past_threshold_breach`、`current_threshold_breach`、`temp_over_critical_before`、`media_errors_in_log`。

### 14.1 システム

| ID | 判定 |
|---|---|
| `system.os.version` | `info`。値 `{name: string, displayVersion: string\|null, build: int, ubr: int\|null}` |
| `system.os.uptime` | 30 日超（`> 30` 日）は `caution`、他は `ok`（`SystemCheck.cs:182`） |
| `system.memory.modules` | 判定する。0 枚または一覧が取れない → `unavailable`。容量の違うモジュールが混ざる → `caution`（`code: mixed_capacity`、`capacities`）。他は `ok`（`:195`） |
| `system.memory.configured_speed`（各枚） | 定格か設定のどちらかが 0・欠け → `unavailable`。設定 < 定格 → `caution`（`below_rated`。故障ではないと画面で言う）。設定 > 定格 → `ok`（`above_rated`）。等しい → `ok`（`:207`） |
| `system.gpu.device_status`（各 GPU） | 22 → `ok`（`disabled_code22`）。他の 0 以外 → `check`（`device_error`）。0 → `ok`（`:220`） |
| `system.gpu.generic_driver`（各 GPU・bool） | 名前が Basic Display／基本ディスプレイ → `caution`、他は `ok`。状態が 0 以外なら `info`（§14.0） |
| `system.gpu.driver_age`（各 GPU・`d`） | 判定は小数の日数で `(今 − ドライバの日付).TotalDays > 730` → `caution`（旧どおり。値は切り捨ての整数で出すが、判定に整数を使わない）。日付が無い → `unavailable`。状態が 0 以外なら `info` |
| `system.storage.health`（各台） | Windows の HealthStatus 2、または OperationalStatus に 3・5・6・7 → `check`。HealthStatus 1 → `caution`。0 → `ok`。他・読めない → `unavailable`（`:244`）。値は `healthy\|warning\|critical` |
| `system.storage.free`（各ボリューム） | 空き率 < 3% → `check`、< 10% → `caution`、他 `ok`。容量 0 は空き率 1 として扱う（旧どおり） |
| `system.pagefile.count` | 0 → `caution`、他 `ok` |
| `system.pagefile.peak`（各ファイル） | 割当が正で、ピーク > 割当 × 0.8 → `caution`、他 `ok`（`:280`） |
| `system.eventlog.whea7d` | 1 以上 → `check`、0 → `ok`。読めない → `unavailable` |
| `system.eventlog.whea_warning7d` | 1 以上 → `caution`、0 → `ok` |
| `system.eventlog.{disk,ntfs,stornvme,volmgr}7d` | 10 以上 → `check`、1〜9 → `caution`、0 → `ok`（`:308`） |
| `system.eventlog.{disk,ntfs,stornvme,volmgr}_warning7d` | `info`（旧は文に添えるだけで判定に使わない） |
| `system.eventlog.kp41_7d` | 1 以上 → `caution`、0 → `ok` |

### 14.2 CPU

| ID | 判定 |
|---|---|
| `cpu.compute_errors` | 1 以上 → `check`（`compute_mismatch`）。照合 0 回 → `unavailable` |
| `cpu.steadiness` | 旧 `Steadiness.Judge` の 1 判定（`PL/Load/Steadiness.cs:31`）: 持続率 < 70 → `check`、< 90 または変動係数 > 0.10 → `caution`、他 `ok`。根拠に `cv`。立ち上がりの 2 個を除いて 6 個未満 → `unavailable`（`reason: partial`、`too_short`） |
| `cpu.variation` | `info`（`cpu.steadiness` の判定の材料。独立には判定しない） |
| `cpu.peer_ratio`（各論理 CPU） | 同じ効率クラスの中央値（偶数個なら上側＝`ElementAt(n/2)`）の 75% 未満 → `caution`。中央値 0 → `unavailable`。pin の成否が分からない → `unavailable`（`pin_unknown`）。論理 CPU が 1 個なら項目を出さない |
| `cpu.effective_clock` | 値は取れたサンプルの平均。全サンプルでクロックが取れて持続性が計算できたときだけ判定: クロックの持続率 < 85 → `caution`、他 `ok`（根拠 `clockSteadiness`）。平均は取れたが持続性が出ない → 値はそのまま `unavailable`・`reason: partial`。1 つも取れない → `null`・`unsupported` |

### 14.3 メモリ

| ID | 判定 |
|---|---|
| `memory.mismatches` | 1 以上 → `check`（`pattern_mismatch`。停止しても消さない）。照合 0 バイト → `unavailable`。止まった回で 0 件 → `unavailable`・`reason: partial`・値 0 |

他のメモリの項目は `info`（§3.0）。空きの低下で止めた回は note `memory.pressure`。

### 14.4 記憶媒体（台ごと）

絶対値の行（旧 `GrowthFinding`、`PL/Storage/StorageJudge.cs:236`）は、前回と比べられたかで分かれる。「増えた」は §14.5 の決まりで判断する。

| ID | 0 | 比べられて増えた | 比べられて増えていない | 比べられない（初回など） |
|---|---|---|---|---|
| `storage.nvme.media_errors` | `ok` | `check` | `ok` | `caution` |
| `storage.ata.5`／`.187`／`.196` | `ok` | `check` | `ok` | `caution` |
| `storage.ata.197`／`.198` | `ok` | `check` | `check`（今も読めないセクタがある） | `check` |
| `storage.ata.199` | `ok` | `caution`（`likely_cable`） | `ok` | `caution`（`likely_cable`） |

| ID | 判定 |
|---|---|
| `storage.health` | §14.1 の `system.storage.health` と同じ決まり |
| `storage.temperature` | NVMe: 現在温度が読めない → 累計にかかわらず `unavailable`（旧どおり）。≥ 80 → `check`、≥ 70 → `caution`、他 `ok`。ただし危険温度超過の累計分 > 0 なら少なくとも `caution`（`temp_over_critical_before`、根拠 `warningTempMinutes`・`criticalTempMinutes`。Core の `HardwareDetails.cs:159` が既に読んでいる）。ATA: 194、無ければ 190 の値が 1〜127 のとき ≥ 70 → `check`、≥ 60 → `caution`、他 `ok`。範囲外・無し → `unavailable` |
| `storage.nvme.critical_warning` | 0 以外 → `check`、0 → `ok` |
| `storage.nvme.available_spare` | しきい値 > 0 かつ 予備 < しきい値 → `check`。予備 < しきい値 + 10 → `caution`。他 `ok` |
| `storage.nvme.percentage_used` | ≥ 100 → `check`、≥ 90 → `caution`、他 `ok` |
| `storage.nvme.error_log_entries` | 最近のエラーログ（Error Information、ログ 0x01、最大 16 件）に媒体の誤り（Status Code Type = 2）が 1 件以上 → `caution`（`media_errors_in_log`、根拠 `recentMediaErrors`・`recentEntries`）。そうでなく、比べられて累計が増えた → `caution`。他 `ok`。ログ 0x01 が読めないときは累計の増え方だけで判定する（`PL/Storage/StorageJudge.cs:126`） |
| `storage.ata.threshold_status` | しきい値が読めない → `unavailable`。しきい値 0 の属性は比べない（旧 `PL/Storage/AtaSmart.cs:17`）。今の値 ≤ しきい値の属性がある → `check`（`current_threshold_breach`）。過去に割った（worst ≤ しきい値）→ `caution`（`past_threshold_breach`）。他 `ok` |
| `storage.ata.predict_failure` | 真 → `check`。偽 → `ok`。この窓口が読めない台では**項目を出さない**（旧は真のときだけ出していた。出さないことで、SMART だけ読める台が毎回「測れず」にならない） |

ログ 0x01 の読み取りは Core と Helper に**足す**（今の PCミエルはヘルスのログ 0x02 だけ。`src/PcMieru.Core/Hardware/Collectors/StorageHealthReader.cs:114`）。結果へは件数だけを出し、LBA・ページの中身は出さない。§11 の作業順 3 に含める。

### 14.5 増分（`.delta`）

- 比べられた（基準値の状態が Compared）ときだけ値を出す。値は `after − before` の符号つき十進の文字列。根拠に `before`・`after`・`sinceUtc`。
- **増えた**の決まり:
  - `storage.nvme.critical_warning.delta`: 数の大小ではなく**新しく立ったビット**（`after & ~before` が 0 以外）。根拠 `newBits`、`code: new_bits`。ビットが消えただけなら増えていない。
  - 他: `after > before`（旧 `CounterDelta.Grew`、`PL/Storage/Baseline.cs:35`）。
- 判定: 旧 `Counters.GrowthVerdict`（`PL/Storage/Baseline.cs:59`）に載る物（media_errors・critical_warning・ATA 5/187/196/197/198 → `check`、error_log_entries・ATA 199 → `caution`）は、増えたらその判定、増えていなければ `ok`（減った物は `code: counter_decreased`）。載らない物（unsafe_shutdowns・power_on_hours・data_units_written・percentage_used・available_spare・ATA 9）は常に `info`。
- 比べられない（値 `null`）ときは `info`。理由を `evidence.code` に入れる:
  - 通電時間・書き込み量・ATA 9 が減った → `baseline_counter_reset`（別の台と見なし、その台の `.delta` は全部 `null`。旧 `WentBackwards`）。これら以外の数が減っただけなら比べられたまま（上の `counter_decreased`）。
  - NVMe の通電時間が、前回からの実時間 ＋ 3 時間（旧 `HoursSlack` 2 時間 ＋ `ClockSkew` 1 時間、`Baseline.cs:386`）より多く進んだ → `baseline_clock`。
- **時計が戻ったのと、古い採取が遅れて届いたのを分ける**（codex 3 回目の指摘で直した。通し番号を記録の側だけに振ると「間に別の保存があった」ことしか分からず、どちらの採取が新しいか分からない）:
  - 採取の**始め**に、プロセス間ロックを短く取って利用者ごとの採取番号 `acq` を 1 つ進めて受け取る（`baseline\acq-seq.json`。全台で共通の 1 本）。基準値の各記録はその回の `acq` を持つ（旧ファイルの記録は 0 とみなす）。
  - 記録のときロックの下で読み直し、自分より大きい `acq` の記録がもうある → この回の採取の方が古い。**比べず、記録もしない**（`baseline_concurrent`）。無ければ、読み直した中の最後の Withheld でない記録と比べて記録する（間に保存があっても、その方が古い採取なら正しい比較元）。
  - 時計の扱いは旧の条件そのまま（`PL/Storage/Baseline.cs:351`〜`:359`）: 連続性の判定が `AheadOfClock`（NVMe の通電時間が許す上限より進んだ）なら比べない（`baseline_clock`）。そのうち**今の時刻が比較元の記録時刻より前**のときだけ `withheld` で記録し、次の回の比較元には前の記録を残す。今の時刻が前より後の `AheadOfClock` は普通に記録する。
