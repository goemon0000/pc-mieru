using System.Diagnostics;
using System.Runtime.InteropServices;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Collectors;

namespace PcMieru.Checks.Load;

/// <summary>
/// The CPU test session of the real machine (DESIGN-A §2, §6.2): the logical processors Core lists, the real chunk, the thread pin of
/// Windows, real threads, the monotonic clock, and the check's own PDH query (<see cref="CpuCheckPdhSession"/>, never the live view's).
/// Made on a worker when a CPU check starts; opening PDH is the only thing in it that can be slow, and it is not on the caller's thread.
/// </summary>
internal sealed class RealCpuTestSession : ICpuTestSession
{
    private readonly PdhSampleGuard? _pdh;

    public RealCpuTestSession()
    {
        Cpus = LogicalCpuMap.Read();
        var pdh = OpenPdh();
        _pdh = pdh is null ? null : new PdhSampleGuard(pdh.Sample, pdh.Dispose);
    }

    public IReadOnlyList<LogicalCpu> Cpus { get; }

    public IChunkWork Work { get; } = new ComputeChunkWork();

    public ICpuPin Pin { get; } = new Win32CpuPin();

    public ILoadThreadStarter Threads { get; } = new RealLoadThreadStarter();

    public ILoadClock Clock { get; } = new RealLoadClock();

    public CpuPdhSample SamplePdh() => _pdh?.Sample() ?? CpuPdhSample.Missing(PackagePowerState.NoCounter);

    // Never waits for a sample that is still in flight (see PdhSampleGuard): a hung counter call cannot hold the end of the check.
    public void Dispose() => _pdh?.Dispose();

    // A PDH that cannot be opened is not a failed check: the clock, the utilisation and the power are then "not supported", the load still runs.
    private static CpuCheckPdhSession? OpenPdh()
    {
        try
        {
            var pdh = new CpuCheckPdhSession();
            if (pdh.IsOpen)
            {
                return pdh;
            }

            pdh.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Makes the end of a PDH session safe against a sample that hangs. A PDH call cannot be interrupted, and closing the query under a call that
/// is still inside it is not safe either, so <see cref="Dispose"/> does not wait for the call and does not close under it: it marks the guard
/// closed and returns at once, and the query is closed by the call itself when (if) it comes back. With no call in flight it closes at once.
/// A sample asked for after the dispose is a missing sample.
/// </summary>
internal sealed class PdhSampleGuard(Func<CpuPdhSample> sample, Action close) : IDisposable
{
    private readonly object _gate = new();
    private int _inFlight;
    private bool _disposed;
    private bool _closed;

    public CpuPdhSample Sample()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return CpuPdhSample.Missing();
            }

            _inFlight++;
        }

        try
        {
            return sample();
        }
        finally
        {
            bool closeNow;
            lock (_gate)
            {
                _inFlight--;
                closeNow = _disposed && _inFlight == 0 && !_closed;
                _closed |= closeNow;
            }

            if (closeNow)
            {
                close();
            }
        }
    }

    public void Dispose()
    {
        bool closeNow;
        lock (_gate)
        {
            _disposed = true;
            closeNow = _inFlight == 0 && !_closed;
            _closed |= closeNow;
        }

        if (closeNow)
        {
            close();
        }
    }
}

/// <summary>The real chunk: <see cref="CpuLoadTest.Chunk"/>.</summary>
internal sealed class ComputeChunkWork : IChunkWork
{
    public ulong Chunk(ulong seed) => CpuLoadTest.Chunk(seed);
}

/// <summary>
/// Pins the calling thread to one logical processor by its processor group and the bit in the group (SetThreadGroupAffinity): the group
/// matters on a machine with more than 64 logical processors. A refusal is false; the load goes on unpinned and the check then does not
/// compare that processor (DESIGN-A 14.2: a pin that cannot be confirmed is "pin_unknown").
/// </summary>
internal sealed partial class Win32CpuPin : ICpuPin
{
    public bool PinCurrentThread(LogicalCpu cpu)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        var affinity = new GroupAffinity { Mask = (nuint)(1UL << cpu.Number), Group = cpu.Group };
        return SetThreadGroupAffinity(GetCurrentThread(), in affinity, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public nuint Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadGroupAffinity(nint thread, in GroupAffinity affinity, nint previousAffinity);
}

/// <summary>A dedicated thread of below-normal priority, a background one (it cannot keep the process alive), named for a debugger.</summary>
internal sealed class RealLoadThreadStarter : ILoadThreadStarter
{
    public ILoadThread Start(string name, Action onStart, Func<bool> step, Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(onStart);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(onFault);
        var thread = new Thread(() =>
        {
            try
            {
                onStart();
                while (step())
                {
                }
            }
            catch (Exception ex)
            {
                // Nothing is swallowed: an exception that escapes the step (out of memory too) ends this thread and is reported as a fault, so
                // the run fails. A thread that died quietly would leave its CPU unloaded and the result would read as a finding about it.
                try
                {
                    onFault(ex);
                }
                catch (Exception)
                {
                    // Only the handler is guarded, so that a handler that throws cannot end the process from a load thread.
                }
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = name,
        };
        thread.Start();
        return new RealLoadThread(thread);
    }

    private sealed class RealLoadThread(Thread thread) : ILoadThread
    {
        public bool IsAlive => thread.IsAlive;

        public void Join() => thread.Join();
    }
}

/// <summary>The monotonic clock and a wait that a cancellation cuts short.</summary>
internal sealed class RealLoadClock : ILoadClock
{
    public long Timestamp => Stopwatch.GetTimestamp();

    public TimeSpan Elapsed(long since) => Stopwatch.GetElapsedTime(since);

    public bool WaitCancelled(TimeSpan wait, CancellationToken ct) => ct.WaitHandle.WaitOne(wait);
}
