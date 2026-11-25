# DESIGN-C — パーツぐらしの窓と利用者の引き継ぎ

**PCミエル v0.6.0・パーツぐらし v0.8.0｜SPEC-unify u0.5**

下書き: codex gpt-6.2-sol（2026-11-20）。点検と修正: Opus（司会 2fb51ba0）。直した所は §10 で、**本文と §10 が食い違ったら §10 を取る**。実装、ビルド、実機試験は未実施。

## 0. 版の線

段 C では、PCミエルに従の窓「パーツぐらし」を追加し、旧版の物語・レベル・冒険・表示設定を利用者の同意で引き継ぐ。PCミエル v0.6.0 の公開に合わせて紹介ページを統合する。公開した配布物で起動と引き継ぎを確認した後、パーツぐらし最後の版 v0.8.0 を公開する。旧 repo は v0.8.0 公開から 1 か月間 Issues を受け、その後 archive する。記憶媒体の基準値と `twins.json` は段 A の最初の記憶媒体点検で扱う。GPU 負荷の公開と BTO 参考ガイドの連携本体は後の版に置く。［`SPEC-unify.md:172`、`:178`、`:190`、`:208`、`:222`、`DESIGN-A.md:226`］

**確認済み**は、PCミエルの `area/unify`（`39fb4e9`）と、`pl-cloud` のパーツぐらし v0.7.4（`25e4571`）の読み取りで確認した事実。以下の `PC/` と `PL/` は各作業ツリーのルート、設計書名だけの参照は `PC/docs/unify/` を指す。**設計案**は段 C で追加する契約。**未確認**には実機、公開ページ、配布物での確認方法を添えた。

## 1. プロジェクトと移植

| 旧ファイル・現行の入口 | 新しい置き場 | 方針と確認済みの根拠 |
|---|---|---|
| `PL/src/PartsLife.Core/Game/{Director,PartyState,LenientJson,Story,Conditions,PartySlot,SlotKind}.cs`、`Adventure/*.cs` | `PC/src/PcMieru.Party.Core/` | **移植＋保存 I/O の分離。** レベル式、物語、冒険、装備、保存の変換規則を移す。Windows 非依存の試験を維持する。現行は `Director.Tick` が枠ごとの経験値と旅程を進め、冒険へ渡す。［`PL/src/PartsLife.Core/Game/Director.cs:79`、`:93`、`:165`、`PL/src/PartsLife.Core/Game/PartyState.cs:100`、`:122`、`:139`、`:344`、`:368`］ |
| `PL/src/PartsLife/MainWindow.xaml`、`MainWindow.xaml.cs`、`MainWindow.Tabs.cs`、`Rendering/`、`Game/GameAssets.cs`、`assets/` | `PC/src/PcMieru.Party/` → `PCMieru.Party.exe` | **描画・タブ・素材を移植、起動・採取・保存を接続し直す。** WPF、`asInvoker`。盤、物語、詳細、表示設定を維持する。既存の DrawingVisual 描画を使い、素材は現行と同様にリソースへ組み込む。権利表記を同梱する。［`PL/src/PartsLife/PartsLife.csproj:7`、`:49`、`PL/src/PartsLife/app.manifest:9`、`PL/src/PartsLife/Rendering/BoardRenderer.cs:119`、`:136`、`PL/src/PartsLife/MainWindow.Tabs.cs:173`、`:255`、`:323`］ |
| `PL/src/PartsLife/Game/Composition.cs`、`PartsLife.Core/Hardware/GpuRoster.cs`、`PartsLife/Sensors/` | `PC/src/PcMieru.Core/Collection/` の集計ソースと `PcMieru.Party.Core/` の盤面変換 | **採取を書き直し、枠のキー・並び・換算を移植。** 旧窓は独自の system・PDH・disk サンプラーを呼び、CPU ソケット別・GPU 別・記憶媒体の種類別に枠を作る。［`PL/src/PartsLife/MainWindow.xaml.cs:16`、`:526`、`PL/src/PartsLife/Game/Composition.cs:30`、`:50`、`:64`、`:77`、`PL/src/PartsLife.Core/Hardware/GpuRoster.cs:39`］ |
| `PL/src/PartsLife/App.xaml.cs`、`Startup.cs`、`MainWindow.Tabs.cs` の点検コマンド | `PC/src/PcMieru.Party/` の `Main` と、`PC/src/PcMieru.Gui/` の起動・移行・点検導線 | **書き直し。** 主画面ナビの末尾に「パーツぐらし」。Party の点検メニューは「点検を開く」。旧版は WPF の起動処理で保存と更新を準備し、点検は `CheckWindowHost` を開く。Run は HKCU の `PartsLife`。［`PL/src/PartsLife/App.xaml.cs:17`、`:68`、`:88`、`PL/src/PartsLife/MainWindow.xaml.cs:656`、`PL/src/PartsLife/Startup.cs:19`、`:35`］ |
| `PL/src/PartsLife/Updates/`、`PL/src/PartsLife.Update/` | 旧 repo の v0.8.0 リリース枝。PCミエル側は段 B の `PcMieru.Update` | **旧 updater の配布互換を維持。** Party 自身に更新クライアントを置かず、PCミエルの配布一式に含める。旧 updater は配置形態から単体 exe と ZIP を選ぶ。［`PL/src/PartsLife.Update/InstallLayout.cs:10`、`:18`、`:21`、`DESIGN-B.md:80`、`:143`］ |

盤面のドラッグ、拡大、名札、メニュー、表示設定を使える。進行は計測値で自動的に進む。Party から始められる点検は主画面の点検タブへの遷移だけとする。［`SPEC-unify.md:194`］

## 2. 採取と主画面との連絡

### 2.1 採取元

**確認済み。** GUI は `SnapshotPump` に Core の `SamplerLoop` を渡す。ループは `Latest` と `Sampled` を公開し、GUI 側の Pump は UI に渡す待機枠を最新値で置き換える。`Snapshot` は `System` と `Processes` を持ち、`System` に CPU、メモリ、ディスク、NIC、GPU と一覧の取得状態がある。現行の `WindowsSnapshotSource` は同時にプロセスとウィンドウを扱う。Party から `WindowsSnapshotSource` や通常のサービス一式を作ると、集計値だけの採取にはならない。［`PC/src/PcMieru.Gui/Program.cs:46`、`:49`、`PC/src/PcMieru.Gui/Data/SnapshotPump.cs:37`、`:50`、`:222`、`PC/src/PcMieru.Core/Collection/SamplerLoop.cs:97`、`:112`、`:256`、`:270`、`PC/src/PcMieru.Core/Contracts/Snapshot.cs:10`、`:25`、`PC/src/PcMieru.Core/Collection/WindowsSnapshotSource.cs:185`、`:207`、`:237`］

**設計（§10 の 1）。** Party は**主画面の有無にかかわらず、いつも自分のプロセスで** `SamplerLoop` と新設の `AggregateSnapshotSource` を組み立てる（仕様 §2 のとおり。旧 v0.7.4 も自分で採取していた）。Core の CPU・メモリ・PhysicalDisk・NIC・GPU（PDH のアダプター集計）の採取部品を使い、`Processes=[]` とし、GPU の PDH インスタンスはアダプター集計まで畳む。現行 `GpuAggregator` のプロセス別辞書は集計モードで作らない。SensorHost は起こさない（Party は NVML の値を使わない）。主画面と並べたときの上積みは §7 で測り、予算を超えたときだけ後の版で主画面からの受け渡しを足す。［現行の購読と GPU 実装は `PC/src/PcMieru.Core/Collection/SamplerSettings.cs:22`、`:86`、`PC/src/PcMieru.Core/Collection/GpuSampler.cs:27`、`:58`、`:194`、`:230`、`:245`。`SPEC-unify.md:44`、`:199`］

### 2.2 採取の周期・鮮度と主画面との連絡

**設計。**

- 周期は展開時 1 秒、折り畳み時 2 秒。最小化・非表示でも採取は続ける（進行は計測で進むため）が、描画は止める。
- 経過判定に単調時計を使い、同じ採取の再掲で進行を動かさない。欠測・停止・スリープの空白は加算しない。各供給元の最初の有効サンプルで起点を立て、次回から測定窓に基づく経過秒を渡す。`max(3 × 周期, 6 秒)` を超えた値は古い値として表示する。
- 主画面との連絡は**窓メッセージ 2 つだけ**（パイプや常設の通信は作らない）。`RegisterWindowMessage` で登録する名前は `PCMieru.OpenChecks.v1` と `PCMieru.LanguageChanged.v1`。送り先は、同じ利用者・同じセッションで、実行ファイルの実体が同じ配置（段 B の配置 ID）にあるプロセスの最上位の窓に限る。受け手は送り手を確かめない代わりに、受けて行うことを固定の 2 つに限る（点検タブを前に出す・`gui.ini` の言語を読み直す）。引数やパスは受けない。
### 2.3 値と盤面の対応

| Party が読む値 | 取得・変換の設計と確認済みの根拠 |
|---|---|
| CPU 全体、論理 CPU 数、可能ならソケット別使用率と論理 CPU 数 | `Cpu.TotalPercent` と `PerLogicalPercent` を使う。ソケット別は Core のトポロジー読み取りを拡張し、プロセッサグループと論理 CPU の対応が確認できた組だけ送る。対応不明時は全体値を表示し、各ソケットに同じ値で経験値を付けない。旧版のキー `cpu#i`・`cpu-cooler#i` を維持する。［`PC/src/PcMieru.Core/Contracts/Snapshot.cs:37`、`PC/src/PcMieru.Core/Hardware/Collectors/CpuTopology.cs:104`、`PL/src/PartsLife/Game/Composition.cs:50`、`:109`］ |
| メモリ全量・使用量 | `TotalPhysicalBytes - AvailablePhysicalBytes`。全量が正で範囲内のときに使用率を作る。［`PC/src/PcMieru.Core/Contracts/Snapshot.cs:50`、`PL/src/PartsLife/Game/Composition.cs:61`］ |
| GPU 最大使用率、GPU ごとの枠番号・表示名・使用率・専用 VRAM 使用量／総量 | `GpuInfo` のアダプター集計を使う。盤は従来どおり最大 6 枚。現行旧版の順序は専用 VRAM 降順、PCI バス、名前、LUID の順。初回は旧並びを再現し、同じトポロジーの間は枠番号を固定する。抜き差しで既存枠を詰めず、対応が曖昧な枠の進行を止める。旧保存の `gpu#i` 自体は順番しか示さないため、旧版終了後に構成が変わった場合の同一カード対応は保証できない。初回引き継ぎでその旨を案内する。［`PC/src/PcMieru.Core/Contracts/Snapshot.cs:75`、`PL/src/PartsLife.Core/Hardware/GpuRoster.cs:41`、`PL/src/PartsLife/Game/Composition.cs:18`、`:64`、`PL/src/PartsLife.Core/Adventure/AdventureState.cs:62`］ |
| ディスク全体の読み書き B/s、種類別の台数・読み書き B/s・容量、最も埋まったローカルボリュームの使用率 | `DiskInfo` を物理ディスク番号で、同じトポロジー世代の Core 機器情報と結ぶ。NVMe、SSD、HDD を分け、判別不能は `DISK` の表示に回す。種類別の経験値に全体の I/O を山分けしない。容量はローカルのボリュームを重複排除して集計し、複数台にまたがり帰属を確かめられない容量は種類別に足さない。機器変更でキャッシュを無効化し、イベントの取りこぼしに備え 60 秒ごとに機器の変化を確認する。容量は 30 秒以上間隔を空け、バックグラウンドの別枠で読む。［`PC/src/PcMieru.Core/Contracts/Snapshot.cs:59`、`PC/src/PcMieru.Core/Collection/PdhSystemSampler.cs:117`、`:144`、`PC/src/PcMieru.Core/Hardware/Collectors/StorageCollector.cs:117`、`:133`、`:183`、`:199`、`:265`、`PC/src/PcMieru.Gui/Shell/MainForm.cs:309`、`PL/src/PartsLife/Game/Composition.cs:30`、`:77`、`PL/src/PartsLife/MainWindow.xaml.cs:534`］ |
| ネットの送受信 B/s | 稼働インターフェースの `NicInfo` を Core の表示対象ルールに沿って合計する。取得できた範囲を `partial` で示す。ネットは詳細表示用とし、従来の RPG の進行式には加えない。［`PC/src/PcMieru.Core/Contracts/Snapshot.cs:67`、`PC/src/PcMieru.Core/Collection/NicSampler.cs:93`、`PL/src/PartsLife.Core/Game/Director.cs:6`、`:82`］ |

`Work` の比率は従来の単位を保つ。ディスク全体は読み書き合算を 200 MiB/s で正規化し、種類ごとの枠は旧版の換算を維持する。欠測の枠は `—` を表示し、その枠の経験値、装備の初期配布、該当する観測イベントを止める。CPU・メモリが有効な間は、取得できたチャンネルで旅程を進める。欠測チャンネルは式への寄与を省き、アイドル判定や閾値判定には使わない。完全なフレームでは `Director` の旧版との出力一致を試験する。負荷ゼロでも現行の `ExpRate` は加算するため、欠測をゼロに変換して `Tick` へ渡す方法では経験値を守れない。［`PL/src/PartsLife/MainWindow.xaml.cs:540`、`:560`、`PL/src/PartsLife/Game/Composition.cs:30`、`:85`、`PL/src/PartsLife.Core/Game/Director.cs:79`、`:102`、`:281`、`PL/src/PartsLife.Core/Adventure/AdventureEngine.cs:94`］

## 3. 起動、単一起動、終了

1. **門を最初に取る。** `PCMieru.Party.exe` は明示的な `[STAThread] Main` で、WPF 初期化、設定・保存の読み取り、Mutex より先に段 B の共通の門と journal 検査を通す。`PCMieru.gate.txt` を配置の実体から開き、作業終了まで共有ハンドルを保持する。交換中・回復待ち・回復失敗では理由を表示して終了する。試験用引数も同じ入口を通す。段 B のルールは GUI、CLI/MCP、SensorHost、Helper、NetMeter と交換役にも適用する。［`DESIGN-B.md:112`、`:130`、`:132`、`:151`］
2. **常駐の所有者を一人にする。** Party とパーツぐらし v0.8.0 が `Global\PCMieru.PartsLife.Resident.v1.<SIDのハッシュ>` を共有する。ACL は当該利用者と SYSTEM。セッションが異なっても同じ利用者の保存を同時に進めない。取得と解放は同じスレッド。Party の再起動要求は同一セッションの既存 Party に `activate` を送り、復元・前面表示する。別セッションまたは旧製品が所有している場合は「もう一方が動いています」と表示して終了する。現行の `Local\PartsLife.Update` は交換専用で、通常起動全体の単一起動には使われていない。［`PL/src/PartsLife/App.xaml.cs:68`、`:88`、`PL/src/PartsLife.Update/SwapBoot.cs:74`、`:83`］
3. **主画面との寿命を分ける。** GUI のナビから同じ配置の `PCMieru.Party.exe` を起動する。Party を閉じると保存・採取を閉じる。GUI を閉じても、利用者が開いている Party は単独表示を続ける。Party が門を保持している間、更新は延期する。更新画面の利用者向け文面は、Party の使用を確認できたときに「パーツぐらしを閉じると更新できます」とする。自動終了は行わない。
4. **点検と言語。** 「点検を開く」は、同じ配置の主画面の窓があれば `PCMieru.OpenChecks.v1` を送り、主画面は点検タブを選んで前面に出す。無ければ同じ配置の `PCMieru.exe --open-checks` を起動する（新しい引数。既存の `--lang` と同じ解析に足す。`PC/src/PcMieru.Gui/Program.cs:90`、`:108`）。言語は、主画面から開いたときは `--lang ja|en` で主画面の言語を渡す。主画面で言語を変えたら `PCMieru.LanguageChanged.v1` を Party の窓へ送り、Party は `gui.ini` の `language` を読み直して従う（`PC/src/PcMieru.Gui/Shell/GuiSettings.cs:134`）。ログオンなどで単独で起きたときは Party の保存にある `Lang`、未設定なら OS に従う。主画面から受けた言語は Party の単独時の設定へ書き戻さない。［`PL/src/PartsLife/App.xaml.cs:98`］
5. **ログオン起動。** PCミエルの設定に「ログオン時にパーツぐらしを起動」を置く。既定オフ。HKCU Run の値名は `PCMieru.Party`、値は実体に解決した絶対パスを引用符で囲み `--logon` を付ける。同じ保存を使う複数配置のうち、明示した 1 配置を登録する。Windows 側で無効にした登録や他配置の登録があれば、その状態を画面に表示する。移行時の切り替えは §4.3 に従う。
6. **復帰と保存。** センサーまたは描画の失敗は連続回数で管理し、該当処理を止めて理由を表示し、間隔を空けて作り直す。正常な処理が続けば連続失敗を解除する。保存は変更時と終了時、稼働中は最大 60 秒間隔で最新状態を単一の writer に渡す。I/O は UI スレッドの外で行い、成功したときだけ保存済みとする。終了時の保存が失敗したら再試行か未保存で終了かを選べる。旧版は累計失敗で両タイマーを止め、保存例外を握りつぶしている。［`PL/src/PartsLife/MainWindow.xaml.cs:126`、`:171`、`:563`、`:580`、`PL/src/PartsLife.Core/Game/PartyState.cs:368`、`:388`］

## 4. `state.json` の引き継ぎ

### 4.1 初回の判断と確定

**確認済み。** 旧版は Roaming AppData の `PartsLife\state.json`、見つからなければ `PartsParty\state.json` を読む。現行の `PartyState.Load` は読み方によって旧ファイルの控えを作り、破損時に元ファイルを移動する経路も持つ。段 C の読み取りには `Load` のファイル操作を使わず、本文の解析を分離する。［`PL/src/PartsLife.Core/Game/PartyState.cs:202`、`:239`、`:257`、`:265`、`:301`、`:344`］

1. **入口。** v0.6.0 の主画面を初めて表示した後、バックグラウンドで候補を探す。Party 単独を先に開いた場合も同じ処理を使う。宛先は `%APPDATA%\PCMieru\party\state.json`。先に宛先を調べ、有効な保存があれば「既にパーツぐらしのデータがあります」と表示する。宛先が読めない場合は既存ファイルを保全し、理由を表示する。
2. **探し順。** `%APPDATA%\PartsLife\state.json` → `%APPDATA%\PartsParty\state.json`。前者が実際に不在のときだけ後者へ進む。拒否、共有違反、破損は別々に扱う。候補がない場合は案内を出さず、Party を開いたときに新規開始できる。候補があれば「パーツぐらしの続きを引き継ぐ」「あとで」「新しく始める」を提示する。「あとで」では宛先を作らず、主画面の設定から再開できる。「新しく始める」は既存の続きが宛先に入らなくなることを選択時に示し、Party で新規保存を始める。
3. **旧版の稼働。** 同意前と確定直前に旧版を確認する。v0.8.0 とは共通 Mutex で排他する。v0.7.4 以前については同じ利用者の `PartsLife.exe` を PID と作成時刻で確認する。旧名フォルダから移す場合は旧名 exe の実在・名称も fixture で確かめ、識別できる旧常駐を検査対象にする。候補プロセスの所有者や状態を判定できないときは保留する。稼働中なら「パーツぐらしを終了してから、もう一度お試しください」と出す。強制終了は行わない。旧版には協調ロックがないため、検査間に起動して終了する競合を完全には検出できない。ファイル側の検査も併用する。［`SPEC-unify.md:176`、`:181`、`PL/src/PartsLife/App.xaml.cs:68`、`:88`］
4. **コピーと検証。** 別の利用者内移行ロックで、GUI の引き継ぎ、Party の初回起動、宛先の初回作成を直列化する。確認画面を出している間はロックを離し、同意後に取り直して宛先、候補、旧版の状態を再検査する。元は `FileAccess.Read` と `FileShare.ReadWrite | FileShare.Delete` で開く。上限 8 MiB、深さ 64。読み取り前後と確定直前にファイルの実体・サイズ・内容ハッシュを照合する。置換や変化が見つかれば保留する。旧版は `.tmp` の flush 後に `state.json` を置き換えるため、開いたハンドル上の内容が安定しているだけでは同じ版を読んだ証拠にならない。［`PL/src/PartsLife.Core/Game/PartyState.cs:373`、`:377`、`:384`、`:386`］
5. **確定と再試行。** 変換結果を宛先と同じディレクトリの一意な `.tmp` に `CreateNew` で書き、読み直して検証し、`Flush(true)` 後に上書きなしで rename する。宛先が先に作られていたらそちらを優先する。必要な原本控えは新側にだけ置く。完了の根拠は有効な宛先の存在。補助記録が作れなかった場合や電断後も二重コピーを発生させない。失敗時は今回の一時物だけを片付ける。PCミエルのバイナリを v0.5 に戻しても旧版は元の保存で再開でき、再度 v0.6 に進めば新側の保存から続く。両製品それぞれで進んだデータの統合は提供しない。

### 4.2 保存の形と版

| 旧データ | 段 C の扱い |
|---|---|
| `SaveVersion` なし、または `0` | 生 JSON の版欄を先に検証する。旧版の `MigrateKeys` と同じく `cpu`・`cpu-cooler`・`gpu` を `#0` へ移す。現在名が両方あるときは現在名を優先する。冒険が既にあれば維持し、なければ初期値を作る。宛先は `SaveVersion=1`。［`PL/src/PartsLife.Core/Game/PartyState.cs:15`、`:122`、`:149`］ |
| `SaveVersion=1` | PascalCase を維持する。`Experience`、`Journey/Chapter/Scene`、`Log/LastEventAt/RecentVignettes`、`UptimeSeconds/LastSeenUnix/Boots` を引き継ぐ。`Adventure` は町、行き先、進行、所持金、持ち物、装備枠、戦績を含めて引き継ぐ。`Lang/Zoom/WindowLeft/WindowTop/Topmost/Expanded/Labels` も写す。負の座標を許容し、現在の画面外なら置き直す。［`PL/src/PartsLife.Core/Game/PartyState.cs:25`、`:57`、`:70`、`PL/src/PartsLife.Core/Adventure/AdventureState.cs:29`、`:53`、`:66`、`:78`、`PL/src/PartsLife/MainWindow.xaml.cs:318`］ |
| 未知の欄、部分破損、未来版 | ルートと `Adventure` の未知欄は `JsonExtensionData` で往復させる。版欄の型不正、負数、未来版、重複キー、壊れた JSON は理由を表示して保留する。旧版は未来版でも既知欄を読むが、段 C は旧版が豊富な情報を持つ可能性がある保存の取り込みを止める。既知欄の部分破損は修復箇所を提示して再同意を得る。修復前の原本を新側に確保してから確定する。経験値や物語の主要欄が読めない場合は続きの取り込み成功と表示しない。［`PL/src/PartsLife.Core/Game/PartyState.cs:90`、`:133`、`:344`、`PL/src/PartsLife.Core/Adventure/AdventureState.cs:88`、`:95`］ |
| `UpdateMode`、notice、Run、点検データ | 旧 `UpdateMode` は PCミエルの同意に変換しない。段 B の更新設定を使う。v0.8.0 の通知既読は旧側の別ファイルに置く。Run は次節で別に尋ねる。点検の基準値と twins は段 A が扱う。［`PL/src/PartsLife.Core/Game/PartyState.cs:79`、`SPEC-unify.md:167`、`:178`、`:185`、`DESIGN-A.md:226`］ |

### 4.3 Run の切り替えと復元

**確認済み。** 旧 `Startup.IsEnabled` は `HKCU\...\Run` に `PartsLife` の非空文字列があるかを見る。PCミエルの `StartupApprovedToggle.Read` と `StartupApprovedCodec` は承認印の読み取りと復元に使える。正常に開けたキーで承認印が不在なら Windows の既定は有効。アクセス拒否や不明な形式は、有効と扱わない。［`PL/src/PartsLife/Startup.cs:19`、`:22`、`:35`、`PC/src/PcMieru.Wire/Privileged/StartupApprovedToggle.cs:111`、`:153`、`PC/src/PcMieru.Wire/Privileged/StartupApprovedCodec.cs:28`、`:59`、`:76`］

**設計案。** 保存が確定した後、旧 Run の値が旧 exe を指し、`StartupApproved\Run` で実効有効と判断できたときだけ「ログオン時の起動もパーツぐらし（PCミエル）に切り替えますか」と尋ねる。旧設定が無効・不明・読めない場合、または新名に別の登録がある場合は、移行処理からの書き換えを行わない。

- 同意後に承認と 4 値を再読し、`Run` の旧名・新名と `StartupApproved\Run` の旧名・新名について、存在、型、生の内容を回復用 journal に確定する。PCミエルの通常のログオン設定操作も同じロックと回復処理を通る。
- 新名の承認を一時無効にしてから新 Run を書く。旧 Run と旧承認印を外し、新名の承認を有効にする。全値を読んで確かめてから commit を記録する。途中で失敗したら、元の 4 値を型と有無まで復元する。保存のコピー成功と Run の成功は別表示にする。
- 強制終了後は次の GUI または Party 起動で journal を回復する。途中で Windows 設定などから変更が入り、観測値が記録した変更前・変更途中のどちらとも一致しない場合は、利用者の変更を上書きせず「ログオン設定の復元を確認してください」と表示する。復元に失敗した場合は journal を保持し、再試行できるようにする。

## 5. パーツぐらし v0.8.0

- **変更範囲。** 現行の窓、進行、保存先、配布形態を維持し、共通の単一起動と一度だけの案内を加える。文面は「パーツぐらしは PCミエルの一部になりました。PCミエルで続きを引き継げます。」。ボタンは「PCミエルの紹介を開く」と「閉じる」。閉じるか紹介を開いた時点で、旧側の `%LOCALAPPDATA%\PartsLife\final-notice.json` に通知 ID と既読を原子的に書く。記録の失敗は表示し、次回まで黙って既読にしない。紹介を開く操作はブラウザーで案内を表示する。インストールや Run の変更は利用者の操作を待つ。［`SPEC-unify.md:172`、`:176`］
- **旧 updater で届ける。** v0.8.0 を旧 repo の通常の latest Release として出す。`PartsLife.exe`、`PartsLife.zip`、`SHA256.txt` を現行契約どおり公開する。ZIP の許可対象はルートの `PartsLife.exe` と `PartsLife.Check.exe`。単体と ZIP のメイン exe はいずれも実ファイルの版を `0.8.0` にする。v0.7.4 の自動モードは更新し、通知モードと書き込み不可の配置は案内に回る。オフの利用者は README、Release 説明、統合ページから手動で辿れる。［`PL/src/PartsLife.Update/UpdateEndpoints.cs:46`、`PL/src/PartsLife.Update/InstallLayout.cs:10`、`:25`、`PL/src/PartsLife.Update/Updater.cs:95`、`:141`、`:160`、`:193`、`:240`、`:256`、`PL/.github/workflows/release.yml:125`、`:137`、`:145`］
- **交換の起動確認。** v0.8.0 は旧更新役からの `--updated-from` と `Confirmed` のやり取りを維持する。更新済み子は共通 Mutex を取って通常窓を表示し、既存の生存確認を完了する。その後に初回案内を出す。Mutex が競合した子は短い理由表示後に終了し、旧 updater の巻き戻しを成立させる。通常運転に入った v0.8.0 では新しい版の定期確認と適用を終了し、起動時の交換回復経路は残す。v0.7.4 では旧親が `Confirmed` を待っており、新版には既定 15 秒の確認がある。［`PL/src/PartsLife/Updates/UpdateApply.cs:85`、`:110`、`:131`、`:187`、`PL/src/PartsLife.Update/SwapBoot.cs:39`、`PL/src/PartsLife/Updates/UpdateController.cs:48`、`:217`］
- **公開順。** PCミエル v0.6.0 公開とサイト切替 → 公開 ZIP で門・Party・一時コピーからの引き継ぎを再確認 → パーツぐらし v0.8.0 公開、README 更新、Release 説明と X の案内 1 回 → 1 か月後に旧 repo を archive。README と Release は新製品で引き継ぐ手順、旧版が引き続き起動できること、修正の終了時期を短く記す。［`SPEC-unify.md:173`、`:191`、`:217`、`:225`］

## 6. web-autoai 側への依頼書骨子

**依頼先と公開日。** web-autoai 担当が内部 Git で実装・点検・公開する。切替は PCミエル v0.6.0 の公開に合わせる。渡すものは節構成、本稿の文面、転送表、実際の版付き ZIP と SHA-256、デモ用の画像、PCB1 と `pcm-parts/1` の試料。こちらの作業は依頼書とアプリ側の確認まで。［`SPEC-unify.md:192`、`:219`］

| 統合ページの節 | 掲載する内容・導線 |
|---|---|
| PCミエルの概要とダウンロード `#download` | 主画面を先に紹介し、現行版の配布物、指紋、起動手順へ案内する。 |
| 点検と記録 `#checks` | システム、CPU・メモリ負荷、記憶媒体の変化、記録の開始・印・CSV の導線。実際に収録された機能を載せる。 |
| パーツぐらし `#party` | 「PCミエルからパーツぐらしの窓を開けます。PC の働きに合わせて、物語と冒険が進みます。」移行案内へのリンクを置く。 |
| 旧版からの引き継ぎ `#parts-life-migration` | 「パーツぐらしは PCミエルの一部になりました。旧版を終了し、PCミエルで『続きを引き継ぐ』を選んでください。元の保存はそのまま残ります。」v0.8.0 の配布前後で案内を出し分ける。 |
| 構成の共有 `#configuration` | PCB1 の取り込み先を `/my-config`、`pcm-parts/1` の取り込み先を `/tools/power-check` として、関連リンク欄または操作ボタンに設定する。サイトの実パーサーで両形式を受け入れることを確かめる。 |

| 旧入口 | 転送先案 |
|---|---|
| `/parts-life` と末尾 `/` のある入口 | `/pc-mieru#party` へ恒久転送。言語別入口があれば対応する言語の正規ページへ転送。 |
| `/parts-life` の既存の点検・記録・構成・配布・移行用の節リンク | 対応先は `#checks`、`#configuration`、`#download`、`#parts-life-migration`。旧フラグメントは HTTP リクエストに来ないため、公開中の旧アンカー名を調べ、必要な別名を統合先に設けてブラウザーで到達を確認する。 |

**確認済み。** PCミエルのページは `/pc-mieru`（2026-11-10 に 0.1.0 で公開、同日 0.2.0 に更新。web-autoai の記録 8685d820・d6eb4d64）。旧 `docs/SPEC.md` の `/tools/pc-mieru` は使われていない。**未確認**は言語別経路、`/parts-life` の旧アンカー、転送後の最終レスポンスで、担当側が確認して表を確定する。恒久転送は配布と移行節が利用可能になった時点で有効にする。［`~/.claude/worklog/web-autoai-done.md:176`、`:177`、`PC/docs/SPEC.md:264`］

文面は変わった点を短く伝える。評価・レビュー・価格に触れる文に数字を入れず、本文には URL や Markdown リンクを書かない。リンクは指定欄に設定する。負荷やメモリの数字は同条件で測定してから掲載する。［`SPEC-unify.md:192`、`:206`、`~/.claude/CLAUDE.md:110`、`~/.claude/projects/c--Users-Administrator/memory/feedback-pc-mieru-modest-posts.md:11`］

## 7. 配布と軽さの予算

**配布。** 現行 `publish.ps1` は managed exe を個別に自己完結・ReadyToRun で発行し、Helper と NetMeter を NativeAOT で加える。段 C は Party の WPF 発行を加え、同じ SDK・RID の共有 runtime ファイルは同一ハッシュのときだけ重複を畳む。内容が異なる同名ファイルは発行を失敗させる。WinForms の主プロセスに Party や WPF を読み込ませない。段 B の内外の bundle manifest に `PCMieru.Party.exe`、Party の DLL・実行時 JSON、WPF ランタイム、追加リソースを列挙し、外部 manifest と ZIP の集合を一致させる。v0.6 の発行工程は Party の同梱を必須とする。v0.5 が使う名前の規則と門プロトコルは段 B の契約に従う。［`PC/tools/publish.ps1:325`、`:334`、`:347`、`:355`、`DESIGN-B.md:82`、`:98`、`:108`、`:261`］

| 測定形 | 出荷予算の案 |
|---|---|
| 主画面のみ | 段 A/B の条件で private bytes 100 MB 未満、定常 CPU 0.3% 未満かつ 10 ms/s 未満、warm 起動 1 秒未満。段 C で主画面に足すのは窓メッセージの受け口だけで、追加の定周期処理はゼロ。 |
| Party 単独 | 同じ機械・窓寸法・折り畳み状態で v0.7.4 と交互に測る。定常 CPU 時間、private bytes、warm 初描画は v0.7.4 以下を初回の予算とする。最初に有効値が出る時刻も記録する。 |
| 主画面と Party | 合計から主画面単体の値を引いた増分を、Party 単独の予算以内に収める（両方が自分で採取するので、増分は Party 単独とほぼ同じになるはず）。超えたら §10 の 1 の受け渡しを後の版で検討する。 |

`tools/measure.ps1` に 3 形態を加える。ウォームアップ 30 秒の後に 5 分、各形態 3 回以上、同じ条件で比較する。CPU は全論理 CPU 比と ms/s を併記し、GUI・Party・SensorHost の個別値と合計、private bytes、working set、ハンドル数、初描画と最初の有効値を残す。2 時間の試験で長期増加と保存時間を調べる。Party の背景描画は変化時に限り、キャラクター描画は旧版と同じ 140 ms を最短とする。最小化・非表示時は描画を止める。v0.5 と v0.6 の ZIP 差分、基準の v0.2.0 の ZIP 51.6 MB との差も記録する。数値予算の v0.7.4 実測値は未確定。［旧版タイマーは `PL/src/PartsLife/MainWindow.xaml.cs:55`、`:340`。計測と比較条件は `DESIGN-A.md:304`、`DESIGN-B.md:246`、`SPEC-unify.md:202`］

## 8. 試験と作業順

### 試験

| 範囲 | 合格条件 |
|---|---|
| 進行と盤 | 旧 Core の保存・経験値・冒険・装備・長時間シミュレーション試験を移す。旧版と同じ完全な入力で進行とキーが一致。複数ソケット、GPU 順序、抜き差し、再接続、ディスク番号再利用、計測不能で誤って別の枠が育たない。起点にする試験は `PL/tests/PartsLife.Core.Tests/SaveMigrationTests.cs:1`、`SaveRecoveryTests.cs:1`、`ExperienceRuleTests.cs:1`、`GpuRosterTests.cs:1`、`LongSimulationTests.cs:1`。 |
| 採取と画面 | 偽 Snapshot で集計ソースが渡す値を固定（プロセス・窓の列挙をしないことも）。欠測、スリープ、周期の切替、古い値の表示を確認。窓メッセージは別の配置・別の利用者の窓に送らないこと、受け手が固定の 2 つしかしないことを試す。日英、主画面での言語切替、折り畳み、別モニター、既存 Party の前面化、「点検を開く」（主画面あり・なし）を smoke と実機で試す。 |
| 引き継ぎ | 旧名、両方あり、v0/v1、未知欄、部分破損、壊れた JSON、未来版、共有違反、起動中、確定直前の置換、同時起動、既存宛先、電断相当の中断を再現する。新側を再読込でき、元の SHA-256 が一致し、再試行で既存宛先を上書きしないことを確認する。 |
| オーナーの実保存 | 提供済みの控えを一時フォルダにコピーし、さらにそのコピーを読み取り用の入力にする。試験ハーネスに旧根・新根・Run の代替ストアを明示的に注入し、本番用のパス解決を通さない。入力の前後 SHA-256、章・場面、経験値キーと値、冒険の町・装備・所持品、表示設定、再起動後の継続を照合する。ライブの `%APPDATA%\PartsLife\state.json` にはアクセスせず、試料や物語ログを CI に載せない。 |
| Run | 承認印なしで実効有効、明示有効、無効、不明、読めない、既存の新値、拒否を試す。各レジストリ操作、commit 前後で失敗を注入し、完了時は新だけ有効、失敗時は元の 4 値へ復元されることを照合する。実ログオンは隔離した Windows 利用者で試す。 |
| v0.5 → v0.6 → v0.5 | 段 B の試験用 Party に加えて、**出荷済み v0.5 updater バイナリ**に実際の v0.6 WPF ZIP を渡す。正常系で manifest、導入記録、全ハッシュ、Party の起動を確認。欠落・改変 ZIP は交換前に拒否。交換後の確認失敗では Party と追加 runtime ファイルが消え、外した旧ファイルと旧導入記録のハッシュが戻る。別経路・別セッションで Party が門を保持している間は交換が延期される。［`DESIGN-B.md:258`、`:261`］ |
| 旧 updater とサイト | `PL/tools/update-dry-run.py` を起点に v0.7.4 から v0.8.0 の単体・ZIP、三つのモード、読み取り専用配置、起動確認、巻き戻し、通知の既読保持を試す。公開時は旧経路の恒久転送、言語・アンカー、取得した ZIP の SHA-256、二つの構成形式の取り込みまで確認する。［`SPEC-unify.md:241`、`:243`、`:244`］ |

### Sonnet 作業順

1. **保存と進行の移植。** `PcMieru.Party.Core` と既存試験を追加し、旧 I/O を呼ばない importer を作る。合格条件は Excel 等の目視で代用せず、fixture の状態比較と原本ハッシュで固定する。
2. **配布と門の最小実装。** Party の空ウィンドウを起動し、共通の門を先に入れる。WPF を含む ZIP と manifest を v0.5 実バイナリに渡し、交換と巻き戻しを早期に通す。
3. **Core の集計。** 共有 DTO、Snapshot の投影、集計専用ソース、機器の更新、欠測処理を入れる。CPU グループ・GPU 並び・ディスク対応を試料で固める。
4. **WPF の窓。** 偽ソースで起動と盤の描画を通した後、移植した盤と実測をつなぐ。主画面のナビ、前面化、言語、点検への遷移（窓メッセージ 2 つ）を入れる。
5. **引き継ぎの画面と Run。** 初回案内、保留、再試行、宛先保全、Run の journal を接続する。隔離した Windows 利用者と、オーナーの実保存の一時コピーで確認する。
6. **旧版の最後の Release 候補。** 別の旧 repo 枝に共通 Mutex、案内、起動確認を入れる。旧 updater で試験し、公開は v0.6.0 の確認後まで待つ。
7. **通し試験と出荷。** 軽さ、日英画面、v0.5 からの実 ZIP、サイトの取り込みを確認する。web-autoai 側へ切替素材を渡し、§5 の順で公開する。

## 9. 仕様で決まっていない点

| 未決事項 | 採用案と確定条件 |
|---|---|
| GUI 併用時の供給元 | Party はいつも自分で採取する（仕様 §2 のまま。§10 の 1）。併用の上積みを §7 で測り、予算を超えたときだけ後の版で受け渡しを足す。 |
| GPU の旧番号の対応 | 初回は旧版の並びを再現する。構成変化後の `gpu#i` は曖昧な対応を表示し、他の GPU に育成や装備を自動で付け替えない。旧版と同じ現物構成、同型複数枚、抜き差しの fixture と実機で確定する。 |
| Party の独立常駐と更新 | GUI を閉じても Party は残す。共通の門を持つ間は更新を延期し、利用者が Party を閉じた後に進む。主画面の終了操作と更新時の文面を実機で確認する。 |
| 部分修復と未来版 | v0/v1 の部分修復は箇所表示と再同意で扱い、未来版は取り込み保留とする。所有者の実保存の控えと既存 fixture を通して、維持すべき欄と修復対象を固定する。 |
| 軽さとページ転送 | 性能値は同条件の v0.7.4 実測で確定する。PCミエルのページ `/pc-mieru` は公開済み（11-10）。言語経路・旧アンカーは web-autoai 担当が公開前に確定する。 |

Opus の判断（2026-11-20）: GUI 終了後も Party は続ける・未来版の保存は取り込み保留・Run は復元用 journal（仕様 §8.2 の「失敗したら新旧の両方を元に戻す」に要る）を案どおり採る。GUI 稼働中の受け渡しは採らない（§10 の 1）。

## 10. Opus の点検で直した所（下書きから）

1. **主画面からの受け渡し（`pcm-party/1` の名前付きパイプ）をやめ、Party はいつも自分で採取する**（§2・§3・§7・§8・§9）。下書きは主画面が動いている間だけパイプで集計値を受け、主画面が消えたら自前の採取に切り替える形だった。仕様 §2 は「Party は自分のプロセスで Core の SamplerLoop を使う」と決めており、旧 v0.7.4 も自分で採取していた。受け渡しは相手の確認・再接続・切り替えの競合・遅い受け手と、作って守る物が多い割に、節約できるのは集計の PDH 数本分。併用の上積みを測り、予算を超えたときだけ後の版で足す。
2. **主画面との連絡は登録した窓メッセージ 2 つ**（§2.2・§3 の 4）。主画面には単一起動の仕組みが無いので、`--open-checks` で起動するだけでは主画面が 2 つになる。同じ配置の主画面の窓へ `PCMieru.OpenChecks.v1` を送り、いなければ起動する。言語の変更も `PCMieru.LanguageChanged.v1` で知らせ、Party は `gui.ini` を読み直す。受け手のすることは固定の 2 つだけで、引数を受けない。
3. **サイトの経路**（§6）。`/pc-mieru` は 11-10 に公開済みと記録で確かめた。未確認として残すのは言語別経路と旧アンカーだけ。
