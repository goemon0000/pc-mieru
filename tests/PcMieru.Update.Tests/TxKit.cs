using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PcMieru.Update.Bundle;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Tests;

// The kit of the swap-transaction tests (DESIGN-B 11 step 4): scratch folders, a small synthetic release in two generations, the planted user files, the fault injectors and the fake launcher.
// Nothing here knows the engine's internals except through its public seams, so a test is written against what the engine promises, not against how it is built.

/// <summary>Where the tests make their folders: PCM_TEST_TMP when it is set (the worker points it at its scratchpad), the temp folder otherwise. Never an install, never a real settings folder.</summary>
internal static class Scratch
{
    public const string Variable = "PCM_TEST_TMP";

    public static string Root()
    {
        var configured = Environment.GetEnvironmentVariable(Variable);
        var root = string.IsNullOrEmpty(configured) ? Path.GetTempPath() : configured;
        Directory.CreateDirectory(root);
        return root;
    }

    public static string NewFolder(string label)
    {
        // The label is cut short on purpose: the Win32 calls of the engine take plain paths (no \\?\ prefix), so the whole path of the deepest file must stay well below MAX_PATH.
        var path = Path.Combine(Root(), $"pcm-tx-{(label.Length > 24 ? label[..24] : label)}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a scratch folder that cannot be removed is left for the next sweep
        }
    }
}

/// <summary>One generation of a small release: files by name (without the inner manifest) and the inner manifest made of them.</summary>
internal sealed class SyntheticSet(string version, string buildId)
{
    public const string OldBuild = "11111111-1111-4111-8111-111111111111";
    public const string NewBuild = "22222222-2222-4222-8222-222222222222";

    public string Version { get; } = version;

    public string BuildId { get; } = buildId;

    public SortedDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public static byte[] Bytes(string label, int length)
    {
        var random = new Random(label.Aggregate(17, (h, c) => (h * 31) + c));
        var data = new byte[length];
        random.NextBytes(data);
        return data;
    }

    /// <summary>The first generation: a changed-later exe, the swap exe, a big library (several copy chunks), an unchanged file, a file the next one drops, a satellite, a notice and the gate file.</summary>
    public static SyntheticSet Old()
    {
        var set = new SyntheticSet("0.5.0", OldBuild);
        set.Files["PCMieru.exe"] = Bytes("exe-old", 3000);
        set.Files["PCMieru.Swap.exe"] = Bytes("swap-old", 2000);
        set.Files["PcMieru.Core.dll"] = Bytes("core-old", 200_000);
        set.Files["stable.dll"] = Bytes("stable", 5000);
        set.Files["old-only.dll"] = Bytes("old-only", 1500);
        set.Files["ja/PcMieru.Core.resources.dll"] = Bytes("ja", 800);
        set.Files["NOTICE.txt"] = Bytes("notice-old", 400);
        set.Files[GateFile.FileName] = GateFile.Bytes();
        return set;
    }

    /// <summary>The next generation: three files changed, one dropped, two added (one in a folder, en, that is not there yet; a bundle may only have ja/ and en/ below the root), the rest unchanged.</summary>
    public static SyntheticSet Next(SyntheticSet old)
    {
        var set = new SyntheticSet("0.5.1", NewBuild);
        foreach (var (name, data) in old.Files)
        {
            set.Files[name] = data;
        }

        set.Files["PCMieru.exe"] = Bytes("exe-new", 3100);
        set.Files["PCMieru.Swap.exe"] = Bytes("swap-new", 2100);
        set.Files["PcMieru.Core.dll"] = Bytes("core-new", 210_000);
        set.Files["NOTICE.txt"] = Bytes("notice-new", 450);
        set.Files.Remove("old-only.dll");
        set.Files["new-feature.dll"] = Bytes("feature", 4000);
        set.Files["en/PcMieru.Core.resources.dll"] = Bytes("added", 1200);
        return set;
    }

    public byte[] ManifestBytes() => new BundleManifest(Version, BuildId, Files.Select(f => TestBundle.Line(f.Key, f.Value))).ToUtf8Bytes();

    public InstalledRecord Record()
    {
        var bytes = ManifestBytes();
        Assert.True(BundleManifest.TryParse(bytes, out var manifest, out var verdict), "the synthetic manifest must parse: " + verdict.Fault);
        return InstalledRecord.FromBundle(manifest, bytes);
    }

    /// <summary>Every file of the generation with the inner manifest, by relative name.</summary>
    public IEnumerable<KeyValuePair<string, byte[]>> AllFiles() =>
        Files.Append(new KeyValuePair<string, byte[]>(BundleFormat.InnerManifestName, ManifestBytes()));

    public void WriteTo(string folder)
    {
        foreach (var (name, data) in AllFiles())
        {
            var path = TxPaths.Under(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
    }
}

/// <summary>What a test does with the launch-and-verify seam: answers from a script (the last answer repeats) and remembers each request.</summary>
internal sealed class FakeLauncher(params LaunchVerdict[] script) : ILaunchVerifier
{
    public static FakeLauncher Ok() => new(LaunchVerdict.Ok);

    public static FakeLauncher AlwaysFail() => new(LaunchVerdict.Failed);

    public List<LaunchRequest> Calls { get; } = [];

    public Action<LaunchRequest>? OnCall { get; init; }

    public LaunchVerdict LaunchAndVerify(LaunchRequest request)
    {
        Calls.Add(request);
        OnCall?.Invoke(request);
        return script[Math.Min(Calls.Count - 1, script.Length - 1)];
    }
}

internal sealed class FixedDisk(long free) : IDiskSpace
{
    public Action? OnAsk { get; init; }

    public long FreeBytes(string folder)
    {
        OnAsk?.Invoke();
        return free;
    }
}

internal sealed class FailingPlacement(Exception failure) : IRunnerPlacement
{
    public void Place(TxIo io, PlacementContext context) => throw failure;
}

// ---------------------------------------------------------------------------------------------------------------------------- injectors

/// <summary>One place a process can be stopped: the n-th time the code reaches <paramref name="Edge"/> of <paramref name="Step"/>.</summary>
internal readonly record struct CutPoint(TxStep Step, TxEdge Edge, int Nth)
{
    public override string ToString() => $"{Step.Name}/{Edge}#{Nth}";
}

/// <summary>Records every point the code reaches, in order, and counts the occurrences per (step, edge).</summary>
internal sealed class Recorder : IFaultInjector
{
    private readonly Dictionary<(TxStep, TxEdge), int> _counts = [];

    public List<CutPoint> Points { get; } = [];

    public Action<TxStep, TxEdge, string?>? Hook { get; init; }

    public void Reach(TxStep step, TxEdge edge, string? item)
    {
        _counts.TryGetValue((step, edge), out var n);
        _counts[(step, edge)] = ++n;
        Points.Add(new CutPoint(step, edge, n));
        Hook?.Invoke(step, edge, item);
    }
}

/// <summary>Stops the process at one point: throws <see cref="PowerCutException"/> there and at every later call (a power cut is final).</summary>
internal sealed class Cutter(CutPoint at) : IFaultInjector
{
    private readonly Dictionary<(TxStep, TxEdge), int> _counts = [];

    public bool Fired { get; private set; }

    public Action? BeforeCut { get; init; }

    public void Reach(TxStep step, TxEdge edge, string? item)
    {
        if (Fired)
        {
            throw new PowerCutException("after the cut");
        }

        _counts.TryGetValue((step, edge), out var n);
        _counts[(step, edge)] = ++n;
        if (step == at.Step && edge == at.Edge && n == at.Nth)
        {
            Fired = true;
            BeforeCut?.Invoke();
            throw new PowerCutException(at.ToString());
        }
    }
}

/// <summary>Throws one ordinary I/O error at one point (the disk said no), and is silent otherwise.</summary>
internal sealed class FailOnce(CutPoint at) : IFaultInjector
{
    private readonly Dictionary<(TxStep, TxEdge), int> _counts = [];

    public bool Fired { get; private set; }

    public void Reach(TxStep step, TxEdge edge, string? item)
    {
        _counts.TryGetValue((step, edge), out var n);
        _counts[(step, edge)] = ++n;
        if (!Fired && step == at.Step && edge == at.Edge && n == at.Nth)
        {
            Fired = true;
            throw new IOException("injected: the disk did not accept the write (" + at + ")");
        }
    }
}

internal sealed class HookInjector(Action<TxStep, TxEdge, string?> hook) : IFaultInjector
{
    public void Reach(TxStep step, TxEdge edge, string? item) => hook(step, edge, item);
}

// ---------------------------------------------------------------------------------------------------------------------------- the rig

internal enum Generation
{
    Old,
    New,
}

/// <summary>
/// An install folder with the old generation and planted user files, a staged folder with the new generation, and the means to run a swap or a recovery the way the runner does: enter the gate exclusively
/// (a fresh entry each time, like a fresh process), run, dispose.
/// </summary>
internal sealed class Rig : IDisposable
{
    private Rig(string root, SyntheticSet old, SyntheticSet next)
    {
        Root = root;
        Old = old;
        New = next;
    }

    public string Root { get; }

    public string Install => Path.Combine(Root, "install");

    public string Staged => Path.Combine(Root, "staged");

    public SyntheticSet Old { get; }

    public SyntheticSet New { get; }

    public SortedDictionary<string, byte[]> UserFiles { get; } = new(StringComparer.Ordinal);

    public List<string> UserDirs { get; } = [];

    public TxPaths Paths => new(Install);

    public string ExePath => Path.Combine(Install, "PCMieru.exe");

    /// <summary>Makes the install (the old generation, with the user's own files planted) and the staged folder (the new generation). With <paramref name="recorded"/> the old installed.json is written too.</summary>
    public static Rig Create(string label, bool recorded = false, Action<SyntheticSet, SyntheticSet>? shape = null, bool plantUserFiles = true)
    {
        var old = SyntheticSet.Old();
        var next = SyntheticSet.Next(old);
        shape?.Invoke(old, next);
        var rig = new Rig(Scratch.NewFolder(label), old, next);
        Directory.CreateDirectory(rig.Install);
        old.WriteTo(rig.Install);
        next.WriteTo(rig.Staged);
        if (plantUserFiles)
        {
            rig.PlantUserFiles();
        }

        if (recorded)
        {
            Directory.CreateDirectory(rig.Paths.Update);
            File.WriteAllBytes(rig.Paths.Installed, old.Record().ToUtf8Bytes());
        }

        return rig;
    }

    /// <summary>Files and a folder the user made: in the install folder, in a folder the release also uses (ja), in a folder of their own, a second one at the top, and an empty folder.</summary>
    private void PlantUserFiles()
    {
        UserFiles["notes.txt"] = Encoding.UTF8.GetBytes("my own notes\n");
        UserFiles["ja/mine.txt"] = Encoding.UTF8.GetBytes("my own japanese file\n");
        UserFiles["plugins/deep/x.dat"] = SyntheticSet.Bytes("plugin", 5000);
        UserFiles["keep.txt"] = Encoding.UTF8.GetBytes("keep me\n");
        UserDirs.Add("empty-dir");
        foreach (var (name, data) in UserFiles)
        {
            var path = TxPaths.Under(Install, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }

        foreach (var directory in UserDirs)
        {
            Directory.CreateDirectory(TxPaths.Under(Install, directory));
        }
    }

    public void Dispose() => Scratch.Delete(Root);

    /// <summary>A directory junction (no privilege needed): <c>mklink /J</c>. Remove it with <c>Directory.Delete(link)</c> (the link only) BEFORE the rig is disposed.</summary>
    public static void Junction(string link, string target)
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Arguments = $"/c mklink /J \"{link}\" \"{target}\"";
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && Directory.Exists(link), "mklink /J failed: " + output);
    }

    // ------------------------------------------------------------------------------------------------------------------------ running

    public GateEntry Enter() => InstallGate.EnterExclusive(ExePath);

    public static TxSeams Seams(IFaultInjector? fault = null, ILaunchVerifier? launcher = null, IDiskSpace? disk = null, IRunnerPlacement? placement = null) =>
        new()
        {
            Fault = fault ?? NoFaults.Instance,
            Launcher = launcher,
            Disk = disk ?? SystemDiskSpace.Instance,
            Placement = placement ?? RealRunnerPlacement.Instance,
        };

    /// <summary>A swap of the staged folder with a fresh gate entry (disposed when the call ends, however it ends: a power cut unwinds through it).</summary>
    public TxResult Swap(TxSeams seams)
    {
        using var gate = Enter();
        Assert.True(gate.IsOpen, "the gate must be enterable: " + gate.Reason);
        return SwapTransaction.Run(gate, new SwapRequest(Staged), seams);
    }

    public TxResult Swap(ILaunchVerifier launcher, IFaultInjector? fault = null) => Swap(Seams(fault, launcher));

    /// <summary>A recovery with a fresh gate entry (no launcher unless given: the runner's local recovery).</summary>
    public TxResult Recover(ILaunchVerifier? launcher = null, bool automatic = false, IFaultInjector? fault = null)
    {
        using var gate = Enter();
        Assert.True(gate.IsOpen, "the gate must be enterable: " + gate.Reason);
        return TxRecovery.Recover(gate, Seams(fault, launcher), automatic);
    }

    // ------------------------------------------------------------------------------------------------------------------------ looking

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Every file (by SHA-256) and folder ("&lt;dir&gt;") below <paramref name="root"/>, by relative name with '/', except the update folder.</summary>
    public static SortedDictionary<string, string> Snapshot(string root, bool includeUpdate = false)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!includeUpdate && (relative == GateFile.UpdateFolderName || relative.StartsWith(GateFile.UpdateFolderName + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            result[relative] = Directory.Exists(path) ? "<dir>" : ShaOrHeld(relative, path);
        }

        return result;
    }

    /// <summary>The marker for the gate file while a swapper holds it exclusively (nothing else can open it then): its bytes and identity are asserted by their own test, and <see cref="Diff"/> accepts the marker for the gate only.</summary>
    public const string HeldGate = "<held gate>";

    private static string ShaOrHeld(string relative, string path)
    {
        try
        {
            return Sha(File.ReadAllBytes(path));
        }
        catch (IOException) when (string.Equals(relative, GateFile.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return HeldGate;
        }
    }

    public SortedDictionary<string, string> Expected(Generation generation)
    {
        var set = generation == Generation.Old ? Old : New;
        var tree = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, data) in set.AllFiles())
        {
            tree[name] = Sha(data);
        }

        foreach (var (name, data) in UserFiles)
        {
            tree[name] = Sha(data);
        }

        foreach (var directory in UserDirs)
        {
            tree[directory] = "<dir>";
        }

        foreach (var file in tree.Where(t => t.Value != "<dir>").Select(t => t.Key).ToList())
        {
            var parts = file.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                tree[string.Join('/', parts[..i])] = "<dir>";
            }
        }

        return tree;
    }

    public static string Diff(SortedDictionary<string, string> expected, SortedDictionary<string, string> actual)
    {
        var lines = new List<string>();
        foreach (var (name, hash) in expected)
        {
            if (!actual.TryGetValue(name, out var found))
            {
                lines.Add("missing " + name);
            }
            else if (found != hash && !(found == HeldGate && string.Equals(name, GateFile.FileName, StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add("changed " + name);
            }
        }

        lines.AddRange(actual.Keys.Where(name => !expected.ContainsKey(name)).Select(name => "extra " + name));
        return string.Join("; ", lines.Take(6)) + (lines.Count > 6 ? $" and {lines.Count - 6} more" : string.Empty);
    }

    /// <summary>Asserts the install folder (outside update/) is exactly the generation plus the user's files, byte for byte, with no extra file or folder.</summary>
    public void AssertTree(Generation generation, string context)
    {
        var difference = Diff(Expected(generation), Snapshot(Install));
        Assert.True(difference.Length == 0, $"{context}: the install is not all-{generation.ToString().ToLowerInvariant()}: {difference}");
    }

    /// <summary>Asserts nothing below the install folder changed since <paramref name="before"/> (a <see cref="Snapshot"/>).</summary>
    public void AssertUnchanged(SortedDictionary<string, string> before, string context, bool includeUpdate = false)
    {
        var difference = Diff(before, Snapshot(Install, includeUpdate));
        Assert.True(difference.Length == 0, $"{context}: the install changed: {difference}");
    }

    /// <summary>Whether the install folder is exactly the old or exactly the new generation (user files byte-identical either way); fails with the differences when it is neither.</summary>
    public Generation AssertWholeGeneration(string context)
    {
        var actual = Snapshot(Install);
        var vsOld = Diff(Expected(Generation.Old), actual);
        var vsNew = Diff(Expected(Generation.New), actual);
        Assert.True(vsOld.Length == 0 || vsNew.Length == 0, $"{context}: the install is neither all-old nor all-new. Against old: {vsOld}. Against new: {vsNew}");
        return vsOld.Length == 0 ? Generation.Old : Generation.New;
    }

    private static readonly string[] AllowedLeftovers =
    [
        "installed.json", "installed.json.tmp", "last-result.json", "last-result.json.tmp", "blocked.json", "blocked.json.tmp", "recover.exe", "recover.exe.new", "RECOVER.txt.tmp", "recovery-failed.tmp",
    ];

    /// <summary>
    /// After a transaction that ended (committed, rolled back, recovered): nothing of it is left but the harmless files, installed.json is the record of the generation that is installed, and there is
    /// no journal, no sentinel, no work or prev folder, no note.
    /// </summary>
    public void AssertSettled(Generation generation, string context)
    {
        var update = Paths.Update;
        if (!Directory.Exists(update))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(update))
        {
            var name = Path.GetFileName(entry);
            Assert.True(AllowedLeftovers.Contains(name, StringComparer.Ordinal), $"{context}: '{name}' is left in the update folder");
        }

        var set = generation == Generation.Old ? Old : New;
        var record = Paths.Installed;
        if (File.Exists(record))
        {
            Assert.Equal(set.Record().ToUtf8Bytes(), File.ReadAllBytes(record));
        }
        else
        {
            Assert.True(generation == Generation.Old, $"{context}: installed.json is missing for the {generation} generation");
        }
    }

    /// <summary>The whole check for a recovery that ended well: whole generation, settled.</summary>
    public Generation AssertEnded(string context)
    {
        var generation = AssertWholeGeneration(context);
        AssertSettled(generation, context);
        return generation;
    }
}

internal static class RepoFiles
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcMieru.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("PcMieru.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The configuration folder name the tests run from (Release or Debug), read from the path of the test assembly.</summary>
    public static string Configuration()
    {
        var parts = AppContext.BaseDirectory.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var bin = Array.FindLastIndex(parts, p => p.Equals("bin", StringComparison.OrdinalIgnoreCase));
        return bin >= 0 && bin + 1 < parts.Length ? parts[bin + 1] : "Release";
    }
}

/// <summary>Runs work for many cut points on several threads and collects the failures (an assertion failure in a worker would otherwise be lost).</summary>
internal static class Parallelism
{
    public static void ForEach<T>(IEnumerable<T> items, Action<T> body, string what)
    {
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8) }, item =>
        {
            try
            {
                body(item);
            }
            catch (Exception ex)
            {
                failures.Add($"{item}: {ex.GetType().Name}: {ex.Message}");
            }
        });
        Assert.True(failures.IsEmpty, $"{what}: {failures.Count} failed.{Environment.NewLine}" + string.Join(Environment.NewLine, failures.Take(8)));
    }
}
