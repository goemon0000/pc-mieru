using System.Diagnostics;
using System.Text;
using PcMieru.Update.Feed;
using PcMieru.Update.Launch;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Tests;

/// <summary>
/// The managed doubles of tests/PcMieru.TxChild and tests/PcMieru.Swap.TestBuild as the FILES of an install: the child as <c>PCMieru.exe</c> (it speaks the verify protocol with the product's own admission and
/// session, and says which generation it is), the test build of the swapper as <c>PCMieru.Swap.exe</c> and as <c>I\update\recover.exe</c> (the real runner, in a real process).
/// </summary>
internal static class ManagedChild
{
    private static readonly string[] ChildFiles = ["PCMieru.TxChild.dll", "PCMieru.TxChild.runtimeconfig.json", "PCMieru.TxChild.deps.json", "PcMieru.Update.dll", "PcMieru.Wire.dll"];
    private static readonly string[] RunnerFiles = ["PCMieru.Swap.TestBuild.dll", "PCMieru.Swap.TestBuild.runtimeconfig.json", "PCMieru.Swap.TestBuild.deps.json", "PcMieru.Update.dll", "PcMieru.Wire.dll"];

    public static string ChildFolder() => Path.Combine(RepoFiles.Root(), "tests", "PcMieru.TxChild", "bin", RepoFiles.Configuration(), "net10.0-windows");

    public static string SwapFolder() => Path.Combine(RepoFiles.Root(), "tests", "PcMieru.Swap.TestBuild", "bin", RepoFiles.Configuration(), "net10.0-windows");

    public static string ChildExe() => Path.Combine(ChildFolder(), "PCMieru.TxChild.exe");

    /// <summary>The generation's <c>PCMieru.exe</c> is the child double (with the files it needs), and says which generation it is and how it behaves when it is asked to verify.</summary>
    public static void AsProgram(SyntheticSet set, string generation, string mode = "good")
    {
        set.Files["PCMieru.exe"] = File.ReadAllBytes(ChildExe());
        foreach (var name in ChildFiles)
        {
            set.Files[name] = File.ReadAllBytes(Path.Combine(ChildFolder(), name));
        }

        set.Files["generation.txt"] = Encoding.UTF8.GetBytes(generation + "\n");
        set.Files["txchild-mode.txt"] = Encoding.UTF8.GetBytes(mode + "\n");
    }

    /// <summary>The generation's <c>PCMieru.Swap.exe</c> is the test build of the swapper (so that a recover.exe made from it is the recorded one).</summary>
    public static void AsSwapper(SyntheticSet set) => set.Files["PCMieru.Swap.exe"] = File.ReadAllBytes(Path.Combine(SwapFolder(), "PCMieru.Swap.TestBuild.exe"));

    /// <summary>Both generations are the doubles: the old one is "0.5.0" and always good; the new one is "0.5.1" and behaves as <paramref name="newMode"/> when it is asked to verify.</summary>
    public static Action<SyntheticSet, SyntheticSet> Shape(string newMode = "good") => (old, next) =>
    {
        AsProgram(old, "0.5.0");
        AsProgram(next, "0.5.1", newMode);
        AsSwapper(old);
        AsSwapper(next);
    };

    /// <summary>Puts the real runner (a process of the swapper's test build) where it belongs: <c>I\update\recover.exe</c> and what a managed program needs beside it.</summary>
    public static void PlaceRunner(string install)
    {
        var update = Path.Combine(install, "update");
        Directory.CreateDirectory(update);
        File.Copy(Path.Combine(SwapFolder(), "PCMieru.Swap.TestBuild.exe"), Path.Combine(update, "recover.exe"), overwrite: true);
        foreach (var name in RunnerFiles)
        {
            File.Copy(Path.Combine(SwapFolder(), name), Path.Combine(update, name), overwrite: true);
        }
    }

    public static bool IsGone(int pid)
    {
        var until = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < until)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }
}

/// <summary>
/// The launch check with REAL processes: <see cref="ProductionLaunchVerifier"/> starts the child double as the new generation's <c>PCMieru.exe --verify-update</c> (limited inheritance, a job object) from inside the
/// swap transaction, the double admits itself with the product's own <see cref="VerifyAdmission"/> from the gate handle it was handed, and every way the check can go wrong ends the swap as a rollback with the child dead.
/// </summary>
public sealed class LaunchProcessTests
{
    private sealed class ShorterCheck(ILaunchVerifier inner) : ILaunchVerifier
    {
        public List<LaunchRequest> Requests { get; } = [];

        public LaunchVerdict LaunchAndVerify(LaunchRequest request)
        {
            Requests.Add(request);
            return inner.LaunchAndVerify(request with { UiCheck = TimeSpan.FromSeconds(2), Total = TimeSpan.FromSeconds(40) });
        }
    }

    private sealed record Attempt(int Number, nint Gate, nint Pipe, string Report)
    {
        public int Pid => int.Parse(Lines.First(l => l.StartsWith("pid ", StringComparison.Ordinal))[4..], System.Globalization.CultureInfo.InvariantCulture);

        public string[] Lines => File.ReadAllLines(Report);

        public string Inherited => Lines.First(l => l.StartsWith("inherited ", StringComparison.Ordinal))[10..];
    }

    private sealed record Run(TxResult Result, Rig Rig, ShorterCheck Check, List<Attempt> Attempts, TimeSpan Took);

    private static Run SwapWith(string mode, Rig rig, bool inspectInheritance = false)
    {
        var attempts = new List<Attempt>();
        using var gate = rig.Enter();
        Assert.True(gate.IsOpen, "the gate must be enterable: " + gate.Reason);
        var verifier = new ProductionLaunchVerifier(gate, new VerifyTimings { DrawnBudget = TimeSpan.FromSeconds(4) })
        {
            ArgumentsOf = (tx, g, p) =>
            {
                var report = Path.Combine(rig.Root, $"report-{attempts.Count + 1}.txt");
                attempts.Add(new Attempt(attempts.Count + 1, g, p, report));
                var arguments = VerifyAdmission.ArgumentsFor(tx, g, p) + " " + mode + " " + LaunchNative.Quote(report);
                return inspectInheritance ? FormattableString.Invariant($"{arguments} {Environment.ProcessId}") : arguments;
            },
        };
        var check = new ShorterCheck(verifier);
        var clock = Stopwatch.StartNew();
        var result = SwapTransaction.Run(gate, new SwapRequest(rig.Staged), Rig.Seams(launcher: check));
        return new Run(result, rig, check, attempts, clock.Elapsed);
    }

    [Fact]
    public void A_good_child_is_verified_for_the_check_time_and_the_swap_is_committed()
    {
        using var rig = Rig.Create("lp-good", recorded: true, ManagedChild.Shape());
        var run = SwapWith("good", rig);
        Assert.Equal(TxOutcome.Committed, run.Result.Outcome);
        Assert.Single(run.Attempts);
        Assert.InRange(run.Took.TotalSeconds, 2, 40); // at least the two seconds of good answers, then done
        Assert.True(ManagedChild.IsGone(run.Attempts[0].Pid), "the verified child must have ended after done");
        rig.AssertTree(Generation.New, "after a verified swap");
        using var again = rig.Enter();
        Assert.True(again.IsOpen, "the gate must be free once the runner is done: " + again.Reason);
    }

    [Fact]
    public void The_child_gets_exactly_the_gate_and_the_pipe_and_nothing_else()
    {
        using var rig = Rig.Create("lp-handles", recorded: true, ManagedChild.Shape());
        Assert.True(LaunchNative.TryCreatePipe(out var extraServer, out var extraInheritableClient));
        using var server = extraServer;
        using var client = extraInheritableClient;
        var run = SwapWith("good", rig, inspectInheritance: true);
        Assert.Equal(TxOutcome.Committed, run.Result.Outcome);
        var attempt = run.Attempts.Single();
        var expected = new[] { (ulong)attempt.Gate, (ulong)attempt.Pipe }.Order().Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(string.Join(',', expected), attempt.Inherited);
    }

    [Theory]
    [InlineData("hang")]
    [InlineData("nopong")]
    [InlineData("die")]
    [InlineData("dielate")]
    [InlineData("nodrawn")]
    public void Every_way_the_check_can_fail_ends_as_a_rollback_with_the_child_dead_and_the_build_blocked(string mode)
    {
        using var rig = Rig.Create("lp-fail-" + mode, recorded: true, ManagedChild.Shape());
        var run = SwapWith(mode, rig);
        Assert.Equal(TxOutcome.RolledBack, run.Result.Outcome);
        Assert.Equal(TxReason.LaunchFailed, run.Result.Reason);
        Assert.Equal(TxLimits.MaxLaunchAttempts, run.Attempts.Count);
        Assert.All(run.Attempts, attempt => Assert.True(ManagedChild.IsGone(attempt.Pid), $"attempt {attempt.Number} ({mode}): the child must be killed"));
        rig.AssertTree(Generation.Old, "after a failed launch check (" + mode + ")");
        var blocked = BlockedTags.Read(rig.Paths.Blocked);
        Assert.Contains(blocked, tag => tag.BuildId == rig.New.BuildId);
        Assert.InRange(run.Took.TotalSeconds, 0, 90);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12abc")]
    [InlineData("zzzzzzzzzzzz 4000000000 4000000004")]
    public void A_hand_typed_verify_command_with_bad_arguments_is_refused_before_anything_starts(string tail)
    {
        using var rig = Rig.Create("lp-hand-bad", recorded: true, ManagedChild.Shape());
        var info = new ProcessStartInfo(rig.ExePath, VerifyAdmission.Option + " " + tail) { WorkingDirectory = rig.Install };
        var ran = BoundedProcess.Run(info, TimeSpan.FromSeconds(60));
        Assert.Equal(VerifyExit.BadArguments, ran.ExitCode);
        Assert.False(File.Exists(Path.Combine(rig.Root, "started.log")));
    }

    [Fact]
    public void A_hand_typed_verify_command_with_numbers_that_are_not_handles_is_refused_and_changes_nothing()
    {
        using var rig = Rig.Create("lp-hand-numbers", recorded: true, ManagedChild.Shape());
        var before = Rig.Snapshot(rig.Install, includeUpdate: true);
        var info = new ProcessStartInfo(rig.ExePath, VerifyAdmission.Option + " 12abc0123456 4000000000 4000000004") { WorkingDirectory = rig.Install };
        var ran = BoundedProcess.Run(info, TimeSpan.FromSeconds(60));
        Assert.NotEqual(0, ran.ExitCode);
        Assert.Equal(VerifyExit.PipeInvalid, ran.ExitCode);
        Assert.False(File.Exists(Path.Combine(rig.Root, "started.log")));
        rig.AssertUnchanged(before, "a hand-typed verify command", includeUpdate: true);
    }
}

/// <summary>
/// <see cref="VerifyAdmission.Admit"/> with REAL handles in this process: the gate this test holds exclusively (and a duplicate of it, as the runner hands down), pipes, files, other installs, and a journal that does
/// or does not wait for the check. Nothing is believed from the command line alone.
/// </summary>
public sealed class VerifyAdmissionHandleTests
{
    private const string Option = VerifyAdmission.Option;

    private static string[] Args(string tx, nint gate, nint pipe) =>
        [Option, tx, ((ulong)gate).ToString(System.Globalization.CultureInfo.InvariantCulture), ((ulong)pipe).ToString(System.Globalization.CultureInfo.InvariantCulture)];

    private static (Microsoft.Win32.SafeHandles.SafeFileHandle Server, Microsoft.Win32.SafeHandles.SafeFileHandle Client) Pipe()
    {
        Assert.True(LaunchNative.TryCreatePipe(out var server, out var client));
        return (server, client);
    }

    /// <summary>Runs the swap with a launcher that looks at the world while the journal is VerifyPending (the only time admission can say yes).</summary>
    private static void WhileVerifyPending(Rig rig, Action<GateEntry, string> look)
    {
        using var gate = rig.Enter();
        Assert.True(gate.IsOpen);
        var launcher = new FakeLauncher(LaunchVerdict.Ok) { OnCall = request => look(gate, request.Tx) };
        var result = SwapTransaction.Run(gate, new SwapRequest(rig.Staged), Rig.Seams(launcher: launcher));
        Assert.Equal(TxOutcome.Committed, result.Outcome);
    }

    [Fact]
    public void The_exclusive_gate_the_runner_duplicates_and_a_pipe_are_admitted_while_the_journal_waits_for_this_check()
    {
        using var rig = Rig.Create("va-good", recorded: true);
        var admitted = false;
        WhileVerifyPending(rig, (gate, tx) =>
        {
            var (server, client) = Pipe();
            using var serverChannel = new PipeChannel(server);
            var clientValue = client.DangerousGetHandle();
            client.SetHandleAsInvalid(); // the admission owns this value now
            var copy = gate.DuplicateForChild();
            Assert.NotEqual(0, copy);
            using var entry = VerifyAdmission.Admit(Args(tx, copy, clientValue), rig.ExePath, out var code);
            Assert.NotNull(entry);
            Assert.Equal(VerifyExit.Done, code);
            Assert.Equal(tx, entry.Tx);
            Assert.True(entry.Gate.IsOpen);
            Assert.True(entry.Channel.Send("hello"));
            Assert.True(serverChannel.TryReceive(TimeSpan.FromSeconds(10), out var line));
            Assert.Equal("hello", line);
            admitted = true;
        });
        Assert.True(admitted, "the launcher was never asked");
    }

    [Fact]
    public void Another_transactions_id_is_refused_because_the_journal_is_not_waiting_for_it()
    {
        using var rig = Rig.Create("va-tx", recorded: true);
        var seen = false;
        WhileVerifyPending(rig, (gate, tx) =>
        {
            var (server, client) = Pipe();
            using var serverChannel = new PipeChannel(server);
            using (client)
            {
                var copy = gate.DuplicateForChild();
                var other = tx[0] == 'a' ? "b" + tx[1..] : "a" + tx[1..];
                using var entry = VerifyAdmission.Admit(Args(other, copy, client.DangerousGetHandle()), rig.ExePath, out var code);
                Assert.Null(entry);
                Assert.Equal(VerifyExit.NotWaitingForVerification, code);
                Assert.True(LaunchNative.IsPipe(client.DangerousGetHandle()), "a refused pipe handle is still the caller's");
                seen = true;
            }
        });
        Assert.True(seen);
    }

    [Fact]
    public void A_handle_that_is_not_a_pipe_is_refused_first()
    {
        using var rig = Rig.Create("va-notpipe", recorded: true);
        using var gate = rig.Enter();
        var copy = gate.DuplicateForChild();
        using var plain = File.OpenHandle(Path.Combine(rig.Root, "plain.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var entry = VerifyAdmission.Admit(Args("0123456789ab", copy, plain.DangerousGetHandle()), rig.ExePath, out var code);
        Assert.Null(entry);
        Assert.Equal(VerifyExit.PipeInvalid, code);
        LaunchNative.CloseHandle(copy);
    }

    [Fact]
    public void A_file_that_is_not_the_gate_is_refused_even_with_a_good_pipe()
    {
        using var rig = Rig.Create("va-notgate", recorded: true);
        var (server, client) = Pipe();
        using var serverChannel = new PipeChannel(server);
        using (client)
        using (var plain = File.OpenHandle(Path.Combine(rig.Root, "plain.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            using var entry = VerifyAdmission.Admit(Args("0123456789ab", plain.DangerousGetHandle(), client.DangerousGetHandle()), rig.ExePath, out var code);
            Assert.Null(entry);
            Assert.Equal(VerifyExit.GateNotInherited, code);
        }
    }

    [Fact]
    public void The_exclusive_gate_of_another_install_is_refused()
    {
        using var mine = Rig.Create("va-mine", recorded: true);
        using var theirs = Rig.Create("va-theirs", recorded: true);
        using var theirGate = theirs.Enter();
        var copy = theirGate.DuplicateForChild();
        var (server, client) = Pipe();
        using var serverChannel = new PipeChannel(server);
        using (client)
        {
            using var entry = VerifyAdmission.Admit(Args("0123456789ab", copy, client.DangerousGetHandle()), mine.ExePath, out var code);
            Assert.Null(entry);
            Assert.Equal(VerifyExit.GateNotInherited, code);
        }

        LaunchNative.CloseHandle(copy);
    }

    [Fact]
    public void A_gate_that_is_held_shared_like_every_ordinary_program_does_is_not_what_the_runner_gives()
    {
        using var rig = Rig.Create("va-shared", recorded: true);
        using var shared = InstallGate.EnterStartup(rig.ExePath);
        Assert.True(shared.IsOpen, shared.Reason);
        var copy = shared.DuplicateForChild();
        var (server, client) = Pipe();
        using var serverChannel = new PipeChannel(server);
        using (client)
        {
            using var entry = VerifyAdmission.Admit(Args("0123456789ab", copy, client.DangerousGetHandle()), rig.ExePath, out var code);
            Assert.Null(entry);
            Assert.Equal(VerifyExit.GateNotInherited, code);
        }

        LaunchNative.CloseHandle(copy);
    }

    [Fact]
    public void A_real_exclusive_gate_without_a_journal_that_waits_is_refused()
    {
        using var rig = Rig.Create("va-nojournal", recorded: true);
        using var gate = rig.Enter();
        var copy = gate.DuplicateForChild();
        var (server, client) = Pipe();
        using var serverChannel = new PipeChannel(server);
        using (client)
        {
            using var entry = VerifyAdmission.Admit(Args("0123456789ab", copy, client.DangerousGetHandle()), rig.ExePath, out var code);
            Assert.Null(entry);
            Assert.Equal(VerifyExit.NotWaitingForVerification, code);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Shapes_that_are_not_a_verify_command_are_refused_with_the_bad_arguments_code(int shape)
    {
        using var rig = Rig.Create("va-shape" + shape, recorded: true);
        string[] args = shape switch
        {
            0 => [Option],
            1 => [Option, "0123456789ab", "5", "5"], // the same value twice
            2 => [Option, "0123456789ab", "0", "7"], // not a handle
            _ => [Option, "0123456789AB", "9", "7"], // upper case is not a transaction id
        };
        using var entry = VerifyAdmission.Admit(args, rig.ExePath, out var code);
        Assert.Null(entry);
        Assert.Equal(VerifyExit.BadArguments, code);
    }
}

/// <summary>
/// The hand-off with a real chain of processes: the GUI double (<c>tests/PcMieru.TxChild handoff</c>, the product's own <see cref="GuiHandOff"/>) starts the real runner (<c>I\update\recover.exe</c> is a copy of the
/// swapper's test build), sends the one message over the pipe and, once the runner says "accepted", ends like the GUI does; the runner waits for that process's handle, enters the gate exclusively, re-checks from the
/// disk, swaps, starts the new generation's <c>PCMieru.exe --verify-update</c> (the child double) and, at the end, starts the generation that is left (a line in started.log).
/// </summary>
public sealed class HandOffProcessTests
{
    private sealed record Chain(int GuiExit, string GuiOutput);

    private static Chain RunGuiDouble(ApplyRig rig, StagedUpdate? staged = null)
    {
        staged ??= rig.Staged;
        var info = new ProcessStartInfo(ManagedChild.ChildExe())
        {
            WorkingDirectory = rig.Rig.Root,
        };
        foreach (var argument in new[] { "handoff", InstallGate.InstallFolderOf(rig.Rig.ExePath)!, staged.Folder, staged.ManifestSha256, staged.Version, staged.BuildId, staged.Tag })
        {
            info.ArgumentList.Add(argument);
        }

        Runner(info, rig);
        var ran = BoundedProcess.Run(info, TimeSpan.FromSeconds(120));
        return new Chain(ran.ExitCode, ran.Output);
    }

    /// <summary>The environment of the test build of the swapper (it works on this one install, with this L): passed down by the GUI double to the runner it starts.</summary>
    private static void Runner(ProcessStartInfo info, ApplyRig rig)
    {
        info.Environment[PcMieru.Swap.TestBuild.TestEntry.Variable] = InstallGate.InstallFolderOf(rig.Rig.ExePath);
        info.Environment[PcMieru.Swap.TestBuild.TestEntry.LocalVariable] = rig.Local;
    }

    private static string StartedLog(ApplyRig rig) => Path.Combine(rig.Rig.Root, "started.log");

    /// <summary>The runner is a process of its own that outlives the GUI double: waits for what it was supposed to do (a line in started.log) and for the runner to be gone.</summary>
    private static string[] WaitForStarted(ApplyRig rig, int lines)
    {
        var until = Environment.TickCount64 + 120_000;
        while (Environment.TickCount64 < until)
        {
            try
            {
                var seen = File.Exists(StartedLog(rig)) ? File.ReadAllLines(StartedLog(rig)) : [];
                if (seen.Length >= lines && RunnerGone(rig))
                {
                    return seen;
                }
            }
            catch (IOException)
            {
                // being appended to
            }

            Thread.Sleep(100);
        }

        Assert.Fail("the runner did not finish what it was asked to do. last-result: " + (rig.LastResultOnDisk()?.ToString() ?? "none"));
        return [];
    }

    private static bool RunnerGone(ApplyRig rig)
    {
        var path = rig.Rig.Paths.Recover;
        foreach (var process in Process.GetProcessesByName("recover"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase) && !process.HasExited)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // gone or not ours
            }
            finally
            {
                process.Dispose();
            }
        }

        return true;
    }

    private static void AssertUserFilesInLocalUnchanged(ApplyRig rig)
    {
        foreach (var name in ApplyRig.UserFilesInLocal)
        {
            Assert.Equal(SyntheticSet.Bytes(name, 777), File.ReadAllBytes(Path.Combine(rig.Local, name.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [Fact]
    public void A_good_new_generation_is_swapped_in_verified_and_started_and_the_users_files_are_untouched()
    {
        using var rig = new ApplyRig("hp-good", shape: ManagedChild.Shape());
        ManagedChild.PlaceRunner(rig.Rig.Install);
        var guiOutput = RunGuiDouble(rig);
        Assert.Equal(0, guiOutput.GuiExit);
        Assert.Contains("handoff accepted", guiOutput.GuiOutput);

        var started = WaitForStarted(rig, 1);
        Assert.Single(started);
        Assert.StartsWith("started 0.5.1 ", started[0], StringComparison.Ordinal);
        Assert.Equal(("committed", "none"), rig.LastResultOnDisk());
        rig.Rig.AssertTree(Generation.New, "after the chain, committed");
        AssertUserFilesInLocalUnchanged(rig);
    }

    [Fact]
    public void A_new_generation_that_cannot_be_verified_is_rolled_back_the_old_one_starts_and_the_tag_is_blocked()
    {
        using var rig = new ApplyRig("hp-back", shape: ManagedChild.Shape("die"));
        ManagedChild.PlaceRunner(rig.Rig.Install);
        var gui = RunGuiDouble(rig);
        Assert.Equal(0, gui.GuiExit);

        var started = WaitForStarted(rig, 1);
        Assert.Single(started);
        Assert.StartsWith("started 0.5.0 ", started[0], StringComparison.Ordinal);
        Assert.Equal(("rolledBack", "launchFailed"), rig.LastResultOnDisk());
        Assert.Contains(BlockedTags.Read(rig.Rig.Paths.Blocked), tag => tag.BuildId == rig.Rig.New.BuildId);
        rig.Rig.AssertTree(Generation.Old, "after the chain, rolled back");
        AssertUserFilesInLocalUnchanged(rig);
    }

    [Fact]
    public void A_runner_that_refuses_leaves_the_gui_open_and_changes_nothing()
    {
        using var rig = new ApplyRig("hp-refuse", shape: ManagedChild.Shape());
        ManagedChild.PlaceRunner(rig.Rig.Install);
        File.WriteAllText(rig.GuiIni, "update.mode=notify\n");
        var before = rig.Snapshot();
        var gui = RunGuiDouble(rig);
        Assert.Equal(ChildModes5, gui.GuiExit);
        Assert.Contains("handoff refused NotAutomatic", gui.GuiOutput);
        Assert.True(RunnerGone(rig) || SpinUntilGone(rig));
        Assert.False(File.Exists(StartedLog(rig)), "a refusal starts nothing (the GUI is still the one that is open)");
        rig.AssertUnchanged(before, "a refused hand-off");
    }

    private const int ChildModes5 = 5;

    private static bool SpinUntilGone(ApplyRig rig)
    {
        var until = Environment.TickCount64 + 30_000;
        while (Environment.TickCount64 < until && !RunnerGone(rig))
        {
            Thread.Sleep(100);
        }

        return RunnerGone(rig);
    }

    [Fact]
    public void A_stage_whose_manifest_hash_is_not_the_one_sent_is_refused_and_changes_nothing()
    {
        using var rig = new ApplyRig("hp-hash", shape: ManagedChild.Shape());
        ManagedChild.PlaceRunner(rig.Rig.Install);
        var before = rig.Snapshot();
        var wrong = rig.Staged with { ManifestSha256 = new string('0', 64) };
        var gui = RunGuiDouble(rig, wrong);
        Assert.Equal(ChildModes5, gui.GuiExit);
        Assert.Contains("handoff refused StageChanged", gui.GuiOutput);
        Assert.True(SpinUntilGone(rig));
        rig.AssertUnchanged(before, "a stage that is not the one that was sent");
    }

    [Fact]
    public void Recover_exe_started_by_hand_with_apply_and_no_message_exits_with_a_code_and_changes_nothing()
    {
        using var rig = new ApplyRig("hp-hand", shape: ManagedChild.Shape());
        ManagedChild.PlaceRunner(rig.Rig.Install);
        var before = rig.Snapshot();
        var info = new ProcessStartInfo(rig.Rig.Paths.Recover, "--apply 4000000000 4000000004") { WorkingDirectory = rig.Rig.Install };
        Runner(info, rig);
        var ran = BoundedProcess.Run(info, TimeSpan.FromSeconds(60));
        Assert.Equal(SwapExit.BadCommandLine, ran.ExitCode);
        Assert.False(File.Exists(StartedLog(rig)));
        rig.AssertUnchanged(before, "recover.exe --apply with nothing to apply");
    }
}
