using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;
using PcMieru.Update.Staging;
using PcMieru.Update.Transaction;

namespace PcMieru.Update.Tests;

// The kit of the REAL TRIAL of the update hand-off (HandOffTrialTests): a published A is installed into a scratch install I, a published B (or a B whose launch check fails) is staged into a scratch L with the product's own
// UpdateService, the GUI's side of the pipe is driven by the product's own GuiHandOff (inside the TxChild double), and the runner is the TEST BUILD of PCMieru.Swap.exe placed as I\update\recover.exe.
//
// Everything here is small on purpose, and each piece has a plain unit test (HandOffTrialKitTests) that needs no real publish: the trial itself needs publish folders made by tools/publish.ps1 and minutes of real
// processes, so it is the one thing that cannot run in a normal test run, and the helpers it stands on must not be where a mistake hides.

/// <summary>The environment variables the trial reads, and what it says when they are not there.</summary>
internal static class TrialEnvironment
{
    /// <summary>The publish of the OLD version (A): the folder tools/publish.ps1 was given as -Output, or the PCMieru-X.Y.Z-win-x64 folder inside it.</summary>
    public const string PublishA = "PCMIERU_TRIAL_A";

    /// <summary>The publish of the NEW version (B), in the same form as <see cref="PublishA"/>. Its version must be newer than A's.</summary>
    public const string PublishB = "PCMIERU_TRIAL_B";

    /// <summary>Null when both publish variables are set; otherwise why the trial is skipped. The trial is skipped (not failed) where nobody made the publishes: CI and every normal run never have them.</summary>
    public static string? SkipReason(Func<string, string?> getVariable)
    {
        var missing = new[] { PublishA, PublishB }.Where(name => string.IsNullOrWhiteSpace(getVariable(name))).ToList();
        return missing.Count == 0
            ? null
            : $"the hand-off trial needs a publish of A and of B made by tools/publish.ps1: set {string.Join(" and ", missing)} (the -Output folder, or the PCMieru-X.Y.Z-win-x64 folder in it)";
    }

    /// <summary>
    /// The folder that PCM_TEST_TMP names, as a full path on a drive letter (the runner's final start is given a report file under it, and the runner's test switch takes a drive path only: a share would be the network).
    /// The trial refuses to run without it (the helpers of the other tests fall back to the system's temp folder, which a trial that starts real programs must never do).
    /// </summary>
    public static bool TryScratchRoot(string? configured, out string root, out string problem)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured) || configured.Length < 3 || !char.IsAsciiLetter(configured[0]) || configured[1] != ':')
        {
            problem = $"set {Scratch.Variable} to a full path on a drive letter (like C:\\work\\tmp): every folder the trial makes goes under it, and nothing goes to the system's temp folder";
            return false;
        }

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        problem = string.Empty;
        return true;
    }
}

/// <summary>A publish folder made by tools/publish.ps1: the inner folder (the install's files and PCMieru.bundle.json) and, beside it in the output folder, the zip, the outer manifest and SHA256.txt.</summary>
internal sealed record TrialPublish(string Folder, string Output, string Version, string BuildId, BundleManifest Manifest, byte[] ManifestBytes)
{
    public string ZipPath => Path.Combine(Output, BundleFormat.ZipName(Version));

    public string OuterManifestPath => Path.Combine(Output, BundleFormat.OuterManifestName(Version));

    public string HashListPath => Path.Combine(Output, BundleFormat.HashListName);

    /// <summary>Finds the inner folder from what the variable holds (the output folder or the inner folder) and reads its manifest. Nothing is written; the files themselves are read by <see cref="ReadFiles"/>.</summary>
    public static bool TryResolve(string? given, out TrialPublish? publish, out string problem)
    {
        publish = null;
        if (string.IsNullOrWhiteSpace(given) || !Directory.Exists(given))
        {
            problem = $"'{given}' is not a folder";
            return false;
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(given));
        if (!File.Exists(Path.Combine(folder, BundleFormat.InnerManifestName)))
        {
            var inner = Directory.GetDirectories(folder).Where(d => File.Exists(Path.Combine(d, BundleFormat.InnerManifestName))).ToList();
            if (inner.Count != 1)
            {
                problem = inner.Count == 0
                    ? $"'{folder}' has no {BundleFormat.InnerManifestName}, and no folder in it has one: it is not a publish made by tools/publish.ps1"
                    : $"'{folder}' holds {inner.Count} publish folders ({string.Join(", ", inner.Select(Path.GetFileName))}): name the one";
                return false;
            }

            folder = inner[0];
        }

        var bytes = File.ReadAllBytes(Path.Combine(folder, BundleFormat.InnerManifestName));
        if (!BundleManifest.TryParse(bytes, out var manifest, out var verdict))
        {
            problem = $"the manifest of '{folder}' does not parse: {verdict.Fault}";
            return false;
        }

        publish = new TrialPublish(folder, Path.GetDirectoryName(folder)!, manifest.Version, manifest.BuildId, manifest, bytes);
        problem = string.Empty;
        return true;
    }

    /// <summary>What the staging needs beside the folder: the zip, the outer manifest and SHA256.txt, the three files a GitHub Release carries. Null when all three are there.</summary>
    public string? ReleaseProblem() =>
        new[] { ZipPath, OuterManifestPath, HashListPath }.FirstOrDefault(path => !File.Exists(path)) is { } missing
            ? $"'{missing}' is missing: the publish must be the whole output of tools/publish.ps1 (the inner folder, the zip, the outer manifest and SHA256.txt in one folder)"
            : null;

    /// <summary>Every file the inner manifest lists, by name, each checked against its line (a folder that is not what its manifest says is not a publish).</summary>
    public SortedDictionary<string, byte[]> ReadFiles()
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var line in Manifest.Files)
        {
            var data = File.ReadAllBytes(TxPaths.Under(Folder, line.Path));
            if (Rig.Sha(data) != line.Sha256)
            {
                throw new InvalidOperationException($"'{line.Path}' in '{Folder}' is not what the manifest says: the folder was changed after tools/publish.ps1 made it");
            }

            files[line.Path] = data;
        }

        return files;
    }
}

/// <summary>
/// One generation of an install as the trial knows it: the files of its inner manifest, the manifest's own bytes, and from them the exact tree the install folder must have (the owner's planted files too).
/// </summary>
internal sealed record TrialGeneration(string Version, string BuildId, IReadOnlyList<BundleFile> Lines, byte[] ManifestBytes)
{
    /// <summary>The generation of a set of files, with a manifest the C# writer makes of them (what tools/publish.ps1 writes is byte for byte the same: RealMachineTests proves it).</summary>
    public static TrialGeneration Of(string version, string buildId, SortedDictionary<string, byte[]> files)
    {
        var lines = files.Select(f => TestBundle.Line(f.Key, f.Value)).ToList();
        return new TrialGeneration(version, buildId, lines, new BundleManifest(version, buildId, lines).ToUtf8Bytes());
    }

    /// <summary>Every file (by SHA-256) and folder ("&lt;dir&gt;") the install folder must hold outside update\: the generation, its manifest, and the owner's files.</summary>
    public SortedDictionary<string, string> Tree(IReadOnlyDictionary<string, byte[]> userFiles)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines)
        {
            files[line.Path] = line.Sha256;
        }

        files[BundleFormat.InnerManifestName] = Rig.Sha(ManifestBytes);
        foreach (var (name, data) in userFiles)
        {
            files[name] = Rig.Sha(data);
        }

        return TrialTrees.OfHashes(files);
    }
}

internal static class TrialTrees
{
    /// <summary>The tree <see cref="Rig.Snapshot"/> would give for these files: each file by hash, and every folder on the way as "&lt;dir&gt;".</summary>
    public static SortedDictionary<string, string> OfHashes(IReadOnlyDictionary<string, string> hashes)
    {
        var tree = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, hash) in hashes)
        {
            tree[name] = hash;
            var parts = name.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                tree[string.Join('/', parts[..i])] = "<dir>";
            }
        }

        return tree;
    }

    public static SortedDictionary<string, string> Of(IReadOnlyDictionary<string, byte[]> files) =>
        OfHashes(files.ToDictionary(f => f.Key, f => Rig.Sha(f.Value), StringComparer.Ordinal));
}

/// <summary>
/// The folders of ONE trial run: one scratch root under PCM_TEST_TMP, and in it the install I, the local folder L (the stand-in for %LOCALAPPDATA%\PCMieru) and the smoke report. Short names on purpose: the engine's
/// Win32 calls take plain paths, so the deepest file of a real publish must stay below MAX_PATH. <see cref="AssertAllUnderRoot"/> is the one place that says "nothing the trial uses is anywhere else".
/// </summary>
internal sealed class TrialPaths
{
    private TrialPaths(string root)
    {
        Root = root;
        Install = Path.Combine(root, "i");
        Local = Path.Combine(root, "l");
        Report = Path.Combine(root, "smoke-report.json");
    }

    public string Root { get; }

    /// <summary>I: the scratch install (A is put here; the swap makes it B or puts A back).</summary>
    public string Install { get; }

    /// <summary>L: the scratch local folder. The stage is under <c>L\update\&lt;id&gt;</c>; the owner's files planted here must never change.</summary>
    public string Local { get; }

    /// <summary>The file the final start of the runner (<c>PCMieru.exe --smoke &lt;file&gt; --fakes</c>) writes: a full path on a drive letter, a file, under the root.</summary>
    public string Report { get; }

    public string ExePath => Path.Combine(Install, "PCMieru.exe");

    public TxPaths Tx => new(Install);

    /// <summary>A new scratch root under PCM_TEST_TMP, with I and L made. Fails (it does not fall back to the temp folder) when PCM_TEST_TMP is not a full path on a drive letter.</summary>
    public static TrialPaths Create(string label) => Create(label, Environment.GetEnvironmentVariable(Scratch.Variable));

    /// <summary>A new scratch root under <paramref name="configured"/> (what PCM_TEST_TMP holds), with I and L made. The label is cut short: the engine's Win32 calls take plain paths.</summary>
    public static TrialPaths Create(string label, string? configured)
    {
        Assert.True(TrialEnvironment.TryScratchRoot(configured, out var scratch, out var problem), problem);
        Directory.CreateDirectory(scratch);
        var paths = At(Path.Combine(scratch, $"pcm-trial-{(label.Length > 12 ? label[..12] : label)}-{Guid.NewGuid().ToString("N")[..8]}"));
        Assert.True(IsStrictlyUnder(scratch, paths.Root), $"the scratch root '{paths.Root}' is not under {Scratch.Variable} ('{scratch}')");
        Directory.CreateDirectory(paths.Install);
        Directory.CreateDirectory(paths.Local);
        return paths;
    }

    /// <summary>The paths of a run whose root is already chosen (the unit tests choose their own).</summary>
    public static TrialPaths At(string root) => new(root);

    /// <summary>True when <paramref name="path"/> is a full path that, once every "." and ".." is resolved, is inside <paramref name="root"/> (not the root itself, and not a sibling that merely starts with the same letters).</summary>
    public static bool IsStrictlyUnder(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.Length > folder.Length && full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Asserts that every path of the run, and every one in <paramref name="others"/> (the stage, the state folder, what the engine says it wrote), is under the root. One call, once the paths are known.</summary>
    public void AssertAllUnderRoot(params string[] others)
    {
        var own = new[] { Install, Local, Report, ExePath, Tx.Update, Tx.Installed, Tx.Recover, Tx.Blocked, Tx.LastResult, Tx.Journal };
        foreach (var path in own.Concat(others))
        {
            Assert.True(IsStrictlyUnder(Root, path), $"'{path}' is not under the scratch root '{Root}': the trial would touch something that is not its own");
        }
    }

    /// <summary>Deletes the scratch root (the files of a publish may be read-only; a file a dying process still holds is given a few seconds). False when it is still there.</summary>
    public bool Delete()
    {
        TrialWait.Until(
            () =>
            {
                ClearReadOnly(Root);
                Scratch.Delete(Root);
                return !Directory.Exists(Root);
            },
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(500));
        return !Directory.Exists(Root);
    }

    private static void ClearReadOnly(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: a folder that cannot be listed cannot be deleted either, and Delete says so
        }
    }
}

/// <summary>The owner's own files the trial plants (in I, and in L with a gui.ini that says update.mode=auto) and checks again after both runs: whatever the update does, they must be byte-identical.</summary>
internal sealed class TrialPlanted(string install, string local)
{
    public IReadOnlyDictionary<string, byte[]> InInstall { get; } = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["notes.txt"] = "my own notes\n"u8.ToArray(),
        ["ja/mine.txt"] = "my own japanese file\n"u8.ToArray(),
        ["plugins/deep/x.dat"] = SyntheticSet.Bytes("trial-plugin", 4000),
    };

    /// <summary>Everything in L outside update\: gui.ini (the runner reads it for update.mode) and two files of the owner's. The engine writes nothing here but under update\.</summary>
    public IReadOnlyDictionary<string, byte[]> InLocal { get; } = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["gui.ini"] = "unrelated=1\nupdate.mode=auto\n"u8.ToArray(),
        ["settings/mine.json"] = SyntheticSet.Bytes("trial-settings", 777),
        ["records/a.bin"] = SyntheticSet.Bytes("trial-records", 777),
    };

    public void Plant()
    {
        Write(install, InInstall);
        Write(local, InLocal);
    }

    /// <summary>What differs between L outside update\ and what was planted (empty: byte-identical, and nothing else there).</summary>
    public string LocalDifference() => Rig.Diff(TrialTrees.Of(InLocal), Rig.Snapshot(local));

    /// <summary>What differs between the planted files of I and the files now in I (a file the update may not touch is missing or changed). The whole tree of I is compared separately, by generation.</summary>
    public string InstallDifference()
    {
        var lines = new List<string>();
        foreach (var (name, data) in InInstall)
        {
            var path = TxPaths.Under(install, name);
            if (!File.Exists(path))
            {
                lines.Add("missing " + name);
            }
            else if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(data))
            {
                lines.Add("changed " + name);
            }
        }

        return string.Join("; ", lines);
    }

    private static void Write(string root, IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var (name, data) in files)
        {
            var path = TxPaths.Under(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
    }
}

/// <summary>What the smoke run says about itself (SmokeRun.Write): did every check pass, which version is it, and what update note does it read from its own install. Parsed from the file; a file that is being written is not a report yet.</summary>
internal sealed record TrialReport(bool Ok, string Version, string? NoteMessage, string? NoteTo, string? NoteFrom, int ChecksTotal, IReadOnlyList<string> FailedChecks)
{
    /// <summary>The report in <paramref name="json"/>; null when it is not (yet) a whole report: cut off, not an object, or without ok, version and checks.</summary>
    public static TrialReport? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? message = null, to = null, from = null;
            if (root.TryGetProperty("updateNote", out var note) && note.ValueKind == JsonValueKind.Object)
            {
                message = Text(note, "message");
                to = Text(note, "to");
                from = Text(note, "from");
            }

            var failed = new List<string>();
            foreach (var check in checks.EnumerateArray())
            {
                if (check.ValueKind == JsonValueKind.Object && check.TryGetProperty("ok", out var passed) && passed.ValueKind == JsonValueKind.False)
                {
                    failed.Add($"{Text(check, "name")}: {Text(check, "detail")}");
                }
            }

            return new TrialReport(ok.GetBoolean(), version.GetString()!, message, to, from, checks.GetArrayLength(), failed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The report in the file, or null when the file is not there, is being written, or is not a whole report. The file is opened so that its writer is not in the way.</summary>
    public static TrialReport? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>The last-result.json the runner leaves in I\update (outcome and reason), read as the GUI's note reads it.</summary>
internal static class TrialDisk
{
    public static (string Outcome, string Reason)? LastResult(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return (document.RootElement.GetProperty("outcome").GetString() ?? string.Empty, document.RootElement.GetProperty("reason").GetString() ?? string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>A wait with a limit and an answer: the trial waits for real processes, and every wait ends at its limit.</summary>
internal static class TrialWait
{
    /// <summary>Asks <paramref name="probe"/> now and then every <paramref name="every"/> until it says yes (true) or <paramref name="limit"/> has passed (false: asked one last time at the limit).</summary>
    public static bool Until(Func<bool> probe, TimeSpan limit, TimeSpan every)
    {
        var until = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        while (true)
        {
            if (probe())
            {
                return true;
            }

            if (Environment.TickCount64 >= until)
            {
                return false;
            }

            Thread.Sleep(every);
        }
    }
}

/// <summary>
/// The processes of the trial are the ones whose program file is under the scratch root (the runner is I\update\recover.exe, the program is I\PCMieru.exe): found by that and by nothing else, so that the owner's own
/// PCMieru, started from somewhere else, is never in this list and never killed.
/// </summary>
internal static class TrialProcesses
{
    /// <summary>The program files (and ids) of the running processes that were started from under <paramref name="root"/>.</summary>
    public static IReadOnlyList<string> Under(string root) => Scan(root, kill: false);

    /// <summary>Kills every process started from under <paramref name="root"/> (with its children) and returns what it killed.</summary>
    public static IReadOnlyList<string> KillUnder(string root) => Scan(root, kill: true);

    private static List<string> Scan(string root, bool kill)
    {
        var found = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.HasExited)
                    {
                        continue;
                    }

                    var path = process.MainModule?.FileName;
                    if (path is null || !TrialPaths.IsStrictlyUnder(root, path))
                    {
                        continue;
                    }

                    found.Add($"{process.Id} {path}");
                    if (kill)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(15_000);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // gone already, or not ours to look at
                }
            }
        }

        return found;
    }
}

/// <summary>
/// The process's error mode while a trial runs: no "stopped working" box (SEM_NOGPFAULTERRORBOX), no critical-error box, no open-file box. The mode is inherited by every process started afterwards without
/// CREATE_DEFAULT_ERROR_MODE, which the .NET process start and the engine's own LaunchNative both are: the TxChild double, the runner, and the new version's launch check inherit it. Without it the broken
/// B' of the trial would crash into a Windows error dialog that waits for a person, three times, until the launch check's budget kills it.
/// </summary>
internal sealed class QuietCrashes : IDisposable
{
    internal const uint Wanted = 0x1 | 0x2 | 0x8000;

    private readonly uint _previous;

    public QuietCrashes()
    {
        _previous = GetErrorMode();
        SetErrorMode(_previous | Wanted);
    }

    /// <summary>The error mode now.</summary>
    public static uint Current() => GetErrorMode();

    public void Dispose() => SetErrorMode(_previous);

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();
}

/// <summary>
/// B': a build whose launch check fails every time, made of the REAL B. <b>The choice.</b> No test build of PCMieru.exe exists, and the two ways to make a build that "never starts" differ in how close they are to a real
/// failure. (1) The TxChild double renamed into place as PCMieru.exe is a program that only says how it was told to behave: its failure is scripted, and it does not even have the real window code. (2) The real
/// PCMieru.exe of B with ONE library of B that is not what its name says: PcMieru.Core.dll holds the bytes of PcMieru.Wire.dll, another real (unsigned, valid) library of the same product. The stage accepts it
/// (the files are what the manifest says, the signature check says unsigned, like every file of the product), the swap puts it in, and the real PCMieru.exe, started by the real launch check with the real admission
/// and the real handles, dies when it first needs PcMieru.Core (the runtime refuses an assembly that is not the one it was asked for): the failure of a build whose file was damaged or mixed up, in a real program, with
/// no scripted behaviour anywhere. It fails fast (at once, not after a drawn budget), so the three attempts take seconds. The double stays the fallback if the real program ever started with a wrong library.
/// </summary>
internal static class TrialBrokenBuild
{
    public const string Victim = "PcMieru.Core.dll";
    public const string Donor = "PcMieru.Wire.dll";

    public static SortedDictionary<string, byte[]> Of(SortedDictionary<string, byte[]> good)
    {
        if (!good.TryGetValue(Victim, out var victim) || !good.TryGetValue(Donor, out var donor))
        {
            throw new InvalidOperationException($"the publish has no {Victim} or no {Donor} (it has: {string.Join(", ", good.Keys.Where(k => k.StartsWith("PcMieru", StringComparison.OrdinalIgnoreCase)))}): B' cannot be made from it");
        }

        if (victim.AsSpan().SequenceEqual(donor))
        {
            throw new InvalidOperationException($"{Victim} and {Donor} are the same bytes: replacing one by the other would not break anything");
        }

        var broken = new SortedDictionary<string, byte[]>(good, StringComparer.Ordinal)
        {
            [Victim] = donor,
        };
        return broken;
    }
}

/// <summary>A is put into the scratch install: its files, a manifest and an installed.json that name the TEST BUILD of the swapper, the runner placed as recover.exe, and the owner's files planted.</summary>
internal static class TrialInstall
{
    /// <summary>The test build of PCMieru.Swap.exe (tests/PcMieru.Swap.TestBuild, built with these tests).</summary>
    public static string TestBuildSwapPath() => Path.Combine(ManagedChild.SwapFolder(), "PCMieru.Swap.TestBuild.exe");

    /// <summary>
    /// <b>How the launcher's trust check of recover.exe is satisfied.</b> RunnerHandOff.Prepare makes I\update\recover.exe a copy of I\PCMieru.Swap.exe, and only when that file is the one the installed record names (the
    /// record is installed.json, or the manifest when there is none); RunnerHandOff.Check, and the swap transaction before its first change, compare recover.exe with the same line. So the trial makes A's
    /// PCMieru.Swap.exe the TEST BUILD (in the install, in A's manifest, and in installed.json: all three the same bytes) and places the same exe, with the files a managed program needs, as recover.exe. The product's check
    /// is not changed and not bypassed: it runs, on bytes that really are what the record says. The manifest and the record are written by the C# writer, which makes byte for byte what tools/publish.ps1 writes.
    /// </summary>
    public static TrialGeneration InstallA(TrialPublish a, TrialPaths paths, TrialPlanted planted)
    {
        var files = a.ReadFiles();
        if (!files.ContainsKey(RealRunnerPlacement.SwapName))
        {
            throw new InvalidOperationException($"the publish of A has no {RealRunnerPlacement.SwapName}: it is not a publish of this build");
        }

        files[RealRunnerPlacement.SwapName] = File.ReadAllBytes(TestBuildSwapPath());
        foreach (var (name, data) in files)
        {
            var path = TxPaths.Under(paths.Install, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }

        var generation = TrialGeneration.Of(a.Version, a.BuildId, files);
        File.WriteAllBytes(Path.Combine(paths.Install, BundleFormat.InnerManifestName), generation.ManifestBytes);
        if (!BundleManifest.TryParse(generation.ManifestBytes, out var manifest, out var verdict))
        {
            throw new InvalidOperationException("the manifest the trial wrote for A does not parse: " + verdict.Fault);
        }

        Directory.CreateDirectory(paths.Tx.Update);
        File.WriteAllBytes(paths.Tx.Installed, InstalledRecord.FromBundle(manifest, generation.ManifestBytes).ToUtf8Bytes());
        ManagedChild.PlaceRunner(paths.Install);
        planted.Plant();
        return generation;
    }
}

/// <summary>The release as GitHub would serve it (a fake GitHub that answers from memory: no network), made of a real publish or of a broken one.</summary>
internal static class TrialRelease
{
    /// <summary>B as published: the zip, the outer manifest and SHA256.txt exactly as tools/publish.ps1 made them.</summary>
    public static (FakeRelease Release, TrialGeneration Generation) Real(TrialPublish b) =>
        (
            new FakeRelease(b.Version)
            {
                ZipBytes = File.ReadAllBytes(b.ZipPath),
                ManifestBytes = File.ReadAllBytes(b.OuterManifestPath),
                ShaText = File.ReadAllText(b.HashListPath),
            },
            new TrialGeneration(b.Version, b.BuildId, b.Manifest.Files, b.ManifestBytes));

    /// <summary>B' (see <see cref="TrialBrokenBuild"/>): B's files with one library mixed up, a build id of its own, the same version (the tag the runner blocks is B's tag, with B''s build id), and a zip and manifests made for it.</summary>
    public static (FakeRelease Release, TrialGeneration Generation) Broken(TrialPublish b, string buildId)
    {
        var bundle = new TestBundle(b.Version) { BuildId = buildId };
        bundle.Files.Clear();
        foreach (var (name, data) in TrialBrokenBuild.Of(b.ReadFiles()))
        {
            bundle.Files[name] = data;
        }

        var release = new FakeRelease(b.Version) { ZipBytes = bundle.Zip(), ManifestBytes = bundle.Outer().ToUtf8Bytes() };
        return (release, new TrialGeneration(b.Version, buildId, bundle.Inner().Files, bundle.InnerBytes()));
    }
}

/// <summary>
/// The staging, with the product's own <see cref="UpdateService"/> (not a copy of what it does): a check against a fake GitHub that serves the release from memory, then <c>StageAsync</c>, which downloads, validates
/// (zip against the outer manifest, inner against outer, the signature policy, the disk space) and writes <c>L\update\&lt;id&gt;\cache\&lt;tx&gt;\{files\, release.manifest.json}</c>. The id is the install's own
/// (the volume and file id of its gate file), so the folder is the one the runner computes for the same install.
/// </summary>
internal static class TrialStage
{
    public static async Task<StagedUpdate> StageAsync(FakeRelease release, TrialGeneration installed, TrialPaths paths, ISignatureInspector? inspector, CancellationToken ct, IDiskSpace? disk = null)
    {
        Assert.True(AppVersion.TryParse(installed.Version, out var running), "the installed version is not X.Y.Z: " + installed.Version);
        var state = InstallId.StateFolder(paths.Local, paths.ExePath) ?? throw new InvalidOperationException("the scratch install has no id (is its gate file there?)");
        paths.AssertAllUnderRoot(state);

        var github = new FakeGitHub();
        release.PublishTo(github);
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var service = new UpdateService(new UpdateServiceOptions
        {
            Running = running,
            Installed = running,
            StateFolder = state,
            Clock = clock,
            InstallFolder = paths.Install,
            PlacementOf = _ => new Placement(PlacementKind.CanSwap, "trial"),
            SignatureInspector = inspector,
            DiskSpace = disk,
            TransportFactory = () => new UpdateHttp(running.ToString(), github, clock: clock),
        });

        var check = await service.CheckAsync(UpdateMode.Automatic, manual: true, ct);
        Assert.True(check.Kind == UpdateCheckKind.Candidate, $"the check found no candidate: {check.Kind} {check.Refusal} ({check.Detail})");
        var stage = await service.StageAsync(UpdateMode.Automatic, check, ct);
        Assert.True(stage.Kind == UpdateStageKind.Staged, "the stage did not happen: " + stage);
        var staged = stage.Staged ?? throw new InvalidOperationException("the stage says it happened but names no folder");
        paths.AssertAllUnderRoot(staged.Folder);
        return staged;
    }
}
