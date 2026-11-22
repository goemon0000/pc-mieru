using System.Text;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;
using static PcMieru.Update.Tests.TxSwapTests;

namespace PcMieru.Update.Tests;

/// <summary>
/// What a cleaning and a recovery do with the leftovers of update/ when something there is a link, or is not theirs (review of step 4, M1, S1, m3). A junction at work\, at prev-x.y.z\ or at work\&lt;tx&gt;\next that
/// points to a folder of somebody's own is unlinked and never entered: every planted file behind it is still there, byte for byte, after a preparation, after a recovery with no journal, and after the cleanup
/// that follows a commit. Entries that are not transaction folders are left alone (and the result says so). A recovery with no journal does not throw the backup away when the files are not what installed.json says.
/// </summary>
public sealed class TxLinkSafetyTests
{
    private const string StaleTx = "abcdef012345";

    /// <summary>Removes the links a test made, as links (a plain rmdir), before the scratch folder is deleted. Whatever is not a link any more is left to the rig.</summary>
    private sealed class Links : IDisposable
    {
        private readonly List<string> _paths = [];

        public void Add(string path) => _paths.Add(path);

        public void Dispose()
        {
            foreach (var path in _paths)
            {
                try
                {
                    if (Directory.Exists(path) && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        Directory.Delete(path, recursive: false);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // left to the scratch sweep
                }
            }
        }
    }

    /// <summary>The folder a link points to: somebody's own files, outside the install, with folders that LOOK like transaction folders (so a walk through the link would delete them).</summary>
    private static string PlantVictim(Rig rig, params string[] txNames)
    {
        var victim = Path.Combine(rig.Root, "victim");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "loose.txt"), "somebody's own file\n");
        foreach (var tx in txNames.Append("0123456789ab"))
        {
            var folder = Path.Combine(victim, tx);
            Directory.CreateDirectory(Path.Combine(folder, "next", "deep"));
            File.WriteAllBytes(Path.Combine(folder, "planted.bin"), SyntheticSet.Bytes("p" + tx, 300));
            File.WriteAllBytes(Path.Combine(folder, "next", "deep", "planted2.bin"), SyntheticSet.Bytes("q" + tx, 300));
        }

        return victim;
    }

    private static void AssertVictimUntouched(SortedDictionary<string, string> before, string victim, string context)
    {
        var difference = Rig.Diff(before, Rig.Snapshot(victim, includeUpdate: true));
        Assert.True(difference.Length == 0, $"{context}: what the link points to changed: {difference}");
    }

    /// <summary>A leftover state: the link at <paramref name="where"/> (work, prev or next) in update/, as an earlier run could have left it.</summary>
    private static void PlantStaleLink(Rig rig, Links links, string where, string victim)
    {
        var update = rig.Paths.Update;
        Directory.CreateDirectory(update);
        switch (where)
        {
            case "work":
                Rig.Junction(Path.Combine(update, "work"), victim);
                links.Add(Path.Combine(update, "work"));
                break;
            case "prev":
                Rig.Junction(Path.Combine(update, "prev-" + rig.Old.Version), victim);
                links.Add(Path.Combine(update, "prev-" + rig.Old.Version));
                break;
            default:
                Directory.CreateDirectory(Path.Combine(update, "work", StaleTx));
                Rig.Junction(Path.Combine(update, "work", StaleTx, "next"), victim);
                links.Add(Path.Combine(update, "work", StaleTx, "next"));
                break;
        }
    }

    [Theory]
    [InlineData("work")]
    [InlineData("prev")]
    [InlineData("next")]
    public void A_link_left_in_update_is_unlinked_by_the_cleaning_of_a_swap_and_what_it_points_to_survives(string where)
    {
        using var rig = Rig.Create("m1-swap-" + where, recorded: true);
        using var links = new Links();
        var victim = PlantVictim(rig, StaleTx);
        var before = Rig.Snapshot(victim, includeUpdate: true);
        PlantStaleLink(rig, links, where, victim);

        var result = rig.Swap(FakeLauncher.Ok());

        Assert.Equal(TxOutcome.Committed, result.Outcome);
        AssertVictimUntouched(before, victim, "after a swap");
        rig.AssertTree(Generation.New, "after the swap");
        rig.AssertSettled(Generation.New, "after the swap"); // no work/ and no prev-*: the link is gone too
    }

    [Theory]
    [InlineData("work")]
    [InlineData("prev")]
    [InlineData("next")]
    public void A_link_left_in_update_is_unlinked_by_a_recovery_without_a_journal_and_what_it_points_to_survives(string where)
    {
        using var rig = Rig.Create("m1-rec-" + where, recorded: true);
        using var links = new Links();
        var victim = PlantVictim(rig, StaleTx);
        var before = Rig.Snapshot(victim, includeUpdate: true);
        PlantStaleLink(rig, links, where, victim);

        var result = rig.Recover();

        Assert.Equal(TxOutcome.NothingToDo, result.Outcome);
        Assert.False(result.CleanupPending, result.Detail);
        AssertVictimUntouched(before, victim, "after a recovery");
        rig.AssertTree(Generation.Old, "after the recovery");
        rig.AssertSettled(Generation.Old, "after the recovery");
    }

    [Theory]
    [InlineData("work")]
    [InlineData("prev")]
    [InlineData("next")]
    public void A_link_that_appears_while_the_swap_runs_is_unlinked_by_the_cleanup_after_the_commit_and_what_it_points_to_survives(string where)
    {
        using var rig = Rig.Create("m1-commit-" + where);
        using var links = new Links();
        SortedDictionary<string, string>? before = null;
        string? victim = null;
        var launcher = new FakeLauncher(LaunchVerdict.Ok)
        {
            OnCall = request =>
            {
                // Between the launch check and the cleanup the folder that holds this transaction's leftovers is replaced by a link to somebody's folder that has a folder of the same name.
                victim = PlantVictim(rig, request.Tx);
                before = Rig.Snapshot(victim, includeUpdate: true);
                var update = rig.Paths.Update;
                switch (where)
                {
                    case "work":
                        Directory.Move(Path.Combine(update, "work"), Path.Combine(rig.Root, "aside-work"));
                        Rig.Junction(Path.Combine(update, "work"), victim);
                        links.Add(Path.Combine(update, "work"));
                        break;
                    case "prev":
                        Directory.Move(Path.Combine(update, "prev-" + rig.Old.Version), Path.Combine(rig.Root, "aside-prev"));
                        Rig.Junction(Path.Combine(update, "prev-" + rig.Old.Version), victim);
                        links.Add(Path.Combine(update, "prev-" + rig.Old.Version));
                        break;
                    default:
                        var next = rig.Paths.Next(request.Tx);
                        Directory.Delete(next, recursive: true);
                        Rig.Junction(next, victim);
                        links.Add(next);
                        break;
                }
            },
        };

        var result = rig.Swap(launcher);

        Assert.Equal(TxOutcome.Committed, result.Outcome);
        Assert.False(result.CleanupPending, result.Detail);
        Assert.NotNull(victim);
        AssertVictimUntouched(before!, victim!, "after the cleanup of a commit");
        rig.AssertTree(Generation.New, "after the commit");
    }

    [Fact]
    public void Entries_that_are_not_transaction_folders_are_left_alone_and_the_result_says_so()
    {
        using var rig = Rig.Create("m1-names", recorded: true);
        var update = rig.Paths.Update;
        var keep = new List<string>
        {
            Path.Combine(update, "work", "not-a-transaction", "keep.txt"), // a folder of another name
            Path.Combine(update, "work", "FEDCBA987654", "keep.txt"), // 12 hex digits, but not lower-case
            Path.Combine(update, "work", "stray.txt"), // a file
            Path.Combine(update, "prev-notaversion", StaleTx, "keep.txt"), // a prev entry whose suffix is not a version
            Path.Combine(update, "prev-0.5.0", "other-name", "keep.txt"), // a folder of another name in a real prev-<version>
        };
        foreach (var path in keep)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "not ours\n");
        }

        var ours = Path.Combine(update, "work", StaleTx, "next", "x.bin"); // a real transaction folder: this one goes
        Directory.CreateDirectory(Path.GetDirectoryName(ours)!);
        File.WriteAllBytes(ours, [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(update, "prev-0.5.0", "0123456789ab"));

        var result = rig.Swap(FakeLauncher.Ok());

        Assert.Equal(TxOutcome.Committed, result.Outcome);
        foreach (var path in keep)
        {
            Assert.True(File.Exists(path), "left alone, so it must still be there: " + path);
        }

        Assert.False(Directory.Exists(Path.Combine(update, "work", StaleTx)), "a transaction folder is removed");
        Assert.False(Directory.Exists(Path.Combine(update, "prev-0.5.0", "0123456789ab")), "a transaction folder is removed");
        Assert.Contains("left alone", result.Detail, StringComparison.Ordinal);
        Assert.Contains("not-a-transaction", result.Detail, StringComparison.Ordinal);
        Assert.Contains("left alone", File.ReadAllText(rig.Paths.LastResult), StringComparison.Ordinal);
    }

    [Fact]
    public void A_recovery_without_a_journal_leaves_entries_that_are_not_transaction_folders_and_says_so()
    {
        using var rig = Rig.Create("m1-names-rec", recorded: true);
        var update = rig.Paths.Update;
        var strange = Path.Combine(update, "work", "not-a-transaction", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(strange)!);
        File.WriteAllText(strange, "not ours\n");
        var ours = Path.Combine(update, "work", StaleTx, "x.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(ours)!);
        File.WriteAllBytes(ours, [1]);

        var result = rig.Recover();

        Assert.Equal(TxOutcome.NothingToDo, result.Outcome);
        Assert.True(File.Exists(strange));
        Assert.False(Directory.Exists(Path.Combine(update, "work", StaleTx)));
        Assert.Contains("left alone", result.Detail, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- S1

    [Fact]
    public void A_swapping_state_whose_journal_is_gone_keeps_the_backup_and_sets_the_sentinel_and_a_recovery_with_the_journal_back_succeeds()
    {
        using var rig = Rig.Create("s1-nojournal");
        var cutter = new Cutter(new CutPoint(TxSteps.SwapOverwrite, TxEdge.After, 2));
        Assert.Throws<PowerCutException>(() => rig.Swap(Rig.Seams(cutter, FakeLauncher.Ok())));
        Assert.True(cutter.Fired);
        var journalBytes = File.ReadAllBytes(rig.Paths.Journal);
        Assert.Equal(TxPhase.Swapping, ReadJournal(rig).Phase);
        var halfSwapped = Rig.Snapshot(rig.Install);
        File.Delete(rig.Paths.Journal); // the journal is lost, the half-swapped files and the backup are what is left

        var result = rig.Recover();

        Assert.Equal((TxOutcome.RecoveryFailed, TxReason.SetNotRestored), (result.Outcome, result.Reason));
        Assert.True(File.Exists(rig.Paths.Sentinel), "the sentinel must be written");
        AssertBackupIntact(rig); // the backup is the only way back: it must still be whole
        rig.AssertUnchanged(halfSwapped, "a failed recovery changes no managed file");

        // The journal comes back (or a person puts the state right): the recovery goes on and succeeds.
        File.WriteAllBytes(rig.Paths.Journal, journalBytes);
        var again = rig.Recover();
        Assert.Equal(TxOutcome.RolledBack, again.Outcome);
        Assert.Equal(Generation.Old, rig.AssertEnded("after the journal was put back"));
        Assert.False(File.Exists(rig.Paths.Sentinel));
    }

    [Fact]
    public void A_state_with_leftovers_and_no_journal_whose_files_are_whole_is_tidied()
    {
        // The other side of the check above: leftovers of a cut preparation, the installed set is what installed.json says, so nothing is lost by tidying.
        using var rig = Rig.Create("s1-whole");
        var cutter = new Cutter(new CutPoint(TxSteps.JournalPrepared, TxEdge.Before, 1));
        Assert.Throws<PowerCutException>(() => rig.Swap(Rig.Seams(cutter, FakeLauncher.Ok())));
        Assert.False(File.Exists(rig.Paths.Journal));
        Assert.True(Directory.Exists(Path.Combine(rig.Paths.Update, "work")));

        var result = rig.Recover();

        Assert.Equal(TxOutcome.NothingToDo, result.Outcome);
        Assert.Equal(Generation.Old, rig.AssertEnded("after tidying the leftovers of a cut preparation"));
    }

    [Fact]
    public void An_install_that_was_never_recorded_is_held_to_its_own_manifest_before_a_stale_backup_folder_is_tidied()
    {
        // A first update that was cut before installed.json was written leaves no journal and maybe a backup folder. The install's own PCMieru.bundle.json is what the files are held to.
        using var rig = Rig.Create("s1-own-whole");
        Assert.False(File.Exists(rig.Paths.Installed));
        var backup = Path.Combine(rig.Paths.Update, "prev-0.4.0", StaleTx, "files", "y");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllBytes(backup, [4, 5, 6]);

        var result = rig.Recover();

        Assert.Equal(TxOutcome.NothingToDo, result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(rig.Paths.Update, "prev-0.4.0", StaleTx)), "the files are the manifest's, so the leftover backup is tidied");
        Assert.Equal(Generation.Old, rig.AssertEnded("after tidying the leftovers of an install that was never recorded"));
    }

    [Fact]
    public void An_install_that_was_never_recorded_and_whose_file_is_not_what_its_own_manifest_says_keeps_the_backup_and_sets_the_sentinel()
    {
        using var rig = Rig.Create("s1-own-broken");
        var backup = Path.Combine(rig.Paths.Update, "prev-0.4.0", StaleTx, "files", "y");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllBytes(backup, [4, 5, 6]);
        var name = rig.Old.AllFiles().First(f => f.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Key;
        var target = TxPaths.Under(rig.Install, name);
        File.WriteAllBytes(target, Encoding.UTF8.GetBytes("not the file the manifest names"));
        var before = Rig.Snapshot(rig.Install);

        var result = rig.Recover();

        Assert.Equal((TxOutcome.RecoveryFailed, TxReason.SetNotRestored), (result.Outcome, result.Reason));
        Assert.True(File.Exists(backup), "the backup stays");
        Assert.True(File.Exists(rig.Paths.Sentinel));
        rig.AssertUnchanged(before, "a failed recovery changes no managed file");
    }

    // ---------------------------------------------------------------------------------------------------------------------------- m3

    [Fact]
    public void A_folder_that_was_there_before_the_swap_is_not_removed_by_a_rollback_but_one_the_swap_made_is()
    {
        using var rig = Rig.Create("m3-dirs");
        var mine = Path.Combine(rig.Install, "en"); // the new version adds en\PcMieru.Core.resources.dll: this empty folder is the user's
        Directory.CreateDirectory(mine);

        var result = rig.Swap(FakeLauncher.AlwaysFail());

        Assert.Equal(TxOutcome.RolledBack, result.Outcome);
        Assert.True(Directory.Exists(mine), "an empty folder that was there before the swap stays");
        Assert.Empty(Directory.GetFileSystemEntries(mine));
        var expected = rig.Expected(Generation.Old);
        expected["en"] = "<dir>";
        var difference = Rig.Diff(expected, Rig.Snapshot(rig.Install));
        Assert.True(difference.Length == 0, difference);

        // The same swap with no such folder: the folder the swap made is removed again.
        using var other = Rig.Create("m3-dirs2");
        Assert.Equal(TxOutcome.RolledBack, other.Swap(FakeLauncher.AlwaysFail()).Outcome);
        other.AssertTree(Generation.Old, "the folder the swap made is gone again");
        Assert.False(Directory.Exists(Path.Combine(other.Install, "en")));
    }

    // ---------------------------------------------------------------------------------------------------------------------------- m2: every segment of the path

    [Theory]
    [InlineData("m2dir/x.dll", "x.dll")]       // the link is the folder right above the file
    [InlineData("m2dir/sub/x.dll", "sub/x.dll")] // the link is further up, with a real folder below it: only a look at EVERY segment finds it
    public void A_link_at_any_folder_above_an_installed_file_is_a_difference_even_when_the_file_behind_it_is_whole(string recorded, string behind)
    {
        using var rig = Rig.Create("m2-segment");
        using var links = new Links();
        var bytes = SyntheticSet.Bytes("m2", 500);
        var victim = Path.Combine(rig.Root, "m2-target");
        var inVictim = Path.Combine(victim, behind.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(inVictim)!);
        File.WriteAllBytes(inVictim, bytes);
        var link = Path.Combine(rig.Install, "m2dir");
        Rig.Junction(link, victim);
        links.Add(link);
        var file = new InstalledFile(recorded, bytes.Length, Rig.Sha(bytes));

        var finding = ManagedSet.CheckOne(rig.Paths, file, gate: null);

        Assert.NotNull(finding);
        Assert.Equal(new SetFinding(recorded, FindingKind.Link), finding!.Value);
    }

    [Fact]
    public void A_real_nested_folder_with_the_whole_file_is_not_a_difference()
    {
        using var rig = Rig.Create("m2-real");
        var bytes = SyntheticSet.Bytes("m2r", 500);
        var path = Path.Combine(rig.Install, "ja", "sub", "x.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);

        Assert.Null(ManagedSet.CheckOne(rig.Paths, new InstalledFile("ja/sub/x.dll", bytes.Length, Rig.Sha(bytes)), gate: null));
    }
}
