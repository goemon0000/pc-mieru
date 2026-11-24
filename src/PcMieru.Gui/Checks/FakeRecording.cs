using PcMieru.Checks;
using PcMieru.Core;

namespace PcMieru.Gui;

/// <summary>
/// A store of runs that lives in memory: the one of <c>--fakes</c> and of the tests. It never reads or makes a folder. Like the real one it lists off the
/// calling thread, refuses to delete the run that is being recorded, keeps an old-format run read only, and says "not found" for a run that is gone.
/// </summary>
internal sealed class InMemoryRunStore : IRunStore
{
    private readonly object _gate = new();
    private readonly List<RunInfo> _runs = [];
    private readonly Dictionary<Guid, RunSummary> _summaries = [];
    private readonly Dictionary<Guid, Exception> _readFailures = [];
    private readonly List<RunListProblem> _problems = [];
    private readonly List<int> _listThreads = [];
    private Guid? _recordingId;
    private int _listCalls;
    private int _readCalls;
    private int _deleteCalls;

    /// <summary>Said for every delete, whatever the run is (a test: "the run is being recorded by something else"). Null: the ordinary answers.</summary>
    public RunDeleteOutcome? ForcedDelete { get; set; }

    /// <summary>When set, every delete waits on it before it answers (a test holds a delete in flight to look at the screen meanwhile). Null: no wait.</summary>
    public ManualResetEventSlim? DeleteHold { get; set; }

    /// <summary>A pause every list read makes on its worker, so a test can see the list is not read on the window's thread.</summary>
    public TimeSpan ListDelay { get; set; }

    /// <summary>When set, the list read throws it (the folder could not be opened).</summary>
    public Exception? ListFailure { get; set; }

    public int ListCalls => Volatile.Read(ref _listCalls);

    public int ReadCalls => Volatile.Read(ref _readCalls);

    public int DeleteCalls => Volatile.Read(ref _deleteCalls);

    /// <summary>The managed thread ids the list was read on.</summary>
    public IReadOnlyList<int> ListThreads
    {
        get
        {
            lock (_gate)
            {
                return [.. _listThreads];
            }
        }
    }

    public IReadOnlyList<RunInfo> Runs
    {
        get
        {
            lock (_gate)
            {
                return [.. _runs];
            }
        }
    }

    public void Add(RunInfo info, RunSummary? summary = null)
    {
        lock (_gate)
        {
            _runs.RemoveAll(r => r.RunId == info.RunId);
            _runs.Add(info);
            if (summary is not null)
            {
                _summaries[info.RunId] = summary;
            }
        }
    }

    public void AddProblem(string file, string reason)
    {
        lock (_gate)
        {
            _problems.Add(new RunListProblem(file, reason));
        }
    }

    /// <summary>Makes opening this run fail the way an unreadable file does (an <see cref="IOException"/> or a <see cref="FormatException"/>).</summary>
    public void FailRead(Guid runId, Exception failure)
    {
        lock (_gate)
        {
            _readFailures[runId] = failure;
        }
    }

    /// <summary>The run whose lock is held (the one being recorded), or null.</summary>
    public void SetRecording(Guid? runId)
    {
        lock (_gate)
        {
            _recordingId = runId;
        }
    }

    public RunInfo? Info(Guid runId)
    {
        lock (_gate)
        {
            return _runs.FirstOrDefault(r => r.RunId == runId);
        }
    }

    public Task<RunList> ListAsync(CancellationToken ct) => Task.Run(
        () =>
        {
            Interlocked.Increment(ref _listCalls);
            lock (_gate)
            {
                _listThreads.Add(Environment.CurrentManagedThreadId);
            }

            if (ListDelay > TimeSpan.Zero)
            {
                Thread.Sleep(ListDelay);
            }

            if (ListFailure is { } failure)
            {
                throw failure;
            }

            lock (_gate)
            {
                var sorted = _runs.OrderByDescending(r => r.StartedAtUtc).ThenBy(r => r.RunId).ToList();
                return new RunList(sorted, [.. _problems]);
            }
        },
        CancellationToken.None);

    public Task<RunDetail?> ReadAsync(Guid runId, CancellationToken ct) => Task.Run(
        () =>
        {
            Interlocked.Increment(ref _readCalls);
            lock (_gate)
            {
                if (_readFailures.TryGetValue(runId, out var failure))
                {
                    throw failure;
                }

                var info = _runs.FirstOrDefault(r => r.RunId == runId);
                return info is null ? null : new RunDetail(info, _summaries.GetValueOrDefault(runId) ?? new RunSummary());
            }
        },
        CancellationToken.None);

    public Task<RunDeleteResult> DeleteAsync(Guid runId, CancellationToken ct) => Task.Run(
        () =>
        {
            Interlocked.Increment(ref _deleteCalls);
            DeleteHold?.Wait(CancellationToken.None);
            lock (_gate)
            {
                if (ForcedDelete is { } forced)
                {
                    return new RunDeleteResult(forced);
                }

                var info = _runs.FirstOrDefault(r => r.RunId == runId);
                if (info is null)
                {
                    return new RunDeleteResult(RunDeleteOutcome.NotFound);
                }

                if (_recordingId == runId)
                {
                    return new RunDeleteResult(RunDeleteOutcome.Recording);
                }

                if (info.IsLegacy)
                {
                    return new RunDeleteResult(RunDeleteOutcome.ReadOnlyLegacy);
                }

                _runs.Remove(info);
                _summaries.Remove(runId);
                return new RunDeleteResult(RunDeleteOutcome.Deleted);
            }
        },
        CancellationToken.None);
}

/// <summary>
/// A recorder that records nothing and writes nothing: it keeps the run in the in-memory store and ends it when it is told to. The state a test
/// or the smoke run wants to look at is set from outside (a refusal to start, a stop that never returns).
/// </summary>
internal sealed class FakeRecordingBackend(InMemoryRunStore store, string folder, Func<DateTime>? utcNow = null) : IRecordingBackend
{
    private readonly object _gate = new();
    private readonly Func<DateTime> _utcNow = utcNow ?? (static () => DateTime.UtcNow);
    private readonly List<RunMarker> _marks = [];
    private readonly TaskCompletionSource _hang = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<RunResult>? _completion;
    private RecordingRun? _run;
    private int _startCalls;
    private int _stopCalls;

    /// <summary>When set, the next start is refused with this failure (nothing is made).</summary>
    public RunStartFailure? FailStart { get; set; }

    /// <summary>The stop never returns (a writer that hangs) until <see cref="ReleaseStop"/>: the window's bounded stop has to give up by itself.</summary>
    public bool HangStop { get; set; }

    public int StartCalls => Volatile.Read(ref _startCalls);

    public int StopCalls => Volatile.Read(ref _stopCalls);

    public RunStopReason? LastStopReason { get; private set; }

    public IReadOnlyList<MarkerKind> MarkKinds
    {
        get
        {
            lock (_gate)
            {
                return [.. _marks.Select(m => m.Kind)];
            }
        }
    }

    /// <summary>Lets a hanging stop finish (a test ends its worker threads this way).</summary>
    public void ReleaseStop() => _hang.TrySetResult();

    public Task<RecordingRun> StartAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _startCalls);
        if (FailStart is { } failure)
        {
            throw new RunStartException(failure, "fake start refused");
        }

        lock (_gate)
        {
            if (_run is not null)
            {
                throw new RunStartException(RunStartFailure.AlreadyRunning, "fake: a run is going");
            }

            var id = Guid.NewGuid();
            var started = _utcNow();
            var info = FakeRunData.Info(id, folder, started, 0, RunStatus.Recording, null);
            store.Add(info);
            store.SetRecording(id);
            _marks.Clear();
            _completion = new TaskCompletionSource<RunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _run = new RecordingRun(id, started, info.JsonPath, info.CsvPath, _completion.Task);
            return Task.FromResult(_run);
        }
    }

    public Task<RunMarker> MarkAsync(MarkerKind kind, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_run is not { } run)
            {
                throw new InvalidOperationException("No run is being recorded.");
            }

            var now = _utcNow();
            var mark = new RunMarker(kind, Math.Max(0, (now - run.StartedAtUtc).Ticks / 10), now);
            _marks.Add(mark);
            if (store.Info(run.RunId) is { } info)
            {
                store.Add(info with { Markers = [.. _marks] });
            }

            return Task.FromResult(mark);
        }
    }

    public async Task<RunResult?> StopCurrentAsync(RunStopReason reason)
    {
        Interlocked.Increment(ref _stopCalls);
        LastStopReason = reason;
        if (HangStop)
        {
            await _hang.Task.ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_run is not { } run)
            {
                return null;
            }

            var ended = _utcNow();
            var seconds = Math.Max(0, (ended - run.StartedAtUtc).TotalSeconds);
            var status = reason == RunStopReason.IoError ? RunStatus.Failed : RunStatus.Completed;
            var info = FakeRunData.Info(run.RunId, folder, run.StartedAtUtc, seconds, status, reason, markers: [.. _marks]);
            store.Add(info, FakeRunData.Simple(seconds));
            store.SetRecording(null);
            var result = new RunResult(run.RunId, status, reason, run.StartedAtUtc, ended, info.DurationUs, info.Rows, 0, run.JsonPath, run.CsvPath);
            _run = null;
            _completion?.TrySetResult(result);
            return result;
        }
    }

    /// <summary>The recorder ends the run by itself (low space, a write error): what happens without anyone pressing stop.</summary>
    public RunResult? EndByItself(RunStopReason reason)
    {
        lock (_gate)
        {
            if (_run is not { } run)
            {
                return null;
            }

            LastStopReason = reason;
            var ended = _utcNow();
            var seconds = Math.Max(0, (ended - run.StartedAtUtc).TotalSeconds);
            var status = reason == RunStopReason.IoError ? RunStatus.Failed : RunStatus.Completed;
            var info = FakeRunData.Info(run.RunId, folder, run.StartedAtUtc, seconds, status, reason, markers: [.. _marks]);
            store.Add(info, FakeRunData.Simple(seconds));
            store.SetRecording(null);
            var result = new RunResult(run.RunId, status, reason, run.StartedAtUtc, ended, info.DurationUs, info.Rows, 0, run.JsonPath, run.CsvPath);
            _run = null;
            _completion?.TrySetResult(result);
            return result;
        }
    }
}

/// <summary>The fake recorder, its store and the made-up folder of <c>--fakes</c>, kept together so the smoke run and the tests reach the same objects.</summary>
internal sealed class FakeRecordingKit
{
    public FakeRecordingKit(string? folder = null, Func<DateTime>? utcNow = null)
    {
        // A path to show, nothing more: no code of the fake asks the disk about it.
        Folder = folder ?? RunStore.DefaultRunsFolder();
        UtcNow = utcNow ?? (static () => DateTime.UtcNow);
        Store = new InMemoryRunStore();
        Backend = new FakeRecordingBackend(Store, Folder, UtcNow);
    }

    public string Folder { get; }

    public Func<DateTime> UtcNow { get; }

    public InMemoryRunStore Store { get; }

    public FakeRecordingBackend Backend { get; }

    /// <summary>Whether the made-up folder "exists" (the folder button and the folder line ask the hub, which asks this).</summary>
    public bool FolderPresent { get; set; }

    /// <param name="folder">Where the hub gets its folder from (a test passes one that counts). Null: the made-up folder.</param>
    public RecordingHub CreateHub(Func<string>? folder = null)
    {
        var hub = new RecordingHub(() => Backend, () => Store, folder ?? (() => Folder), UtcNow)
        {
            FolderExists = _ => FolderPresent,
            FileExists = _ => true,

            // The made-up runs have no files: the old-app copy goes nowhere.
            CopyFile = static (_, _) => { },
            TempFolder = () => Path.Combine(Folder, "temp"),
        };
        return hub;
    }
}

/// <summary>Made-up runs for the smoke run and the tests: a finished one with holes and a section, a failed one, a cut-off one, an old one.</summary>
internal static class FakeRunData
{
    public static readonly Guid CompletedId = new("c0c0c0c0-0000-4000-8000-000000000001");
    public static readonly Guid FailedId = new("c0c0c0c0-0000-4000-8000-000000000002");
    public static readonly Guid InterruptedId = new("c0c0c0c0-0000-4000-8000-000000000003");
    public static readonly Guid LegacyId = new("c0c0c0c0-0000-4000-8000-000000000004");

    /// <summary>A run's header as a list gives it. The length is the header's (a recording's or a cut-off run's is "about").</summary>
    public static RunInfo Info(
        Guid id,
        string folder,
        DateTime startedUtc,
        double seconds,
        RunStatus status,
        RunStopReason? reason,
        bool legacy = false,
        IReadOnlyList<RunMarker>? markers = null)
    {
        var us = (long)(seconds * 1_000_000);
        return new RunInfo(
            id,
            legacy ? RunCodes.LegacyFormat : RunCodes.Format,
            "pcmieru",
            legacy,
            legacy ? "old-" + id.ToString("N")[..8] : null,
            startedUtc,
            status == RunStatus.Recording ? null : startedUtc.AddSeconds(seconds),
            us,
            status,
            reason,
            0,
            (long)seconds,
            [],
            [new RunSourceInfo("core", null, "core")],
            markers ?? [],
            Path.Combine(folder, "run-" + id.ToString("D") + ".json"),
            Path.Combine(folder, "sensors-" + id.ToString("D") + ".csv"));
    }

    /// <summary>What the fake recorder leaves for a run it ended: a few readings that were there the whole time.</summary>
    public static RunSummary Simple(double seconds) => new()
    {
        DurationSec = seconds,
        Rows = (long)seconds,
        Freshness = SummaryFreshness.Exact,
        Metrics =
        [
            new RunMetricSummary("core.cpu_usage_pct", "%", "core", (long)seconds, seconds, 0, 2.5, 18.4, 61, null),
            new RunMetricSummary("core.cpu_mhz", "MHz", "core", (long)seconds, seconds, 0, 800, 3_900, 4_700, null),
        ],
    };

    /// <summary>
    /// A finished run with everything the summary shows: holes (a power reading that was never available, a GPU that appeared late), a limit
    /// reason, a temperature rise with a clock fall, a bench section and a load section that was never ended, and the notes.
    /// </summary>
    public static RunSummary Full() => new()
    {
        DurationSec = 600,
        Rows = 600,
        DroppedTicks = 2,
        Freshness = SummaryFreshness.Exact,
        SustainedWindowSec = 200,
        Metrics =
        [
            new RunMetricSummary("core.cpu_usage_pct", "%", "core", 600, 598, 2, 3.1, 41.5, 100, 96.2),
            new RunMetricSummary("core.cpu_mhz", "MHz", "core", 600, 598, 2, 800, 4_200, 5_200, 5_000),
            new RunMetricSummary("core.mem_available_bytes", "B", "core", 600, 600, 0, 9_000_000_000, 11_000_000_000, 13_000_000_000, null),
            new RunMetricSummary("core.gpu0_util_pct", "%", "core", 540, 540, 60, 0, 52, 99, 97),
            new RunMetricSummary("gpu0.temp_c", "°C", "gpu0", 540, 540, 60, 38, 66.4, 83, 81),
            new RunMetricSummary("gpu0.clock_mhz", "MHz", "gpu0", 540, 540, 60, 210, 2_480, 2_820, 2_560),
            new RunMetricSummary("gpu0.power_w", "W", "gpu0", 540, 540, 60, 22, 188, 262, 255),
        ],
        Limits =
        [
            new RunLimitSummary("gpu0", 540, 480, 420, [new RunLimitReason("sw-power-cap", 312, 300), new RunLimitReason("sw-thermal-slowdown", 41, 41)]),
        ],
        CoOccurrences = [new RunCoOccurrence("gpu0", 301, 340, 71, 83, 2_650, 2_410, 94)],
        Sections =
        [
            new RunSection("bench", 60, 180, false,
            [
                new RunSectionMetric("core.cpu_usage_pct", "%", 120, 55, 88, 100, 91),
                new RunSectionMetric("gpu0.temp_c", "°C", 60, 61, 70, 78, 71),
            ]),
            new RunSection("stress", 200, 600, true,
            [
                new RunSectionMetric("core.cpu_usage_pct", "%", 400, 90, 99, 100, 99),
            ]),
        ],
        Notes =
        [
            new RunNote("no-data", "core.cpu_package_w", "unsupported"),
            new RunNote("sparse", "gpu0.temp_c", "90"),
            new RunNote("dropped-ticks", "", "2"),
            new RunNote("gaps", "", "62/60"),
        ],
    };

    public static IReadOnlyList<RunMarker> FullMarkers() =>
    [
        new RunMarker(MarkerKind.BenchStart, 60_000_000, DateTime.UtcNow),
        new RunMarker(MarkerKind.BenchEnd, 180_000_000, DateTime.UtcNow),
        new RunMarker(MarkerKind.StressStart, 200_000_000, DateTime.UtcNow),
        new RunMarker(MarkerKind.Warning, 60_000_000, DateTime.UtcNow, "late_gpu:NVIDIA GeForce RTX 5070"),
    ];

    /// <summary>The runs of the smoke run and of the tests: a finished one, a failed one, a cut-off one and an old one, newest first by start time.</summary>
    public static void Seed(InMemoryRunStore store, string folder, DateTime nowUtc)
    {
        store.Add(Info(CompletedId, folder, nowUtc.AddHours(-3), 600, RunStatus.Completed, RunStopReason.User, markers: FullMarkers()), Full());
        store.Add(Info(FailedId, folder, nowUtc.AddHours(-30), 45, RunStatus.Failed, RunStopReason.IoError), Simple(45));
        store.Add(Info(InterruptedId, folder, nowUtc.AddHours(-52), 735, RunStatus.Interrupted, RunStopReason.Unknown), Simple(742));
        store.Add(
            Info(LegacyId, Path.Combine(Path.GetDirectoryName(folder) ?? folder, "old"), nowUtc.AddDays(-40), 900, RunStatus.Completed, RunStopReason.User, legacy: true),
            Legacy());
    }

    /// <summary>An old-format run's summary: the seconds are estimates, and there is a note that says so.</summary>
    public static RunSummary Legacy() => new()
    {
        DurationSec = 900,
        Rows = 900,
        Freshness = SummaryFreshness.LegacyEstimated,
        Metrics =
        [
            new RunMetricSummary("core.cpu_usage_pct", "%", "core", 900, 900, 0, 1, 22, 97, null),
            new RunMetricSummary("gpu0.temp_c", "°C", "gpu0", 900, 880, 20, 35, 58, 77, null),
        ],
        Notes = [new RunNote("legacy-estimated", "")],
    };
}

/// <summary>Puts the window's <see cref="RecordingHub"/> together. Building it reads nothing, creates nothing and takes no lease.</summary>
internal static class RecordingComposition
{
    /// <param name="services">The app's services; the recorder asks them for the sampler only when a recording starts.</param>
    /// <param name="root">Where recordings are kept. Null: <c>Documents\PCMieru\runs</c> (and the old records of the earlier app, read only). A given folder has no old records.</param>
    /// <param name="fake">True: the in-memory recorder and store. Null: follows <c>--fakes</c>.</param>
    /// <param name="kit">With the fake one only: the objects the caller wants to reach (the smoke run, a test). Null: made here and kept by nobody.</param>
    /// <param name="defaultFolder">Where the default folder comes from, asked only when the hub is first asked for its folder. Null: <see cref="RunStore.DefaultRunsFolder"/>. A test passes a counting one.</param>
    public static RecordingHub Create(PcMieruServices services, string? root = null, bool? fake = null, FakeRecordingKit? kit = null, Func<string>? defaultFolder = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (fake ?? services.Options.ForceFakes)
        {
            return (kit ?? new FakeRecordingKit()).CreateHub();
        }

        // The default folder is asked for when the tab is first shown or a recording starts (the hub keeps the answer), never here: building the
        // window's context must not ask Windows where Documents is (that can be a slow redirected folder).
        var provider = defaultFolder ?? RunStore.DefaultRunsFolder;
        return new RecordingHub(
            () => new LiveRecordingBackend(new RunRecorder(services.Recording, root)),
            () => new RunStore(root),
            () => root ?? provider());
    }
}
