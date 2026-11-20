using PcMieru.Checks.SystemInfo;

namespace PcMieru.Checks.Tests.SystemInfo;

public sealed class QueryDeadlineTests
{
    [Fact]
    public void Remaining_runs_down_with_the_monotonic_clock_and_expires_exactly_at_the_duration()
    {
        var clock = new ManualTimeProvider();
        var deadline = QueryTest.Deadline(clock, 5);

        Assert.Equal(TimeSpan.FromSeconds(5), deadline.Remaining);
        Assert.False(deadline.IsExpired);

        clock.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.Equal(TimeSpan.FromMilliseconds(1), deadline.Remaining);
        Assert.False(deadline.IsExpired);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
        Assert.True(deadline.IsExpired);
    }

    [Fact]
    public void Remaining_is_never_negative()
    {
        var clock = new ManualTimeProvider();
        var deadline = QueryTest.Deadline(clock, 1);

        clock.Advance(TimeSpan.FromHours(3));

        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
        Assert.True(deadline.IsExpired);
    }

    [Fact]
    public void A_change_of_the_wall_clock_does_not_move_the_deadline()
    {
        var clock = new ManualTimeProvider();
        var deadline = QueryTest.Deadline(clock, 5);

        clock.Wall = clock.Wall.AddDays(400); // a user sets the date a year ahead
        Assert.Equal(TimeSpan.FromSeconds(5), deadline.Remaining);

        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Wall = clock.Wall.AddDays(-900); // and back, further than before
        Assert.Equal(TimeSpan.FromSeconds(3), deadline.Remaining);
        Assert.False(deadline.IsExpired);
    }

    [Fact]
    public void A_zero_deadline_is_expired_at_once()
    {
        var deadline = QueryDeadline.After(new ManualTimeProvider(), TimeSpan.Zero);

        Assert.True(deadline.IsExpired);
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24 * 3600 + 1)]
    public void A_deadline_outside_zero_to_24_hours_is_refused(double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QueryDeadline.After(new ManualTimeProvider(), TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Two_deadlines_on_one_clock_are_independent_and_a_shared_one_is_shared()
    {
        var clock = new ManualTimeProvider();
        var shared = QueryTest.Deadline(clock, 5);

        clock.Advance(TimeSpan.FromSeconds(3));
        var later = QueryTest.Deadline(clock, 5);

        Assert.Equal(TimeSpan.FromSeconds(2), shared.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(5), later.Remaining);
    }

    [Theory]
    [InlineData(5_000_000, 5000)]
    [InlineData(1_200, 2)] // 1.2 ms rounds up: a native timeout of 1 ms would come back too early, 2 ms never too late
    [InlineData(400, 1)] // under a millisecond is still 1, because 0 means "do not wait" to the Windows calls
    [InlineData(0, 1)]
    public void Remaining_milliseconds_round_up_and_are_at_least_one(long remainingMicroseconds, int expectedMs)
    {
        var clock = new ManualTimeProvider(frequency: 1_000_000); // microsecond ticks
        var deadline = QueryDeadline.After(clock, TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(remainingMicroseconds * 10));

        Assert.Equal(expectedMs, deadline.RemainingMilliseconds());
    }

    [Fact]
    public void A_timeout_for_a_TimeSpan_api_is_at_least_one_millisecond()
    {
        var clock = new ManualTimeProvider();
        var deadline = QueryTest.Deadline(clock, 1);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromMilliseconds(1), deadline.RemainingAtLeastOneMillisecond());
    }
}

public sealed class CheckReadingTests
{
    [Fact]
    public void A_value_has_no_reason()
    {
        var reading = CheckReading<long>.Ok(3);

        Assert.True(reading.HasValue);
        Assert.Null(reading.Reason);
        Assert.Equal(3, reading.Value);
    }

    [Theory]
    [InlineData(CheckReason.Timeout)]
    [InlineData(CheckReason.Error)]
    [InlineData(CheckReason.Denied)]
    [InlineData(CheckReason.Unsupported)]
    [InlineData(CheckReason.Partial)]
    public void A_missing_reading_has_a_reason_and_no_value(CheckReason reason)
    {
        var reading = CheckReading<long>.Missing(reason);

        Assert.False(reading.HasValue);
        Assert.Equal(reason, reading.Reason);
        Assert.Throws<InvalidOperationException>(() => reading.Value);
    }

    [Fact]
    public void The_named_factories_map_to_the_reasons_of_the_spec()
    {
        Assert.Equal(CheckReason.Timeout, CheckReading<long>.TimedOut().Reason);
        Assert.Equal(CheckReason.Error, CheckReading<long>.Failed().Reason);
        Assert.Equal(CheckReason.Partial, CheckReading<long>.Stopped().Reason);
    }

    [Fact]
    public void A_null_value_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => CheckReading<string>.Ok(null!));
    }
}
