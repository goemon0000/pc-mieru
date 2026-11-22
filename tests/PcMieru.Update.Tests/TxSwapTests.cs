using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PcMieru.Update.Bundle;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Tests;

/// <summary>
/// The swap transaction (DESIGN-B 5, step 4) on a synthetic release in scratch folders: the happy path, every abort before the journal exists, every rollback, and what the user's own files go through
/// (byte for byte the same, always). The cuts of the power are in <see cref="TxFaultMatrixTests"/>; the recovery by phase is in <see cref="TxRecoveryTests"/>.
/// </summary>
public sealed class TxSwapTests
{
    // ---------------------------------------------------------------------------------------------------------------------------- helpers

    internal static TxJournal ReadJournal(Rig rig)
    {
        Assert.True(TxJournal.TryParse(File.ReadAllBytes(rig.Paths.Journal), out var journal, out var error), "journal.json must parse: " + error);
        return journal;
    }

    internal static (string Outcome, string Reason, string Kind) ReadLastResult(Rig rig)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(rig.Paths.LastResult));
        var root = document.RootElement;
        Assert.Equal(LastResult.Format, root.GetProperty("format").GetString());
        return (root.GetProperty("outcome").GetString()!, root.GetProperty("reason").GetString()!, root.GetProperty("kind").GetString()!);
    }

    internal static string PrevFolder(Rig rig)
    {
        var tx = Directory.GetDirectories(Path.Combine(rig.Paths.Update, "prev-" + rig.Old.Version)).Single();
        return Path.Combine(tx, "files");
    }

    /// <summary>The backup, proven by hashes: every non-gate file of the old record is there with its length and SHA-256, and so is the old installed.json.</summary>
    internal static void AssertBackupIntact(Rig rig)
    {
        var tx = Directory.GetDirectories(Path.Combine(rig.Paths.Update, "prev-" + rig.Old.Version)).Single();
        foreach (var (name, data) in rig.Old.AllFiles().Where(f => !GateFile.IsExcludedFromSwap(f.Key)))
        {
            var path = TxPaths.Under(Path.Combine(tx, "files"), name);
            Assert.True(File.Exists(path), $"the backup lacks '{name}'");
            Assert.Equal(Rig.Sha(data), Rig.Sha(File.ReadAllBytes(path)));
        }

        Assert.Equal(rig.Old.Record().ToUtf8Bytes(), File.ReadAllBytes(Path.Combine(tx, "installed.json")));
    }

    private static void AssertNothingLeft(Rig rig, string context)
    {
        var update = rig.Paths.Update;
        Assert.False(File.Exists(rig.Paths.Journal), context + ": journal.json is left");
        Assert.False(File.Exists(rig.Paths.Sentinel), context + ": the sentinel is left");
        Assert.False(File.Exists(rig.Paths.Note), context + ": RECOVER.txt is left");
        Assert.False(Directory.Exists(Path.Combine(update, "work")), context + ": work/ is left");
        Assert.Empty(Directory.Exists(update) ? Directory.GetDirectories(update, "prev-*") : []);
    }

    private static void AssertAborted(Rig rig, TxResult result, TxReason reason, string context)
    {
        Assert.Equal(TxOutcome.Aborted, result.Outcome);
        Assert.Equal(reason, result.Reason);
        AssertNothingLeft(rig, context);
        Assert.False(string.IsNullOrWhiteSpace(TxReasonText.Ja(reason)), "the reason needs a Japanese text");
        Assert.False(string.IsNullOrWhiteSpace(TxReasonText.En(reason)), "the reason needs an English text");
        var last = ReadLastResult(rig);
        Assert.Equal("aborted", last.Outcome);
        Assert.Equal(LastResult.Camel(reason.ToString()), last.Reason);
        Assert.Equal(TxOutcome.NothingToDo, rig.Recover().Outcome); // nothing is open: a recovery finds nothing to do
    }

    private static void AssertTreeIs(SortedDictionary<string, string> expected, Rig rig, string context)
    {
        var difference = Rig.Diff(expected, Rig.Snapshot(rig.Install));
        Assert.True(difference.Length == 0, $"{context}: {difference}");
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the happy path

    [Fact]
    public void A_manually_unzipped_folder_is_swapped_and_the_new_set_is_all_new()
    {
        using var rig = Rig.Create("happy");
        var launcher = FakeLauncher.Ok();

        var result = rig.Swap(launcher);

        Assert.Equal(TxOutcome.Committed, result.Outcome);
        Assert.False(result.CleanupPending, result.Detail);
        Assert.Equal("0.5.0", result.FromVersion);
        Assert.Equal("0.5.1", result.ToVersion);
        rig.AssertTree(Generation.New, "after the commit");
        rig.AssertSettled(Generation.New, "after the commit");
        AssertNothingLeft(rig, "after the commit");
        Assert.Equal(("committed", "none", "swap"), ReadLastResult(rig));
        var request = Assert.Single(launcher.Calls);
        Assert.Equal((1, 3, "0.5.1", SyntheticSet.NewBuild), (request.Attempt, request.MaxAttempts, request.Version, request.BuildId));
        Assert.False(File.Exists(rig.Paths.Blocked), "a good update leaves no failure tag");
        Assert.Equal(TxOutcome.NothingToDo, rig.Recover().Outcome);
    }

    [Fact]
    public void A_second_update_starts_from_installed_json_and_commits()
    {
        using var rig = Rig.Create("twice");
        Assert.Equal(TxOutcome.Committed, rig.Swap(FakeLauncher.Ok()).Outcome);

        // A third generation: the new one with two files changed and one added.
        var third = new SyntheticSet("0.5.2", "33333333-3333-4333-8333-333333333333");
        foreach (var (name, data) in rig.New.Files)
        {
            third.Files[name] = data;
        }

        third.Files["stable.dll"] = SyntheticSet.Bytes("stable-3", 5100);
        third.Files["PCMieru.exe"] = SyntheticSet.Bytes("exe-3", 3200);
        third.Files["ja/more.resources.dll"] = SyntheticSet.Bytes("more", 700);
        var staged = Path.Combine(rig.Root, "staged3");
        third.WriteTo(staged);

        using var gate = rig.Enter();
        var result = SwapTransaction.Run(gate, new SwapRequest(staged), Rig.Seams(launcher: FakeLauncher.Ok()));

        Assert.Equal((TxOutcome.Committed, "0.5.1", "0.5.2"), (result.Outcome, result.FromVersion, result.ToVersion));
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, data) in third.AllFiles())
        {
            expected[name] = Rig.Sha(data);
        }

        foreach (var (name, data) in rig.UserFiles)
        {
            expected[name] = Rig.Sha(data);
        }

        foreach (var directory in rig.UserDirs.Append("en").Append("ja").Append("plugins").Append("plugins/deep"))
        {
            expected[directory] = "<dir>";
        }

        AssertTreeIs(expected, rig, "after the second update");
        Assert.Equal(third.Record().ToUtf8Bytes(), File.ReadAllBytes(rig.Paths.Installed));
    }

    [Fact]
    public void The_gate_file_is_never_swapped_and_keeps_its_identity_and_bytes()
    {
        using var rig = Rig.Create("gate");
        var before = FileIdentity.Of(rig.Paths.Gate);
        Assert.NotNull(before);

        var touched = new List<string>();
        var recorder = new HookInjector((step, edge, item) =>
        {
            if (item is not null && item.EndsWith(GateFile.FileName, StringComparison.OrdinalIgnoreCase))
            {
                touched.Add($"{step.Name}/{edge}");
            }

            if (step == TxSteps.JournalPrepared && edge == TxEdge.Before)
            {
                // The backup and the staging copy hold every managed file but the gate file.
                Assert.Empty(Directory.GetFiles(rig.Paths.Update, GateFile.FileName, SearchOption.AllDirectories));
            }
        });
        var result = rig.Swap(Rig.Seams(recorder, FakeLauncher.Ok()));

        Assert.Equal(TxOutcome.Committed, result.Outcome);
        Assert.Empty(touched); // no step ever named the gate file
        var after = FileIdentity.Of(rig.Paths.Gate);
        Assert.NotNull(after);
        Assert.True(after.Value.IsSameFile(before.Value), "the gate file must be the same file (volume and index) after the swap");
        Assert.Equal(GateFile.Bytes(), File.ReadAllBytes(rig.Paths.Gate));
    }

    [Fact]
    public void The_recovery_exe_and_the_note_are_in_place_before_the_first_change_and_the_note_is_cleaned_after_the_commit()
    {
        using var rig = Rig.Create("runner");
        var seen = new List<string>();
        var hook = new HookInjector((step, edge, _) =>
        {
            if (step == TxSteps.JournalPrepared && edge == TxEdge.Before)
            {
                // Everything the transaction needs to recover is there, and not one managed file has changed.
                seen.Add("prepared");
                Assert.True(File.Exists(rig.Paths.Recover));
                Assert.Equal(Rig.Sha(rig.Old.Files["PCMieru.Swap.exe"]), Rig.Sha(File.ReadAllBytes(rig.Paths.Recover)));
                var note = File.ReadAllText(rig.Paths.Note);
                Assert.Contains("recover.exe", note, StringComparison.Ordinal);
                Assert.Contains("ZIP", note, StringComparison.Ordinal);
                Assert.Contains("新しいフォルダー", note, StringComparison.Ordinal);
                Assert.Contains("new folder", note, StringComparison.Ordinal);
                Assert.DoesNotContain("http", note, StringComparison.OrdinalIgnoreCase);
                rig.AssertTree(Generation.Old, "before the journal");
            }
        });

        var result = rig.Swap(Rig.Seams(hook, FakeLauncher.Ok()));

        Assert.Equal(TxOutcome.Committed, result.Outcome);
        Assert.Equal(["prepared"], seen);
        Assert.True(File.Exists(rig.Paths.Recover), "recover.exe stays (it is hash-checked before every use)");
        Assert.False(File.Exists(rig.Paths.Note));
    }

    [Fact]
    public void Every_rename_is_preceded_by_its_intent_in_the_journal_on_the_disk()
    {
        using var rig = Rig.Create("intent");
        var checkedRenames = 0;
        var hook = new HookInjector((step, edge, item) =>
        {
            if (edge != TxEdge.Before || (step != TxSteps.SwapAdd && step != TxSteps.SwapOverwrite && step != TxSteps.SwapDisplace))
            {
                return;
            }

            // The journal the process would leave behind if the power went now: it must already name this file, as "intent", and with the intent of THIS kind of change.
            var journal = ReadJournal(rig);
            var working = journal.Ops.Where(o => o.State == TxOpState.Intent).ToList();
            var op = Assert.Single(working);
            Assert.EndsWith(op.Path.Replace('/', '\\'), item!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(step == TxSteps.SwapAdd ? TxOpIntent.Add : step == TxSteps.SwapOverwrite ? TxOpIntent.Overwrite : TxOpIntent.Remove, op.Intent);
            Assert.Equal(TxPhase.Swapping, journal.Phase);
            checkedRenames++;
        });

        var result = rig.Swap(Rig.Seams(hook, FakeLauncher.Ok()));

        Assert.Equal(TxOutcome.Committed, result.Outcome);
        Assert.Equal(8, checkedRenames); // 2 adds + 5 overwrites (the exe, the swap exe, the library, the notice, the manifest) + 1 remove
    }

    // ---------------------------------------------------------------------------------------------------------------------------- aborts before the journal

    [Fact]
    public void A_manually_unzipped_folder_whose_file_differs_from_its_manifest_is_not_swapped_and_says_what_the_user_does()
    {
        using var rig = Rig.Create("firstdiff");
        var edited = Encoding.UTF8.GetBytes("edited by the user");
        File.WriteAllBytes(Path.Combine(rig.Install, "stable.dll"), edited);
        var expected = rig.Expected(Generation.Old);
        expected["stable.dll"] = Rig.Sha(edited);

        var result = rig.Swap(FakeLauncher.Ok());

        AssertAborted(rig, result, TxReason.InstalledFilesChanged, "first record, a changed file");
        AssertTreeIs(expected, rig, "nothing was touched");
        Assert.False(File.Exists(rig.Paths.Installed), "an unverified folder gets no installed.json");
        Assert.Contains("stable.dll", result.Detail, StringComparison.Ordinal);
        // What the user sees: a plain sentence that sends them to a new folder (and does not say "journal" or "hash").
        Assert.Contains("新しいフォルダー", TxReasonText.Ja(TxReason.InstalledFilesChanged), StringComparison.Ordinal);
        Assert.Contains("unzip it into a new folder", TxReasonText.En(TxReason.InstalledFilesChanged), StringComparison.Ordinal);
    }

    [Fact]
    public void A_manually_unzipped_folder_with_a_missing_file_is_not_swapped()
    {
        using var rig = Rig.Create("firstmissing");
        File.Delete(Path.Combine(rig.Install, "NOTICE.txt"));
        var expected = rig.Expected(Generation.Old);
        expected.Remove("NOTICE.txt");

        var result = rig.Swap(FakeLauncher.Ok());

        AssertAborted(rig, result, TxReason.InstalledFilesChanged, "first record, a missing file");
        AssertTreeIs(expected, rig, "nothing was touched");
    }

    [Fact]
    public void A_folder_with_neither_installed_json_nor_a_manifest_is_not_swapped()
    {
        using var rig = Rig.Create("norecord");
        File.Delete(Path.Combine(rig.Install, BundleFormat.InnerManifestName));
        var expected = rig.Expected(Generation.Old);
        expected.Remove(BundleFormat.InnerManifestName);

        var result = rig.Swap(FakeLauncher.Ok());

        AssertAborted(rig, result, TxReason.NoRecord, "no record");
        AssertTreeIs(expected, rig, "nothing was touched");
    }

    [Fact]
    public void An_old_file_that_differs_from_installed_json_is_not_swapped()
    {
        using var rig = Rig.Create("recdiff", recorded: true);
        var edited = Encoding.UTF8.GetBytes("edited later");
        File.WriteAllBytes(Path.Combine(rig.Install, "PcMieru.Core.dll"), edited);
        var expected = rig.Expected(Generation.Old);
        expected["PcMieru.Core.dll"] = Rig.Sha(edited);

        var result = rig.Swap(FakeLauncher.Ok());

        AssertAborted(rig, result, TxReason.InstalledFilesChanged, "installed.json, a changed file");
        AssertTreeIs(expected, rig, "nothing was touched");
        Assert.Equal(rig.Old.Record().ToUtf8Bytes(), File.ReadAllBytes(rig.Paths.Installed));
    }

    public static TheoryData<string, string, bool> Collisions => new()
    {
        { "new-feature.dll", "a file the new version also wants", false },
        { "en", "a FILE where the new version needs a folder", false },
        { "new-feature.dll", "a FOLDER where the new version needs a file", true },
    };

    [Theory]
    [MemberData(nameof(Collisions))]
    public void A_new_name_taken_by_something_of_unknown_owner_stops_the_swap_and_leaves_it_alone(string name, string why, bool asFolder)
    {
        using var rig = Rig.Create("collide");
        var path = TxPaths.Under(rig.Install, name);
        var content = Encoding.UTF8.GetBytes("the user's own file: " + why);
        var expected = rig.Expected(Generation.Old);
        if (asFolder)
        {
            Directory.CreateDirectory(path);
            expected[name] = "<dir>";
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            expected[name] = Rig.Sha(content);
        }

        var result = rig.Swap(FakeLauncher.Ok());

        AssertAborted(rig, result, TxReason.NameCollision, why);
        AssertTreeIs(expected, rig, "the planted thing and everything else is untouched");
        if (!asFolder)
        {
            Assert.Equal(content, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void The_free_space_is_counted_before_anything_is_copied_and_a_short_disk_stops_the_swap()
    {
        using var rig = Rig.Create("space");
        var asked = 0;
        var disk = new FixedDisk(1000)
        {
            OnAsk = () =>
            {
                asked++;
                Assert.False(Directory.Exists(Path.Combine(rig.Paths.Update, "work")), "nothing is copied before the space is counted");
                Assert.Empty(Directory.GetDirectories(rig.Paths.Update, "prev-*"));
            },
        };

        var result = rig.Swap(Rig.Seams(launcher: FakeLauncher.Ok(), disk: disk));

        Assert.Equal(1, asked);
        AssertAborted(rig, result, TxReason.DiskFull, "short disk");
        rig.AssertTree(Generation.Old, "a short disk");
        Assert.Contains("free some space", TxReasonText.En(TxReason.DiskFull), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_space_estimate_is_exact_enough_to_pass_with_what_it_asks_for_and_fail_one_byte_below()
    {
        long need;
        using (var probe = Rig.Create("spaceprobe"))
        {
            var result = probe.Swap(Rig.Seams(launcher: FakeLauncher.Ok(), disk: new FixedDisk(0)));
            var match = Regex.Match(result.Detail, @"^(\d+) bytes are needed and 0 are free$");
            Assert.True(match.Success, result.Detail);
            need = long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(need > 200_000 + 210_000, "the estimate covers the new files and the full backup");
        }

        using (var enough = Rig.Create("spaceenough"))
        {
            Assert.Equal(TxOutcome.Committed, enough.Swap(Rig.Seams(launcher: FakeLauncher.Ok(), disk: new FixedDisk(need))).Outcome);
        }

        using (var short1 = Rig.Create("spaceshort"))
        {
            Assert.Equal(TxReason.DiskFull, short1.Swap(Rig.Seams(launcher: FakeLauncher.Ok(), disk: new FixedDisk(need - 1))).Reason);
        }
    }

    [Fact]
    public void A_disk_that_fills_up_in_the_middle_of_a_copy_stops_the_swap_without_a_trace()
    {
        using var rig = Rig.Create("diskfull");
        var fail = new FailOnce(new CutPoint(TxSteps.BackupCopy, TxEdge.Mid, 3));

        var result = rig.Swap(Rig.Seams(fail, FakeLauncher.Ok()));

        Assert.True(fail.Fired);
        AssertAborted(rig, result, TxReason.IoFailure, "I/O error in the middle of a backup copy");
        rig.AssertTree(Generation.Old, "an I/O error before the journal");
    }

    [Fact]
    public void A_recovery_exe_that_cannot_be_placed_stops_the_swap_before_the_journal()
    {
        using var rig = Rig.Create("noplace");

        var result = rig.Swap(Rig.Seams(launcher: FakeLauncher.Ok(), placement: new FailingPlacement(new RunnerUnavailableException("PCMieru.Swap.exe is not usable"))));

        AssertAborted(rig, result, TxReason.RunnerUnavailable, "the runner cannot be placed");
        rig.AssertTree(Generation.Old, "the runner cannot be placed");
    }

    [Fact]
    public void A_record_without_the_swap_exe_cannot_place_the_recovery_exe()
    {
        using var rig = Rig.Create("noswapexe", shape: (old, next) =>
        {
            old.Files.Remove("PCMieru.Swap.exe");
            next.Files.Remove("PCMieru.Swap.exe");
        });

        var result = rig.Swap(FakeLauncher.Ok());

        AssertAborted(rig, result, TxReason.RunnerUnavailable, "no swap exe in the record");
        rig.AssertTree(Generation.Old, "no swap exe in the record");
    }

    [Fact]
    public void A_backup_that_does_not_read_back_whole_stops_the_swap_before_the_journal()
    {
        using var rig = Rig.Create("backupbad");
        var hook = new HookInjector((step, edge, _) =>
        {
            if (step == TxSteps.BackupRecord && edge == TxEdge.After)
            {
                var backup = Directory.GetFiles(Path.Combine(rig.Paths.Update, "prev-0.5.0"), "stable.dll", SearchOption.AllDirectories).Single();
                var bytes = File.ReadAllBytes(backup);
                bytes[10] ^= 0xFF; // the backup of a file rots between the copy and the check
                File.WriteAllBytes(backup, bytes);
            }
        });

        var result = rig.Swap(Rig.Seams(hook, FakeLauncher.Ok()));

        AssertAborted(rig, result, TxReason.BackupIncomplete, "a rotten backup");
        rig.AssertTree(Generation.Old, "a rotten backup");
        Assert.Contains("stable.dll", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_staged_file_that_is_not_what_its_manifest_says_stops_the_swap()
    {
        using var rig = Rig.Create("stagedbad");
        var staged = Path.Combine(rig.Staged, "NOTICE.txt");
        var bytes = File.ReadAllBytes(staged);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(staged, bytes);

        var result = rig.Swap(FakeLauncher.Ok());

        AssertAborted(rig, result, TxReason.StagedMismatch, "a bad staged file");
        rig.AssertTree(Generation.Old, "a bad staged file");
    }

    [Fact]
    public void A_staged_folder_without_a_manifest_stops_the_swap()
    {
        using var rig = Rig.Create("stagednomanifest");
        File.Delete(Path.Combine(rig.Staged, BundleFormat.InnerManifestName));

        var result = rig.Swap(FakeLauncher.Ok());

        AssertAborted(rig, result, TxReason.StagedUnreadable, "no staged manifest");
        rig.AssertTree(Generation.Old, "no staged manifest");
    }

    [Fact]
    public void The_same_build_is_not_swapped()
    {
        using var rig = Rig.Create("samebuild");
        Directory.Delete(rig.Staged, recursive: true);
        rig.Old.WriteTo(rig.Staged);

        var result = rig.Swap(FakeLauncher.Ok());

        AssertAborted(rig, result, TxReason.SameBuild, "the same build");
        rig.AssertTree(Generation.Old, "the same build");
    }

    [Fact]
    public void A_staged_generation_with_another_gate_file_is_not_swapped()
    {
        using var rig = Rig.Create("othergate");
        rig.New.Files[GateFile.FileName] = Encoding.ASCII.GetBytes("another gate");
        Directory.Delete(rig.Staged, recursive: true);
        rig.New.WriteTo(rig.Staged);

        var result = rig.Swap(FakeLauncher.Ok());

        AssertAborted(rig, result, TxReason.StagedMismatch, "another gate file");
        rig.AssertTree(Generation.Old, "another gate file");
    }

    [Theory]
    [InlineData("journal.json")]
    [InlineData("recovery-failed")]
    public void An_open_journal_or_a_sentinel_stops_a_new_swap_and_is_left_as_it_is(string name)
    {
        using var rig = Rig.Create("open");
        Directory.CreateDirectory(rig.Paths.Update);
        var path = Path.Combine(rig.Paths.Update, name);
        var content = Encoding.ASCII.GetBytes("something an earlier run left");
        File.WriteAllBytes(path, content);

        var result = rig.Swap(FakeLauncher.Ok());

        Assert.Equal((TxOutcome.Aborted, TxReason.JournalPresent), (result.Outcome, result.Reason));
        Assert.Equal(content, File.ReadAllBytes(path));
        rig.AssertTree(Generation.Old, "an open journal");
        Assert.False(Directory.Exists(Path.Combine(rig.Paths.Update, "work")));
    }

    [Fact]
    public void A_gate_that_is_not_held_stops_the_swap_at_once()
    {
        using var rig = Rig.Create("nogate");
        using var someoneElse = new FileStream(rig.Paths.Gate, FileMode.Open, FileAccess.Read, FileShare.Read); // a program of the install is running
        using var gate = rig.Enter();
        Assert.Equal(GateOutcome.Busy, gate.Outcome);

        var result = SwapTransaction.Run(gate, new SwapRequest(rig.Staged), Rig.Seams(launcher: FakeLauncher.Ok()));

        Assert.Equal((TxOutcome.Aborted, TxReason.GateLost), (result.Outcome, result.Reason));
        Assert.False(Directory.Exists(rig.Paths.Update));
        rig.AssertTree(Generation.Old, "no gate");
    }

    [Fact]
    public void An_update_folder_that_is_a_junction_stops_the_swap()
    {
        using var rig = Rig.Create("junction");
        var elsewhere = Path.Combine(rig.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Rig.Junction(rig.Paths.Update, elsewhere);
        try
        {
            var result = rig.Swap(FakeLauncher.Ok());

            Assert.Equal((TxOutcome.Aborted, TxReason.UpdateFolderUnsafe), (result.Outcome, result.Reason));
            Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
            rig.AssertTree(Generation.Old, "a junction as update");
        }
        finally
        {
            Directory.Delete(rig.Paths.Update); // the junction only
        }
    }

    [Fact]
    public void A_swap_needs_a_launcher_in_the_seams()
    {
        using var rig = Rig.Create("nolauncher");
        using var gate = rig.Enter();

        Assert.Throws<ArgumentException>(() => SwapTransaction.Run(gate, new SwapRequest(rig.Staged), Rig.Seams()));
        rig.AssertTree(Generation.Old, "no launcher");
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the verify loop and rollbacks

    [Fact]
    public void A_new_version_that_never_starts_is_rolled_back_after_three_counted_attempts_and_the_user_files_are_byte_identical()
    {
        using var rig = Rig.Create("rollback3");
        var counted = new List<int>();
        var launcher = new FakeLauncher(LaunchVerdict.Failed)
        {
            OnCall = request =>
            {
                // The attempt is on the disk BEFORE the launch.
                var journal = ReadJournal(rig);
                Assert.Equal(TxPhase.VerifyPending, journal.Phase);
                counted.Add(journal.LaunchAttempts);
                Assert.Equal(request.Attempt, journal.LaunchAttempts);
                rig.AssertTree(Generation.New, "while the new version is being launched");
            },
        };

        var result = rig.Swap(launcher);

        Assert.Equal((TxOutcome.RolledBack, TxReason.LaunchFailed, TxTag.LaunchFailed), (result.Outcome, result.Reason, result.Tag));
        Assert.Equal([1, 2, 3], counted);
        Assert.Equal(3, launcher.Calls.Count);
        rig.AssertTree(Generation.Old, "after the rollback");
        rig.AssertSettled(Generation.Old, "after the rollback");
        AssertNothingLeft(rig, "after the rollback");
        Assert.Equal(("rolledBack", "launchFailed", "swap"), ReadLastResult(rig));
        var tags = BlockedTags.Read(rig.Paths.Blocked);
        var tag = Assert.Single(tags);
        Assert.Equal((SyntheticSet.NewBuild, "0.5.1", TxTag.LaunchFailed), (tag.BuildId, tag.Version, tag.Reason));
        Assert.Equal(rig.Old.Record().ToUtf8Bytes(), File.ReadAllBytes(rig.Paths.Installed));
    }

    [Fact]
    public void A_launch_that_fails_twice_and_then_works_is_kept()
    {
        using var rig = Rig.Create("launch3rd");
        var launcher = new FakeLauncher(LaunchVerdict.Failed, LaunchVerdict.Failed, LaunchVerdict.Ok);

        var result = rig.Swap(launcher);

        Assert.Equal(TxOutcome.Committed, result.Outcome);
        Assert.Equal(3, launcher.Calls.Count);
        rig.AssertTree(Generation.New, "kept on the third attempt");
    }

    [Fact]
    public void A_launch_seam_that_throws_counts_as_a_failed_launch()
    {
        using var rig = Rig.Create("launchthrows");
        var launcher = new ThrowingLauncher();

        var result = rig.Swap(launcher);

        Assert.Equal((TxOutcome.RolledBack, TxReason.LaunchFailed), (result.Outcome, result.Reason));
        Assert.Equal(3, launcher.Calls);
        rig.AssertTree(Generation.Old, "the launch seam failed three times");
    }

    private sealed class ThrowingLauncher : ILaunchVerifier
    {
        public int Calls { get; private set; }

        public LaunchVerdict LaunchAndVerify(LaunchRequest request)
        {
            Calls++;
            throw new InvalidOperationException("the launch seam is broken");
        }
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("revert")]
    public void A_new_set_that_does_not_hold_before_a_launch_is_rolled_back_without_launching_again(string how)
    {
        using var rig = Rig.Create("setmismatch");
        var launcher = new FakeLauncher(LaunchVerdict.Failed)
        {
            OnCall = _ =>
            {
                // Between two attempts something changes a file of the new set: a missing added file, or an overwritten file put back to the old content.
                if (how == "delete")
                {
                    File.Delete(Path.Combine(rig.Install, "new-feature.dll"));
                }
                else
                {
                    File.WriteAllBytes(Path.Combine(rig.Install, "PCMieru.exe"), rig.Old.Files["PCMieru.exe"]);
                }
            },
        };

        var result = rig.Swap(launcher);

        Assert.Equal((TxOutcome.RolledBack, TxReason.SetMismatch, TxTag.GenerationMismatch), (result.Outcome, result.Reason, result.Tag));
        Assert.Single(launcher.Calls); // the second attempt is never made: the set is re-hashed first
        rig.AssertTree(Generation.Old, "after the rollback");
        rig.AssertSettled(Generation.Old, "after the rollback");
        Assert.Equal(TxTag.GenerationMismatch, Assert.Single(BlockedTags.Read(rig.Paths.Blocked)).Reason);
    }

    [Fact]
    public void A_new_file_that_is_neither_old_nor_new_makes_the_rollback_stop_and_keeps_the_backup()
    {
        using var rig = Rig.Create("neither");
        var garbage = Encoding.UTF8.GetBytes("neither the old nor the new file");
        var launcher = new FakeLauncher(LaunchVerdict.Failed) { OnCall = _ => File.WriteAllBytes(Path.Combine(rig.Install, "PcMieru.Core.dll"), garbage) };

        var result = rig.Swap(launcher);

        Assert.Equal((TxOutcome.RecoveryFailed, TxReason.FileNeitherHash), (result.Outcome, result.Reason));
        Assert.False(result.InstallUsable);
        Assert.True(File.Exists(rig.Paths.Sentinel));
        Assert.True(File.Exists(rig.Paths.Journal));
        AssertBackupIntact(rig);
        Assert.Equal(garbage, File.ReadAllBytes(Path.Combine(rig.Install, "PcMieru.Core.dll"))); // not touched: it is nobody's known file
        Assert.Equal(("recoveryFailed", "fileNeitherHash", "swap"), ReadLastResult(rig));
        using var start = InstallGate.EnterStartup(rig.ExePath);
        Assert.Equal(GateOutcome.RecoveryFailed, start.Outcome); // the start gate says "recovery failed"
    }

    [Fact]
    public void A_handle_held_on_a_file_that_is_replaced_rolls_the_swap_back()
    {
        using var rig = Rig.Create("sharing-overwrite");
        TxResult result;
        using (new FileStream(Path.Combine(rig.Install, "PcMieru.Core.dll"), FileMode.Open, FileAccess.Read, FileShare.Read)) // an antivirus scan, a preview pane
        {
            result = rig.Swap(FakeLauncher.Ok());

            Assert.Equal((TxOutcome.RolledBack, TxReason.SwapIoFailure), (result.Outcome, result.Reason));
            rig.AssertTree(Generation.Old, "a sharing violation on an overwrite, rolled back while the handle is still held");
        }

        AssertNothingLeft(rig, "after the rollback");
        Assert.Equal(("rolledBack", "swapIoFailure", "swap"), ReadLastResult(rig));
        Assert.Empty(BlockedTags.Read(rig.Paths.Blocked)); // the new version did not fail: the disk did; no tag
        Assert.Equal(TxOutcome.Committed, rig.Swap(FakeLauncher.Ok()).Outcome); // the handle is gone: the same update goes through
        rig.AssertTree(Generation.New, "the retry");
    }

    [Fact]
    public void A_handle_held_on_a_file_that_is_removed_rolls_the_swap_back()
    {
        using var rig = Rig.Create("sharing-remove");
        using (new FileStream(Path.Combine(rig.Install, "old-only.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = rig.Swap(FakeLauncher.Ok());

            Assert.Equal((TxOutcome.RolledBack, TxReason.SwapIoFailure), (result.Outcome, result.Reason));
            rig.AssertTree(Generation.Old, "a sharing violation on a removal");
        }

        AssertNothingLeft(rig, "after the rollback");
    }

    [Fact]
    public void A_file_the_user_edits_during_the_swap_is_never_displaced_or_overwritten()
    {
        using var rig = Rig.Create("edited-during");
        var edited = Encoding.UTF8.GetBytes("edited while the swap runs");
        var hook = new HookInjector((step, edge, _) =>
        {
            if (step == TxSteps.JournalSwapping && edge == TxEdge.After)
            {
                File.WriteAllBytes(Path.Combine(rig.Install, "old-only.dll"), edited);
            }
        });

        var result = rig.Swap(Rig.Seams(hook, FakeLauncher.Ok()));

        // The swap stops at the edited file (it is not the old one any more, so it is neither displaced nor replaced) and the rollback, which decides by real hashes, cannot prove a file that is neither the old
        // nor the new one: it stops there too, as the brief says (item 7). The user's bytes are what they wrote, the journal, the backup and the sentinel stay, and the start gate says "recovery failed".
        Assert.Equal((TxOutcome.RecoveryFailed, TxReason.FileNeitherHash), (result.Outcome, result.Reason));
        Assert.Equal(edited, File.ReadAllBytes(Path.Combine(rig.Install, "old-only.dll")));
        Assert.DoesNotContain(Directory.GetFiles(rig.Paths.Update, "old-only.dll", SearchOption.AllDirectories), p => p.Contains("displaced", StringComparison.Ordinal));
        Assert.True(File.Exists(rig.Paths.Journal));
        Assert.True(File.Exists(rig.Paths.Sentinel));
        AssertBackupIntact(rig);
        using var start = InstallGate.EnterStartup(rig.ExePath);
        Assert.Equal(GateOutcome.RecoveryFailed, start.Outcome);
    }

    [Fact]
    public void A_name_that_a_user_file_takes_after_the_preparation_is_left_alone_and_the_rest_is_rolled_back()
    {
        using var rig = Rig.Create("late-collision");
        var mine = Encoding.UTF8.GetBytes("my file, made while the update runs");
        var hook = new HookInjector((step, edge, _) =>
        {
            if (step == TxSteps.JournalSwapping && edge == TxEdge.After)
            {
                File.WriteAllBytes(Path.Combine(rig.Install, "new-feature.dll"), mine);
            }
        });

        var result = rig.Swap(Rig.Seams(hook, FakeLauncher.Ok()));

        Assert.Equal(TxOutcome.RolledBack, result.Outcome);
        var expected = rig.Expected(Generation.Old);
        expected["new-feature.dll"] = Rig.Sha(mine);
        AssertTreeIs(expected, rig, "the user's file under the wanted name is untouched, the managed set is old");
        Assert.Equal(mine, File.ReadAllBytes(Path.Combine(rig.Install, "new-feature.dll")));
    }

    [Fact]
    public void An_added_file_that_the_user_replaced_after_the_swap_is_not_deleted_by_the_rollback()
    {
        using var rig = Rig.Create("replaced-added");
        var mine = Encoding.UTF8.GetBytes("my version of an added file");
        var launcher = new FakeLauncher(LaunchVerdict.Failed) { OnCall = _ => File.WriteAllBytes(Path.Combine(rig.Install, "en", "PcMieru.Core.resources.dll"), mine) };

        var result = rig.Swap(launcher);

        Assert.Equal(TxOutcome.RolledBack, result.Outcome);
        Assert.Equal(mine, File.ReadAllBytes(Path.Combine(rig.Install, "en", "PcMieru.Core.resources.dll")));
        var expected = rig.Expected(Generation.Old);
        expected["en"] = "<dir>";
        expected["en/PcMieru.Core.resources.dll"] = Rig.Sha(mine);
        AssertTreeIs(expected, rig, "only the user's file is extra");
    }

    [Fact]
    public void The_installed_json_is_the_new_record_only_when_the_whole_new_set_is_in_place()
    {
        using var rig = Rig.Create("record-order");
        var seen = new List<string>();
        var hook = new HookInjector((step, edge, _) =>
        {
            if (step == TxSteps.SwapInstalled && edge == TxEdge.Before)
            {
                // Right before the record changes, every file of the new set is already in place and installed.json is still the old record.
                Assert.Equal(rig.Old.Record().ToUtf8Bytes(), File.ReadAllBytes(rig.Paths.Installed));
                foreach (var (name, data) in rig.New.AllFiles().Where(f => !GateFile.IsExcludedFromSwap(f.Key))) // the gate file is held by the swapper itself and is never swapped
                {
                    Assert.Equal(Rig.Sha(data), Rig.Sha(File.ReadAllBytes(TxPaths.Under(rig.Install, name))));
                }

                seen.Add("checked");
            }
        });

        Assert.Equal(TxOutcome.Committed, rig.Swap(Rig.Seams(hook, FakeLauncher.Ok())).Outcome);
        Assert.Equal(["checked"], seen);
    }

    [Fact]
    public void The_note_texts_and_the_ja_en_reason_texts_are_plain_and_never_name_a_url()
    {
        foreach (var reason in Enum.GetValues<TxReason>().Where(r => r != TxReason.None))
        {
            var ja = TxReasonText.Ja(reason);
            var en = TxReasonText.En(reason);
            Assert.False(string.IsNullOrWhiteSpace(ja), $"no Japanese text for {reason}");
            Assert.False(string.IsNullOrWhiteSpace(en), $"no English text for {reason}");
            Assert.DoesNotContain("http", ja + en, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("journal", ja + en, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SHA", ja + en, StringComparison.Ordinal);
        }

        var note = RecoverNote.Text("0.5.0", "0.5.1");
        Assert.Contains("recover.exe", note, StringComparison.Ordinal);
        Assert.DoesNotContain("http", note, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Encoding.UTF8.GetString([0xEF, 0xBB, 0xBF]), Encoding.UTF8.GetString(RecoverNote.Bytes("0.5.0", null)), StringComparison.Ordinal);
    }
}
