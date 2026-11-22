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

            WriteBlockedTag(j);
            _io.DeleteFile(TxSteps.RollbackJournalDelete, _paths.Journal);
        }
        catch (TxFailure failure)
        {
            return Fail(j, failure.Reason, failure.Message);
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

                    _io.DeleteFileAndEmptyParents(TxSteps.RemoveNew, target, _paths.Install);
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

        _io.Move(TxSteps.RestorePlace, staging, target, replace: true);
    }

    /// <summary>The backup file is there, is a plain file and has the recorded length and hash. A lock or any read error is "unavailable", not an I/O failure of the rollback: the files are untouched.</summary>
    private static void RequireBackupFile(string path, long bytes, string sha256, string label)
    {
        Presence presence;
        long length;
        string? sha;
        try
        {
            presence = ManagedSet.Probe(path, out length, out sha);
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

    // ---------------------------------------------------------------------------------------------------------------- Failure

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

    /// <summary>No journal: a lone journal.json.tmp is removed; a sentinel is cleared only when the installed set verifies against installed.json; leftovers of a finished transaction are tidied.</summary>
    private TxResult RecoverWithoutJournal()
    {
        if (ManagedSet.Exists(_paths.JournalTemp))
        {
            _io.DeleteFile(TxSteps.JournalTmpDelete, _paths.JournalTemp);
        }

        if (ManagedSet.Exists(_paths.Sentinel))
        {
            var bytes = ReadSmall(_paths.Installed, TxJson.MaxBytes);
            if (bytes is null || !InstalledRecord.TryParse(bytes, out var record, out _))
            {
                return Fail(null, TxReason.SetNotRestored, "recovery-failed is set, there is no journal and installed.json cannot be read");
            }

            var findings = ManagedSet.Verify(_paths, record, _gate);
            if (findings.Count > 0)
            {
                return Fail(null, TxReason.SetNotRestored, "recovery-failed is set and the installed files are not what installed.json says: " + ManagedSet.Describe(findings));
            }

            _io.DeleteFile(TxSteps.SentinelDelete, _paths.Sentinel);
        }

        var left = false;
        foreach (var stale in _paths.TransactionFolders())
        {
            var folder = stale;
            left |= !Attempt(() => _io.DeleteTree(TxSteps.StaleClean, folder));
        }

        left |= !Attempt(DeleteNote);
        return Make(TxOutcome.NothingToDo, TxReason.None, "nothing was open", cleanupPending: left);
    }
}
