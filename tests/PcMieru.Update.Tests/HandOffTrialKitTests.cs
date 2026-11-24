using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using PcMieru.Update.Bundle;
using PcMieru.Update.Transaction;

namespace PcMieru.Update.Tests;

// The plain tests of the helpers the real trial of the update hand-off stands on (HandOffTrialKit). The trial itself needs two publishes made by tools/publish.ps1 and minutes of real processes, so it cannot be in a
// normal run; what it is built from can, and a mistake in a helper (a path check that lets a sibling folder through, a tree that is not the one Snapshot makes, a report parser that takes a cut-off file for a report)
// must not be found by a run that takes ten minutes. These tests use small synthetic publishes (TestBundle) in scratch folders and start nothing of the product; the one test that stages runs the product's UpdateService
// against a fake GitHub that answers from memory (no network), so the class is in the collection that serialises the tests that make an UpdateHttp.

/// <summary>A publish made the way tools/publish.ps1 lays it out, but small and synthetic: the inner folder with the files and PCMieru.bundle.json, and beside it the zip, the outer manifest and SHA256.txt.</summary>
internal static class SyntheticPublish
{
    public const string FirstBuildId = "11111111-1111-4111-8111-111111111111";
    public const string SecondBuildId = "22222222-2222-4222-8222-222222222222";

    /// <summary>Makes the publish of <paramref name="version"/> in <paramref name="output"/> and returns <paramref name="output"/>. Without <paramref name="withRelease"/> only the inner folder is made.</summary>
    public static string Make(string output, string version, string buildId, bool withRelease = true)
    {
        var bundle = new TestBundle(version) { BuildId = buildId };
        bundle.Files[TrialBrokenBuild.Victim] = Encoding.ASCII.GetBytes("core of " + version);
        bundle.Files[TrialBrokenBuild.Donor] = Encoding.ASCII.GetBytes("wire of " + version + ", a library that is not the core");
        var folder = Path.Combine(output, bundle.Root);
        foreach (var (name, data) in bundle.Files)
        {
            var path = TxPaths.Under(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }

        File.WriteAllBytes(Path.Combine(folder, BundleFormat.InnerManifestName), bundle.InnerBytes());
        if (withRelease)
        {
            var zip = bundle.Zip();
            var outer = bundle.Outer().ToUtf8Bytes();
            File.WriteAllBytes(Path.Combine(output, BundleFormat.ZipName(version)), zip);
            File.WriteAllBytes(Path.Combine(output, BundleFormat.OuterManifestName(version)), outer);
            File.WriteAllText(
                Path.Combine(output, BundleFormat.HashListName),
                $"{FakeRelease.Hex(zip)}  {BundleFormat.ZipName(version)}\n{FakeRelease.Hex(outer)}  {BundleFormat.OuterManifestName(version)}\n");
        }

        return output;
    }
}

[Collection(FeedCollection.Name)]
public sealed class HandOffTrialKitTests : IDisposable
{
    private readonly List<string> _folders = [];
    private readonly List<TrialPaths> _trials = [];

    public void Dispose()
    {
        foreach (var trial in _trials)
        {
            trial.Delete();
        }

        foreach (var folder in _folders)
        {
            Scratch.Delete(folder);
        }
    }

    private string Folder(string label)
    {
        var folder = Scratch.NewFolder(label);
        _folders.Add(folder);
        return folder;
    }

    private TrialPaths NewTrial(string label)
    {
        var paths = TrialPaths.Create(label, Scratch.Root());
        _trials.Add(paths);
        return paths;
    }

    private static TrialPublish Resolve(string given)
    {
        Assert.True(TrialPublish.TryResolve(given, out var publish, out var problem), problem);
        return publish ?? throw new InvalidOperationException("the publish was found but not returned");
    }

    // ----------------------------------------------------------------------------------------------------------------------- the environment

    [Fact]
    public void The_trial_is_skipped_until_both_publishes_are_named_and_says_which_one_is_missing()
    {
        var neither = TrialEnvironment.SkipReason(_ => null);
        Assert.NotNull(neither);
        Assert.Contains(TrialEnvironment.PublishA, neither);
        Assert.Contains(TrialEnvironment.PublishB, neither);

        var onlyA = TrialEnvironment.SkipReason(name => name == TrialEnvironment.PublishA ? @"C:\a" : null);
        Assert.NotNull(onlyA);
        Assert.Contains(TrialEnvironment.PublishB, onlyA);
        Assert.DoesNotContain(TrialEnvironment.PublishA, onlyA);

        Assert.NotNull(TrialEnvironment.SkipReason(_ => "   "));
        Assert.Null(TrialEnvironment.SkipReason(_ => @"C:\somewhere"));
    }

    [Fact]
    public void The_trial_fact_is_skipped_exactly_when_the_environment_of_this_run_lacks_a_publish()
    {
        var fact = new HandOffTrialFactAttribute();
        Assert.Equal(TrialEnvironment.SkipReason(Environment.GetEnvironmentVariable), fact.Skip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\tmp")]
    [InlineData(@"tmp")]
    [InlineData(@"C:tmp")]
    [InlineData(@"\rooted")]
    [InlineData(@"\\server\share\tmp")]
    public void A_scratch_root_that_is_not_a_full_path_on_a_drive_letter_is_refused(string? configured)
    {
        Assert.False(TrialEnvironment.TryScratchRoot(configured, out var root, out var problem));
        Assert.Equal(string.Empty, root);
        Assert.Contains(Scratch.Variable, problem);
    }

    [Fact]
    public void A_scratch_root_on_a_drive_letter_is_taken_without_its_trailing_separator()
    {
        Assert.True(TrialEnvironment.TryScratchRoot(@"C:\work\tmp\", out var root, out _));
        Assert.Equal(@"C:\work\tmp", root);
    }

    // ----------------------------------------------------------------------------------------------------------------------- the paths

    [Theory]
    [InlineData(@"C:\r\a", true)]
    [InlineData(@"C:\r\a\b.txt", true)]
    [InlineData(@"C:\R\A", true)]
    [InlineData(@"C:\r\a\..\b", true)]
    [InlineData(@"C:\r", false)]
    [InlineData(@"C:\r\", false)]
    [InlineData(@"C:\rr\a", false)]
    [InlineData(@"C:\r\..\x", false)]
    [InlineData(@"C:\r\a\..\..\x", false)]
    [InlineData(@"D:\r\a", false)]
    [InlineData(@"a\b", false)]
    [InlineData("", false)]
    public void A_path_is_under_the_root_only_when_it_is_strictly_inside_it_once_dots_are_resolved(string path, bool expected) =>
        Assert.Equal(expected, TrialPaths.IsStrictlyUnder(@"C:\r", path));

    [Fact]
    public void The_paths_of_a_trial_are_all_under_its_root_and_the_root_is_under_the_scratch_root()
    {
        var scratch = Folder("trial-paths");
        var paths = TrialPaths.Create("a-very-long-label-that-is-cut", scratch);
        _trials.Add(paths);

        Assert.True(TrialPaths.IsStrictlyUnder(scratch, paths.Root));
        Assert.True(Directory.Exists(paths.Install));
        Assert.True(Directory.Exists(paths.Local));
        Assert.True(Path.GetFileName(paths.Root).Length < 40, paths.Root);
        paths.AssertAllUnderRoot(Path.Combine(paths.Local, "update", "x"));
        Assert.Equal(Path.Combine(paths.Install, "update", "blocked.json"), paths.Tx.Blocked);
    }

    [Fact]
    public void A_path_outside_the_root_stops_the_trial_and_is_named()
    {
        var paths = NewTrial("outside");
        var other = Path.Combine(Path.GetDirectoryName(paths.Root)!, "somebody-elses");

        var thrown = Assert.Throws<Xunit.Sdk.TrueException>(() => paths.AssertAllUnderRoot(other));
        Assert.Contains("somebody-elses", thrown.Message);
        Assert.Throws<Xunit.Sdk.TrueException>(() => paths.AssertAllUnderRoot(paths.Root));
        Assert.Throws<Xunit.Sdk.TrueException>(() => paths.AssertAllUnderRoot(paths.Root + "-sibling"));
    }

    [Fact]
    public void A_scratch_root_that_is_not_a_drive_path_is_refused_and_nothing_falls_back_to_the_temp_folder()
    {
        Assert.Throws<Xunit.Sdk.TrueException>(() => TrialPaths.Create("x", null));
        Assert.Throws<Xunit.Sdk.TrueException>(() => TrialPaths.Create("x", @"relative\tmp"));
    }

    [Fact]
    public void Deleting_the_root_removes_a_tree_with_a_read_only_file_in_it()
    {
        var paths = NewTrial("readonly");
        var file = Path.Combine(paths.Install, "sub", "locked.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        Assert.True(paths.Delete());
        Assert.False(Directory.Exists(paths.Root));
    }

    // ----------------------------------------------------------------------------------------------------------------------- the owner's files

    [Fact]
    public void Planted_files_are_found_unchanged_until_one_changes_goes_or_comes()
    {
        var paths = NewTrial("planted");
        var planted = new TrialPlanted(paths.Install, paths.Local);
        planted.Plant();
        Assert.Equal(string.Empty, planted.InstallDifference());
        Assert.Equal(string.Empty, planted.LocalDifference());

        // the engine's own folder is not the owner's: anything under L\update is not a difference
        Directory.CreateDirectory(Path.Combine(paths.Local, "update", "x"));
        File.WriteAllText(Path.Combine(paths.Local, "update", "x", "state.json"), "{}");
        Assert.Equal(string.Empty, planted.LocalDifference());

        File.AppendAllText(Path.Combine(paths.Install, "notes.txt"), "!");
        Assert.Equal("changed notes.txt", planted.InstallDifference());
        File.Delete(Path.Combine(paths.Install, "ja", "mine.txt"));
        Assert.Contains("missing ja/mine.txt", planted.InstallDifference());

        File.WriteAllText(Path.Combine(paths.Local, "stray.txt"), "x");
        Assert.Contains("stray.txt", planted.LocalDifference());
    }

    [Fact]
    public void The_planted_gui_ini_says_the_update_mode_is_automatic_because_the_runner_reads_it()
    {
        var paths = NewTrial("gui-ini");
        new TrialPlanted(paths.Install, paths.Local).Plant();

        var lines = File.ReadAllLines(Path.Combine(paths.Local, "gui.ini"));
        Assert.Contains("update.mode=auto", lines);
    }

    // ----------------------------------------------------------------------------------------------------------------------- the smoke report

    private const string FullReport = """
        {"ok":true,"version":"0.5.1","updateNote":{"message":"committed","to":"0.5.1","from":null},"checks":[{"name":"a","ok":true,"detail":"fine"},{"name":"b","ok":false,"detail":"broken"}]}
        """;

    [Fact]
    public void A_whole_report_is_read_with_its_version_its_note_and_its_failed_checks()
    {
        var report = TrialReport.Parse(FullReport);

        Assert.NotNull(report);
        Assert.True(report.Ok);
        Assert.Equal("0.5.1", report.Version);
        Assert.Equal("committed", report.NoteMessage);
        Assert.Equal("0.5.1", report.NoteTo);
        Assert.Null(report.NoteFrom);
        Assert.Equal(2, report.ChecksTotal);
        Assert.Equal(["b: broken"], report.FailedChecks);
    }

    [Fact]
    public void A_rollback_note_carries_both_versions_and_a_report_without_a_note_has_none()
    {
        var blocked = TrialReport.Parse("""{"ok":false,"version":"0.5.0","updateNote":{"message":"blocked","to":"0.5.1","from":"0.5.0"},"checks":[]}""");
        Assert.NotNull(blocked);
        Assert.False(blocked.Ok);
        Assert.Equal(("blocked", "0.5.1", "0.5.0"), (blocked.NoteMessage, blocked.NoteTo, blocked.NoteFrom));
        Assert.Equal(0, blocked.ChecksTotal);

        var plain = TrialReport.Parse("""{"ok":true,"version":"0.5.0","updateNote":null,"checks":[]}""");
        Assert.NotNull(plain);
        Assert.Null(plain.NoteMessage);
        Assert.Null(plain.NoteTo);
        Assert.Null(plain.NoteFrom);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"ok\":true,\"version\":\"0.5.1\",\"checks\":[{\"name\":\"a\",")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"ok":true,"checks":[]}""")]
    [InlineData("""{"ok":"yes","version":"0.5.1","checks":[]}""")]
    [InlineData("""{"ok":true,"version":"0.5.1"}""")]
    [InlineData("""{"ok":true,"version":5,"checks":[]}""")]
    public void A_file_that_is_cut_off_or_not_a_report_is_not_a_report(string json) => Assert.Null(TrialReport.Parse(json));

    [Fact]
    public void A_report_is_read_from_a_file_its_writer_still_has_open_and_not_before_it_is_whole()
    {
        var folder = Folder("report-file");
        var path = Path.Combine(folder, "smoke-report.json");
        Assert.Null(TrialReport.TryRead(path));

        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var half = Encoding.UTF8.GetBytes(FullReport[..40]);
        writer.Write(half);
        writer.Flush();
        Assert.Null(TrialReport.TryRead(path));

        writer.Write(Encoding.UTF8.GetBytes(FullReport[40..]));
        writer.Flush();
        var whole = TrialReport.TryRead(path);
        Assert.NotNull(whole);
        Assert.Equal("0.5.1", whole.Version);
    }

    [Fact]
    public void The_last_result_of_the_runner_is_read_as_its_outcome_and_reason_and_garbage_is_none()
    {
        var folder = Folder("last-result");
        var path = Path.Combine(folder, "last-result.json");
        Assert.Null(TrialDisk.LastResult(path));

        File.WriteAllText(path, """{"outcome":"rolledBack","reason":"launchFailed"}""");
        Assert.Equal(("rolledBack", "launchFailed"), TrialDisk.LastResult(path));

        File.WriteAllText(path, "{}");
        Assert.Null(TrialDisk.LastResult(path));
        File.WriteAllText(path, "not json at all");
        Assert.Null(TrialDisk.LastResult(path));
    }

    // ----------------------------------------------------------------------------------------------------------------------- waiting, processes, error mode

    [Fact]
    public void A_wait_that_is_answered_at_once_asks_once_and_a_wait_that_is_not_ends_at_its_limit_after_asking_more_than_once()
    {
        var asked = 0;
        Assert.True(TrialWait.Until(() => ++asked > 0, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)));
        Assert.Equal(1, asked);

        asked = 0;
        var started = Environment.TickCount64;
        Assert.False(TrialWait.Until(() => ++asked < 0, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50)));
        Assert.True(Environment.TickCount64 - started >= 250);
        Assert.True(asked >= 2, $"asked {asked} times");

        asked = 0;
        Assert.True(TrialWait.Until(() => ++asked == 3, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20)));
        Assert.Equal(3, asked);
    }

    [Fact]
    public void Only_processes_started_from_under_the_root_are_found_and_killed_and_one_from_anywhere_else_is_spared()
    {
        var root = Folder("processes");
        var inside = Path.Combine(root, "inside", "trial-shell.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), inside);
        var outside = Path.Combine(Environment.SystemDirectory, "ping.exe");

        using var mine = Process.Start(new ProcessStartInfo(inside, "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("the copy of cmd.exe did not start");
        using var theirs = Process.Start(new ProcessStartInfo(outside, "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })
            ?? throw new InvalidOperationException("ping did not start");
        try
        {
            Assert.True(TrialWait.Until(() => TrialProcesses.Under(root).Any(line => line.StartsWith(mine.Id + " ", StringComparison.Ordinal)), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100)));
            Assert.DoesNotContain(TrialProcesses.Under(root), line => line.StartsWith(theirs.Id + " ", StringComparison.Ordinal));

            var killed = TrialProcesses.KillUnder(root);
            Assert.Contains(killed, line => line.StartsWith(mine.Id + " ", StringComparison.Ordinal));
            Assert.True(mine.WaitForExit(15_000), "the process under the root was not killed");
            Assert.False(theirs.HasExited, "a process from outside the root was killed");
            Assert.Empty(TrialProcesses.Under(root));
        }
        finally
        {
            theirs.Kill(entireProcessTree: true);
            theirs.WaitForExit(15_000);
            TrialProcesses.KillUnder(root);
        }
    }

    [Fact]
    public void The_quiet_error_mode_is_set_while_a_trial_runs_and_put_back_after_it()
    {
        var before = QuietCrashes.Current();
        using (new QuietCrashes())
        {
            Assert.Equal(QuietCrashes.Wanted, QuietCrashes.Current() & QuietCrashes.Wanted);
        }

        Assert.Equal(before, QuietCrashes.Current());
    }

    // ----------------------------------------------------------------------------------------------------------------------- generations

    [Fact]
    public void A_generation_has_a_manifest_of_its_files_and_its_tree_is_what_a_snapshot_of_the_written_files_gives()
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["PCMieru.exe"] = SyntheticSet.Bytes("exe", 300),
            ["ja/PcMieru.Core.resources.dll"] = SyntheticSet.Bytes("ja", 200),
        };
        var generation = TrialGeneration.Of("0.5.0", SyntheticPublish.FirstBuildId, files);
        var user = new Dictionary<string, byte[]> { ["plugins/deep/x.dat"] = SyntheticSet.Bytes("x", 100) };

        Assert.Equal(2, generation.Lines.Count);
        Assert.True(BundleManifest.TryParse(generation.ManifestBytes, out var manifest, out var verdict), verdict.Fault.ToString());
        Assert.Equal(("0.5.0", SyntheticPublish.FirstBuildId), (manifest.Version, manifest.BuildId));

        var folder = Folder("generation");
        foreach (var (name, data) in files.Concat(user).Append(new KeyValuePair<string, byte[]>(BundleFormat.InnerManifestName, generation.ManifestBytes)))
        {
            var path = TxPaths.Under(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }

        Assert.Equal(string.Empty, Rig.Diff(generation.Tree(user), Rig.Snapshot(folder)));
        Assert.Equal("<dir>", generation.Tree(user)["plugins/deep"]);
        Assert.Equal("<dir>", generation.Tree(user)["ja"]);
    }

    [Fact]
    public void Broken_build_is_the_good_one_with_only_the_core_library_replaced_by_the_wire_library()
    {
        var good = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [TrialBrokenBuild.Victim] = [1, 2, 3],
            [TrialBrokenBuild.Donor] = [9, 9],
            ["PCMieru.exe"] = [5],
        };

        var broken = TrialBrokenBuild.Of(good);

        Assert.Equal(good.Keys, broken.Keys);
        Assert.Equal<byte>([9, 9], broken[TrialBrokenBuild.Victim]);
        Assert.Equal<byte>([9, 9], broken[TrialBrokenBuild.Donor]);
        Assert.Equal<byte>([5], broken["PCMieru.exe"]);
        Assert.Equal<byte>([1, 2, 3], good[TrialBrokenBuild.Victim]);
    }

    [Fact]
    public void Broken_build_cannot_be_made_from_a_publish_without_the_two_libraries_or_where_they_are_the_same()
    {
        var withoutDonor = new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { [TrialBrokenBuild.Victim] = [1] };
        Assert.Contains(TrialBrokenBuild.Donor, Assert.Throws<InvalidOperationException>(() => TrialBrokenBuild.Of(withoutDonor)).Message);

        var withoutVictim = new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { [TrialBrokenBuild.Donor] = [1] };
        Assert.Throws<InvalidOperationException>(() => TrialBrokenBuild.Of(withoutVictim));

        var same = new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { [TrialBrokenBuild.Victim] = [1, 1], [TrialBrokenBuild.Donor] = [1, 1] };
        Assert.Throws<InvalidOperationException>(() => TrialBrokenBuild.Of(same));
    }

    // ----------------------------------------------------------------------------------------------------------------------- a publish

    [Fact]
    public void A_publish_is_found_from_its_output_folder_or_its_inner_folder_and_says_its_version_and_build()
    {
        var output = SyntheticPublish.Make(Folder("publish"), "0.5.1", SyntheticPublish.SecondBuildId);

        var fromOutput = Resolve(output);
        var fromInner = Resolve(fromOutput.Folder);

        Assert.Equal(("0.5.1", SyntheticPublish.SecondBuildId), (fromOutput.Version, fromOutput.BuildId));
        Assert.Equal(fromOutput.Folder, fromInner.Folder);
        Assert.Equal(fromOutput.Output, fromInner.Output);
        Assert.Equal(output, fromOutput.Output);
        Assert.Null(fromOutput.ReleaseProblem());
    }

    [Fact]
    public void A_folder_that_is_not_one_publish_is_refused_with_a_reason()
    {
        Assert.False(TrialPublish.TryResolve(null, out _, out var nothing));
        Assert.Contains("not a folder", nothing);
        Assert.False(TrialPublish.TryResolve(Path.Combine(Folder("nowhere"), "missing"), out _, out _));

        var empty = Folder("empty-publish");
        Assert.False(TrialPublish.TryResolve(empty, out _, out var none));
        Assert.Contains("publish.ps1", none);

        var two = Folder("two-publishes");
        SyntheticPublish.Make(two, "0.5.0", SyntheticPublish.FirstBuildId, withRelease: false);
        SyntheticPublish.Make(two, "0.5.1", SyntheticPublish.SecondBuildId, withRelease: false);
        Assert.False(TrialPublish.TryResolve(two, out _, out var several));
        Assert.Contains("2 publish folders", several);
    }

    [Fact]
    public void A_publish_without_its_zip_or_hash_list_says_which_file_is_missing()
    {
        var output = SyntheticPublish.Make(Folder("no-release"), "0.5.1", SyntheticPublish.SecondBuildId, withRelease: false);
        var publish = Resolve(output);

        Assert.Contains(BundleFormat.ZipName("0.5.1"), publish.ReleaseProblem());
        File.WriteAllBytes(publish.ZipPath, [1]);
        File.WriteAllBytes(publish.OuterManifestPath, [1]);
        Assert.Contains(BundleFormat.HashListName, publish.ReleaseProblem());
    }

    [Fact]
    public void The_files_of_a_publish_are_read_and_a_file_changed_after_the_publish_was_made_is_caught()
    {
        var publish = Resolve(SyntheticPublish.Make(Folder("read-files"), "0.5.1", SyntheticPublish.SecondBuildId));

        var files = publish.ReadFiles();
        Assert.Equal(publish.Manifest.Files.Count, files.Count);
        Assert.Equal("core of 0.5.1", Encoding.ASCII.GetString(files[TrialBrokenBuild.Victim]));

        File.AppendAllText(Path.Combine(publish.Folder, TrialBrokenBuild.Victim), "tampered");
        var thrown = Assert.Throws<InvalidOperationException>(publish.ReadFiles);
        Assert.Contains(TrialBrokenBuild.Victim, thrown.Message);
    }

    // ----------------------------------------------------------------------------------------------------------------------- A is installed

    [Fact]
    public void A_is_installed_with_the_test_build_as_its_swapper_in_the_files_the_manifest_the_record_and_recover_exe_and_the_launchers_own_check_accepts_it()
    {
        var a = Resolve(SyntheticPublish.Make(Folder("install-a"), "0.5.0", SyntheticPublish.FirstBuildId));
        var paths = NewTrial("install-a");
        var planted = new TrialPlanted(paths.Install, paths.Local);
        var testBuild = File.ReadAllBytes(TrialInstall.TestBuildSwapPath());
        Assert.NotEqual<byte>(a.ReadFiles()[RealRunnerPlacement.SwapName], testBuild);

        var generation = TrialInstall.InstallA(a, paths, planted);

        var prepared = RunnerHandOff.Prepare(paths.Install);
        Assert.True(prepared.IsReady, prepared.ToString());
        var checkedAgain = RunnerHandOff.Check(paths.Install);
        Assert.True(checkedAgain.IsReady, checkedAgain.ToString());
        Assert.Equal<byte>(testBuild, File.ReadAllBytes(paths.Tx.Recover));
        Assert.Equal<byte>(testBuild, File.ReadAllBytes(Path.Combine(paths.Install, RealRunnerPlacement.SwapName)));
        Assert.Equal(Rig.Sha(testBuild), generation.Lines.Single(line => line.Path == RealRunnerPlacement.SwapName).Sha256);
        Assert.Contains(Rig.Sha(testBuild), File.ReadAllText(paths.Tx.Installed));
        Assert.Contains(Rig.Sha(testBuild), File.ReadAllText(Path.Combine(paths.Install, BundleFormat.InnerManifestName)));
        Assert.Equal(string.Empty, Rig.Diff(generation.Tree(planted.InInstall), Rig.Snapshot(paths.Install)));
        Assert.Equal(string.Empty, planted.InstallDifference());
        Assert.Equal(string.Empty, planted.LocalDifference());
        paths.AssertAllUnderRoot();
    }

    // ----------------------------------------------------------------------------------------------------------------------- B is released and staged

    [Fact]
    public void The_real_release_is_the_publishes_own_zip_and_manifests_and_the_broken_one_is_made_of_the_same_files_with_one_library_mixed_up()
    {
        var b = Resolve(SyntheticPublish.Make(Folder("release"), "0.5.1", SyntheticPublish.SecondBuildId));
        var donor = b.ReadFiles()[TrialBrokenBuild.Donor];

        var (real, realGeneration) = TrialRelease.Real(b);
        Assert.Equal<byte>(File.ReadAllBytes(b.ZipPath), real.ZipBytes);
        Assert.Equal<byte>(File.ReadAllBytes(b.OuterManifestPath), real.ManifestBytes);
        Assert.Equal(File.ReadAllText(b.HashListPath), real.ShaFile());
        Assert.Equal(b.BuildId, realGeneration.BuildId);
        Assert.Equal<byte>(b.ManifestBytes, realGeneration.ManifestBytes);

        var (broken, brokenGeneration) = TrialRelease.Broken(b, "33333333-3333-4333-8333-333333333333");
        using var zip = new ZipArchive(new MemoryStream(broken.ZipBytes));
        var core = zip.GetEntry($"{BundleFormat.RootFolder("0.5.1")}/{TrialBrokenBuild.Victim}") ?? throw new InvalidOperationException("the broken zip has no core library");
        using var content = new MemoryStream();
        using (var stream = core.Open())
        {
            stream.CopyTo(content);
        }

        Assert.Equal<byte>(donor, content.ToArray());
        Assert.Equal("33333333-3333-4333-8333-333333333333", brokenGeneration.BuildId);
        Assert.Equal("0.5.1", brokenGeneration.Version);
        Assert.Equal(Rig.Sha(donor), brokenGeneration.Lines.Single(line => line.Path == TrialBrokenBuild.Victim).Sha256);
        Assert.True(BundleManifest.TryParse(broken.ManifestBytes, out var outer, out var verdict), verdict.Fault.ToString());
        Assert.Equal("33333333-3333-4333-8333-333333333333", outer.BuildId);
        Assert.Equal(Rig.Sha(brokenGeneration.ManifestBytes), outer.Files.Single(line => line.Path == BundleFormat.InnerManifestName).Sha256);
    }

    [Fact]
    public async Task B_is_staged_by_the_products_own_service_under_the_local_folder_with_the_manifest_the_runner_will_bind_to()
    {
        var a = Resolve(SyntheticPublish.Make(Folder("stage-a"), "0.5.0", SyntheticPublish.FirstBuildId));
        var b = Resolve(SyntheticPublish.Make(Folder("stage-b"), "0.5.1", SyntheticPublish.SecondBuildId));
        var paths = NewTrial("stage");
        var installed = TrialInstall.InstallA(a, paths, new TrialPlanted(paths.Install, paths.Local));
        var (release, target) = TrialRelease.Real(b);

        var staged = await TrialStage.StageAsync(release, installed, paths, new ScriptedInspector(), CancellationToken.None, new FakeDisk(long.MaxValue / 4));

        Assert.Equal(("0.5.1", SyntheticPublish.SecondBuildId, "v0.5.1"), (staged.Version, staged.BuildId, staged.Tag));
        Assert.True(TrialPaths.IsStrictlyUnder(paths.Local, staged.Folder), staged.Folder);
        Assert.Equal(staged.ManifestSha256, Rig.Sha(File.ReadAllBytes(Path.Combine(staged.Folder, "release.manifest.json"))));
        Assert.Equal<byte>(b.ReadFiles()[TrialBrokenBuild.Victim], File.ReadAllBytes(Path.Combine(staged.FilesFolder(), TrialBrokenBuild.Victim)));
        Assert.Equal(target.BuildId, staged.BuildId);
        var expected = new SortedDictionary<string, string>(target.Lines.ToDictionary(line => line.Path, line => line.Sha256, StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.Equal(string.Empty, Rig.Diff(expected, SnapshotOfFiles(staged.FilesFolder(), target)));
    }

    [Fact]
    public async Task The_broken_build_is_staged_like_any_other_and_what_is_staged_carries_the_mixed_up_library()
    {
        var a = Resolve(SyntheticPublish.Make(Folder("stage-bad-a"), "0.5.0", SyntheticPublish.FirstBuildId));
        var b = Resolve(SyntheticPublish.Make(Folder("stage-bad-b"), "0.5.1", SyntheticPublish.SecondBuildId));
        var paths = NewTrial("stage-bad");
        var installed = TrialInstall.InstallA(a, paths, new TrialPlanted(paths.Install, paths.Local));
        var (release, _) = TrialRelease.Broken(b, "33333333-3333-4333-8333-333333333333");

        var staged = await TrialStage.StageAsync(release, installed, paths, new ScriptedInspector(), CancellationToken.None, new FakeDisk(long.MaxValue / 4));

        Assert.Equal(("0.5.1", "33333333-3333-4333-8333-333333333333"), (staged.Version, staged.BuildId));
        Assert.Equal<byte>(b.ReadFiles()[TrialBrokenBuild.Donor], File.ReadAllBytes(Path.Combine(staged.FilesFolder(), TrialBrokenBuild.Victim)));
    }

    private static SortedDictionary<string, string> SnapshotOfFiles(string folder, TrialGeneration generation)
    {
        var present = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in generation.Lines)
        {
            var path = TxPaths.Under(folder, line.Path);
            if (File.Exists(path))
            {
                present[line.Path] = Rig.Sha(File.ReadAllBytes(path));
            }
        }

        return present;
    }
}
