namespace PcMieru.Core.Collection;

/// <summary>CPU and I/O rates of one process over the last window.</summary>
internal readonly record struct ProcessRates(Value<double> Cpu, Value<double> ReadPerSec, Value<double> WritePerSec);

/// <summary>
/// Per-process deltas, keyed by <see cref="ProcessKey"/> (PID plus creation time), so a reused PID is a new process.
/// Contract (SPEC 4): the first sample, the first one after <see cref="Reset"/>, a process new since the last tick
/// and a counter that went backwards are all NotCollected; they are never 0.
/// CPU % is 100 x delta(user+kernel) / elapsed / logical processors, the Task Manager definition (100 = every CPU busy).
/// </summary>
internal sealed class ProcessDeltaEngine
{
    private const string CpuSource = "NtQuerySystemInformation(SystemProcessInformation) user+kernel time delta";
    private const string IoSource = "NtQuerySystemInformation(SystemProcessInformation) transfer count delta";

    internal const string DetailNoBaseline = "no baseline yet (first sample)";
    internal const string DetailNewProcess = "new since the previous sample";
    internal const string DetailBackwards = "counter went backwards";
    internal const string DetailNoElapsed = "no time elapsed between samples";

    private readonly record struct Previous(long CpuTime, ulong Read, ulong Write);

    private Dictionary<ProcessKey, Previous> _previous = new(512);
    private Dictionary<ProcessKey, Previous> _next = new(512);
    private bool _hasBaseline;

    public bool HasBaseline => _hasBaseline;

    /// <summary>Drops the baseline: resume from sleep, un-pause, explicit request.</summary>
    public void Reset()
    {
        _previous.Clear();
        _next.Clear();
        _hasBaseline = false;
    }

    /// <summary>
    /// Fills <paramref name="output"/> (same order and length as <paramref name="current"/>) and makes this sample the next baseline.
    /// </summary>
    /// <param name="elapsedSeconds">Time between the previous and this reading of the kernel, from a monotonic clock.</param>
    public void Compute(IReadOnlyList<RawProcess> current, double elapsedSeconds, int logicalProcessors, Span<ProcessRates> output)
    {
        if (output.Length < current.Count)
        {
            throw new ArgumentException("output is shorter than current", nameof(output));
        }

        var usable = _hasBaseline && elapsedSeconds > 0 && logicalProcessors > 0;
        for (var i = 0; i < current.Count; i++)
        {
            var p = current[i];
            var key = p.Key;
            _next[key] = new Previous(p.CpuTime, p.ReadTransferCount, p.WriteTransferCount);

            if (!_hasBaseline)
            {
                output[i] = All(DetailNoBaseline);
            }
            else if (!usable)
            {
                output[i] = All(DetailNoElapsed);
            }
            else if (!_previous.TryGetValue(key, out var before))
            {
                output[i] = All(DetailNewProcess);
            }
            else
            {
                output[i] = new ProcessRates(
                    Cpu(p.CpuTime - before.CpuTime, elapsedSeconds, logicalProcessors),
                    Rate(p.ReadTransferCount, before.Read, elapsedSeconds),
                    Rate(p.WriteTransferCount, before.Write, elapsedSeconds));
            }
        }

        // Only processes seen in this sample survive, so the baseline cannot grow with PIDs that came and went.
        (_previous, _next) = (_next, _previous);
        _next.Clear();
        _hasBaseline = true;
    }

    /// <summary>Delta of 100 ns units to percent of the whole machine, clamped to 0..100 (timer jitter can overshoot slightly).</summary>
    internal static Value<double> Cpu(long deltaTicks, double elapsedSeconds, int logicalProcessors)
    {
        if (deltaTicks < 0)
        {
            return Value.NotCollected<double>(CpuSource, DetailBackwards);
        }

        var percent = 100.0 * deltaTicks / 1e7 / elapsedSeconds / logicalProcessors;
        return Value.Ok(Math.Clamp(percent, 0.0, 100.0), CpuSource);
    }

    internal static Value<double> Rate(ulong now, ulong before, double elapsedSeconds)
    {
        if (now < before)
        {
            return Value.NotCollected<double>(IoSource, DetailBackwards);
        }

        return Value.Ok((now - before) / elapsedSeconds, IoSource);
    }

    private static ProcessRates All(string detail) => new(
        Value.NotCollected<double>(CpuSource, detail),
        Value.NotCollected<double>(IoSource, detail),
        Value.NotCollected<double>(IoSource, detail));
}
