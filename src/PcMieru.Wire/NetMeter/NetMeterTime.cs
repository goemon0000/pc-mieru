namespace PcMieru.Wire;

/// <summary>The one conversion from QueryPerformanceCounter counts to the meter's 100 ns clock, shared by the clock and by the
/// timestamps of the events, so an event's time and "now" are on the same base.</summary>
public static class NetMeterTime
{
    public const long TicksPerSecond = 10_000_000;

    /// <summary>
    /// Counts at <paramref name="frequency"/> per second to 100 ns units. Split into whole seconds and a remainder so that a
    /// long uptime at a 10 MHz counter cannot overflow the multiplication. The origin is whatever the counter's is (boot).
    /// </summary>
    public static long FromQpc(long count, long frequency)
    {
        var seconds = count / frequency;
        var rest = count % frequency;
        return (seconds * TicksPerSecond) + (rest * TicksPerSecond / frequency);
    }
}
