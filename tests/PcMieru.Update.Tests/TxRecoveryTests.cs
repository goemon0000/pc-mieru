using System.Text;
using System.Text.RegularExpressions;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;
using static PcMieru.Update.Tests.TxSwapTests;

namespace PcMieru.Update.Tests;

/// <summary>
/// The recovery (DESIGN-B 5, 13): what a fresh process does with whatever a stopped transaction left, decided by the REAL hashes of the files and not by what the journal counted. Every test stops a real swap at a
/// named point with a power cut (an exception that unwinds with no cleanup), then recovers with a fresh gate entry.
/// </summary>
public sealed class TxRecoveryTests
{
    private static CutPoint At(TxStep step, TxEdge edge, int nth = 1) => new(step, edge, nth);

    /// <summary>Runs the swap and cuts the power at <paramref name="at"/> (asserting it really was cut there).</summary>
    private static void Cut(Rig rig, CutPoint at, ILaunchVerifier? launcher = null)
    {
        var cutter = new Cutter(at);
        Assert.Throws<PowerCutException>(() => rig.Swap(Rig.Seams(cutter, launcher ?? FakeLauncher.Ok())));
        Assert.True(cutter.Fired, "the power was never cut at " + at);
    }

    private static string JournalText(Rig rig) => File.ReadAllText(rig.Paths.Journal);

    // ---------------------------------------------------------------------------------------------------------------------------- what the start gate says in between

    [Fact]
    public void A_stopped_transaction_makes_the_start_gate_say_recovery_pending_until_it_is_recovered()
    {
        using var rig = Rig.Create("startgate");
        Cut(rig, At(TxSteps.SwapOverwrite, TxEdge.After));

        using (var start = InstallGate.EnterStartup(rig.ExePath))
        {
            Assert.Equal(GateOutcome.RecoveryPending, start.Outcome);
            Assert.EndsWith(Path.Combine("update", GateFile.RecoverNoteName), start.RecoveryNotePath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(TxOutcome.RolledBack, rig.Recover().Outcome);
        using var after = InstallGate.EnterStartup(rig.ExePath);
        Assert.Equal(GateOutcome.Open, after.Outcome);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- Prepared and Swapping go back

    [Fact]
    public void A_prepared_journal_is_rolled_back_and_the_install_was_never_touched()
    {
        using var rig = Rig.Create("rec-prepared");
        Cut(rig, At(TxSteps.JournalPrepared, TxEdge.After));
        Assert.Equal(TxPhase.Prepared, ReadJournal(rig).Phase);
        rig.AssertTree(Generation.Old, "a prepared journal");

        var result = rig.Recover(automatic: true);

        Assert.Equal((TxOutcome.RolledBack, TxReason.Interrupted), (result.Outcome, result.Reason));
        Assert.Equal(("rolledBack", "interrupted", "recovery"), ReadLastResult(rig));
        Assert.Equal(Generation.Old, rig.AssertEnded("after recovering a prepared journal"));
        Assert.Empty(BlockedTags.Read(rig.Paths.Blocked)); // an interruption says nothing about the new build
    }

    [Theory]
    [InlineData("swap.add", 1)]
    [InlineData("swap.overwrite", 1)]
    [InlineData("swap.overwrite", 3)]
    [InlineData("swap.displace", 1)]
    public void A_swap_stopped_right_after_a_rename_is_put_back_to_the_old_set(string stepName, int nth)
    {
        using var rig = Rig.Create("rec-swapping");
        var step = TxSteps.All.Single(s => s.Name == stepName);
        Cut(rig, At(step, TxEdge.After, nth));
        Assert.Equal(TxPhase.Swapping, ReadJournal(rig).Phase);
        Assert.NotEqual(0, Rig.Diff(rig.Expected(Generation.Old), Rig.Snapshot(rig.Install)).Length); // the install really is half-swapped now

        var result = rig.Recover(automatic: true);

        Assert.Equal((TxOutcome.RolledBack, TxReason.Interrupted), (result.Outcome, result.Reason));
        Assert.Equal(Generation.Old, rig.AssertEnded("after a half swap"));
        Assert.Equal(("rolledBack", "interrupted", "recovery"), ReadLastResult(rig));
    }

    [Fact]
    public void A_swap_stopped_after_every_file_and_the_new_record_is_put_back_including_installed_json()
    {
        using var rig = Rig.Create("rec-installed");
        Cut(rig, At(TxSteps.SwapInstalled, TxEdge.After));
        rig.AssertTree(Generation.New, "all files are new at this point");
        Assert.Equal(rig.New.Record().ToUtf8Bytes(), File.ReadAllBytes(rig.Paths.Installed));
        Assert.Equal(TxPhase.Swapping, ReadJournal(rig).Phase); // the phase is not VerifyPending yet, and the recovery goes by the hashes anyway

        var result = rig.Recover();

        Assert.Equal(TxOutcome.RolledBack, result.Outcome);
        Assert.Equal(Generation.Old, rig.AssertEnded("after all files and the record were swapped"));
        Assert.Equal(rig.Old.Record().ToUtf8Bytes(), File.ReadAllBytes(rig.Paths.Installed));
    }

    [Fact]
    public void A_recovery_decides_by_the_real_files_and_not_by_the_intent_the_journal_names()
    {
        using var rig = Rig.Create("rec-hashes");
        Cut(rig, At(TxSteps.SwapOverwrite, TxEdge.After, 2));
        // Make the journal lie: every op says "pending", as if nothing had been done. The files say otherwise.
        var journal = ReadJournal(rig);
        foreach (var op in journal.Ops)
        {
            op.State = TxOpState.Pending;
        }

        File.WriteAllBytes(rig.Paths.Journal, journal.ToUtf8Bytes());

        var result = rig.Recover();

        Assert.Equal(TxOutcome.RolledBack, result.Outcome);
        Assert.Equal(Generation.Old, rig.AssertEnded("with a journal that lies about the progress"));
    }

    // ---------------------------------------------------------------------------------------------------------------------------- VerifyPending

    [Fact]
    public void A_verify_pending_journal_without_a_launcher_is_rolled_back()
    {
        using var rig = Rig.Create("rec-vp-nolauncher");
        Cut(rig, At(TxSteps.Launch, TxEdge.Before));
        var journal = ReadJournal(rig);
        Assert.Equal((TxPhase.VerifyPending, 1), (journal.Phase, journal.LaunchAttempts));
        rig.AssertTree(Generation.New, "the new set was whole when the power went");

        var result = rig.Recover(automatic: true);

        Assert.Equal((TxOutcome.RolledBack, TxReason.NoLauncher), (result.Outcome, result.Reason));
        Assert.Equal(Generation.Old, rig.AssertEnded("recover.exe has no launcher"));
        Assert.Empty(BlockedTags.Read(rig.Paths.Blocked)); // the new build is not blamed: it was never given a chance
    }

    [Fact]
    public void A_verify_pending_journal_with_a_consistent_set_resumes_the_verification_and_counts_the_used_attempts()
    {
        using var rig = Rig.Create("rec-vp-resume");
        Cut(rig, At(TxSteps.Launch, TxEdge.Before));
        var launcher = FakeLauncher.Ok();

        var result = rig.Recover(launcher, automatic: true);

        Assert.Equal(TxOutcome.Committed, result.Outcome);
        var request = Assert.Single(launcher.Calls);
        Assert.Equal(2, request.Attempt); // attempt 1 was counted before the power went
        Assert.Equal(Generation.New, rig.AssertEnded("a resumed verification"));
    }

    [Fact]
    public void The_attempts_used_before_the_power_cut_are_not_given_again()
    {
        using var rig = Rig.Create("rec-vp-attempts");
        Cut(rig, At(TxSteps.Launch, TxEdge.After, 2), FakeLauncher.AlwaysFail());
        Assert.Equal(2, ReadJournal(rig).LaunchAttempts);
        var launcher = FakeLauncher.AlwaysFail();

        var result = rig.Recover(launcher);

        Assert.Single(launcher.Calls); // one attempt was left, not three
        Assert.Equal(3, launcher.Calls[0].Attempt);
        Assert.Equal((TxOutcome.RolledBack, TxReason.LaunchFailed, TxTag.LaunchFailed), (result.Outcome, result.Reason, result.Tag));
        Assert.Equal(Generation.Old, rig.AssertEnded("after the third failed attempt"));
        Assert.Equal(TxTag.LaunchFailed, Assert.Single(BlockedTags.Read(rig.Paths.Blocked)).Reason);
    }

    [Fact]
    public void Three_used_attempts_mean_no_more_launches()
    {
        using var rig = Rig.Create("rec-vp-spent");
        Cut(rig, At(TxSteps.Launch, TxEdge.After, 3), FakeLauncher.AlwaysFail());
        Assert.Equal(3, ReadJournal(rig).LaunchAttempts);
        var launcher = FakeLauncher.Ok();

        var result = rig.Recover(launcher);

        Assert.Empty(launcher.Calls);
        Assert.Equal((TxOutcome.RolledBack, TxReason.LaunchFailed), (result.Outcome, result.Reason));
        Assert.Equal(Generation.Old, rig.AssertEnded("with no attempts left"));
    }

    [Theory]
    [InlineData("delete-added")]
    [InlineData("delete-overwritten")]
    [InlineData("old-content")]
    public void A_verify_pending_journal_whose_set_does_not_hold_is_rolled_back_without_a_launch(string how)
    {
        using var rig = Rig.Create("rec-vp-broken");
        Cut(rig, At(TxSteps.Launch, TxEdge.Before));
        switch (how)
        {
            case "delete-added":
                File.Delete(Path.Combine(rig.Install, "new-feature.dll"));
                break;
            case "delete-overwritten":
                File.Delete(Path.Combine(rig.Install, "PcMieru.Core.dll"));
                break;
            default:
                File.WriteAllBytes(Path.Combine(rig.Install, "NOTICE.txt"), rig.Old.Files["NOTICE.txt"]);
                break;
        }

        var launcher = FakeLauncher.Ok();
        var result = rig.Recover(launcher);

        Assert.Empty(launcher.Calls);
        Assert.Equal((TxOutcome.RolledBack, TxReason.SetMismatch), (result.Outcome, result.Reason));
        Assert.Equal(Generation.Old, rig.AssertEnded("a set that does not hold"));
        Assert.Equal(TxTag.GenerationMismatch, Assert.Single(BlockedTags.Read(rig.Paths.Blocked)).Reason);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- CommitIntent and Rollback

    [Fact]
    public void A_commit_intent_journal_is_finished_even_with_no_launcher()
    {
        using var rig = Rig.Create("rec-commit");
        Cut(rig, At(TxSteps.JournalCommitIntent, TxEdge.After));
        Assert.Equal(TxPhase.CommitIntent, ReadJournal(rig).Phase);

        var result = rig.Recover(automatic: true);

        Assert.Equal(TxOutcome.Committed, result.Outcome);
        Assert.Equal(Generation.New, rig.AssertEnded("a finished commit"));
        Assert.Equal(("committed", "none", "recovery"), ReadLastResult(rig));
    }

    [Fact]
    public void A_commit_intent_journal_whose_set_was_tampered_with_is_rolled_back_instead()
    {
        using var rig = Rig.Create("rec-commit-bad");
        Cut(rig, At(TxSteps.JournalCommitIntent, TxEdge.After));
        File.Delete(Path.Combine(rig.Install, "PcMieru.Core.dll"));

        var result = rig.Recover();

        Assert.Equal((TxOutcome.RolledBack, TxReason.SetMismatch), (result.Outcome, result.Reason));
        Assert.Equal(Generation.Old, rig.AssertEnded("a tampered commit"));
    }

    [Fact]
    public void A_rollback_that_was_cut_goes_on_and_keeps_the_reason_it_was_made_for()
    {
        using var rig = Rig.Create("rec-rollback");
        Cut(rig, At(TxSteps.RestorePlace, TxEdge.Before), FakeLauncher.AlwaysFail());
        var journal = ReadJournal(rig);
        Assert.Equal((TxPhase.Rollback, TxTag.LaunchFailed), (journal.Phase, journal.Tag));

        var result = rig.Recover(automatic: true);

        Assert.Equal((TxOutcome.RolledBack, TxReason.LaunchFailed), (result.Outcome, result.Reason));
        Assert.Equal(Generation.Old, rig.AssertEnded("a rollback that went on"));
        Assert.Equal(TxTag.LaunchFailed, Assert.Single(BlockedTags.Read(rig.Paths.Blocked)).Reason);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- when recovery cannot be proven

    [Fact]
    public void A_corrupt_journal_stops_the_recovery_keeps_everything_and_writes_the_sentinel()
    {
        using var rig = Rig.Create("rec-corrupt");
        Cut(rig, At(TxSteps.SwapOverwrite, TxEdge.After, 2));
        var bytes = File.ReadAllBytes(rig.Paths.Journal);
        var broken = bytes[..(bytes.Length / 2)];
        File.WriteAllBytes(rig.Paths.Journal, broken);
        var before = Rig.Snapshot(rig.Install);

        var result = rig.Recover(automatic: true);

        Assert.Equal((TxOutcome.RecoveryFailed, TxReason.JournalUnreadable), (result.Outcome, result.Reason));
        Assert.False(result.InstallUsable);
        Assert.True(File.Exists(rig.Paths.Sentinel));
        Assert.Equal(broken, File.ReadAllBytes(rig.Paths.Journal)); // kept as it is
        rig.AssertUnchanged(before, "a refused recovery"); // nothing in the install was touched
        AssertBackupIntact(rig); // the backup is still there, proven by hashes
        Assert.Equal(("recoveryFailed", "journalUnreadable", "recovery"), ReadLastResult(rig));
        using (var start = InstallGate.EnterStartup(rig.ExePath))
        {
            Assert.Equal(GateOutcome.RecoveryFailed, start.Outcome);
        }

        // Again, and again: the same answer, nothing more destroyed.
        Assert.Equal(TxReason.JournalUnreadable, rig.Recover().Reason);
        AssertBackupIntact(rig);
    }

    [Theory]
    [InlineData("phase")]
    [InlineData("op")]
    [InlineData("format")]
    [InlineData("empty")]
    public void A_journal_with_an_unknown_phase_or_a_foreign_shape_is_refused_not_guessed(string what)
    {
        using var rig = Rig.Create("rec-unknown");
        Cut(rig, At(TxSteps.SwapOverwrite, TxEdge.After, 1));
        var text = JournalText(rig);
        text = what switch
        {
            "phase" => Regex.Replace(text, "\"phase\"\\s*:\\s*\"swapping\"", "\"phase\": \"teleporting\""),
            "op" => Regex.Replace(text, "\"intent\"\\s*:\\s*\"add\"", "\"intent\": \"fly\""),
            "format" => text.Replace("pcm-journal/1", "pcm-journal/9", StringComparison.Ordinal),
            _ => string.Empty,
        };
        Assert.NotEqual(JournalText(rig), text);
        File.WriteAllText(rig.Paths.Journal, text);

        var result = rig.Recover(automatic: true);

        Assert.Equal((TxOutcome.RecoveryFailed, TxReason.JournalUnreadable), (result.Outcome, result.Reason));
        Assert.True(File.Exists(rig.Paths.Sentinel));
        AssertBackupIntact(rig);
    }

    [Fact]
    public void A_file_that_is_neither_the_old_nor_the_new_one_stops_the_recovery_and_a_manual_fix_lets_it_finish()
    {
        using var rig = Rig.Create("rec-neither");
        Cut(rig, At(TxSteps.SwapOverwrite, TxEdge.After, 1));
        var journal = ReadJournal(rig);
        var untouched = journal.Ops.Where(o => o.Intent == TxOpIntent.Overwrite && o.State == TxOpState.Pending).First();
        var target = Path.Combine(rig.Install, untouched.Path.Replace('/', '\\'));
        var garbage = Encoding.UTF8.GetBytes("neither the old nor the new file");
        File.WriteAllBytes(target, garbage);

        var result = rig.Recover(automatic: true);

        Assert.Equal((TxOutcome.RecoveryFailed, TxReason.FileNeitherHash), (result.Outcome, result.Reason));
        Assert.Equal(garbage, File.ReadAllBytes(target)); // never touched
        Assert.True(File.Exists(rig.Paths.Sentinel));
        Assert.True(File.Exists(rig.Paths.Journal));
        AssertBackupIntact(rig);
        using (var start = InstallGate.EnterStartup(rig.ExePath))
        {
            Assert.Equal(GateOutcome.RecoveryFailed, start.Outcome);
        }

        // The owner puts the file they care about back (here: the old content); a manual recovery now finishes and removes the sentinel.
        File.WriteAllBytes(target, rig.Old.AllFiles().Single(f => f.Key == untouched.Path).Value);
        var second = rig.Recover();

        Assert.Equal(TxOutcome.RolledBack, second.Outcome);
        Assert.False(File.Exists(rig.Paths.Sentinel), "a successful recovery removes the sentinel");
        Assert.Equal(Generation.Old, rig.AssertEnded("after the manual fix"));
        using var after = InstallGate.EnterStartup(rig.ExePath);
        Assert.Equal(GateOutcome.Open, after.Outcome);
    }

    [Fact]
    public void A_backup_that_is_locked_during_the_recovery_stops_it_with_everything_kept_and_a_retry_works()
    {
        using var rig = Rig.Create("rec-locked");
        Cut(rig, At(TxSteps.SwapOverwrite, TxEdge.After, 1));
        var journal = ReadJournal(rig);
        var working = journal.Ops.First(o => o.State == TxOpState.Intent && o.Intent == TxOpIntent.Overwrite);
        var backup = TxPaths.Under(rig.Paths.PrevFiles(journal.From.Version, journal.Tx), working.Path);
        Assert.True(File.Exists(backup));
        TxResult result;
        using (new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None)) // a scanner holds the backup file
        {
            result = rig.Recover(automatic: true);

            Assert.Equal((TxOutcome.RecoveryFailed, TxReason.BackupUnavailable), (result.Outcome, result.Reason));
            Assert.True(File.Exists(rig.Paths.Sentinel));
            Assert.True(File.Exists(rig.Paths.Journal));
        }

        AssertBackupIntact(rig); // released: every backup file is whole
        var second = rig.Recover();

        Assert.Equal(TxOutcome.RolledBack, second.Outcome);
        Assert.False(File.Exists(rig.Paths.Sentinel));
        Assert.Equal(Generation.Old, rig.AssertEnded("after the lock was released"));
    }

    [Fact]
    public void A_backup_file_that_changed_is_never_restored_and_the_recovery_fails_with_the_journal_kept()
    {
        using var rig = Rig.Create("rec-badbackup");
        Cut(rig, At(TxSteps.SwapOverwrite, TxEdge.After, 1));
        var journal = ReadJournal(rig);
        var working = journal.Ops.First(o => o.State == TxOpState.Intent && o.Intent == TxOpIntent.Overwrite);
        var backup = TxPaths.Under(rig.Paths.PrevFiles(journal.From.Version, journal.Tx), working.Path);
        var bytes = File.ReadAllBytes(backup);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(backup, bytes);
        var target = TxPaths.Under(rig.Install, working.Path);
        var newContent = File.ReadAllBytes(target);

        var result = rig.Recover();

        Assert.Equal((TxOutcome.RecoveryFailed, TxReason.BackupUnavailable), (result.Outcome, result.Reason));
        Assert.Equal(newContent, File.ReadAllBytes(target)); // a bad backup is never copied over a file
        Assert.True(File.Exists(rig.Paths.Journal));
        Assert.True(File.Exists(rig.Paths.Sentinel));
    }

    // ---------------------------------------------------------------------------------------------------------------------------- retries

    [Fact]
    public void Automatic_retries_stop_after_three_counted_attempts_and_a_manual_recovery_still_works()
    {
        using var rig = Rig.Create("rec-retries");
        Cut(rig, At(TxSteps.SwapOverwrite, TxEdge.After, 1));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var cutter = new Cutter(At(TxSteps.RestoreCopy, TxEdge.Before));
            Assert.Throws<PowerCutException>(() => rig.Recover(automatic: true, fault: cutter));
            Assert.Equal(attempt, ReadJournal(rig).RecoveryAttempts); // counted before anything was changed
        }

        var fourth = rig.Recover(automatic: true);

        Assert.Equal((TxOutcome.RecoveryFailed, TxReason.RetriesExhausted), (fourth.Outcome, fourth.Reason));
        Assert.True(File.Exists(rig.Paths.Sentinel));
        Assert.True(File.Exists(rig.Paths.Journal));
        AssertBackupIntact(rig);

        var manual = rig.Recover(automatic: false);

        Assert.Equal(TxOutcome.RolledBack, manual.Outcome);
        Assert.False(File.Exists(rig.Paths.Sentinel));
        Assert.Equal(Generation.Old, rig.AssertEnded("a manual recovery after the automatic ones gave up"));
    }

    // ---------------------------------------------------------------------------------------------------------------------------- tidying

    [Fact]
    public void A_lone_journal_temp_file_is_removed_and_a_stale_one_beside_a_valid_journal_too()
    {
        using var rig = Rig.Create("rec-tmp");
        Directory.CreateDirectory(rig.Paths.Update);
        File.WriteAllText(rig.Paths.JournalTemp, "{ half a journal");

        var lone = rig.Recover();

        Assert.Equal(TxOutcome.NothingToDo, lone.Outcome);
        Assert.False(File.Exists(rig.Paths.JournalTemp));
        rig.AssertTree(Generation.Old, "a lone temp file");

        Cut(rig, At(TxSteps.SwapOverwrite, TxEdge.After, 1));
        File.WriteAllText(rig.Paths.JournalTemp, "{ a newer journal that never got its rename");
        var beside = rig.Recover();

        Assert.Equal(TxOutcome.RolledBack, beside.Outcome);
        Assert.False(File.Exists(rig.Paths.JournalTemp));
        Assert.Equal(Generation.Old, rig.AssertEnded("a stale temp beside a valid journal"));
    }

    [Fact]
    public void A_sentinel_with_no_journal_is_cleared_only_when_the_installed_files_match_installed_json()
    {
        using var rig = Rig.Create("rec-sentinel", recorded: true);
        File.WriteAllText(rig.Paths.Sentinel, "recovery failed: something\n");
        var edited = Encoding.UTF8.GetBytes("edited");
        var path = Path.Combine(rig.Install, "stable.dll");
        File.WriteAllBytes(path, edited);

        var refused = rig.Recover();

        Assert.Equal((TxOutcome.RecoveryFailed, TxReason.SetNotRestored), (refused.Outcome, refused.Reason));
        Assert.True(File.Exists(rig.Paths.Sentinel));
        Assert.Equal(edited, File.ReadAllBytes(path)); // never "repaired"

        File.WriteAllBytes(path, rig.Old.Files["stable.dll"]);
        var cleared = rig.Recover();

        Assert.Equal(TxOutcome.NothingToDo, cleared.Outcome);
        Assert.False(File.Exists(rig.Paths.Sentinel));
        Assert.Equal(Generation.Old, rig.AssertEnded("a sentinel with nothing behind it"));
    }

    [Fact]
    public void An_added_file_that_the_user_replaced_is_left_alone_by_the_recovery_byte_for_byte()
    {
        using var rig = Rig.Create("rec-user-added");
        Cut(rig, At(TxSteps.SwapInstalled, TxEdge.After));
        var mine = Encoding.UTF8.GetBytes("my own new-feature.dll, made after the swap");
        File.WriteAllBytes(Path.Combine(rig.Install, "new-feature.dll"), mine);

        var result = rig.Recover();

        Assert.Equal(TxOutcome.RolledBack, result.Outcome);
        var expected = rig.Expected(Generation.Old);
        expected["new-feature.dll"] = Rig.Sha(mine);
        var difference = Rig.Diff(expected, Rig.Snapshot(rig.Install));
        Assert.True(difference.Length == 0, difference);
        Assert.Equal(mine, File.ReadAllBytes(Path.Combine(rig.Install, "new-feature.dll")));
        rig.AssertSettled(Generation.Old, "the user's file stays");
    }

    [Fact]
    public void A_folder_the_user_put_into_an_added_folder_keeps_that_folder_when_the_added_file_goes()
    {
        using var rig = Rig.Create("rec-user-subfolder");
        Cut(rig, At(TxSteps.SwapInstalled, TxEdge.After));
        var mine = Encoding.UTF8.GetBytes("a file made next to the added one");
        File.WriteAllBytes(Path.Combine(rig.Install, "en", "mine.txt"), mine);

        var result = rig.Recover();

        Assert.Equal(TxOutcome.RolledBack, result.Outcome);
        Assert.False(File.Exists(Path.Combine(rig.Install, "en", "PcMieru.Core.resources.dll")));
        Assert.Equal(mine, File.ReadAllBytes(Path.Combine(rig.Install, "en", "mine.txt")));
        Assert.True(File.Exists(Path.Combine(rig.Install, "keep.txt")));
    }

    [Fact]
    public void Recovering_where_nothing_was_ever_swapped_does_nothing()
    {
        using var rig = Rig.Create("rec-nothing");
        var before = Rig.Snapshot(rig.Install, includeUpdate: true);

        var result = rig.Recover(automatic: true);

        Assert.Equal(TxOutcome.NothingToDo, result.Outcome);
        rig.AssertUnchanged(before, "nothing to recover", includeUpdate: true);
        Assert.False(Directory.Exists(rig.Paths.Update));
    }

    [Fact]
    public void A_recovery_needs_the_gate_and_does_nothing_without_it()
    {
        using var rig = Rig.Create("rec-nogate");
        Cut(rig, At(TxSteps.SwapOverwrite, TxEdge.After, 1));
        var before = Rig.Snapshot(rig.Install);
        using var other = new FileStream(rig.Paths.Gate, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gate = rig.Enter();
        Assert.Equal(GateOutcome.Busy, gate.Outcome);

        var result = TxRecovery.Recover(gate, Rig.Seams(), automatic: false);

        Assert.Equal((TxOutcome.Aborted, TxReason.GateLost), (result.Outcome, result.Reason));
        rig.AssertUnchanged(before, "a recovery without the gate");
        Assert.True(File.Exists(rig.Paths.Journal));
    }
}
