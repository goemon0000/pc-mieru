using System.Text;
using PcMieru.Update.Bundle;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Transaction;

// Rollback and recovery (DESIGN-B 5, 13). Both decide by the REAL hashes of the files: the journal says what was being done, never what is true.
internal sealed partial class TxEngine
{
    // ---------------------------------------------------------------------------------------------------------------- Rollback

    /// <summary>
    /// Puts the old generation back and proves it. Never throws an I/O exception: a rollback that cannot be completed or proven is <see cref="Fail"/> (the journal and the backup stay, the sentinel is written).
    /// Shared by the swap (an I/O failure, a failed launch, a set that does not hold) and by the recovery (anything interrupted).
    /// </summary>
    /// <param name="tag">The blocked.json tag to write when the rollback is done, or null to keep the one the journal already has.</param>
    private TxResult RollBack(TxJournal j, TxReason reason, string? tag, string detail)
    {
        try
        {
            j.Phase = TxPhase.Rollback;
            j.Tag = tag ?? j.Tag;
            var step = TxSteps.JournalRollback;
            if (_kind == "recovery" && !_attemptCounted)
            {
                _attemptCounted = true;
                j.RecoveryAttempts++; // counted BEFORE anything is changed: a recovery that is cut again is still counted
                step = TxSteps.JournalRecoveryAttempt;
            }

            WriteJournal(j, step);
            RestoreOps(j);
            RestoreRecord(j);

            var left = ManagedSet.Verify(_paths, j.From, _gate).Select(f => f.ToString()).ToList();
            var record = ManagedSet.Probe(_paths.Installed, out _, out var recordSha);
            if (record != Presence.File || !string.Equals(recordSha, j.FromInstalledSha256, StringComparison.Ordinal))
            {
                left.Add("installed.json (not the old record)");
            }

            foreach (var op in j.Ops.Where(o => o.Intent == TxOpIntent.Add))
            {
                // Only OUR added file counts (the new hash); a different file under that name is the user's.
                if (ManagedSet.Probe(_paths.InInstall(op.Path), out _, out var addedSha) == Presence.File && string.Equals(addedSha, op.NewSha256, StringComparison.Ordinal))
                {
                    left.Add($"{BundleNames.Show(op.Path)} (an added file is still there)");
                }
            }

            if (left.Count > 0)
            {
                throw new TxFailure(TxReason.SetNotRestored, "the old set did not verify after the rollback: " + string.Join(", ", left.Take(3)));
            }

            if (ManagedSet.Exists(_paths.Sentinel))
            {
                _io.DeleteFile(TxSteps.SentinelDelete, _paths.Sentinel);
            }

            if (WriteBlockedTag(j) is { } tagError)
            {
                detail += "; " + tagError;
            }

            _io.DeleteFile(TxSteps.RollbackJournalDelete, _paths.Journal);
        }
        catch (TxFailure failure)
        {
            return Fail(j, failure.Reason, failure.Message);
        }
        catch (Exception ex) when (TxNative.IsInUse(ex))
        {
            // A running program holds one of the files (it could not even be renamed aside, or a read ran into its lock). Nothing is lost: the journal and the backup stay, there is NO sentinel, and the
            // same recovery can be run again when the program is closed.
            return Busy(j, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(j, TxReason.RecoveryIoFailure, ex.Message);
        }

        var result = Make(TxOutcome.RolledBack, reason, detail, j.Tag);
        WriteLastResult(result);
        var cleanup = Cleanup(j.Tx, j.From.Version);
        return cleanup ? result with { CleanupPending = true } : result;
    }

    /// <summary>
    /// Brings every changed name back to the old content, by what is really on the disk: an added file goes only when it has the NEW hash (and its now-empty folders with it); a replaced file is restored from the
    /// backup when it is the new one (or is gone); a displaced file is moved back (or restored from the backup). A replaced or removed name whose file is neither the old nor the new one is not touched: the rollback
    /// stops there (<see cref="TxReason.FileNeitherHash"/>). An ADDED name that holds a file with neither hash is somebody else's: it is left alone and is not a failure (the old set never had that name).
    /// </summary>
    private void RestoreOps(TxJournal j)
    {
        for (var i = j.Ops.Count - 1; i >= 0; i--)
        {
            var op = j.Ops[i];
            var target = _paths.InInstall(op.Path);
            var presence = ManagedSet.Probe(target, out _, out var sha);
            var isOld = presence == Presence.File && string.Equals(sha, op.OldSha256, StringComparison.Ordinal);
            var isNew = presence == Presence.File && string.Equals(sha, op.NewSha256, StringComparison.Ordinal);
            switch (op.Intent)
            {
                case TxOpIntent.Add:
                    if (presence == Presence.Absent)
                    {
                        break;
                    }

                    if (!isNew)
                    {
                        // Somebody else's file under a name the new version wanted (it appeared after the preparation, or it replaced ours): not ours, so it is never touched. The old set does not
                        // contain this name, so leaving the file where it is does not make the old set wrong.
                        break;
                    }

                    // The folders the journal says this transaction made, and only those, go when the file leaves them empty.
                    _io.DeleteFileAndEmptyParents(TxSteps.RemoveNew, target, _paths.Install, [.. j.NewDirs.Select(_paths.InInstall)], TxSteps.RestoreAside, TxPaths.Under(_paths.InUse(j.Tx), op.Path));
                    break;

                case TxOpIntent.Overwrite:
                    if (isOld)
                    {
                        break;
                    }

                    if (!isNew && presence != Presence.Absent)
                    {
                        throw new TxFailure(TxReason.FileNeitherHash, $"'{BundleNames.Show(op.Path)}' is neither the old nor the new file");
                    }

                    RestoreFromBackup(j, op.Path, target);
                    break;

                default:
                    if (isOld)
                    {
                        break;
                    }

                    if (presence != Presence.Absent)
                    {
                        throw new TxFailure(TxReason.FileNeitherHash, $"'{BundleNames.Show(op.Path)}' is not the old file and is not gone");
                    }

                    Undisplace(j, op, target);
                    break;
            }
        }
    }

    /// <summary>The old file back from the displaced folder (if it is whole there), else from the backup.</summary>
    private void Undisplace(TxJournal j, TxOp op, string target)
    {
        var displaced = TxPaths.Under(_paths.Displaced(j.Tx), op.Path);
        Presence presence;
        string? sha;
        try
        {
            presence = ManagedSet.Probe(displaced, out _, out sha);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TxFailure(TxReason.BackupUnavailable, $"the displaced file '{BundleNames.Show(op.Path)}' cannot be read: {ex.Message}");
        }

        if (presence == Presence.File && string.Equals(sha, op.OldSha256, StringComparison.Ordinal))
        {
            _io.Move(TxSteps.Undisplace, displaced, target, replace: false);
            return;
        }

        RestoreFromBackup(j, op.Path, target);
    }

    /// <summary>
    /// The old file from the backup: read back and checked first (a missing, short or changed backup file stops the rollback), copied to work/&lt;tx&gt;/restore, checked again, and renamed over the target in one step.
    /// </summary>
    private void RestoreFromBackup(TxJournal j, string relativeName, string target)
    {
        if (!j.From.TryGet(relativeName, out var file))
        {
            throw new TxFailure(TxReason.BackupUnavailable, $"'{BundleNames.Show(relativeName)}' is not in the old record");
        }

        var backup = TxPaths.Under(_paths.PrevFiles(j.From.Version, j.Tx), relativeName);
        RequireBackupFile(backup, file.Bytes, file.Sha256, relativeName);
        var staging = TxPaths.Under(_paths.Restore(j.Tx), relativeName);
        try
        {
            _io.CopyNew(TxSteps.RestoreCopy, backup, staging, file.Bytes, file.Sha256, overwrite: true);
        }
        catch (TxSourceMismatchException ex)
        {
            throw new TxFailure(TxReason.BackupUnavailable, ex.Message);
        }

        _io.MoveOver(TxSteps.RestorePlace, TxSteps.RestoreAside, staging, target, TxPaths.Under(_paths.InUse(j.Tx), relativeName));
    }

    /// <summary>
    /// The backup file is there, is a plain file and has the recorded length and hash. A program that holds the file (a scanner) is "in use": the rollback stops with <see cref="TxOutcome.Busy"/> and goes on when the
    /// file is let go. Any other read error is "unavailable", not an I/O failure of the rollback: the files are untouched.
    /// </summary>
    private static void RequireBackupFile(string path, long bytes, string sha256, string label)
    {
        Presence presence;
        long length;
        string? sha;
        try
        {
            presence = ManagedSet.Probe(path, out length, out sha);
        }
        catch (Exception ex) when (TxNative.IsInUse(ex))
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TxFailure(TxReason.BackupUnavailable, $"the backup of '{BundleNames.Show(label)}' cannot be read: {ex.Message}");
        }

        if (presence != Presence.File || length != bytes || !string.Equals(sha, sha256, StringComparison.Ordinal))
        {
            throw new TxFailure(TxReason.BackupUnavailable, $"the backup of '{BundleNames.Show(label)}' is missing or is not the recorded file");
        }
    }

    /// <summary>installed.json back to the old record when it is not that already (from prev-&lt;old&gt;/&lt;tx&gt;/installed.json, checked against the journal's hash of the old bytes).</summary>
    private void RestoreRecord(TxJournal j)
    {
        var presence = ManagedSet.Probe(_paths.Installed, out _, out var sha);
        if (presence == Presence.File && string.Equals(sha, j.FromInstalledSha256, StringComparison.Ordinal))
        {
            return;
        }

        var backup = _paths.PrevInstalled(j.From.Version, j.Tx);
        var length = j.From.ToUtf8Bytes().Length;
        RequireBackupFile(backup, length, j.FromInstalledSha256, "installed.json");
        var staging = Path.Combine(_paths.Restore(j.Tx), "installed.json");
        try
        {
            _io.CopyNew(TxSteps.RestoreInstalled, backup, staging, length, j.FromInstalledSha256, overwrite: true);
        }
        catch (TxSourceMismatchException ex)
        {
            throw new TxFailure(TxReason.BackupUnavailable, ex.Message);
        }

        _io.Move(TxSteps.RestoreInstalled, staging, _paths.Installed, replace: true);
    }

    // ---------------------------------------------------------------------------------------------------------------- Busy and failure

    /// <summary>
    /// A rollback that stopped only because a running program uses a file: <see cref="TxOutcome.Busy"/>. The journal (phase Rollback) and the backup are kept and no sentinel is written, so the start gate says
    /// "recovery pending" and the next recovery goes on from the real files. An automatic attempt that ended like this is not held against the three that are allowed (waiting for a program to close is not a failure).
    /// </summary>
    private TxResult Busy(TxJournal j, string detail)
    {
        if (_kind == "recovery" && _attemptCounted && j.RecoveryAttempts > 0)
        {
            j.RecoveryAttempts--;
            Attempt(() => WriteJournal(j, TxSteps.JournalRecoveryAttempt));
            _attemptCounted = false;
        }

        var result = Make(TxOutcome.Busy, TxReason.FilesInUse, detail, j.Tag);
        WriteLastResult(result);
        return result;
    }

    /// <summary>
    /// A transaction or recovery that cannot be proven: the sentinel is written (so the start gate says "recovery failed" and nothing tries again by itself), the journal and the backup are KEPT, and the answer is
    /// <see cref="TxOutcome.RecoveryFailed"/>. <paramref name="journal"/> may be null (a journal that cannot be read).
    /// </summary>
    private TxResult Fail(TxJournal? journal, TxReason reason, string detail)
    {
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes($"recovery failed: {TxReasonCode(reason)}\n");
        Attempt(() => _io.WriteReplace(TxSteps.SentinelWrite, _paths.Sentinel, text));
        var result = Make(TxOutcome.RecoveryFailed, reason, detail, journal?.Tag);
        WriteLastResult(result);
        return result;
    }

    private static string TxReasonCode(TxReason reason) => LastResult.Camel(reason.ToString());

    // ---------------------------------------------------------------------------------------------------------------- Recovery

    /// <summary>
    /// The recovery. Nothing open (no journal, no sentinel) does nothing but tidy. A journal is read strictly (anything else is <see cref="TxReason.JournalUnreadable"/>: kept, sentinel written). An automatic
    /// recovery gives up when three attempts are counted in the journal; a manual one always tries. What it does by phase: Prepared, Swapping and Rollback go back to the old set; VerifyPending verifies the new set
    /// again and launches it (the attempts already counted are used up), or goes back when it does not hold or there is no launcher; CommitIntent verifies once more and finishes.
    /// </summary>
    public TxResult Recover(bool automatic)
    {
        try
        {
            return RecoverCore(automatic);
        }
        catch (TxFailure failure) when (failure.Reason is TxReason.GateLost or TxReason.UpdateFolderUnsafe)
        {
            return Make(TxOutcome.Aborted, failure.Reason, failure.Message);
        }
        catch (TxFailure failure)
        {
            return Fail(null, failure.Reason, failure.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(null, TxReason.RecoveryIoFailure, ex.Message);
        }
    }

    private TxResult RecoverCore(bool automatic)
    {
        CheckGate();
        switch (ManagedSet.Look(_paths.Update))
        {
            case Presence.Absent:
                return Make(TxOutcome.NothingToDo, TxReason.None, "there is no update folder");
            case Presence.Other:
                break;
            default:
                throw new TxFailure(TxReason.UpdateFolderUnsafe, "the update folder is a link or not a folder");
        }

        _mayWriteResult = true;
        if (!ManagedSet.Exists(_paths.Journal))
        {
            return RecoverWithoutJournal();
        }

        var bytes = ReadSmall(_paths.Journal, TxJson.MaxBytes);
        if (bytes is null)
        {
            return Fail(null, TxReason.JournalUnreadable, "journal.json cannot be read");
        }

        if (!TxJournal.TryParse(bytes, out var journal, out var error))
        {
            return Fail(null, TxReason.JournalUnreadable, "journal.json is refused: " + error);
        }

        Bind(journal);
        if (ManagedSet.Exists(_paths.JournalTemp))
        {
            _io.DeleteFile(TxSteps.JournalTmpDelete, _paths.JournalTemp); // a journal write that was cut before its rename
        }

        if (automatic && journal.RecoveryAttempts >= TxLimits.MaxAutomaticRecoveryAttempts)
        {
            return Fail(journal, TxReason.RetriesExhausted, $"{journal.RecoveryAttempts} recovery attempts were already made");
        }

        switch (journal.Phase)
        {
            case TxPhase.VerifyPending when _seams.Launcher is not null:
                return Guarded(journal, () => VerifyLoop(journal));
            case TxPhase.VerifyPending:
                return RollBack(journal, TxReason.NoLauncher, null, "the new version cannot be verified here, so it is not kept");
            case TxPhase.CommitIntent:
                return Commit(journal);
            default:
                return RollBack(journal, ReasonOf(journal), null, $"found phase '{TxJournal.PhaseName(journal.Phase)}'");
        }
    }

    private static TxReason ReasonOf(TxJournal journal) => journal.Tag switch
    {
        TxTag.LaunchFailed => TxReason.LaunchFailed,
        TxTag.GenerationMismatch => TxReason.SetMismatch,
        _ => TxReason.Interrupted,
    };

    /// <summary>
    /// The record the installed set is held to when there is no journal: installed.json. When installed.json is NOT THERE AT ALL (<paramref name="allowOwnManifest"/>, only for a stale backup folder: a swap writes
    /// the first record before anything else, so no swap has begun) the install's own manifest is used, as the preparation does for a first record, and it is trusted only as far as the files verify against it.
    /// installed.json that is there but cannot be read or is refused gives nothing (null): the person decides.
    /// </summary>
    private InstalledRecord? ReadRecordToVerifyAgainst(bool allowOwnManifest, out string name)
    {
        name = "installed.json";
        var bytes = ReadSmall(_paths.Installed, TxJson.MaxBytes);
        if (bytes is not null)
        {
            return InstalledRecord.TryParse(bytes, out var record, out _) ? record : null;
        }

        if (!allowOwnManifest || ManagedSet.Exists(_paths.Installed))
        {
            return null;
        }

        var manifest = ReadSmall(_paths.InInstall(BundleFormat.InnerManifestName), BundleManifest.MaxBytes);
        if (manifest is null || !BundleManifest.TryParse(manifest, out var bundle, out _))
        {
            return null;
        }

        name = BundleFormat.InnerManifestName;
        return InstalledRecord.FromBundle(bundle, manifest);
    }

    /// <summary>
    /// No journal. When there is anything to tidy that could still be a half-done swap (a journal.json.tmp, the sentinel, or a transaction folder with a backup in it), nothing is removed until the installed set
    /// verifies against installed.json (<see cref="ReadRecordToVerifyAgainst"/>): a journal that is gone must not take the only copy of the old files with it. If it does not verify, everything is kept and the sentinel is written (a person, or a
    /// journal put back, decides). Only then are the lone journal.json.tmp and the sentinel removed and the leftovers of a finished transaction tidied.
    /// </summary>
    private TxResult RecoverWithoutJournal()
    {
        var stale = _paths.ScanStale();
        _leftAlone = stale.LeftAlone;
        var sentinel = ManagedSet.Exists(_paths.Sentinel);
        var temporary = ManagedSet.Exists(_paths.JournalTemp);

        // Only a folder that can hold a BACKUP makes the verification necessary: prev-<version>/<tx>. work/<tx> holds only copies of the new files (and files renamed aside), and a preparation cut before its
        // first record (installed.json) has made nothing else, so a first update of an install that was never recorded and was cut at its first step is not failed for a record that was never written.
        var holdsBackup = stale.ToDelete.Any(folder => Path.GetRelativePath(_paths.Update, folder).StartsWith("prev-", StringComparison.OrdinalIgnoreCase));
        if (sentinel || temporary || holdsBackup)
        {
            var what = sentinel ? "recovery-failed is set" : temporary ? "journal.json.tmp is left" : "a folder with a backup is left";
            var record = ReadRecordToVerifyAgainst(allowOwnManifest: !sentinel && !temporary, out var recordName);
            if (record is null)
            {
                return Fail(null, TxReason.SetNotRestored, $"{what}, there is no journal and installed.json cannot be read; nothing was removed");
            }

            var findings = ManagedSet.Verify(_paths, record, _gate);
            if (findings.Count > 0)
            {
                return Fail(null, TxReason.SetNotRestored, $"{what}, there is no journal and the installed files are not what {recordName} says (the backup is kept): " + ManagedSet.Describe(findings));
            }
        }

        if (temporary)
        {
            _io.DeleteFile(TxSteps.JournalTmpDelete, _paths.JournalTemp);
        }

        if (sentinel)
        {
            _io.DeleteFile(TxSteps.SentinelDelete, _paths.Sentinel);
        }

        var left = false;
        foreach (var folder in stale.ToDelete)
        {
            var one = folder;
            left |= !Attempt(() => _io.DeleteTree(TxSteps.StaleClean, one));
        }

        left |= !Attempt(DeleteNote);
        return Make(TxOutcome.NothingToDo, TxReason.None, "nothing was open", cleanupPending: left);
    }
}
