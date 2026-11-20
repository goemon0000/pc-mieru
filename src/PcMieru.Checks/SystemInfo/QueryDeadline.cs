namespace PcMieru.Checks.SystemInfo;

/// <summary>
/// An absolute deadline on a monotonic clock (DESIGN-A §6.1): the moment is a <see cref="TimeProvider.GetTimestamp"/> value, so a change of
/// the wall clock (a time sync, a user setting the date) never lengthens or shortens a query. The system check makes one and every
/// event query of the run shares it; a WMI question makes its own. It is immutable and can be read from any thread.
/// </summary>
internal sealed class QueryDeadline
{
    /// <summary>The longest deadline that can be made. Far above what a check asks for (5 s); it only keeps the arithmetic far from overflow.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    private readonly long _expiresAt;

    private QueryDeadline(TimeProvider time, long expiresAt, TimeSpan duration)
    {
        Time = time;
        _expiresAt = expiresAt;
        Duration = duration;
    }

    /// <summary>The clock the deadline is measured on. The wait that cuts a stuck call short uses the same one.</summary>
    public TimeProvider Time { get; }

    /// <summary>How long the deadline was when it was made.</summary>
    public TimeSpan Duration { get; }

    /// <summary>A deadline <paramref name="duration"/> from now, on <paramref name="time"/>'s monotonic clock.</summary>
    public static QueryDeadline After(TimeProvider time, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (duration < TimeSpan.Zero || duration > MaxDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "A deadline is between zero and 24 hours.");
        }

        var start = time.GetTimestamp();

        // Whole clock ticks, rounded up, in decimal: a double turns 4.999 s * 1000 into 4999.000000000001 and the deadline one tick late.
        var ticks = decimal.Ceiling((decimal)duration.Ticks * time.TimestampFrequency / TimeSpan.TicksPerSecond);
        return new QueryDeadline(time, start + (long)ticks, duration);
    }

    /// <summary>What is left, never negative.</summary>
    public TimeSpan Remaining
    {
        get
        {
            var left = Time.GetElapsedTime(Time.GetTimestamp(), _expiresAt);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    public bool IsExpired => Remaining <= TimeSpan.Zero;

    /// <summary>
    /// What is left, in whole milliseconds rounded up and at least 1, for a Windows call that takes a timeout in milliseconds
    /// (0 would mean "do not wait" and -1 "wait for ever"). Call it only after <see cref="IsExpired"/> was false.
    /// </summary>
    public int RemainingMilliseconds()
    {
        var ms = Math.Ceiling(Remaining.TotalMilliseconds);
        return (int)Math.Clamp(ms, 1, int.MaxValue);
    }

    /// <summary>What is left as a timeout for an API that takes a <see cref="TimeSpan"/>: at least 1 ms.</summary>
    public TimeSpan RemainingAtLeastOneMillisecond()
    {
        var left = Remaining;
        return left < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : left;
    }
}
