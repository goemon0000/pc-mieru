# PCミエル（PC Mieru）仕様書 v0.2

Windows のタスクマネージャーより軽く・見やすく・CLI と AI からも使える、プロセス管理と PC 部品の詳細表示のアプリ。
web-autoai.com が配布する（GitHub Releases・サイトに紹介ページ）。MIT。

- 2026-11-08 v0.1。Codex 相談 r1（技術選定・範囲・安全、`docs/review/codex-r1.md`）を反映。
- 2026-11-08 v0.2。Codex 点検 r2（`docs/review/codex-r2.md`）を反映。**r2 の §1 と §3 の API・安全の注意（構造体のオフセット、PDH のインスタンス解析、StartupApproved の値、EcoQoS の 2 操作、WinVerifyTrust の設定、IsProcessCritical/PPL の判定、NVMe の 128bit、EDID の辿り方 等）は、この仕様の一部として守る。** この本文と食い違うときは本文を正とし、本文に無い細部は r2 に従う。
- 方針の一言: **「取れない物を取れたと言わない」**。各値は取得元と状態を持ち、欠損を 0 や「正常」に置き換えない。
- 軽さの主張は実測だけ（§11）。測る前に「タスクマネージャーより軽い」と書かない。

## 1. 対象と前提

- Windows 10 22H2 / Windows 11、x64。日本語 UI を先、英語を次。
- 通常の利用者権限で起動する（asInvoker）。管理者が要る操作は補助 exe（§8）に閉じ込め、その都度 UAC。
- **自前のカーネルドライバは配らない。** WinRing0 も使わない。ファン・クロック・電圧の制御はしない。
- 通信しない。更新の確認は「サイトを開く」メニューだけ（v1）。
- 対象の利用者: 初心者（「何が重いのか知りたい」）〜上級者（列を足して詳細を見る・CLI・AI）。

## 2. 構成

```
PCMieru.exe            GUI（WinForms・GUI サブシステム・通常権限）。AssemblyName=PCMieru
pcmi.exe               CLI と MCP サーバ（コンソール サブシステム）。AssemblyName=pcmi
PCMieru.Helper.exe     管理者の要る型付きの操作だけ。NativeAOT の単体 exe（隣の DLL に依存しない）。1 回の UAC で 1 要求
PCMieru.SensorHost.exe ハングし得るメーカー API（NVML・ADLX）の隔離。stdout に JSON 行。AssemblyName=PCMieru.SensorHost
PcMieru.Core.dll       UI に依存しない計測・部品情報・操作・レポート・データの形
```

- **Windows のファイル名は大文字小文字を区別しない。** `PCMieru.exe` と `pcmieru.exe` は同じ名前になるので、CLI は `pcmi.exe`。
- .NET 10、WinForms＋通常 JIT。ReadyToRun で起動を速くする。トリミング・NativeAOT は GUI/CLI/SensorHost では使わない（WinForms は非対応）。Helper だけ NativeAOT（パーツぐらしの PartsLife.Check と同じ形。発行時に「exe の隣を探す読み込み」が無いことを検査）。
- 配布は自己完結（self-contained）のフォルダを ZIP 1 本に。GUI・CLI・SensorHost は同じランタイムを共有する（利用者に .NET の導入を求めない）。発行は各 exe を別フォルダに publish → 1 つに集める。同名のファイルは中身（sha256）が一致することを確かめ、不一致なら発行を止める（黙って上書きしない）。
- GUI と CLI と MCP は **Core の同じ Snapshot だけ** を使う。計算を別々に持たない。
- 再利用元: パーツぐらし（goemon0000/parts-life、同じ持ち主・MIT）の NVML・ADLX・GPU アダプタ・PDH・NVMe/SMART 解析。写した物はファイル頭に出典を書く。

## 3. データの形（Core）

```
Snapshot {
  schemaVersion: "1",
  takenAtUtc, intervalMs,
  system: { cpu, memory, disks[], nics[], gpus[], uptimeSec, disksState, nicsState, gpusState },
  processes: ProcessInfo[]
}
Value<T> = { value?: T, status: "ok"|"unsupported"|"denied"|"timeout"|"error"|"gone"|"notCollected", source: string, detail?: string }
```

- 一覧（`disks`・`nics`・`gpus`）は状態を持てないので、横に `disksState`・`nicsState`・`gpusState`（`Value<int>`）が付く。読めたときは ok と件数、読めなかったときはその status と理由。**空の一覧は、状態が ok のときだけ「無い」と読む。** GUI と CLI は、状態が ok でない空の一覧を空の枠にせず、その文を出す。状態が未設定（古い JSON・手で組んだ Snapshot）の一覧は何も言わない。
- プロセスの識別は **PID＋作成時刻**（`ProcessKey`、JSON では文字列 `"<pid>:<createTime FILETIME 10進>"`）。PID だけで操作しない。
- JSON のキーは英語で固定。単位はキー名に入れる（`workingSetBytes`、`cpuPercent`）。2^53 を超え得る値（FILETIME、128bit の NVMe カウンタ、累積の転送量）は 10 進の文字列。それ以外は number。
- 時刻は UTC の ISO 8601（表示用）。操作のキーは ISO から作り直さない。
- Snapshot は観測区間（開始・終了）を持つ。初回・復帰直後・カウンタの逆行は CPU% を `notCollected`。
- メモリの列の意味（r2 §1）: `privateWorkingSetBytes`＝WorkingSetPrivateSize、`workingSetBytes`＝WorkingSetSize、`commitBytes`＝PrivatePageCount（バイト数そのもの・ページサイズを掛けない）。PagefileUsage は使わない。

## 4. 計測（軽さの要）

| 物 | 取り方 | 頻度 |
|---|---|---|
| 全プロセス | `NtQuerySystemInformation(SystemProcessInformation)` 1 回＋前回との差分（CPU 時間・I/O 転送量）。バッファ不足は広げて再試行 | 間隔ごと |
| プロセスのパス・説明・発行元・アイコン・コマンドライン・所有者 | 選択時か表示時に遅延取得してキャッシュ（ProcessKey ごと）。毎秒全部を開かない | 初回だけ |
| 署名の確認 | 詳細を開いたときだけ（WinVerifyTrust） | 選択時 |
| CPU 全体・コアごと | `NtQuerySystemInformation(SystemProcessorPerformanceInformation)`、processor group を考慮 | 間隔ごと |
| メモリ | `GlobalMemoryStatusEx`＋`GetPerformanceInfo`（コミット・キャッシュ・ページプール） | 間隔ごと |
| ディスク | PDH `PhysicalDisk`（`PdhAddEnglishCounter`＝日本語 Windows の名前問題を避ける）。クエリは保持 | 間隔ごと |
| NIC | `GetIfTable2`（送受信の差分）＋`GetAdaptersAddresses` | 間隔ごと |
| GPU 全体・プロセス別 | PDH `GPU Engine`（エンジン種別ごとに合算し、代表値は最も忙しいエンジン＝タスクマネージャーと同じ考え）・`GPU Process Memory`／`GPU Adapter Memory` | 間隔ごと（GPU 列・タブを見ていない間は止める） |
| GPU 温度・電力・ファン・クロック | SensorHost（NVML／ADLX）。ハングしたら SensorHost を殺して作り直す | 2 秒 |

- 既定の間隔 1 秒。選べる: 0.5／1／2／5 秒・一時停止。最小化中は 5 秒、トレイ表示もしない（v1）。
- プロセス一覧は `ListView` の VirtualMode。行は差分更新、グラフは自前の軽い描画（ダブルバッファ）。
- 並べ替え中も、選択中・右クリック中は行を動かさない。
- 列名は「I/O 読み取り」「I/O 書き込み」（B/s）。プロセスの読み書き量（ファイル・ネットワーク・デバイスの合計）であり、物理ディスクの転送量ではない。画面のヒントでもそう書く。タスクマネージャーの「ディスク」列の代わりとは称さない。
- CPU % = `100 × Δ(UserTime＋KernelTime) / Stopwatch の実経過 / 有効な論理 CPU 数`。全体の CPU は KernelTime から IdleTime を引く。タスクマネージャーと「同じ」とは書かず、式をヘルプに書く。
- processor group: 論理 CPU 64 超の機械では group ごとに取る（取れなければ取れた分だけ・欠けを status で示す）。
- GPU: エンジンの識別は `(adapter LUID, phys, eng)`。同じエンジンの PID 分を合計し、代表値は最も忙しいエンジン。プロセスの代表値もそのプロセスの最も忙しいエンジン。PDH のインスタンス名 `pid_…_luid_…_phys_…_eng_…_engtype_…` はトークンとして解析し、知らない形は欠損に。

## 5. 画面（GUI）

左に縦のナビ、右に中身。上に検索とメニュー（表示・間隔・テーマ・言語・常に手前）。

1. **概要**（起動時の画面・初心者向け）: CPU／メモリ／GPU／ディスク／ネットワークのカード（今の値・小さなグラフ・温度があれば温度。ネットワークは送受信の合計で、書き方は下の「ネットワークの単位」に従う）。その下に「いま重いアプリ」上位 5（製品名で）。「気づいたこと」欄（§6 の観察）。
2. **プロセス**: ツリー表示／一覧表示の切替。既定の列＝名前（製品名）・CPU・メモリ（プライベート作業セット）・ディスク・ネットワーク・GPU・状態（応答なし＝窓が `IsHungAppWindow`／中断＝全スレッドが Suspended）。列の追加＝PID・I/O の読み／書き・ネットワークの送信／受信（合計の列を送りと受けに分けたもの）・exe 名・ユーザー・優先度・スレッド・ハンドル・作業セット・コミット・起動時刻・コマンドライン・セッション・アーキテクチャ・効率モード。群の分け方は**独自の基準**（見える窓を持つ＝アプリ／Windows のフォルダの署名済み＝Windows／それ以外＝バックグラウンド）をヘルプに書き、タスクマネージャーと一致するとは称さない。
   上部に「新しいタスクを実行」（ShellExecute。管理者で、のチェックは Helper ではなく `runas` 動詞）。
   サービスを持つプロセス（svchost 等）は詳細ペインにサービス名を出し、サービスタブへ移れる（逆も）。
   プロセスごとの通信量（GUI 側）: 「ネットワーク」列は送信と受信の合計（Core の `NetSendBytesPerSec`／`NetReceiveBytesPerSec`、単位は B/s）。既定で表示（ディスクと GPU の間）、棒つき、並べ替えは降順が先で、生の値の合計で比べ、欠損は昇順でも降順でも最後。値が無い間は灰色の「-」を出し、セルの説明に理由（まだ計測していない／準備中／管理者の確認で許可されなかった／失敗／この PC では計測できない）を書く。列の見出しと「表示する列」の項目の説明に「TCP と UDP を、ループバックや VPN も含むすべてのアダプターで数えるので、パフォーマンスのアダプターごとの値とは合わないことがある」と書く。「送信」「受信」の列は既定で隠し、「表示する列」から出せる。すでに列を保存してある人には、ネットワーク列を 1 回だけ足す（`proc.cols.v`）。その後に隠せば隠れたまま。
   計測は任意で、管理者の確認が 1 回出る（Core の `INetMeterControl`）。上部のボタン「通信量を計測」（盾つき）は Off／拒否／失敗のときに押せる。準備中は押せず「計測を準備中…」。計測中は「計測を止める」（盾なし）。この PC で計測できない（Unsupported）ときはボタンごと隠す（無効のボタンには説明が出ないため）。開始は UI スレッドの外で呼び、状態の変化は UI スレッドに戻して表示する。設定「起動時に通信量の計測を始める（管理者の確認が出ます）」（`net.meter.autostart`、既定オフ）をオンにすると、起動のたびに 1 回だけ開始する。
   右の詳細ペイン: アイコン・製品名・説明・発行元・署名・パス・親・起動時刻・コマンドライン（折り畳み）・送信／受信（今の速さ。列と同じ単位と、値が無いときの扱い）・「これは何？」（§6 の既知の説明があれば）・操作ボタン。
   操作（右クリックと詳細ペイン）: 閉じる（窓があるときだけ。HWND→PID/ProcessKey を照合して `SendMessageTimeout(WM_CLOSE)`）・終了（TerminateProcess、確認あり）・ツリーごと終了（その時点の子孫の ProcessKey を全部確保し対象件数を出して確認・部分成功を出す）・優先度（realtime は出さない）・効率モード（`SetProcessInformation(ProcessPowerThrottling)` の EXECUTION_SPEED を Control/State 両方＋`IDLE_PRIORITY_CLASS`。変更前の優先度と mask を覚え、解除で戻す。Win10 では「EcoQoS」と書かず「低 QoS」）・CPU の割り当て（v1 は単一 group 内の hard affinity だけ）・ファイルの場所を開く・プロパティ・Web で検索（既定のブラウザ。プロセス名だけを渡す）・情報をコピー。
3. **パフォーマンス**: CPU（全体／コアごと切替・クロック・コア数・キャッシュ）・メモリ（使用・コミット・キャッシュ・スロット使用数・速度）・各ディスク（使用率・読み書き速度・応答時間）・各 NIC（送受信。縦軸・見出し・統計は「ネットワークの単位」に従い、リンク速度だけは常に bit）・各 GPU（エンジン別・専用／共有メモリ・温度・電力）。履歴は 60 点（間隔×60）のリングバッファ（ネットワークの履歴は B/s のまま持ち、単位を変えると描き直すだけ）。
4. **スタートアップ**: 登録先ごとの provider。
   - 切り替えできる（v1）: HKCU `Run`→承認先 HKCU `…\Explorer\StartupApproved\Run`／HKLM `Run`（64bit view）→HKLM `…\StartupApproved\Run`／HKLM `Run`（32bit view＝WOW6432Node）→HKLM `…\StartupApproved\Run32`／ユーザーと共通のスタートアップフォルダ（Known Folder API）→各 scope の `StartupFolder`（値名はファイル名）。HKCU の Run は view 共有なので 2 回数えない。
   - 値は REG_BINARY 12 バイト（先頭 DWORD が状態、後ろ 8 バイトが無効にした FILETIME）。**有効＝2 か 6、無効＝3 か 7**。未知の値・型は表示だけで書き換えない。書くときは元の型・全バイト・「値が無かった」ことを記録し、戻せるようにする。無効化は先頭を 3、FILETIME を今に。有効化は 2 と 0。
   - 一覧だけ（v1）: タスクスケジューラのログオン起動・パッケージアプリの StartupTask。切り替えは v1.x。RunOnce・ポリシーは扱わないとヘルプに書く。
   - HKLM の書き換えは Helper。
5. **サービス**: 名前・表示名・状態・起動の種類・PID・説明。開始／停止／再起動は Helper（依存関係を表示して確認）。`*_PENDING`・checkpoint/wait hint を待ち、要求の成功と RUNNING/STOPPED への到達を分けて出す。途中失敗は「どこまで行ったか」を出す（自動で戻せるとは言わない）。起動の種類の変更は v1.x。
6. **ユーザー**: セッションの一覧（ユーザー名・状態は WTS）。CPU・メモリは Snapshot の SessionId で集計。行を開くとそのセッションのプロセス。他ユーザーのプロセスが見えない場合は「一部だけ」と書く。切断・サインアウトは v1.x。
7. **PCの構成**（旧称「PCの部品」。画面の名前は 2026-11 に変えた）: §7。左に部品の木、右に表。上に「型番・容量・接続・今の値」、下に専門項目を折り畳み。各項目に取得元。コピー（テキスト／JSON）。
8. **診断レポート**: 観測時間（10 秒既定）を選んで「作る」→ Markdown（AI に貼る用）と JSON。伏せ字の切替（既定オン、§9）。コピー・保存。AI への送信機能は持たない。

- テーマ: ライト／ダーク／Windows に従う。高 DPI（PerMonitorV2）。キーボードだけで全部操作できる（Del＝終了の確認、Ctrl+F＝検索、F5＝今すぐ更新、Space＝一時停止）。
- 色だけに頼らない（「高負荷」「取得できません」を文字でも出す）。ゲーム中の GPU 100% を異常扱いしない。「高負荷」と「故障の兆候」を分ける。
- 設定は `%APPDATA%\PCMieru\settings.json`（列幅・並び・タブ・間隔・テーマ・言語・ネットワークの単位・通信量の計測を起動時に始めるか）。
- ネットワークの単位（`net.unit`）: 4 つから選ぶ。「自動（bps）」（既定）・「Mbps 固定」・「自動（B/s）」・「MB/s 固定」（英語は Auto (bps)／Mbps (fixed)／Auto (B/s)／MB/s (fixed)）。10 進（1 kbps＝1000 bps、1 Mbps＝1,000,000 bps）で、バイトは 1 B＝8 bit、MB/s も 1,000,000 B/s。自動は有効数字 3 桁で bps→kbps→Mbps→Gbps（B/s→kB/s→MB/s→GB/s）と単位を替え、固定は小数 2 桁（0 でないのに 0.01 に満たない値は「<0.01 Mbps」）。ディスクとメモリの 1024 進の表記は変えない。変更は設定画面とプロセス画面の「表示する列」メニューのどちらでもでき、互いに同期し、開いている画面（プロセス・パフォーマンス・概要）はその場で書き直す。

## 6. 観察（「なぜ重い？」）と「これは何？」

- 観察は**根拠つきの事実と候補**だけ。例: 「CPU 全体は 15% ですが、論理 CPU 1 つが 100% です。1 つのスレッドで詰まっている可能性があります（候補）。上位: xxx.exe」。断定しない。
- 規則（v1）: 単一コア張り付き・コミット（CommitTotal/CommitLimit）が 90% 超・利用可能な物理メモリが少ない（AvailablePhysical）の 2 つは別々に評価・ディスク使用率 90% 超が 5 秒以上（応答時間つき）・1 プロセスがメモリの 50% 超・GPU 温度が高い（メーカー API の上限が取れたときだけその値と比べる）・ストレージの健康（NVMe の critical warning・予備領域・寿命の使用率）。
- 「これは何？」: よく見る Windows・ドライバ・ゲームランチャー等のプロセス **約 60 件**（v1。v1.x で増やす）の短い説明（日英）を同梱の JSON に持つ。一致は**パスと検証済みの署名者も見る**（名前だけで「安全」と言わない。`CompanyName` は書き換えられるリソースなので署名者とは別の欄。`C:\Windows\System32\svchost.exe` 以外の svchost は「場所が普通ではない」と出す）。
- 署名の確認: UI の外のスレッドで WinVerifyTrust（`WTD_UI_NONE`・`WTD_CACHE_ONLY_URL_RETRIEVAL`＝通信しない・`WTD_REVOKE_NONE`、STATEACTION_CLOSE を必ず）。埋め込み署名が無ければカタログ署名も見る。表示は「署名あり（オフラインで確認・失効は未確認）」。キャッシュはファイルのパス＋サイズ＋更新時刻で。

## 7. PC の部品（自前ドライバなし）

| 部品 | 取得元 | 主な項目 | 限界（画面にも書く） |
|---|---|---|---|
| CPU | CPUID 命令（System.Runtime.Intrinsics.X86）・`GetLogicalProcessorInformationEx`・SMBIOS Type 4・レジストリの ~MHz | 名前・ベンダ・ファミリ/モデル/ステッピング・コア/スレッド・P/E コア（効率クラス）・キャッシュ L1/L2/L3・命令セット（AVX2/AVX-512 等）・ソケット・基本クロック | 実効クロック・電力・温度は v1 では出さない（PawnIO なし） |
| メモリ | SMBIOS Type 16/17（`GetSystemFirmwareTable('RSMB')`）| スロットごとの容量・種類（DDR4/5）・速度/構成速度・メーカー・型番・ランク・電圧・フォームファクタ・最大容量・スロット数 | SPD の直接読み出しではない（XMP/EXPO・タイミングは出ない）。BIOS の空欄・誤記はそのまま出す |
| マザーボード/BIOS | SMBIOS Type 0/1/2/3 | メーカー・型番・リビジョン・BIOS のベンダ/版/日付・UEFI/セキュアブート・TPM の有無（`Tpm` は WMI だと管理者→v1 は `Win32_Tpm` を試して denied なら denied） | 電圧・VRM 温度は出ない |
| GPU | DXGI（アダプタ・LUID・専用/共有メモリ）・SetupAPI（ドライバ版・日付・PCI ID・場所）・レジストリ（`HardwareInformation.qwMemorySize`）・NVML/ADLX（温度・電力・電力上限・ファン・クロック・PCIe 世代と幅・VBIOS） | 上記 | Intel は IGCL を v1.x。メーカー API が無い物は温度なし |
| ストレージ | SetupAPI・`IOCTL_STORAGE_QUERY_PROPERTY`（デバイス・アダプタの記述子＝バス種別・型番・FW）・`IOCTL_DISK_GET_DRIVE_GEOMETRY_EX`・ボリューム・パーティション | 型番・容量・バス（NVMe/SATA/USB）・FW・TRIM・パーティション（GPT/MBR）と割り当てのドライブ文字・空き | |
| ストレージの健康 | NVMe: `StorageDeviceProtocolSpecificProperty` の SMART/Health log（通常権限で試し、denied なら Helper）。SATA: Helper の ATA pass-through（SMART 属性 5/9/12/194/197/198/199・241/242 は単位が不明なら単位不明と出す） | 温度・寿命の使用率・予備領域・書き込み量・電源投入時間・異常停止回数・メディアエラー・critical warning | USB の箱・RAID では取れないことが多い→ unsupported |
| NIC | `GetAdaptersAddresses`・`GetIfTable2`・SetupAPI・WLAN API | 名前・種類・リンク速度（＝実測ではない）・MAC（伏せ字の対象）・IP・DNS・ドライバ・Wi-Fi の SSID/帯域/信号（WLAN API） | |
| ディスプレイ | `QueryDisplayConfig`・`DisplayConfigGetDeviceInfo`・EDID（レジストリ `Device Parameters\EDID`） | 製造元・型番・製造年週・物理サイズ・ネイティブ解像度・今の解像度と Hz・HDR の状態・接続（HDMI/DP/内蔵）・どの GPU に挿さっているか | KVM・ドック・仮想画面で欠ける |
| オーディオ | MMDevice API | 出力/入力の機器・既定・形式（サンプルレート/ビット） | |
| USB | SetupAPI/CfgMgr32 | VID/PID・名前・階層・メーカー | 実際の給電 W は取れない |
| バッテリー | `GetSystemPowerStatus`・battery IOCTL | 残量・設計容量・満充電容量・劣化度・サイクル数（対応時） | デスクトップは「なし」 |
| OS | RtlGetVersion・レジストリ | 版・ビルド・起動時刻・稼働時間・電源プラン・仮想化（VBS/HVCI） | |
| 電源（PSU） | 出さない | — | 一般の ATX 電源は型番も W 数も読めない、と書く |

- 項目ごとの取得 API を分ける（r2 §1）: Secure Boot＝`GetFirmwareEnvironmentVariable` は権限が要るのでレジストリ `SecureBoot\State\UEFISecureBootEnabled`・TPM＝`Tbsi_GetDeviceInfo`・VBS/HVCI＝`Win32_DeviceGuard`（WMI、1 回だけ）。CPUID の対応命令と「OS が使える」（`IsProcessorFeaturePresent`・XGETBV）を分ける。メモリ速度は MT/s で（SMBIOS Type 17 の Speed は MT/s）。
- RSMB は provider 定数 `0x52534D42`、返りは `RawSMBIOSData` ヘッダー付き。
- EDID: 今の DisplayConfig の target の monitor device path → SetupAPI の device instance → hardware key の `Device Parameters\EDID`。レジストリの全走査はしない（過去の接続を今の画面として出さない）。checksum・拡張ブロック・EDID override を扱う。preferred timing を「ネイティブ」と断定しない（「推奨」と書く）。
- NVMe: 最小限のアクセス（0）と共有 Read/Write で開き `StorageDeviceProtocolSpecificProperty`（ProtocolTypeNvme・NVMeDataTypeLogPage・Health log 0x02）。返りの descriptor・offset・長さを確かめる。denied・unsupported・error を分ける。書き込み量＝Data Units×512,000 バイト（128bit は文字列）。寿命の使用率は 100 超もあり得る（clamp しない）。
- SATA SMART: Helper の固定の読み取り要求だけ（パーツぐらしの PartsLife.Check の解析を写す）。属性の raw を共通の温度・回数と決めつけない。
- Wi-Fi の SSID が取れないのは位置情報の同意のことがある→「プライバシー設定で拒否」と出し、Helper で迂回しない。
- 部品情報は「PCの構成」タブを開いたとき・CLI の `hw` のときに集める。キャッシュし、機器の変化（`WM_DEVICECHANGE`）で取り直す。WMI は毎秒呼ばない。
- 各項目に取得元と状態。
- 温度（CPU・マザー）は v1.x で LHM＋PawnIO を**任意の追加機能**として検討（PawnIO 自体もドライバで、導入に管理者が要る。既定では入れない）。

## 8. 操作と安全

- 操作の直前に ProcessKey で対象を引き直し、**開いた同じハンドルに**対して行う（ハンドルから `GetProcessTimes` で作成時刻を確かめる）。作成時刻が違えば「別のプロセスに変わりました」で止める。CLI で `--started-at` を省いたときは「今その PID のプロセス」を対象とし、そう表示する。
- 守るもの: PCミエル自身（GUI・CLI・Helper・SensorHost）・`IsProcessCritical`（PROCESS_QUERY_LIMITED_INFORMATION、out 引数が真）・保護プロセス（`GetProcessInformation(ProcessProtectionLevelInfo)` が PROTECTION_LEVEL_NONE 以外）・PID 0/4・Windows 中核（smss・csrss・wininit・services・lsass・winlogon・fontdrvhost・dwm を `%SystemRoot%\System32` のパスで。csrss/winlogon は対話セッションにもいる）。これらは終了・優先度・affinity・効率モードを出さない。**判定できなかった（問い合わせ失敗）ときも操作しない**（「守られているかを確かめられません」）。
- explorer.exe は「再起動」を別に用意する。
- 名前で終了（CLI）: 一致が複数なら一覧を出して止める。`--all` で全部。
- ツリーごと終了: その時点の子孫だけ。後から生まれた子は追わない。
- スタートアップの無効化は消さず戻せる。サービスの停止は依存を出して確認、前の状態を記録。
- Helper（パイプは使わない）: GUI/CLI が `ShellExecuteEx("runas")` で **要求をコマンドライン引数に固定して** 起動する（例 `PCMieru.Helper.exe service-stop --name Spooler --result <path> --nonce <乱数>`）。起動後に別の要求へ差し替えられない。1 回の UAC で 1 要求、終わったら終了。結果は終了コードと、呼び出し元が作った `%LOCALAPPDATA%\PCMieru\helper\<nonce>.json` への JSON（呼び出し元は nonce と一致する物だけ読む。結果は表示に使うだけ）。
  - 受ける要求（固定の表）: service-start/stop/restart（ローカル SCM・サービス名だけ）・startup-hklm-set（provider と値名と enable/disable だけ。キーのパスは受けない）・process-terminate/priority/efficiency（pid＋作成時刻。Helper 側で開き直し・守る判定をやり直す）・smart-read（物理ドライブ番号だけ。固定の SMART 読み取りだけで任意の IOCTL・ATA コマンドは受けない）・nvme-health（物理ドライブ番号だけ）・disk-health-all（物理ドライブ番号の一覧 `--drives 0,2,3` だけ。0〜255 の小さな整数・昇順・重複なし・16 個まで。パスは受けない。バスは Helper が自分で見分け、1 回の昇格＝UAC 1 回で全ドライブを読む。結果は `drive.<n>.*` のキーで返り、各ドライブが自分の code を持つ）。
  - 別の資格情報の UAC（標準ユーザーが管理者のパスワードを入れる）だと Helper のユーザーは別人になる。結果ファイルは呼び出し元が先に作って Helper は上書きするだけ（ACL は呼び出し元のもの）。
  - Helper は NativeAOT 単体で、system32 以外の DLL を読まない（発行時に検査）。ZIP の置き場所が利用者の書けるフォルダであることは残る危険として README に書く。
  - Explorer の再起動は Helper ではなく呼び出し元のユーザーで行う（昇格した Explorer を作らない）。
- AI（MCP）に出さない物: 変更操作すべて（v1）、任意のシェル、メモリの読み取り・ダンプ、DLL の注入、ハンドルの強制クローズ、保護の迂回、ドライバ、電圧・クロック・ファン。
- DLL は `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)` で、メーカーの DLL は既知の場所から絶対パスで読む。

## 9. CLI（`pcmi.exe`）

```
pcmi ps      [--sort cpu|mem|io|gpu|name] [--top N] [--sample 1s] [--tree] [--json]
pcmi proc    <pid> [--json]                      詳細（パス・発行元・署名・親・コマンドライン）
pcmi hw      [all|cpu|memory|board|gpu|storage|network|display|audio|usb|battery|os] [--json]
pcmi health  [--json]                            ストレージの健康（denied なら --elevate で Helper）
pcmi perf    [--sample 1s] [--json]              全体の値
pcmi watch   [--interval 1s] [--top N] [--ndjson]
pcmi report  [--duration 10s] [--format markdown|json] [--no-redact] [-o file]
pcmi kill    --pid N [--started-at T] [--tree] | --name X [--all]
pcmi priority --pid N <idle|below|normal|above|high>   （realtime は出さない）
pcmi eco     --pid N on|off                      効率モード
pcmi startup list|enable <id>|disable <id> [--json]
pcmi services list [--running] [--json] | start|stop|restart <name>
pcmi run     <command line>                      新しいタスク
pcmi mcp                                         MCP サーバ（stdio）
pcmi version
```

- stdout はデータだけ、診断は stderr。終了コード: 0 成功・1 一般の失敗・2 引数の誤り・3 対象なし・4 権限不足・5 守られた対象・6 複数一致。
- `ps` の CPU は差分が要るので、GUI が動いていなければ `--sample`（既定 1 秒）だけ測ってから返す。GUI が動いているときの共有は v1.x。
- 人向けの表は端末幅に合わせ、日本語の幅（全角＝2）を考慮。`--json` は §3 の形。
- 伏せ字（report と MCP の既定）: ユーザー名→`<user>`、ユーザーのフォルダ→`%USERPROFILE%`、PC 名→`<host>`、シリアル・MAC・IP・SSID→伏せる、コマンドラインは出さない。

## 10. MCP（`pcmieru mcp`）

- stdio の JSON-RPC 2.0。protocolVersion は相手の要求が対応表（2025-06-18・2025-03-26・2024-11-05）にあればそれ、無ければ 2025-06-18。
- tools（すべて読み取りのみ。`annotations.readOnlyHint: true`）:
  - `get_system_summary` — CPU/メモリ/GPU/ディスク/ネットの今の値と観察
  - `list_processes` — sort・top・filter（名前の部分一致）
  - `get_process` — pid → 詳細（コマンドラインは既定で出さない）
  - `get_hardware` — 部品の種別 → §7
  - `get_storage_health` — ドライブごとの健康
  - `diagnose` — duration（最大 30 秒）観測して観察と上位を返す
  - `get_report` — §5 の 8 と同じ物（伏せ字つき）
- 返す文字列の中のプロセス名・ファイル名は**データ**として JSON に入れ、説明文に埋め込まない（指示の紛れ込み対策）。制御文字は escape。
- MCP の dispatcher は **読み取り専用のインターフェース（`IReadOnlyInsights`）だけ** を参照する。CLI のサブコマンドを名前で実行する作りにしない。readOnlyHint は注釈であって権限の境界ではない。
- 読み取りの裏で UAC を出さない（storage health の denied はそのまま返す）。
- 伏せ字を外す引数を tool に持たせない（外すのは起動時の `pcmi mcp --no-redact` だけ）。出力の直前に共通の redactor を通す（パス・UNC・エラー文・デバイス ID も）。
- 入力の schema・大きさの上限・観測は同時に 1 つ・stdin の EOF で終了・`notifications/cancelled` で観測を止める。
- README に Claude Code・Claude Desktop・Codex の設定例を載せる。
- 変更操作（終了など）の MCP 化は v1.x で「承認待ちの札を返し、人が GUI で対象を見て実行する」形を検討。AI が付ける `confirmed: true` を人の確認とみなさない。

## 11. 軽さの合格条件（公開前に実測、サイトに書くのはこの数字だけ）

この PC（7950X・64GB・Win11）で、1 秒間隔・プロセスタブ表示・既定の列・同じ窓の大きさ・GPU 採取あり:
- 外部の計測器（`tools/measure.ps1`）がプロセスの CPU 時間の差分と private bytes／作業セットを 1 秒ごとに読む。表示値を目で読まない。
- 「GUI だけ」と「GUI＋SensorHost の合計」の両方。作業セットの合計は共有ページの二重計上がある旨を書く。
- CPU は `CPU 秒 / 実経過秒 / 論理 CPU 数 × 100` と **CPU ms/秒（1 コア換算）** の両方。
- 起動直後 30 秒（部品のメタデータ集め中）と定常（その後 5 分）を分ける。3 回以上・PCミエルとタスクマネージャーを交互に、同時には動かさない。平均と最大。
- 起動は「一覧が描かれるまで」と「2 点目を取って CPU 値が出るまで」の 2 つ。warm＝直前に 1 度起動して閉じた後、cold＝OS 再起動後。
- OS build・電源プラン・ドライバ版・発行の設定を記録。
- 長時間: 2 時間・全タブを巡回・プロセスの生成消滅を繰り返す負荷で、private bytes とハンドル数が増え続けないこと。
- working set を強制的に削って数字を作らない。
- 目標（保証ではない）: private 100MB 未満・定常 CPU 平均 0.3% 未満（1 コア換算 10ms/秒未満）・warm 起動 1 秒未満。

## 12. 試験

- CI（windows-latest）: SystemProcessInformation の解析（作ったバッファ・境界）・差分と PID の再利用・欠損の扱い・JSON の形（スナップショット試験）・SMBIOS の解析（実機の RSMB を保存した試料、シリアルは消す）・NVMe Health log・EDID・伏せ字・守る対象の判定・CLI の引数と終了コード・MCP の initialize/tools/list/tools/call。
- 実機（この PC）: 通常ユーザーで全タブ・権限拒否・スリープ復帰・SensorHost を止めたときの回復・UAC を断ったとき。
- CI の仮想機での成功を「ハードの対応を確かめた」と扱わない。

## 13. 版の線

- **v1.0**: §4〜§10 の全部（ただし「v1.x」と書いた物を除く）。
- 確かめていない環境（Win10・AMD/Intel GPU・ノートとバッテリー・論理 CPU 64 超・別資格情報の UAC）は README とページに「未確認」と書いて出す。
- **v1.x**: 複数 group をまたぐ affinity・タスクスケジューラ/StartupTask の切り替え・プロセスごとのネット接続（GetExtendedTcpTable）・ファイルを使っているプロセス（Restart Manager）・サービスの起動の種類の変更・ユーザーの切断・GUI と CLI のスナップショット共有・Intel IGCL・MCP の承認待ち操作・自動更新（パーツぐらしの方式）・LHM＋PawnIO を任意で。
- **v2**: アプリの履歴・ダンプ・ハンドル/モジュール・ETW（Helper）でのプロセスごとのディスク/ネット帰属。

## 14. 配布

- GitHub `goemon0000/pc-mieru`（公開）。タグ `v*` → Actions で試験・ビルド・Release（ZIP と SHA256）。
- 未署名の間は SmartScreen の警告が出る。警告の無効化は案内しない（「詳細情報→実行」の説明と SHA256 だけ）。SignPath はパーツぐらしの申請が通ってから。
- 依存物のライセンス表記（NOTICE）を同梱。
- web-autoai.com に紹介ページ `/tools/pc-mieru`（内部 Git → 点検 → mark-reviewed → remote-build）。
