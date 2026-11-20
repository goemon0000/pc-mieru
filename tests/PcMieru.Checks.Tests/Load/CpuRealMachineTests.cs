using System.Diagnostics;
using System.Globalization;
using PcMieru.Checks.Load;
using PcMieru.Checks.Tests.Run;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Collectors;
using Xunit.Abstractions;

namespace PcMieru.Checks.Tests.Load;

/// <summary>
/// The CPU check on this machine: the real session (one below-normal thread on every logical processor, the real chunk, the real pin, the
/// check's own PDH query), no elevation, a temp root. It loads the CPU: run on purpose and never while a game is running
/// (<c>--filter Category=RealMachine</c>). What it prints is masked: counts, numbers, verdicts and the reason a reading is missing, never a
/// processor model, a name or a serial.
/// </summary>
public sealed class CpuRealMachineTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "RealMachine")]
    public async Task The_real_cpu_check_of_thirty_seconds_passes_the_strict_validator_and_reads_back()
    {
        using var temp = new TempRoot();
        var services = CheckServices.CreateLive(temp.Path, legacyDirectory: null, app: Samples.App);
        var runner = new CheckRunner(services);
        var log = new ProgressLog();

        var watch = Stopwatch.StartNew();
        var run = await runner.RunAsync(new CheckRequest(CheckKind.Cpu, TimeSpan.FromSeconds(30)), log, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(3));
        watch.Stop();

        Report(run, watch.Elapsed, log);

        Assert.Null(run.FailureCode);
        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.True(run.IsSaved, "the run was not saved: " + run.Save.FailureCode);
        var problems = CheckResultValidator.Validate(run.Result);
        Assert.True(problems.Count == 0, string.Join(" | ", problems));
        Assert.Equal(CheckNotes.CpuTemperatureNotMeasured, Assert.Single(run.Result.Notes));
        Assert.DoesNotContain(run.Result.Items, static i => i.Id.Contains("temperature", StringComparison.Ordinal));

        // The whole 30 seconds were loaded, every logical processor got its row, and no chunk was computed wrongly.
        Assert.InRange(Rows.Number(Row(run, "cpu.duration")), 29.5, 40);
        Assert.Equal(Environment.ProcessorCount, run.Result.Items.Count(static i => i.Id == "cpu.logical_throughput"));
        Assert.True(Row(run, "cpu.chunks").Value!.AsInt > 0);
        Assert.Equal(CheckValue.Int(0), Row(run, "cpu.compute_errors").Value);

        // Progress: a reading about every second with the pace, and no more than one a second.
        var readings = log.Items.Where(static p => p.RecentRate is not null).ToList();
        Assert.NotEmpty(readings);
        Assert.InRange(readings.Count, 1, 31);

        // The file read back by the read-only history is the file that was written.
        var back = await CheckHistory.OpenReadOnly(Path.Combine(temp.Path, "checks")).GetAsync(run.Result.RunId, CancellationToken.None);
        Assert.Equal(CheckHistoryGetStatus.Found, back.Status);
        Assert.Equal(Kit.Bytes(run.Result), Kit.Bytes(back.Result!));

        // Nothing but the one folder of the check was made under the temp root.
        Assert.Equal(["checks"], Directory.GetDirectories(temp.Path).Select(static d => Path.GetFileName(d)));
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    [Trait("Category", "RealMachine")]
    public async Task The_real_cpu_check_stopped_after_five_seconds_is_a_stopped_result_and_no_load_thread_is_left()
    {
        using var rig = new RunRig(cpuSessionFactory: () => session = new RecordingRealSession());
        using var cts = new CancellationTokenSource();
        var log = new ProgressLog();

        var watch = Stopwatch.StartNew();
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var run = await rig.RunAsync(new CheckRequest(CheckKind.Cpu, TimeSpan.FromSeconds(30)), log, cts.Token).WaitAsync(TimeSpan.FromMinutes(2));
        watch.Stop();

        Report(run, watch.Elapsed, log);
        var handles = session!.Handles;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  load threads started={handles.Count} alive_after_the_run={handles.Count(static h => h.IsAlive)} logical_processors={session.Cpus.Count}"));

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal(CheckNotes.CpuTemperatureNotMeasured, Assert.Single(run.Result.Notes));
        Assert.InRange(watch.Elapsed.TotalSeconds, 4.5, 15); // it stopped at once, not at the end of the thirty seconds

        // No load thread is alive once RunAsync has returned: the load was joined before the result was made.
        Assert.Equal(session.Cpus.Count, handles.Count);
        Assert.All(handles, static h => Assert.False(h.IsAlive));
        Assert.True(session.Disposed);

        // What was found in the five seconds is kept, and a partial row is never ok.
        Assert.Equal(CheckReason.Partial, Row(run, "cpu.chunks").Reason);
        Assert.True(Row(run, "cpu.chunks").Value!.AsInt > 0);
        Assert.DoesNotContain(run.Result.Items, static i => i.Verdict == ItemVerdict.Ok);
        Assert.Equal(CheckValue.Int(0), Row(run, "cpu.compute_errors").Value);
        Assert.Equal(CheckOutcome.Stopped, rig.History.Saved.Single().Outcome);
    }

    private RecordingRealSession? session;

    private static CheckItem Row(CheckRun run, string id) => run.Result.Items.First(i => i.Id == id);

    private static string Shown(CheckItem? item) =>
        item is null
            ? "no-row"
            : item.Value is null
                ? "missing:" + (item.Reason?.ToString() ?? "no-reason") + "/" + item.Verdict
                : item.Value + "/" + item.Verdict + (item.Reason is { } reason ? "/" + reason : string.Empty);

    private void Report(CheckRun run, TimeSpan took, ProgressLog log)
    {
        var result = run.Result;
        var counts = result.Counts;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"outcome={result.Outcome} verdict={result.Verdict} ok={counts.Ok} caution={counts.Caution} check={counts.Check} unavailable={counts.Unavailable} info={counts.Info} items={result.Items.Count} saved={run.IsSaved} wall={took.TotalSeconds:0.0}s"));
        output.WriteLine("  notes: " + (result.Notes is { Count: > 0 } notes ? string.Join(",", notes) : "(none)"));
        foreach (var id in new[]
                 {
                     "cpu.duration", "cpu.chunks", "cpu.throughput", "cpu.compute_errors", "cpu.steadiness", "cpu.variation",
                     "cpu.effective_clock", "cpu.utilization", "cpu.package_power",
                 })
        {
            var item = result.Items.FirstOrDefault(i => i.Id == id);
            var tail = item?.Evidence?.Code is { } code ? " code=" + code : string.Empty;
            output.WriteLine($"  {id}={Shown(item)}{tail}");
        }

        var logical = result.Items.Where(static i => i.Id == "cpu.logical_throughput").ToList();
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  cpu.logical_throughput rows={logical.Count} with_value={logical.Count(static i => i.Value is not null)}"));

        var peers = result.Items.Where(static i => i.Id == "cpu.peer_ratio").ToList();
        var byVerdict = peers.GroupBy(static i => i.Verdict).OrderBy(static g => g.Key).Select(static g => $"{g.Key}={g.Count()}");
        var codes = peers.Where(static p => p.Evidence?.Code is not null).Select(static p => p.Evidence!.Code!.Value.ToString()).Distinct().Order();
        var classes = peers.Where(static p => p.Evidence?.EfficiencyClass is not null).Select(static p => p.Evidence!.EfficiencyClass!.Value).Distinct().Order();
        output.WriteLine($"  cpu.peer_ratio rows={peers.Count} [{string.Join(" ", byVerdict)}] codes=[{string.Join(",", codes)}] efficiency_classes=[{string.Join(",", classes)}]");

        var readings = log.Items.Where(static p => p.RecentRate is not null).ToList();
        var stages = log.Items.Where(static p => p.RecentRate is null).Select(static p => p.Stage.ToString());
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  progress: readings={readings.Count} first_rate={readings.FirstOrDefault()?.RecentRate:0}chunks/s stages=[{string.Join(",", stages)}]"));
    }

    /// <summary>The real session with its threads recorded: after the run, the test asks every one of them whether it is still alive.</summary>
    private sealed class RecordingRealSession : ICpuTestSession
    {
        private readonly RealCpuTestSession _inner = new();

        public RecordingRealSession() => Threads = new Recording(_inner.Threads, Handles);

        public List<ILoadThread> Handles { get; } = [];

        public bool Disposed { get; private set; }

        public IReadOnlyList<LogicalCpu> Cpus => _inner.Cpus;

        public IChunkWork Work => _inner.Work;

        public ICpuPin Pin => _inner.Pin;

        public ILoadThreadStarter Threads { get; }

        public ILoadClock Clock => _inner.Clock;

        public CpuPdhSample SamplePdh() => _inner.SamplePdh();

        public void Dispose()
        {
            Disposed = true;
            _inner.Dispose();
        }

        private sealed class Recording(ILoadThreadStarter inner, List<ILoadThread> handles) : ILoadThreadStarter
        {
            public ILoadThread Start(string name, Action onStart, Func<bool> step, Action<Exception> onFault)
            {
                var thread = inner.Start(name, onStart, step, onFault);
                lock (handles)
                {
                    handles.Add(thread);
                }

                return thread;
            }
        }
    }
}
