using PcMieru.Wire;

namespace PcMieru.Core.Management;

/// <summary>
/// The texts of every <see cref="MessageKeys"/> constant in ja and en (Wire has no language), plus the few keys only
/// Core produces. A test fails when a constant has no entry or when ja and en use different placeholders.
/// Placeholders per key are written in the comments of <see cref="MessageKeys"/> (and checked by the same test).
/// </summary>
public static class ManagementStrings
{
    /// <summary>Core-only: the user said no at the UAC prompt (not an error).</summary>
    public const string UacDeclined = "mgmt.uac.declined";

    /// <summary>Core-only: the Helper ended with exit code 0 but left no readable result file.</summary>
    public const string HelperDone = "mgmt.helper.done";

    /// <summary>Core-only, kept from the skeleton: a generic "protected" line for callers with no reason.</summary>
    public const string ProtectedReason = "mgmt.protected.reason";

    /// <summary>Core-only: the Helper did not end in time. It is left running, so the change may still happen.</summary>
    public const string HelperTimeout = "mgmt.helper.timeout";

    /// <summary>Core-only: the request is one the Helper would refuse, so it was not sent (no UAC prompt was shown).</summary>
    public const string HelperBadRequest = "mgmt.helper.bad-request";

    public static readonly StringEntry[] All =
    [
        new(ProtectedReason, "保護されたプロセスのため操作できません", "This process is protected and cannot be changed"),
        new(UacDeclined, "管理者への昇格が拒否されました", "The administrator prompt was declined"),
        new(HelperDone, "管理者権限の操作を実行しました", "The administrator operation was carried out"),

        // --- protection ---
        new(MessageKeys.ProtectedSystem, "システムプロセスのため操作できません", "This is a system process and cannot be changed"),
        new(MessageKeys.ProtectedSelf, "PCミエル自身のため操作できません", "This is PC Mieru itself and cannot be changed"),
        new(MessageKeys.ProtectedCritical, "終了すると Windows が停止する重要なプロセスのため操作できません", "This is a critical Windows process (ending it would crash Windows) and cannot be changed"),
        new(MessageKeys.ProtectedProcess, "保護プロセスのため操作できません", "This is a protected process and cannot be changed"),
        new(MessageKeys.ProtectedCore, "Windows の中核プロセスのため操作できません", "This is a core Windows process and cannot be changed"),
        new(MessageKeys.ProtectedUnknown, "守られているかを確かめられないため操作しません", "Cannot confirm the process is not protected, so nothing was done"),

        // --- processes ---
        new(MessageKeys.ProcessTerminated, "プロセス {pid} を終了しました", "Process {pid} was ended"),
        new(MessageKeys.ProcessTerminatePending, "終了を要求しましたが、プロセス {pid} がまだ残っています", "Ending was requested but process {pid} is still there"),
        new(MessageKeys.ProcessGone, "プロセスは既に終了しています", "The process has already ended"),
        new(MessageKeys.ProcessReplaced, "別のプロセスに変わっています（PID が再利用されました）。何もしていません", "This is no longer the same process (the PID was reused). Nothing was done"),
        new(MessageKeys.ProcessDenied, "権限がありません。管理者として実行すると操作できる場合があります", "Access denied. Running as administrator may allow it"),
        new(MessageKeys.ProcessError, "操作に失敗しました（エラー {code}）", "The operation failed (error {code})"),
        new(MessageKeys.ProcessPrioritySet, "優先度を {priority} にしました", "Priority set to {priority}"),
        new(MessageKeys.EfficiencyOn, "効率モードをオンにしました", "Efficiency mode is on"),
        new(MessageKeys.EfficiencyOff, "効率モードをオフにしました（元の優先度に戻しました）", "Efficiency mode is off (the previous priority was restored)"),
        new(MessageKeys.EfficiencyUnsupported, "このプロセスには効率モードを設定できません", "Efficiency mode cannot be set for this process"),
        new(MessageKeys.EfficiencyRolledBack, "効率モードの設定が途中で失敗したため、元の状態に戻しました", "Setting efficiency mode failed halfway, so it was put back as it was"),
        new(MessageKeys.EfficiencyRollbackFailed, "効率モードの設定が途中で失敗し、元に戻すこともできませんでした。プロセスの状態を確認してください", "Setting efficiency mode failed halfway and could not be put back. Please check the process"),
        new(MessageKeys.CloseDone, "ウィンドウを閉じ、プロセスが終了しました", "The window was closed and the process ended"),
        new(MessageKeys.CloseSent, "ウィンドウに閉じる要求を送りました。プロセスはまだ動いています（保存の確認が出ているかもしれません）", "A close request was sent to the window. The process is still running (it may be asking to save)"),
        new(MessageKeys.CloseNoWindow, "閉じられるウィンドウがありません", "There is no window to close"),
        new(MessageKeys.CloseNoReply, "ウィンドウが応答しません。「終了」を使うこともできます", "The window did not respond. You can use End instead"),
        new(MessageKeys.TreeDone, "{count} 個のプロセスを終了しました", "{count} process(es) ended"),
        new(MessageKeys.TreePartial, "{total} 個のうち {done} 個のプロセスを終了しました", "{done} of {total} processes ended"),
        new(MessageKeys.TreeNothing, "終了できるプロセスがありません", "There is no process to end"),
        new(MessageKeys.AffinitySet, "CPU の割り当てを変更しました", "The CPU assignment was changed"),
        new(MessageKeys.AffinityInvalid, "CPU の割り当てが正しくありません（空、または存在しない CPU を含んでいます）", "The CPU assignment is not valid (empty, or it includes CPUs that do not exist)"),
        new(MessageKeys.AffinityMultiGroup, "複数のプロセッサグループにまたがるプロセスの割り当ては変更できません", "The CPU assignment of a process that spans processor groups cannot be changed"),

        // --- services ---
        new(MessageKeys.ServiceStarted, "サービス {name} を開始しました", "Service {name} was started"),
        new(MessageKeys.ServiceStopped, "サービス {name} を停止しました", "Service {name} was stopped"),
        new(MessageKeys.ServiceRestarted, "サービス {name} を再起動しました", "Service {name} was restarted"),
        new(MessageKeys.ServiceAlreadyRunning, "サービス {name} は既に実行中です", "Service {name} is already running"),
        new(MessageKeys.ServiceAlreadyStopped, "サービス {name} は既に停止しています", "Service {name} is already stopped"),
        new(MessageKeys.ServiceStartPending, "サービス {name} の開始を要求しましたが、時間内に実行中になりませんでした（まだ開始処理中かもしれません）", "Starting service {name} was requested but it did not reach Running in time (it may still be starting)"),
        new(MessageKeys.ServiceStopPending, "サービス {name} の停止を要求しましたが、時間内に停止しませんでした（まだ停止処理中かもしれません）", "Stopping service {name} was requested but it did not reach Stopped in time (it may still be stopping)"),
        new(MessageKeys.ServiceStartFailed, "サービス {name} を開始できませんでした（終了コード {exitCode}）", "Service {name} could not be started (exit code {exitCode})"),
        new(MessageKeys.ServiceNotFound, "サービス {name} が見つかりません", "Service {name} was not found"),
        new(MessageKeys.ServiceDenied, "サービス {name} を操作する権限がありません", "No permission to control service {name}"),
        new(MessageKeys.ServiceDisabled, "サービス {name} は無効に設定されているため開始できません", "Service {name} is disabled and cannot be started"),
        new(MessageKeys.ServiceDependentsRunning, "サービス {name} を必要とする次のサービスが実行中のため、停止しません: {dependents}", "Service {name} was not stopped because these running services need it: {dependents}"),
        new(MessageKeys.ServiceCannotStop, "サービス {name} は停止を受け付けません", "Service {name} does not accept a stop request"),
        new(MessageKeys.ServiceRestartStoppedOnly, "サービス {name} は停止しましたが、再び開始できませんでした（{reason}）。停止したままです", "Service {name} was stopped but could not be started again ({reason}). It stays stopped"),
        new(MessageKeys.ServiceError, "サービス {name} の操作に失敗しました（エラー {code}）", "Controlling service {name} failed (error {code})"),
        new(MessageKeys.ServiceBadName, "サービス名が正しくありません", "The service name is not valid"),

        // --- startup entries ---
        new(MessageKeys.StartupEnabled, "{name} を有効にしました", "{name} was enabled"),
        new(MessageKeys.StartupDisabled, "{name} を無効にしました（削除はしていません）", "{name} was disabled (nothing was deleted)"),
        new(MessageKeys.StartupAlreadyEnabled, "{name} は既に有効です", "{name} is already enabled"),
        new(MessageKeys.StartupAlreadyDisabled, "{name} は既に無効です", "{name} is already disabled"),
        new(MessageKeys.StartupNotFound, "スタートアップ項目 {name} が見つかりません", "Startup item {name} was not found"),
        new(MessageKeys.StartupListOnly, "この項目は一覧の表示だけで、切り替えはできません", "This entry is shown for information only and cannot be switched"),
        new(MessageKeys.StartupUnknownState, "{name} の状態の値が未知の形式のため、書き換えません", "The state value of {name} has an unknown format, so it is not changed"),
        new(MessageKeys.StartupDenied, "{name} を切り替える権限がありません", "No permission to switch {name}"),
        new(MessageKeys.StartupError, "スタートアップの切り替えに失敗しました（エラー {code}）", "Switching the startup entry failed (error {code})"),
        new(MessageKeys.StartupRestored, "{name} を最初の変更の前の状態に戻しました", "{name} was restored to the state before the first change"),
        new(MessageKeys.StartupNothingToUndo, "{id} には元に戻す記録がありません", "There is nothing recorded to undo for {id}"),

        // --- drives ---
        new(MessageKeys.DiskRead, "ドライブの情報を読み取りました", "The drive information was read"),
        new(MessageKeys.DiskNotFound, "ドライブ {drive} が見つかりません", "Drive {drive} was not found"),
        new(MessageKeys.DiskUnsupportedBus, "{bus} 接続のドライブには問い合わせません", "Drives on the {bus} bus are not queried"),
        new(MessageKeys.DiskDenied, "ドライブを読む権限がありません。管理者として実行してください", "No permission to read the drive. Run as administrator"),
        new(MessageKeys.DiskNoData, "このドライブからは情報が得られませんでした", "No information could be read from this drive"),
        new(MessageKeys.DiskError, "ドライブの読み取りに失敗しました（エラー {code}）", "Reading the drive failed (error {code})"),

        // --- helper client ---
        new(MessageKeys.HelperMissing, "PCMieru.Helper.exe が見つかりません。管理者権限が必要な操作はできません", "PCMieru.Helper.exe was not found. Operations that need administrator rights are not available"),
        new(MessageKeys.HelperStartFailed, "管理者権限のヘルパーを起動できませんでした（エラー {code}）", "The administrator helper could not be started (error {code})"),
        new(MessageKeys.HelperNoResult, "ヘルパーの結果を読み取れませんでした（操作が行われたかは終了コードだけが頼りです）", "The helper's result could not be read (only the exit code tells what happened)"),
        new(HelperTimeout, "管理者権限のヘルパーが時間内に終わりませんでした。操作は後から完了する場合があるので、現在の状態を確かめてください", "The administrator helper did not finish in time. The change may still complete, so check the current state"),
        new(HelperBadRequest, "この操作は管理者権限のヘルパーが受け付けない内容のため、実行しませんでした", "This request is one the administrator helper does not accept, so it was not run"),
        new(MessageKeys.HelperUnexpectedExit, "ヘルパーが想定外の終了コード {code} で終了しました", "The helper ended with an unexpected exit code {code}"),

        // --- run new task ---
        new(MessageKeys.TaskEmpty, "実行するコマンドを入力してください", "Enter a command to run"),
        new(MessageKeys.TaskStarted, "{command} を起動しました", "Started {command}"),
        new(MessageKeys.TaskNotFound, "{command} が見つかりません", "{command} was not found"),
        new(MessageKeys.TaskFailed, "起動に失敗しました（エラー {code}）", "Starting failed (error {code})"),

        // --- explorer restart ---
        new(MessageKeys.ShellRestarted, "エクスプローラーを再起動しました", "Explorer was restarted"),
        new(MessageKeys.ShellElevatedRefused, "PCミエルが管理者として動いているため、エクスプローラーは再起動しません（管理者権限のエクスプローラーを作らないためです）。通常の権限で起動し直してください", "Explorer was not restarted because PC Mieru is running as administrator (this avoids creating an elevated Explorer). Start PC Mieru normally and try again"),
        new(MessageKeys.ShellFailed, "エクスプローラーを再起動できませんでした", "Explorer could not be restarted"),
    ];
}
