using System.Diagnostics;
using PcMieru.Update.Transaction;
using static PcMieru.Update.Tests.Observation;

namespace PcMieru.Update.Tests;

// The power-cut tests with a REAL process (the in-process ones are in TxFaultMatrixTests). The child (tests/PcMieru.TxChild) runs the transaction or a recovery and stops itself at a named step; the test then
// kills the whole process (Process.Kill: no finally, no Dispose, nothing unwinds, the exclusive gate handle goes away with the process) and a fresh recovery has to leave one whole generation.
// What an exception cannot show and a kill does: the gate is released by the OS, a half-written file is whatever the disk had, and nothing in the child got a chance to "clean up".

public sealed class TxKillTests
{
    private static readonly TimeSpan WaitForMarker = TimeSpan.FromSeconds(60);

    private static string ChildExe()
    {
        var exe = Path.Combine(RepoFiles.Root(), "tests", "PcMieru.TxChild", "bin", RepoFiles.Configuration(), "net10.0-windows", "PCMieru.TxChild.exe");
        Assert.True(File.Exists(exe), "the child exe was not built (the Update.Tests project references tests/PcMieru.TxChild): " + exe);
        return exe;
    }

    private sealed record ChildRun(bool Exited, int ExitCode, string Output);

    /// <summary>Starts the child, waits until it says it stopped at the point (the marker file), and kills it. If it ends before that, the test fails with what it said.</summary>
    private static ChildRun RunAndKill(string marker, params string[] args)
    {
        var info = new ProcessStartInfo(ChildExe()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var clock = Stopwatch.StartNew();
        while (!File.Exists(marker))
        {
            if (process.HasExited)
            {
                Assert.True(process.WaitForExit(15_000), "the ended child did not report its end");
                return new ChildRun(true, process.ExitCode, BoundedProcess.Drained(stdout, stderr));
            }

            if (clock.Elapsed > WaitForMarker)
            {
                BoundedProcess.Kill(process);
                Assert.Fail("the child never reached the stop point within " + WaitForMarker);
            }

            Thread.Sleep(10);
        }

        Assert.False(process.HasExited, "the child must still be alive at its stop point");
        BoundedProcess.Kill(process);
        Assert.True(process.HasExited, "the killed child did not end");
        return new ChildRun(false, process.ExitCode, string.Empty);
    }

    private static string Name(string step) => step;

    private static void KillCase(Rig rig, string[] childArgs, string marker, string context, bool finalWithLauncher)
    {
        var run = RunAndKill(marker, childArgs);
        Assert.False(run.Exited, $"{context}: the child ended before its stop point (exit {run.ExitCode}): {run.Output}");

        var observed = Observe(rig, $"after the kill ({context})");
        var launcher = finalWithLauncher ? FakeLauncher.Ok() : null;
        var result = rig.Recover(launcher);
        Assert.True(result.Outcome != TxOutcome.RecoveryFailed, $"{context}: recovery failed after the kill: {result.Reason} {result.Detail}");
        var generation = rig.AssertEnded($"{context}: after the kill and a recovery");
        Assert.Equal(Expect(observed, finalWithLauncher), generation);
        Assert.Equal(TxOutcome.NothingToDo, rig.Recover().Outcome);
        Assert.True(File.Exists(marker));
    }

    public static TheoryData<string, string, int, string, bool> SwapKills => new()
    {
        // step, edge, nth, the launcher the child has ("ok" or "fail"), whether the last recovery has a launcher
        { "prepare.runner", "After", 1, "ok", false },
        { "journal.prepared", "Mid", 1, "ok", false },
        { "journal.prepared", "After", 1, "ok", true },
        { "swap.add", "Before", 1, "ok", false },
        { "swap.add", "After", 1, "ok", true },
        { "swap.overwrite", "After", 2, "ok", false },
        { "swap.displace", "Before", 1, "ok", true },
        { "swap.installed", "After", 1, "ok", false },
        { "swap.installed", "After", 1, "ok", true },
        { "verify.launch", "Before", 1, "ok", true },
        { "verify.launch", "Before", 1, "ok", false },
        { "journal.commit-intent", "After", 1, "ok", false },
        { "commit.journal-delete", "Before", 1, "ok", true },
        { "rollback.restore-place", "After", 1, "fail", true },
        { "rollback.remove-new", "Before", 1, "fail", false },
        { "rollback.installed", "After", 1, "fail", true },
    };

    [Theory]
    [MemberData(nameof(SwapKills))]
    public void A_swap_killed_at_a_point_is_recovered_to_one_whole_generation(string step, string edge, int nth, string childLauncher, bool finalWithLauncher)
    {
        using var rig = Rig.Create("kill-" + Name(step));
        var marker = Path.Combine(rig.Root, "stopped.txt");
        KillCase(rig, ["swap", rig.Install, rig.Staged, step, edge, nth.ToString(System.Globalization.CultureInfo.InvariantCulture), marker, childLauncher], marker, $"swap killed at {step}/{edge}#{nth}", finalWithLauncher);
    }

    public static TheoryData<string, string, int, string, string, int, bool> RecoveryKills => new()
    {
        // The state is made in-process (a swap cut at <first>), then the child's RECOVERY is killed at <step>.
        { "swap.overwrite", "After", 3, "rollback.restore-place", "After", 1, false },
        { "swap.installed", "After", 1, "rollback.installed", "Before", 1, true }, // installed.json is the new record by then, so the rollback has to put the old one back
        { "swap.installed", "After", 1, "rollback.remove-new", "After", 1, false },
        { "swap.installed", "After", 1, "journal.recovery-attempt", "After", 1, true },
        { "verify.launch", "Before", 1, "rollback.journal-delete", "Before", 1, false },
        { "journal.commit-intent", "After", 1, "commit.journal-delete", "Before", 1, false },
    };

    [Theory]
    [MemberData(nameof(RecoveryKills))]
    public void A_recovery_killed_at_a_point_is_recovered_again(string firstStep, string firstEdge, int firstNth, string step, string edge, int nth, bool finalWithLauncher)
    {
        using var rig = Rig.Create("killrec-" + Name(step));
        var cutter = new Cutter(new CutPoint(TxSteps.All.Single(s => s.Name == firstStep), Enum.Parse<TxEdge>(firstEdge), firstNth));
        Assert.Throws<PowerCutException>(() => rig.Swap(Rig.Seams(cutter, FakeLauncher.Ok())));
        Assert.True(cutter.Fired);

        var marker = Path.Combine(rig.Root, "stopped.txt");
        KillCase(rig, ["recover", rig.Install, step, edge, nth.ToString(System.Globalization.CultureInfo.InvariantCulture), marker, "none"], marker, $"swap cut at {firstStep}/{firstEdge}#{firstNth}, recovery killed at {step}/{edge}#{nth}", finalWithLauncher);
    }

    [Fact]
    public void The_child_itself_runs_a_swap_to_the_end_and_a_failing_launcher_rolls_it_back()
    {
        foreach (var (launcher, generation, outcome) in new[] { ("ok", Generation.New, "Committed"), ("fail", Generation.Old, "RolledBack") })
        {
            using var rig = Rig.Create("kill-end-" + launcher);
            var info = new ProcessStartInfo(ChildExe()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "swap", rig.Install, rig.Staged, "-", "Before", "1", Path.Combine(rig.Root, "never.txt"), launcher })
            {
                info.ArgumentList.Add(arg);
            }

            var run = BoundedProcess.Run(info, TimeSpan.FromSeconds(120));
            var output = run.Output;
            Assert.True(run.ExitCode == 0, "the child failed: " + output);
            Assert.StartsWith("result " + outcome, output.Trim(), StringComparison.Ordinal);
            Assert.Equal(generation, rig.AssertEnded("the child's own run"));
            Assert.False(File.Exists(Path.Combine(rig.Root, "never.txt")));
        }
    }

    [Fact]
    public void A_killed_swapper_leaves_the_gate_to_the_next_one()
    {
        // The exclusive handle belongs to the process: killed at the very moment it holds the gate (journal written, files half swapped), the next entry still gets in at once.
        using var rig = Rig.Create("kill-gate");
        var marker = Path.Combine(rig.Root, "stopped.txt");
        var run = RunAndKill(marker, "swap", rig.Install, rig.Staged, "swap.overwrite", "After", "1", marker, "ok");
        Assert.False(run.Exited, run.Output);
        using var gate = rig.Enter();
        Assert.True(gate.IsOpen, "the gate must be free after the holder was killed: " + gate.Reason);
    }
}
