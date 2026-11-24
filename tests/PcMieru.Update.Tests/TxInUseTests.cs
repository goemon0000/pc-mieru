using Xunit.Abstractions;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;
using static PcMieru.Update.Tests.TxSwapTests;

namespace PcMieru.Update.Tests;

/// <summary>
/// Files a running program holds (review of step 4, S2). A program that has a DLL mapped as an IMAGE keeps Windows from deleting it or replacing it by name, but its name can be changed: the swap and the rollback
/// rename such a file aside into work\&lt;tx&gt;\inuse\ and put the other file at the free name. A program that keeps a file open WITHOUT letting it be renamed (a scanner) stops the rollback for a while:
/// that is "busy" (journal and backup kept, no sentinel, the same recovery runs again), never "recovery failed". The images here are really mapped (<see cref="MappedImage"/>); nothing is mocked.
/// </summary>
public sealed class TxInUseTests
{
    private readonly ITestOutputHelper _output;

    public TxInUseTests(ITestOutputHelper output) => _output = output;

    private static CutPoint At(TxStep step, TxEdge edge, int nth = 1) => new(step, edge, nth);

    private static void Cut(Rig rig, CutPoint at)
    {
        var cutter = new Cutter(at);
        Assert.Throws<PowerCutException>(() => rig.Swap(Rig.Seams(cutter, FakeLauncher.Ok())));
        Assert.True(cutter.Fired, "the power was never cut at " + at);
    }

    /// <summary>
    /// A recovery that runs after the test let go of a file it held. Something outside the test (a virus scanner, the indexer) can still have a file that was closed a moment ago for a few milliseconds, and that is
    /// "busy" by design (the journal stays and the same recovery runs again), so a busy result is asked again, up to two seconds. A recovery that is still busy then (nothing in this process holds anything) is returned
    /// as it is, and the test fails with the detail of the result. How many Busy answers it took is written to the test output, so a machine whose scanner is slow shows it (and a test that began to need more
    /// than a few is seen before it fails).
    /// </summary>
    private TxResult RecoverWhenLetGo(Rig rig, bool automatic = false)
    {
        var result = rig.Recover(automatic: automatic);
        var busy = 0;
        for (var again = 0; again < 20 && result.Outcome == TxOutcome.Busy; again++)
        {
            busy++;
            Thread.Sleep(100);
            result = rig.Recover(automatic: automatic);
        }

        _output.WriteLine($"RecoverWhenLetGo: {busy} Busy answer(s) before it was let go (limit 20, 100 ms apart); the result: {result.Outcome}");
        return result;
    }

    /// <summary>Every step the code reached, with its edge and what it was about (the file's path, as the code named it).</summary>
    private sealed class Steps
    {
        public List<(TxStep Step, TxEdge Edge, string? Item)> Reached { get; } = [];

        public IFaultInjector Injector => new HookInjector((step, edge, item) => Reached.Add((step, edge, item)));

        public bool Has(TxStep step, TxEdge edge, string fileName) =>
            Reached.Any(r => r.Step == step && r.Edge == edge && r.Item is not null && Path.GetFileName(r.Item).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] LeftBehind(Rig rig) =>
        Directory.Exists(Path.Combine(rig.Paths.Update, "work")) ? Directory.GetFiles(Path.Combine(rig.Paths.Update, "work"), "*", SearchOption.AllDirectories) : [];

    // ---------------------------------------------------------------------------------------------------------------------------- the premise

    [Fact]
    public void A_mapped_image_cannot_be_deleted_or_replaced_by_name_but_it_can_be_renamed()
    {
        // The test image really is the thing the swap is written for: if Windows ever let these succeed the tests below would prove nothing.
        using var rig = Rig.Create("inuse-premise", shape: Rig.RealImages);
        var path = Path.Combine(rig.Install, "PcMieru.Core.dll");
        rig.Map("PcMieru.Core.dll");

        Assert.ThrowsAny<Exception>(() => File.Delete(path));
        Assert.ThrowsAny<Exception>(() => File.Move(Path.Combine(rig.Staged, "PcMieru.Core.dll"), path, overwrite: true));
        Assert.ThrowsAny<Exception>(() => File.WriteAllBytes(path, [1, 2, 3]));
        var renamed = path + ".renamed";
        File.Move(path, renamed);
        Assert.False(File.Exists(path));
        File.Move(renamed, path);
        Assert.Equal(Rig.Sha(rig.Old.Files["PcMieru.Core.dll"]), Rig.Sha(File.ReadAllBytes(path)));
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the swap

    [Fact]
    public void A_swap_over_images_a_running_program_has_mapped_renames_them_aside_and_commits()
    {
        using var rig = Rig.Create("inuse-swap", recorded: true, shape: Rig.RealImages);
        rig.Map("PcMieru.Core.dll"); // changed by the release: overwritten
        rig.Map("old-only.dll");     // dropped by the release: removed
        var steps = new Steps();

        var result = rig.Swap(Rig.Seams(steps.Injector, FakeLauncher.Ok()));

        Assert.Equal((TxOutcome.Committed, TxReason.None), (result.Outcome, result.Reason));
        rig.AssertTree(Generation.New, "committed over mapped images");
        Assert.True(steps.Has(TxSteps.SwapAside, TxEdge.Before, "PcMieru.Core.dll") && steps.Has(TxSteps.SwapAside, TxEdge.After, "PcMieru.Core.dll"), "the changed library was not set aside");
        Assert.False(File.Exists(rig.Paths.Journal));
        Assert.False(File.Exists(rig.Paths.Sentinel));
        Assert.Equal(("committed", "none", "swap"), ReadLastResult(rig));

        // What a program still has mapped stays where it is (renamed): the old file is under work\<tx>\inuse, whole. Its folder cannot be deleted yet, which the result says.
        Assert.True(result.CleanupPending, "the mapped old file cannot be deleted yet, so a cleanup is pending");
        var aside = LeftBehind(rig).Where(f => Path.GetFileName(f).StartsWith("PcMieru.Core.dll.", StringComparison.Ordinal)).ToList();
        var inUse = Assert.Single(aside);
        Assert.Contains(Path.Combine("inuse"), inUse, StringComparison.Ordinal);
        Assert.Equal(Rig.Sha(rig.Old.Files["PcMieru.Core.dll"]), Rig.Sha(File.ReadAllBytes(inUse)));

        // The program ends; the next recovery finds nothing open, tidies what was left and the update folder is as clean as after any other swap.
        rig.ReleaseHolds();
        var tidy = rig.Recover();

        Assert.Equal(TxOutcome.NothingToDo, tidy.Outcome);
        Assert.False(tidy.CleanupPending);
        Assert.Empty(LeftBehind(rig));
        Assert.Equal(Generation.New, rig.AssertEnded("after the program ended and the leftovers were tidied"));
    }

    [Fact]
    public void A_file_a_scanner_holds_open_makes_the_swap_roll_back_with_the_old_version_whole()
    {
        using var rig = Rig.Create("inuse-scanner-swap", recorded: true);
        var held = Path.Combine(rig.Install, "PcMieru.Core.dll");
        TxResult result;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read)) // open, and nobody may rename or delete it
        {
            result = rig.Swap(Rig.Seams(launcher: FakeLauncher.Ok()));
        }

        Assert.Equal((TxOutcome.RolledBack, TxReason.SwapIoFailure), (result.Outcome, result.Reason));
        Assert.False(File.Exists(rig.Paths.Sentinel), "a file that could not be replaced is not a failed recovery");
        Assert.False(File.Exists(rig.Paths.Journal));
        rig.AssertTree(Generation.Old, "rolled back after a scanner held a file");
        Assert.Equal(Generation.Old, rig.AssertEnded("after the rollback"));
        Assert.Equal(TxOutcome.NothingToDo, rig.Recover().Outcome);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the rollback

    [Fact]
    public void A_rollback_over_images_the_new_version_has_mapped_renames_them_aside_and_ends_rolled_back()
    {
        using var rig = Rig.Create("inuse-rollback", recorded: true, shape: Rig.RealImages);
        var steps = new Steps();
        var launcher = new FakeLauncher(LaunchVerdict.Failed)
        {
            OnCall = _ =>
            {
                rig.Map("PcMieru.Core.dll"); // the new version has started and has its libraries mapped, then it fails its check
                rig.Map("new-feature.dll");
            },
        };

        var result = rig.Swap(Rig.Seams(steps.Injector, launcher));

        Assert.Equal((TxOutcome.RolledBack, TxReason.LaunchFailed), (result.Outcome, result.Reason));
        Assert.False(File.Exists(rig.Paths.Sentinel), "a mapped image is never a failed recovery");
        Assert.False(File.Exists(rig.Paths.Journal));
        rig.AssertTree(Generation.Old, "rolled back over mapped images");
        Assert.True(steps.Has(TxSteps.RestoreAside, TxEdge.After, "PcMieru.Core.dll"), "the changed library was not set aside");
        Assert.True(steps.Has(TxSteps.RestoreAside, TxEdge.After, "new-feature.dll"), "the added library was not set aside");
        Assert.Equal(("rolledBack", "launchFailed", "swap"), ReadLastResult(rig));
        Assert.True(result.CleanupPending);
        Assert.Equal(2, LeftBehind(rig).Count(f => f.Contains(Path.DirectorySeparatorChar + "inuse" + Path.DirectorySeparatorChar, StringComparison.Ordinal)));

        rig.ReleaseHolds();
        var tidy = rig.Recover();

        Assert.Equal(TxOutcome.NothingToDo, tidy.Outcome);
        Assert.Equal(Generation.Old, rig.AssertEnded("after the program ended"));
    }

    [Fact]
    public void A_recovery_after_a_cut_over_mapped_images_goes_back_to_the_old_version_without_failing()
    {
        // The power was cut with the new core in place; the new version's process is up and has mapped it (and the added library). The recovery in a fresh process finds them mapped.
        using var rig = Rig.Create("inuse-recover", recorded: true, shape: Rig.RealImages);
        Cut(rig, At(TxSteps.JournalVerifyPending, TxEdge.After));
        rig.Map("PcMieru.Core.dll");
        rig.Map("new-feature.dll");

        var result = rig.Recover(automatic: true);

        Assert.Equal(TxOutcome.RolledBack, result.Outcome);
        Assert.False(File.Exists(rig.Paths.Sentinel));
        rig.AssertTree(Generation.Old, "recovered over mapped images");
        rig.ReleaseHolds();
        Assert.Equal(TxOutcome.NothingToDo, rig.Recover().Outcome);
        Assert.Equal(Generation.Old, rig.AssertEnded("after the program ended"));
    }

    // ---------------------------------------------------------------------------------------------------------------------------- busy

    [Fact]
    public void A_file_nobody_may_rename_during_the_rollback_makes_it_busy_with_everything_kept_and_no_sentinel()
    {
        using var rig = Rig.Create("inuse-busy-swap", recorded: true);
        FileStream? holder = null;
        var launcher = new FakeLauncher(LaunchVerdict.Failed)
        {
            // The new version has started and something keeps its new core open, so that it cannot be renamed, replaced or deleted.
            OnCall = _ => holder = new FileStream(Path.Combine(rig.Install, "PcMieru.Core.dll"), FileMode.Open, FileAccess.Read, FileShare.Read),
        };
        TxResult result;
        try
        {
            result = rig.Swap(Rig.Seams(launcher: launcher));

            Assert.Equal((TxOutcome.Busy, TxReason.FilesInUse), (result.Outcome, result.Reason));
            Assert.False(result.InstallUsable);
            Assert.True(File.Exists(rig.Paths.Journal), "the journal stays: it is what the next recovery goes on from");
            Assert.Equal(TxPhase.Rollback, ReadJournal(rig).Phase);
            Assert.False(File.Exists(rig.Paths.Sentinel), "busy is not failed: no sentinel");
            AssertBackupIntact(rig);
            Assert.Equal(("busy", "filesInUse", "swap"), ReadLastResult(rig));
            Assert.False(string.IsNullOrWhiteSpace(TxReasonText.Ja(result.Reason)));
            Assert.False(string.IsNullOrWhiteSpace(TxReasonText.En(result.Reason)));
            using (var start = InstallGate.EnterStartup(rig.ExePath))
            {
                Assert.Equal(GateOutcome.RecoveryPending, start.Outcome);
            }
        }
        finally
        {
            holder?.Dispose();
        }

        var second = RecoverWhenLetGo(rig);

        Assert.True(second.Outcome == TxOutcome.RolledBack, "the recovery after the file was let go: " + second);
        Assert.False(File.Exists(rig.Paths.Sentinel));
        Assert.Equal(Generation.Old, rig.AssertEnded("after the file was let go"));
        using var after = InstallGate.EnterStartup(rig.ExePath);
        Assert.Equal(GateOutcome.Open, after.Outcome);
    }

    [Fact]
    public void An_automatic_recovery_that_finds_a_file_held_is_busy_every_time_and_never_uses_up_its_three_attempts()
    {
        using var rig = Rig.Create("inuse-busy-auto");
        Cut(rig, At(TxSteps.SwapOverwrite, TxEdge.After, 1));
        var journal = ReadJournal(rig);
        var working = journal.Ops.First(o => o.State == TxOpState.Intent && o.Intent == TxOpIntent.Overwrite);
        var held = TxPaths.Under(rig.Install, working.Path);
        Assert.Equal(0, journal.RecoveryAttempts);

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            for (var round = 1; round <= TxLimits.MaxAutomaticRecoveryAttempts + 2; round++)
            {
                var result = rig.Recover(automatic: true);

                Assert.Equal((TxOutcome.Busy, TxReason.FilesInUse), (result.Outcome, result.Reason));
                Assert.False(File.Exists(rig.Paths.Sentinel), $"round {round}: no sentinel");
                var now = ReadJournal(rig);
                Assert.Equal(TxPhase.Rollback, now.Phase);
                Assert.Equal(0, now.RecoveryAttempts);
                AssertBackupIntact(rig);
            }

            Assert.Equal(("busy", "filesInUse", "recovery"), ReadLastResult(rig));
            using var start = InstallGate.EnterStartup(rig.ExePath);
            Assert.Equal(GateOutcome.RecoveryPending, start.Outcome);
        }

        var last = RecoverWhenLetGo(rig, automatic: true);

        Assert.True(last.Outcome == TxOutcome.RolledBack, "the recovery after the program let go (not \"retries exhausted\"): " + last);
        Assert.Equal(Generation.Old, rig.AssertEnded("after the program let go"));
    }
}
