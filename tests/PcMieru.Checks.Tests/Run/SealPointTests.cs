using PcMieru.Checks.Load;
using PcMieru.Checks.Storage;
using PcMieru.Checks.SystemInfo;
using PcMieru.Checks.Tests.Load;
using PcMieru.Checks.Tests.Storage;
using PcMieru.Checks.Tests.SystemInfo;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Collectors;

namespace PcMieru.Checks.Tests.Run;

/// <summary>
/// The seal point of a run (DESIGN-A 2): the moment the procedure returns its rows. A stop that comes after it does not change a completed
/// run; a stop that the procedure sees makes a stopped run, and a stop that the procedure did not see (it came after the rows were whole and the
/// procedure did not say "stopped") leaves the run completed. Tested on the four real procedures (storage, system, CPU, memory), each wrapped so that
/// the test cancels at an exact moment: before the real procedure starts, between its return and the wrapper's, or after the wrapper has
/// returned (on the first reading of the services' clock that follows, which is the runner making the result final).
/// </summary>
public sealed class SealPointTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public static TheoryData<CheckKind> Kinds => new() { CheckKind.Storage, CheckKind.System, CheckKind.Cpu, CheckKind.Memory };

    // The services' clock: it cancels the run on the first reading of the time after the wrapper has said that the procedure returned.
    private sealed class SealClock(TimeProvider inner) : TimeProvider
    {
        private int _armed;

        public Action? OnFirstReadAfterArm { get; set; }

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                OnFirstReadAfterArm?.Invoke();
            }

            return inner.GetUtcNow();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => inner.CreateTimer(callback, state, dueTime, period);

        public void Arm() => Volatile.Write(ref _armed, 1);
    }

    // Runs the real procedure and then does what the test says, still inside the procedure's own task.
    private sealed class Wrapped(ICheckProcedure inner, Action? beforeInner, Action? beforeReturn, SealClock clock) : ICheckProcedure
    {
        public CheckKind Kind => inner.Kind;

        public bool Returned { get; private set; }

        public async Task ExecuteAsync(CheckContext context, CancellationToken ct)
        {
            beforeInner?.Invoke();
            await inner.ExecuteAsync(context, ct).ConfigureAwait(false);
            beforeReturn?.Invoke();
            Returned = true;
            clock.Arm(); // from here the procedure has returned its rows
        }
    }

    private sealed class Case(RunRig rig, CheckRunner runner, Wrapped wrapped, CancellationTokenSource cts) : IDisposable
    {
        public RunRig Rig => rig;

        public Wrapped Wrapped => wrapped;

        public Task<CheckRun> RunAsync(CheckKind kind) =>
            runner.RunAsync(kind is CheckKind.Cpu or CheckKind.Memory ? new CheckRequest(kind, TimeSpan.FromSeconds(30)) : new CheckRequest(kind), null, cts.Token).WaitAsync(Patience);

        public void Dispose()
        {
            cts.Dispose();
            rig.Dispose();
        }
    }

    // A healthy machine for the kind, the real procedure of it, and the cancel at the moment the test chose.
    private static Case Make(CheckKind kind, bool cancelBeforeReturn, bool cancelAfterReturn = true, bool cancelBeforeStart = false)
    {
        var cts = new CancellationTokenSource();
        var clock = new SealClock(kind == CheckKind.Storage ? new FakeTime(Drives.T0) : new ManualTimeProvider());
        var rig = new RunRig(clock: clock, cpuSessionFactory: () => new FakeCpuSession(Cpus.Uniform(2)), memorySessionFactory: () => FakeMemorySession.Planned(4));
        ICheckProcedure procedure;
        switch (kind)
        {
            case CheckKind.Storage:
                rig.Source.Returns(Drives.Inspect(Drives.Nvme()));
                procedure = new StorageCheckProcedure();
                break;
            case CheckKind.System:
                rig.Wmi.Rows("MSFT_PhysicalDisk", FakeWmiReader.MachineDisks());
                rig.Time.Now = new DateTimeOffset(Hw.Now);
                procedure = new SystemCheckProcedure();
                break;
            case CheckKind.Memory:
                procedure = new MemoryCheckProcedure();
                break;
            default:
                procedure = new CpuCheckProcedure();
                break;
        }

        var wrapped = new Wrapped(procedure, cancelBeforeStart ? () => cts.Cancel() : null, cancelBeforeReturn ? () => cts.Cancel() : null, clock);
        if (cancelAfterReturn)
        {
            clock.OnFirstReadAfterArm = () => cts.Cancel();
        }

        return new Case(rig, new CheckRunner(rig.Services, [wrapped]), wrapped, cts);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_stop_that_comes_after_the_procedure_returned_its_rows_leaves_the_run_completed_and_the_rows_whole(CheckKind kind)
    {
        using var test = Make(kind, cancelBeforeReturn: false);

        var run = await test.RunAsync(kind);

        Assert.True(test.Wrapped.Returned);
        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.NotEmpty(run.Result.Items);

        // The rows are those of the same run that nobody stopped: not one of them was made partial by the stop that came after.
        using var control = Make(kind, cancelBeforeReturn: false, cancelAfterReturn: false);
        var uncancelled = await control.RunAsync(kind);
        Assert.Equal(CheckOutcome.Completed, uncancelled.Result.Outcome);
        Assert.Equal(Shape(uncancelled.Result), Shape(run.Result));
        Assert.Equal(CheckOutcome.Completed, Assert.Single(test.Rig.History.Saved).Outcome);
    }

    private static List<string> Shape(CheckResult result) => [.. result.Items.Select(static i => $"{i.Id}|{i.Subject?.Id}|{i.Verdict}|{i.Reason}")];

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_stop_that_the_procedure_sees_makes_a_stopped_run(CheckKind kind)
    {
        using var test = Make(kind, cancelBeforeReturn: false, cancelAfterReturn: false, cancelBeforeStart: true);

        var run = await test.RunAsync(kind);

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal(CheckOutcome.Stopped, Assert.Single(test.Rig.History.Saved).Outcome);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_stop_that_comes_after_the_rows_are_whole_and_that_the_procedure_does_not_report_leaves_the_run_completed(CheckKind kind)
    {
        // The real procedure has returned whole rows; the stop comes before the wrapper returns, and the wrapper does not say "stopped". The
        // runner does not read the token after the return: a run is never "stopped" with whole rows (which could then be ok overall).
        using var test = Make(kind, cancelBeforeReturn: true, cancelAfterReturn: false);

        var run = await test.RunAsync(kind);

        Assert.True(test.Wrapped.Returned);
        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        using var control = Make(kind, cancelBeforeReturn: false, cancelAfterReturn: false);
        var uncancelled = await control.RunAsync(kind);
        Assert.Equal(Shape(uncancelled.Result), Shape(run.Result));
    }

    [Fact]
    public async Task A_cpu_stop_in_the_middle_of_the_load_is_stopped_and_no_row_says_ok()
    {
        using var cts = new CancellationTokenSource();
        var session = new FakeCpuSession(Cpus.Uniform(2)) { OnWait = n => { if (n == 7) { cts.Cancel(); } } };
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await rig.RunAsync(new CheckRequest(CheckKind.Cpu, TimeSpan.FromSeconds(30)), null, cts.Token).WaitAsync(Patience);

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.DoesNotContain(run.Result.Items, static i => i.Verdict == ItemVerdict.Ok); // a stopped run is never ok (DESIGN-A 2)
        Assert.Equal(CheckReason.Partial, run.Result.Items.Single(static i => i.Id == "cpu.compute_errors").Reason);
        Assert.Equal(CheckReason.Partial, run.Result.Items.Single(static i => i.Id == "cpu.chunks").Reason);
    }

    [Fact]
    public async Task A_cpu_stop_that_comes_while_the_load_is_ending_is_a_stop_with_partial_rows_and_never_a_stop_with_whole_ones()
    {
        // The stop comes with the last reading, after every second the run was asked for was done: the procedure sees the token when the load
        // returns, so the rows are partial and the run is stopped (before the seal point).
        using var cts = new CancellationTokenSource();
        var session = new CancelOnReadingSession(new FakeCpuSession(Cpus.Uniform(2)), cts, reading: 5);
        using var rig = new RunRig(cpuSessionFactory: () => session);

        var run = await rig.RunAsync(new CheckRequest(CheckKind.Cpu, TimeSpan.FromSeconds(30)), null, cts.Token).WaitAsync(Patience);

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.DoesNotContain(run.Result.Items, static i => i.Verdict == ItemVerdict.Ok);
        Assert.Equal(CheckReason.Partial, run.Result.Items.Single(static i => i.Id == "cpu.compute_errors").Reason);
    }

    // A session that cancels while the PDH sample of the given reading is taken: a stop that arrives in the middle of a second of the load.
    private sealed class CancelOnReadingSession(FakeCpuSession inner, CancellationTokenSource cts, int reading) : ICpuTestSession
    {
        private int _samples;

        public IReadOnlyList<LogicalCpu> Cpus => inner.Cpus;

        public IChunkWork Work => ((ICpuTestSession)inner).Work;

        public ICpuPin Pin => ((ICpuTestSession)inner).Pin;

        public ILoadThreadStarter Threads => ((ICpuTestSession)inner).Threads;

        public ILoadClock Clock => ((ICpuTestSession)inner).Clock;

        public CpuPdhSample SamplePdh()
        {
            if (Interlocked.Increment(ref _samples) == reading)
            {
                cts.Cancel();
            }

            return inner.SamplePdh();
        }

        public void Dispose() => inner.Dispose();
    }
}
