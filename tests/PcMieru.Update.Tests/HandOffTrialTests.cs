using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using PcMieru.Swap.TestBuild;
using PcMieru.Update.Feed;
using PcMieru.Update.Transaction;

namespace PcMieru.Update.Tests;

// The REAL TRIAL of the update hand-off (step 5a, part 3c-2c): two real publishes made by tools/publish.ps1 (A, the old version, and B, a newer one) go through the whole chain on this machine, with real processes, in
// folders that all belong to the trial. What it proves, that no unit test can: the product's own pieces, put together the way a person's machine puts them together, hand an update over, swap it in, check the new
// version by really starting it, and either keep it or put the old one back, and leave a note that the version that is then started shows.
//
//   1. A is installed into a scratch install I. A's PCMieru.Swap.exe is replaced by the TEST BUILD of the swapper, in the install, in A's manifest and in installed.json (all the same bytes), and the same exe is
//      placed as I\update\recover.exe with the files a managed program needs. That is how the launcher's trust check of the runner is satisfied: RunnerHandOff only accepts a recover.exe that is the Swap.exe the
//      install's record names, and now it is. The check is the product's own and is run in-process (Prepare and Check) before anything starts; it is not bypassed and not changed.
//   2. The owner's files are planted in I and in the scratch L (a stand-in for %LOCALAPPDATA%\PCMieru, with a gui.ini that says update.mode=auto).
//   3. B (or B', see TrialBrokenBuild) is STAGED into L by the product's own UpdateService (a check, then StageAsync) against a fake GitHub that answers from memory: no network, and the stage is
//      L\update\<id>\cache\<tx>\ exactly as the real program would have left it for the runner to find.
//   4. The GUI's side of the pipe is the product's own GuiHandOff, run by the TxChild double (tests/PcMieru.TxChild, "handoff"): it starts I\update\recover.exe --apply <pipe> <process>, sends the one message,
//      and exits when the runner says "accepted" (like the GUI closing). The runner is a real process of the test build: it waits for the GUI, enters the gate exclusively, re-checks everything from the disk
//      (the stage, its hash, the blocked tags, the version, update.mode in L\gui.ini), swaps, and checks the new version with the REAL I\PCMieru.exe --verify-update (up to 3 attempts).
//   5. The environment variables of the test build (PCMIERU_SWAP_TEST, _LOCAL, _NOT_ELEVATED and _START) make the runner use this scratch L and, as its last act, start I\PCMieru.exe --smoke <report> --fakes
//      instead of a plain start: the window of the real program would read and write the owner's settings and may ask the network for a newer version. The smoke run keeps its settings in memory and makes no
//      request, and its report says which version it is and which update note it reads from ITS OWN install folder (I\update): that the report says "B" and "committed" is the proof that the exe that started is
//      the scratch install's, at the new version, and that the runner left its result where the next start reads it.
//
// After a run: the report, the runner's last-result.json, the blocked tags (I\update\blocked.json: the file is in the install's update folder, NOT in L), the whole tree of I against the generation it must be,
// and the planted files (byte for byte) are checked, all of them, and every difference is named in one message together with what is on the disk, because the run takes minutes and cannot be repeated for one
// missing line. Every path the trial builds is asserted to be under the one scratch root (PCM_TEST_TMP) once the paths are known; every process of the trial is started from under that root (the runner, the
// started program) or is the GUI double (bounded to two minutes), and what is left under the root is killed at the end; the root is deleted.
//
// How to run it (see the report of the worker, and tools/publish.ps1): make the two publishes with different -Output folders and versions, set PCMIERU_TRIAL_A and PCMIERU_TRIAL_B and PCM_TEST_TMP (a folder on a
// drive letter), and run `dotnet test tests/PcMieru.Update.Tests -c Debug --filter "FullyQualifiedName~HandOffTrialTests"`. Without the two publish variables the tests are SKIPPED, and every normal run
// (`Category!=RealMachine`) and CI never run them.

/// <summary>
/// A fact that is skipped (shown as skipped, not failed) when the environment does not name the two publishes the trial needs (PCMIERU_TRIAL_A and PCMIERU_TRIAL_B). xUnit 2 has no way to skip from inside a test, so
/// the reason is decided here, when the test is discovered.
/// </summary>
public sealed class HandOffTrialFactAttribute : FactAttribute
{
    public HandOffTrialFactAttribute() => Skip = TrialEnvironment.SkipReason(Environment.GetEnvironmentVariable);
}

[Collection(FeedCollection.Name)]
[Trait("Category", "RealMachine")]
public sealed class HandOffTrialTests
{
    /// <summary>The build id of B': nobody else's (the synthetic builds of the other tests use 1111..., 2222... and the default), so a tag in blocked.json can only be this one.</summary>
    private const string BrokenBuildId = "33333333-3333-4333-8333-333333333333";

    /// <summary>The hand-off to the runner's "accepted": the GUI double ends at once when it is accepted (it waits at most 30 s for the answer), and the pipe may be held a little longer by the runner it started.</summary>
    private static readonly TimeSpan GuiLimit = TimeSpan.FromSeconds(120);

    /// <summary>From "accepted" to the smoke report: the swap and its launch checks (up to three), then the smoke run of the started program, which has a budget of 300 s of its own.</summary>
    private static readonly TimeSpan ReportLimit = TimeSpan.FromMinutes(10);

    /// <summary>The wait for the report gives up early when nothing of the trial has been running for this long and there is still no report (the runner ended without starting anything).</summary>
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(20);

    /// <summary>How long a process that has written its report may take to end, and how long a kill may take to be seen.</summary>
    private static readonly TimeSpan EndLimit = TimeSpan.FromSeconds(60);

    [HandOffTrialFact]
    public async Task A_good_new_version_is_handed_over_swapped_in_verified_and_started_and_the_owners_files_are_untouched()
    {
        using var quiet = new QuietCrashes();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var trial = new Trial("a-to-b");
        var leftovers = string.Empty;
        try
        {
            var run = await trial.RunAsync(broken: false, cts.Token);
            var findings = new Findings();
            findings.Equal<(string, string)?>(("committed", "none"), run.LastResult, "update\\last-result.json (outcome, reason)");
            trial.ExpectStarted(findings, run, version: trial.B.Version, message: "committed", to: trial.B.Version, from: null);
            findings.Expect(run.Blocked.All(tag => tag.BuildId != trial.B.BuildId), "a build that was swapped in and verified is in blocked.json");
            trial.ExpectUntouched(findings, run.Target);
            if (findings.Any)
            {
                Assert.Fail($"A to B: the swap was not what it must be:{Environment.NewLine}{findings}{Environment.NewLine}{trial.Diagnostics()}");
            }
        }
        finally
        {
            leftovers = trial.CleanUp();
        }

        Assert.True(leftovers.Length == 0, "the trial could not clean up: " + leftovers);
    }

    [HandOffTrialFact]
    public async Task A_new_version_whose_launch_check_fails_is_rolled_back_the_old_one_starts_with_the_note_and_the_build_is_blocked()
    {
        using var quiet = new QuietCrashes();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var trial = new Trial("a-to-b2");
        var leftovers = string.Empty;
        try
        {
            var run = await trial.RunAsync(broken: true, cts.Token);
            var findings = new Findings();
            findings.Equal<(string, string)?>(("rolledBack", "launchFailed"), run.LastResult, "update\\last-result.json (outcome, reason)");
            trial.ExpectStarted(findings, run, version: trial.A.Version, message: "blocked", to: trial.B.Version, from: trial.A.Version);
            findings.Expect(run.Blocked.Any(tag => tag.BuildId == BrokenBuildId), $"I\\update\\blocked.json (the install's own file, not one in L) has no tag for build {BrokenBuildId}");
            findings.Expect(run.Blocked.All(tag => tag.BuildId != trial.B.BuildId), "the build of the REAL B is blocked: the trial blocked the wrong build");
            trial.ExpectUntouched(findings, run.Installed);
            if (findings.Any)
            {
                Assert.Fail($"A to B': the rollback was not what it must be:{Environment.NewLine}{findings}{Environment.NewLine}{trial.Diagnostics()}");
            }
        }
        finally
        {
            leftovers = trial.CleanUp();
        }

        Assert.True(leftovers.Length == 0, "the trial could not clean up: " + leftovers);
    }

    /// <summary>What a run left behind and what the processes said: everything the two tests look at, taken once the chain has ended.</summary>
    private sealed record Run(
        TrialGeneration Installed,
        TrialGeneration Target,
        StagedUpdate Staged,
        int GuiExit,
        string GuiOutput,
        TrialReport? Report,
        (string Outcome, string Reason)? LastResult,
        IReadOnlyList<BlockedTag> Blocked,
        IReadOnlyList<string> StillRunning);

    /// <summary>The findings of a run: every expectation that did not hold, collected so that one failure message says all of them (the run takes minutes).</summary>
    private sealed class Findings
    {
        private readonly List<string> _lines = [];

        public bool Any => _lines.Count > 0;

        public void Expect(bool holds, string what)
        {
            if (!holds)
            {
                _lines.Add(what);
            }
        }

        public void Equal<T>(T expected, T actual, string what) =>
            Expect(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected '{expected}', got '{actual}'");

        public override string ToString() => string.Join(Environment.NewLine, _lines.Select(line => "  - " + line));
    }

    /// <summary>One run of the trial: its publishes, its scratch root, the owner's planted files, and the steps of the chain.</summary>
    private sealed class Trial
    {
        public Trial(string label)
        {
            Assert.True(
                File.Exists(ManagedChild.ChildExe()) && File.Exists(TrialInstall.TestBuildSwapPath()),
                "the doubles are not built: tests/PcMieru.TxChild and tests/PcMieru.Swap.TestBuild must be built with this configuration (build PcMieru.slnx first)");
            A = Resolve(TrialEnvironment.PublishA);
            B = Resolve(TrialEnvironment.PublishB);
            Assert.True(
                AppVersion.TryParse(A.Version, out var older) && AppVersion.TryParse(B.Version, out var newer) && older < newer,
                $"B ({B.Version}) must be a newer version than A ({A.Version}); both X.Y.Z, made by tools/publish.ps1 with different -Version");
            Assert.NotEqual(A.BuildId, B.BuildId);
            Paths = TrialPaths.Create(label);
            Planted = new TrialPlanted(Paths.Install, Paths.Local);
        }

        public TrialPublish A { get; }

        public TrialPublish B { get; }

        public TrialPaths Paths { get; }

        public TrialPlanted Planted { get; }

        private static TrialPublish Resolve(string variable)
        {
            Assert.True(TrialPublish.TryResolve(Environment.GetEnvironmentVariable(variable), out var publish, out var problem), $"{variable}: {problem}");
            return publish ?? throw new InvalidOperationException($"{variable}: the publish was found but not returned");
        }

        /// <summary>A is installed, B (or B') is staged, the hand-off is driven and the chain is waited for; what is left is read. Nothing is judged here except what the next step cannot do without.</summary>
        public async Task<Run> RunAsync(bool broken, CancellationToken ct)
        {
            var installed = TrialInstall.InstallA(A, Paths, Planted);
            var prepared = RunnerHandOff.Prepare(Paths.Install);
            Assert.True(prepared.IsReady, "the launcher's own check (RunnerHandOff.Prepare) does not accept the runner the trial placed: " + prepared);
            var checkedAgain = RunnerHandOff.Check(Paths.Install);
            Assert.True(checkedAgain.IsReady, "the launcher's own check (RunnerHandOff.Check) does not accept the runner the trial placed: " + checkedAgain);

            if (!broken)
            {
                Assert.True(B.ReleaseProblem() is null, B.ReleaseProblem());
            }

            var (release, target) = broken ? TrialRelease.Broken(B, BrokenBuildId) : TrialRelease.Real(B);
            var staged = await TrialStage.StageAsync(release, installed, Paths, inspector: null, ct);
            Paths.AssertAllUnderRoot(staged.Folder);

            var chain = RunGuiDouble(staged);
            if (chain.ExitCode != 0 || !chain.Output.Contains("handoff accepted", StringComparison.Ordinal))
            {
                Assert.Fail($"the hand-off was not accepted (the GUI double ended with {chain.ExitCode}): {chain.Output}{Environment.NewLine}{Diagnostics()}");
            }

            var report = WaitForReport();
            IReadOnlyList<string> stillRunning = [];
            if (report is not null && !TrialWait.Until(() => TrialProcesses.Under(Paths.Root).Count == 0, EndLimit, TimeSpan.FromSeconds(1)))
            {
                stillRunning = TrialProcesses.Under(Paths.Root);
            }

            return new Run(installed, target, staged, chain.ExitCode, chain.Output, report, TrialDisk.LastResult(Paths.Tx.LastResult), BlockedTags.Read(Paths.Tx.Blocked), stillRunning);
        }

        /// <summary>
        /// The GUI's side of the pipe: the TxChild double's "handoff" runs the product's GuiHandOff towards I\update\recover.exe. The environment it gets is what the test build of the runner reads (the runner it starts
        /// inherits it): this install, this L, "not elevated" (the machine's token is not what the trial is about) and the report file of the final start.
        /// </summary>
        private (int ExitCode, string Output) RunGuiDouble(StagedUpdate staged)
        {
            var info = new ProcessStartInfo(ManagedChild.ChildExe()) { WorkingDirectory = Paths.Root };
            foreach (var argument in new[] { "handoff", Paths.Install, staged.Folder, staged.ManifestSha256, staged.Version, staged.BuildId, staged.Tag })
            {
                info.ArgumentList.Add(argument);
            }

            info.Environment[TestEntry.Variable] = Paths.Install;
            info.Environment[TestEntry.LocalVariable] = Paths.Local;
            info.Environment[TestEntry.NotElevatedVariable] = "1";
            info.Environment[TestEntry.StartVariable] = Paths.Report;
            var ran = BoundedProcess.Run(info, GuiLimit);
            return (ran.ExitCode, ran.Output);
        }

        /// <summary>
        /// Waits for the smoke report of the final start. Ends early when nothing of the trial runs any more for <see cref="IdleGrace"/> and there is still no report (the runner failed before it started anything), so
        /// that a broken chain says so after seconds and not after the whole limit.
        /// </summary>
        private TrialReport? WaitForReport()
        {
            TrialReport? report = null;
            long? idleSince = null;
            TrialWait.Until(
                () =>
                {
                    report = TrialReport.TryRead(Paths.Report);
                    if (report is not null)
                    {
                        return true;
                    }

                    if (TrialProcesses.Under(Paths.Root).Count > 0)
                    {
                        idleSince = null;
                        return false;
                    }

                    idleSince ??= Environment.TickCount64;
                    return Environment.TickCount64 - idleSince.Value > (long)IdleGrace.TotalMilliseconds;
                },
                ReportLimit,
                TimeSpan.FromSeconds(2));
            return report;
        }

        /// <summary>
        /// What the started program says: it was started, it passed its own checks, it is the version <paramref name="version"/>, and the update note it reads from its own install folder is
        /// <paramref name="message"/> (committed or blocked) of <paramref name="to"/> and <paramref name="from"/> (the runner's note after a commit has no from).
        /// </summary>
        public void ExpectStarted(Findings findings, Run run, string version, string message, string to, string? from)
        {
            if (run.Report is not { } report)
            {
                findings.Expect(false, $"no smoke report in {ReportLimit.TotalMinutes:0} minutes: I\\PCMieru.exe --smoke was not started by the runner, or did not finish (still running: {string.Join("; ", run.StillRunning)})");
                return;
            }

            findings.Expect(report.Ok, "the smoke report of the started program says it is not ok");
            findings.Expect(report.ChecksTotal > 0, "the smoke report has no checks");
            findings.Expect(report.FailedChecks.Count == 0, "failed checks of the started program: " + string.Join(" | ", report.FailedChecks.Take(5)));
            findings.Equal<string?>(version, report.Version, "the version of the program that was started");
            findings.Equal<string?>(message, report.NoteMessage, "the update note it reads (message)");
            findings.Equal<string?>(to, report.NoteTo, "the update note it reads (to)");
            findings.Equal<string?>(from, report.NoteFrom, "the update note it reads (from)");
            findings.Expect(run.StillRunning.Count == 0, "processes of the trial were still running after the report was written: " + string.Join("; ", run.StillRunning));
        }

        /// <summary>
        /// The owner's files are what was planted, byte for byte, in I and in L (and L holds nothing else outside update\), and the whole install folder outside update\ is exactly the generation that is meant to be
        /// there (its files, its manifest) plus the planted files: nothing missing, nothing changed, nothing extra.
        /// </summary>
        public void ExpectUntouched(Findings findings, TrialGeneration generation)
        {
            findings.Equal(string.Empty, Planted.InstallDifference(), "the owner's files in I that the update changed");
            findings.Equal(string.Empty, Planted.LocalDifference(), "L outside update\\ against what was planted");
            findings.Equal(string.Empty, Rig.Diff(generation.Tree(Planted.InInstall), Rig.Snapshot(Paths.Install)), $"I outside update\\ against generation {generation.Version} and the planted files");
        }

        /// <summary>What is on the disk and what is running, for the message of a failure: the run cannot be repeated for one missing line.</summary>
        public string Diagnostics()
        {
            var text = new StringBuilder();
            text.AppendLine($"scratch root: {Paths.Root}");
            text.AppendLine($"I\\update\\last-result.json: {Raw(Paths.Tx.LastResult)}");
            text.AppendLine($"I\\update\\blocked.json: {Raw(Paths.Tx.Blocked)}");
            text.AppendLine($"I\\update (names): {Listing(Paths.Tx.Update)}");
            text.AppendLine($"smoke report: {Raw(Paths.Report)}");
            text.AppendLine($"running under the root: {string.Join("; ", TrialProcesses.Under(Paths.Root))}");
            return text.ToString();
        }

        /// <summary>
        /// Kills what is left under the root (the runner, the started program: nothing of the owner's can be there, the root is the trial's own), waits until it is gone and deletes the root. Never throws;
        /// returns what could not be done (empty: all of it was).
        /// </summary>
        public string CleanUp()
        {
            var problems = new List<string>();
            try
            {
                if (!TrialWait.Until(() => TrialProcesses.KillUnder(Paths.Root).Count == 0, EndLimit, TimeSpan.FromMilliseconds(500)))
                {
                    problems.Add("processes started from under the root are still running: " + string.Join("; ", TrialProcesses.Under(Paths.Root)));
                }

                if (!Paths.Delete())
                {
                    problems.Add($"the scratch root '{Paths.Root}' could not be deleted");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            {
                problems.Add(ex.GetType().Name + ": " + ex.Message);
            }

            return string.Join("; ", problems);
        }

        private static string Raw(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return "(not there)";
                }

                var text = File.ReadAllText(path);
                return text.Length > 1500 ? text[..1500] + "..." : text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "(cannot be read: " + ex.GetType().Name + ")";
            }
        }

        private static string Listing(string folder)
        {
            try
            {
                return Directory.Exists(folder)
                    ? string.Join(", ", Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal))
                    : "(not there)";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "(cannot be listed: " + ex.GetType().Name + ")";
            }
        }
    }
}
