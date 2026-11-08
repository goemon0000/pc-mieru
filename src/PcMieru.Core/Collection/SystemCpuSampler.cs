using System.Runtime.InteropServices;

namespace PcMieru.Core.Collection;

/// <summary>SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION: 48 bytes per logical processor. KernelTime includes IdleTime.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessorTimes
{
    public long IdleTime;
    public long KernelTime;
    public long UserTime;
    public long DpcTime;
    public long InterruptTime;
    public uint InterruptCount;
    public uint Padding;
}

internal sealed record CpuReading(int LogicalProcessors, Value<double> Total, IReadOnlyList<Value<double>> PerLogical);

/// <summary>
/// Whole-machine and per-logical-processor CPU % from NtQuerySystemInformation(SystemProcessorPerformanceInformation).
/// busy = (user + kernel) - idle (kernel already contains idle). Per CPU = 100 x delta(busy) / delta(user + kernel);
/// total = sum(busy) / sum(user + kernel), so an uneven machine is weighted by real elapsed CPU time.
/// </summary>
internal sealed class SystemCpuSampler
{
    private const string Source = "NtQuerySystemInformation(SystemProcessorPerformanceInformation)";

    private ProcessorTimes[] _previous = [];
    private ProcessorTimes[] _current = [];
    private int _previousCount;
    private bool _hasBaseline;

    public SystemCpuSampler()
    {
        LogicalProcessors = Math.Max(1, (int)Win32.GetActiveProcessorCount(Win32.AllProcessorGroups));
    }

    public int LogicalProcessors { get; }

    public void Reset() => _hasBaseline = false;

    public unsafe CpuReading Sample()
    {
        // Ask for one more entry than expected: if the machine reports more than we counted, we notice rather than truncate silently.
        var capacity = LogicalProcessors + 1;
        if (_current.Length < capacity)
        {
            _current = new ProcessorTimes[capacity];
        }

        uint returned;
        int status;
        fixed (ProcessorTimes* buffer = _current)
        {
            status = Win32.NtQuerySystemInformation(
                Win32.SystemProcessorPerformanceInformation,
                buffer,
                (uint)(capacity * sizeof(ProcessorTimes)),
                out returned);
        }

        if (status < 0)
        {
            _hasBaseline = false;
            var detail = $"NTSTATUS 0x{status:X8}";
            return Unavailable(Value.Failed<double>(Source, detail));
        }

        var count = Math.Min((int)(returned / (uint)sizeof(ProcessorTimes)), capacity);
        var reading = Compute(_hasBaseline ? _previous.AsSpan(0, _previousCount) : default, _current.AsSpan(0, count), LogicalProcessors, _hasBaseline);

        // This reading is the next baseline.
        (_previous, _current) = (_current, _previous);
        _previousCount = count;
        _hasBaseline = true;
        return reading;
    }

    /// <summary>Pure math, separated for tests. <paramref name="expected"/> is the logical processor count the machine should report.</summary>
    internal static CpuReading Compute(ReadOnlySpan<ProcessorTimes> before, ReadOnlySpan<ProcessorTimes> now, int expected, bool hasBaseline)
    {
        var perLogical = new Value<double>[expected];
        if (!hasBaseline)
        {
            Array.Fill(perLogical, Value.NotCollected<double>(Source, ProcessDeltaEngine.DetailNoBaseline));
            return new CpuReading(expected, Value.NotCollected<double>(Source, ProcessDeltaEngine.DetailNoBaseline), perLogical);
        }

        double busyAll = 0;
        double totalAll = 0;
        var covered = 0;
        for (var i = 0; i < expected; i++)
        {
            if (i >= now.Length || i >= before.Length)
            {
                // Seen on machines with several processor groups: the call may describe only the caller's group.
                perLogical[i] = Value.NotCollected<double>(Source, "the system did not report this logical processor");
                continue;
            }

            var dTotal = (now[i].KernelTime + now[i].UserTime) - (before[i].KernelTime + before[i].UserTime);
            var dIdle = now[i].IdleTime - before[i].IdleTime;
            if (dTotal <= 0 || dIdle < 0 || dIdle > dTotal)
            {
                perLogical[i] = Value.NotCollected<double>(Source, dTotal == 0 ? ProcessDeltaEngine.DetailNoElapsed : ProcessDeltaEngine.DetailBackwards);
                continue;
            }

            var busy = dTotal - dIdle;
            busyAll += busy;
            totalAll += dTotal;
            covered++;
            perLogical[i] = Value.Ok(Math.Clamp(100.0 * busy / dTotal, 0.0, 100.0), Source);
        }

        // SPEC 4: with missing processors (e.g. another processor group) the total covers what could be measured, and says so.
        var total = covered == 0 || totalAll <= 0
            ? Value.NotCollected<double>(Source, "no logical processor could be measured")
            : new Value<double>(
                Math.Clamp(100.0 * busyAll / totalAll, 0.0, 100.0),
                ValueStatus.Ok,
                Source,
                covered == expected ? null : $"computed from {covered} of {expected} logical processors");
        return new CpuReading(expected, total, perLogical);
    }

    private CpuReading Unavailable(Value<double> reason)
    {
        var perLogical = new Value<double>[LogicalProcessors];
        Array.Fill(perLogical, reason);
        return new CpuReading(LogicalProcessors, reason, perLogical);
    }
}
