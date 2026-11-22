namespace PcMieru.Checks;

/// <summary>A mark in a run. The codes are fixed (<see cref="RunCodes"/>); free text is not accepted, so nothing a person typed ends up in an export.</summary>
public enum MarkerKind
{
    BenchStart,
    BenchEnd,
    StressStart,
    StressEnd,
    Mark,

    /// <summary>
    /// Written by the recorder itself, never by a caller. It says a GPU appeared after the columns were fixed (detail <c>late_gpu:&lt;name&gt;</c>), that a
    /// GPU came back under a new id and went on in its old column (<c>gpu_rebound:&lt;name&gt;</c>).
    /// </summary>
    Warning,
}

/// <summary>How far a run got. <c>recording</c> is only ever written while the recorder holds the run's lock.</summary>
public enum RunStatus
{
    Recording,
    Completed,
    Interrupted,
    Failed,
}

/// <summary>Why a run ended. <see cref="Unknown"/> is a run that was found without an end (the process died); it is never given to <see cref="IRunRecorder.StopAsync"/> by a caller.</summary>
public enum RunStopReason
{
    User,
    WindowClose,
    LowSpace,
    IoError,
    ProcessExit,
    Unknown,

    /// <summary>The CSV reached the recorder's own size cap (below the reader's, so a finished run can always be read).</summary>
    SizeLimit,
}

/// <summary>How a column's cells are read: a plain number, a 0/1 flag, an unsigned bit mask written as a decimal string (never through a double), or text.</summary>
public enum RunMetricKind
{
    Number,
    Flag,
    Bitmask,
    Text,
}

/// <summary>Why <see cref="IRunRecorder.StartAsync"/> did not start.</summary>
public enum RunStartFailure
{
    /// <summary>A run is already being prepared, recorded or stopped in this process (one recording per GUI process).</summary>
    AlreadyRunning,

    /// <summary>Less than 200 MB is free where the runs are kept.</summary>
    LowSpace,

    /// <summary>A folder or file could not be created.</summary>
    IoError,

    /// <summary>The sampler is gone (the loop was disposed), so there is nothing to record from.</summary>
    SamplerUnavailable,
}

public sealed class RunStartException(RunStartFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public RunStartFailure Failure { get; } = failure;
}

/// <summary>What the recorder is doing now. Step 5's update idle check treats everything but <see cref="Idle"/> as "a recording is in the way".</summary>
public enum RunActivityState
{
    Idle,

    /// <summary>StartAsync is taking the lease and creating the files.</summary>
    Preparing,

    Recording,

    /// <summary>The one stop path is running: unsubscribe, return the lease, drain the writer, save the final JSON, release the lock.</summary>
    Stopping,
}

/// <summary>
/// "Is a recording in the way?" for the update idle check (DESIGN-B §2: preparing and stopping count too). <see cref="RunRecorder"/> implements it;
/// the update code needs this one member and nothing else of the recorder.
/// </summary>
public interface IRunActivity
{
    RunActivityState State { get; }

    /// <summary>True in every state but <see cref="RunActivityState.Idle"/>.</summary>
    bool IsBusy { get; }
}

/// <summary>A mark. <see cref="OffsetUs"/> is on the run's monotonic clock (microseconds since the start), the same scale as the CSV's <c>offsetUs</c>.</summary>
public sealed record RunMarker(MarkerKind Kind, long OffsetUs, DateTime Utc, string? Detail = null);

/// <summary>One column of the CSV (key = the column name), as the JSON's <c>metrics</c> dictionary lists it.</summary>
public sealed record RunMetricInfo(string Key, string Unit, string Source, RunMetricKind Kind);

/// <summary>One source of values: <c>core</c> (the sampler pass) or <c>gpu&lt;N&gt;</c> (a SensorHost frame). <see cref="DeviceId"/> is the stable adapter id of a GPU, null for core.</summary>
public sealed record RunSourceInfo(string Source, string? DeviceId, string Name);

/// <summary>The end of a run: what <see cref="IRunRecorder.StopAsync"/> and <see cref="RunHandle.Completion"/> give.</summary>
public sealed record RunResult(
    Guid RunId,
    RunStatus Status,
    RunStopReason StopReason,
    DateTime StartedAtUtc,
    DateTime EndedAtUtc,
    long DurationUs,
    long Rows,
    long DroppedTicks,
    string JsonPath,
    string CsvPath,
    string? Error = null);

/// <summary>A run that is being recorded. <see cref="Completion"/> finishes when the run ends, whoever ended it (a user stop, low space, a write error, the window closing).</summary>
public sealed class RunHandle
{
    internal RunHandle(Guid runId, DateTime startedAtUtc, string jsonPath, string csvPath, RunSession session)
    {
        RunId = runId;
        StartedAtUtc = startedAtUtc;
        JsonPath = jsonPath;
        CsvPath = csvPath;
        Session = session;
    }

    public Guid RunId { get; }

    public DateTime StartedAtUtc { get; }

    public string JsonPath { get; }

    public string CsvPath { get; }

    /// <summary>Completes when the run has ended and everything is closed (the lease and the subscription are returned, the final JSON is saved, the lock is released).</summary>
    public Task<RunResult> Completion => Session.Completion;

    internal RunSession Session { get; }
}

/// <summary>Records one run at a time from <see cref="PcMieru.Core.IRecordingSamples"/> (DESIGN-B §2, §7.1).</summary>
public interface IRunRecorder
{
    /// <summary>Starts a recording. Throws <see cref="RunStartException"/> (a run is already going, no space, a file error) with nothing left behind.</summary>
    Task<RunHandle> StartAsync(CancellationToken ct);

    /// <summary>Puts a mark at "now" through the writer, in order with the samples, and saves the JSON. Throws <see cref="InvalidOperationException"/> when the run is not recording.</summary>
    Task<RunMarker> MarkAsync(RunHandle run, MarkerKind kind, CancellationToken ct);

    /// <summary>Ends the run. A second call (or a call during a stop that low space or a write error began) waits for the same result.</summary>
    Task<RunResult> StopAsync(RunHandle run, RunStopReason reason);

    /// <summary>
    /// Stops whatever run there is and returns its result, or null when none is going. For the window closing and the session ending, where the caller has
    /// no <see cref="RunHandle"/>. A run that is still being prepared is waited for and then stopped. On the interface (not on <see cref="IRunActivity"/>,
    /// which the update code only reads) so the GUI can call it through the one thing it holds. From a form's closing handler use
    /// <see cref="RunRecorderExtensions.StopCurrentBlocking"/>, which is bounded.
    /// </summary>
    Task<RunResult?> StopCurrentAsync(RunStopReason reason);
}

/// <summary>The bounded, synchronous stop for places that cannot await (<c>FormClosing</c>, <c>SessionEnding</c>, process exit).</summary>
public static class RunRecorderExtensions
{
    /// <summary>How long exit may wait for a run to close (about 2 s).</summary>
    public static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Stops the current run and waits for it, but never longer than <paramref name="bound"/> (<see cref="ExitBound"/> by default). Returns the result,
    /// or null when no run was going or the stop did not finish in time (then the run is left as <c>recording</c> on the disk, with its lock released by
    /// the process ending: the next start recovers it as interrupted). Never throws and never blocks on a context: the stop runs on the thread pool, so a UI
    /// thread waiting here cannot deadlock it.
    /// </summary>
    public static RunResult? StopCurrentBlocking(this IRunRecorder recorder, RunStopReason reason, TimeSpan? bound = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        try
        {
            var stop = Task.Run(() => recorder.StopCurrentAsync(reason));
            return stop.Wait(bound ?? ExitBound) ? stop.Result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// One run in a listing (JSON only: no CSV is read to make a list). <see cref="Rows"/> and <see cref="DurationUs"/> here are what the JSON said when it was
/// last saved, which is at most one flush interval (10 s) behind the CSV for a run that was cut off and has not been recovered yet (the next recording
/// start rewrites them from the CSV). A screen must not present them as exact for such a run: show them as "about", or open the run (the detail is read
/// from the CSV and is exact).
/// </summary>
public sealed record RunInfo(
    Guid RunId,
    string Format,
    string App,
    bool IsLegacy,
    string? LegacyId,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    long DurationUs,
    RunStatus Status,
    RunStopReason? StopReason,
    long DroppedTicks,
    long Rows,
    IReadOnlyList<RunMetricInfo> Metrics,
    IReadOnlyList<RunSourceInfo> Sources,
    IReadOnlyList<RunMarker> Markers,
    string JsonPath,
    string CsvPath);

/// <summary>A file in the runs folder that could not be listed, with the reason code (<c>unreadable</c>, <c>not_a_run</c>, <c>bad_format</c>).</summary>
public sealed record RunListProblem(string File, string Reason);

public sealed record RunList(IReadOnlyList<RunInfo> Runs, IReadOnlyList<RunListProblem> Problems);

public sealed record RunDetail(RunInfo Info, RunSummary Summary);

public enum RunDeleteOutcome
{
    Deleted,
    NotFound,

    /// <summary>The run is being recorded (its lock is held): nothing was deleted.</summary>
    Recording,

    /// <summary>An old <c>partslife-run/1</c> run: it is only ever read.</summary>
    ReadOnlyLegacy,

    /// <summary>A file could not be deleted (the CSV goes first, so a half-deleted run is a JSON without data, never data without a JSON).</summary>
    Failed,
}

public sealed record RunDeleteResult(RunDeleteOutcome Outcome, string? Error = null);

/// <summary>Reads and deletes finished runs, the old format included (read only).</summary>
public interface IRunStore
{
    Task<RunList> ListAsync(CancellationToken ct);

    /// <summary>
    /// The run and its summary, or null when there is no such run. A run whose JSON cannot be parsed throws <see cref="FormatException"/>; one whose file
    /// cannot be read (or an old run whose CSV is missing) throws <see cref="IOException"/>. The list shows such a file as a problem instead.
    /// </summary>
    Task<RunDetail?> ReadAsync(Guid runId, CancellationToken ct);

    /// <summary>Deletes a finished run. A run whose lock is held (it is recording) is refused.</summary>
    Task<RunDeleteResult> DeleteAsync(Guid runId, CancellationToken ct);
}
