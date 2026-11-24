using PcMieru.Core;

namespace PcMieru.Checks;

/// <summary>
/// One run's resources and its ONE stop path (the recorder starts it from <see cref="IRunRecorder.StopAsync"/>, a low-space or write error, the
/// window closing or the process exiting). <see cref="StopTask"/> is set once, under the recorder's lock: every later stop waits for the same result.
/// </summary>
internal sealed class RunSession(object owner, Guid runId, DateTime startedAtUtc, long startUs, string jsonPath, string csvPath, string lockPath)
{
    private readonly TaskCompletionSource<RunResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public object Owner { get; } = owner;

    public Guid RunId { get; } = runId;

    public DateTime StartedAtUtc { get; } = startedAtUtc;

    /// <summary>The run's start on the monotonic clock (<see cref="MonotonicClock"/>), the zero of every <c>offsetUs</c>.</summary>
    public long StartUs { get; } = startUs;

    public string JsonPath { get; } = jsonPath;

    public string CsvPath { get; } = csvPath;

    public string LockPath { get; } = lockPath;

    public RunWriter? Writer { get; set; }

    public IDisposable? Lease { get; set; }

    public IDisposable? Lock { get; set; }

    public Action<RecordingSample>? Handler { get; set; }

    /// <summary>The process-exit hook, registered while the run is active and disposed when the run has ended (the stop path does it).</summary>
    public IDisposable? ExitRegistration { get; set; }

    /// <summary>Set once by the recorder under its lock; null while the run is recording.</summary>
    public Task<RunResult>? StopTask { get; set; }

    /// <summary>Completes (never faults) when the run has ended and everything is closed.</summary>
    public Task<RunResult> Completion => _completion.Task;

    public void Complete(RunResult result) => _completion.TrySetResult(result);
}
