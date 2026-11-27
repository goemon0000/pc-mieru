using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;
using PcMieru.Update.Transaction;

namespace PcMieru.Update.Tests;

// The hand-off trial with the SHIPPED runner (v0.5.0's own PCMieru.Swap.exe), against the owner's REAL %LOCALAPPDATA%\PCMieru. HandOffTrialTests replaces A's swapper with the TEST BUILD, which can be redirected to a scratch L
// and told to start a harmless smoke run; the shipped Swap.exe cannot: it reads gui.ini and finds the stage in the system's L, and after a commit (or a rollback) it starts the installed PCMieru.exe plainly, with no argument.
// This trial runs exactly that, so that the hand-off of a release candidate is seen working with the bytes that are already on people's machines.
//
// THIS TEST TOUCHES THE OWNER'S DATA, so it is RealMachine-tagged and SKIPPED unless PCMIERU_OWNER_L_TRIAL names a folder that holds a file "backup.ok". Whoever runs it must have, first, backed up and hashed
// %LOCALAPPDATA%\PCMieru, %APPDATA%\PartsLife and %APPDATA%\PCMieru (and exported HKCU\...\Run), and must, whatever happens (also on failure), kill what the trial started and put the folders back byte for byte and
// verify that (identical set of files and hashes). The test does not do that for the caller: it only (1) puts update.mode=auto at the end of the real gui.ini for the run (the shipped runner swaps only when gui.ini says
// so) and puts the original bytes and time back in its finally, and (2) deletes its own scratch root. What the runner and the started program add to L (the stage under L\update\<id>, state files) is what the caller's
// restore removes.
//
// What is real: A = the shipped v0.5.0 files (its own manifest, its own PCMieru.Swap.exe, recover.exe made by the launcher's own RunnerHandOff.Prepare), B = the candidate (PCMIERU_TRIAL_B, the whole publish: folder, zip,
// outer manifest, SHA256.txt), the stage made by the product's UpdateService in the real L, the GUI's side of the pipe = the product's GuiHandOff inside the TxChild double, the runner = the shipped exe in a real process.
// The install I is a scratch folder under PCM_TEST_TMP (never the owner's install). No PCMIERU_SWAP_* variable is given to anything (the shipped runner ignores them; they are removed from the child's environment).
//
// The runner's last act is the plain start of I\PCMieru.exe. The trial waits for that process (its command line must not be --verify-update), records its command line and file version, and kills it and everything
// else that runs from under the scratch root at once: nobody ever interacts with it. A dialog or a UAC prompt is never answered; the processes are killed and the run fails.
public sealed class OwnerLocalTrialFactAttribute : FactAttribute
{
    public OwnerLocalTrialFactAttribute() => Skip = OwnerLocalTrial.SkipReason(Environment.GetEnvironmentVariable);
}

internal static class OwnerLocalTrial
{
    public const string Variable = "PCMIERU_OWNER_L_TRIAL";
    public const string MarkerName = "backup.ok";

    public static string? SkipReason(Func<string, string?> getVariable)
    {
        if (TrialEnvironment.SkipReason(getVariable) is { } why)
        {
            return why;
        }

        var folder = getVariable(Variable);
        return string.IsNullOrWhiteSpace(folder) || !File.Exists(Path.Combine(folder, MarkerName))
            ? $"this trial runs against the owner's real %LOCALAPPDATA%\\PCMieru: set {Variable} to a folder that holds '{MarkerName}', written after the folders were backed up (and restore them afterwards)"
            : null;
    }

    /// <summary>The same answer the shipped runner gets (SwapApply.DefaultLocalFolder).</summary>
    public static string OwnerLocal() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMieru");
}

/// <summary>The command line of another process of this user (NtQueryInformationProcess, class 60), so that a plain start can be told from a launch check.</summary>
internal static class OwnerTrialProcess
{
    public static string? CommandLine(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            _ = NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out var needed);
            if (needed < 16 || needed > 65536)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(handle, 60, buffer, needed, out _) != 0)
                {
                    return null;
                }

                var bytes = (ushort)Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(text, bytes / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, IntPtr buffer, int length, out int returned);
}

[Collection(FeedCollection.Name)]
[Trait("Category", "RealMachine")]
public sealed class OwnerLocalHandOffTrialTests
{
    private const string BrokenBuildId = "44444444-4444-4444-8444-444444444444";

    private static readonly TimeSpan GuiLimit = TimeSpan.FromSeconds(120);

    /// <summary>From "accepted" to the plain start: the swap, the launch checks (up to three), the start.</summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(20);

    [OwnerLocalTrialFact]
    public Task The_shipped_runner_swaps_in_the_candidate_checks_it_and_starts_it_plainly() => RunCaseAsync("good", broken: false);

    [OwnerLocalTrialFact]
    public Task The_shipped_runner_rolls_a_candidate_whose_launch_check_fails_back_and_starts_the_old_one() => RunCaseAsync("broken", broken: true);

    private sealed record Started(int Pid, string Path, string CommandLine, string? FileVersion, string? ProductVersion);

    private static async Task RunCaseAsync(string label, bool broken)
    {
        using var quiet = new QuietCrashes();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var evidence = new List<string>();
        var failures = new List<string>();
        var local = OwnerLocalTrial.OwnerLocal();
        var guiIni = Path.Combine(local, "gui.ini");
        byte[]? iniBytes = null;
        var iniTime = default(DateTime);
        var iniChanged = false;
        TrialPaths? paths = null;
        try
        {
            var running = Process.GetProcesses().Select(p => p.ProcessName).Where(n => n.StartsWith("PCMieru", StringComparison.OrdinalIgnoreCase) || n.StartsWith("PartsLife", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(running.Count == 0, "a PCMieru or Parts Life process runs (it is the owner's, never closed): " + string.Join(", ", running));
            Assert.True(File.Exists(ManagedChild.ChildExe()), "the TxChild double is not built with this configuration: " + ManagedChild.ChildExe());
            var a = Resolve(TrialEnvironment.PublishA);
            var b = Resolve(TrialEnvironment.PublishB);
            Assert.True(AppVersion.TryParse(a.Version, out var older) && AppVersion.TryParse(b.Version, out var newer) && older < newer, $"B ({b.Version}) must be newer than A ({a.Version})");
            Assert.NotEqual(a.BuildId, b.BuildId);
            Assert.True(b.ReleaseProblem() is null, b.ReleaseProblem());
            evidence.Add($"A {a.Version} build {a.BuildId} ({a.Folder})");
            evidence.Add($"B {b.Version} build {b.BuildId} ({b.Folder})");
            evidence.Add($"real L: {local}");

            paths = TrialPaths.Create("ol-" + label);
            var planted = new TrialPlanted(paths.Install, paths.Local);
            var installedA = InstallShippedA(a, paths, planted);
            var prepared = RunnerHandOff.Prepare(paths.Install);
            Assert.True(prepared.IsReady, "the launcher's own check (RunnerHandOff.Prepare) does not accept the shipped runner: " + prepared);
            Assert.True(RunnerHandOff.Check(paths.Install).IsReady, "RunnerHandOff.Check does not accept the shipped runner");
            var swapLine = a.Manifest.Files.First(f => f.Path == RealRunnerPlacement.SwapName);
            evidence.Add($"recover.exe sha256 {Rig.Sha(File.ReadAllBytes(paths.Tx.Recover))} = shipped Swap.exe line {swapLine.Sha256}");
            Assert.Equal(swapLine.Sha256, Rig.Sha(File.ReadAllBytes(paths.Tx.Recover)));

            // The shipped runner swaps only when the real gui.ini says update.mode=auto: for this run, and restored in the finally (and by the caller's restore).
            iniChanged = true;
            if (File.Exists(guiIni))
            {
                iniBytes = File.ReadAllBytes(guiIni);
                iniTime = File.GetLastWriteTimeUtc(guiIni);
                File.AppendAllText(guiIni, Environment.NewLine + "update.mode=auto" + Environment.NewLine);
            }
            else
            {
                Directory.CreateDirectory(local);
                File.WriteAllText(guiIni, "update.mode=auto" + Environment.NewLine);
            }

            var (release, target) = broken ? TrialRelease.Broken(b, BrokenBuildId) : TrialRelease.Real(b);
            var staged = await StageIntoOwnerLocalAsync(release, a.Version, paths, local, cts.Token);
            evidence.Add($"staged: {staged.Folder} (version {staged.Version}, build {staged.BuildId})");

            var chain = RunGuiDouble(paths, staged);
            evidence.Add($"GUI double exit {chain.ExitCode}: {chain.Output.Trim()}");
            if (chain.ExitCode != 0 || !chain.Output.Contains("handoff accepted", StringComparison.Ordinal))
            {
                failures.Add($"the hand-off was not accepted (the GUI double ended with {chain.ExitCode})");
            }

            var started = failures.Count == 0 ? WaitForPlainStart(paths, StartLimit) : null;
            if (started is null)
            {
                failures.Add("no plain start of I\\PCMieru.exe was seen");
            }
            else
            {
                evidence.Add($"started: pid {started.Pid} {started.Path} command line [{started.CommandLine}] file version {started.FileVersion} product version {started.ProductVersion}");
                var expect = broken ? a.Version : b.Version;
                if (!(started.CommandLine.Trim().Trim('"').Equals(paths.ExePath, StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add($"the program was not started plainly: [{started.CommandLine}]");
                }

                if (!((started.FileVersion ?? string.Empty).StartsWith(expect, StringComparison.Ordinal) || (started.ProductVersion ?? string.Empty).StartsWith(expect, StringComparison.Ordinal)))
                {
                    failures.Add($"the program that was started is not version {expect} (file {started.FileVersion}, product {started.ProductVersion})");
                }
            }

            // everything of the trial must be gone before the disk is judged (the runner ends by itself after its last act)
            TrialWait.Until(() => TrialProcesses.Under(paths.Root).Count == 0, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(500));
            var lingering = TrialProcesses.KillUnder(paths.Root);
            evidence.Add("still running 15 s after the kill: " + (lingering.Count == 0 ? "nothing" : string.Join("; ", lingering)));

            var last = TrialDisk.LastResult(paths.Tx.LastResult);
            evidence.Add($"I\\update\\last-result.json: {Raw(paths.Tx.LastResult)}");
            evidence.Add($"I\\update\\blocked.json: {Raw(paths.Tx.Blocked)}");
            evidence.Add($"I\\update\\installed.json: {Raw(paths.Tx.Installed)}");
            evidence.Add($"real L\\update (names): {Listing(Path.Combine(local, "update"))}");
            var blocked = BlockedTags.Read(paths.Tx.Blocked);
            var generation = broken
                ? new TrialGeneration(a.Version, a.BuildId, a.Manifest.Files, a.ManifestBytes)
                : new TrialGeneration(b.Version, b.BuildId, b.Manifest.Files, b.ManifestBytes);
            var treeDiff = Rig.Diff(generation.Tree(planted.InInstall), Rig.Snapshot(paths.Install));
            evidence.Add($"I outside update\\ against generation {generation.Version}: " + (treeDiff.Length == 0 ? "identical (files and hashes, plus the planted files)" : treeDiff));
            var plantedDiff = planted.InstallDifference();
            evidence.Add("the owner's planted files in I: " + (plantedDiff.Length == 0 ? "byte-identical" : plantedDiff));

            if (broken)
            {
                Expect(failures, last == ("rolledBack", "launchFailed"), $"last-result.json (outcome, reason): expected rolledBack/launchFailed, got {last}");
                Expect(failures, blocked.Any(t => t.BuildId == BrokenBuildId), $"I\\update\\blocked.json has no tag for build {BrokenBuildId}");
                Expect(failures, blocked.All(t => t.BuildId != b.BuildId), "the build of the REAL B is blocked");
            }
            else
            {
                Expect(failures, last == ("committed", "none"), $"last-result.json (outcome, reason): expected committed/none, got {last}");
                Expect(failures, blocked.All(t => t.BuildId != b.BuildId), "a build that was swapped in and verified is in blocked.json");
                var installedText = File.Exists(paths.Tx.Installed) ? File.ReadAllText(paths.Tx.Installed) : string.Empty;
                Expect(failures, installedText.Contains(b.BuildId, StringComparison.Ordinal) && installedText.Contains(b.Version, StringComparison.Ordinal), "installed.json is not B's (version and build id)");
                var exact = File.Exists(paths.Tx.Installed) && File.ReadAllBytes(paths.Tx.Installed).AsSpan().SequenceEqual(InstalledRecord.FromBundle(b.Manifest, b.ManifestBytes).ToUtf8Bytes());
                evidence.Add("installed.json equals the record the product writes for B, byte for byte: " + exact);
            }

            Expect(failures, treeDiff.Length == 0, "the tree of I is not what it must be: " + treeDiff);
            Expect(failures, plantedDiff.Length == 0, "the owner's planted files in I changed: " + plantedDiff);
            _ = installedA;
            _ = target;
        }
        finally
        {
            string? cleanup = null;
            try
            {
                if (paths is not null)
                {
                    TrialWait.Until(() => TrialProcesses.KillUnder(paths.Root).Count == 0, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(500));
                }

                if (iniChanged)
                {
                    if (iniBytes is null)
                    {
                        File.Delete(guiIni);
                    }
                    else
                    {
                        File.WriteAllBytes(guiIni, iniBytes);
                        File.SetLastWriteTimeUtc(guiIni, iniTime);
                    }
                }

                if (paths is not null && !paths.Delete())
                {
                    cleanup = $"the scratch root '{paths.Root}' could not be deleted";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            {
                cleanup = ex.GetType().Name + ": " + ex.Message;
            }

            evidence.Add("cleanup: " + (cleanup ?? "gui.ini put back, scratch root deleted"));
            var folder = Environment.GetEnvironmentVariable(OwnerLocalTrial.Variable);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                File.WriteAllLines(Path.Combine(folder, $"evidence-{label}.txt"), evidence.Concat(failures.Select(f => "FAILED: " + f)));
            }

            if (cleanup is not null)
            {
                failures.Add(cleanup);
            }
        }

        Assert.True(failures.Count == 0, $"{label}: " + string.Join(Environment.NewLine, failures.Select(f => "  - " + f)) + Environment.NewLine + string.Join(Environment.NewLine, evidence));
    }

    private static void Expect(List<string> failures, bool holds, string what)
    {
        if (!holds)
        {
            failures.Add(what);
        }
    }

    private static TrialPublish Resolve(string variable)
    {
        Assert.True(TrialPublish.TryResolve(Environment.GetEnvironmentVariable(variable), out var publish, out var problem), $"{variable}: {problem}");
        return publish ?? throw new InvalidOperationException($"{variable}: the publish was found but not returned");
    }

    /// <summary>A as shipped: its files and its own manifest bytes, the record the product writes for it, and the owner's stand-in files planted in I. Nothing of A is replaced (the swapper stays the shipped one).</summary>
    private static TrialGeneration InstallShippedA(TrialPublish a, TrialPaths paths, TrialPlanted planted)
    {
        var files = a.ReadFiles();
        Assert.True(files.ContainsKey(RealRunnerPlacement.SwapName), $"the publish of A has no {RealRunnerPlacement.SwapName}");
        foreach (var (name, data) in files)
        {
            var path = TxPaths.Under(paths.Install, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }

        File.WriteAllBytes(Path.Combine(paths.Install, BundleFormat.InnerManifestName), a.ManifestBytes);
        Directory.CreateDirectory(paths.Tx.Update);
        File.WriteAllBytes(paths.Tx.Installed, InstalledRecord.FromBundle(a.Manifest, a.ManifestBytes).ToUtf8Bytes());
        foreach (var (name, data) in planted.InInstall)
        {
            var path = TxPaths.Under(paths.Install, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }

        return new TrialGeneration(a.Version, a.BuildId, a.Manifest.Files, a.ManifestBytes);
    }

    /// <summary>The product's own UpdateService stages B into the REAL L (where the shipped runner looks): L\update\&lt;id&gt;\cache\&lt;tx&gt;. Everything else of the trial is asserted to be under the scratch root.</summary>
    private static async Task<StagedUpdate> StageIntoOwnerLocalAsync(FakeRelease release, string installedVersion, TrialPaths paths, string local, CancellationToken ct)
    {
        Assert.True(AppVersion.TryParse(installedVersion, out var running), "the installed version is not X.Y.Z: " + installedVersion);
        paths.AssertAllUnderRoot();
        var state = InstallId.StateFolder(local, paths.ExePath) ?? throw new InvalidOperationException("the scratch install has no id (is its gate file there?)");
        Assert.True(TrialPaths.IsStrictlyUnder(local, state), $"the state folder '{state}' is not under the real L '{local}'");

        var github = new FakeGitHub();
        release.PublishTo(github);
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var service = new UpdateService(new UpdateServiceOptions
        {
            Running = running,
            Installed = running,
            StateFolder = state,
            Clock = clock,
            InstallFolder = paths.Install,
            PlacementOf = _ => new Placement(PlacementKind.CanSwap, "trial"),
            SignatureInspector = null,
            DiskSpace = null,
            TransportFactory = () => new UpdateHttp(running.ToString(), github, clock: clock),
        });

        var check = await service.CheckAsync(UpdateMode.Automatic, manual: true, ct);
        Assert.True(check.Kind == UpdateCheckKind.Candidate, $"the check found no candidate: {check.Kind} {check.Refusal} ({check.Detail})");
        var stage = await service.StageAsync(UpdateMode.Automatic, check, ct);
        Assert.True(stage.Kind == UpdateStageKind.Staged, "the stage did not happen: " + stage);
        var staged = stage.Staged ?? throw new InvalidOperationException("the stage says it happened but names no folder");
        Assert.True(TrialPaths.IsStrictlyUnder(local, staged.Folder), $"the stage '{staged.Folder}' is not under the real L '{local}'");
        return staged;
    }

    /// <summary>The GUI's side of the pipe (the product's GuiHandOff in the TxChild double) towards I\update\recover.exe. No PCMIERU_SWAP_* variable.</summary>
    private static (int ExitCode, string Output) RunGuiDouble(TrialPaths paths, StagedUpdate staged)
    {
        var info = new ProcessStartInfo(ManagedChild.ChildExe()) { WorkingDirectory = paths.Root };
        foreach (var argument in new[] { "handoff", paths.Install, staged.Folder, staged.ManifestSha256, staged.Version, staged.BuildId, staged.Tag })
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("PCMIERU_SWAP", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(key);
        }

        var ran = BoundedProcess.Run(info, GuiLimit);
        return (ran.ExitCode, ran.Output);
    }

    /// <summary>
    /// Waits for the plain start of I\PCMieru.exe (a process of exactly that file whose command line is not a launch check), and kills it and its children the moment it is seen. Gives up at the limit, or when nothing of
    /// the trial has run for <see cref="IdleGrace"/>.
    /// </summary>
    private static Started? WaitForPlainStart(TrialPaths paths, TimeSpan limit)
    {
        var until = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        long? idleSince = null;
        var nextIdleLook = 0L;
        while (Environment.TickCount64 < until)
        {
            foreach (var process in Process.GetProcessesByName("PCMieru"))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited || !string.Equals(process.MainModule?.FileName, paths.ExePath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var line = OwnerTrialProcess.CommandLine(process.Id) ?? "(unreadable)";
                        if (line.Contains("--verify-update", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var info = process.MainModule!.FileVersionInfo;
                        var started = new Started(process.Id, paths.ExePath, line, info.FileVersion, info.ProductVersion);
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(15_000);
                        _ = TrialProcesses.KillUnder(paths.Root);
                        return started;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                        // not there any more, or not yet readable: asked again in a moment
                    }
                }
            }

            if (Environment.TickCount64 >= nextIdleLook)
            {
                nextIdleLook = Environment.TickCount64 + 1000;
                if (TrialProcesses.Under(paths.Root).Count > 0)
                {
                    idleSince = null;
                }
                else
                {
                    idleSince ??= Environment.TickCount64;
                    if (Environment.TickCount64 - idleSince.Value > (long)IdleGrace.TotalMilliseconds)
                    {
                        return null;
                    }
                }
            }

            Thread.Sleep(50);
        }

        return null;
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
            return text.Length > 1500 ? text[..1500] + "..." : text.Trim();
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
