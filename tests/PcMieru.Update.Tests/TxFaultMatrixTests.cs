using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using PcMieru.Update.Transaction;
using static PcMieru.Update.Tests.Observation;
using static PcMieru.Update.Tests.TxSwapTests;

namespace PcMieru.Update.Tests;

// The fault matrix (DESIGN-B 5, 13 and the brief's item 8). Every step of the transaction is a NAMED point (TxSteps) that registers itself when the code reaches it; the points are not listed by hand. A scenario is
// run once with a recorder to learn which points it reaches, and then once per point with the power cut there. A fresh recovery must always end with the managed files all-old or all-new, hash-verified, and the
// user's files byte-identical.

/// <summary>One thing the code does that can be stopped: how to make the starting state, how to run it under an injector, and (when the state has a file only the user can fix) the repair that comes before the last recovery.</summary>
internal sealed record Scenario(string Name, Func<string, Rig> Make, Func<Rig, IFaultInjector, TxResult> Run, Action<Rig>? Repair = null)
{
    public override string ToString() => Name;
}

internal static class Scenarios
{
    private static readonly Dictionary<string, Scenario> ByName = new[]
    {
        // A first swap from a manually unzipped folder: the first record, stale folders of an older transaction, the real recovery.exe placement, the new version starts at once.
        new Scenario("swap-ok", label => WithStale(Rig.Create(label)), (rig, fault) => rig.Swap(Rig.Seams(fault, FakeLauncher.Ok()))),

        // A later swap (installed.json exists) whose new version never starts: every step of the rollback.
        new Scenario("swap-rollback", label => Rig.Create(label, recorded: true), (rig, fault) => rig.Swap(Rig.Seams(fault, FakeLauncher.AlwaysFail()))),

        // A recovery of a swap that was stopped with every file and the record already new.
        new Scenario("recover-swapping", label => Cut(Rig.Create(label), TxSteps.SwapInstalled, TxEdge.After), (rig, fault) => rig.Recover(automatic: true, fault: fault)),

        // A recovery that verifies the new set again and commits it.
        new Scenario("recover-verify", label => Cut(Rig.Create(label), TxSteps.Launch, TxEdge.Before), (rig, fault) => rig.Recover(FakeLauncher.Ok(), automatic: true, fault: fault)),

        // A recovery that cannot be proven (a file that is neither hash): the sentinel; the owner fixes the file, then the last recovery finishes.
        new Scenario("recover-failed", label => Garbage(Cut(Rig.Create(label), TxSteps.SwapOverwrite, TxEdge.After)), (rig, fault) => rig.Recover(automatic: true, fault: fault), RepairGarbage),

        // A recovery that rolls back with a sentinel from an earlier failure in place.
        new Scenario("recover-sentinel", label => WithSentinel(Cut(Rig.Create(label), TxSteps.SwapInstalled, TxEdge.After)), (rig, fault) => rig.Recover(fault: fault)),

        // A recovery that finishes a commit, with a sentinel in place.
        new Scenario("recover-commit", label => WithSentinel(Cut(Rig.Create(label), TxSteps.JournalCommitIntent, TxEdge.After)), (rig, fault) => rig.Recover(fault: fault)),

        // A recovery with no journal at all: a journal temp file, a sentinel, stale folders and a note are tidied.
        new Scenario("recover-tidy", label => WithTidyLeftovers(Rig.Create(label, recorded: true)), (rig, fault) => rig.Recover(fault: fault)),
    }.ToDictionary(s => s.Name, StringComparer.Ordinal);

    public static IEnumerable<string> Names => ByName.Keys;

    public static IEnumerable<object[]> NameData => Names.Select(n => new object[] { n });

    public static Scenario Get(string name) => ByName[name];

    // ------------------------------------------------------------------------------------------------------------------------ states

    private static Rig WithStale(Rig rig)
    {
        var work = Path.Combine(rig.Paths.Update, "work", "deadbeef0000", "next");
        Directory.CreateDirectory(work);
        File.WriteAllBytes(Path.Combine(work, "x"), [1, 2, 3]);
        var prev = Path.Combine(rig.Paths.Update, "prev-0.4.0", "cafe00000000", "files");
        Directory.CreateDirectory(prev);
        File.WriteAllBytes(Path.Combine(prev, "y"), [4, 5, 6]);
        return rig;
    }

    /// <summary>Runs the swap and cuts the power at the first time it reaches <paramref name="step"/> at <paramref name="edge"/>.</summary>
    private static Rig Cut(Rig rig, TxStep step, TxEdge edge)
    {
        var cutter = new Cutter(new CutPoint(step, edge, 1));
        try
        {
            rig.Swap(Rig.Seams(cutter, FakeLauncher.Ok()));
        }
        catch (PowerCutException)
        {
            // the point of it
        }

        if (!cutter.Fired)
        {
            throw new InvalidOperationException("the scenario's first cut was never reached");
        }

        return rig;
    }

    private static Rig WithSentinel(Rig rig)
    {
        File.WriteAllText(rig.Paths.Sentinel, "recovery failed: an earlier failure\n");
        return rig;
    }

    private static Rig WithTidyLeftovers(Rig rig)
    {
        WithStale(rig);
        File.WriteAllText(rig.Paths.JournalTemp, "{ half");
        File.WriteAllText(rig.Paths.Sentinel, "recovery failed: an earlier failure\n");
        File.WriteAllBytes(rig.Paths.Note, RecoverNote.Bytes("0.5.0", "0.5.1"));
        return rig;
    }

    private static Rig Garbage(Rig rig)
    {
        var journal = ReadJournal(rig);
        var op = journal.Ops.First(o => o.Intent == TxOpIntent.Overwrite && o.State == TxOpState.Pending);
        File.WriteAllBytes(TxPaths.Under(rig.Install, op.Path), Encoding.UTF8.GetBytes("neither the old nor the new file"));
        return rig;
    }

    private static void RepairGarbage(Rig rig)
    {
        if (!File.Exists(rig.Paths.Journal))
        {
            return; // the recovery got as far as finishing: nothing left to repair
        }

        var journal = ReadJournal(rig);
        foreach (var op in journal.Ops.Where(o => o.Intent == TxOpIntent.Overwrite))
        {
            var target = TxPaths.Under(rig.Install, op.Path);
            var bytes = File.ReadAllBytes(target);
            var sha = Rig.Sha(bytes);
            if (sha != op.OldSha256 && sha != op.NewSha256)
            {
                File.WriteAllBytes(target, rig.Old.AllFiles().Single(f => f.Key == op.Path).Value);
            }
        }
    }
}

/// <summary>What a scenario reaches, found once per test run by running it with a recorder.</summary>
internal sealed record Recording(IReadOnlyList<CutPoint> Points, IReadOnlySet<TxPhase> JournalPhases);

internal static class Recordings
{
    private static readonly ConcurrentDictionary<string, Lazy<Recording>> Cache = new(StringComparer.Ordinal);

    public static Recording Of(string scenario) => Cache.GetOrAdd(scenario, name => new Lazy<Recording>(() => Record(Scenarios.Get(name)))).Value;

    private static Recording Record(Scenario scenario)
    {
        using var rig = scenario.Make("rec-" + scenario.Name);
        var phases = new HashSet<TxPhase>();
        var recorder = new Recorder
        {
            Hook = (step, edge, _) =>
            {
                if (step.Name.StartsWith("journal.", StringComparison.Ordinal) && edge == TxEdge.After && File.Exists(rig.Paths.Journal))
                {
                    phases.Add(ReadJournal(rig).Phase); // the phase of the journal as it is on the disk after the step
                }
            },
        };
        scenario.Run(rig, recorder);
        return new Recording(recorder.Points, phases);
    }
}

/// <summary>The state a stopped transaction leaves, read before anything recovers it.</summary>
internal sealed record Observed(TxPhase? Phase, int Attempts, Generation? WholeInstall);

internal static class Observation
{
    public static Observed Observe(Rig rig, string context)
    {
        if (!File.Exists(rig.Paths.Journal))
        {
            // No journal: nothing is open, and then the managed files must be a whole generation already (a journal is what covers every half state).
            return new Observed(null, 0, rig.AssertWholeGeneration(context + " (no journal, so the install must be whole)"));
        }

        var journal = ReadJournal(rig);
        return new Observed(journal.Phase, journal.LaunchAttempts, null);
    }

    /// <summary>What the decision table of DESIGN-B 5/13 says the last recovery must end with, for the state that was observed.</summary>
    public static Generation Expect(Observed state, bool launcher) => state.Phase switch
    {
        null => state.WholeInstall!.Value,
        TxPhase.CommitIntent => Generation.New,
        TxPhase.VerifyPending => launcher && state.Attempts < TxLimits.MaxLaunchAttempts ? Generation.New : Generation.Old,
        _ => Generation.Old, // Prepared, Swapping and Rollback all go back
    };
}

public sealed class TxFaultMatrixTests
{
    private static string Label(Scenario scenario, CutPoint point) => $"{scenario.Name}-{point.Step.Name}-{point.Edge}{point.Nth}".Replace('.', '-');

    /// <summary>Cuts the power at <paramref name="point"/> and checks that a fresh recovery (with or without a launcher) ends whole and as the table says.</summary>
    private static void CutAndRecover(Scenario scenario, CutPoint point, bool withLauncher)
    {
        using var rig = scenario.Make(Label(scenario, point) + (withLauncher ? "-L" : "-N"));
        var cutter = new Cutter(point);
        Assert.Throws<PowerCutException>(() => scenario.Run(rig, cutter));
        Assert.True(cutter.Fired, "the power was never cut at " + point);

        var observed = Observe(rig, "after the cut at " + point);
        scenario.Repair?.Invoke(rig);
        var launcher = withLauncher ? FakeLauncher.Ok() : null;
        var result = rig.Recover(launcher);

        Assert.True(result.Outcome != TxOutcome.RecoveryFailed, $"the last recovery failed after the cut at {point}: {result.Reason} {result.Detail}");
        var generation = rig.AssertEnded($"after the cut at {point} and a recovery {(withLauncher ? "with" : "without")} a launcher");
        Assert.Equal(Expect(observed, withLauncher), generation);
        Assert.Equal(TxOutcome.NothingToDo, rig.Recover().Outcome); // and a second recovery finds nothing to do
    }

    public static TheoryData<string> ScenarioNames => new(Scenarios.Names);

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void A_power_cut_at_any_point_is_recovered_to_one_whole_generation(string name)
    {
        var scenario = Scenarios.Get(name);
        var points = Recordings.Of(name).Points;
        Assert.True(points.Count >= 5, $"{name}: only {points.Count} points were recorded");

        Parallelism.ForEach(points, point =>
        {
            CutAndRecover(scenario, point, withLauncher: false);
            CutAndRecover(scenario, point, withLauncher: true);
        }, $"{name}: cut points ({points.Count})");
    }

    // ---------------------------------------------------------------------------------------------------------------------------- a cut inside a recovery

    public static TheoryData<string, string, string, int> FirstCuts => new()
    {
        // scenario, step, edge, nth: a few representative states, each then cut AGAIN at every point of its own recovery.
        { "swap-ok", "swap.overwrite", "After", 2 },
        { "swap-ok", "swap.installed", "After", 1 },
        { "swap-ok", "verify.launch", "Before", 1 },
        { "swap-ok", "journal.commit-intent", "After", 1 },
        { "swap-rollback", "rollback.restore-place", "After", 1 },
        { "swap-rollback", "rollback.remove-new", "Before", 1 },
    };

    [Theory]
    [MemberData(nameof(FirstCuts))]
    public void A_second_power_cut_during_the_recovery_is_recovered_too(string name, string stepName, string edgeName, int nth)
    {
        var scenario = Scenarios.Get(name);
        var first = new CutPoint(TxSteps.All.Single(s => s.Name == stepName), Enum.Parse<TxEdge>(edgeName), nth);
        Assert.Contains(first, Recordings.Of(name).Points);

        // The points of the recovery that follows this first cut.
        var secondPoints = new List<CutPoint>();
        using (var probe = Boot(scenario, first, "probe"))
        {
            var recorder = new Recorder();
            probe.Recover(fault: recorder);
            secondPoints.AddRange(recorder.Points);
        }

        Assert.NotEmpty(secondPoints);
        Parallelism.ForEach(secondPoints, second =>
        {
            foreach (var withLauncher in new[] { false, true })
            {
                using var rig = Boot(scenario, first, "nested");
                var cutter = new Cutter(second);
                Assert.Throws<PowerCutException>(() => rig.Recover(fault: cutter));
                Assert.True(cutter.Fired, "the second cut was never reached: " + second);

                var observed = Observe(rig, $"after the cut at {first} and again at {second}");
                var result = rig.Recover(withLauncher ? FakeLauncher.Ok() : null);

                Assert.True(result.Outcome != TxOutcome.RecoveryFailed, $"recovery failed after {first} and {second}: {result.Reason} {result.Detail}");
                var generation = rig.AssertEnded($"after the cuts at {first} and {second}");
                Assert.Equal(Expect(observed, withLauncher), generation);
            }
        }, $"{name} cut at {first} then again ({secondPoints.Count} points)");
    }

    /// <summary>A rig of the scenario that was cut at <paramref name="first"/> (the scenario's own run, interrupted).</summary>
    private static Rig Boot(Scenario scenario, CutPoint first, string label)
    {
        var rig = scenario.Make(label + "-" + Label(scenario, first));
        var cutter = new Cutter(first);
        Assert.Throws<PowerCutException>(() => scenario.Run(rig, cutter));
        return rig;
    }

    // ---------------------------------------------------------------------------------------------------------------------------- an ordinary I/O error at any point

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void An_ordinary_io_error_at_any_point_ends_in_a_whole_generation_and_never_escapes(string name)
    {
        var scenario = Scenarios.Get(name);
        var points = Recordings.Of(name).Points;

        Parallelism.ForEach(points, point =>
        {
            using var rig = scenario.Make("io-" + Label(scenario, point));
            var fail = new FailOnce(point);
            TxResult result;
            try
            {
                result = scenario.Run(rig, fail);
            }
            catch (Exception ex)
            {
                Assert.Fail($"an I/O error at {point} escaped as {ex.GetType().Name}: {ex.Message}");
                return;
            }

            Assert.True(fail.Fired, "the I/O error was never injected at " + point);
            _ = Observe(rig, $"after the I/O error at {point} ({result.Outcome}/{result.Reason})"); // with no journal left the install must already be whole
            switch (result.Outcome)
            {
                case TxOutcome.Committed:
                    Assert.Equal(Generation.New, rig.AssertWholeGeneration($"committed despite an I/O error at {point}"));
                    break;
                case TxOutcome.RolledBack or TxOutcome.Aborted:
                    Assert.Equal(Generation.Old, rig.AssertWholeGeneration($"{result.Outcome} after an I/O error at {point}"));
                    break;
                case TxOutcome.RecoveryFailed:
                    Assert.True(File.Exists(rig.Paths.Sentinel) || File.Exists(rig.Paths.Journal), $"a failed recovery at {point} must leave the journal or the sentinel");
                    break;
            }

            // Whatever the answer was: the next recovery (the error was a one-time thing) leaves a whole, settled install.
            scenario.Repair?.Invoke(rig);
            var last = rig.Recover();
            Assert.True(last.Outcome != TxOutcome.RecoveryFailed, $"recovery after an I/O error at {point}: {last.Reason} {last.Detail}");
            var generation = rig.AssertEnded($"after the I/O error at {point} and a recovery");
            if (result.Outcome == TxOutcome.Committed)
            {
                Assert.Equal(Generation.New, generation);
            }
        }, $"{name}: I/O errors ({points.Count} points)");
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the list is complete

    [Fact]
    public void Every_step_the_code_defines_is_reached_with_every_edge_its_kind_has()
    {
        var reached = new Dictionary<TxStep, HashSet<TxEdge>>();
        foreach (var name in Scenarios.Names)
        {
            foreach (var point in Recordings.Of(name).Points)
            {
                if (!reached.TryGetValue(point.Step, out var edges))
                {
                    reached[point.Step] = edges = [];
                }

                edges.Add(point.Edge);
            }
        }

        var problems = new List<string>();
        foreach (var step in TxSteps.All)
        {
            var needs = step.Kind == TxStepKind.Write ? new[] { TxEdge.Before, TxEdge.Mid, TxEdge.After } : new[] { TxEdge.Before, TxEdge.After };
            if (!reached.TryGetValue(step, out var edges))
            {
                problems.Add($"{step.Name}: never reached by any scenario");
                continue;
            }

            problems.AddRange(needs.Where(e => !edges.Contains(e)).Select(e => $"{step.Name}: edge {e} never reached"));
        }

        Assert.True(problems.Count == 0, "Steps the scenarios do not cover (add a scenario, or the step is dead):" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        Assert.Equal(TxSteps.All.Count, TxSteps.All.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(reached.Keys.Except(TxSteps.All)); // and nothing was reached that is not registered
    }

    [Fact]
    public void The_steps_and_the_journals_the_scenarios_write_cover_every_phase()
    {
        var all = Enum.GetValues<TxPhase>().ToHashSet();
        Assert.Equal(all.OrderBy(p => p), TxSteps.All.Select(s => s.Phase).Distinct().OrderBy(p => p));

        var written = new HashSet<TxPhase>();
        foreach (var name in Scenarios.Names)
        {
            written.UnionWith(Recordings.Of(name).JournalPhases);
        }

        Assert.Equal(all.OrderBy(p => p), written.OrderBy(p => p)); // a journal of every phase was really written to the disk and parsed back
    }

    [Fact]
    public void Every_registered_step_is_used_in_the_code_and_every_file_change_goes_through_the_io_funnel()
    {
        var folder = Path.Combine(RepoFiles.Root(), "src", "PcMieru.Update", "Transaction");
        var files = Directory.GetFiles(folder, "*.cs").ToDictionary(f => Path.GetFileName(f)!, f => StripComments(File.ReadAllText(f)), StringComparer.OrdinalIgnoreCase);

        // 1. Each step field of TxSteps is named somewhere outside TxFault.cs.
        var unused = typeof(TxSteps).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(TxStep))
            .Select(f => f.Name)
            .Where(field => !files.Where(kv => kv.Key != "TxFault.cs").Any(kv => Regex.IsMatch(kv.Value, @"\bTxSteps\." + field + @"\b")))
            .ToList();
        Assert.True(unused.Count == 0, "Steps defined but never used: " + string.Join(", ", unused));

        // 2. Nothing outside TxIo.cs and TxNative.cs changes the file system: a new mutation must be added there, as a named step. (A call on the funnel object, "_io.X", is the allowed way and is not matched; a direct "TxNative.X" call from the engine is.)
        var mutation = new Regex(
            @"(?<!_io\.)\b(File\.(Delete|Move|Copy|Create\w*|WriteAll\w*|AppendAll\w*|Replace|SetAttributes|SetCreationTime\w*|SetLastWrite\w*|SetLastAccess\w*|Encrypt|Decrypt|OpenWrite|CreateSymbolicLink)"
            + @"|Directory\.(Delete|Move|CreateDirectory|CreateSymbolicLink|SetCreationTime\w*|SetLastWrite\w*)"
            + @"|FileMode\.(Create|CreateNew|OpenOrCreate|Append|Truncate)|FileAccess\.(Write|ReadWrite)"
            + @"|MoveFileEx\w*|DeleteFile\w*|CreateFile\w*|ReplaceFile\w*|SetFileAttributes\w*|RemoveDirectory\w*|CreateDirectory\w*)\b|\.(MoveTo|CopyTo)\(|\.Delete\(\)");
        var offenders = files.Where(kv => kv.Key is not ("TxIo.cs" or "TxNative.cs"))
            .SelectMany(kv => mutation.Matches(kv.Value).Select(m => $"{kv.Key}: {m.Value}"))
            .ToList();
        Assert.True(offenders.Count == 0, "File changes outside the TxIo funnel (no fault point):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static string StripComments(string code) =>
        string.Join('\n', code.Split('\n').Select(line =>
        {
            var at = line.IndexOf("//", StringComparison.Ordinal);
            return at < 0 ? line : line[..at];
        }));
}
