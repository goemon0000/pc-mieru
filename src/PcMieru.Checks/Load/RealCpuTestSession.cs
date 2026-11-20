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
    private readonly CpuCheckPdhSession? _pdh;

    public RealCpuTestSession()
    {
        Cpus = LogicalCpuMap.Read();
        _pdh = OpenPdh();
    }

    public IReadOnlyList<LogicalCpu> Cpus { get; }

    public IChunkWork Work { get; } = new ComputeChunkWork();

    public ICpuPin Pin { get; } = new Win32CpuPin();

    public ILoadThreadStarter Threads { get; } = new RealLoadThreadStarter();

    public ILoadClock Clock { get; } = new RealLoadClock();

    public CpuPdhSample SamplePdh() => _pdh?.Sample() ?? CpuPdhSample.Missing(PackagePowerState.NoCounter);

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
    public ILoadThread Start(string name, Action onStart, Func<bool> step)
    {
        ArgumentNullException.ThrowIfNull(onStart);
        ArgumentNullException.ThrowIfNull(step);
        var thread = new Thread(() =>
        {
            try
            {
                onStart();
                while (step())
                {
                }
            }
            catch (Exception)
            {
                // The load's own steps catch what a chunk throws and count it; this is only so that nothing can ever end the process from here.
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
