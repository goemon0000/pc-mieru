using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PcMieru.Wire;

namespace PcMieru.Core.Collection;

/// <summary>
/// The real <see cref="ISnapshotSource"/>: one NtQuerySystemInformation(SystemProcessInformation) per tick plus the
/// system-wide counters, with deltas against the previous tick (SPEC 4). Calls are serialized; the work runs on the
/// thread pool so a slow counter never blocks the caller's thread.
/// </summary>
public sealed class WindowsSnapshotSource : ISnapshotSource, IProcessGroupSource
{
    private const int InitialBufferBytes = 1 << 20;
    private const int MaxBufferBytes = 256 << 20;
    private const int MaxAttempts = 8;
    private const int MaxInternedNames = 4096;

    private static readonly Value<double> NoCpuTemperature = Value.Unsupported<double>(
        "none",
        "the CPU package temperature needs a kernel driver, which PC Mieru does not install");

    /// <summary>How long a tick waits for each counter stage. The first tick also pays for opening PDH and DXGI.</summary>
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan FirstStageTimeout = TimeSpan.FromSeconds(8);

    /// <summary>The whole budget of one read from the network meter (it may reconnect once inside it); less than <see cref="StageTimeout"/> so the stage's own limit is only the backstop.</summary>
    private static readonly TimeSpan NetReadTimeout = TimeSpan.FromSeconds(2.5);

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
    private readonly ProcessGrouper _grouper;

    // Everything that goes through PDH or a provider runs as a guarded stage next to the kernel query: a hung counter
    // costs that one part of the snapshot (reported as a timeout), never the whole tick.
    private readonly Task<PdhSystemSampler> _pdhInit;
    private readonly NicSampler _nic = new();
    private readonly GpuSampler _gpu = new();
    private readonly GpuSensorCache _gpuSensors;
    private readonly GuardedStage<PdhSystemReading> _pdhStage = new();
    private readonly GuardedStage<NicReading> _nicStage = new();
    private readonly GuardedStage<GpuReading> _gpuStage = new();
    private readonly GuardedStage<WindowProbeResult> _windowStage = new();

    // The network meter is another process: its read is a guarded stage too. The rates are worked out on the tick's own thread
    // (they need the process list), from the meter's counters and the meter's own clock.
    private readonly Func<INetMeterControl?> _net;
    private readonly GuardedStage<NetStageResult> _netStage = new();
    private readonly NetRateCalculator _netCalc = new();
    private readonly List<ProcessKey> _netKeys = new(512);
    private NetRates[] _netRates = new NetRates[512];

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
    private ProcessInfo[]? _latestProcesses;

    /// <param name="clock">Wall clock for <see cref="Snapshot.TakenAtUtc"/> (tests and CLI inject their own).</param>
    /// <param name="control">Interval, pause and GPU subscription state; a default <see cref="SamplerSettings"/> when null.</param>
    /// <param name="sensors">
    /// Resolves the GPU sensor client when one is wanted, every time (the Hardware module registers after this one, and it can be
    /// replaced). Null or returning null means "no sensor client": the GPU sensor fields say Unsupported.
    /// </param>
    public WindowsSnapshotSource(Func<DateTime> clock, ISamplerControl? control = null, Func<ISensorClient?>? sensors = null)
        : this(clock, control, sensors, new SignatureVerifier(), null)
    {
    }

    /// <param name="verifier">Shared with the details provider so a file is hashed once for both (grouping looks at Windows-folder files).</param>
    /// <param name="net">
    /// Resolves the network meter control every time (it can be replaced). Null or returning null means no meter: the network
    /// values say it is off.
    /// </param>
    internal WindowsSnapshotSource(Func<DateTime> clock, ISamplerControl? control, Func<ISensorClient?>? sensors, SignatureVerifier verifier, Func<INetMeterControl?>? net)
    {
        _net = net ?? (static () => null);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _grouper = new ProcessGrouper(new WindowsFolderClassifier(verifier.Verify).Classify);
        _control = control ?? new SamplerSettings();
        _gpuSensors = new GpuSensorCache(sensors ?? (static () => null));

        // Opening the PDH query takes a few hundred milliseconds once; do it off the caller's thread.
        _pdhInit = Task.Run(static () => new PdhSystemSampler());
    }

    public ISamplerControl Control => _control;

    public int LogicalProcessors => _cpu.LogicalProcessors;

    /// <summary>
    /// The image name of this exact process (PID and creation time) if the most recent sample contained it, otherwise null.
    /// Lets the details provider name kernel processes that have no image file and confirm a PID it cannot open is still the
    /// same process. Safe from any thread.
    /// </summary>
    internal string? NameOf(ProcessKey key)
    {
        var latest = Volatile.Read(ref _latestProcesses);
        if (latest is null)
        {
            return null;
        }

        foreach (var p in latest)
        {
            if (p.Key == key)
            {
                return p.Name;
            }
        }

        return null;
    }

    public void ResetBaseline() => _resetRequested = true;

    /// <inheritdoc />
    public Value<ProcessGroup> GroupOf(ProcessKey key) => _grouper.GroupOf(key);

    /// <summary>Waits until every process in the latest sample has had its Windows-or-Background check run (for tests and one-shot tools).</summary>
    internal bool WaitForGroups(TimeSpan timeout) => _grouper.WaitForIdle(timeout);

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
        _grouper.Dispose();
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
            _netCalc.Reset();
            _hasPrevious = false;
            _pdhReset = _nicReset = _gpuReset = true;
        }

        var gpuActive = _control.GpuActive;
        var timeout = _hasPrevious ? StageTimeout : FirstStageTimeout;
        _ = _pdhStage.Start(RunPdhStage);
        _ = _nicStage.Start(RunNicStage);
        _ = _gpuStage.Start(() => RunGpuStage(gpuActive));
        _ = _windowStage.Start(WindowProbe.Probe);
        _ = _netStage.Start(RunNetStage);

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
        var windows = JoinWindows(timeout);
        ComputeNet(JoinNet(timeout));
        var processes = BuildProcesses(gpu, windows);

        var nominal = (int)_control.EffectiveInterval.TotalMilliseconds;
        var intervalMs = _hasPrevious ? (int)Math.Max(1, Math.Round(elapsedSeconds * 1000)) : nominal;
        var system = new SystemInfo(
            new CpuInfo(logical, cpu.Total, cpu.PerLogical, pdh.CpuMhz) { TemperatureC = NoCpuTemperature },
            memory,
            pdh.Disks,
            nics.Nics,
            gpu.Gpus,
            Environment.TickCount64 / 1000,
            pdh.DisksState,
            nics.State,
            gpu.State);

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
        _latestProcesses = processes;
        _grouper.Update(processes, windows.Result, windows.FailureStatus, windows.FailureDetail);
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

    private GpuReading RunGpuStage(bool active)
    {
        if (_gpuReset)
        {
            _gpuReset = false;
            _gpu.Reset();
        }

        return _gpu.Sample(active);
    }

    private NetStageResult RunNetStage()
    {
        var control = _net();
        if (control is null)
        {
            return NetStageResult.Without(NetMeterValues.Missing(new NetMeterState(NetMeterStatus.Off)));
        }

        var state = control.State;
        if (state.Status != NetMeterStatus.Running)
        {
            return NetStageResult.Without(NetMeterValues.Missing(state));
        }

        if (control is not INetMeterReader reader)
        {
            return NetStageResult.Without(Value.Unsupported<double>(NetMeterValues.Source, NetMeterValues.UnsupportedDetail));
        }

        if (reader.TryRead(NetReadTimeout, out var reading, out var generation) && reading is not null)
        {
            return new NetStageResult(reading, generation, default);
        }

        // The read failed. The control has already moved on and says why (Failed with plain words), or was stopped meanwhile.
        var after = control.State;
        return NetStageResult.Without(after.Status == NetMeterStatus.Running
            ? Value.Failed<double>(NetMeterValues.Source, NetMeterValues.FailedDetail)
            : NetMeterValues.Missing(after));
    }

    private NetStageResult JoinNet(TimeSpan timeout) =>
        _netStage.TryJoin(timeout, out var result, out var failure, out var failureStatus) && result is not null
            ? result
            : NetStageResult.Without(Value.Missing<double>(failureStatus, NetMeterValues.Source, "network meter: " + failure));

    /// <summary>Per-process network rates for this tick, in the order of <c>_raw</c>: from the meter's counters, or the reason there are none.</summary>
    private void ComputeNet(NetStageResult net)
    {
        var count = _raw.Count;
        if (_netRates.Length < count)
        {
            _netRates = new NetRates[Math.Max(count, _netRates.Length * 2)];
        }

        if (net.Reading is null)
        {
            Array.Fill(_netRates, NetRates.Both(net.Missing), 0, count);
            return;
        }

        _netKeys.Clear();
        foreach (var p in _raw)
        {
            _netKeys.Add(p.Key);
        }

        _netCalc.Compute(net.Reading, net.Generation, _netKeys, _netRates);
    }

    /// <summary>The meter's part of a tick: its counters, or (when <see cref="Reading"/> is null) the value every process gets instead.</summary>
    private sealed record NetStageResult(NetMeterReading? Reading, long Generation, Value<double> Missing)
    {
        public static NetStageResult Without(Value<double> missing) => new(null, 0, missing);
    }

    private PdhSystemReading JoinPdh(TimeSpan timeout)
    {
        if (_pdhStage.TryJoin(timeout, out var reading, out var failure, out var failureStatus) && reading is not null)
        {
            return reading;
        }

        return new PdhSystemReading(
            [],
            Value.Missing<double>(failureStatus, "PDH Processor Information", failure),
            Value.Missing<int>(failureStatus, "PDH PhysicalDisk", failure));
    }

    private NicReading JoinNics(TimeSpan timeout) =>
        _nicStage.TryJoin(timeout, out var nics, out var failure, out var failureStatus) && nics is not null
            ? nics
            : new NicReading([], Value.Missing<int>(failureStatus, "GetIfTable2", failure));

    private GpuTick JoinGpu(TimeSpan timeout, bool active)
    {
        if (!_gpuStage.TryJoin(timeout, out var reading, out var failure, out var failureStatus) || reading is null)
        {
            var detail = "GPU counters: " + failure;
            var failed = GpuSampler.Failed(_gpuKnown, failureStatus, detail);
            return new GpuTick(failed, null, failureStatus, detail, GpuSampler.ListState(failed, failureStatus, detail));
        }

        _gpuKnown = reading.Gpus;
        if (!active)
        {
            _gpuSensors.Clear();
            return new GpuTick(reading.Gpus, reading, ValueStatus.NotCollected, null, reading.State);
        }

        var merged = GpuSensorMerge.Apply(reading.Gpus, _gpuSensors.Current());
        return new GpuTick(merged, reading, ValueStatus.NotCollected, null, reading.State);
    }

    private WindowsTick JoinWindows(TimeSpan timeout)
    {
        if (_windowStage.TryJoin(timeout, out var result, out var failure, out var failureStatus) && result is not null)
        {
            return new WindowsTick(result, ValueStatus.Ok, null);
        }

        return new WindowsTick(null, failureStatus, "window enumeration: " + failure);
    }

    /// <summary>The window part of a tick; <see cref="Result"/> is null when the enumeration failed, with the reason.</summary>
    private sealed record WindowsTick(WindowProbeResult? Result, ValueStatus FailureStatus, string? FailureDetail);

    /// <summary>The GPU part of a tick: the adapters for SystemInfo and what the per-process column needs.</summary>
    private sealed record GpuTick(IReadOnlyList<GpuInfo> Gpus, GpuReading? Reading, ValueStatus FailureStatus, string? FailureDetail, Value<int> State);

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

    private ProcessInfo[] BuildProcesses(GpuTick gpu, WindowsTick windows)
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
                ProcessStateRule.Decide(p.AllThreadsSuspended, p.Pid, windows.Result, windows.FailureStatus, windows.FailureDetail),
                _netRates[i].Send,
                _netRates[i].Receive);
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
