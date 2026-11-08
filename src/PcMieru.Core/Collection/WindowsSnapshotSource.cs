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

    private byte[] _buffer = GC.AllocateUninitializedArray<byte>(InitialBufferBytes, pinned: true);
    private ProcessRates[] _rates = new ProcessRates[512];
    private long _lastStamp;
    private long _lastSleepDebt;
    private DateTime _lastTakenAt;
    private bool _hasPrevious;
    private volatile bool _resetRequested;
    private bool _disposed;

    public WindowsSnapshotSource(Func<DateTime> clock, ISamplerControl? control = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _control = control ?? new SamplerSettings();
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
    }

    private Snapshot SampleCore(CancellationToken ct)
    {
        var length = ReadProcessBuffer();
        var stamp = Stopwatch.GetTimestamp();
        var takenAt = DateTime.SpecifyKind(_clock(), DateTimeKind.Utc);

        var sleepDebt = SleepDebtTicks();
        var slept = _hasPrevious && sleepDebt - _lastSleepDebt > SleepThresholdTicks;
        if (_resetRequested || slept)
        {
            _resetRequested = false;
            _deltas.Reset();
            _cpu.Reset();
            _hasPrevious = false;
        }

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

        var processes = BuildProcesses();

        var nominal = (int)_control.EffectiveInterval.TotalMilliseconds;
        var intervalMs = _hasPrevious ? (int)Math.Max(1, Math.Round(elapsedSeconds * 1000)) : nominal;
        var system = new SystemInfo(
            new CpuInfo(logical, cpu.Total, cpu.PerLogical, Value.NotCollected<double>("processor frequency", "not collected yet")),
            memory,
            [],
            [],
            [],
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

    private ProcessInfo[] BuildProcesses()
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
                Value.NotCollected<double>("GPU Engine", "not collected yet"),
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
