using System.Diagnostics;
using System.Globalization;
using PcMieru.Checks.Load;
using PcMieru.Checks.Tests.Run;
using Xunit.Abstractions;

namespace PcMieru.Checks.Tests.Load;

/// <summary>
/// The memory check on this machine: the real session (64 MiB blocks from the native allocator with every page touched, below-normal work
/// threads, the real status of the physical memory), no elevation, a temp root. It borrows a quarter of the free memory and writes and reads it
/// back for the whole time: run on purpose and never while a game is running (<c>--filter Category=RealMachine</c>). This is the only class of
/// tests that makes a real memory session (a guard test in <see cref="MemoryCheckProcedureTests"/> sees to it). What it prints is masked:
/// counts, numbers, verdicts and the reason a reading is missing, never a serial or a model name.
/// </summary>
public sealed class MemoryRealMachineTests(ITestOutputHelper output)
{
    private const long Mib = 1L << 20;

    [Fact]
    [Trait("Category", "RealMachine")]
    public async Task The_real_memory_check_of_thirty_seconds_passes_the_strict_validator_finds_nothing_and_reads_back()
    {
        using var temp = new TempRoot();
        var services = CheckServices.CreateLive(temp.Path, legacyDirectory: null, app: Samples.App);
        var runner = new CheckRunner(services);
        var log = new ProgressLog();
        var before = PrivateBytes();

        var watch = Stopwatch.StartNew();
        var run = await runner.RunAsync(new CheckRequest(CheckKind.Memory, TimeSpan.FromSeconds(30)), log, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(3));
        watch.Stop();

        var after = PrivateBytes();
        Report(run, watch.Elapsed, log, before, after);

        Assert.Null(run.FailureCode);
        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.True(run.IsSaved, "the run was not saved: " + run.Save.FailureCode);
        var problems = CheckResultValidator.Validate(run.Result);
        Assert.True(problems.Count == 0, string.Join(" | ", problems));
        Assert.Equal(CheckNotes.MemoryBorrowedRegionOnly, run.Result.Notes[0]);

        // Something was borrowed and verified, and not one place read back wrong: the one judged row is ok.
        Assert.True(Bytes(Row(run, "memory.borrowed")) > 0, "no memory was borrowed");
        Assert.True(Bytes(Row(run, "memory.verified")) > 0, "nothing was verified");
        Assert.True(Row(run, "memory.passes").Value!.AsInt > 0);
        Assert.Equal(CheckValue.Int(0), Row(run, "memory.mismatches").Value);
        Assert.Equal(ItemVerdict.Ok, Row(run, "memory.mismatches").Verdict);
        Assert.Null(Row(run, "memory.mismatches").Reason);
        Assert.InRange(Rows.Number(Row(run, "memory.duration")), 29.5, 40);
        Assert.True(Rows.Number(Row(run, "memory.write_rate")) > 0);
        Assert.True(Rows.Number(Row(run, "memory.verify_rate")) > 0);
        Assert.DoesNotContain(CheckNotes.MemoryPressure, run.Result.Notes);

        // Progress: a reading about every second with the pace in MiB/s, and no more than one a second.
        var readings = log.Items.Where(static p => p.RecentRate is not null).ToList();
        Assert.NotEmpty(readings);
        Assert.InRange(readings.Count, 1, 31);
        Assert.All(readings, static p => Assert.Equal("MiB/s", p.RateUnit));

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
    public async Task The_real_memory_check_stopped_after_five_seconds_is_a_stopped_result_and_the_memory_is_given_back()
    {
        var session = default(RecordingRealSession);
        using var rig = new RunRig(memorySessionFactory: () => session = new RecordingRealSession());
        using var cts = new CancellationTokenSource();
        var log = new ProgressLog();
        var before = PrivateBytes();

        var watch = Stopwatch.StartNew();
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var run = await rig.RunAsync(new CheckRequest(CheckKind.Memory, TimeSpan.FromSeconds(30)), log, cts.Token).WaitAsync(TimeSpan.FromMinutes(2));
        watch.Stop();

        var after = PrivateBytes();
        Report(run, watch.Elapsed, log, before, after);
        var handles = session!.Handles;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  work threads started={handles.Count} alive_after_the_run={handles.Count(static h => h.IsAlive)} blocks_allocated={session.Allocated} blocks_freed={session.Freed}"));

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal(CheckNotes.MemoryBorrowedRegionOnly, run.Result.Notes[0]);
        Assert.InRange(watch.Elapsed.TotalSeconds, 4.5, 15); // it stopped at once, not at the end of the thirty seconds

        // A stopped run is partial and never ok; what was found in the five seconds is kept.
        Assert.DoesNotContain(run.Result.Items, static i => i.Verdict == ItemVerdict.Ok);
        Assert.NotEqual(CheckVerdict.Ok, run.Result.Verdict);
        Assert.Equal(CheckOutcome.Stopped, rig.History.Saved.Single().Outcome);
        Assert.NotEqual(ItemVerdict.Check, Row(run, "memory.mismatches").Verdict); // nothing read back wrong (nothing at all, if the five seconds went into the allocation)

        // Every thread was joined and every block freed before the run came back.
        Assert.All(handles, static h => Assert.False(h.IsAlive));
        Assert.Equal(session.Allocated, session.Freed);
        Assert.True(session.Disposed);

        // The memory is back: the process holds no more than 128 MiB over what it held at the start.
        Assert.True(after - before <= 128 * Mib, $"private bytes grew by {(after - before) / Mib} MiB and were not given back");
    }

    [Fact]
    [Trait("Category", "RealMachine")]
    public void A_real_block_is_page_aligned_holds_what_is_written_and_is_freed_once()
    {
        using var session = new RealMemoryTestSession();
        var before = PrivateBytes();

        var block = session.Allocate();
        try
        {
            var words = block.Words(0, (int)(session.BlockBytes / sizeof(ulong)));
            Patterns.Fill(words, PatternKind.Random, 0xFEED, 0);
            var firstOfSecondHalf = block.Words(words.Length / 2, words.Length / 2);
            var found = Patterns.Verify(firstOfSecondHalf, PatternKind.Random, 0xFEED, words.Length / 2);
            Assert.Equal(0, found.Mismatches);
            Assert.Equal(0, Patterns.Verify(words, PatternKind.Random, 0xFEED, 0).Mismatches);
            Assert.True(Patterns.Verify(words, PatternKind.Random, 0xBEEF, 0, keep: 0).Mismatches > 0); // another seed reads back wrong
        }
        finally
        {
            block.Free();
        }

        block.Free(); // a second free does nothing
        Assert.True(PrivateBytes() - before <= 128 * Mib);
        Assert.NotNull(session.ReadStatus());
        Assert.True(session.ReadStatus()!.Value.TotalBytes > 0);
    }

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static CheckItem Row(CheckRun run, string id) => run.Result.Items.First(i => i.Id == id);

    private static long Bytes(CheckItem item) => item.Value is { } value ? long.Parse(value.AsText, CultureInfo.InvariantCulture) : 0;

    private static string Shown(CheckItem? item) =>
        item is null
            ? "no-row"
            : item.Value is null
                ? "missing:" + (item.Reason?.ToString() ?? "no-reason") + "/" + item.Verdict
                : item.Value + "/" + item.Verdict + (item.Reason is { } reason ? "/" + reason : string.Empty);

    private void Report(CheckRun run, TimeSpan took, ProgressLog log, long privateBefore, long privateAfter)
    {
        var result = run.Result;
        var counts = result.Counts;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"outcome={result.Outcome} verdict={result.Verdict} ok={counts.Ok} caution={counts.Caution} check={counts.Check} unavailable={counts.Unavailable} info={counts.Info} items={result.Items.Count} saved={run.IsSaved} wall={took.TotalSeconds:0.0}s"));
        output.WriteLine("  notes: " + (result.Notes is { Count: > 0 } notes ? string.Join(",", notes) : "(none)"));
        var borrowed = result.Items.FirstOrDefault(static i => i.Id == "memory.borrowed");
        var passes = result.Items.FirstOrDefault(static i => i.Id == "memory.passes");
        var write = result.Items.FirstOrDefault(static i => i.Id == "memory.write_rate");
        var verify = result.Items.FirstOrDefault(static i => i.Id == "memory.verify_rate");
        var mismatches = result.Items.FirstOrDefault(static i => i.Id == "memory.mismatches");
        var verified = result.Items.FirstOrDefault(static i => i.Id == "memory.verified");
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  borrowed_gib={(borrowed is null ? 0 : Bytes(borrowed) / (double)(1L << 30)):0.00} verified_gib={(verified is null ? 0 : Bytes(verified) / (double)(1L << 30)):0.00} passes={Shown(passes)} write_rate={Shown(write)}MiB/s verify_rate={Shown(verify)}MiB/s mismatches={Shown(mismatches)}"));
        var readings = log.Items.Where(static p => p.RecentRate is not null).ToList();
        var stages = log.Items.Where(static p => p.RecentRate is null).Select(static p => p.Stage.ToString());
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  progress: readings={readings.Count} first_rate={readings.FirstOrDefault()?.RecentRate:0}MiB/s stages=[{string.Join(",", stages)}]"));
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  private_bytes: before={privateBefore / Mib} MiB after={privateAfter / Mib} MiB"));
    }

    /// <summary>The real session with its threads and blocks recorded: after the run, the test asks every thread whether it is alive and every block whether it was freed.</summary>
    private sealed class RecordingRealSession : IMemoryTestSession
    {
        private readonly RealMemoryTestSession _inner = new();
        private int _allocated;
        private int _freed;

        public RecordingRealSession() => Threads = new RecordingStarter(_inner.Threads, Handles);

        public List<ILoadThread> Handles { get; } = [];

        public int Allocated => Volatile.Read(ref _allocated);

        public int Freed => Volatile.Read(ref _freed);

        public bool Disposed { get; private set; }

        public long BlockBytes => _inner.BlockBytes;

        public int LogicalProcessorCount => _inner.LogicalProcessorCount;

        public ILoadThreadStarter Threads { get; }

        public ILoadClock Clock => _inner.Clock;

        public MemoryStatus? ReadStatus() => _inner.ReadStatus();

        public IMemoryBlock Allocate()
        {
            var block = _inner.Allocate();
            Interlocked.Increment(ref _allocated);
            return new RecordingBlock(block, () => Interlocked.Increment(ref _freed));
        }

        public ulong NextSeed() => _inner.NextSeed();

        public void Dispose()
        {
            Disposed = true;
            _inner.Dispose();
        }

        private sealed class RecordingBlock(IMemoryBlock inner, Action onFreed) : IMemoryBlock
        {
            private int _done;

            public Span<ulong> Words(int firstWord, int count) => inner.Words(firstWord, count);

            public void Free()
            {
                inner.Free();
                if (Interlocked.Exchange(ref _done, 1) == 0)
                {
                    onFreed();
                }
            }
        }

        private sealed class RecordingStarter(ILoadThreadStarter inner, List<ILoadThread> handles) : ILoadThreadStarter
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
