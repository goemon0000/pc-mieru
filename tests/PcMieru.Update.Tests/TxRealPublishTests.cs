using PcMieru.Update.Bundle;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Tests;

// The transaction on a REAL publish output (tools/publish.ps1: ~240 files, ~120 MB), not on the synthetic set. It is a RealMachine test because it needs a published folder on the disk: the environment variable
// PCMIERU_TRIAL_FOLDER names it (the folder that holds PCMieru.bundle.json). Everything the test writes is under the scratch folder (Scratch: PCM_TEST_TMP); the published folder is only read.
//
//   A  = the published folder (copied to an install, with the owner's own files planted)
//   B  = A with another version and buildId, a changed file (PCMieru.exe, NOTICE.txt), an added root file, an added file in a new folder (en/), a removed satellite, and the manifest written again
//
// A publish made before the swapper ships (-PartialFloor, no PCMieru.Swap.exe in it) gets a stand-in for the swapper (a copy of PCMieru.Helper.exe, a NativeAOT exe): the transaction needs a runner to place and only
// compares bytes; the stand-in is listed in both manifests. A publish that has the real PCMieru.Swap.exe is used as it is.

[Trait("Category", "RealMachine")]
public sealed class TxRealPublishTests
{
    private const string FolderVariable = "PCMIERU_TRIAL_FOLDER";
    private const string RemovedSatellite = "ja/System.Windows.Forms.Design.resources.dll";

    private sealed class Trial : IDisposable
    {
        private readonly string _root;

        public Trial(string label)
        {
            var published = Environment.GetEnvironmentVariable(FolderVariable);
            Assert.True(!string.IsNullOrEmpty(published) && File.Exists(Path.Combine(published, BundleFormat.InnerManifestName)),
                $"set {FolderVariable} to a folder made by tools/publish.ps1 (the one with {BundleFormat.InnerManifestName})");
            _root = Scratch.NewFolder(label);
            Install = Path.Combine(_root, "install");
            Staged = Path.Combine(_root, "staged");

            var manifestBytes = File.ReadAllBytes(Path.Combine(published, BundleFormat.InnerManifestName));
            Assert.True(BundleManifest.TryParse(manifestBytes, out var manifest, out var verdict), "the published manifest must parse: " + verdict.Fault);
            Version = manifest.Version;
            BuildId = manifest.BuildId;

            // A: the published files.
            foreach (var line in manifest.Files)
            {
                var path = TxPaths.Under(published, line.Path);
                var data = File.ReadAllBytes(path);
                Assert.Equal(line.Sha256, Rig.Sha(data)); // the folder is what its manifest says
                A[line.Path] = data;
            }

            if (!A.ContainsKey(RealRunnerPlacement.SwapName))
            {
                A[RealRunnerPlacement.SwapName] = File.ReadAllBytes(Path.Combine(published, "PCMieru.Helper.exe")); // the stand-in, see above
                StandIn = true;
            }

            // B: another build.
            foreach (var (name, data) in A)
            {
                B[name] = data;
            }

            B["PCMieru.exe"] = [.. A["PCMieru.exe"], .. SyntheticSet.Bytes("exe-b", 64)];
            B["NOTICE.txt"] = [.. A["NOTICE.txt"], .. "\nchanged in the trial\n"u8.ToArray()];
            B["extra-feature.dll"] = SyntheticSet.Bytes("extra", 5000);
            B["en/PcMieru.Core.resources.dll"] = SyntheticSet.Bytes("en-core", 900);
            Assert.True(B.Remove(RemovedSatellite), "the satellite the trial removes is not in this publish: " + RemovedSatellite);
            NextVersion = NextPatch(Version);
            NextBuildId = "33333333-3333-4333-8333-333333333333";

            Write(Install, A, Version, BuildId);
            Write(Staged, B, NextVersion, NextBuildId);
            foreach (var (name, data) in UserFiles)
            {
                var path = TxPaths.Under(Install, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, data);
            }
        }

        public string Install { get; }

        public string Staged { get; }

        public string Version { get; }

        public string BuildId { get; }

        public string NextVersion { get; }

        public string NextBuildId { get; }

        public bool StandIn { get; }

        public SortedDictionary<string, byte[]> A { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<string, byte[]> B { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<string, byte[]> UserFiles { get; } = new(StringComparer.Ordinal)
        {
            ["notes.txt"] = "my own notes\n"u8.ToArray(),
            ["ja/mine.txt"] = "my own japanese file\n"u8.ToArray(),
            ["plugins/deep/x.dat"] = SyntheticSet.Bytes("plugin", 4000),
        };

        public string ExePath => Path.Combine(Install, "PCMieru.exe");

        private static string NextPatch(string version)
        {
            var parts = version.Split('.');
            return $"{parts[0]}.{parts[1]}.{int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) + 1}";
        }

        private static void Write(string folder, SortedDictionary<string, byte[]> files, string version, string buildId)
        {
            foreach (var (name, data) in files)
            {
                var path = TxPaths.Under(folder, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, data);
            }

            var manifest = new BundleManifest(version, buildId, files.Where(f => f.Key != BundleFormat.InnerManifestName).Select(f => TestBundle.Line(f.Key, f.Value)));
            File.WriteAllBytes(Path.Combine(folder, BundleFormat.InnerManifestName), manifest.ToUtf8Bytes());
        }

        /// <summary>What the install must be, byte for byte: the generation (its files and its manifest), the owner's files, and every folder on the way.</summary>
        public SortedDictionary<string, string> Expected(SortedDictionary<string, byte[]> generation, string version, string buildId)
        {
            var manifest = new BundleManifest(version, buildId, generation.Where(f => f.Key != BundleFormat.InnerManifestName).Select(f => TestBundle.Line(f.Key, f.Value))).ToUtf8Bytes();
            var tree = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, data) in generation.Where(f => f.Key != BundleFormat.InnerManifestName))
            {
                tree[name] = Rig.Sha(data);
            }

            tree[BundleFormat.InnerManifestName] = Rig.Sha(manifest);
            foreach (var (name, data) in UserFiles)
            {
                tree[name] = Rig.Sha(data);
            }

            foreach (var file in tree.Keys.ToList())
            {
                var parts = file.Split('/');
                for (var i = 1; i < parts.Length; i++)
                {
                    tree[string.Join('/', parts[..i])] = "<dir>";
                }
            }

            return tree;
        }

        public void AssertInstallIs(SortedDictionary<string, byte[]> generation, string version, string buildId, string context)
        {
            var difference = Rig.Diff(Expected(generation, version, buildId), Rig.Snapshot(Install));
            Assert.True(difference.Length == 0, $"{context}: {difference}");
        }

        public TxResult Swap(ILaunchVerifier launcher)
        {
            using var gate = InstallGate.EnterExclusive(ExePath);
            Assert.True(gate.IsOpen, "the gate must be enterable: " + gate.Reason);
            var before = FileIdentity.Of(Path.Combine(Install, GateFile.FileName));
            var result = SwapTransaction.Run(gate, new SwapRequest(Staged), Rig.Seams(launcher: launcher));
            var after = FileIdentity.Of(Path.Combine(Install, GateFile.FileName));
            Assert.True(before is not null && after is not null && after.Value.IsSameFile(before.Value), "the gate file must be the same file after the swap");
            return result;
        }

        public TxResult Recover()
        {
            using var gate = InstallGate.EnterExclusive(ExePath);
            Assert.True(gate.IsOpen, "the gate must be enterable: " + gate.Reason);
            return TxRecovery.Recover(gate, Rig.Seams(), automatic: false);
        }

        public void Dispose() => Scratch.Delete(_root);
    }

    [Fact]
    public void A_real_publish_folder_is_swapped_to_another_build_and_the_whole_new_set_is_in_place()
    {
        using var trial = new Trial("real-ok");
        trial.AssertInstallIs(trial.A, trial.Version, trial.BuildId, "before");
        var launcher = FakeLauncher.Ok();

        var result = trial.Swap(launcher);

        Assert.True(result.Outcome == TxOutcome.Committed, $"{result.Outcome} {result.Reason}: {result.Detail}");
        Assert.Equal((trial.Version, trial.NextVersion), (result.FromVersion, result.ToVersion));
        trial.AssertInstallIs(trial.B, trial.NextVersion, trial.NextBuildId, "after the commit");
        Assert.Equal(trial.NextBuildId, Assert.Single(launcher.Calls).BuildId);
        Assert.False(File.Exists(TxPaths.Under(trial.Install, RemovedSatellite)), "the file the new build dropped is gone");
        Assert.True(File.Exists(TxPaths.Under(trial.Install, "en/PcMieru.Core.resources.dll")), "the file the new build added (in a new folder) is there");
        Assert.False(File.Exists(Path.Combine(trial.Install, GateFile.UpdateFolderName, GateFile.RecoverNoteName)), "the note is cleaned after the commit");
        Assert.Equal(TxOutcome.NothingToDo, trial.Recover().Outcome);
    }

    [Fact]
    public void A_real_publish_folder_whose_new_build_never_starts_is_put_back_byte_for_byte()
    {
        using var trial = new Trial("real-back");

        var result = trial.Swap(FakeLauncher.AlwaysFail());

        Assert.True(result.Outcome == TxOutcome.RolledBack, $"{result.Outcome} {result.Reason}: {result.Detail}");
        trial.AssertInstallIs(trial.A, trial.Version, trial.BuildId, "after the rollback");
        Assert.Equal(TxOutcome.NothingToDo, trial.Recover().Outcome);
    }

    [Theory]
    [InlineData("swap.add", "After", 2)]
    [InlineData("swap.overwrite", "After", 1)]
    [InlineData("swap.overwrite", "After", 2)]
    [InlineData("swap.displace", "Before", 1)]
    [InlineData("swap.installed", "After", 1)]
    [InlineData("journal.verify-pending", "After", 1)]
    [InlineData("journal.commit-intent", "After", 1)]
    public void A_real_publish_folder_cut_at_a_point_is_recovered_to_one_whole_generation(string step, string edge, int nth)
    {
        using var trial = new Trial("real-cut");
        var cutter = new Cutter(new CutPoint(TxSteps.All.Single(s => s.Name == step), Enum.Parse<TxEdge>(edge), nth));
        Assert.Throws<PowerCutException>(() =>
        {
            using var gate = InstallGate.EnterExclusive(trial.ExePath);
            SwapTransaction.Run(gate, new SwapRequest(trial.Staged), Rig.Seams(cutter, FakeLauncher.Ok()));
        });
        Assert.True(cutter.Fired);

        var recovered = trial.Recover();

        Assert.NotEqual(TxOutcome.RecoveryFailed, recovered.Outcome);
        var expectNew = step == "journal.commit-intent";
        if (expectNew)
        {
            trial.AssertInstallIs(trial.B, trial.NextVersion, trial.NextBuildId, $"after a cut at {step}/{edge}#{nth} and a recovery");
        }
        else
        {
            trial.AssertInstallIs(trial.A, trial.Version, trial.BuildId, $"after a cut at {step}/{edge}#{nth} and a recovery");
        }
    }
}
