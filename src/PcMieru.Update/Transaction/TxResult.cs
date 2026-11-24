namespace PcMieru.Update.Transaction;

/// <summary>How a transaction or a recovery ended.</summary>
public enum TxOutcome
{
    /// <summary>A recovery found nothing open (it may have tidied leftovers).</summary>
    NothingToDo,

    /// <summary>The new version is in place, verified and started once; the journal is gone.</summary>
    Committed,

    /// <summary>The old version is back, verified. The new version is NOT installed (see <see cref="TxResult.Reason"/>).</summary>
    RolledBack,

    /// <summary>The transaction stopped BEFORE the journal was committed: nothing in the install folder was changed (except the first installed.json, which was verified against the manifest first).</summary>
    Aborted,

    /// <summary>The recovery could not put the files back or could not prove the result. The journal and the backup are kept, the sentinel is written, and a person must run recover.exe or unzip a new copy.</summary>
    RecoveryFailed,

    /// <summary>
    /// The recovery (or the swap's own rollback) could not finish BECAUSE a file of the install is in use by a running program (a sharing violation, or a rename that was refused). Nothing is lost: the journal and the
    /// backup are kept, NO sentinel is written, and the same recovery can be run again once the program is closed.
    /// </summary>
    Busy,
}

/// <summary>
/// Why. Each value has a plain sentence for the person (<see cref="TxReasonText"/>); step 5 shows it on the update screen and the settings page.
/// </summary>
public enum TxReason
{
    /// <summary>Nothing to say (a commit, an empty recovery).</summary>
    None,

    // ----------------------------------------------------------------- Aborted: no change was made
    /// <summary>A journal (or the recovery-failed sentinel) from an earlier transaction is still there: a recovery must finish first.</summary>
    JournalPresent,

    /// <summary>The gate is not held, or the gate file is not the fixed bytes.</summary>
    GateLost,

    /// <summary>I\update is a link (a reparse point) or cannot be used.</summary>
    UpdateFolderUnsafe,

    /// <summary>The install is on a volume that is not NTFS or ReFS (a FAT or exFAT stick, a network share). The swap relies on the file system's ordered metadata log; it does not update there.</summary>
    NotNtfs,

    /// <summary>The process runs with an elevated token (as an administrator). A swap or a recovery is refused: it would leave files that a normal start cannot write.</summary>
    Elevated,

    /// <summary>The staged folder has no readable PCMieru.bundle.json, or one that is refused.</summary>
    StagedUnreadable,

    /// <summary>A staged file is not what its manifest says, or the two generations disagree about the gate file or the spelling of a name.</summary>
    StagedMismatch,

    /// <summary>Checked again from the disk after the program that asked had closed (the swapper's <c>--apply</c>): the version is blocked, not newer than the installed one, or gui.ini no longer says <c>update.mode=auto</c>. Nothing was changed.</summary>
    UpdateNotAllowed,

    /// <summary>The staged build is the installed build.</summary>
    SameBuild,

    /// <summary>There is no installed.json and no usable PCMieru.bundle.json to make the first one from.</summary>
    NoRecord,

    /// <summary>installed.json is there but cannot be read or is refused.</summary>
    InstalledRecordUnreadable,

    /// <summary>A file of the installed set is missing, changed or replaced by a link (or, for the first record, differs from the manifest). There is no automatic swap.</summary>
    InstalledFilesChanged,

    /// <summary>A file the new version brings already exists under that name and belongs to nobody the record knows (the user's). There is no automatic swap.</summary>
    NameCollision,

    /// <summary>There is not enough free space on the volume for the new files and the backup.</summary>
    DiskFull,

    /// <summary>The backup of the old set did not come out whole.</summary>
    BackupIncomplete,

    /// <summary>recover.exe could not be placed or does not match the record.</summary>
    RunnerUnavailable,

    /// <summary>Any other file failure while preparing.</summary>
    IoFailure,

    // ----------------------------------------------------------------- RolledBack
    /// <summary>A rename or a write failed while swapping (a sharing violation, access denied, ...). Not a fault of the new version: no failure tag.</summary>
    SwapIoFailure,

    /// <summary>A file that was supposed to be the old one was not (someone changed it between the check and the swap).</summary>
    FilesChangedDuringSwap,

    /// <summary>The new version did not start and answer in three attempts. The build is tagged in blocked.json.</summary>
    LaunchFailed,

    /// <summary>The installed set was not the new generation when it was checked (a mixed or damaged set). The build is tagged in blocked.json when the swap had got that far.</summary>
    SetMismatch,

    /// <summary>The caller did not give a launcher: the new version cannot be verified here, so it is not kept.</summary>
    NoLauncher,

    /// <summary>The update was cut off (a crash, a power loss, a killed process) and a recovery put the earlier state back.</summary>
    Interrupted,

    // ----------------------------------------------------------------- RecoveryFailed
    /// <summary>journal.json is broken, or names an unknown phase, or does not agree with itself.</summary>
    JournalUnreadable,

    /// <summary>A managed file has neither the old nor the new hash (and is not an added file that is absent).</summary>
    FileNeitherHash,

    /// <summary>The backup (or the displaced file) that is needed is missing, damaged or locked.</summary>
    BackupUnavailable,

    /// <summary>A file operation of the recovery failed.</summary>
    RecoveryIoFailure,

    /// <summary>The old set did not verify after the rollback.</summary>
    SetNotRestored,

    /// <summary>Three automatic attempts were used; only a person (recover.exe) tries again.</summary>
    RetriesExhausted,

    // ----------------------------------------------------------------- Busy
    /// <summary>A file of the install is in use by a running program and could not even be moved aside. The journal is kept; close the program and run the recovery again.</summary>
    FilesInUse,
}

/// <summary>The answer of <see cref="SwapTransaction.Run"/> and <see cref="TxRecovery.Recover"/>.</summary>
public sealed record TxResult(TxOutcome Outcome, TxReason Reason, string Detail)
{
    /// <summary>The transaction id (12 hex digits), or null when none was made.</summary>
    public string? Tx { get; init; }

    public string? FromVersion { get; init; }

    public string? FromBuildId { get; init; }

    public string? ToVersion { get; init; }

    public string? ToBuildId { get; init; }

    /// <summary>The failure tag written to blocked.json ("launch-failed", "generation-mismatch"), or null.</summary>
    public string? Tag { get; init; }

    /// <summary>True when prev-/work/ could not be removed after a commit or a rollback (it is retried; the result stands).</summary>
    public bool CleanupPending { get; init; }

    /// <summary>
    /// The install is in a state the next start can use as it is (a commit, a rollback, nothing to do, an abort). Not when the recovery failed, and not when it is only waiting (Busy: the journal is still there, so the
    /// start gate would say "recovery pending").
    /// </summary>
    public bool InstallUsable => Outcome is not (TxOutcome.RecoveryFailed or TxOutcome.Busy);

    public override string ToString() => $"{Outcome}/{Reason}: {Detail}";
}

/// <summary>The plain sentence for a reason (what the person sees), in Japanese and English. Step 5 puts it on the update screen; the settings page reads last-result.json and shows the same.</summary>
public static class TxReasonText
{
    public static string Ja(TxReason reason) => reason switch
    {
        TxReason.None => string.Empty,
        TxReason.JournalPresent => "前回の更新が途中で止まっています。先にその回復が必要です。",
        TxReason.GateLost => "PCミエルの起動の門を確かめられなかったため、更新しませんでした。",
        TxReason.UpdateFolderUnsafe => "更新用のフォルダー（update）が使えないため、更新しませんでした。",
        TxReason.NotNtfs => "このフォルダーのあるドライブは NTFS ではないため、自動では更新しません。ZIP を新しいフォルダーに展開してください。",
        TxReason.Elevated => "管理者として実行されているため、更新も回復も行いません。ふつうの権限で、もう一度開いてください。",
        TxReason.StagedUnreadable or TxReason.StagedMismatch => "ダウンロードした内容を確かめられなかったため、更新しませんでした。もう一度試してください。",
        TxReason.UpdateNotAllowed => "この更新は、いまの設定や版では入れないことになっているため、何も変えずにやめました。",
        TxReason.SameBuild => "すでに同じ版です。",
        TxReason.NoRecord or TxReason.InstalledRecordUnreadable or TxReason.InstalledFilesChanged =>
            "PCミエルのファイルが、展開したときの状態と違っています。自動では更新できません。ZIP をもう一度ダウンロードして、新しいフォルダーに展開してください。",
        TxReason.NameCollision => "新しい版が使うファイルと同じ名前のファイルが、すでにフォルダーにあります。自動では更新しません。ZIP を新しいフォルダーに展開してください。",
        TxReason.DiskFull => "空き容量が足りないため、更新を始めませんでした。空きを作ってから、もう一度試してください。",
        TxReason.BackupIncomplete or TxReason.RunnerUnavailable or TxReason.IoFailure => "更新の準備が整わなかったため、何も変えずにやめました。もう一度試してください。",
        TxReason.SwapIoFailure or TxReason.FilesChangedDuringSwap => "更新の途中でファイルを書き換えられなかったため、元の状態に戻しました。",
        TxReason.LaunchFailed or TxReason.SetMismatch or TxReason.NoLauncher => "新しい版が正しく起動しなかったため、元の版に戻しました。",
        TxReason.Interrupted => "更新が途中で止まったため、元の状態に戻しました。",
        TxReason.JournalUnreadable or TxReason.FileNeitherHash or TxReason.BackupUnavailable or TxReason.RecoveryIoFailure or TxReason.SetNotRestored or TxReason.RetriesExhausted =>
            "元に戻す作業が終わりませんでした。update フォルダーの recover.exe を実行するか、ZIP を新しいフォルダーに展開してください。",
        TxReason.FilesInUse => "PCミエルのファイルを使っているプログラムがあるため、回復はまだ終わっていません。PCミエルを閉じてから、もう一度回復を試してください。",
        _ => string.Empty,
    };

    public static string En(TxReason reason) => reason switch
    {
        TxReason.None => string.Empty,
        TxReason.JournalPresent => "An earlier update stopped part-way. It has to be recovered first.",
        TxReason.GateLost => "PC Mieru's start gate could not be checked, so nothing was updated.",
        TxReason.UpdateFolderUnsafe => "The update folder cannot be used, so nothing was updated.",
        TxReason.NotNtfs => "The drive of this folder is not NTFS, so PC Mieru does not update itself here. Unzip the ZIP into a new folder.",
        TxReason.Elevated => "This is running as administrator, so it does not update or recover anything. Open it again with normal rights.",
        TxReason.StagedUnreadable or TxReason.StagedMismatch => "The downloaded files could not be checked, so nothing was updated. Try again.",
        TxReason.UpdateNotAllowed => "This update is not allowed by the current settings or version, so nothing was changed.",
        TxReason.SameBuild => "This is already the same version.",
        TxReason.NoRecord or TxReason.InstalledRecordUnreadable or TxReason.InstalledFilesChanged =>
            "PC Mieru's files are different from how they were unzipped, so it cannot update itself here. Download the ZIP again and unzip it into a new folder.",
        TxReason.NameCollision => "A file with the same name as one of the new version's files is already in the folder. Nothing was updated. Unzip the ZIP into a new folder.",
        TxReason.DiskFull => "There is not enough free space, so the update was not started. Free some space and try again.",
        TxReason.BackupIncomplete or TxReason.RunnerUnavailable or TxReason.IoFailure => "The update could not be prepared, so nothing was changed. Try again.",
        TxReason.SwapIoFailure or TxReason.FilesChangedDuringSwap => "A file could not be replaced during the update, so the earlier state was put back.",
        TxReason.LaunchFailed or TxReason.SetMismatch or TxReason.NoLauncher => "The new version did not start properly, so the earlier version was put back.",
        TxReason.Interrupted => "The update stopped part-way, so the earlier state was put back.",
        TxReason.JournalUnreadable or TxReason.FileNeitherHash or TxReason.BackupUnavailable or TxReason.RecoveryIoFailure or TxReason.SetNotRestored or TxReason.RetriesExhausted =>
            "Putting the earlier version back did not finish. Run recover.exe in the update folder, or unzip the ZIP into a new folder.",
        TxReason.FilesInUse => "A program is using PC Mieru's files, so the recovery is not finished yet. Close PC Mieru and try the recovery again.",
        _ => string.Empty,
    };
}
