using PcMieru.Checks.Storage;
using PcMieru.Checks.Tests.SystemInfo;
using PcMieru.Core;
using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Model;
using PcMieru.Checks.Tests.Storage;

namespace PcMieru.Checks.Tests.Run;

/// <summary>
/// A scripted source of the storage check's input. Each call takes the next step of the script; the argument of every call is recorded, so
/// a test can say "the source was asked without elevation, once". It never reads a drive and never elevates anything.
/// </summary>
internal sealed class FakeStorageSource : IStorageInspectionSource
{
    private readonly object _gate = new();
    private readonly Queue<Func<bool, CancellationToken, Task<StorageInspection>>> _script = [];
    private readonly List<bool> _elevations = [];

    /// <summary>Set when a call has started (so a test can cancel "in the middle of the sample").</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The <c>allowElevation</c> argument of every call, in order.</summary>
    public IReadOnlyList<bool> Elevations
    {
        get
        {
            lock (_gate)
            {
                return [.. _elevations];
            }
        }
    }

    public int Calls => Elevations.Count;

    public FakeStorageSource Returns(StorageInspection inspection, Action? during = null) =>
        Then((_, _) =>
        {
            during?.Invoke();
            return Task.FromResult(inspection);
        });

    public FakeStorageSource Throws(Exception exception) => Then((_, _) => Task.FromException<StorageInspection>(exception));

    /// <summary>The call starts, then waits until the token is cancelled and ends with the cancellation (a sample in progress that is stopped).</summary>
    public FakeStorageSource Hangs() =>
        Then(async (_, ct) =>
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        });

    /// <summary>
    /// A machine where one drive is denied at normal privilege and readable once the user allows elevation: the answer depends on the
    /// argument alone. It stands in for the Helper; the Helper itself is never started.
    /// </summary>
    public FakeStorageSource ByElevation(StorageInspection normal, StorageInspection elevated) =>
        Then((allow, _) => Task.FromResult(allow ? elevated : normal));

    public FakeStorageSource Then(Func<bool, CancellationToken, Task<StorageInspection>> step)
    {
        lock (_gate)
        {
            _script.Enqueue(step);
        }

        return this;
    }

    public Task<StorageInspection> InspectAsync(bool allowElevation, CancellationToken ct = default)
    {
        Func<bool, CancellationToken, Task<StorageInspection>> step;
        lock (_gate)
        {
            _elevations.Add(allowElevation);
            if (!_script.TryDequeue(out step!))
            {
                throw new InvalidOperationException("the fake source has no step left");
            }
        }

        return step(allowElevation, ct);
    }
}

/// <summary>A history that records what it was asked to save and can answer or fail in a way the test chooses. The real store is behind it.</summary>
internal sealed class RecordingHistory(ICheckHistoryMaintainer inner) : ICheckHistoryMaintainer
{
    private readonly List<CheckResult> _saved = [];

    public IReadOnlyList<CheckResult> Saved
    {
        get
        {
            lock (_saved)
            {
                return [.. _saved];
            }
        }
    }

    /// <summary>When set, replaces the save: the result is recorded, the real store is not called.</summary>
    public Func<CheckResult, Task<CheckSaveState>>? Override { get; set; }

    public Task<CheckHistoryPage> ListAsync(CheckHistoryQuery query, CancellationToken ct) => inner.ListAsync(query, ct);

    public Task<CheckHistoryGetResult> GetAsync(Guid runId, CancellationToken ct) => inner.GetAsync(runId, ct);

    public Task<CheckSaveState> SaveAsync(CheckResult result)
    {
        lock (_saved)
        {
            _saved.Add(result);
        }

        return Override is { } custom ? custom(result) : inner.SaveAsync(result);
    }
}

/// <summary>
/// The services of one test: everything on a fresh temp folder, the baseline with its fake lock, the history store on a fault-injecting file
/// system, the source scripted by the test, a clock set by hand and run IDs counted from 1. Nothing here touches the user's folders.
/// </summary>
internal sealed class RunRig : IDisposable
{
    private long _runs;

    /// <param name="withLegacy">The baseline has an old install's records to read.</param>
    /// <param name="clock">The clock the checks' deadlines run on (a test that moves time by hand); the baseline and the history keep their own.</param>
    /// <param name="hardwareFactory">The factory of the system check's hardware provider. Null: a factory that gives <see cref="Hardware"/> every time.</param>
    public RunRig(bool withLegacy = false, TimeProvider? clock = null, Func<IHardwareProvider>? hardwareFactory = null)
    {
        Baseline = new BaselineRig(withLegacy);
        Source = new FakeStorageSource();
        HistoryFaults = new FaultFileSystem();
        HistoryRoot = Path.Combine(Baseline.Root, "checks");
        History = new RecordingHistory(Kit.Store(HistoryRoot, Baseline.Time, HistoryFaults));
        Hardware = new FakeHardware();
        Wmi = FakeWmiReader.Typical(FakeWmiReader.AnyDisks());
        Events = new FakeEventCounter();
        Services = new CheckServices(clock ?? Baseline.Time, Source, History, Baseline.Engine, Samples.App, NextRunId, hardwareFactory ?? (() => Hardware), Wmi, Events);
        Runner = new CheckRunner(Services);
    }

    public BaselineRig Baseline { get; }

    public FakeStorageSource Source { get; }

    /// <summary>The hardware of Core for the system check (a healthy typical machine until a test says otherwise). It never reads the machine.</summary>
    public FakeHardware Hardware { get; }

    /// <summary>WMI for the checks: ten healthy disks that say no serial, the page file and the video controller of a typical machine. It never calls Windows.</summary>
    public FakeWmiReader Wmi { get; }

    /// <summary>The event log for the system check: no event of any kind. It never calls Windows.</summary>
    public FakeEventCounter Events { get; }

    public FaultFileSystem HistoryFaults { get; }

    public string HistoryRoot { get; }

    public RecordingHistory History { get; }

    public CheckServices Services { get; }

    public CheckRunner Runner { get; }

    public FakeTime Time => Baseline.Time;

    /// <summary>The run IDs the next runs get: 1, 2, 3 ... (Kit.Id).</summary>
    public Guid NextRunId() => Kit.Id(Interlocked.Increment(ref _runs));

    public Task<CheckRun> RunAsync(CheckRequest? request = null, IProgress<CheckProgress>? progress = null, CancellationToken ct = default) =>
        Runner.RunAsync(request ?? new CheckRequest(CheckKind.Storage), progress, ct);

    /// <summary>The file of the run read back through the read-only history, as the CLI and the app will read it.</summary>
    public async Task<CheckResult?> ReadBackAsync(Guid runId)
    {
        var got = await CheckHistory.OpenReadOnly(HistoryRoot).GetAsync(runId, CancellationToken.None);
        return got.Status == CheckHistoryGetStatus.Found ? got.Result : null;
    }

    public void Dispose() => Baseline.Dispose();
}

/// <summary>A progress sink that collects what it is given, and can be slow or throw.</summary>
internal sealed class ProgressLog : IProgress<CheckProgress>
{
    private readonly object _gate = new();
    private readonly List<CheckProgress> _items = [];

    public Action<CheckProgress>? OnReport { get; set; }

    public IReadOnlyList<CheckProgress> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    public IReadOnlyList<CheckStage> Stages => [.. Items.Select(static p => p.Stage)];

    public void Report(CheckProgress value)
    {
        lock (_gate)
        {
            _items.Add(value);
        }

        OnReport?.Invoke(value);
    }
}

/// <summary>A kind of check made of a delegate, to test the framework without a real check behind it.</summary>
internal sealed class FakeProcedure(CheckKind kind, Func<CheckContext, CancellationToken, Task> body) : ICheckProcedure
{
    public CheckKind Kind => kind;

    public Task ExecuteAsync(CheckContext context, CancellationToken ct) => body(context, ct);
}
