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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTime> _clock;
    private readonly ISamplerControl _control;
    private readonly SystemCpuSampler _cpu = new();

    // PDH, the adapter table and the GPU counters each run as a guarded stage, as in WindowsSnapshotSource: a hung counter costs that one
    // part of the snapshot (reported as a timeout), never the whole tick.
    private readonly Task<PdhSystemSampler> _pdhInit;
    private readonly NicSampler _nic = new();
    private readonly GpuSampler _gpu = new(aggregateOnly: true);
    private readonly GuardedStage<PdhSystemReading> _pdhStage = new();
    private readonly GuardedStage<NicReading> _nicStage = new();
    private readonly GuardedStage<GpuReading> _gpuStage = new();

    private volatile Func<TimeSpan>? _beat;
    private IReadOnlyList<GpuInfo> _gpuKnown = [];
    private long _lastStamp;
    private long _lastSleepDebt;
    private bool _hasPrevious;
    private volatile bool _resetRequested;
    private volatile bool _pdhReset;
    private volatile bool _nicReset;
    private volatile bool _gpuReset;
    private bool _disposed;

    /// <param name="clock">Wall clock for <see cref="Snapshot.TakenAtUtc"/> (tests inject their own).</param>
    /// <param name="control">
    /// Interval and pause state, read only for the nominal interval of the first sample; a default <see cref="SamplerSettings"/> when null.
    /// The GPU counters are always collected here whatever <see cref="ISamplerControl.GpuActive"/> says: the source exists for them.
    /// </param>
    public AggregateSnapshotSource(Func<DateTime> clock, ISamplerControl? control = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _control = control ?? new SamplerSettings();

        // Opening the PDH query takes a few hundred milliseconds once; do it off the caller's thread.
        _pdhInit = Task.Run(static () => new PdhSystemSampler());
    }

    public ISamplerControl Control => _control;

    void IBeatAware.UseBeat(Func<TimeSpan> beat) => _beat = beat ?? throw new ArgumentNullException(nameof(beat));

    public void ResetBaseline() => _resetRequested = true;

    public async Task<Snapshot> SampleAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => SampleCore(ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Wait (briefly) for a running sample so nothing it uses is freed under it.
        var entered = _gate.Wait(TimeSpan.FromSeconds(2));
        _disposed = true;
        if (entered)
        {
            _gate.Release();
        }

        // A counter call that is still stuck owns its sampler: closing a PDH query under it is worse than leaking one.
        if (!_pdhStage.IsBusy && _pdhInit.IsCompletedSuccessfully)
        {
            _pdhInit.Result.Dispose();
        }

        if (!_gpuStage.IsBusy)
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

        var timeout = _hasPrevious ? WindowsSnapshotSource.StageTimeout : WindowsSnapshotSource.FirstStageTimeout;
        _ = _pdhStage.Start(RunPdhStage);
        _ = _nicStage.Start(RunNicStage);
        _ = _gpuStage.Start(RunGpuStage);

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
        var sampler = _pdhInit.GetAwaiter().GetResult();
        if (_pdhReset)
        {
            _pdhReset = false;
            sampler.Reset();
        }

        return sampler.Sample();
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

    private GpuReading RunGpuStage()
    {
        if (_gpuReset)
        {
            _gpuReset = false;
            _gpu.Reset();
        }

        return _gpu.Sample(active: true);
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
            var detail = "GPU counters: " + failure;
            var failed = NoSensors(GpuSampler.Failed(_gpuKnown, failureStatus, detail));
            return new GpuReading(failed, new Dictionary<uint, double>(), false, detail, GpuSampler.ListState(failed, failureStatus, detail));
        }

        _gpuKnown = reading.Gpus;
        return reading;
    }

    /// <summary>
    /// <see cref="GpuSampler.Failed"/> stamps the sensor fields with the failure too, which says "SensorHost did not answer". There is no
    /// SensorHost here, so those fields go back to "not collected" and the vendor origin stays unset.
    /// </summary>
    private static IReadOnlyList<GpuInfo> NoSensors(IReadOnlyList<GpuInfo> gpus) =>
        gpus.Select(static g => g with
        {
            TemperatureC = Value.NotCollected<double>("SensorHost", "not read yet"),
            PowerWatts = Value.NotCollected<double>("SensorHost", "not read yet"),
            FanPercent = Value.NotCollected<double>("SensorHost", "not read yet"),
            ClockMhz = Value.NotCollected<double>("SensorHost", "not read yet"),
        }).ToArray();
}
