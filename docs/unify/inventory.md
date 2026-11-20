# PCミエル × パーツぐらし 統合 — 棚卸し（2026-11-20）

対象: PCミエル main 7a0acd8（v0.2.0）＋未マージ area/parts-share 9e068c4・area/icon b61b791／
パーツぐらし release/v0.7.4 25e4571（= origin/main）＋未リリース feature/sensor-log 25f7baf。
読み取りだけで作った。数値のうち「未計測」「自己申告」と書いた物は誰も測っていない。

## 1. 全体の形

| | PCミエル | パーツぐらし |
|---|---|---|
| GUI | WinForms（.NET 10）・タスクマネージャー型の窓。常駐しない | WPF（.NET 10）・透明の常駐窓（ドット絵）＋点検の窓 CheckWindow（同じプロセス・押すまで DLL を読まない） |
| exe | PCMieru.exe（GUI）・pcmi.exe（CLI と MCP）・PCMieru.SensorHost.exe（NVML/ADLX を隔離・stdout に ndjson）・PCMieru.Helper.exe（AOT・管理者・**要求ごとに runas・結果はファイル**）・PCMieru.NetMeter.exe（AOT・管理者・ETW・名前付きパイプ） | PartsLife.exe（単一ファイル）・PartsLife.Check.exe（AOT・管理者・**名前付きパイプの JSON Lines で進み具合を返す・stop を受ける**） |
| 権限 | 全 exe asInvoker。昇格は Helper・NetMeter だけ | 本体 asInvoker。昇格は Check.exe だけ |
| 共通の土台 | Core（UI なし・Snapshot・`Value<T>` で「取れない物を取れたと言わない」）・Wire（Helper 契約と特権処理） | Core（物語・冒険・保存。WPF なし）・Checks（UI なしの点検）・Update |
| 試験 | Core 1051・Cli 324・Gui 166（RealMachine 76 は CI で外す） | Core 約113・Update 約142・Checks（件数未確認） |
| CI と配布 | GitHub Actions・zip＋SHA256.txt・未署名・README 無し | GitHub Release・zip＋SHA256.txt・未署名（SignPath 申請中） |

## 2. 機能の対照

記号: ◎ 製品にある・○ 未リリースの枝にある・△ 部分的・— 無い

| 領域 | 機能 | PCミエル | パーツぐらし | 統合での扱いの見込み |
|---|---|---|---|---|
| 画面 | プロセス・サービス・スタートアップ・ユーザー・パフォーマンス・診断・設定 | ◎ | — | 主の画面のまま |
| 画面 | PC の構成（SMBIOS・EDID・ツリー） | ◎ | △（点検のシステム面に WMI で一部） | PCミエルに寄せる |
| 画面 | 常駐のドット絵の窓（負荷でキャラが動く） | — | ◎ | 従（サブ）。開けば出る |
| RPG | 物語 23 章・冒険 8 町・レベル・小話 | — | ◎（Core 約3.2k 行＋JSON 約330KB） | 従。ハードの数値だけ渡せば動く形（`Director.Tick(Work, slots)`） |
| 計測 | CPU 全体・コア別・メモリ | ◎ | ◎ | PCミエルの Snapshot へ一本化 |
| 計測 | GPU 使用率・VRAM（PDH・DXGI/D3DKMT） | ◎ | ◎ | 一本化。GpuCounterName は元が同じ |
| 計測 | GPU 温度・電力・ファン・クロック（NVML・ADLX） | ◎（SensorHost） | △（点検の中だけ。ADLX は sensor-log 枝） | PCミエルの SensorHost に一本化 |
| 計測 | ディスクの読み書き | ◎ | ◎ | 一本化 |
| 計測 | プロセス別の通信量（ETW） | ◎（opt-in・UAC 経路は手で未確認） | — | そのまま |
| 計測 | CPU 温度・ファン・電圧 | —（カーネルドライバを避ける） | — | 両方とも無い。統合でも足さない前提 |
| 点検 | システム点検（WMI・DXGI・7 日分の WHEA/disk/Kernel-Power 41） | — | ◎ | 作り直して PCミエルの診断へ |
| 点検 | CPU 負荷（答え合わせつき・持続性・コア間の差・実効クロック） | — | ◎ | 作り直して移す |
| 点検 | メモリ負荷（空きの一部を借りてパターン検査） | — | ◎ | 作り直して移す |
| 点検 | 記憶媒体の精密（SMART/NVMe の**前回からの増分**・読み取りだけの表面スキャン） | △（SMART を読むだけ・増分も走査も無い） | ◎（Check.exe 経由） | PCミエルの Helper では長い処理と進み具合を運べない＝設計の論点 |
| 点検 | GPU 負荷（D3D11 計算シェーダ・83℃ で止める・NVIDIA だけ） | — | ○（作ってあるがビルドで切ってある・実機未検証） | 立ち会いの実機確認まで切ったまま |
| 点検 | テストの記録（記録・印・要約・AI 向けコピー） | — | ○（sensor-log 枝。§4） | 作り直して移す |
| 書き出し | 構成コード PCB1＋JSON（WMI・DXGI。`/my-config` が読む） | — | ◎ | 互換を切らない |
| 書き出し | `pcm-parts/1`（SMBIOS・EDID・PCI ID。`/tools/power-check` が読む） | ○（parts-share 枝・版は 0.2.0 のまま） | — | 互換を切らない。形を変えるなら先に 6746a72f へ |
| 書き出し | 診断レポート（Markdown/JSON・伏せ字） | ◎ | — | そのまま |
| 外から | CLI `pcmi`（15 コマンド） | ◎ | △（`--shot` 等の開発用だけ） | 点検を CLI に出すかは仕様で |
| 外から | MCP（読むだけの 7 ツール） | ◎ | — | 点検の結果を読むツールを足すかは仕様で |
| 更新 | 自動更新（GitHub Release・許可リスト・SHA256・署名者の一致・ロールバック） | — | ◎（v0.7.3〜） | PCミエルへ持ってくる候補。既存利用者の移行に要る |
| 起動 | ログオン時の自動起動（HKCU Run） | — | ◎ | 常駐の窓を持つならいる |
| 保存 | 設定 | `%LOCALAPPDATA%\PCMieru\gui.ini`（SPEC は `%APPDATA%\settings.json` と書く＝食い違い） | `%APPDATA%\PartsLife\state.json`（PascalCase・壊れたら退避と部分修復） | 物語の保存は state.json をそのまま引き継げる形に |
| 保存 | 点検の履歴 | — | `%APPDATA%\PartsLife\checks\`（1 台 200 件） | 引き継ぐ |
| 言語 | ja/en | ◎（Strings 表・全キー必須の試験） | ◎（`T(ja,en)`・物語は JSON に併記） | PCミエルの表へ寄せる。物語の JSON はそのまま |
| 見た目 | 明暗のテーマ・文字サイズ | ◎ | — | |
| 写し | 画面の写し | `--smoke --shots --fakes` | `--shot`・`--shot-check` | codex の点検に使う |

## 3. 重なっている部品（同じ元から写した物）

PCミエルは NOTICE.txt に MIT で次を明記して写している: SensorHost の Nvml.cs・Adlx.cs（sensor-log 枝から）、SensorMapping、
StorageProtocol・HealthParsers・StorageHealthReader、Wire の StorageProtocol（読み取りコマンドの許可リスト）・DiskOperations、
AOT exe の `CheckLazyNativeModules`。GpuCounterName の解析もパーツぐらしが元。
→ 点検を作り直すときの下地は PCミエルにもうある。二重に持たず PCミエル側を正にする。

## 4. 未リリースの物

- **PCミエル area/parts-share（9e068c4）**: `pcm-parts/1`。本番の `/tools/power-check`（noindex）が読んでいる。`app` が "PCMieru 0.2.0" のまま。`observed.nvmlDefaultPowerLimitW` は文字列 "board default N W" を読み戻して作る（GpuSensorMerge.TryParseBoardDefaultWatts）＝脆い。
- **PCミエル area/icon（b61b791）**: 目のアイコン。`integrate` と同じ commit で main に入っていない。
- **PCミエル NetMeter の UAC 経路**: 昇格済みの開発シェルでしか動かしていない。
- **パーツぐらし feature/sensor-log（25f7baf）**: テストの記録。→ §4.1

### 4.1 テストの記録（パーツぐらし feature/sensor-log・未リリース）
- 門: `Features.TestRecording = true`（コンパイル時定数）。管理者不要・ドライバ無し・通信無し。点検の窓の「記録」面。押すまで何も読まない・書かない。常駐しない。
- 読む物（1 秒ごと・1 回の読みは 0.8 秒で諦める）: PDH（論理 CPU 別の負荷・周波数・OS の制限指標・**CPU パッケージ電力**＝PDH の電力メーター mW→W）・GlobalMemoryStatusEx・ディスク全体・AC/電池・NVML（温度・クロック・電力と上限・ファン・使用率・VRAM・**制限理由のビット**）・AMD は ADLX（vtable の位置を手で書いている・確かめたのは iGPU 1 台）。CPU 温度・電圧・ファン・基板は「拡張が要る」と出して列を作らない。
- 仕組み: ISensorSource／SensorHub／SourceRunner（ソースごとの専用スレッド・前の読みが戻らなければ飛ばす）。Availability 型が「取れない理由」を持つ。**PCミエルの Core/SensorHost とは別に同じ物を読んでいる**＝統合では PCミエルの Snapshot と SensorHost を源にして、記録・要約・書き出しの層だけを移すのが筋。
- 置き場と形式: `ドキュメント\PartsLife\runs\`。`run-<ID>.json`（format "partslife-run"/1・機械名やユーザー名やパスは入れない）＋`sensors-<ID>.csv`（1 行ごとに Flush・ただし Flush(true) ではない）。12 時間で自動停止・開始時に空き 200MB 未満なら断る。**削除も保持の上限も無い**。
- 要約（純関数）: 負荷あり 50% 以上・無負荷 20% 以下・持続値は最後の 1/3（1 分未満は出さない）・30 秒窓で温度 +3℃ かつクロック −5% を「同時に起きた」と書く（原因とは言わない）・GPU の制限理由を秒で集計・印（bench/stress）の区間ごとの数値。
- AI 向けの書き出し（"partslife-ai-export/1"）: 前文（3 部で答えさせる・診断の断定と電圧の助言を禁じる）＋要約＋最大 48 点の経過＋項目辞書。許可リスト方式・PC 名やユーザー名やパスやシリアルらしき名前は出さず保留・最後に ExportGuard（文字列一致）。CPU/GPU の型番は出す。
- 試験: 記録 約30・要約 21・書き出し 12・ファイル 11・門 11。
- 弱い所: 記録中のディスクフルは見ていない／PDH の検出に固定の Sleep がある（Sources.System.cs:53, 290）／電力の単位を確かめたのは 1 台。
- 文書に残っている次の段: CPU 温度など（LibreHardwareMonitor＋PawnIO を別プロセスで）・Intel IGCL・同じ PC のブラウザとの自動連携・サーバー側の解析・同名 AMD GPU の見分け。

## 5. 統合の前に知っておくべき食い違い・弱い所

### 前提の読み違い（仕様の段で直す）
- 「常駐は操作を受けない」は**入力を透過するという意味ではない**。パーツぐらしの窓はドラッグ・右クリック・拡大縮小・ボタンを受ける。README の意味は「ゲームとして選ばせたり育てさせたりしない」。
- PCミエルは常駐しない。統合して常駐の窓を持つなら、§11 の軽さの合格条件（private bytes < 100MB・定常 CPU < 0.3%・warm 起動 < 1 秒）を常駐の形でどう測るかを決め直す。しかも **§11 は一度も測っていない**（`tools/measure.ps1` がどこにも無い）。パーツぐらしの「CPU 約1.8%・118MB」も自己申告。

### 設計の論点になる違い
- 管理者の補助 exe の通信が違う: PCミエル Helper＝要求 1 回で UAC 1 回・結果はファイル（短い処理向け）。パーツぐらし Check.exe＝パイプで進み具合と停止（表面スキャンのような長い処理向け）。精密点検を移すなら PCミエル側に長い処理の道が要る（NetMeter と同じパイプの作法が下地にある）。
- GUI の技術が違う（WinForms と WPF）。RPG の窓は WPF の DrawingVisual で描いている。
- 書き出しが 2 つ（PCB1 と pcm-parts/1）。取る道も違う（WMI＋DXGI と SMBIOS＋EDID）。両方ともサイトで生きている。

### それぞれの弱い所（作り直すときに直す候補）
- パーツぐらし: タイマーの失敗が累計 60 回で常駐が永久に止まる（MainWindow.xaml.cs 171-181）／保存の失敗を黙って握りつぶす（PartyState.cs 388）／ディスクの種類を起動時 1 回しか取らない／WMI に時間切れが無い・`DriveInfo.IsReady` が切れたネットワークドライブで固まりうる／表面スキャンの同期 ReadFile が途中で止まらない／点検の履歴を Flush せずに書き、壊れていたら黙って初期化する（Baseline.cs 497-520）／EventCounts.cs:22 のフラグ 0x100 が意図どおりか要確認。
- PCミエル: §11 未計測・配布物 113〜120MB／設定の置き場が SPEC と違う／SPEC の「サイトを開く」メニューが無い＝更新を知る手段が無い／README・インストーラが無い／ARCHITECTURE.md が古い（StubCommand の記述）。
- 両方: 配布 exe が未署名（パーツぐらしの更新の署名確認は今は実質効いていない）。
