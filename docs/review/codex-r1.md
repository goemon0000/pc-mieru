**私の推奨は、C#／.NET 10 の WinForms＋通常の JIT、GUI と CLI は別 exe、初版は「プロセス管理＋部品図鑑＋AI 向け診断レポート」に絞ることです。**

反対する点は二つあります。「Task Manager 全機能以上」を数日で完成させる計画と、未計測の段階で「Task Manager より軽い」と宣伝することです。前者は段階的な到達目標、後者は公開前の合格条件にしてください。

**1. 初版の機能範囲**

「v1」は数日で作る公開プレビュー相当とします。全面的な置き換えを名乗るのは、後続版で十分です。

| Task Manager 相当の機能 | 推奨時期・実装方針 |
|---|---|
| プロセス一覧 | **v1**。CPU、メモリ、I/O、PID、検索、並べ替え |
| パフォーマンスグラフ | **v1**。CPU、メモリ、ディスク、NIC、GPU の全体値 |
| アプリ履歴 | **v2**。まず自アプリの観測期間内の履歴。Task Manager の履歴と同等とは呼ばない |
| スタートアップ | **v1.x**。一覧→対応した登録方式だけ有効／無効。起動への影響度は別課題 |
| ユーザー | **v1.x**。セッション別集計。切断・サインアウト操作はさらに後 |
| 詳細 | **v1**。プロセス一覧の列追加として実現。別画面への二重実装は不要 |
| サービス | **v1.x**。SCM による一覧と PID 対応。開始／停止は権限に応じて helper |
| タスク終了 | **v1**。通常終了を試す操作と、強制終了を区別 |
| 優先度変更 | **v1.x**。権限確認と変更前の値の記録 |
| CPU affinity | **v1.x**。processor groups／CPU Sets を考慮。単純な64ビットマスクだけでは不足 |
| 効率モード | **v1.x**。EcoQoS と優先度の扱いを検証。低優先度だけで同名を付けない |
| ファイルの場所を開く | **v1**。取得可能な実行ファイルの選択表示 |
| ダンプ作成 | **v2**。人間による明示操作のみ。サイズ・機密性・対象権限を扱う |

「超える」機能の優先順位は次です。

| 機能 | 判断 |
|---|---|
| プロセスツリー | **v1 の差別化の柱**。親の終了・PID 再利用で誤接続しない |
| 「このプロセスは何？」 | **v1**。製品名、説明、発行元、パス、親プロセス。署名確認は選択時に実行 |
| プロセス別通信接続 | **v1.x**。`GetExtendedTcpTable`／`GetExtendedUdpTable`。接続先と PID は取れるが、通信速度は取れない |
| プロセス別 GPU | **v1.x**。PDH の GPU Engine／GPU Process Memory。初版必須にしない |
| プロセス別ディスク I/O | **v1 は「I/O 読み書き」と表示**。物理ディスク転送量とは区別。ETW による帰属は後続 |
| ファイルを使用中のプロセス | **v1.x**。Restart Manager で対象ファイルの使用者候補。完全なハンドル検索とは呼ばない |
| ハンドル／モジュール | **v2**。選択したプロセスだけ。権限・ビット数・ブロックする問い合わせが難所 |
| 「なぜ遅い？」 | **v1 は観測結果の説明**。原因候補、根拠、次の確認操作を出す |
| 温度 | **v1 は既存 GPU reader の対応範囲**。CPU／マザーボードはオプションの後続機能 |

GPU 使用率は各エンジンの単純合計にしないこと。Task Manager も代表値に最も忙しいエンジンを使っています。[Microsoft の説明](https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/)

**部品情報は「何でも取得」ではなく、「取得元と限界が分かる」ことを品質にする。**

下表の「不要」は一般的なローカル標準ユーザー環境を想定します。企業ポリシー、デバイス ACL、ドライバによる例外は残ります。Windows やメーカーの既存ドライバの利用は前提です。

| 部品 | 自作ドライバなしで使う取得元・内容 | 管理者／追加ドライバ・限界 |
|---|---|---|
| CPU | CPUID、`GetLogicalProcessorInformationEx`、SMBIOS、PDH。名称、命令セット、コア、キャッシュ、負荷 | 基本情報は不要。実効クロック、電力、温度を同じ方法で全部取れるわけではない |
| メモリモジュール | `GetSystemFirmwareTable('RSMB')` の SMBIOS Type 17、必要に応じ `Win32_PhysicalMemory`。容量、型番、スロット、構成速度 | 通常不要。**SMBIOS は SPD の直接読み出しではない**。XMP／EXPO プロファイルや実タイミングは保証不可 |
| マザーボード／BIOS | SMBIOS Type 0／2、WMI の BIOS／BaseBoard | 通常不要。電圧や VRM 温度は別物。ファームウェアの空欄・誤記あり |
| GPU | DXGI、SetupAPI、NVML／ADLX／IGCL。アダプター、VRAM、ドライバ、対応する温度・電力・ファン | 基本情報・多くの読取は通常不要。メーカーのドライバと対応 API が必要。全 GPU 共通の完全取得は不可 |
| ストレージ基本情報 | SetupAPI、`IOCTL_STORAGE_QUERY_PROPERTY`、ボリューム API。型番、バス、容量、ファームウェア、接続関係 | 通常不要で取れる範囲が広い。シリアル等はマスク対象 |
| NVMe 健康情報 | `IOCTL_STORAGE_QUERY_PROPERTY` の protocol-specific query による SMART／Health log | 無権限で成功する構成もある。ハンドルの要求アクセス、ACL、ストレージ経路次第で helper が必要。RAID／USB では非対応も |
| SATA SMART | ATA pass-through、対応する storage query／WMI provider | **管理者が必要になりやすい**。USB ブリッジ・RAID・属性のメーカー差が大きい。v1 の完全対応は捨てる |
| NIC | IP Helper の `GetAdaptersAddresses`／`GetIfTable2`、SetupAPI、WLAN API | 基本情報は通常不要。リンク速度と実測転送速度を区別 |
| ディスプレイ | `QueryDisplayConfig`、DisplayConfig の device info、EDID／`WmiMonitorID`、SetupAPI | 通常不要。現在の解像度・Hz と EDID の公称値を区別。KVM／ドック／仮想画面で情報欠落あり |
| オーディオ | MMDevice API、endpoint property store、SetupAPI | 通常不要。エンドポイント情報は取れるが DAC チップの詳細仕様までは保証不可 |
| USB | SetupAPI／CfgMgr32、対応する hub IOCTL。VID／PID、デバイス階層、対応時の接続速度 | 基本列挙は通常不要。hub への詳細問い合わせはアクセス条件あり。USB-C の実給電 W 数は一般取得不可 |
| バッテリー | `GetSystemPowerStatus`、battery class IOCTL。残量、設計／満充電容量、対応時の cycle count | 基本状態は不要。詳細はデバイス ACL と提供項目次第。デスクトップは対象なし |
| PSU | 対応するデジタル PSU の USB／HID、メーカー別 protocol | **一般的な ATX 電源は型番・定格 W・効率を読めない**。SMBIOS Type 39 があっても信頼できる完全情報ではない |
| ファン／CPU・基板センサー | LHM＋PawnIO、GPU はメーカー API | **PawnIO は署名済みでも追加カーネルドライバ**。導入に管理者権限。読取権限は導入版／ACL を確認し helper 側へ。機種依存 |

SMBIOS の標準取得口は [GetSystemFirmwareTable](https://learn.microsoft.com/windows/win32/api/sysinfoapi/nf-sysinfoapi-getsystemfirmwaretable)、NVMe の取得方式は [Windows の NVMe API 資料](https://learn.microsoft.com/en-us/windows/win32/fileio/working-with-nvme-devices)に基づきます。

重要な訂正：**「LHM＋PawnIO を入れれば SPD／温度が全部取れる」は成立しません。** 対象チップ、モジュール、ライブラリ版ごとの実装が必要です。[PawnIO 自体もハードウェアアクセス用ドライバ](https://pawnio.eu/)です。

各項目に `取得元・取得時刻・状態` を持たせてください。状態は「成功／非対応／権限不足／タイムアウト／未取得」。欠損をゼロや正常に置換しないことが大切です。

**2. 初心者と専門家の UX**

別アプリのような二つのモードより、**同じ画面で情報を段階的に開く構造**を勧めます。

- 初期画面：CPU／メモリ／GPU／ディスクの概要、その下に負荷上位のアプリ。
- ナビゲーション：「いまの負荷」「動いているもの」「PCの部品」「診断レポート」。
- 初心者向け表示名は製品名。PID、exe 名、ユーザー名などは列設定で追加。
- 選択すると右側に説明と詳細。終了ボタンを一覧の各行に並べない。
- CPU 基準で並べ、選択・右クリック中は行の移動を止める。手動の更新一時停止も付ける。
- 初期更新間隔は1秒。グラフ履歴はメモリ上のリングバッファ。
- 色だけに頼らず「負荷が高い」「情報を取得できません」を併記。
- 部品タブはまず型番・容量・接続・現在値。その下に専門項目を展開。
- 日本語表示でも JSON キーは英語で固定。列幅、並び順、選択タブを保存。

「信号機の健康判定」は控えめに。ゲーム中の GPU 使用率100%は異常ではありません。「高負荷」と「故障の兆候」は別の表示にしてください。

「なぜ遅い？」は、例えば「CPU全体の負荷は低いが、一つの論理CPUに負荷が集中しています。単一スレッドの制約が候補です」。証拠がない段階で「CPUが原因」と断定しない設計です。

**3. 技術選定と性能**

私の選択は **B の修正版：WinForms＋通常 JIT**。NativeAOT は初版から外します。

| 候補 | 評価 |
|---|---|
| A：WPF | 再利用と UI 開発速度では強い。RAM 最優先なら第一候補にしない |
| **B：WinForms＋JIT** | **推奨**。既存 C# reader を再利用でき、標準コントロールと仮想リストで実装しやすい |
| C：NativeAOT＋独自 Win32／Direct2D UI | 長期候補。日本語、DPI、アクセシビリティ、キーボード操作まで自作すると初版が遅れる |
| D：Avalonia NativeAOT | クロスプラットフォームが要件にない。今回の追加コストに見合いにくい |
| E：Rust＋egui | 小さくできる余地はあるが、既存 reader の移植／FFI と UI 品質の検証が増える |
| F：C++ Win32 | 資源効率の有力候補。短期開発では interop・寿命管理・保守の負担が大きい |

WinForms の trimming は現在も SDK で制限されており、NativeAOT 前提の計画にはしません。[公式の制約](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities)

**実測前の設計上の見積もり**として、最小限の WinForms 構成で次を置きます。

- GUI：起動後安定時の private working set **50〜100 MiB 程度**。センサー helper は別に計上。
- 更新1秒、約300プロセスで、CPU は全論理CPUを100%とした **平均0.2%以下を目標**。
- SSD 上で操作可能になるまで、warm **0.3〜0.8秒**、cold **0.8〜2秒**程度を仮置き。
- Defender、GPU reader 初期化、プロセス数で変動。**保証値でも、Task Manager に勝った証拠でもありません。**

メモリは private bytes と working set、CPU は CPU time と表示百分率、起動は初回表示と初回データ取得を分けて測ること。同じ機能・更新周期で Task Manager と比較してください。working set を強制削減して数字だけ小さくするのは不可です。

安価な計測の要点：

- `NtQuerySystemInformation(SystemProcessInformation)` の一括取得＋前回との差分を主軸にする。「常に一回」ではなく、バッファ不足時の再試行あり。
- PID 単独ではなく **PID＋作成時刻**で識別。構造体の境界検査と API 不成立時の代替経路を用意。
- パス、署名、アイコン、所有者、コマンドラインは遅延取得・キャッシュ。毎秒全プロセスを開かない。
- CPU 使用率は経過時間と CPU 数で正規化。Task Manager と計算法が違う場合は明記。
- PDH はクエリを維持。日本語 Windows のカウンター名問題を避ける API を使う。
- WMI はインベントリ取得に限定。毎秒呼ばない。機器変更時にキャッシュ更新。
- GPU Engine はプロセス／adapter／engine を正しくまとめ、表示していない詳細の取得を止める。
- UI は `ListView.VirtualMode`、行の差分更新、グラフだけ軽く描画。全行再生成を避ける。
- 最小化中は明示的な記録モード以外を減速。常時ファイル書き込みはしない。

**ETW は「管理者不要の安価な標準計測」と扱わないこと。** 通常の tracing session と system provider の権限は同じではありません。初版後に、必要時だけ起動する elevated helper で kernel disk／network tracing を実装する方が明快です。独自に権限を付与すれば非管理者経路もありますが、その設定自体が導入コストです。[SystemTraceProvider の権限](https://learn.microsoft.com/en-us/windows/win32/etw/configuring-and-starting-a-systemtraceprovider-session)

さらに、非同期 timeout はネイティブ呼び出しを止めません。GPU reader の既存 timeout 実装がスレッドを放置する方式なら、繰り返すと詰まります。**ハングから回復する単位は別プロセス**にしてください。

**4. CLI／AI 連携**

CLI 案には賛成です。ただし `ps` の CPU は二点間の差分が必要です。GUI が動いていないときは、短い観測時間を取って返します。

```text
xtm ps --sort cpu --top 10 --sample 1s --json
xtm hw all --json
xtm watch --interval 1s --ndjson
xtm report --duration 10s --format markdown --redact
xtm kill --pid 1234 --started-at <timestamp>
xtm gui
```

推奨する配布構造：

```text
Xtm.exe              GUI subsystem、通常権限
xtm-cli.exe          Console subsystem、CLI／後続の MCP
Xtm.Helper.exe       必要時のみ昇格、限定された操作
Xtm.SensorHost.exe   ハングし得る vendor API の隔離
Xtm.Core.dll         UI 非依存の共有ロジックとモデル
```

ブランド確定時に短い CLI 名へ変更すればよいです。GUI と CLI を一つの PE に押し込む利益は小さく、親コンソールへの attach やリダイレクトの例外処理が増えます。**単一 ZIP と単一 exe は別の要件**です。

GUI 稼働中は、その snapshot をユーザー限定 named pipe で再利用。未起動なら CLI が必要な reader だけ動かす。常駐サービスは不要です。

JSON には `schemaVersion`、UTC 時刻、観測期間、単位、取得元、欠損理由を含める。PID は JSON number、64ビットの大きな累積カウンターは受け手の精度も考えて表現を決めます。stdout はデータ専用、診断は stderr、終了コードは文書化します。

`report --ai` は曖昧です。**レポート生成と AI への送信を分離**し、初版はローカル出力だけにしてください。短い観測結果から貼り付け可能な要約を作れば、十分に連携価値があります。

MCP は **v1.x の read-only から**。破壊操作に GUI 確認を要求する案には次の問題があります。

- GUI のない利用で完了できない。
- 確認待ちで tool call が長時間停止する。
- 確認中に PID が再利用される。

導入するなら、変更操作は承認待ち ticket を返し、ローカル UI が具体的な対象と操作を表示して実行、MCP は結果を読む方式。確認は PID＋作成時刻＋操作内容に結び付けます。**AI が指定する `confirmed:true` は人間の確認ではありません。**

**5. 安全設計**

- 破壊操作直前に対象を再確認し、取得した同じ process handle に対して操作する。表示時の PID をそのまま信用しない。
- `IsProcessCritical`、保護状態、セッション、所有者、システムプロセス名を組み合わせる。名前一覧だけで安全判定しない。
- `csrss`／`wininit`／`lsass` 等の OS 中核や PPL を迂回して操作する機能は作らない。
- `explorer.exe` は「再起動」を人間向けに用意する候補。通常アプリと同じ一括強制終了に混ぜない。
- `kill <name>` の複数一致を黙って全終了させない。対象一覧の解決と実行を区別する。
- 「プロセスツリーを終了」は選択された親子関係と対象件数を示す別操作。自動追跡で新規子プロセスまで殺し続けない。
- サービス変更は依存関係と元状態を取得。スタートアップ無効化は削除せず復元可能な方式で実装。
- helper は任意コマンドを受け取らず、型付きの限定操作だけ。pipe の接続者、対象、要求を helper 側でも検証。
- 外部 DLL は既定の探索順に任せず、信頼する場所からロードする。

**AI に公開しない機能**は、任意 shell、任意メモリ読取・ダンプ、DLL injection、ハンドル強制 close、保護プロセスの迂回、ドライバ導入／制御、電圧・クロック・ファン制御です。

read-only でも漏えいは起きます。レポートと MCP の既定出力では、コマンドライン、ユーザー名、個人フォルダー、接続先、シリアルを必要最小限にします。プロセス名やファイル名を AI 向けの「命令文」に埋め込まず、信頼できないデータとして渡してください。

**6. 名前案**

既知の範囲だけの判断で、商標・GitHub・コマンド名の空きは未確認です。

| 製品名 | CLI 案 | コメント |
|---|---|---|
| **PC Mieru／PCミエル** | `pcmieru` | 第一候補。部品情報と初心者向け説明の両方に合う |
| Parts Lens／パーツレンズ | `ptlens` | Parts Life と系列化しやすい。Lens 系の一般名称との近さはある |
| Kibi Monitor／キビモニター | `kibimon` | 軽快さが伝わる。短い `kibi` は避ける |
| PC Satori／PCサトリ | `pcsatori` | 診断の意味に合う。Satori 単独は既存名が多い |
| Saku Monitor／サクモニター | `sakumon` | 日本語で発音しやすく、CLI も比較的短い |

`xtm` は設計用の仮名にはよいですが、公開名としては短すぎて衝突しやすいと思います。

**7. 最大のリスクと、数日で出す線**

最大のリスクは UI フレームワークより、**取得できない情報を取れると思い込むことと、計測値の意味を取り違えること**です。I/O をディスク転送量、リンク速度を通信速度、SMBIOS を SPD、欠損を正常と表示すると、見た目が完成しても診断ツールとして信用を失います。

次に重要なのは以下です。

- **性能**：全センサー常時取得・全行更新・毎秒署名確認をすると、どの言語でも重くなる。
- **対応機種**：開発機の NVIDIA 一台で AMD／Intel／ノート／USBストレージ対応を確認した扱いにしない。
- **アンチチート**：注入や process memory 読取を避けても誤検知ゼロは保証できない。ドライバなしの既定モードを優先。
- **配布信用**：未署名の間は SmartScreen が導入の障害。警告の一括無効化を案内しない。SHA256 は Authenticode の代替ではない。
- **ライセンス**：Parts Life が MIT でも、LHM 等の依存物まで MIT になるわけではない。通知・再配布条件を確認。
- **保守**：CLI／GUI／MCP が別々の計算を持つと食い違う。Core の snapshot を唯一のデータモデルにする。

数日で公開するなら、次で切ります。

1. **Win11 x64、プロセスツリー、基本メトリクス、検索、選択プロセスの説明、終了。**
2. **全体グラフ、CPU／DIMM／基板／GPU／ストレージの基本インベントリ。**
3. **CLI の `ps`／`hw`／`watch`／`report`、マスク付き診断レポート。**
4. **既存 GPU reader の対応範囲だけ温度表示。SMART 完全対応・PawnIO 導入・ETW・MCP は次版。**

GitHub Actions では snapshot の差分計算、PID 再利用、欠損処理、JSON 契約、SMBIOS／NVMe バッファ解析を検証します。実機では通常ユーザー、権限拒否、スリープ復帰、GPU reader ハングを確認。CI の仮想機でハードウェア対応や性能を証明した扱いにはしません。

**初版の売り文句は「PCの負荷と部品が見える。CLIと診断レポート付き」。軽さは測定結果で示し、全機能置換は後から達成するのがよいです。**
