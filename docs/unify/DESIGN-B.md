# DESIGN-B — 自動更新とテストの記録（PCミエル v0.5.0）

下書き: codex gpt-6.2-sol（2026-11-20）。点検と修正: Opus（司会 2fb51ba0）。直した所は §13 で、**本文と §13 が食い違ったら §13 を取る**。実装、ビルド、実機試験は未実施。

## 0. 版の線

正本は `docs/unify/SPEC-unify.md` u0.4、`area/unify@65bf1ba`。PCミエルの既存コードの行番号もこの版を指す。`PL/` は `C:\Users\Administrator\dev\pl-cloud` の `25e4571`、`REC/` は `C:\Users\Administrator\dev\pl-sensors` の `feature/sensor-log@25f7baf`。段 A の設計を実装上の前提とする。以下の **確認済み** は現物または仕様で確認した事項、**設計** は段 B で新設する契約を指す。

段 B は配布一式の自動更新と、記録の開始・印・停止・一覧・削除・CSV・基本要約まで。AI 向けコピーと保持上限は後段、Party 本体とパーツぐらしからの移行は段 C。v0.5 の更新器による Party 入り ZIP の導入・巻き戻しは、記録と独立した出荷条件とする。更新モードは初回に尋ね、「知らせるだけ」を選択済みで表示する。同意確定まで更新通信はゼロ。未署名版を無人で入れるのは「自動」を明示選択した場合に限る。`docs/unify/SPEC-unify.md:121`、`:143`、`:199`、`:210`、`:224`、`:241`。

## 1. プロジェクトと移植

**設計。** `PcMieru.Update` に通信、配布検証、導入記録とトランザクションを置く。`PcMieru.Swap` はこれを参照する `asInvoker` の NativeAOT 実行役で、配布名は `PCMieru.Swap.exe`。起動の門と journal の有無を調べるコードは `PcMieru.Wire` に置き、通常起動する六つの exe で共用する。記録は段 A の `PcMieru.Checks/Recording` に置き、Core の採取ストリームを読む。GUI は設定、操作、表示を担当する。新規プロジェクトと試験を `PcMieru.slnx` に登録し、移植元を `NOTICE.txt` に追記する。

**確認済み。** 現行の GUI・CLI・SensorHost は Core 参照を持ち、Core、Helper、NetMeter は Wire を参照する。発行処理は GUI・CLI・SensorHost の self-contained 出力を結合し、Helper と NetMeter は検査した NativeAOT exe を個別に足す。`src/PcMieru.Gui/PcMieru.Gui.csproj:25`、`src/PcMieru.Cli/PcMieru.Cli.csproj:14`、`src/PcMieru.SensorHost/PcMieru.SensorHost.csproj:15`、`src/PcMieru.Core/PcMieru.Core.csproj:11`、`src/PcMieru.Helper/PcMieru.Helper.csproj:26`、`src/PcMieru.NetMeter/PcMieru.NetMeter.csproj:27`、`tools/publish.ps1:189`、`:211`、`:327`、`:344`、`:347`。

| 旧ファイル | 行き先と扱い |
|---|---|
| `PL/src/PartsLife.Update/{AppVersion,UpdateEndpoints,UpdateHttp,ReleaseInfo,Sha256List,UpdateSchedule}.cs` | `PcMieru.Update/Feed`。版比較、Release 解析、転送検査、ストリームの上限、SHA と確認間隔を移植。repo、資産名、同一タグへの URL 制約を書き換える。`PL/src/PartsLife.Update/UpdateEndpoints.cs:46`、`:90`、`PL/src/PartsLife.Update/UpdateHttp.cs:41`、`:84`、`:112`、`PL/src/PartsLife.Update/ReleaseInfo.cs:35`、`PL/src/PartsLife.Update/UpdateSchedule.cs:17` |
| `PL/src/PartsLife.Update/{Updater,InstallLayout,UpdateState}.cs` | `PcMieru.Update/{Feed,Bundle,State}`。確認結果と再検証の考え方を移植。単体 exe と固定二ファイルの分岐を、配布一式の manifest とキャッシュに書き換える。`PL/src/PartsLife.Update/Updater.cs:155`、`:173`、`:190`、`:256`、`PL/src/PartsLife.Update/InstallLayout.cs:12` |
| `PL/src/PartsLife.Update/Authenticode.cs` | `PcMieru.Update/Bundle`。WinVerifyTrust の呼び出しを移植。署名者の照合をクライアントに埋め込んだ Subject・Issuer の組へ変更する。`PL/src/PartsLife.Update/Authenticode.cs:54`、`:90`、`:154`、`:168` |
| `PL/src/PartsLife.Update/{SwapBoot,Swapper,SwapRecovery}.cs`、`PL/src/PartsLife/Updates/UpdateApply.cs` | `PcMieru.Update/Transaction` と `PcMieru.Swap`。生存確認と故障注入試験を引き継ぎ、名前付き Mutex、`.old`、旧 GUI による交換を、実ファイルの門、全体の控え、別プロセスの journal に書き換える。`PL/src/PartsLife.Update/SwapBoot.cs:36`、`:60`、`:73`、`PL/src/PartsLife.Update/Swapper.cs:37`、`PL/src/PartsLife/Updates/UpdateApply.cs:24`、`:110`、`:187` |
| `PL/src/PartsLife/Updates/UpdateController.cs` | `PcMieru.Gui/Updates/UpdateController.cs`。確認の予約、状態通知、操作待ちを移植。WPF のタイマーとメニューを WinForms の設定ページと終了手順へ書き換える。`PL/src/PartsLife/Updates/UpdateController.cs:36`、`:112`、`:137`、`:194`、`:269`、`:303` |
| `REC/src/PartsLife.Checks/Recording/{RunRecorder,RunStore,Session,Series}.cs` | `PcMieru.Checks/Recording`。印のコード、セッションと CSV の組、旧形式の読み取りを引き継ぐ。記録の寿命、採取時刻付き CSV、耐久書き込み、空き容量による停止を書き換える。`REC/src/PartsLife.Checks/Recording/RunRecorder.cs:138`、`:190`、`:208`、`:219`、`REC/src/PartsLife.Checks/Recording/RunStore.cs:15`、`REC/src/PartsLife.Checks/Recording/Session.cs:11`、`:85`、`:234`、`REC/src/PartsLife.Checks/Recording/Series.cs:53` |
| `REC/src/PartsLife.Checks/Recording/{Metrics,Summarizer,Summary,RecordText}.cs` | `PcMieru.Checks/Recording` と GUI の文言表。項目、閾値、理由名、印の区間を移植し、時間集計を書き換える。`REC/src/PartsLife.Checks/Recording/Metrics.cs:96`、`:116`、`REC/src/PartsLife.Checks/Recording/Summarizer.cs:33`、`:197`、`:226`、`:283`、`:325`、`REC/src/PartsLife.Checks/Recording/Summary.cs:9`、`REC/src/PartsLife.Checks/Recording/RecordText.cs:13` |
| `REC/src/PartsLife.Checks/Recording/{DefaultSources,SensorHub,SensorPlumbing,PdhQuery,Sources.*}.cs` | 独立した採取器の移植は対象外。電力の換算を Core の PDH、制限理由を SensorHost の NVML に足す。AC・電池は Core の既存 Windows 読み取りを共用する。`REC/src/PartsLife.Checks/Recording/DefaultSources.cs:18`、`REC/src/PartsLife.Checks/Recording/PdhQuery.cs:29`、`REC/src/PartsLife.Checks/Recording/Sources.System.cs:45`、`:190`、`:349`、`src/PcMieru.Core/Collection/PdhSystemSampler.cs:33`、`src/PcMieru.Core/Hardware/Native/BatteryNative.cs:31`、`src/PcMieru.SensorHost/Nvml.cs:74` |

## 2. 呼び出し口と実行の寿命

**設計。** 下記は境界のシグネチャ。ファイル、HTTP、子プロセス、時計は内部型に分け、試験で差し替える。`ValidatedRelease` と `StagedUpdate` は検証処理だけが作る値。実行役は受け取った情報をディスク上で再検証する。

```csharp
public enum UpdateMode { Ask, Notify, Automatic, Off }
public enum GateStatus { Entered, Busy, RecoveryRequired, Unavailable }

public sealed record GateEntry(GateStatus Status, IDisposable? Lease);

public static class InstallGate
{
    public static GateEntry EnterStartup(string executablePath);
}

public interface IUpdateService
{
    Task<UpdateCheck> CheckAsync(UpdateMode mode, bool manual, CancellationToken ct);
    Task<StagedUpdate> StageAsync(ValidatedRelease release, CancellationToken ct);
    Task<ApplyHandoff> PrepareApplyAsync(StagedUpdate update, CancellationToken ct);
}

public interface IRecordingSamples
{
    IDisposable AcquireRecordingLease();
    event Action<RecordingSample> RecordingSampled;
}

public interface ISensorCadence
{
    IDisposable AcquireOneSecondLease();
}

public interface IRunRecorder
{
    Task<RunHandle> StartAsync(CancellationToken ct);
    Task<RunMarker> MarkAsync(RunHandle run, MarkerKind kind, CancellationToken ct);
    Task<RunResult> StopAsync(RunHandle run, RunStopReason reason);
}

public interface IRunStore
{
    Task<RunList> ListAsync(CancellationToken ct);
    Task<RunDetail?> ReadAsync(Guid runId, CancellationToken ct);
    Task<RunDeleteResult> DeleteAsync(Guid runId, CancellationToken ct);
}
```

- `SamplerLoop` が `IRecordingSamples`、`SensorHostClient` が `ISensorCadence` を実装する。Checks は一つ目だけを使う。どちらの lease も参照数で管理し、二重 `Dispose` は一度分だけ返す。ハンドラーは不変 DTO をキューに渡す。ディスク I/O と要約は writer 側で実行する。
- 記録は GUI プロセスにつき一件。開始失敗、停止、容量不足、writer 異常、ウィンドウ終了は同じ終了処理へ入り、購読と lease を `finally` で返す。停止要求は同じ完了結果を待てる。更新の待ち判定には記録の準備中と停止処理中も含める。
- 更新の書き換えを行うのは `PcMieru.Swap` だけ。GUI の引き渡しには起動した実行役への匿名パイプと親プロセスハンドルを使う。LocalAppData に残ったファイルだけで無人交換を再開する入口は作らない。開始済み journal の回復は別のローカル処理で、現在の通信モードにかかわらず成立させる。

## 3. 配布形式と検証

### 3.1 入手先と世代

**設計。** アプリは `https://api.github.com/repos/goemon0000/pc-mieru/releases/latest` を問い合わせる。安定版の正規タグ `vX.Y.Z` だけを数値比較し、実行版と配置版の両方より新しい版を候補にする。Release の `draft`・`prerelease`、配置の失敗版一覧にあるタグ、別 repo・別タグの資産を除く。必要な asset は `PCMieru-X.Y.Z-win-x64.zip`、`PCMieru-X.Y.Z-win-x64.manifest.json`、`SHA256.txt` 各一つ。`SHA256.txt` に ZIP と manifest の両方の一意なハッシュを載せる。GitHub が asset digest を返した場合はそれとも照合する。

資産の最初の URL は `https://github.com/goemon0000/pc-mieru/releases/download/vX.Y.Z/<資産名>` と厳密一致。転送は同一資産 URL または `release-assets.githubusercontent.com`・`objects.githubusercontent.com` の HTTPS 443 に限り、各ホップを検証して最大 5 回。API の転送も同じ API URL に限定する。資格情報、Cookie、HTTP への移行は拒否する。README には「SHA256 は同じ Release にある。公開元を信頼し、取得の破損を見つける方式」と明記する。旧版の転送先、ホップ数、資産 digest 照合の根拠は `PL/src/PartsLife.Update/UpdateEndpoints.cs:46`、`:90`、`:108`、`PL/src/PartsLife.Update/UpdateHttp.cs:22`、`:28`、`:41`、`:112`、`PL/src/PartsLife.Update/Updater.cs:195`、`:202`。

外部 manifest `pcm-bundle/1` は `{format, version, rid, buildId, gateProtocol, files}`。`rid` は `win-x64`、`gateProtocol` は整数 `1`、`files` は `{path, bytes, sha256}` の配列。配布フォルダ内に同じ形式の `PCMieru.bundle.json` を入れる。内側の一覧は自分自身を除く。外側には内側 manifest 自身のハッシュも載せ、残りの項目が内側と一致することを要求する。初回の手動展開では内側の一覧と実ファイルを照らし、初回の交換時に導入記録を作る。旧版のファイルを重ねた配置で所有権の衝突があれば自動交換を拒否する。

`buildId` は manifest だけに持つ（発行ごとの一意値）。世代の一致は全ファイルのハッシュと導入記録の照合で確かめ、exe・DLL への埋め込みはしない（ハッシュの一致の方が強く、発行の手間も増えない）。通常の起動ではハッシュを取らない（起動を軽く保つ。世代が混ざり得るのは交換の途中だけで、それは journal が見る）。署名対応後は、全 exe・DLL の有効な Authenticode と、クライアントに埋め込んだ Subject・Issuer の組を要求する。署名を移行する版の出荷順は §12。v0.5 では署名が付いていて検証に失敗する候補も拒否する。署名の仕様は `docs/unify/SPEC-unify.md:165`。

### 3.2 ZIP の名前と中身

**確認済み。** `publish.ps1` はフォルダ名を `PCMieru-<版>-<RID>` とし、結合時に相対パスを保ち `.pdb` を除外する。五つの製品 exe と Core の DLL（実名は `PcMieru.Core.dll`。AssemblyName を指定していないため。ZIP の現物も同じ綴り）を検査し、`LICENSE` と `NOTICE.txt` を加え、最上位フォルダごと ZIP にする。**ZIP 化は `[IO.Compression.ZipFile]::CreateFromDirectory`（`tools/publish.ps1:378`）で、Windows PowerShell 5.1（.NET Framework）で走らせると項目名の区切りが `\` になる。手元の v0.2.0 の ZIP は実際に `PCMieru-0.2.0-win-x64\pcmi.exe` の形（Opus が `unzip -l` で確認）。CI は pwsh（`.github/workflows/release.yml:46`）なので `/` になる**。現物 `C:\Users\Administrator\dev\pc-mieru\dist\PCMieru-0.2.0-win-x64.zip` は隣接 SHA と一致し、236 ファイルだった。内訳は DLL 222、exe 6、JSON 6、`NOTICE.txt` と `LICENSE`。製品 exe に加えて `createdump.exe` があり、サブディレクトリのファイルは `ja/` の `.resources.dll` 4 件だった。JSON 6 件は `PCMieru`、`pcmi`、`PCMieru.SensorHost` の `.deps.json` と `.runtimeconfig.json`。発行時のサテライト言語指定は `en;ja`。`tools/publish.ps1:228`、`:314`、`:327`、`:347`、`:350`、`:355`、`:376`、`Directory.Build.props:12`、`C:\Users\Administrator\dev\pc-mieru\dist\SHA256.txt:1`。

**設計。** クライアントに次の名前規則を埋め込む。`<stem>` は ASCII の英数字で始まり、残りは英数字・`.`・`_`・`-`。大文字小文字の衝突判定は Windows に合わせ `OrdinalIgnoreCase`。ZIP と manifest の対応では正規名の綴りまで一致させる。

| 相対名 | 許可する名前 |
|---|---|
| ルートの exe | `pcmi.exe`、`PCMieru.exe`、`PCMieru.<stem>.exe`、`createdump.exe`。`PCMieru.Party.exe` は v0.5 のこの規則で通る。 |
| ルートの DLL | `<stem>.dll`。SDK・ランタイムの具体的な一式は、その版の manifest との完全一致で確定する。 |
| ルートの付随ファイル | `<stem>.json`、`<stem>.txt`、`<stem>.md`、`<stem>.pdb`、`LICENSE`。 |
| サテライト | `ja/<stem>.resources.dll`、`en/<stem>.resources.dll`。 |
| v0.5 の必須床 | `PCMieru.exe`、`pcmi.exe`、`PCMieru.SensorHost.exe`、`PCMieru.Helper.exe`、`PCMieru.NetMeter.exe`、`PcMieru.Core.dll`、`PCMieru.Swap.exe`、`PCMieru.bundle.json`、`PCMieru.gate.txt`。段 A の `PcMieru.Checks.dll` と、新設の `PcMieru.Update.dll` も v0.5 発行の必須に加える。必須床の照合は大小文字を区別しない（`OrdinalIgnoreCase`）。既知の三つの managed exe に対応する上記 JSON 6 件も発行必須とする。 |

1. ZIP 中央ディレクトリを先に列挙する。全項目が `PCMieru-X.Y.Z-win-x64/` の内側にあること。ルートの明示的ディレクトリエントリは省略可。明示ディレクトリはルート・`ja/`・`en/` のみで、空のものは拒否する。
2. ZIP 区切りは `/`。`\`、先頭 `/`、ドライブ名、`:`、空要素、`.`・`..`、制御文字、末尾の点や空白、拡張子付きも含む DOS デバイス名を拒否する。シンボリックリンク、reparse point、通常ファイル・ディレクトリ以外の項目、暗号化項目、重複名、ファイルとディレクトリの衝突を拒否する。解凍先も実ディレクトリの配下と確認し、既存のリンクを辿らない。
3. 必須床を確認し、続いて **ZIP のファイル集合＝外部 manifest のファイル集合** を照合する。未知の JSON キーを将来用に読み飛ばせても、未知の形式・RID・門プロトコル、重複 JSON キー、重複パス、欠けたハッシュ、余分なファイルは拒否する。`update/`、`gui.ini`、ユーザーデータは配布対象外。`PCMieru.gate.txt` はクライアント固定の内容とハッシュを持つ特別な配布ファイル。
4. 新規ステージに `CreateNew` で展開し、各ファイルの長さと SHA-256 をストリームで照合する。ヘッダー・実バイト数・manifest の長さがすべて一致してから世代を確認する。v0.5 の提案値は ZIP 256 MiB、展開合計 768 MiB、ファイル 4,096 件、manifest 2 MiB。上限はダウンロード・展開の実バイトにも掛ける。発行側も同じ検証器を実行する。
5. 発行は結合・AOT 検査・同梱物の追加・必要な署名を済ませてから、実ファイルを列挙して manifest を作る。ZIP は `CreateFromDirectory` に任せず、ファイルを 1 つずつ `/` 区切りの項目名で `CreateEntry` する（どちらの PowerShell で発行しても同じ ZIP になる）。CI はその ZIP を実際に v0.5 クライアントの検証器へ渡す。現行の Release workflow が添付している ZIP と `SHA256.txt` に、外部 manifest を追加する。`tools/publish.ps1:347`、`:364`、`:369`、`:376`、`.github/workflows/release.yml:47`、`:53`、`:62`。

## 4. 保存場所と起動の門

`I` は実行ファイルの配置フォルダ、`L` は `%LOCALAPPDATA%\PCMieru`。`<id>` は門のボリューム ID とファイル ID をキーにした配置識別子。別名で起動しても同じ実ファイルを開く。`<tx>` はトランザクション UUID。以下はすべて **設計**。

| パス | 所有者と用途 |
|---|---|
| `I\PCMieru.gate.txt` | 手動展開の時点で存在する門。交換・巻き戻しの対象から外す。 |
| `I\PCMieru.bundle.json`、`I\<配布相対名>` | 配布 payload。内側 manifest は初回導入記録の入力。 |
| `I\update\installed.json` | 現世代の版・buildId・相対名・長さ・ハッシュ。削除の根拠。 |
| `I\update\journal.json`、`I\update\journal.json.tmp` | 交換・回復の永続状態。相対名、旧新ハッシュ、手順、試行回数を持つ。 |
| `I\update\prev-<旧版>\<tx>\files\…`、同じ場所の `installed.json` | 旧版の管理対象一式と旧導入記録。 |
| `I\update\work\<tx>\{next,displaced,restore}\…` | 同一ボリューム上の新ファイル、退避ファイル、復元用の書き込み途中。 |
| `I\update\recover.exe`、`I\update\RECOVER.txt` | 旧世代の検証済み NativeAOT 実行役の控えと回復手順。最初の変更より先に用意する。 |
| `I\update\blocked.json`、`I\update\last-result.json` | 配置で共有する失敗タグと最後の結果。 |
| `L\gui.ini`、`L\gui.ini.tmp`、`L\gui.ini.lock` | 利用者の設定と原子的保存用の一時ファイル・直列化ファイル。 |
| `L\update\<id>\state.json`、`L\update\<id>\check.lock` | 確認日時、回数制限、候補の版とキャッシュ状態。利用者内の確認を直列化する。 |
| `L\update\<id>\cache\<tx>\{bundle.zip.part,bundle.zip,SHA256.txt,release.manifest.json,files\…}` | ダウンロードと検証済みステージ。 |

`I\update` 配下と `L` の JSON 更新は同じディレクトリの `.tmp` へ書き、flush してから置換する。回復判断は `I` の記録だけで可能にする。門は既存ファイルを `FileMode.Open`、`FileAccess.Read` で開く。通常プロセスは `FileShare.Read`、交換役は `FileShare.None`。削除共有を付けず、ハンドルをプロセスの作業終了まで保持する。実 exe のパスを起点にし、カレントディレクトリや `%LOCALAPPDATA%` から門を探さない。配置フォルダへの別名は同じ実体へ解決し、門そのものや `update` 配下の reparse point は拒否する。門を共有で取ってから journal を読む。journal が存在する、状態を判定できない、あるいは一時 journal が単独で残った場合は回復入口へ回す。journal の確認に使う I/O はアクセス拒否と不在を区別する。

| exe | 門と journal を調べる位置 | 取れない・回復待ち・回復失敗 |
|---|---|---|
| `PCMieru.exe` | `Main` のサービス、画面、採取、更新タイマーより前。現行の組み立ては `src/PcMieru.Gui/Program.cs:6`、`:30`、`:48`。 | 通常画面の代わりに理由を表示。交換中なら再試行、回復可能なら共有を解放して回復役を起動し、自身は終了。失敗時は `I\update\RECOVER.txt` と控えの位置を示す。 |
| `pcmi.exe` | CLI と MCP の共通入口で、コマンドと stdio RPC の前。`src/PcMieru.Cli/Program.cs:49`。 | stderr のみ。交換中・回復未完了・壊れた記録は `Failure`、アクセス拒否は `Denied`。MCP の stdout に応答を書かない。 |
| `PCMieru.SensorHost.exe` | stdin 監視と vendor ライブラリ読み込みの前。`src/PcMieru.SensorHost/Program.cs:26`、`:51`。 | stderr の固定理由と非ゼロ終了。フレームなし。親は再起動の待ち時間を使い、記録は欠測を残す。 |
| `PCMieru.Helper.exe` | System32 の検索設定後、要求の処理より前。`src/PcMieru.Helper/Program.cs:20`、`:34`、`:49`。 | `OperationCode.Denied` または `Error`。要求実行、結果ファイル作成、回復の起動をせず終了。親は既存の失敗経路で表示する。 |
| `PCMieru.NetMeter.exe` | 同じく検索設定後、引数処理・パイプ・ETW より前。`src/PcMieru.NetMeter/Program.cs:17`、`:28`、`:35`。 | `MeterExit.Internal`。パイプも ETW も開かず終了。門が正常な引数なし起動は `InvalidArguments=2` を保つ。`src/PcMieru.NetMeter/MeterHost.cs:7`、`tools/publish.ps1:290`。 |
| `PCMieru.Party.exe` | 段 C の `Main` で WPF、IPC、常駐処理の前。 | 交換中・回復待ち・回復失敗を表示して終了。段 B の試験用 Party でも同じ Wire を使う。 |

**確認済み。** Helper は `runas` を使用し、NetMeter の昇格は起動側が選ぶ。したがって、別資格情報で起動しても配置の門を読めることを試験する。`src/PcMieru.Core/Management/HelperClient.cs:40`、`:59`、`:67`、`src/PcMieru.Core/Collection/NetMeterTransport.cs:117`、`:128`、`:136`。managed exe の OS ローダーは `Main` より先に動く。交換中の同時起動がイメージを掴んだ場合は共有違反として交換側が扱い、journal に従って復元する。この競合を実機試験に入れる。

## 5. 更新の状態機械

| 状態 | 処理と遷移 |
|---|---|
| `Check` | 確定したモード、期日、回数制限を見てから Release を取得。新しい候補がなければ待機。「知らせるだけ」または書き込み不可なら候補の版と Release ページを表示して終了。「自動」で適用可能なら `Download`。 |
| `Download` | `L\update\<id>\cache\<tx>` に SHA、manifest と ZIP を保存。ZIP は `.part` に受ける。中断時は未完のステージを適用不可とする。 |
| `Verify` | §3 の URL、SHA、名前、集合、ハッシュ、版、世代、署名を検査。成功で `Stage`、失敗で理由を表示し、そのステージを除去。 |
| `Stage` | `L` の検証済み候補を記録。点検・記録・画面操作の終了を待つ。GUI が操作待ちに入ったら、まず門を持っている他のプロセスを Restart Manager（`RmGetList`、門のファイルを登録）で調べ、自分と自分の子以外がいれば閉じずに延期する。いなければ検証済みの今の `PCMieru.Swap.exe` を `I\update\recover.exe` に写してハッシュを照合し、**その写しを実行役として起動し**（`I\PCMieru.Swap.exe` 自身は実行しない。走っている exe は上書きできないため）、設定を保存し、所有する採取・補助プロセスを通常の終了経路で閉じる。実行役が引き渡しを受けてから GUI が終了する。 |
| `GateAcquire` | 実行役は親の終了を確認して `I` の門を専有で取る。他の CLI・MCP・補助 exe・Party が門を持つ間は延期する（MCP として動き続ける `pcmi` は AI アプリが閉じるまで門を持つ。終了させない。設定ページと通知に「AI アプリが pcmi を使っているため待っています」と出す）。強制終了の対象は自分が起動した検証プロセスに限定する。GUI を閉じた後に延期になったら、journal を作らず旧 GUI を再起動する。専有後に配置版、mode に対応する今回の引き渡し、失敗版、キャッシュを再確認する。 |
| `Prepared` | `I` 側へ同一ボリュームの `next` を作り、書き直したバイトをハッシュ確認。旧管理対象と旧導入記録を `prev` に全コピーし、flush・照合する。旧ファイルのハッシュが変わっている、新規名に所有者不明の実ファイルがある、控えが不完全、回復役を置けない場合はここで中止。計画と preimage が揃ってから journal を確定する。 |
| `Swapping` | 各操作の意図を journal に書いてから rename／置換する。旧版にだけあったファイルは旧導入記録と実ハッシュが一致するものだけ `displaced` に移動する。新規名、上書き、除去の完了位置を記録する。門ファイルは一貫して同じ実体を使う。すべて照合できたら新 `installed.json` を書き `VerifyPending`。途中停止や共有違反からは `Rollback`。 |
| `VerifyPending` | 専有のまま新一式を再照合。対象は、すべての導入済みファイルのハッシュ（これが世代の一致の確かめ。§3.1）。混在を見つけたら即 `Rollback`。通過すれば試行を journal に先に加算し、検証専用 GUI を起動。描画完了後、UI スレッドの応答と生存を単調時計で 15 秒確認する。試行全体の期限は 120 秒。失敗は最大 3 試行。 |
| `CommitIntent` → `Cleanup` | GUI が検証完了を通知し終了したら、確定の意図を記録。新世代を再照合して結果を確定し、journal を解消して門を返す。旧控え、`work`、ダウンロードを片付け、通常 GUI を起動する。 |
| `Rollback` | 起動試行が尽きた、または世代不一致なら対象タグを `blocked.json` に永続化する。交換 I/O だけの失敗ではタグを登録しない。旧ファイルは控えから復元し、新版で足したファイルはハッシュ一致時だけ除去する。旧版から除去したファイルと旧 `installed.json` も戻す。旧一式を再照合してから journal を解消し、旧 GUI を起動する。 |
| `RecoveryFailed` | 巻き戻しの書き込みに失敗したら journal と控えを保持する。自動再試行は 3 回で止め、次は GUI の「回復を再試行」または `I\update\recover.exe --recover` から実行する。正常な一式を照合できるまで全 exe の通常処理を止める。 |

**設計上の実行条件。**

- 検証用 GUI は通常起動と別の入口を持つ。実行役が限定継承させた専有門ハンドルと匿名パイプ、journal のトランザクション ID が揃った場合のみ入る。コマンドラインのフラグだけでは起動の門を通過できない。検証中は設定保存、更新通信、記録、UAC、通常の補助プロセス起動を抑止する。実行役が終了したら検証子も終了させ、次の回復試行に進める。旧方式の 15 秒・120 秒と試行上限の元は `PL/src/PartsLife.Update/SwapBoot.cs:36`、`:42`、`:45`、`:48`。
- journal には `from`・`to`、両世代の導入記録、phase、相対名ごとの旧新ハッシュと操作意図、起動試行、回復試行を保存する。状態ファイルと控えは `Flush(true)`、置換は同一ボリュームの書き込み完了を待つ Windows API で行う。各操作の直前・直後で落ちても、回復はカウンターだけに頼らず、旧・新・退避先の実ハッシュを調べて再開する。
- `Prepared` または `Swapping` で中断した場合は旧一式へ戻す。`VerifyPending` なら整合した候補だけ検証を再開し、消費済み試行は数える。`CommitIntent` なら新一式を照合して確定を完了する。JSON が壊れている、未知の状態、どちらのハッシュでもないファイルが見つかった場合は控えを保全して `RecoveryFailed`。有効な一式を確定した後の不要ファイル削除は再試行できる。削除の失敗で有効な配置を巻き戻さない。
- 書けるかは、通常トークンで `I` と `I\update` の新規ファイル作成・flush・rename・削除を確認し、既存の管理対象への変更権限も交換時に確認する。成功を記憶して権限確認を省略しない。書けない配置では「この配置には書き込めないため、更新を通知します」と表示し、ZIP の自動取得と交換をしない。Updater は `runas`、ACL 変更、昇格した Helper・NetMeter の流用を行わない。旧版にも書き込み不可時の通知経路がある。`PL/src/PartsLife/Updates/UpdateRuntime.cs:102`、`PL/src/PartsLife.Update/Updater.cs:160`。PCミエルの規範は `docs/unify/SPEC-unify.md:162`。
- GUI が昇格して動いている（利用者が管理者として実行した）ときは無人交換をせず、通知だけにして次の通常起動に回す（昇格したプロセスから通常の権限の子を確実に作れないため）。交換を有効にするのはローカルの固定ドライブ（`DriveType.Fixed`）の配置だけ。ネットワーク・取り外せる媒体は通知だけ。
- 自動交換は、ダイアログ・メニュー・ドラッグ・点検・記録がなく、最後のユーザー入力から 60 秒経過したときに試す。終了手順へ入る直前に UI スレッドで再判定する。他プロセスが動作中なら次の機会に延期する。60 秒は設計値。旧版の操作待ち判定は `PL/src/PartsLife/MainWindow.xaml.cs:286`、`PL/src/PartsLife/Updates/UpdateController.cs:194`。

## 6. 採取の寿命と 1 秒の要求

**確認済み。** `SamplerLoop` は `Sampled` を発行し、停止中は採取を待ち、復帰時に基準を落とす。最小化時の周期と GPU 購読の参照数は `SamplerSettings` にある。GUI は `SnapshotPump` で表示へつなぐ。SensorHost は既定 2 秒、`--interval-ms` を受け取り、UTC 付きの `SensorFrame` を stdout に出す。Core は子の応答を監視し、GPU キャッシュは 2 秒ごとに非同期で値を得て、表示への返却にはフレームの取得時刻を含めない。`src/PcMieru.Core/Collection/SamplerLoop.cs:104`、`:189`、`:220`、`:270`、`src/PcMieru.Core/Collection/SamplerSettings.cs:46`、`:77`、`:88`、`src/PcMieru.Gui/Data/SnapshotPump.cs:53`、`:116`、`:150`、`:222`、`src/PcMieru.SensorHost/Program.cs:10`、`:16`、`:66`、`src/PcMieru.Core/Hardware/Model/SensorModel.cs:17`、`src/PcMieru.Core/Hardware/Sensors/SensorHostClient.cs:173`、`:333`、`src/PcMieru.Core/Collection/GpuSensorCache.cs:14`、`:28`、`:70`。

**設計。**

1. 最初の記録 lease で Core の実効周期を 1 秒にし、採取の pause を上書きして、GPU 購読と SensorHost の 1 秒 lease を一つ取得する。最小化と pause は表示側では有効。GUI の一時停止中の単発更新は、記録中には進行中ループの結果を使う。最後の返却時に、その時点の利用者設定へ戻す。変更待ちにはループを起こすシグナルを使う。
2. SensorHost は既存の親 stdin を 2 値の周期制御に拡張する。親は起動引数にも現在の要求周期を渡す。子は `1000` または `2000` ms を次の採取境界で適用し、EOF で終了する。ネイティブ採取の応答を UI や記録 writer で待たない。再起動時も lease が残っていれば 1 秒を要求する。新しい `SensorFrame` にプロセス世代 ID、連番、実際の名目周期、単調取得時刻を追加し、親子の schema を同時に上げる。
3. 各値の由来を Core で保持する。CPU、メモリ、PDH GPU 使用率は各採取段の値と時刻。Vendor GPU 値は SensorHost のフレーム時刻と ID。GPU マージ後も出自を項目ごとに持たせる。`GpuSensorCache` は表示の既存鮮度と、記録に渡すフレーム ID・周期を別に持つ。1 秒要求への切替前や子の再起動中は、実際に報告された 2 秒周期のフレームも扱う。`src/PcMieru.Core/Collection/WindowsSnapshotSource.cs:201`、`:232`、`:370`、`:404` が接続箇所。
4. CPU パッケージ電力は既存 PDH 問い合わせに `\Energy Meter(*)\Power` を加え、`*_PKG` の mW を W にする。GPU 制限は NVML の clocks event reasons を `ulong` で保持し、未対応なら欠測。AC 接続と電池残量・状態は既存 Windows 読み取りを共用する。GUI の CPU・GPU・バッテリー表示にも同じ値を出す。温度や電力から未知の制限理由を推定しない。旧換算・既存の採取位置は `REC/src/PartsLife.Checks/Recording/Sources.System.cs:12`、`:45`、`:190`、`src/PcMieru.Core/Collection/PdhSystemSampler.cs:40`、`src/PcMieru.SensorHost/Nvml.cs:74`、`src/PcMieru.Core/Hardware/Collectors/BatteryCollector.cs:18`、`:57`。

## 7. `pcm-run/1` と要約

### 7.1 セッションと CSV

**設計。** `Environment.SpecialFolder.MyDocuments` の `PCMieru\runs\` を使う。保存先は `run-<UUID>.json` と `sensors-<UUID>.csv` の組。`run-<UUID>.lock` は稼働中の保護に使う。Metadata の一時書き込みは `run-<UUID>.json.tmp`。旧記録も Documents を既定とし、JSON と CSV の組で保存する。`REC/src/PartsLife.Checks/Recording/RunStore.cs:15`、`:18`、`:19`。

| フィールド | 型と規則 |
|---|---|
| `format`, `app`, `runId` | `"pcm-run/1"`、製品名と版、UUID。 |
| `startedAtUtc`, `endedAtUtc`, `durationUs` | UTC ISO 8601、実行中は終了欄 `null`。`durationUs` は単調時計の整数マイクロ秒。 |
| `status`, `stopReason`, `droppedTicks` | `recording\|completed\|interrupted\|failed`。理由は `user\|window_close\|low_space\|io_error\|process_exit` など固定値。バッファ超過は件数と時間の穴を残す。 |
| `csv`, `metrics`, `markers` | CSV の相対ベース名、項目辞書 `{key,unit,source,kind}`、印 `{kind,offsetUs,utc}`。印コードは `bench-start`、`bench-end`、`stress-start`、`stress-end`、`mark`。デバイス番号は実行内で固定し、列挙順の変更で付け替えない。 |

CSV は UTF-8（BOM なし）、ヘッダーあり、カンマ区切り、数値は invariant culture。**1 秒の採取 1 回につき 1 行の横長**（旧 CSV と同じ向き。表計算ソフトで開いてそのまま読める・項目ごとに 1 行の縦長より小さい）。取得時刻と再掲の印は**値の出どころ（ソース）ごと**に持つ。同じソースの値は同じ 1 回の取得から来るので、項目ごとに持つ必要がない。

- ソース: `core`（CPU・メモリ・PDH の GPU 使用率・CPU パッケージ電力・AC／電池。SamplerLoop の 1 回）、`gpu<N>`（SensorHost のフレーム。N は実行内で固定の GPU 番号）。
- 列: 先頭に `offsetUs`（記録開始からの単調時計）・`utc`。続けてソースごとに `<src>.frame`（世代と連番、`g1-42` の形）・`<src>.acquiredOffsetUs`・`<src>.periodMs`・`<src>.state`（`fresh|repeat|missing`）・`<src>.reason`（`missing` のときだけ。`host_restart|read_failed|not_started` など固定値）。その後にそのソースの値の列 `<src>.<metric>`（`metrics` 辞書のキーと同じ）。
- 列はその回の開始時に固定する。途中で現れた GPU は列を足さず、`metrics` にも載せない（印に `warning` を残す）。消えた GPU は `missing`。
- `repeat` の行は値を書く（表で読みやすくするため）が、要約は新しい値として数えない。`missing` の行は値・フレーム・取得時刻を空欄にする。整数のビットマスクは十進の文字列で往復し、浮動小数に通さない。`NaN` と無限値は欠測扱い。

```csv
offsetUs,utc,core.frame,core.acquiredOffsetUs,core.periodMs,core.state,core.reason,core.cpu_usage_pct,core.cpu_package_w,gpu1.frame,gpu1.acquiredOffsetUs,gpu1.periodMs,gpu1.state,gpu1.reason,gpu1.clock_mhz,gpu1.limit_reasons
0,2026-11-20T00:00:00.000Z,c-1,0,1000,fresh,,52.5,88.1,g1-42,0,2000,fresh,,2415,4
1000000,2026-11-20T00:00:01.000Z,c-2,1000000,1000,fresh,,61.0,90.4,g1-42,0,2000,repeat,,2415,4
2000000,2026-11-20T00:00:02.000Z,c-3,2000000,1000,fresh,,60.2,90.0,g1-43,2000000,2000,fresh,,2400,4
3000000,2026-11-20T00:00:03.000Z,c-4,3000000,1000,fresh,,59.8,89.7,,,2000,missing,host_restart,,
```

- writer はサンプルを順に書き、10 秒ごとと停止時に `StreamWriter.Flush()`、続けて `FileStream.Flush(true)`。印は writer に順序付きで渡し、セッション JSON へ原子的に保存する。開始時と flush ごとに保存先の空きを見て、200 MB を切ったら停止手順へ進む。I/O に失敗しても最後に読める JSON と CSV を残す。再起動時は lock が取れる `recording` を中断扱いとし、完全な最終 CSV 行まで読めるようにする。
- 旧 `partslife-run/1` は旧 JSON・CSV の組を読み取り専用で開く。旧 CSV の `t_sec` 相当の行時刻を取得時刻の代用とし、名目周期は旧セッションの値、鮮度は「旧形式・推定」と表示する。元ファイルの書き換えや自動移行は行わない。旧形式識別子と読込は `REC/src/PartsLife.Checks/Recording/Session.cs:85`、`:234`、旧 CSV の読み書きは `REC/src/PartsLife.Checks/Recording/Series.cs:53`、`:68`。

### 7.2 要約

**設計。** 集計の最小単位は項目ごとの取得区間。`fresh` の `[取得時刻, 次の fresh の取得時刻)` を記録範囲で切る。次との間隔が、その値の名目周期 `P` の 3 倍を**超えた**ときは区間全体を欠測とする。明示的な取得失敗・子の終了は既知区間をその観測時刻で打ち切る。先頭は最初の取得まで欠測。正常な停止は終端として扱い、最後の値に最大 `P` 秒まで与え、残りを欠測にする。異常終了で停止時刻が不明なら、最後の耐久化済み観測時刻で閉じる。`repeat` 自体から秒数を増やさない。各項目に `observedSeconds`・`missingSeconds`・新規取得点数を出す。複数項目に同じ欠測が出ても、項目の欠測秒を足して実行全体の欠測秒としない。

- 最小・最大は区間のある新規値、平均は観測秒で重み付けする。負荷ありは直前 5 秒の観測区間の平均が 50% 以上。持続値は 60 秒以上の記録で、最後の負荷あり区間を終点に `clamp(記録時間 / 3, 30秒, 300秒)` を取り、観測できた負荷区間が計 10 秒以上なら時間重み付き中央値を出す。末尾のアイドルは窓をずらさない。旧実装の閾値と平滑化は `REC/src/PartsLife.Checks/Recording/Summarizer.cs:33`、`:70`、`:197`。旧コードの点数条件は、段 B では秒へ読み替える。
- 温度とクロックの同時変化は、両者が観測できる連続区間で、5 秒の平滑値を比較する。30 秒先の最初の取得点で、開始クロック 300 MHz 以上、温度 +3℃ 以上、クロック −5% 以上を満たし、負荷が測れる場合は区間平均 50% 以上を条件とする。欠測をまたいだ窓は除く。重なる窓を結合し、温度上昇の大きい順に最大 6 件。表示語は「同時に起きた」。旧条件は `REC/src/PartsLife.Checks/Recording/Summarizer.cs:33`、`:283`、`:308`。
- 制限理由ごとに、ビットが立った既知区間の秒数と、そのうち負荷あり区間との共通部分を数える。複数の理由が同時に立つことがある。分母は制限理由の観測秒、負荷あり内訳は負荷も観測できた共通部分。旧コードの固定周期加算は `REC/src/PartsLife.Checks/Recording/Summarizer.cs:226`。
- `bench` と `stress` は開始と終了を種類別に対応させる。開始が重なれば前をそこで閉じて `open` を付ける。余った終了は警告、余った開始は記録終端で閉じて `open`。区間内の要約は取得区間を印の時刻で切って算出する。旧対応付けは `REC/src/PartsLife.Checks/Recording/Summarizer.cs:325`。
- 固定試料は `P=2秒`、取得 `0, 2, 4, 12, 14秒`、正常停止 `16秒`。中間の 1 秒刻みの再掲込みで、`[4,12)` の 8 秒が欠測、観測は計 8 秒。間隔ちょうど 6 秒は有効。時計を戻した試料でも UTC の文字列順で集計しない。

## 8. GUI

- **初回の更新設定。** 通常 GUI の初回起動で「自動」「知らせるだけ」「切る」を示し、「知らせるだけ」を選択済みにする。「自動」には未署名版も無人で入る旨を添える。「決定」で `L\gui.ini` に `update.mode=auto|notify|off` を永続化してから、確認タイマーを起動する。未設定・未知値・読めない設定・閉じる操作・保存失敗は `Ask`。smoke と検証専用起動では同意 UI も更新通信も行わない。
- **設定ページ。** 現在版、選択モード、最後の確認、書き込み可否、候補版、延期理由、前回の回復結果を表示。「今すぐ確認」と「更新内容を開く」を置く。「切る」と `Ask` では確認ボタンを無効にする。`Notify` は Release 情報まで取得し、ZIP を自動取得しない。`Off` への変更時は確認と未開始の引き渡しを取り消す。開始済み交換は journal の確定または復元まで完結させる。日次確認は旧方針に沿い、起動後 60 秒、前回から 24 時間、起動中は 1 時間ごとに期日を見る。403/429 は 24 時間以上で、応答指定の待ちを最大 7 日まで考慮する。手動確認もその待ちを守る。`PL/src/PartsLife.Update/UpdateSchedule.cs:17`、`:23`、`:35`、`:53`。
- **`gui.ini` の変更。** 更新の選択は `update.mode` 一つで表す。確認時刻と失敗タグは §4 のファイルに置く。既存の `GuiSettings` は `%LOCALAPPDATA%\PCMieru\gui.ini` を読み、未知キーを保持し、現在の保存は直接書き込みで I/O エラーを飲み込む。段 B ではロック内の read–modify–replace と、選択の保存成否を返す入口を足す。古い GUI が保持していた設定を後から保存する競合では、更新キーの最新値を保持する。`src/PcMieru.Gui/Shell/GuiSettings.cs:64`、`:104`、`:130`、`:177`、`:185`。
- **テストの記録。** 左ナビ「点検」のタブを「システム」「負荷」「記憶媒体」「テストの記録」「履歴」の順に置く。記録タブに「開始」「印」「停止」、経過、保存先、一覧、選択した回の要約と「CSV を開く」「削除」。印はベンチ開始／終了、負荷開始／終了、汎用。タブ移動・最小化中も記録し、ウィンドウを閉じるときは停止と保存を待つ。削除は選択 ID の管理対象の組だけを対象とし、記録中の回は無効。旧形式の閲覧は同じタブの「旧記録を開く」。配置と終了手順の根拠は `docs/unify/SPEC-unify.md:46`、`:53`、`docs/unify/DESIGN-A.md:251`、`:255`。

## 9. CLI と MCP

段 B の公開コマンドと MCP ツールは追加しない。段 A のコマンドに起動の門を適用する。更新への同意と記録の操作は GUI に置き、`PCMieru.Swap.exe --recover` はローカル回復入口とする。`pcmi` の終了コードは既存の `Success=0`、`Failure=1`、`BadArguments=2`、`NotFound=3`、`Denied=4`、`Protected=5`、`MultipleMatches=6`。門に関する結果は §4 の割り当てを使う。`src/PcMieru.Cli/ExitCode.cs:5`、`:19`、`docs/unify/DESIGN-A.md:259`。

## 10. 計測と出荷

段 A の `tools/measure.ps1` に、更新の確認待ち、期限に達した確認、記録中、停止後を加える。通常時は主の窓のみの既存条件で private bytes、CPU 比と CPU ms/秒、warm 起動の二時点を測る。GUI 単体と SensorHost 込みを出す。記録中は欠測・再掲率、writer の滞留、停止後の周期復帰も記録する。無操作時の既存目標は private 100 MB 未満、定常 CPU 平均 0.3% 未満、warm 起動 1 秒未満。Party 単体・併用の計測枠は段 C の実行物で埋める。ZIP は v0.2.0 の 51.6 MB と比較する。段 A のサイト取り込み確認も出荷条件に含める。`docs/SPEC.md:232`、`:243`、`docs/unify/SPEC-unify.md:199`、`:216`、`docs/unify/DESIGN-A.md:289`。

## 11. 試験と作業順

### 試験

| 種別 | 合格条件 |
|---|---|
| Feed・同意 | `Ask`、ダイアログを閉じる、設定保存失敗、`Off`、smoke は HTTP 0 回。`Notify`・書き込み不可は ZIP 0 回。別 repo／タグ／host、HTTP への転送、回数制限、draft、prerelease、同じ版、古い版、失敗版を検査。元試験は `PL/tests/PartsLife.Update.Tests/RulesTests.cs:70`、`:106`、`:215`、`:266`、`PL/tests/PartsLife.Update.Tests/UpdaterTests.cs:143`、`:193`、`:205`、`:401`、`:427`。 |
| ZIP・発行 | 現行の `createdump.exe`、日本語リソースと JSON 群を含む実発行が通る。区切りが `\` の ZIP（手元の v0.2.0 の ZIP そのもの）は通らない。Windows PowerShell 5.1 と pwsh の両方で発行した ZIP が同じ項目名になる。誤ルート、ルート外、`..`、ADS、大小文字重複、リンク、Windows 名の別表記、必須欠け、manifest 欠け・余分、SHA／長さ／世代不一致、上限超過が通らない。発行のファイル追加がクライアントの規則と食い違えば CI を落とす。旧試験は `PL/tests/PartsLife.Update.Tests/UpdaterTests.cs:268`、`:294`、`:317`、`:332`。 |
| 門・権限 | 六 exe の共有と交換役の専有を相互に試験。大小文字、8.3 名が有効な環境、ジャンクション経由、別資格情報で同じ門。専有中・journal 不明・回復失敗で GUI、MCP、採取、Helper 操作、ETW、Party が仕事を始めない。読めるが書けない手動展開の通常起動と通知を実機で確認。 |
| Journal・fault | 書き込み、各 rename、`installed.json` 更新、GUI 起動試行、`CommitIntent`、復元の直前直後でプロセスを止める。途中状態から管理対象が全件旧または全件新に戻る。邪魔なハンドル、容量不足、壊れた journal、所有者不明の衝突も試す。回復失敗時に控えが残ることをハッシュで確認。元試験は `PL/tests/PartsLife.Update.Tests/FaultTests.cs:92`、`:102`、`:112`、`:141`、`:174`、`PL/tests/PartsLife.Update.Tests/RecoveryTests.cs:116`、`:137`、`:151`。 |
| 一式の dry-run | `tools/update-dry-run.py` に偽 GitHub と一時フォルダのケースを移植。実際の v0.5 発行物から、別版・別 buildId の配布一式へ更新する。正常確定、3 回の起動失敗、旧世代の補助 exe、途中停止、控えロックを別ケースにする。管理外の利用者ファイルを置き、更新・復元の両方でバイト一致。偽 feed は試験ビルドのループバックに限定。元の実行環境と回復試験は `PL/tools/update-dry-run.py:113`、`:128`、`:187`、`:249`、`:280`、`:327`、`PL/src/PartsLife.Update/UpdateEndpoints.cs:53`。 |
| **v0.5 → Party 入り v0.6 相当 → v0.5** | **v0.5 バイナリをそのまま使用**。追加する `PCMieru.Party.exe` は門 v1 と新 buildId を持ち、manifest に追加する。正常ケースは全ファイルと導入記録の一致、Party が門を取って起動できるところまで確認。別ケースは交換直後の検証を失敗させ、Party が消え、v0.6 側で除いた旧ファイルが復活し、v0.5 一式と導入記録が元のハッシュに戻り、同タグは再適用されないことを確認。Party を ZIP から抜いた同 manifest の変種は適用前に拒否する。 |
| 記録・fixture | lease の重複、開始失敗、停止競合、最小化・pause、最後の返却、ホスト再起動、2 秒と 1 秒、時刻逆行、再掲、3P 境界、欠測、初回と末尾、制限ビットと負荷の交差、印の対、旧形式、末尾破損、容量不足・flush・削除競合。旧閾値の試料は `REC/tests/PartsLife.Checks.Tests/RecordSummaryTests.cs:20`、`:38`、`:52`、`:100`、`:139`、`:165`、`:217`。 |
| GUI・計測 | fake smoke に同意前・選択確定・通知のみ・回復失敗・記録中・停止待ち・欠測要約を追加。Fakes で実データ書き込みと更新通信がないこと。実機は通常権限・別資格情報での門、実 GPU の周期、CPU 電力・AC の欠測表示、§10 の負荷を確認する。既存の発行 smoke は `tools/publish.ps1:277`、`:290`、`:298`。 |

### 実装の順番

1. **配布契約。** `pcm-bundle/1`、名前判定、必須床、二重 manifest、`buildId`、旧更新 Unit の移植。実 ZIP を検証器に通すところまでを一件とする。
2. **通信と同意。** Feed、スケジュール、`gui.ini` の保存結果、設定ページ、通知まで。HTTP 0 回の試験を先に固定する。
3. **起動の門。** Wire と五つの現行 exe、試験用 Party に入れる。発行に門を同梱し、通常・共有・専有・読み取り専用配置の試験を通す。
4. **交換と回復。** 同一ボリューム staging、導入記録、journal と `recover.exe`。故障注入で旧一式の復元と管理外ファイルの保全を先に通す。
5. **GUI 引き渡しと検証。** idle、親の終了、15 秒確認、3 回、失敗タグをつなぐ。全体 dry-run と Party 入り fixture を更新側の完了判定にする。
6. **採取 lease と DTO。** Core の実効周期、SensorHost の周期切替、時刻付きフレーム、キャッシュとマージ、新規の電力・制限・電源状態。表示系の pause と既存の既定周期を回帰試験する。
7. **記録と要約。** CSV writer、低容量停止、旧リーダー、取得区間集計、印。境界試験とファイル故障注入を固定してから GUI に接続する。
8. **記録タブと出荷計測。** 終了時の停止、一覧、削除、CSV、基本要約。fake smoke と実機測定を行い、更新側と記録側を別々に合否判定する。

## 12. 仕様で決まっていない点

| 問い | 案 |
|---|---|
| 書けない配置でも最初から共通の門をどう提供するか | `I\PCMieru.gate.txt` を配布し、同じファイル実体を維持する。v0.4 以前から来る利用者は v0.5 を新しいフォルダへ手動展開する。 |
| 署名を始める最初の版はどう渡すか | 発行先と発行者の組を埋め込める時点で署名方針を有効にする。v0.5 にその組を確定できない場合、信頼の切替版は手動導入とし、そのクライアントから強制する。 |
| アプリ起動前に managed ローダーで失敗したときはどう戻すか | 実行役が生きていればローダー失敗を試行失敗として処理する。再起動後は `I\update\recover.exe` で復元できる。アプリと同じ DLL を要求しないことを発行試験で固定する。 |
| 非ローカル配置やファイルシステムごとの差をどう扱うか | 共有・排他、同一ボリューム置換、flush を実機で確認したローカル配置で交換を有効にする。未検証の配置では通知と手動導入の案内を出す。 |
| 長い空白と停止直前をどこまで観測扱いにするか | §7.2 のとおり 3P 超は区間全体を欠測、正常停止の尾は最大 P。固定試料を先に点検し、要約表示と CSV の再計算規約を揃える。 |
| 旧形式の取得時刻をどの精度で見せるか | 行時刻からの推定と明示する。取得時刻や再掲が記録されていなかった旧セッションに、後から正確な鮮度を割り当てない。 |

Opus の判断（2026-11-20）: 6 件とも案どおり。非ローカルの扱いは §5 のとおり固定ドライブだけ交換する。

## 13. Opus の点検で直した所（下書きから）

1. **ZIP の区切り**（§3.2・§11）。下書きは「v0.2.0 の ZIP が規則に合う」としていたが、手元の ZIP は項目名が `\` 区切りで、下書き自身の規則で拒否される。発行は `/` 区切りで項目を 1 つずつ作る。クライアントは `\` を拒否したまま。
2. **Core の DLL 名**（§3.2）。実名は `PcMieru.Core.dll`。必須床の照合は大小文字を区別しない。仕様 §7.1 の綴りも直した。
3. **実行役は写しから動かす**（§5）。下書きは配置の `PCMieru.Swap.exe` を起動する形で、自分自身の exe を入れ替えられない。交換の前に `I\update\recover.exe` へ写して照合し、写しを動かす（§4 の回復役の控えと同じファイル）。
4. **`buildId` を exe・DLL に埋め込まない**（§3.1・§5）。世代の一致は全ファイルのハッシュと導入記録で確かめる（埋め込みより強く、発行が軽い）。仕様 §7.2 の「版とビルドの印」もそう読み替えて直した。
5. **GUI を閉じる前に門の持ち主を調べる**（§5）。下書きは GUI を閉じてから延期に気づき、旧 GUI を起動し直す形で、MCP の `pcmi` が動いている間は毎回これを繰り返す。Restart Manager で門を持つプロセスを先に調べ、他がいれば閉じない。MCP の `pcmi` は終了させず、待っている理由を見せる。昇格した GUI と固定ドライブ以外の配置では無人交換をしない。
6. **CSV は横長**（§7.1）。下書きは項目ごとに 1 行の縦長で、表計算ソフトで読みにくく大きい。1 回の採取を 1 行にし、取得時刻と再掲の印はソースごとに持つ（同じソースの値は同じ取得から来る）。仕様 §5 の「値ごとに取った時刻」は、値がどのソースの何番のフレームから来たかで満たす。
7. **門の API は `GateOutcome`、印は有無だけ、`I` は最終パス**（§2・§4）。§2 の `GateStatus` は、実装では `GateOutcome`（Open、Busy、RecoveryPending、RecoveryFailed、MissingGate、Denied、ReparseRefused、Unreadable）に分けた。復旧が失敗した印は `I\update\recovery-failed` というファイルの有無だけで、中身は読まない（`last-result.json` は読まない）。見られないとき（アクセス拒否など）は RecoveryPending、リンクのときは ReparseRefused。`I` は exe の最終パス（GetFinalPathNameByHandle の結果）のフォルダーで、シンボリックリンク・ジャンクション・8.3 名・大小文字の別名で起動しても本物の設置の門に着く（ハードリンクの exe は別名を持たないので、開いた名前のフォルダー）。門ファイルは開いたハンドルで確かめ、再解析ポイントか名前が 2 つ以上（ハードリンク）なら ReparseRefused、ハンドルに尋ねられなければ Unreadable（アクセス拒否とは別）とする。
8. **引き渡しの試行を覚える・accepted の後は必ず起動する・L から下だけリンクを拒む**（§4・§5。手順 5a の Opus 点検）。下書きでは、実行役が中止（交換 I/O・ディスク不足・ファイル使用中など）して旧 GUI を起動し直すと、何も覚えていないため次の 5 秒の判定でまた引き渡し、無操作の間じゅう窓が閉じては開いた。GUI は引き渡しの**前に** `L` の更新状態へ試行（ビルド ID・manifest の SHA-256・試行 ID・時刻・回数）を書く。試行 ID は `--apply` の 7 番目の欄で実行役へ渡り、その回の transaction ID になるので、`last-result.json` がどの試行の結果か突き合わせられる。次の起動と各判定で、段にある版がまだ走る版より新しければ失敗とみて分ける。ディスク不足・交換 I/O・I/O の失敗と結果なし（落ちた・電源断）は一時的で、1 時間・6 時間あけて最大 3 回。それ以外（名前の衝突・導入済みファイルの変化・NTFS でない・記録なし・段の不一致・失敗版・未知）は永続で、段を捨てて設定ページに一度だけ知らせ、同じビルドは二度と段にしない。時計が戻ったら「まだ」とみる。実行役が受け付ける前に断った場合（段の変化・新しくない・自動でない・失敗版・段なし）は回数に数えず、段のビルドか確認の世代（確認ごとに新しい ID）が変わるまで引き渡さない。失敗版は候補から外す。段の検証は `L` の親までは別名（ジャンクション・8.3 名）を辿ってよく、`L` から下の各階層だけ再解析ポイントを拒み、開いたハンドルの最終パスが期待どおりかを確かめ、交換が済むまでそのハンドルを持つ（GUI の段作りと実行役が同じ規則）。accepted を送った後は、実行役のどの出口（GUI の終了待ちの期限切れ・門の拒否・回復失敗・例外）でも `I\PCMieru.exe` を 1 回起動する。回復待ちと回復失敗は起動した GUI の門の画面が扱う。ただし利用者がすでに別の通常 GUI を開いて門を持っていれば起動しない。GUI の終了待ちは 30 秒から 5 分に延ばした。引き渡しの手続きは UI スレッドの外で行い、accepted の直後に UI スレッドで閉じる。`blocked.json` は一時ファイルと置換で書き、書けなかったことを巻き戻しの結果に書く。
