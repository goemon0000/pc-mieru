using PcMieru.Core.Collection;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.NetMeter;

/// <summary>
/// The rate rules of SPEC 4.1, with readings written by hand. The calculator only ever sees the meter's own clock, so every
/// window here is exact and nothing sleeps.
/// </summary>
public sealed class NetRateCalculatorTests
{
    private const long Second = 10_000_000;
    private const long CreatedA = 133_000_000_000_000_000;
    private const long CreatedB = 133_000_000_100_000_000;

    private static readonly ProcessKey A = new(100, CreatedA);
    private static readonly ProcessKey B = new(200, CreatedB);

    private readonly NetRateCalculator _calc = new();

    private static NetMeterEntry Entry(ProcessKey key, ulong send, ulong receive) => new(key.Pid, key.CreateTime, send, receive);

    private static NetMeterReading Reading(long seconds, params NetMeterEntry[] entries) =>
        new(seconds * Second, 0, 0, 0, true, 0, entries);

    private static NetMeterReading Reading(long ticks, ulong eventsLost, ulong buffersLost, ulong undecodable, params NetMeterEntry[] entries) =>
        new(ticks, eventsLost, buffersLost, undecodable, true, 0, entries);

    private NetRates[] Run(NetMeterReading reading, long generation, params ProcessKey[] alive)
    {
        var output = new NetRates[alive.Length];
        _calc.Compute(reading, generation, alive, output);
        return output;
    }

    private static void AssertMissing(Value<double> value, ValueStatus status, string detailPart)
    {
        Assert.Equal(status, value.Status);
        Assert.Equal(NetMeterValues.Source, value.Source);
        Assert.Contains(detailPart, value.Detail);
        Assert.False(value.TryGet(out _));
    }

    private static void AssertRate(double expected, Value<double> value)
    {
        Assert.True(value.IsOk, value.Detail);
        Assert.Equal(expected, value.Data, 6);
        Assert.Equal(NetMeterValues.Source, value.Source);
    }

    [Fact]
    public void The_first_reading_is_only_a_baseline_and_says_so_instead_of_reporting_zero()
    {
        var rates = Run(Reading(10, Entry(A, 5_000, 9_000)), 1, A);

        AssertMissing(rates[0].Send, ValueStatus.NotCollected, NetRateCalculator.DetailNoBaseline);
        AssertMissing(rates[0].Receive, ValueStatus.NotCollected, NetRateCalculator.DetailNoBaseline);
        Assert.True(_calc.HasBaseline);
    }

    [Fact]
    public void The_second_reading_gives_bytes_per_second_over_the_meters_own_window()
    {
        _ = Run(Reading(10, Entry(A, 1_000, 2_000)), 1, A);

        // 2.5 s of the meter's clock, whatever the wall clock said.
        var rates = Run(new NetMeterReading((10 * Second) + (25 * Second / 10), 0, 0, 0, true, 0, [Entry(A, 6_000, 12_000)]), 1, A);

        AssertRate(2_000, rates[0].Send);
        AssertRate(4_000, rates[0].Receive);
    }

    [Fact]
    public void A_process_alive_at_both_readings_with_no_entry_in_either_had_no_traffic_and_that_is_zero_not_a_gap()
    {
        _ = Run(Reading(10, Entry(B, 1, 1)), 1, A, B);

        var rates = Run(Reading(11, Entry(B, 1, 1)), 1, A, B);

        AssertRate(0, rates[0].Send);
        AssertRate(0, rates[0].Receive);
        AssertRate(0, rates[1].Send);
        AssertRate(0, rates[1].Receive);
    }

    [Fact]
    public void A_process_that_first_sent_in_this_window_counts_from_zero()
    {
        _ = Run(Reading(10, Entry(B, 1, 1)), 1, A, B);

        var rates = Run(Reading(11, Entry(A, 700, 300), Entry(B, 1, 1)), 1, A, B);

        AssertRate(700, rates[0].Send);
        AssertRate(300, rates[0].Receive);
    }

    [Fact]
    public void A_process_that_was_not_alive_at_the_previous_reading_is_new_not_zero_and_not_a_rate_over_a_partial_window()
    {
        _ = Run(Reading(10, Entry(A, 100, 100)), 1, A);

        var rates = Run(Reading(11, Entry(A, 200, 200), Entry(B, 9_999, 9_999)), 1, A, B);

        AssertRate(100, rates[0].Send);
        AssertMissing(rates[1].Send, ValueStatus.NotCollected, NetRateCalculator.DetailNewProcess);
        AssertMissing(rates[1].Receive, ValueStatus.NotCollected, NetRateCalculator.DetailNewProcess);

        // The window after, it has a baseline of its own.
        var next = Run(Reading(12, Entry(A, 200, 200), Entry(B, 10_999, 9_999)), 1, A, B);
        AssertRate(1_000, next[1].Send);
        AssertRate(0, next[1].Receive);
    }

    [Fact]
    public void A_reused_pid_with_another_creation_time_is_another_process_and_never_inherits_the_old_counter()
    {
        var reused = new ProcessKey(A.Pid, CreatedB);
        _ = Run(Reading(10, Entry(A, 1_000_000, 1_000_000)), 1, A);

        // The old process is gone, a new one took its pid: its counter starts at zero and is far below the old one.
        var rates = Run(Reading(11, Entry(reused, 10, 10)), 1, reused);

        AssertMissing(rates[0].Send, ValueStatus.NotCollected, NetRateCalculator.DetailNewProcess);
        AssertMissing(rates[0].Receive, ValueStatus.NotCollected, NetRateCalculator.DetailNewProcess);
    }

    [Fact]
    public void A_pid_whose_creation_time_differs_between_the_reading_and_the_process_list_is_not_matched()
    {
        // The meter knows (100, CreatedA); the process list has (100, CreatedB): the entry belongs to nobody alive.
        _ = Run(Reading(10, Entry(A, 500, 500)), 1, new ProcessKey(A.Pid, CreatedB));

        var rates = Run(Reading(11, Entry(A, 900, 900)), 1, new ProcessKey(A.Pid, CreatedB));

        AssertRate(0, rates[0].Send);
        AssertRate(0, rates[0].Receive);
    }

    [Fact]
    public void A_counter_that_went_backwards_is_a_gap_in_that_direction_only()
    {
        _ = Run(Reading(10, Entry(A, 5_000, 5_000)), 1, A);

        var rates = Run(Reading(11, Entry(A, 4_000, 6_000)), 1, A);

        AssertMissing(rates[0].Send, ValueStatus.NotCollected, NetRateCalculator.DetailBackwards);
        AssertRate(1_000, rates[0].Receive);
    }

    [Theory]
    [InlineData(3UL, 0UL, 0UL, "dropped 3 network events")]
    [InlineData(0UL, 2UL, 0UL, "dropped 2 network events")]
    [InlineData(1UL, 1UL, 0UL, "dropped 2 network events")]
    [InlineData(0UL, 0UL, 4UL, "4 network events could not be read")]
    [InlineData(5UL, 0UL, 4UL, "dropped 5 network events and 4 could not be read")]
    public void Events_lost_or_unreadable_in_a_window_make_every_rate_an_error_with_the_count(ulong lost, ulong buffers, ulong undecodable, string detail)
    {
        _ = Run(Reading(10 * Second, 0, 0, 0, Entry(A, 100, 100), Entry(B, 100, 100)), 1, A, B);

        var rates = Run(Reading(11 * Second, lost, buffers, undecodable, Entry(A, 5_000, 5_000), Entry(B, 100, 100)), 1, A, B);

        foreach (var rate in rates)
        {
            AssertMissing(rate.Send, ValueStatus.Error, detail);
            AssertMissing(rate.Receive, ValueStatus.Error, detail);
        }
    }

    [Fact]
    public void Loss_that_happened_before_the_baseline_is_not_blamed_on_a_later_window_and_loss_ends_with_its_window()
    {
        // Already 7 lost when the baseline is taken: that is history, the window is clean.
        _ = Run(Reading(10 * Second, 7, 0, 0, Entry(A, 100, 100)), 1, A);
        var clean = Run(Reading(11 * Second, 7, 0, 0, Entry(A, 200, 100)), 1, A);
        AssertRate(100, clean[0].Send);

        // 2 more lost: this window is an error ...
        var lost = Run(Reading(12 * Second, 9, 0, 0, Entry(A, 5_000, 100)), 1, A);
        AssertMissing(lost[0].Send, ValueStatus.Error, "dropped 2 network events");

        // ... and the next one is computed from the reading that ended the bad window.
        var after = Run(Reading(13 * Second, 9, 0, 0, Entry(A, 5_300, 100)), 1, A);
        AssertRate(300, after[0].Send);
    }

    [Fact]
    public void Undecodable_events_are_counted_by_their_increase_not_their_total()
    {
        _ = Run(Reading(10 * Second, 0, 0, 3, Entry(A, 100, 100)), 1, A);

        var rates = Run(Reading(11 * Second, 0, 0, 3, Entry(A, 200, 100)), 1, A);

        AssertRate(100, rates[0].Send);
    }

    [Fact]
    public void A_reading_with_the_same_timestamp_repeats_the_last_rates_and_says_so()
    {
        _ = Run(Reading(10, Entry(A, 0, 0)), 1, A);
        _ = Run(Reading(11, Entry(A, 1_000, 500)), 1, A);

        // The meter's batches come about once a second: asked again at 0.5 s it still reports the same timestamp.
        var again = Run(Reading(11, Entry(A, 1_000, 500)), 1, A);

        AssertRate(1_000, again[0].Send);
        AssertRate(500, again[0].Receive);
        Assert.Equal(NetRateCalculator.DetailRepeated, again[0].Send.Detail);
        Assert.Equal(NetRateCalculator.DetailRepeated, again[0].Receive.Detail);
    }

    [Fact]
    public void A_repeat_does_not_move_the_baseline_so_the_next_advancing_reading_covers_the_whole_window()
    {
        _ = Run(Reading(10, Entry(A, 0, 0)), 1, A);
        _ = Run(Reading(11, Entry(A, 1_000, 0)), 1, A);
        _ = Run(Reading(11, Entry(A, 1_000, 0)), 1, A);

        // 2 s since the last advancing reading (11 -> 13) and 2,000 more bytes: 1,000 per second, not 2,000.
        var rates = Run(Reading(13, Entry(A, 3_000, 0)), 1, A);

        AssertRate(1_000, rates[0].Send);
        Assert.Null(rates[0].Send.Detail);
    }

    [Fact]
    public void A_repeat_keeps_the_words_of_a_gap_or_an_error_and_a_new_process_has_no_rate_to_repeat()
    {
        _ = Run(Reading(10, Entry(A, 100, 100)), 1, A);
        _ = Run(Reading(11, Entry(A, 50, 200)), 1, A); // send went backwards

        var again = Run(Reading(11, Entry(A, 50, 200), Entry(B, 1, 1)), 1, A, B);

        AssertMissing(again[0].Send, ValueStatus.NotCollected, NetRateCalculator.DetailBackwards);
        AssertRate(100, again[0].Receive);
        AssertMissing(again[1].Send, ValueStatus.NotCollected, NetRateCalculator.DetailNewProcess);
    }

    [Fact]
    public void A_repeat_right_after_the_baseline_has_no_rates_yet_and_says_there_is_no_baseline()
    {
        _ = Run(Reading(10, Entry(A, 100, 100)), 1, A);

        var again = Run(Reading(10, Entry(A, 100, 100)), 1, A);

        AssertMissing(again[0].Send, ValueStatus.NotCollected, NetRateCalculator.DetailNoBaseline);
    }

    [Fact]
    public void A_repeated_error_window_stays_an_error()
    {
        _ = Run(Reading(10 * Second, 0, 0, 0, Entry(A, 0, 0)), 1, A);
        _ = Run(Reading(11 * Second, 4, 0, 0, Entry(A, 10, 10)), 1, A);

        var again = Run(Reading(11 * Second, 4, 0, 0, Entry(A, 10, 10)), 1, A);

        AssertMissing(again[0].Send, ValueStatus.Error, "dropped 4 network events");
    }

    [Fact]
    public void A_new_meter_run_is_a_new_baseline_because_its_counters_and_clock_start_over()
    {
        _ = Run(Reading(10, Entry(A, 9_000_000, 9_000_000)), 1, A);
        _ = Run(Reading(11, Entry(A, 9_000_100, 9_000_100)), 1, A);

        // Generation 2: counters are small again and the clock is elsewhere. Comparing would give a "backwards" or a nonsense rate.
        var rates = Run(Reading(500, Entry(A, 10, 10)), 2, A);

        AssertMissing(rates[0].Send, ValueStatus.NotCollected, NetRateCalculator.DetailNoBaseline);

        var next = Run(Reading(501, Entry(A, 60, 10)), 2, A);
        AssertRate(50, next[0].Send);
    }

    [Fact]
    public void Reset_drops_the_baseline()
    {
        _ = Run(Reading(10, Entry(A, 100, 100)), 1, A);
        _ = Run(Reading(11, Entry(A, 200, 200)), 1, A);

        _calc.Reset();
        Assert.False(_calc.HasBaseline);
        var rates = Run(Reading(12, Entry(A, 300, 300)), 1, A);

        AssertMissing(rates[0].Send, ValueStatus.NotCollected, NetRateCalculator.DetailNoBaseline);
        var next = Run(Reading(13, Entry(A, 400, 300)), 1, A);
        AssertRate(100, next[0].Send);
    }

    [Fact]
    public void A_meter_clock_that_goes_back_is_a_new_baseline_never_a_negative_window()
    {
        _ = Run(Reading(100, Entry(A, 100, 100)), 1, A);
        _ = Run(Reading(101, Entry(A, 200, 200)), 1, A);

        var rates = Run(Reading(50, Entry(A, 300, 300)), 1, A);

        AssertMissing(rates[0].Send, ValueStatus.NotCollected, NetRateCalculator.DetailNoBaseline);
    }

    [Fact]
    public void A_process_that_ended_between_readings_simply_leaves_the_result_and_its_traffic_is_ignored()
    {
        _ = Run(Reading(10, Entry(A, 100, 100), Entry(B, 100, 100)), 1, A, B);

        var rates = Run(Reading(11, Entry(A, 200, 100), Entry(B, 9_999, 9_999)), 1, A);

        Assert.Single(rates);
        AssertRate(100, rates[0].Send);
    }

    [Fact]
    public void Results_come_back_in_the_order_of_the_processes_asked_about()
    {
        _ = Run(Reading(10, Entry(A, 0, 0), Entry(B, 0, 0)), 1, A, B);

        var rates = Run(Reading(11, Entry(A, 100, 0), Entry(B, 900, 0)), 1, B, A);

        AssertRate(900, rates[0].Send);
        AssertRate(100, rates[1].Send);
    }

    [Fact]
    public void An_output_shorter_than_the_process_list_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            Span<NetRates> output = new NetRates[1];
            _calc.Compute(Reading(10), 1, [A, B], output);
        });
    }

    [Fact]
    public void A_missing_value_never_carries_a_number()
    {
        var rates = Run(Reading(10, Entry(A, 5_000, 9_000)), 1, A);

        Assert.Equal(0.0, rates[0].Send.Data);
        Assert.Equal(ValueStatus.NotCollected, rates[0].Send.Status);
    }
}
