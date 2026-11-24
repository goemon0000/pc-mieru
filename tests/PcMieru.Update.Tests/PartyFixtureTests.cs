using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;
using PcMieru.Update.Launch;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Tests;

// DESIGN-B 11 on a real publish: v0.5 gains Party, the installed Party takes the shared gate, and startup failure restores v0.5.
// The ZIP check refuses a missing Party before extraction. A held backup leaves a rollback pending until it can resume.
// Set PCMIERU_TRIAL_FOLDER to the folder from tools/publish.ps1 containing PCMieru.bundle.json. Build tests/PcMieru.PartyStub in the
// same configuration as these tests, then run dotnet test --filter FullyQualifiedName~PartyFixtureTests. All writes go under Scratch (PCM_TEST_TMP).
[Trait("Category", "RealMachine")]
public sealed class PartyFixtureTests
{
    private const string FolderVariable = "PCMIERU_TRIAL_FOLDER";
    private const string RemovedSatellite = "ja/System.Windows.Forms.Design.resources.dll";
    private const string PartyExe = "PCMieru.Party.exe";
    private const string NextBuildId = "44444444-4444-4444-8444-444444444444";

    private static readonly string[] PartyFiles =
        [PartyExe, "PCMieru.Party.dll", PartyRuntimeConfig, PartyDeps];

    private const string PartyRuntimeConfig = "PCMieru.Party.runtimeconfig.json";
    private const string PartyDeps = "PCMieru.Party.deps.json";

    [Fact]
    public void The_party_build_commits_and_party_uses_the_installs_shared_gate()
    {
        using var trial = new Trial("party-ok");
        var launcher = FakeLauncher.Ok();

        var result = trial.Swap(launcher);

        Assert.True((result.Outcome, result.Reason) == (TxOutcome.Committed, TxReason.None), result.ToString());
        Assert.Equal(NextBuildId, Assert.Single(launcher.Calls).BuildId);
        trial.AssertInstallIs(trial.New.Tree(trial.UserFiles), trial.NewRecord.ToUtf8Bytes());
        Assert.False(File.Exists(trial.Paths.InInstall(RemovedSatellite)));

        var started = trial.StartParty();
        Assert.Equal(0, started.ExitCode);
        Assert.Contains("party ok", started.Stdout, StringComparison.Ordinal);

        using var gate = trial.EnterExclusive();
        var refused = trial.StartParty();
        Assert.Equal(1, refused.ExitCode);
        Assert.Empty(refused.Stdout);
        Assert.Contains(InstallGate.ReasonOf(GateOutcome.Busy), refused.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_verification_restores_the_original_install_and_blocks_the_tag()
    {
        using var trial = new Trial("party-rollback");
        var launcher = FakeLauncher.AlwaysFail();

        var result = trial.Swap(launcher);

        Assert.True((result.Outcome, result.Reason) == (TxOutcome.RolledBack, TxReason.LaunchFailed), result.ToString());
        Assert.Equal(3, launcher.Calls.Count);
        Assert.All(launcher.Calls, call => Assert.Equal(NextBuildId, call.BuildId));
        Assert.All(PartyFiles, name => Assert.False(File.Exists(trial.Paths.InInstall(name)), name));
        Assert.True(File.Exists(trial.Paths.InInstall(RemovedSatellite)));
        trial.AssertOriginalInstall();

        // The transaction records the failure in I\update\blocked.json. SwapApply reads it before accepting the hand-off.
        var blocked = Assert.Single(BlockedTags.Read(trial.Paths.Blocked));
        Assert.Equal((trial.B.Version, NextBuildId, TxTag.LaunchFailed), (blocked.Version, blocked.BuildId, blocked.Reason));
        using var channel = new ScriptedChannel(ApplyMessage.Of(trial.Stage).ToLine());
        var starter = new FakeStarter();
        var host = new SwapHost
        {
            SelfPath = trial.Paths.Recover,
            LocalFolder = trial.Local,
            Out = TextWriter.Null,
            Error = TextWriter.Null,
            IsElevated = static () => false,
            Starter = starter,
            OpenApplyEndpoint = (_, _) => new ApplyEndpoint(channel,
                _ => throw new InvalidOperationException("a blocked stage should be refused before waiting for the GUI")),
        };
        var before = Rig.Snapshot(trial.Paths.Install, includeUpdate: true);

        Assert.Equal(SwapExit.Aborted, SwapApply.Run(host, trial.Paths.Install, 1234, 5678));
        Assert.Equal(ApplyAnswer.Refuse(ApplyRefusal.Blocked), Assert.Single(channel.Sent));
        Assert.Empty(starter.Starts);
        Assert.Equal(before, Rig.Snapshot(trial.Paths.Install, includeUpdate: true));
    }

    [Fact]
    public void A_zip_missing_party_is_refused_with_the_same_manifests_before_extraction()
    {
        using var trial = new Trial("party-zip");
        var before = Rig.Snapshot(trial.Paths.Install, includeUpdate: true);
        var outer = trial.B.Outer();
        using (var whole = new MemoryStream(trial.B.Zip()))
        {
            var accepted = BundleZip.Validate(whole, trial.B.Version, outer, Limits.Default);
            Assert.True(accepted.Ok, $"{accepted.Fault}: {accepted.Detail}");
        }

        var entries = trial.B.Entries(trial.New.ManifestBytes);
        var party = Assert.Single(entries, entry => entry.Name == trial.B.Root + "/" + PartyExe);
        entries.Remove(party);
        using var incomplete = new MemoryStream(TestBundle.Write(entries));
        var refusal = BundleZip.Extract(incomplete, trial.B.Version, outer, Limits.Default,
            name => throw new InvalidOperationException("extraction started for " + name));

        Assert.False(refusal.Ok);
        Assert.Equal(BundleFault.FileSetMismatch, refusal.Fault);
        Assert.Contains(PartyExe, refusal.Detail, StringComparison.Ordinal);
        Assert.Equal(before, Rig.Snapshot(trial.Paths.Install, includeUpdate: true));
        trial.AssertOriginalInstall();
    }

    [Fact]
    public void A_held_old_manifest_backup_leaves_rollback_pending_until_it_is_released()
    {
        using var trial = new Trial("party-held");
        const string manifest = BundleFormat.InnerManifestName;
        string? backup = null;
        FileStream? held = null;
        var hook = new HookInjector((step, edge, item) =>
        {
            if (edge == TxEdge.After && step == TxSteps.BackupCopy && Path.GetFileName(item) == manifest)
            {
                backup = item;
            }

            // The old manifest is overwritten by B. Hold its backup as soon as the product has verified the copy.
            if (edge == TxEdge.After && step == TxSteps.JournalPrepared)
            {
                Assert.NotNull(backup);
                held = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None);
            }
        });
        var launcher = FakeLauncher.AlwaysFail();

        try
        {
            var result = trial.Swap(launcher, hook);

            Assert.True((result.Outcome, result.Reason) == (TxOutcome.Busy, TxReason.FilesInUse), result.ToString());
            Assert.Equal(3, launcher.Calls.Count);
            Assert.NotNull(held);
            Assert.True(File.Exists(backup));
            Assert.Equal(trial.OriginalTree[manifest], Convert.ToHexStringLower(SHA256.HashData(held)));
            Assert.False(File.Exists(trial.Paths.Sentinel));
            Assert.True(TxJournal.TryParse(File.ReadAllBytes(trial.Paths.Journal), out var journal, out var error), error);
            Assert.Equal(TxPhase.Rollback, journal.Phase);
            Assert.Equal(TxTag.LaunchFailed, journal.Tag);
            Assert.Contains(journal.Ops, op => op.Path == manifest && op.Intent == TxOpIntent.Overwrite);
            Assert.Equal(TxPaths.Under(trial.Paths.PrevFiles(journal.From.Version, journal.Tx), manifest), backup);
            Assert.Equal(trial.OriginalRecord, File.ReadAllBytes(trial.Paths.PrevInstalled(journal.From.Version, journal.Tx)));
            foreach (var file in journal.From.Files.Where(file => file.Path != manifest && !GateFile.IsExcludedFromSwap(file.Path)))
            {
                var saved = TxPaths.Under(trial.Paths.PrevFiles(journal.From.Version, journal.Tx), file.Path);
                Assert.Equal(trial.OriginalTree[file.Path], Rig.Sha(File.ReadAllBytes(saved)));
            }

            Assert.Equal(trial.OriginalTree["PCMieru.exe"], Rig.Sha(File.ReadAllBytes(trial.ExePath)));
        }
        finally
        {
            held?.Dispose();
        }

        var recovered = trial.Recover();
        Assert.True((recovered.Outcome, recovered.Reason) == (TxOutcome.RolledBack, TxReason.LaunchFailed), recovered.ToString());
        trial.AssertOriginalInstall();
        Assert.Equal(TxOutcome.NothingToDo, trial.Recover().Outcome);
    }

    private sealed class Trial : IDisposable
    {
        private readonly string _root;

        public Trial(string label)
        {
            var source = Environment.GetEnvironmentVariable(FolderVariable);
            Assert.True(!string.IsNullOrEmpty(source) && File.Exists(Path.Combine(source, BundleFormat.InnerManifestName)),
                $"set {FolderVariable} to a folder made by tools/publish.ps1 (the one with {BundleFormat.InnerManifestName})");
            Assert.True(TrialPublish.TryResolve(source, out var publish, out var problem), problem);
            Assert.NotNull(publish);
            var files = publish.ReadFiles();
            Assert.Contains(RealRunnerPlacement.SwapName, files.Keys);
            var old = new TrialGeneration(publish.Version, publish.BuildId, publish.Manifest.Files, publish.ManifestBytes);
            OriginalTree = old.Tree(UserFiles);

            var version = Version.Parse(old.Version);
            B = new TestBundle(new Version(version.Major, version.Minor + 1, 0).ToString()) { BuildId = NextBuildId };
            B.Files.Clear();
            foreach (var (name, data) in files)
            {
                B.Files.Add(name, data);
            }

            Assert.True(B.Files.Remove(RemovedSatellite), "the published folder lacks " + RemovedSatellite);
            var stub = Path.Combine(RepoFiles.Root(), "tests", "PcMieru.PartyStub", "bin", RepoFiles.Configuration(), "net10.0-windows");
            // Party uses the compatible PcMieru.Wire.dll already in A. Keep A's copy and add only the Party-specific files.
            Assert.Contains("PcMieru.Wire.dll", B.Files.Keys);
            foreach (var name in PartyFiles)
            {
                Assert.DoesNotContain(B.Files.Keys, existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));
                // A real v0.6 publishes Party self-contained like the other exes. The stub's own build is framework-dependent: its
                // runtimeconfig names a shared framework, which the apphost refuses next to the publish's hostfxr (0x80008096), and
                // without <app>.deps.json the host takes the folder for a framework-dependent app (0x80008081). So Party gets the
                // runtimeconfig of A's other console exe (SensorHost) and SensorHost's deps.json with the app's name changed: both
                // name the runtime pack A already carries.
                if (name == PartyRuntimeConfig)
                {
                    B.Files.Add(name, B.Files["PCMieru.SensorHost.runtimeconfig.json"]);
                    continue;
                }

                if (name == PartyDeps)
                {
                    var deps = Encoding.UTF8.GetString(B.Files["PCMieru.SensorHost.deps.json"]);
                    Assert.Contains("PCMieru.SensorHost", deps, StringComparison.Ordinal);
                    B.Files.Add(name, Encoding.UTF8.GetBytes(deps.Replace("PCMieru.SensorHost", "PCMieru.Party", StringComparison.Ordinal)));
                    continue;
                }

                var path = Path.Combine(stub, name);
                Assert.True(File.Exists(path), "build PcMieru.PartyStub first: " + path);
                B.Files.Add(name, File.ReadAllBytes(path));
            }

            var inner = B.Inner();
            New = new TrialGeneration(B.Version, B.BuildId, inner.Files, inner.ToUtf8Bytes());
            NewRecord = InstalledRecord.FromBundle(inner, New.ManifestBytes);

            _root = Scratch.NewFolder(label);
            try
            {
                Paths = new TxPaths(Path.Combine(_root, "install"));
                Local = Path.Combine(_root, "local");
                WriteFiles(Paths.Install, files);
                WriteFiles(Paths.Install, UserFiles);
                File.WriteAllBytes(Paths.InInstall(BundleFormat.InnerManifestName), publish.ManifestBytes);
                Directory.CreateDirectory(Paths.Update);
                File.WriteAllBytes(Paths.Installed, InstalledRecord.FromBundle(publish.Manifest, publish.ManifestBytes).ToUtf8Bytes());
                OriginalRecord = File.ReadAllBytes(Paths.Installed);

                var state = InstallId.StateFolder(Local, ExePath);
                Assert.NotNull(state);
                var outerBytes = B.Outer(New.ManifestBytes).ToUtf8Bytes();
                Stage = new StagedUpdate
                {
                    Version = B.Version,
                    BuildId = B.BuildId,
                    Tag = "v" + B.Version,
                    Folder = Path.Combine(StageLayout.CacheOf(state), TxPaths.NewTx()),
                    ManifestSha256 = Rig.Sha(outerBytes),
                };
                WriteFiles(Stage.FilesFolder(), B.Files);
                File.WriteAllBytes(TxPaths.Under(Stage.FilesFolder(), BundleFormat.InnerManifestName), New.ManifestBytes);
                File.WriteAllBytes(Path.Combine(Stage.Folder, StageLayout.ManifestFileName), outerBytes);
                File.WriteAllText(Path.Combine(Local, "gui.ini"), "update.mode=auto\n");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public TxPaths Paths { get; }
        public string Local { get; }
        public string ExePath => Paths.InInstall("PCMieru.exe");
        public StagedUpdate Stage { get; }
        public TestBundle B { get; }
        public TrialGeneration New { get; }
        public InstalledRecord NewRecord { get; }
        public byte[] OriginalRecord { get; }
        public SortedDictionary<string, string> OriginalTree { get; }

        public IReadOnlyDictionary<string, byte[]> UserFiles { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["notes.txt"] = "my own notes\n"u8.ToArray(),
            ["ja/mine.txt"] = "my own japanese file\n"u8.ToArray(),
            ["plugins/deep/x.dat"] = SyntheticSet.Bytes("plugin", 4000),
        };

        public void AssertOriginalInstall() => AssertInstallIs(OriginalTree, OriginalRecord);

        public void AssertInstallIs(SortedDictionary<string, string> tree, byte[] record)
        {
            var difference = Rig.Diff(tree, Rig.Snapshot(Paths.Install));
            Assert.True(difference.Length == 0, difference);
            Assert.Equal(record, File.ReadAllBytes(Paths.Installed));
        }

        public TxResult Swap(ILaunchVerifier launcher, IFaultInjector? fault = null)
        {
            using var gate = EnterExclusive();
            return SwapTransaction.Run(gate, new SwapRequest(Stage.FilesFolder()), Rig.Seams(fault, launcher));
        }

        public TxResult Recover()
        {
            using var gate = EnterExclusive();
            return TxRecovery.Recover(gate, Rig.Seams(), automatic: false);
        }

        public GateEntry EnterExclusive()
        {
            var gate = InstallGate.EnterExclusive(ExePath);
            if (!gate.IsOpen)
            {
                gate.Dispose();
                Assert.Fail("the gate must be enterable: " + gate.Reason);
            }

            return gate;
        }

        public (int ExitCode, string Stdout, string Stderr) StartParty()
        {
            var info = new ProcessStartInfo(Paths.InInstall(PartyExe))
            {
                WorkingDirectory = Paths.Install,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Party did not start");
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                Assert.True(BoundedProcess.WaitOrKill(process, TimeSpan.FromSeconds(60)), "Party did not exit within 60 s");
                Assert.True(Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(15)), "Party did not close its output streams");
                return (process.ExitCode, stdout.Result, stderr.Result);
            }
            finally
            {
                if (!process.HasExited)
                {
                    BoundedProcess.Kill(process);
                }
            }
        }

        private static void WriteFiles(string folder, IReadOnlyDictionary<string, byte[]> files)
        {
            foreach (var (name, data) in files)
            {
                var path = TxPaths.Under(folder, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, data);
            }
        }

        public void Dispose() => Scratch.Delete(_root);
    }
}
