using System.Diagnostics;
using PcMieru.Core.Hardware.Collectors;

namespace PcMieru.Core.Collection;

/// <summary>
/// An <see cref="ISnapshotSource"/> that takes only machine-wide aggregates, for Parts Life (DESIGN-C 2.1). It reads CPU, memory,
/// PhysicalDisk, network adapters and GPU adapter totals with the same samplers <see cref="WindowsSnapshotSource"/> uses, and nothing else:
/// <see cref="Snapshot.Processes"/> is always empty, and it never enumerates processes or windows, reads icons or signatures, looks at
/// services, talks to the network meter, or starts SensorHost (the GPU temperature, power, fan and clock fields stay "not collected").
/// The GPU counters are folded to adapter totals without ever building a per-process table. Calls are serialized; the work runs on the
/// thread pool so a slow counter never blocks the caller's thread.
/// </summary>
public sealed class AggregateSnapshotSource : ISnapshotSource, IBeatAware
{
    /// <summary>How long after a failed PDH open the stage waits before it tries to open it again.</summary>
    internal const long PdhRetryAfterMs = 30_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTime> _clock;
    private readonly ISamplerControl _control;
    private readonly SystemCpuSampler _cpu = new();

    // PDH, the adapter table and the GPU counters each run as a guarded stage, as in WindowsSnapshotSource: a hung counter costs that one
    // part of the snapshot (reported as a timeout), never the whole tick.
    private readonly Func<IPdhSampling> _openPdh;
    private readonly Func<long> _nowMs;
    private readonly TimeSpan? _stageTimeout;
    private readonly NicSampler _nic = new();
    private readonly IGpuSampling _gpu;
    private readonly GuardedStage<PdhSystemReading> _pdhStage = new();
    private readonly GuardedStage<NicReading> _nicStage = new();
    private readonly GuardedStage<GpuReading> _gpuStage = new();

    // The sample in progress and every stage body that is running count as users. Dispose only marks the source; the samplers are freed by
    // whoever leaves last, so a counter call that is still running is never closed under itself, and one that finishes late is not leaked.
    private readonly DeferredRelease _life;

    // Owned by the PDH stage: only a stage body (one at a time) touches it, and ReleaseSamplers once no body can be running.
    private IPdhSampling? _pdh;
    private long? _pdhFailedAtMs;

    private volatile Func<TimeSpan>? _beat;
    private IReadOnlyList<GpuInfo> _gpuKnown = [];
    private long _lastStamp;
    private long _lastSleepDebt;
    private bool _hasPrevious;
    private volatile bool _resetRequested;
    private volatile bool _pdhReset;
    private volatile bool _nicReset;
    private volatile bool _gpuReset;
    private int _gpuGeneration;

    /// <param name="clock">Wall clock for <see cref="Snapshot.TakenAtUtc"/> (tests inject their own).</param>
    /// <param name="control">
    /// Interval and pause state, read only for the nominal interval of the first sample; a default <see cref="SamplerSettings"/> when null.
    /// The GPU counters are always collected here whatever <see cref="ISamplerControl.GpuActive"/> says: the source exists for them.
    /// </param>
    public AggregateSnapshotSource(Func<DateTime> clock, ISamplerControl? control = null)
        : this(clock, control, static () => new PdhSystemSampler(), new GpuSampler(aggregateOnly: true), static () => Environment.TickCount64, null)
    {
    }

    /// <summary>The seam for tests: the PDH sampler is opened by <paramref name="openPdh"/> inside its stage, and the GPU sampler is given.</summary>
    /// <param name="stageTimeout">Overrides the join limit of every stage (tests only); null keeps the production limits.</param>
    internal AggregateSnapshotSource(
        Func<DateTime> clock,
        ISamplerControl? control,
        Func<IPdhSampling> openPdh,
        IGpuSampling gpu,
        Func<long> nowMs,
        TimeSpan? stageTimeout)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _control = control ?? new SamplerSettings();
        _openPdh = openPdh ?? throw new ArgumentNullException(nameof(openPdh));
        _gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
        _stageTimeout = stageTimeout;
        _life = new DeferredRelease(ReleaseSamplers);
    }

    public ISamplerControl Control => _control;

    void IBeatAware.UseBeat(Func<TimeSpan> beat) => _beat = beat ?? throw new ArgumentNullException(nameof(beat));

    public void ResetBaseline() => _resetRequested = true;

    public async Task<Snapshot> SampleAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_life.IsRequested, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_life.TryEnter())
            {
                throw new ObjectDisposedException(nameof(AggregateSnapshotSource));
            }

            try
            {
                return await Task.Run(() => SampleCore(ct), ct).ConfigureAwait(false);
            }
            finally
            {
                _life.Exit();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks the source disposed at once and does not wait. The PDH and GPU samplers are closed by the last of the running sample and the
    /// running stage calls to finish (right here when none is running).
    /// </summary>
    public void Dispose() => _life.Request();

    private void ReleaseSamplers()
    {
        try
        {
            _pdh?.Dispose();
            _pdh = null;
        }
        finally
        {
            _gpu.Dispose();
        }
    }

    private Snapshot SampleCore(CancellationToken ct)
    {
        var sleepDebt = WindowsSnapshotSource.SleepDebtTicks();
        var slept = _hasPrevious && sleepDebt - _lastSleepDebt > WindowsSnapshotSource.SleepThresholdTicks;
        if (_resetRequested || slept)
        {
            _resetRequested = false;
            _cpu.Reset();
            _hasPrevious = false;
            _pdhReset = _nicReset = _gpuReset = true;
        }

        var timeout = _stageTimeout ?? (_hasPrevious ? WindowsSnapshotSource.StageTimeout : WindowsSnapshotSource.FirstStageTimeout);
        _ = _pdhStage.Start(RunPdhStage);
        _ = _nicStage.Start(RunNicStage);
        StartGpuStage();

        var stamp = Stopwatch.GetTimestamp();
        var takenAt = DateTime.SpecifyKind(_clock(), DateTimeKind.Utc);
        ct.ThrowIfCancellationRequested();

        var elapsedSeconds = _hasPrevious ? Stopwatch.GetElapsedTime(_lastStamp, stamp).TotalSeconds : 0;
        var cpu = _cpu.Sample();
        var memory = MemorySampler.Sample();

        // The stages have been running since the top of the tick; collect them now (each waits at most `timeout`).
        var pdh = JoinPdh(timeout);
        var nics = JoinNics(timeout);
        var gpu = JoinGpu(timeout);

        var nominal = (int)(_beat?.Invoke() ?? _control.EffectiveInterval).TotalMilliseconds;
        var intervalMs = _hasPrevious ? (int)Math.Max(1, Math.Round(elapsedSeconds * 1000)) : nominal;
        var system = new SystemInfo(
            new CpuInfo(_cpu.LogicalProcessors, cpu.Total, cpu.PerLogical, pdh.CpuMhz)
            {
                TemperatureC = WindowsSnapshotSource.NoCpuTemperature,
                PackagePowerWatts = pdh.PackageWatts,
                Sockets = _cpu.Sockets,
            },
            memory,
            pdh.Disks,
            nics.Nics,
            gpu.Gpus,
            Environment.TickCount64 / 1000,
            pdh.DisksState,
            nics.State,
            gpu.State)
        {
            Power = BatteryCollector.ReadPower(),
        };

        // No Provenance: that is the recorder's input, and this source is never recorded.
        var snapshot = new Snapshot(
            Snapshot.CurrentSchemaVersion,
            takenAt,
            takenAt - TimeSpan.FromMilliseconds(intervalMs),
            intervalMs,
            system,
            []);

        _lastStamp = stamp;
        _lastSleepDebt = sleepDebt;
        _hasPrevious = true;
        return snapshot;
    }

    private PdhSystemReading RunPdhStage()
    {
        if (!_life.TryEnter())
        {
            throw new ObjectDisposedException(nameof(AggregateSnapshotSource));
        }

        try
        {
            var sampler = EnsurePdh();
            if (_pdhReset)
            {
                _pdhReset = false;
                sampler.Reset();
            }

            return sampler.Sample();
        }
        finally
        {
            _life.Exit();
        }
    }

    /// <summary>
    /// The open PDH sampler. Opening it takes a few hundred milliseconds, so it happens here, inside the guarded stage. A failed open (the query
    /// did not open, or the constructor threw) is closed and tried again later, at most once per <see cref="PdhRetryAfterMs"/>, instead of
    /// switching the stage off for the life of the source.
    /// </summary>
    private IPdhSampling EnsurePdh()
    {
        if (_pdh is { IsOpen: true } open)
        {
            return open;
        }

        var now = _nowMs();
        if (_pdhFailedAtMs is { } failedAt && now - failedAt < PdhRetryAfterMs)
        {
            throw new InvalidOperationException($"PDH could not be opened; trying again in {Math.Ceiling((PdhRetryAfterMs - (now - failedAt)) / 1000.0):0} s");
        }

        _pdh?.Dispose();
        _pdh = null;
        try
        {
            var sampler = _openPdh();
            if (!sampler.IsOpen)
            {
                sampler.Dispose();
                throw new InvalidOperationException("PDH could not be opened");
            }

            _pdh = sampler;
            _pdhFailedAtMs = null;
            return sampler;
        }
        catch
        {
            _pdhFailedAtMs = now;
            throw;
        }
    }

    private NicReading RunNicStage()
    {
        if (_nicReset)
        {
            _nicReset = false;
            _nic.Reset();
        }

        var nics = _nic.Sample();
        return new NicReading(nics, _nic.State);
    }

    /// <summary>
    /// Starts the GPU stage with the reset request taken at this moment: the request belongs to the stage that is scheduled now, never to
    /// whichever body happens to reach its check first. When the previous call is still running the stage is skipped and the request stays
    /// pending for the next one that does start.
    /// </summary>
    private void StartGpuStage()
    {
        var reset = _gpuReset;
        var generation = Volatile.Read(ref _gpuGeneration);
        if (_gpuStage.Start(() => RunGpuStage(reset, generation)) && reset)
        {
            _gpuReset = false;
        }
    }

    private GpuReading RunGpuStage(bool reset, int generation)
    {
        if (!_life.TryEnter())
        {
            throw new ObjectDisposedException(nameof(AggregateSnapshotSource));
        }

        try
        {
            // A stage whose join gave up before it got going touches nothing: the next stage starts a new window anyway, and a late collection
            // here would only move the sampler's baseline under it.
            ThrowIfAbandoned(generation);
            if (reset)
            {
                _gpu.Reset();
                ThrowIfAbandoned(generation);
            }

            // A call that is already inside the sampler when its join gives up cannot be stopped; the request the failed join leaves behind
            // makes the next stage reset before it samples, so what this call did to the baseline is thrown away.
            return _gpu.Sample(active: true);
        }
        finally
        {
            _life.Exit();
        }
    }

    private void ThrowIfAbandoned(int generation)
    {
        if (Volatile.Read(ref _gpuGeneration) != generation)
        {
            throw new OperationCanceledException("the GPU stage was given up on before it started");
        }
    }

    private PdhSystemReading JoinPdh(TimeSpan timeout) =>
        _pdhStage.TryJoin(timeout, out var reading, out var failure, out var failureStatus) && reading is not null
            ? reading
            : WindowsSnapshotSource.PdhFailed(failure, failureStatus);

    private NicReading JoinNics(TimeSpan timeout) =>
        _nicStage.TryJoin(timeout, out var nics, out var failure, out var failureStatus) && nics is not null
            ? nics
            : WindowsSnapshotSource.NicsFailed(failure, failureStatus);

    private GpuReading JoinGpu(TimeSpan timeout)
    {
        if (!_gpuStage.TryJoin(timeout, out var reading, out var failure, out var failureStatus) || reading is null)
        {
            // The call that did not answer may still finish and move the sampler's baseline. That collection is discarded: a stage that has not
            // started yet is told to stand down (the generation), and the next stage that does start resets first and so begins a new window
            // (it reads "not collected" once) instead of publishing a rate built on the late call.
            Interlocked.Increment(ref _gpuGeneration);
            _gpuReset = true;
            var detail = "GPU counters: " + failure;
            var failed = NoSensors(GpuSampler.Failed(_gpuKnown, failureStatus, detail));
            return new GpuReading(failed, new Dictionary<uint, double>(), false, detail, GpuSampler.ListState(failed, failureStatus, detail));
        }

        var gpus = NoSensors(reading.Gpus);
        _gpuKnown = gpus;
        return reading with { Gpus = gpus };
    }

    /// <summary>
    /// <see cref="GpuSampler.Failed"/> and the sampler's own "not open" and collect-error results stamp the sensor fields with the failure too,
    /// which reads as "SensorHost did not answer". There is no SensorHost here, so on every result those fields are "not collected" and the
    /// vendor origin is unset.
    /// </summary>
    internal static IReadOnlyList<GpuInfo> NoSensors(IReadOnlyList<GpuInfo> gpus) =>
        gpus.Select(static g => g with
        {
            TemperatureC = Value.NotCollected<double>("SensorHost", "not read yet"),
            PowerWatts = Value.NotCollected<double>("SensorHost", "not read yet"),
            FanPercent = Value.NotCollected<double>("SensorHost", "not read yet"),
            ClockMhz = Value.NotCollected<double>("SensorHost", "not read yet"),
            TemperatureLimitC = Value.NotCollected<double>("none"),
            LimitReasons = Value.NotCollected<ulong>("none"),
            VendorOrigin = null,
        }).ToArray();
}

/// <summary>The PDH sampler as <see cref="AggregateSnapshotSource"/> uses it (a seam for tests).</summary>
internal interface IPdhSampling : IDisposable
{
    bool IsOpen { get; }

    void Reset();

    PdhSystemReading Sample();
}

/// <summary>The GPU sampler as <see cref="AggregateSnapshotSource"/> uses it (a seam for tests).</summary>
internal interface IGpuSampling : IDisposable
{
    void Reset();

    GpuReading Sample(bool active);
}

/// <summary>
/// Frees a resource once nobody is using it any more. Users register with <see cref="TryEnter"/> and leave with <see cref="Exit"/>;
/// <see cref="Request"/> (a dispose) refuses new users at once and runs <c>release</c> exactly once, right away when nobody is inside and
/// otherwise from the last <see cref="Exit"/>. Nothing waits and nothing that is in use is ever released.
/// </summary>
internal sealed class DeferredRelease(Action release)
{
    private readonly object _lock = new();
    private int _users;
    private bool _requested;
    private bool _released;

    public bool IsRequested
    {
        get
        {
            lock (_lock)
            {
                return _requested;
            }
        }
    }

    /// <summary>Registers a user. False once <see cref="Request"/> was called (the caller must not touch the resource).</summary>
    public bool TryEnter()
    {
        lock (_lock)
        {
            if (_requested)
            {
                return false;
            }

            _users++;
            return true;
        }
    }

    public void Exit()
    {
        bool run;
        lock (_lock)
        {
            _users--;
            run = _requested && _users == 0 && !_released;
            _released |= run;
        }

        if (run)
        {
            release();
        }
    }

    public void Request()
    {
        bool run;
        lock (_lock)
        {
            _requested = true;
            run = _users == 0 && !_released;
            _released |= run;
        }

        if (run)
        {
            release();
        }
    }
}
