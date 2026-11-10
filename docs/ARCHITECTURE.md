# PCミエル 構成と作り方の決まり

`docs/SPEC.md` が「何を作るか」、このファイルは「骨組みがどう分かれていて、足すときどこを触るか」。
骨組みの時点では全部が Fake（見本データ）で動く。担当 A〜E は Fake を本物に差し替える。

## 1. プロジェクト

```
src/PcMieru.Wire        契約（Helper との通信）。net10.0。何も参照しない。AOT で動く形だけ（反射なし）
src/PcMieru.Core        契約（record・interface）・Fake・Strings・JSON・PcMieruServices。net10.0-windows
src/PcMieru.Gui         PCMieru.exe（WinForms・PerMonitorV2・asInvoker）
src/PcMieru.Cli         pcmi.exe（CLI と MCP サーバ）
src/PcMieru.SensorHost  ハングし得るメーカー API の隔離。stdout に JSON 行
src/PcMieru.Helper      管理者権限の 1 回きり実行体。NativeAOT。参照は Wire だけ
tests/PcMieru.Core.Tests / Cli.Tests / Gui.Tests
tools/publish.ps1       配布フォルダ・zip・SHA256.txt（CI と同じ物をローカルでも）
```

Gui.Tests は WinForms（`UseWindowsForms`）と Gui への参照が要るので、Core.Tests に混ぜず別に持つ。
Strings の現在言語と culture は全体で 1 つなので、Cli.Tests と Gui.Tests は並列実行を切ってある。

## 2. 担当と触ってよい範囲

| 担当 | 領域 | 主に置く場所 |
|---|---|---|
| A | 採取（プロセス・性能） | `Core/Collection/`、`Core/Json/CollectionJsonContext.cs` |
| B | GUI | `src/PcMieru.Gui/` |
| C | ハード情報・SensorHost | `Core/Hardware/`、`Core/Json/HardwareJsonContext.cs`、`src/PcMieru.SensorHost/` |
| D | 管理操作・Helper | `Core/Management/`、`Core/Json/ManagementJsonContext.cs`、`src/PcMieru.Helper/`、`src/PcMieru.Wire/` |
| E | CLI・報告・MCP | `src/PcMieru.Cli/`、`Core/Reporting/`、`Core/Json/ReportingJsonContext.cs` |

統合担当だけが触る物: `Directory.Build.props`、`Directory.Packages.props`、`PcMieru.slnx`、`global.json`、
`Core/Contracts/`（共有の契約）、`Core/Json/PcMieruJsonContext.cs`、`Core/Localization/`、`Core/Services/`、
`tools/`、`.github/`。ここを変えたいときは、変えずに「自分の領域に新しい interface／record を足す」を先に考える。
既存の interface に足すなら、既定実装つきの任意メンバーだけにする。

契約の決まり（試験が見張る。`ContractRuleTests`・`JsonSchemaTests`・`RepoGuardTests`）:
- Core の契約 record は不変（`init` か位置引数だけ。`set` なし）。
- 取れない値は null や 0 にせず `Value<T>`（Ok / NotCollected / Unsupported / Denied / Failed / Gone）で返す。status 0 は NotCollected。
- 公開 enum は camelCase の文字列で JSON に出る。`OperationCode` の数値は Helper の終了コードなので変えない。
- JSON のスナップショット（`tests/PcMieru.Core.Tests/Fixtures/*.v1.json`）が形の固定。形を変えたら版を上げて新しい fixture を足す。
- 全 manifest は asInvoker。管理者権限を要求するのは Helper を `runas` で起こす経路だけ。
- Helper は Wire だけを参照する。Wire は何も参照しない。

## 3. 差し替えの仕組み

`PcMieruServices.Create(options)`（`Core/Services/PcMieruServices.cs`）が、次の順に各領域の `XxxModule.Register(registry, options)` を呼ぶ。
順序は固定: Collection → Hardware → Management → Reporting（最後。他を組み合わせるため）。

- 各領域は自分の Module の中で interface の実装を `registry.Set<IXxx>(...)` する。今は Fake（`IFakeImplementation` の印つき）。
- 本物に差し替えたら Fake の行を消す。`FakeAreas` から自動で消え、GUI の題名欄の「サンプルデータ」表示と CLI の stderr の注意書きも、全部の Fake が無くなった時点で消える。
- 一部だけ本物でも、残った Fake はその名前で表示される。見本値が実測値に見えないようにするための仕組みなので、Fake を本物に見せかけない。
- 試験は `PcMieruServices.Create(options, configure)` の `configure` で差し替える。

interface の一覧は `Core/Contracts/Interfaces.cs`（A: `ISnapshotSource` `IProcessDetailsProvider`／C: `IHardwareProvider` `IStorageHealthProvider` `ISensorClient`／
D: `IProcessControl` `IProtectionPolicy` `IServiceManager` `IStartupManager` `ISessionProvider` `IHelperClient` `ITaskLauncher`／
E: `IObservationEngine` `IKnownProcessCatalog` `IRedactor` `IReportBuilder`、`Core/Contracts/Insights.cs` の `IReadOnlyInsights`）。
統合で `Contracts/` に移した物: `IProcessGroupSource` `IProcessIconSource` `ProcessGroup`（A が集計の領域に置いていたもの。名前空間は同じ）。
`PcMieruServices` からは `ProcessGroups` `ProcessIcons` `Sampler` で引く。`Sampler`（`SamplerLoop`）は `CollectionModule` が本物・Fake のどちらでも登録する、**唯一の採取ループ**。
GUI の `SnapshotPump` は間隔・一時停止・低速をループの設定に書いて結果を UI スレッドへ渡すだけで、自分では採らない。
レポートと診断（`ReportBuilder` / `Insights.DiagnoseAsync`）の観察窓は、ループが動いていて止まっていなければループの履歴（`SamplerLoop.History`）から作る。
ループが動いていない（`pcmi` と MCP は起動しない）・一時停止中・まだ 1 回も採れていない・5 秒以上新しい採取が無いときは、これまで通り窓が自分で採る。

## 4. 文字列と JSON

Strings（`Core/Localization/Strings.cs`）は全体で 1 つの表。ja と en を必ず両方書く。
- キーの接頭辞で領域を分ける: `core.` `gui.` `cli.` `hw.` `mgmt.` など。自分の接頭辞の外に書かない。
- 各領域が `StringEntry[] All` を持ち、Module か起動時に `Strings.Register`。同じ表を 2 回登録しても害はない。
- `{name}` の置き換えは ja と en で同じ名前の組にする（試験が見る）。無いキーは `[キー]` と出る。GUI の smoke は画面の文字に `[gui.` `[core.` `[cli.` が混じっていたら失敗にする（`[hw.` `[mgmt.` は見ないので、その領域のページを作るときは `SmokeText.IsPlaceholder` に足す）。
- enum から作るキー（状態など）は、全メンバー分があることを試験が確かめる。

JSON（`Core/Json/`）は領域ごとに `JsonSerializerContext` を持ち、`PcMieruJson` が鎖でつなぐ。
ソース生成は 1 つの context を複数ファイルに分けられないので、型は自分の領域の context に足す。
`Value<T>` の T は鎖のどこかで引けること（試験が見る）。CLI の `--json` も MCP も `PcMieruJson` を通す。

## 5. 足し方

**CLI のコマンド**: `src/PcMieru.Cli/Commands/` に `ICliCommand` を実装した class を 1 つ置く。反射で見つかるので共有ファイルは触らない。
データは `Out`、診断は `Err`、引数の誤りは `ArgException`（終了コード 2）。1 行の説明は `cli.help.<name>` を `Texts` で渡す。
まだ本体の無い物は `StubCommand`（eco / kill / priority / run / services / startup / watch）。持ち主が基底を `ICliCommand` に替えて `RunAsync` を書く。

**GUI のページ**: `Pages/` に `PageBase` を継いだ class を 1 つ、`Shell/PageCatalog.cs` に 1 行、`gui.*` の文字列を `GuiStrings.cs` に足す。
ページは初めて開かれたときに作られる。`IThemed` を実装した部品は ThemeService が色と DWM 暗色を当てる。
色は `BackColor` を読んで書き戻さない（環境色が固定されて、テーマを替えても戻らない。戻すときは `ResetBackColor()`）。
`PCMieru.exe --smoke out.json [--shots dir]` が全ページを 2 言語・2 テーマで開いて、文字の欠け・色のずれ・ナビの同期を調べる（終了コード 0 で合格）。
`--lang ja|en` `--theme system|light|dark` `--page <id>` `--fakes` で見た目の確認ができる。

**MCP のツール**: `Cli/Mcp/McpTools.cs` に足す。道具は 7 つ（`get_system_summary` `list_processes` `get_process` `get_hardware` `get_storage_health` `diagnose` `get_report`）で、
`IReadOnlyInsights` の読み取り 7 メソッドに 1 対 1。MCP は読み取り専用で、プロセス操作・サービス・Helper を一切名指ししない（`McpBoundaryTests` がソースを grep して守る）。
MCP の伏せ字は起動引数 `--no-redact` だけで切り替わり、呼び出しごとの切り替えは無い（MCP は 1 引数の `RedactText` / `RedactJson` しか呼ばない）。
`IRedactor` には呼び出しごとの形 `RedactText(text, redact)` / `RedactJson(node, redact)` があるが、これは GUI の「個人情報を伏せる」チェックと `pcmi report --no-redact` 用で、
`redact && Enabled` のときだけ伏せる。つまり**チェックを入れても `--no-redact` は上書きできず、外した 1 回だけ素通しにできる**。
同じく `IReportBuilder.RenderMarkdown` / `RenderJson`（既定は `NotSupportedException`）は同じ文書を伏せる・伏せないの両方で出せて、`ReportDocument.Redacted` は実際にやったことを書く。
ボリュームラベル・音声の名前・USB の名前は JSON の木の中の場所（`volumes[].label` `audio[].name` `usb[].name`）で伏せる。キー名 `label` / `name` では伏せない（他の項目まで消えるため）。
プロトコルは stdio の ndjson JSON-RPC、版は 2025-06-18 / 2025-03-26 / 2024-11-05。
ツールを増やすなら「読むだけ」を保てるか先に決めて、`IReadOnlyInsights` の側を変える（それは統合担当に連絡）。

**Fake を本物に**: 自分の領域の `Fake*.cs` の中の `XxxModule.Register` で `registry.Set<IXxx>(new RealXxx(...))`。Fake の class は他の試験が使うので、本物が安定するまで消さない。

## 6. ビルドの注意

- NuGet の監査: GHSA-p3jp-9v83-76w9（Brotli）は `Directory.Build.props` で抑止してある。PCミエルは Brotli を解凍しない。SDK が直ったら行ごと消す。
- WinForms の WFO0003（manifest と ApplicationHighDpiMode の二重指定）は Gui の csproj で抑止。どちらも PerMonitorV2。
- Helper の NativeAOT 発行は MSVC のリンカを `vswhere.exe` で探す。ローカルは `C:\Program Files (x86)\Microsoft Visual Studio\Installer` が PATH に要る（`tools/publish.ps1` が足す）。
- リンカ引数 `/DEPENDENTLOADFLAG:0x800` で、DLL を System32 からだけ読む。`publish.ps1` が exe を直接読んで、0x800 が立っていること・CLR ヘッダが無いこと・静的 import が許可表の DLL だけであることを確かめる。
  許可表に足すのは「System32 にある、Helper が本当に使う DLL」だけ。足した理由をコミットに書く。
- 試験は 3 本とも net10.0-windows。Gui.Tests は窓を出さずにページを作る範囲（`Harness`）で、窓を開く確認は `--smoke` が受け持つ。

## 7. 配布（`tools/publish.ps1`）

```
powershell -File tools\publish.ps1 -Version 0.2.0 -Verify     # dist\PCMieru-0.2.0-win-x64\ と .zip と SHA256.txt
```

- Gui・Cli・SensorHost を別々に自己完結＋ReadyToRun で発行し、1 つのフォルダに合流させる。
- 同名のファイルは SHA256 が一致していること。違えば止まる。例外は Windows Desktop 側の `System.Drawing.dll` と `Microsoft.VisualBasic.dll` の 2 つだけ
  （Gui だけが Desktop ランタイムを参照するため、型の転送表が違う。Gui の物を採る）。例外を増やすときは文字列表で違いを見てから。
- Helper は NativeAOT の単体 exe。マネージドの dll としては入れない（あれば失敗）。
- `-Verify` は合流後のフォルダで `pcmi version`・`ps --json`・`hw --json`・MCP の initialize と tools/list・`PCMieru.exe --smoke` を実際に動かす。
  Windows PowerShell 5.1 は UTF-8 コンソールだと、ネイティブ exe へ流す最初の行の前に BOM を付ける。MCP サーバは先頭の U+FEFF を 1 つだけ読み飛ばす（途中のは読み飛ばさない）。
- LICENSE（MIT）と NOTICE.txt（第三者の表記）を同梱する。他のプロジェクトのコードを入れたら NOTICE に書く。
- CI（`.github/workflows/ci.yml`）は build・test・GUI smoke と、zip なしの publish。`release.yml` は `v*` のタグで試験・発行・Release に zip と SHA256.txt を付ける。
  どちらも「手元で通った」を保証しない。GitHub 上での初回の結果は未確認。

## 8. 既知の限界と引き継ぎ

- GUI の一覧のヘッダ（ListView の列見出し）は、暗色テーマで実機では暗くなる設計（`SetWindowTheme`）だが、`--shots` の画像（DrawToBitmap）では白く写る。画像だけの差で、実機の目視は未確認。
- 実機の測定は骨組みでは一度もしていない。Fake の数字を見て「速い・軽い」と言わない（SPEC の第 11 節の測り方で測る）。
- **D への引き継ぎ**: Helper に本物の P/Invoke を入れるとき、pl-sensors の `CheckLazyNativeModules`（ILC のマップから、遅延で読まれるネイティブ DLL を拾う検査）を移す。
  いまの Helper の検査は PE の import 表だけで、遅延ロード（`LoadLibrary` 経由）は見えない。
- 性能の目標（SPEC 第 11 節）は未測定。配布フォルダは約 113 MB（自己完結で .NET と WinForms を含む）。軽さを言う前に、トリミングや分割の余地を見る。
