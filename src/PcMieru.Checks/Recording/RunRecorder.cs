using PcMieru.Core;

namespace PcMieru.Checks;

/// <summary>What a test (or the trial) may change in the recorder; the product uses the defaults.</summary>
internal sealed record RunRecorderOptions
{
    public IRunFileSystem FileSystem { get; init; } = PhysicalRunFileSystem.Instance;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The monotonic clock in microseconds: the start of a run is taken from it (it is the clock the sampler stamps <c>AcquiredUs</c> with).</summary>
    public Func<long> NowUs { get; init; } = MonotonicClock.NowMicroseconds;

    public string App { get; init; } = CheckServices.DefaultApp;

    /// <summary>Below this much free space a run does not start and a running one is stopped (DESIGN-B §7.1: 200 MB).</summary>
    public long MinFreeBytes { get; init; } = RunRecorder.DefaultMinFreeBytes;

    /// <summary>The CSV is flushed and the JSON saved this often (10 s).</summary>
    public long FlushIntervalUs { get; init; } = 10_000_000;

    /// <summary>How many samples may wait for the writer; beyond it the oldest goes (it shows as a dropped tick).</summary>
    public int QueueCapacity { get; init; } = 256;

    public Func<Guid> NewId { get; init; } = Guid.NewGuid;
}

/// <summary>
/// Records one run at a time (one recording per GUI process, DESIGN-B §2 and §7.1). Nothing here runs, and nothing is created, until
/// <see cref="StartAsync"/>: the constructor only remembers where the runs go. A run takes a recording lease (which makes the sampler produce full
/// samples), subscribes to <see cref="IRecordingSamples.RecordingSampled"/> with a handler that only enqueues, and hands everything else to the
/// <see cref="RunWriter"/> thread. Every way a run can end (stop, low space, write error, window close, process exit) goes through ONE stop path that
/// returns the subscription and the lease, drains the writer, saves the final JSON and releases the lock.
/// </summary>
public sealed class RunRecorder : IRunRecorder, IRunActivity
{
    /// <summary>200 MB.</summary>
    public const long DefaultMinFreeBytes = 200L * 1024 * 1024;

    private readonly IRecordingSamples _samples;
    private readonly string _root;
    private readonly RunRecorderOptions _options;
    private readonly object _gate = new();

    private RunActivityState _state;
    private RunSession? _session;
    private TaskCompletionSource<RunSession?>? _starting;

    /// <summary>The runs go to <paramref name="root"/>, by default <c>Documents\PCMieru\runs</c>.</summary>
    public RunRecorder(IRecordingSamples samples, string? root = null)
        : this(samples, root, new RunRecorderOptions())
    {
    }

    internal RunRecorder(IRecordingSamples samples, string? root, RunRecorderOptions options)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(options);
        _samples = samples;
        _root = string.IsNullOrWhiteSpace(root) ? RunFiles.DefaultRoot() : root;
        _options = options;
    }

    public RunActivityState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool IsBusy => State != RunActivityState.Idle;

    public Task<RunHandle> StartAsync(CancellationToken ct)
    {
        TaskCompletionSource<RunSession?> starting;
        lock (_gate)
        {
            if (_state != RunActivityState.Idle)
            {
                return Task.FromException<RunHandle>(new RunStartException(RunStartFailure.AlreadyRunning, "A run is already being prepared, recorded or stopped."));
            }

            _state = RunActivityState.Preparing;
            starting = _starting = new TaskCompletionSource<RunSession?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // The folder, the lease and the files are not the caller's thread's business (the GUI's thread must not wait for a disk).
        return Task.Run(() => StartCore(ct, starting), CancellationToken.None);
    }

    public async Task<RunMarker> MarkAsync(RunHandle run, MarkerKind kind, CancellationToken ct)
    {
        var session = Own(run);
        if (kind == MarkerKind.Warning)
        {
            throw new ArgumentException("A warning is written by the recorder, not by a caller.", nameof(kind));
        }

        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state != RunActivityState.Recording || !ReferenceEquals(_session, session))
            {
                throw new InvalidOperationException("The run is not recording.");
            }
        }

        var offsetUs = _options.NowUs() - session.StartUs;
        var utc = _options.Time.GetUtcNow().UtcDateTime;
        // The mark is queued behind the samples before it and finishes when the JSON that holds it is on the disk. The token only ends the wait.
        return await session.Writer!.EnqueueMarker(kind, offsetUs, utc, null).WaitAsync(ct).ConfigureAwait(false);
    }

    public Task<RunResult> StopAsync(RunHandle run, RunStopReason reason)
    {
        var session = Own(run);
        if (reason == RunStopReason.Unknown)
        {
            throw new ArgumentException("A stop needs a reason; unknown is only for a run that was found without an end.", nameof(reason));
        }

        return StopSession(session, reason);
    }

    /// <summary>
    /// Stops whatever run there is (the window closing, the process exiting) and returns its result, or null when none is going. A run that is still
    /// being prepared is waited for and then stopped, so nothing is left behind.
    /// </summary>
    public async Task<RunResult?> StopCurrentAsync(RunStopReason reason)
    {
        RunSession? session;
        TaskCompletionSource<RunSession?>? starting;
        lock (_gate)
        {
            session = _session;
            starting = _state == RunActivityState.Preparing ? _starting : null;
        }

        if (session is null && starting is not null)
        {
            session = await starting.Task.ConfigureAwait(false);
        }

        return session is null ? null : await StopSession(session, reason).ConfigureAwait(false);
    }

    private RunSession Own(RunHandle run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!ReferenceEquals(run.Session.Owner, this))
        {
            throw new ArgumentException("The run belongs to another recorder.", nameof(run));
        }

        return run.Session;
    }

    private RunHandle StartCore(CancellationToken ct, TaskCompletionSource<RunSession?> starting)
    {
        RunSession session;
        try
        {
            session = Prepare(ct);
        }
        catch
        {
            lock (_gate)
            {
                _state = RunActivityState.Idle;
                _starting = null;
            }

            starting.TrySetResult(null);
            throw;
        }

        lock (_gate)
        {
            _starting = null;

            // A stop that began before this point (the writer ran into low space at once) already took the state to Stopping; do not undo it.
            if (session.StopTask is null)
            {
                _session = session;
                _state = RunActivityState.Recording;
            }
        }

        starting.TrySetResult(session);
        return new RunHandle(session.RunId, session.StartedAtUtc, session.JsonPath, session.CsvPath, session);
    }

    /// <summary>Everything a run needs, in an order where each failure undoes only what is already there. Throws <see cref="RunStartException"/> (or cancellation) with nothing left behind.</summary>
    private RunSession Prepare(CancellationToken ct)
    {
        var fs = _options.FileSystem;
        ct.ThrowIfCancellationRequested();

        try
        {
            fs.CreateDirectory(_root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RunStartException(RunStartFailure.IoError, "The runs folder could not be created.", ex);
        }

        try
        {
            // A run that was cut off earlier becomes "interrupted" before we add another (best effort: a file that cannot be fixed now is shown as it is).
            RunRecovery.Recover(fs, _root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not a reason to refuse a new recording.
        }

        if (fs.GetFreeBytes(_root) is { } free && free < _options.MinFreeBytes)
        {
            throw new RunStartException(RunStartFailure.LowSpace, "Less than 200 MB is free where the runs are kept.");
        }

        ct.ThrowIfCancellationRequested();

        var id = _options.NewId();
        var jsonPath = Path.Combine(_root, RunFiles.JsonName(id));
        var tempPath = Path.Combine(_root, RunFiles.TempName(id));
        var csvPath = Path.Combine(_root, RunFiles.CsvName(id));
        var lockPath = Path.Combine(_root, RunFiles.LockName(id));

        IDisposable? lease = null;
        IDisposable? runLock = null;
        IRunFile? csv = null;
        RunWriter? writer = null;
        Action<RecordingSample>? handler = null;
        var filesCreated = false;
        try
        {
            try
            {
                lease = _samples.AcquireRecordingLease();
            }
            catch (ObjectDisposedException ex)
            {
                throw new RunStartException(RunStartFailure.SamplerUnavailable, "The sampler has been shut down.", ex);
            }

            runLock = fs.TryLock(lockPath) ?? throw new RunStartException(RunStartFailure.IoError, "The run's lock file is held by somebody else.");
            csv = fs.CreateNew(csvPath);
            filesCreated = true;

            // The start of the run: the zero of every offsetUs. A sample acquired before it is dropped by the writer (the backlog rule).
            var startUs = _options.NowUs();
            var startedAtUtc = _options.Time.GetUtcNow().UtcDateTime;
            var session = new RunSession(this, id, startedAtUtc, startUs, jsonPath, csvPath, lockPath) { Lease = lease, Lock = runLock };
            writer = new RunWriter(new RunWriterInit(
                fs,
                id,
                _options.App,
                startedAtUtc,
                startUs,
                jsonPath,
                tempPath,
                Path.GetFileName(csvPath),
                csv,
                _options.FlushIntervalUs,
                _options.MinFreeBytes,
                _options.QueueCapacity,
                reason => _ = StopSession(session, reason)));
            csv = null; // the writer owns it now
            session.Writer = writer;

            // The first JSON (status recording, no columns yet) is on the disk before any sample can arrive.
            writer.SaveJson(RunStatus.Recording, null, null, 0);
            ct.ThrowIfCancellationRequested();

            handler = sample => writer.EnqueueSample(sample, _options.NowUs());
            session.Handler = handler;
            _samples.RecordingSampled += handler;
            writer.Start();
            return session;
        }
        catch (Exception ex)
        {
            if (handler is not null)
            {
                try
                {
                    _samples.RecordingSampled -= handler;
                }
                catch (Exception)
                {
                    // Nothing more to undo.
                }
            }

            writer?.Abort();
            csv?.Dispose();
            DisposeQuietly(lease);
            DisposeQuietly(runLock);
            if (filesCreated)
            {
                foreach (var path in new[] { csvPath, jsonPath, tempPath })
                {
                    try
                    {
                        fs.Delete(path);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // A leftover is recovered or listed like any other run.
                    }
                }
            }

            if (ex is IOException or UnauthorizedAccessException)
            {
                throw new RunStartException(RunStartFailure.IoError, "A file of the run could not be created.", ex);
            }

            throw;
        }
    }

    private static void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception)
        {
            // Returning a lease or a lock must not hide why we are here.
        }
    }

    /// <summary>The single stop path of a run. The first caller's reason is the run's reason; every later call gets the same task.</summary>
    private Task<RunResult> StopSession(RunSession session, RunStopReason reason)
    {
        lock (_gate)
        {
            if (session.StopTask is { } existing)
            {
                return existing;
            }

            _session = session;
            _state = RunActivityState.Stopping;
            return session.StopTask = Task.Run(() => StopCore(session, reason));
        }
    }

    private async Task<RunResult> StopCore(RunSession session, RunStopReason reason)
    {
        RunResult result;
        try
        {
            result = await StopSteps(session, reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing is thrown out of the stop path: the result says it failed.
            result = new RunResult(session.RunId, RunStatus.Failed, reason, session.StartedAtUtc, _options.Time.GetUtcNow().UtcDateTime, 0, 0, 0, session.JsonPath, session.CsvPath, ex.Message);
        }
        finally
        {
            DisposeQuietly(session.Lock);
            lock (_gate)
            {
                if (_session is null || ReferenceEquals(_session, session))
                {
                    _session = null;
                    _state = RunActivityState.Idle;
                }
            }
        }

        session.Complete(result);
        return result;
    }

    private async Task<RunResult> StopSteps(RunSession session, RunStopReason reason)
    {
        string? error = null;

        // The subscription and the lease go back first and whatever else fails: they are what keeps the sampler doing the heavier work.
        try
        {
            if (session.Handler is { } handler)
            {
                _samples.RecordingSampled -= handler;
            }
        }
        catch (Exception ex)
        {
            error ??= ex.Message;
        }
        finally
        {
            DisposeQuietly(session.Lease);
        }

        WriterOutcome outcome;
        try
        {
            outcome = await session.Writer!.FinishAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = new WriterOutcome(false, ex, 0, 0, 0, 0);
        }

        var endedAtUtc = _options.Time.GetUtcNow().UtcDateTime;
        var nowUs = _options.NowUs();
        var status = outcome.Ok ? RunStatus.Completed : RunStatus.Failed;
        if (outcome.Failure is { } failure)
        {
            error ??= failure.Message;
        }

        // A normal end lasts until now (the last value of each metric gets at most one period of it, the rest is missing); a failed one is what the
        // writer got onto the disk.
        var durationUs = outcome.Ok
            ? Math.Max(nowUs - session.StartUs, Math.Max(outcome.LastRowOffsetUs, outcome.LastMarkerOffsetUs))
            : outcome.LastRowOffsetUs;
        try
        {
            session.Writer!.SaveJson(status, reason, endedAtUtc, durationUs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The JSON on the disk still says "recording": the next start recovers it as interrupted.
            status = RunStatus.Failed;
            error ??= ex.Message;
        }

        return new RunResult(
            session.RunId,
            status,
            reason,
            session.StartedAtUtc,
            endedAtUtc,
            durationUs,
            outcome.Rows,
            outcome.DroppedTicks,
            session.JsonPath,
            session.CsvPath,
            error);
    }
}
