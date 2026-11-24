using PcMieru.Update.Bundle;

namespace PcMieru.Update.Transaction;

// The swap itself, the verify loop and the commit (DESIGN-B 5). Every change goes through TxIo (a named step with a fault point); every decision about a file is made from its REAL hash.
internal sealed partial class TxEngine
{
    /// <summary>
    /// Runs the swap of a committed (Prepared) journal. Any I/O failure from here on is a rollback, never a half state: the catches sit OUTSIDE <see cref="SwapCore"/>, and <see cref="RollBack"/> itself never throws
    /// an I/O exception (it answers <see cref="TxOutcome.RecoveryFailed"/> instead).
    /// </summary>
    private TxResult Swap(TxJournal journal) => Guarded(journal, () => SwapCore(journal));

    /// <summary>An I/O failure of <paramref name="body"/> means: put the old state back. A file that was not what the record said is "changed during swap"; everything else is a failed swap.</summary>
    private TxResult Guarded(TxJournal journal, Func<TxResult> body)
    {
        try
        {
            return body();
        }
        catch (TxChangedException ex)
        {
            return RollBack(journal, TxReason.FilesChangedDuringSwap, null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RollBack(journal, TxReason.SwapIoFailure, null, ex.Message);
        }
    }

    private TxResult SwapCore(TxJournal j)
    {
        j.Phase = TxPhase.Swapping;
        WriteJournal(j, TxSteps.JournalSwapping);

        for (var i = 0; i < j.Ops.Count; i++)
        {
            var op = j.Ops[i];
            CheckBefore(op);
            if (i > 0)
            {
                j.Ops[i - 1].State = TxOpState.Done;
            }

            // The intent is on the disk BEFORE the rename: a recovery that finds a half-done swap can tell which name was being worked on (and it still decides by the real hashes).
            op.State = TxOpState.Intent;
            WriteJournal(j, TxSteps.JournalIntent);
            Apply(j, op);
        }

        if (j.Ops.Count > 0)
        {
            j.Ops[^1].State = TxOpState.Done;
            WriteJournal(j, TxSteps.JournalIntent);
        }

        var findings = SetFindings(j, includeRecord: false);
        var after = FileIdentity.Of(_paths.Gate);
        if (_gateIdentity is { } before && (after is null || !after.Value.IsSameFile(before)))
        {
            findings.Add($"{GateFileLabel} (not the same file any more)");
        }

        if (findings.Count > 0)
        {
            return RollBack(j, TxReason.SetMismatch, TxTag.GenerationMismatch, "after the swap: " + string.Join(", ", findings.Take(3)));
        }

        _io.WriteReplace(TxSteps.SwapInstalled, _paths.Installed, j.To.ToUtf8Bytes());
        j.Phase = TxPhase.VerifyPending;
        WriteJournal(j, TxSteps.JournalVerifyPending);
        return VerifyLoop(j);
    }

    private static string GateFileLabel => BundleNames.Show(BundleFormat.GateFileName);

    /// <summary>What must be true right before an op is applied: the file is still exactly what the old record says (or, for an add, the name is still free).</summary>
    private void CheckBefore(TxOp op)
    {
        var target = _paths.InInstall(op.Path);
        var parent = Path.GetDirectoryName(target)!;
        if (!string.Equals(parent, _paths.Install, StringComparison.OrdinalIgnoreCase) && TxPaths.IsLink(parent))
        {
            throw new TxChangedException($"the folder of '{BundleNames.Show(op.Path)}' is a link");
        }

        if (op.Intent == TxOpIntent.Add)
        {
            if (ManagedSet.Look(target) != Presence.Absent)
            {
                throw new TxChangedException($"'{BundleNames.Show(op.Path)}' appeared before the swap could add it");
            }

            return;
        }

        var presence = ManagedSet.Probe(target, out _, out var sha);
        if (presence != Presence.File || !string.Equals(sha, op.OldSha256, StringComparison.Ordinal))
        {
            throw new TxChangedException($"'{BundleNames.Show(op.Path)}' is not the file the record says");
        }
    }

    private void Apply(TxJournal j, TxOp op)
    {
        var target = _paths.InInstall(op.Path);
        switch (op.Intent)
        {
            case TxOpIntent.Add:
                _io.Move(TxSteps.SwapAdd, TxPaths.Under(_paths.Next(j.Tx), op.Path), target, replace: false);
                break;
            case TxOpIntent.Overwrite:
                // A file a running program has mapped cannot be overwritten: it is renamed aside into work/<tx>/inuse and the new file takes its name.
                _io.MoveOver(TxSteps.SwapOverwrite, TxSteps.SwapAside, TxPaths.Under(_paths.Next(j.Tx), op.Path), target, TxPaths.Under(_paths.InUse(j.Tx), op.Path));
                break;
            default:
                _io.Move(TxSteps.SwapDisplace, target, TxPaths.Under(_paths.Displaced(j.Tx), op.Path), replace: false);
                break;
        }
    }

    /// <summary>
    /// What is different between the disk and the NEW generation (empty when the new set is whole): every file of the new record by length and hash, every removed name gone and, with
    /// <paramref name="includeRecord"/>, installed.json being the new record. Reads the real bytes; the journal is not consulted.
    /// </summary>
    private List<string> SetFindings(TxJournal j, bool includeRecord)
    {
        var findings = ManagedSet.Verify(_paths, j.To, _gate).Select(f => f.ToString()).ToList();
        foreach (var op in j.Ops.Where(o => o.Intent == TxOpIntent.Remove))
        {
            if (ManagedSet.Exists(_paths.InInstall(op.Path)))
            {
                findings.Add($"{BundleNames.Show(op.Path)} (still there)");
            }
        }

        if (includeRecord)
        {
            var presence = ManagedSet.Probe(_paths.Installed, out _, out var sha);
            if (presence != Presence.File || !string.Equals(sha, j.ToInstalledSha256, StringComparison.Ordinal))
            {
                findings.Add("installed.json (not the new record)");
            }
        }

        return findings;
    }

    // ---------------------------------------------------------------------------------------------------------------- VerifyPending

    /// <summary>
    /// VerifyPending (DESIGN-B 5): re-hash the whole new set; count the attempt in the journal BEFORE the launch; launch and verify through the seam; up to three attempts. A set that does not hold, or three failed
    /// attempts, is a rollback (the second with the "launch-failed" tag, the first with "generation-mismatch").
    /// </summary>
    private TxResult VerifyLoop(TxJournal j)
    {
        var launcher = _seams.Launcher ?? throw new InvalidOperationException("VerifyLoop needs a launcher");
        while (true)
        {
            var findings = SetFindings(j, includeRecord: true);
            if (findings.Count > 0)
            {
                return RollBack(j, TxReason.SetMismatch, TxTag.GenerationMismatch, "before launching: " + string.Join(", ", findings.Take(3)));
            }

            if (j.LaunchAttempts >= TxLimits.MaxLaunchAttempts)
            {
                return RollBack(j, TxReason.LaunchFailed, TxTag.LaunchFailed, $"the new version did not start in {j.LaunchAttempts} attempts");
            }

            j.LaunchAttempts++;
            WriteJournal(j, TxSteps.JournalAttempt);

            _io.Reach(TxSteps.Launch, TxEdge.Before, j.To.BuildId);
            LaunchVerdict verdict;
            try
            {
                verdict = launcher.LaunchAndVerify(new LaunchRequest(_paths.Install, j.Tx, j.LaunchAttempts, TxLimits.MaxLaunchAttempts, j.To.Version, j.To.BuildId, TxLimits.LaunchUiCheck, TxLimits.LaunchTotal));
            }
            catch (Exception ex) when (ex is not PowerCutException)
            {
                verdict = LaunchVerdict.Failed; // the launch seam failing is a failed launch, whatever it threw
            }

            _io.Reach(TxSteps.Launch, TxEdge.After, j.To.BuildId);
            if (verdict == LaunchVerdict.Ok)
            {
                j.Phase = TxPhase.CommitIntent;
                WriteJournal(j, TxSteps.JournalCommitIntent);
                return Commit(j);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------- Commit

    /// <summary>
    /// CommitIntent (DESIGN-B 5): the whole new set is verified once more by its real hashes; then the sentinel (if one is there) and the journal are removed, which is the commit. What is left (the backup, the work
    /// folder, the note) is cleaned afterwards, each on its own, and a failure there never touches the result.
    /// </summary>
    private TxResult Commit(TxJournal j)
    {
        List<string> findings;
        try
        {
            findings = SetFindings(j, includeRecord: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RollBack(j, TxReason.SwapIoFailure, null, ex.Message);
        }

        if (findings.Count > 0)
        {
            return RollBack(j, TxReason.SetMismatch, TxTag.GenerationMismatch, "before the commit: " + string.Join(", ", findings.Take(3)));
        }

        try
        {
            if (ManagedSet.Exists(_paths.Sentinel))
            {
                _io.DeleteFile(TxSteps.SentinelDelete, _paths.Sentinel);
            }

            _io.DeleteFile(TxSteps.CommitJournalDelete, _paths.Journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The new set is whole but the journal cannot be removed: the transaction is not over. Say so (the next recovery finishes it).
            return Fail(j, TxReason.RecoveryIoFailure, "the journal could not be removed: " + ex.Message);
        }

        var result = Make(TxOutcome.Committed, TxReason.None, $"{j.To.Version} ({j.To.BuildId}) is installed");
        WriteLastResult(result);
        var left = Cleanup(j.Tx, j.From.Version);
        return left ? result with { CleanupPending = true } : result;
    }
}
