using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PcMieru.Core.Collection;

/// <summary>
/// The real <see cref="ISnapshotSource"/>: one NtQuerySystemInformation(SystemProcessInformation) per tick plus the
/// system-wide counters, with deltas against the previous tick (SPEC 4). Calls are serialized; the work runs on the
/// thread pool so a slow counter never blocks the caller's thread.
/// </summary>
public sealed class WindowsSnapshotSource : ISnapshotSource
{
    private const int InitialBufferBytes = 1 << 20;
    private const int MaxBufferBytes = 256 << 20;
    private const int MaxAttempts = 8;
    private const int MaxInternedNames = 4096;

    /// <summary>How long a tick waits for each counter stage. The first tick also pays for opening PDH and DXGI.</summary>
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan FirstStageTimeout = TimeSpan.FromSeconds(8);

    /// <summary>A gap this much longer than the unbiased (awake) time means the machine slept; rates over it are meaningless.</summary>
    private const long SleepThresholdTicks = 20_000_000; // 2 s in 100 ns units

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTime> _clock;
    private readonly ISamplerControl _control;
    private readonly NameInterner _names = new();
    private readonly List<RawProcess> _raw = new(512);
    private readonly Dictionary<uint, long> _createByPid = new(512);
    private readonly ProcessDeltaEngine _deltas = new();
    private readonly SystemCpuSampler _cpu = new();

    // Everything that goes through PDH or a provider runs as a guarded stage next to the kernel query: a hung counter
    // costs that one part of the snapshot (reported as a timeout), never the whole tick.
    private readonly Task<PdhSystemSampler> _pdhInit;
    private readonly NicSampler _nic = new();
    private readonly GpuSampler _gpu = new();
    private readonly GpuSensorCache _gpuSensors;
    private readonly GuardedStage<PdhSystemReading> _pdhStage = new();
    private readonly GuardedStage<IReadOnlyList<NicInfo>> _nicStage = new();
    private readonly GuardedStage<GpuReading> _gpuStage = new();

    private byte[] _buffer = GC.AllocateUninitializedArray<byte>(InitialBufferBytes, pinned: true);
    private ProcessRates[] _rates = new ProcessRates[512];
    private IReadOnlyList<GpuInfo> _gpuKnown = [];
    private long _lastStamp;
    private long _lastSleepDebt;
    private DateTime _lastTakenAt;
    private bool _hasPrevious;
    private volatile bool _resetRequested;
    private volatile bool _pdhReset;
    private volatile bool _nicReset;
    private volatile bool _gpuReset;
    private bool _disposed;

    /// <param name="clock">Wall clock for <see cref="Snapshot.TakenAtUtc"/> (tests and CLI inject their own).</param>
    /// <param name="control">Interval, pause and GPU subscription state; a default <see cref="SamplerSettings"/> when null.</param>
    /// <param name="sensors">
    /// Resolves the GPU sensor client when one is wanted, every time (the Hardware module registers after this one, and it can be
    /// replaced). Null or returning null means "no sensor client": the GPU sensor fields say Unsupported.
    /// </param>
    public WindowsSnapshotSource(Func<DateTime> clock, ISamplerControl? control = null, Func<ISensorClient?>? sensors = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _control = control ?? new SamplerSettings();
        _gpuSensors = new GpuSensorCache(sensors ?? (static () => null));

        // Opening the PDH query takes a few hundred milliseconds once; do it off the caller's thread.
        _pdhInit = Task.Run(static () => new PdhSystemSampler());
    }

    public ISamplerControl Control => _control;

    public int LogicalProcessors => _cpu.LogicalProcessors;

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
        var sleepDebt = SleepDebtTicks();
        var slept = _hasPrevious && sleepDebt - _lastSleepDebt > SleepThresholdTicks;
        if (_resetRequested || slept)
        {
            _resetRequested = false;
            _deltas.Reset();
            _cpu.Reset();
            _hasPrevious = false;
            _pdhReset = _nicReset = _gpuReset = true;
        }

        var gpuActive = _control.GpuActive;
        var timeout = _hasPrevious ? StageTimeout : FirstStageTimeout;
        _ = _pdhStage.Start(RunPdhStage);
        _ = _nicStage.Start(RunNicStage);
        _ = _gpuStage.Start(() => RunGpuStage(gpuActive));

        var length = ReadProcessBuffer();
        var stamp = Stopwatch.GetTimestamp();
        var takenAt = DateTime.SpecifyKind(_clock(), DateTimeKind.Utc);

        ct.ThrowIfCancellationRequested();
        var status = SystemProcessParser.Parse(_buffer.AsSpan(0, length), BufferAddress(), _names, _raw);
        if (_raw.Count == 0 || (status != ParseStatus.Ok && _raw.Count < 2))
        {
            throw new InvalidOperationException("SystemProcessInformation could not be read (the buffer was not in the expected layout).");
        }

        _names.TrimTo(MaxInternedNames);

        var elapsedSeconds = _hasPrevious ? Stopwatch.GetElapsedTime(_lastStamp, stamp).TotalSeconds : 0;
        var logical = _cpu.LogicalProcessors;
        if (_rates.Length < _raw.Count)
        {
            _rates = new ProcessRates[Math.Max(_raw.Count, _rates.Length * 2)];
        }

        _deltas.Compute(_raw, elapsedSeconds, logical, _rates);
        var cpu = _cpu.Sample();
        var memory = MemorySampler.Sample();

        // The stages have been running since the top of the tick; collect them now (each waits at most `timeout`).
        var pdh = JoinPdh(timeout);
        var nics = JoinNics(timeout);
        var gpu = JoinGpu(timeout, gpuActive);
        var processes = BuildProcesses(gpu);

        var nominal = (int)_control.EffectiveInterval.TotalMilliseconds;
        var intervalMs = _hasPrevious ? (int)Math.Max(1, Math.Round(elapsedSeconds * 1000)) : nominal;
        var system = new SystemInfo(
            new CpuInfo(logical, cpu.Total, cpu.PerLogical, pdh.CpuMhz),
            memory,
            pdh.Disks,
            nics,
            gpu.Gpus,
            Environment.TickCount64 / 1000);

        var snapshot = new Snapshot(
            Snapshot.CurrentSchemaVersion,
            takenAt,
            takenAt - TimeSpan.FromMilliseconds(intervalMs),
            intervalMs,
            system,
            processes);

        _lastStamp = stamp;
        _lastSleepDebt = sleepDebt;
        _lastTakenAt = takenAt;
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

    private IReadOnlyList<NicInfo> RunNicStage()
    {
        if (_nicReset)
        {
            _nicReset = false;
            _nic.Reset();
        }

        return _nic.Sample();
    }

    private GpuReading RunGpuStage(bool active)
    {
        if (_gpuReset)
        {
            _gpuReset = false;
            _gpu.Reset();
        }

        return _gpu.Sample(active);
    }

    private PdhSystemReading JoinPdh(TimeSpan timeout)
    {
        if (_pdhStage.TryJoin(timeout, out var reading, out var failure, out var failureStatus) && reading is not null)
        {
            return reading;
        }

        return new PdhSystemReading([], Value.Missing<double>(failureStatus, "PDH Processor Information", failure));
    }

    private IReadOnlyList<NicInfo> JoinNics(TimeSpan timeout) =>
        _nicStage.TryJoin(timeout, out var nics, out _, out _) && nics is not null ? nics : [];

    private GpuTick JoinGpu(TimeSpan timeout, bool active)
    {
        if (!_gpuStage.TryJoin(timeout, out var reading, out var failure, out var failureStatus) || reading is null)
        {
            var detail = "GPU counters: " + failure;
            return new GpuTick(GpuSampler.Failed(_gpuKnown, failureStatus, detail), null, failureStatus, detail);
        }

        _gpuKnown = reading.Gpus;
        if (!active)
        {
            _gpuSensors.Clear();
            return new GpuTick(reading.Gpus, reading, ValueStatus.NotCollected, null);
        }

        var merged = GpuSensorMerge.Apply(reading.Gpus, _gpuSensors.Current());
        return new GpuTick(merged, reading, ValueStatus.NotCollected, null);
    }

    /// <summary>The GPU part of a tick: the adapters for SystemInfo and what the per-process column needs.</summary>
    private sealed record GpuTick(IReadOnlyList<GpuInfo> Gpus, GpuReading? Reading, ValueStatus FailureStatus, string? FailureDetail);

    private const string GpuProcessSource = "GPU Engine";

    private static Value<double> ProcessGpu(uint pid, in ProcessRates rates, GpuTick gpu)
    {
        if (gpu.Reading is null)
        {
            return Value.Missing<double>(gpu.FailureStatus, GpuProcessSource, gpu.FailureDetail);
        }

        if (!gpu.Reading.ProcessesCollected)
        {
            return Value.NotCollected<double>(GpuProcessSource, gpu.Reading.ProcessDetail);
        }

        if (!rates.InBaseline)
        {
            return Value.NotCollected<double>(GpuProcessSource, ProcessDeltaEngine.DetailNewProcess);
        }

        // A process that is not in the list has no GPU engine instance: it did not use the GPU in this window.
        return gpu.Reading.ProcessBusiest.TryGetValue(pid, out var percent)
            ? new Value<double>(percent, ValueStatus.Ok, GpuProcessSource, gpu.Reading.ProcessDetail)
            : new Value<double>(0, ValueStatus.Ok, GpuProcessSource, "no GPU engine instance for this process");
    }

    private ProcessInfo[] BuildProcesses(GpuTick gpu)
    {
        _createByPid.Clear();
        foreach (var p in _raw)
        {
            _createByPid[p.Pid] = p.CreateTime;
        }

        var result = new ProcessInfo[_raw.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var p = _raw[i];
            var rates = _rates[i];
            result[i] = new ProcessInfo(
                p.Key,
                p.Name,
                p.ParentPid,
                ParentKeyOf(p),
                p.SessionId,
                p.ThreadCount,
                p.HandleCount,
                p.BasePriority,
                p.WorkingSet,
                p.PrivateWorkingSet,
                p.PrivatePageCount,
                p.ReadTransferCount,
                p.WriteTransferCount,
                rates.Cpu,
                rates.ReadPerSec,
                rates.WritePerSec,
                ProcessGpu(p.Pid, rates, gpu),
                p.AllThreadsSuspended
                    ? Value.Ok(ProcessState.Suspended, "thread wait reason")
                    : Value.Ok(ProcessState.Running, "thread wait reason"));
        }

        return result;
    }

    /// <summary>A parent only counts if it was already running when the child started; otherwise the PID was reused.</summary>
    private ProcessKey? ParentKeyOf(in RawProcess child)
    {
        if (child.Pid == 0 || child.ParentPid == child.Pid || child.ParentPid == 0)
        {
            return null;
        }

        return _createByPid.TryGetValue(child.ParentPid, out var created) && created <= child.CreateTime
            ? new ProcessKey(child.ParentPid, created)
            : null;
    }

    /// <summary>Reads SystemProcessInformation into the pinned buffer, growing it and retrying while the kernel says it is too small.</summary>
    private unsafe int ReadProcessBuffer()
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            uint returned;
            int status;
            fixed (byte* p = _buffer)
            {
                status = Win32.NtQuerySystemInformation(Win32.SystemProcessInformation, p, (uint)_buffer.Length, out returned);
            }

            if (status >= 0)
            {
                return (int)Math.Min(returned, (uint)_buffer.Length);
            }

            if (status is not (Win32.StatusInfoLengthMismatch or Win32.StatusBufferTooSmall or Win32.StatusBufferOverflow))
            {
                throw new InvalidOperationException($"NtQuerySystemInformation(SystemProcessInformation) failed: NTSTATUS 0x{status:X8}");
            }

            // Processes can appear between the size answer and the next call, so leave headroom.
            var wanted = Math.Max((long)returned + (returned / 4) + 65536, (long)_buffer.Length * 2);
            if (wanted > MaxBufferBytes)
            {
                break;
            }

            _buffer = GC.AllocateUninitializedArray<byte>((int)wanted, pinned: true);
        }

        throw new InvalidOperationException("SystemProcessInformation did not fit in the buffer after several attempts.");
    }

    private unsafe ulong BufferAddress() => (ulong)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_buffer));

    /// <summary>Biased tick count (includes sleep) minus unbiased interrupt time (excludes it), in 100 ns units: grows only while asleep.</summary>
    private static long SleepDebtTicks() =>
        Win32.QueryUnbiasedInterruptTime(out var unbiased)
            ? (Environment.TickCount64 * 10_000) - (long)unbiased
            : 0;
}
