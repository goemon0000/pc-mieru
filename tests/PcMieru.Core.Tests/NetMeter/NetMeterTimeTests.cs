using PcMieru.Wire;

namespace PcMieru.Core.Tests.NetMeter;

public sealed class NetMeterTimeTests
{
    private const long Second = NetMeterTime.TicksPerSecond;

    [Theory]
    [InlineData(10_000_000L)] // the usual QueryPerformanceFrequency on Windows 10 and 11
    [InlineData(24_000_000L)] // some Arm and virtual machines
    [InlineData(3_579_545L)] // the ACPI PM timer
    [InlineData(1_000_000_000L)] // a nanosecond counter
    public void A_whole_number_of_seconds_converts_exactly(long frequency)
    {
        Assert.Equal(0, NetMeterTime.FromQpc(0, frequency));
        Assert.Equal(Second, NetMeterTime.FromQpc(frequency, frequency));
        Assert.Equal(90 * Second, NetMeterTime.FromQpc(90 * frequency, frequency));
    }

    [Theory]
    [InlineData(10_000_000L)]
    [InlineData(24_000_000L)]
    public void A_long_uptime_does_not_overflow_the_multiplication(long frequency)
    {
        // 300 days: the naive count * 10^7 / frequency overflows a long long before that.
        var seconds = 300L * 86_400;
        var count = seconds * frequency;
        Assert.True(count > long.MaxValue / Second, "the case must be one where the naive product overflows");

        Assert.Equal(seconds * Second, NetMeterTime.FromQpc(count, frequency));
        Assert.Equal((seconds * Second) + (Second / 10), NetMeterTime.FromQpc(count + (frequency / 10), frequency));
    }

    [Fact]
    public void The_conversion_never_goes_backwards_as_the_count_grows()
    {
        const long frequency = 3_579_545;
        var last = long.MinValue;
        for (long count = 12_345_678_900; count < 12_345_678_900 + 20_000; count += 7)
        {
            var ticks = NetMeterTime.FromQpc(count, frequency);
            Assert.True(ticks >= last);
            last = ticks;
        }
    }
}
