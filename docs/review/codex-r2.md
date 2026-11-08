[SPEC.md](C:/Users/Administrator/dev/pc-mieru/docs/SPEC.md) は全文読みました。**構成は実装に進められますが、§2 の実行ファイル名、§5.4 の起動制御、§8 の Helper 認証は fan-out 前に修正必須です。** 以下は資料に基づく設計レビューで、実機での API 動作検証は含みません。

**1. API・データの具体的な修正**

- **§2：`PCMieru.exe` と `pcmieru.exe` は同じファイル名です。** 通常の Windows ファイルシステムでは同じ配布フォルダに置けません。GUI を `PCMieru.Gui.exe`、CLI を `pcmieru.exe` に変更。`AssemblyName`、DLL、deps/runtimeconfig の名前も明示してください。
- **§2：自己完結 publish の結合手順が未定義。** 各 exe を個別ディレクトリに publish してから集約し、共通ランタイム・依存 DLL の同名ファイルは内容一致を確認。不一致の黙った上書きを禁止。最初のコミットで ZIP から全 exe が起動するところまで固定します。

- **§4：`SYSTEM_PROCESS_INFORMATION` の採用自体は妥当。ただし公開 SDK の予約領域を使う依存です。** `CreateTime/UserTime/KernelTime`、I/O カウンターなどを含む完全な定義を、出典・commit SHA 付きで固定してください。「公開 Win32 API だけ」という説明にはしないこと。[Microsoft の注意書き](https://learn.microsoft.com/windows/win32/api/winternl/nf-winternl-ntquerysysteminformation)、[phnt の定義](https://github.com/winsiderss/phnt/blob/master/ntexapi.h)
- **§4・§12：構造体解析を具体化。** x64 の既存レイアウトでは `CreateTime=0x20`、PID=`0x50`、SessionId=`0x64`、Read/WriteTransferCount=`0xE8/0xF0`。オフセット検査に加え、`NextEntryOffset`・返却長・`UNICODE_STRING.Length/Buffer` の境界を検査。`ImageName` は長さ付きで、NUL 終端やフルパスを仮定しません。
- **§4・§5.2：メモリ列の定義が必要。** `WorkingSetPrivateSize`＝常駐 private、`WorkingSetSize`＝共有分を含む working set、`PrivatePageCount`＝private commit のバイト数。「PageCount」という名前でもページサイズを掛けません。`PagefileUsage` を「ディスク上のページファイル使用量」と表示しないこと。「プライベート」と「コミット」が同じ測定値なら重複列を整理します。
- **§4：CPU は計算式を書く。** プロセスは `100 × Δ(UserTime＋KernelTime) / 実測経過時間 / 全 active 論理 CPU 数`。経過時間は `Stopwatch`。全体 CPU の KernelTime には IdleTime が含まれるので差し引きます。初回・復帰直後・カウンター逆行は未計測扱い。
- **§4：「Processes タブと同じ」は OS 更新依存。** 旧表示の processor utility と CPU 時間率は異なります。一方、Windows 11 は 2025 年の更新で各ページを標準指標に統一し、以前の値を Details の「CPU Utility」列へ残しています。「Win11 は常に utility」も現在は不正確。計算式を固定し、比較時の OS build と列名を記録してください。[公式更新履歴](https://support.microsoft.com/en-au/servicing/os/windows-11/2025/03/march-27-2025-kb5053656-os-build-26100-3624-preview)
- **§4：processor group は「考慮する」だけでは不足。** 通常の問い合わせで全 group 分を取得できたと仮定せず、group ごとの問い合わせか PDH `Processor Information(group,index)` を実装・試験。論理 CPU と物理コアも区別します。
- **§4・§5.2：I/O の説明は良いものの、既定列名「ディスク I/O」と矛盾。** 「プロセス I/O 読取/書込 B/s」に変更。`OtherTransferCount` を含める場合は別記。Task Manager のプロセス別ディスク B/s の代替とは称さないでください。

- **§4：GPU は `engtype` ごとの単純合算では誤ります。** `(adapter LUID, phys, eng)` をエンジンの識別子とし、同じエンジンについて PID 分を集計→GPU 代表値は最大エンジン。別々の同種エンジンを合算すると 100% 超や過大表示になります。プロセス代表値も最大エンジンを使います。[Task Manager の集約方針](https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/)
- **§4：PDH のインスタンス解析を仕様化。** `pid_…_luid_…_phys_…_eng_…_engtype_…` をトークンとして解析し、engtype の種類・固定文字位置を仮定しない。未知形式は欠損。LUID を DXGI と照合し、PID 再利用をまたぐ差分を捨てます。
- **§4：`PdhAddEnglishCounterW` は正しいが、wildcard の展開・動的追加・消滅を別途処理。** wildcard 配列取得か公式の展開手順を選び、初回二点採取と各値の `CStatus` を確認します。[API の注意事項](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw)

- **§5.4：`Run32` は登録元のキー名ではありません。** 登録元は HKLM `Software\Microsoft\Windows\CurrentVersion\Run` の 64/32-bit view。32-bit view の承認先が、通常は 64-bit view 側の `Explorer\StartupApproved\Run32` です。HKCU の Run は共有扱いなので、両 view を別エントリーとして二重列挙しないこと。
- **§5.4：StartupApproved の対応を明記。** 接頭辞は `Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved`。通常、HKCU Run→HKCU `Run`、HKLM native Run→HKLM `Run`、HKLM 32-bit Run→HKLM `Run32`。ユーザー/Common Startup folder は各 scope の `StartupFolder`、値名は `.lnk` 等のファイル名です。フォルダは Known Folder API で取得します。
- **§5.4：値形式は非公開仕様として扱う。** 一般に観測される形式は `REG_BINARY` 12 bytes、先頭 DWORD が状態、後ろ 8 bytes が無効化時の FILETIME。代表例は enabled=`2/6`、disabled=`3/7`。`2` だけを enabled と判定したり、未知値を一律上書きしないこと。元の型・全 bytes・値の不存在を保存し、各対象 build で Task Manager による変更と往復照合が必要です。[実装者による検証記録](https://github.com/unknownaimer/unknownutility/blob/main/docs/DECISIONS.md)
- **§5.4：Scheduled Task は StartupApproved では止まりません。** Task Scheduler の `IRegisteredTask.Enabled` で制御する別 provider に分離。パッケージアプリの StartupTask も別機構です。RunOnce・ポリシー起動まで一括してカバーすると説明しないでください。

- **§5.2：効率モードの大筋は正しい。** Task Manager は低優先度＋EcoQoS を設定します。`SetProcessInformation(ProcessPowerThrottling)` の `EXECUTION_SPEED` を ControlMask/StateMask の両方に設定し、優先度は `IDLE_PRIORITY_CLASS` を使う方針を明記。Windows 10 の対応機能は LowQoS なので「同じ効率モード」と断言しないこと。[公式説明](https://devblogs.microsoft.com/performance-diagnostics/reduce-process-interference-with-task-manager-efficiency-mode/)、[設定 API](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation)
- **§5.2・§8：効率モードは二操作なので部分成功があります。** 変更前の優先度と throttling masks を保存し、失敗・解除時の復元規則を定義。「解除＝Normal＋強制 HighQoS」は不適切。表示も「EcoQoS」「優先度」「本アプリで設定」を分けます。
- **§5.2：affinity は group 選択 UI だけでは実現しません。** `SetProcessAffinityMask` の mask は一 group 分。Windows 11 の multi-group process に任意の跨ぎ mask を設定する API ではありません。v1 は単一 group の hard affinity に限定し、跨ぎ設定は v1.x。CPU Sets は hard affinity と同義ではありません。[processor groups](https://learn.microsoft.com/en-us/windows/win32/procthread/processor-groups)
- **§5.6：WTS は CPU％を返す API ではありません。** セッション名・ユーザー・状態は WTS、CPU/メモリは既存 Snapshot の SessionId 集計に統一。`WTSEnumerateProcessesEx(level=1)` は累積時間等を返しますが、他ユーザーセッションの列挙には管理者要件があります。通常権限では部分取得を表示してください。[WTS の権限要件](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsenumerateprocessesexw)

- **§7：RSMB の通常権限取得は妥当。** `GetSystemFirmwareTable('RSMB', 0, …)`。C# では provider 定数を `0x52534D42` と明示し、文字列の byte 順との取り違えを防止。返却は SMBIOS 本体だけでなく `RawSMBIOSData` ヘッダー付きです。[公式 API](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getsystemfirmwaretable)
- **§7：SMBIOS の UEFI capability と現在の boot 状態は別。** Secure Boot 有効状態・TPM・VBS/HVCI は Type 0/1/2/3 からは揃いません。各項目に実際の取得 API を割り当てること。CPUID の対応命令と OS が使用可能な命令も分離。RAM の速度は MHz と MT/s を混同しないでください。
- **§7：EDID の読み方は妥当だが、レジストリ全走査は避ける。** 現在の DisplayConfig target の monitor device path→SetupAPI device instance→hardware key の `EDID` を読む。接続履歴を現行画面として出さず、EDID override・拡張 block・checksum を処理。「preferred timing」を常に native と断定しません。[EDID override の仕組み](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/overriding-monitor-edids)
- **§7：NVMe の取得方式も妥当。ただし通常権限成功は保証しない。** まず必要最小限の access、共有 Read/Write でデバイスを開き、`ProtocolTypeNvme / NVMeDataTypeLogPage / NVME_LOG_PAGE_HEALTH_INFO` を問い合わせる。返却 descriptor・offset・length・実返却長を検査。権限拒否、未対応、I/O エラーを分けます。[公式 NVMe query](https://learn.microsoft.com/en-us/windows/win32/fileio/working-with-nvme-devices)
- **§7・§3：NVMe 健康値には 128-bit カウンターがあります。** 書込量は Data Units ×512,000 bytes。寿命使用率は 100% 超があり得るので clamp しない。SATA の 194 等の raw 値も単純整数・温度として共通解釈しないでください。
- **§7：Wi-Fi の denied は昇格で解消するとは限りません。** 新しい Windows の SSID 等は位置情報の同意に依存します。privacy 拒否として表示し、Helper で迂回しないこと。[Wi-Fi API の変更](https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes)

- **§4・§1：WinVerifyTrust は選択時だけでも重く、既定では通信し得ます。** UI 外で実行し、`WTD_UI_NONE`、`WTD_CACHE_ONLY_URL_RETRIEVAL`、失効確認方針を固定。「オフライン検証済み」と「オンライン失効確認済み」を区別。返値は `0` のみ成功、STATEACTION_CLOSE も必要です。[WINTRUST_DATA](https://learn.microsoft.com/en-us/windows/desktop/api/wintrust/ns-wintrust-wintrust_data)
- **§4・§6：署名キャッシュは ProcessKey だけでは不足。** ファイル identity・更新に結び付けます。埋込署名が無くても catalog 署名があり得ます。`CompanyName` は改変可能なリソースで、検証済み signer と別フィールドにしてください。
- **§8：`IsProcessCritical` の必要権限は `PROCESS_QUERY_LIMITED_INFORMATION`。** 戻り値は API 成否、out 引数が Critical。失敗を「非 Critical」にしないこと。[公式 API](https://learn.microsoft.com/ja-jp/windows/win32/api/processthreadsapi/nf-processthreadsapi-isprocesscritical)
- **§8：PPL は AccessDenied だけでは識別できません。** `GetProcessInformation(ProcessProtectionLevelInfo)` を使い、`PROTECTION_LEVEL_NONE` と比較。問い合わせ失敗は unknown。これは同じく QUERY_LIMITED_INFORMATION で問い合わせます。[公式 API](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocessinformation)
- **§8：中核プロセスを session 0 に限定しない。** csrss/winlogon は対話セッションにも存在します。自分自身には GUI だけでなく Helper/SensorHost も含め、判定不能時の操作方針を明記します。
- **§3：`2^53 を超えない` 前提を撤回。** 作成 FILETIME は既に超えています。ProcessKey は lossless な文字列にし、64/128-bit 累積値も decimal string 等へ統一。ISO 時刻は表示用とし、JS のミリ秒 Date を通して操作キーを再生成しないこと。
- **§3：status に `error` と `gone`、値に取得時刻・実測区間・古さを追加。** 現状では I/O 故障やプロセス消滅を unsupported に誤分類します。
- **§6：「commit が物理の90%」はメモリ逼迫判定として不適切。** `CommitTotal/CommitLimit` と `AvailablePhysical` を別々に評価。ページフォルト一般を pagefile paging と同一視しないでください。§5 の動的 CPU クロック表示も、§7 の「実効クロックなし」と整合させます。

**2. parity の不足と、v1 の現実的な線**

- **§5・§13：現在の仕様は文字通りの Task Manager parity には未達です。** プロセス別ネット B/s・ディスク帰属、アプリ履歴、Startup impact、ユーザー切断/サインアウト、ダンプ等が欠けます。追加機能が多くても、この差は残ります。
- **v1 で足す優先度が高いもの：** 「新しいタスクを実行」、応答なし/サスペンド等の状態の定義、プロセス↔サービスの相互移動、ユーザー行から所属プロセスへの展開。メモリ・CPU・GPU の列には測定量を明記。
- **§5.2：アプリ/バックグラウンド/Windows の完全再現は別案件です。** `EnumWindows` と親子関係だけでは UWP、host process、ブラウザーのグループを再現できません。v1 は独自の分類基準を公開し、Task Manager と一致するとは称さない方が確実です。
- **残してよい v1：** priority、単一 group affinity、正しく復元できる効率モード、Snapshot 集計のユーザータブ、読み取り MCP、Run/StartupFolder の検証済み範囲、SCM のサービス操作。これらを一律に削る必要はありません。
- **数 agent-days なら v1.x 推奨：** multi-group affinity、Scheduled Task の変更、パッケージ StartupTask 制御、完全なアプリ分類、全 §7 の深掘り、150 件の日英説明データ、汎用 SATA/USB SMART 対応。ハード対応範囲の検証が最大の不確実性です。
- **§12：7950X＋NVIDIA 一台では範囲不足。** Win10、AMD/Intel GPU、ノート・battery、64超 CPU、別ユーザー/UAC 別資格情報は別途必要。未検証機能を明記して出すか、公開条件を満たすまでその機能を延期してください。

**3. Helper・MCP の穴**

- **§8：ユーザー SID＋exe 名＋PID では呼び出し元認証が弱い。** 接続直後に OS 取得の peer PID、保持した process handle、作成時刻、token の user/logon SID・session・integrity を照合。JSON 内の PID/SID は信用しません。
- **§8：pipe は相互認証。** GUI 側も UAC 起動で得た Helper process handle と pipe server PID を照合。ランダム名、`FILE_FLAG_FIRST_PIPE_INSTANCE`、`PIPE_REJECT_REMOTE_CLIENTS`、明示 DACL。クライアントへの過大な GenericWrite は pipe instance 作成権まで与え得ます。[pipe の権限](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)
- **§8：「同じユーザー」だけでは別資格情報 UAC が壊れます。** 標準ユーザーが管理者資格情報を入力すると Helper の SID は異なります。元 caller の SID と昇格先 SID を区別するプロトコルにしてください。
- **§8：一回限りの UAC と要求を結び付ける。** operation・対象・引数を固定した request ID/hash を起動時の情報と照合し、一要求だけ処理。認証後に別のサービス停止などへ差し替えられない設計にします。
- **§8：パスや署名確認だけでは同一ユーザーの悪意あるコードから隔離できません。** 同じ integrity から正規 GUI に注入・操作され得ます。通常ユーザー侵害後まで防ぐ境界が必要なら、要求内容を elevated 側で人に示す設計が必要。「正規 GUI だから人の操作」とは扱わないこと。
- **§8・§14：ZIP 配置の Helper は、隣接 DLL/.NET 依存物も昇格して読みます。** `SetDefaultDllDirectories` を managed Main で呼ぶだけでは起動前ロードを守れません。配布物全体の ACL・整合性・配置を検討し、署名された exe 一個だけの検証で済ませないでください。
- **§8：Helper は汎用 registry writer / DeviceIoControl proxy にしない。** HKLM の固定 startup provider、固定 SMART 読取要求、ローカル SCM のみ。任意の key path・device path・IOCTL・ATA command を受け取らないこと。
- **§8：検証は Helper 側で再実行。** GUI からの「Critical=false」「PPL=false」は信用しない。対象を必要権限で開き、同じ handle 上で ProcessKey と保護状態を再確認します。
- **§8・§9：`--started-at` 省略時の意味を明記。** 開いた時点のプロセスを対象にするなら、古い `ps` の結果を保証できません。ツリーは全子孫の ProcessKey を確保し、親 PID 再利用を排除。部分成功を結果に残します。
- **§5.2・§8：WM_CLOSE は handle 操作ではありません。** HWND→PID/ProcessKey を再照合し、ブロックしない送信方法と UIPI 拒否を処理。Explorer の再起動は元ユーザー・元セッションで行い、Helper から elevated Explorer を起動しないこと。
- **§5.5：サービス再起動はトランザクションではありません。** `SERVICE_*_PENDING`、checkpoint/wait hint、停止不能、依存サービス、途中失敗を処理。起動要求成功と RUNNING 到達を区別し、変更前状態を自動復元できると保証しません。
- **§8：Helper の操作表に効率モード・affinity がありません。** 通常権限のみの仕様なのか、型付き昇格操作も実装するのかを固定してください。

- **§10：readOnlyHint は注釈で、権限境界ではありません。** MCP dispatcher は明示した read-only interface のみ参照し、CLI subcommand を名前で汎用実行しない構成にします。[MCP tools](https://modelcontextprotocol.io/specification/2025-06-18/server/tools)
- **§10：「読み取り」の裏で自動 UAC を出さない。** storage health の denied は MCP にそのまま返すか、所有者が事前設定した権限方針に従うこと。GUI 用の自動 Helper fallback をそのまま共有しないでください。
- **§9・§10：伏せ字解除を tool 引数から指定可能にすると AI が解除できます。** MCP の公開範囲は起動設定で決め、出力直前に共通 redactor を適用。パス・UNC・エラー・デバイス ID・JSON キー/値も対象です。
- **§10：JSON に入れるだけでは prompt injection を防げません。** 未信頼データとして維持し、tool description/instructions に混ぜない。Markdown・端末の制御文字を escape。入力 schema、サイズ、並行観測、キャンセル、stdin EOF 時の停止も定義します。
- **§10：現行の version fallback 方針は、その三版の範囲では正しい。** ただし negotiated version ごとの出力差を守る必要があります。`initialized`、capabilities、取消、tool error を含め SDK に寄せ、対応表は実装した版だけにしてください。[lifecycle](https://modelcontextprotocol.io/specification/2025-06-18/basic/lifecycle)

**4. 5 agents の分担案**

最初の skeleton commit は統合担当が単独で完成させます。

- **プロジェクト固定：** solution、全 csproj、衝突しない AssemblyName、SDK/package versions、manifest、publish 集約、CI、空の各 exe の起動。
- **契約固定：** immutable `Snapshot/ProcessInfo/ProcessKey/Value<T>`、全単位、時刻精度、status、sample window、stable device ID、JSON schema と fixture。
- **境界固定：** `ISnapshotSource`、`IProcessMetadataProvider`、`IHardwareProvider`、`IManagementService`、read-only report/MCP interface、Helper/SensorHost の wire schema・終了/取消契約。
- **縦一本を通す：** fake Snapshot→GUI 一覧→CLI JSON→MCP tool result。プロセス生成/消滅・denied・初回 CPU 未計測も fixture に含めます。

| Agent | 専有する場所・責任 |
|---|---|
| A：計測 | `Core/Collection/**`、native process parser、CPU/memory/PDH/NIC/GPU、差分・履歴・計測試験 |
| B：GUI | `Gui/**`、全画面・仮想一覧・選択維持・DPI/keyboard。計算は追加せず契約を利用 |
| C：部品 | `Core/Hardware/**`、`SensorHost/**`、SMBIOS/EDID/storage/vendor API と parser fixtures |
| D：操作 | `Core/Management/**`、`Helper/**`、startup/services/priority/EcoQoS/affinity、認証・安全試験 |
| E：CLI・AI | `Cli/**`、`Core/Reporting/**`、CLI/MCP、伏せ字、診断規則、protocol 試験 |

- 各 agent は skeleton SHA から別 worktree。共有 DTO・project/依存関係・CI は統合担当だけが変更し、契約変更は先に共通 commit で配布。
- 4 agents なら E を統合担当が兼任。B と D は UI と操作実装を分離し、同じ Form/Service ファイルを共同編集しない構成にします。

**5. §11 の数字を誤解させる点**

- **GUI 単体だけを見出しにしない。** GUI＋SensorHost＋計測中の他 worker の合計 CPU/private bytes も掲載。working set 合算は共有ページを二重計上するため、合算の意味を明記します。
- **平均 CPU は外部計測器で CPU 時間差分から算出。** Task Manager の表示値を読む方法や、自分自身の collector だけによる評価は避ける。`CPU秒/実経過秒/論理CPU数×100` と式を記載。
- **7950X の 0.3% は一コア換算9.6%。** CPU ms/秒も併記し、小型 CPU でも同じ負担と受け取られる表現を避けます。
- **「同じ1秒」だけでは同条件になりません。** 表示列、GPU採取、ソート、窓サイズ、プロセス数、権限、履歴、metadata キャッシュを固定。GPU 採取を止めた PCミエルと、採取中の Task Manager を比較しないこと。
- **同時起動比較は相互に干渉。** 同じ負荷で順序を交互にして別々に測る。電源設定・OS build・driver・公開 build 設定を記録します。
- **60秒一回では不足。** 初期 metadata 収集中と定常状態を分け、複数回の分布を示す。private/working set は平均・最大、CPU は全区間平均とサンプル分布を掲載。
- **起動完了の定義を二つに分ける。** 「一覧を描画」と「二点採取後の有効 CPU/GPU 値を表示」。名前だけ先に出す実装を、値まで取得した相手より速いと比較しないこと。
- **warm/cold を操作として定義。** warm は何を一度実行した後か、cold は再起動後かを記載。working set trimming は cold start の代用になりません。
- **§12 に長時間確認も追加。** 60秒で軽くても、PID metadata/icon キャッシュや PDH instance が増え続ける実装は通ります。プロセス churn・全タブ巡回・数時間後のメモリ/handle 数を公開前に確認してください。
