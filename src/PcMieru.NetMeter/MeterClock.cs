using PcMieru.Wire;

namespace PcMieru.NetMeter;

/// <summary>
/// A monotonic clock in 100 ns units, from QueryPerformanceCounter. The reading's timestamps come from it, so the front end
/// divides by elapsed meter time, not by its own (jittery) clock. The origin is arbitrary (boot); only differences matter.
/// The ETW session stamps its events with the same counter (ClientContext 1 plus PROCESS_TRACE_MODE_RAW_TIMESTAMP), and
/// <see cref="FromQpc"/> is the same conversion, so an event's time and <see cref="Now"/> can be compared directly.
/// </summary>
internal static unsafe class MeterClock
{
    private static readonly long Frequency = ReadFrequency();

    public static long Now()
    {
        long count;
        _ = Native.QueryPerformanceCounter(&count);
        return FromQpc(count);
    }

    /// <summary>A raw QueryPerformanceCounter count (such as an event's timestamp) on the meter clock's base.</summary>
    public static long FromQpc(long count) => NetMeterTime.FromQpc(count, Frequency);

    private static long ReadFrequency()
    {
        long frequency;
        return Native.QueryPerformanceFrequency(&frequency) && frequency > 0 ? frequency : 10_000_000;
    }
}
