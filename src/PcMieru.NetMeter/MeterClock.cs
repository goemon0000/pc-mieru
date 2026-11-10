namespace PcMieru.NetMeter;

/// <summary>
/// A monotonic clock in 100 ns units, from QueryPerformanceCounter. The reading's timestamps come from it, so the front end
/// divides by elapsed meter time, not by its own (jittery) clock. The origin is arbitrary (boot); only differences matter.
/// </summary>
internal static unsafe class MeterClock
{
    private const long TicksPerSecond = 10_000_000;

    private static readonly long Frequency = ReadFrequency();

    public static long Now()
    {
        long count;
        _ = Native.QueryPerformanceCounter(&count);

        // Split into seconds and a remainder so a long uptime at a 10 MHz counter cannot overflow the multiplication.
        var seconds = count / Frequency;
        var rest = count % Frequency;
        return (seconds * TicksPerSecond) + (rest * TicksPerSecond / Frequency);
    }

    private static long ReadFrequency()
    {
        long frequency;
        return Native.QueryPerformanceFrequency(&frequency) && frequency > 0 ? frequency : 10_000_000;
    }
}
