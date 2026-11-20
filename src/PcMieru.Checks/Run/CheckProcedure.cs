namespace PcMieru.Checks;

/// <summary>
/// What one kind of check does (system, CPU, memory, storage plug in as one of these). It collects its rows and notes into the
/// <see cref="CheckCollector"/> as it goes, so that what it found before a stop is kept, and it never builds the result, decides the
/// outcome, validates or saves: that is <see cref="CheckRunner"/>'s single finalisation point.
/// It ends in one of four ways: returns (completed, or stopped when the collector was told so), throws
/// <see cref="OperationCanceledException"/> because its token was cancelled (stopped), throws <see cref="CheckRunFailedException"/> (failed,
/// with its code), or throws anything else (failed, <see cref="CheckRunFailureCodes.InternalError"/>).
/// </summary>
internal interface ICheckProcedure
{
    CheckKind Kind { get; }

    Task ExecuteAsync(CheckContext context, CancellationToken ct);
}

/// <summary>A failure of the run as a whole (not of one reading): the run is failed, with this code. The detail has no path, serial or ID.</summary>
internal sealed class CheckRunFailedException(string code, string? detail = null, Exception? inner = null) : Exception(detail ?? code, inner)
{
    public string Code { get; } = code;

    public string? Detail { get; } = detail;
}

/// <summary>The rows and notes of a run in progress. Only the procedure's own thread writes; the runner reads after the procedure has ended.</summary>
internal sealed class CheckCollector
{
    private readonly List<CheckItem> _items = [];
    private readonly List<string> _notes = [];

    public IReadOnlyList<CheckItem> Items => _items;

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>The procedure stopped by itself (for example a safety limit), not because the caller cancelled.</summary>
    public bool StoppedByProcedure { get; private set; }

    public void Add(CheckItem item) => _items.Add(item);

    public void AddRange(IEnumerable<CheckItem> items) => _items.AddRange(items);

    /// <summary>A note, once: a note that is already there is not written twice (the validator rejects a duplicate).</summary>
    public void Note(string note)
    {
        if (!_notes.Contains(note, StringComparer.Ordinal))
        {
            _notes.Add(note);
        }
    }

    public void MarkStopped() => StoppedByProcedure = true;
}

/// <summary>What a procedure sees of its run: the request, the services, the run's ID and clock, the collector, and the way to tell the caller where it is.</summary>
internal sealed class CheckContext
{
    // About once a second, never closer than 0.9 s: the 0.1 s slack lets a reading that comes a little early after a late one (timer
    // jitter on the load's absolute schedule) still go out, so a 30 s load gives about 30 readings, not 21; a burst is still held back.
    private static readonly TimeSpan LoadReportInterval = TimeSpan.FromMilliseconds(900);

    private readonly CheckProgressPump _pump;
    private readonly Lock _gate = new();
    private long _lastLoadReport = long.MinValue;

    public CheckContext(CheckServices services, CheckRequest request, Guid runId, DateTimeOffset startedUtc, CheckCollector collector, CheckProgressPump pump)
    {
        Services = services;
        Request = request;
        RunId = runId;
        StartedUtc = startedUtc;
        Collector = collector;
        _pump = pump;
    }

    public CheckServices Services { get; }

    public CheckRequest Request { get; }

    public Guid RunId { get; }

    public DateTimeOffset StartedUtc { get; }

    public CheckCollector Collector { get; }

    public TimeProvider Time => Services.Time;

    /// <summary>A stage change. Always delivered.</summary>
    public void Stage(CheckStage stage, double targetSeconds = 0) =>
        _pump.Post(new CheckProgress(RunId, stage, ElapsedSeconds(), targetSeconds));

    /// <summary>A load reading: about one a second and never two within 0.9 s (the first one always goes), whatever the procedure's own pace.</summary>
    public void Load(double elapsedSeconds, double targetSeconds, double? recentRate, string? rateUnit)
    {
        var now = Time.GetTimestamp();
        lock (_gate)
        {
            if (_lastLoadReport != long.MinValue && Time.GetElapsedTime(_lastLoadReport, now) < LoadReportInterval)
            {
                return;
            }

            _lastLoadReport = now;
        }

        _pump.Post(new CheckProgress(RunId, CheckStage.Loading, elapsedSeconds, targetSeconds, recentRate, rateUnit));
    }

    private double ElapsedSeconds() => Math.Max(0, (Time.GetUtcNow() - StartedUtc).TotalSeconds);
}
