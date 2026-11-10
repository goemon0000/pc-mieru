using PcMieru.Core;
using PcMieru.Core.Collection;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.NetMeter;

/// <summary>A fake process table: pids that can be opened (with a creation time), exit and be reused.</summary>
internal sealed class FakeProcessTable : INetMeterProcessResolver
{
    private readonly Dictionary<uint, Proc> _alive = [];

    public List<FakePin> Pins { get; } = [];

    public void Start(uint pid, long created) => _alive[pid] = new Proc(created);

    public void Exit(uint pid) => _alive[pid].Exited = true;

    /// <summary>The process is gone for good: nothing can open that pid any more.</summary>
    public void Forget(uint pid) => _alive.Remove(pid);

    public INetMeterProcessPin? Pin(uint pid)
    {
        if (!_alive.TryGetValue(pid, out var process))
        {
            return null;
        }

        var pin = new FakePin(process);
        Pins.Add(pin);
        return pin;
    }

    internal sealed class Proc(long created)
    {
        public long Created { get; } = created;

        public bool Exited { get; set; }
    }

    internal sealed class FakePin(Proc process) : INetMeterProcessPin
    {
        public long CreationFileTime => process.Created;

        public bool HasExited => process.Exited;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}

public sealed class NetMeterCountersTests
{
    private const long Second = 10_000_000;
    private long _now = 100 * Second;
    private readonly FakeProcessTable _table = new();

    private NetMeterCounters Create() => new(_table, () => _now);

    private static NetMeterEntry? Find(NetMeterReading reading, uint pid) =>
        reading.Entries.Where(e => e.Pid == pid).Select(e => (NetMeterEntry?)e).SingleOrDefault();

    [Fact]
    public void Bytes_are_summed_per_process_and_direction()
    {
        _table.Start(10, 111);
        _table.Start(20, 222);
        using var counters = Create();

        counters.Count(NetMeterDirection.Send, 10, 100, _now);
        counters.Count(NetMeterDirection.Send, 10, 50, _now);
        counters.Count(NetMeterDirection.Receive, 10, 7, _now);
        counters.Count(NetMeterDirection.Receive, 20, 1_000_000, _now);

        var reading = Settled(counters);
        Assert.Equal(new NetMeterEntry(10, 111, 150, 7), Find(reading, 10));
        Assert.Equal(new NetMeterEntry(20, 222, 0, 1_000_000), Find(reading, 20));
        Assert.Equal(2, reading.Entries.Count);
    }

    [Fact]
    public void The_counters_are_cumulative_and_a_snapshot_does_not_reset_them()
    {
        _table.Start(10, 111);
        using var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 100, _now);
        _ = Settled(counters);

        counters.Count(NetMeterDirection.Send, 10, 25, _now);

        Assert.Equal(125UL, Find(Settled(counters), 10)!.Value.SendBytes);
    }

    [Fact]
    public void Four_gigabytes_do_not_wrap_the_total()
    {
        _table.Start(10, 111);
        using var counters = Create();

        counters.Count(NetMeterDirection.Receive, 10, uint.MaxValue, _now);
        counters.Count(NetMeterDirection.Receive, 10, uint.MaxValue, _now);

        Assert.Equal(2UL * uint.MaxValue, Find(Settled(counters), 10)!.Value.ReceiveBytes);
    }

    [Fact]
    public void A_process_that_cannot_be_opened_goes_to_the_lost_total_and_to_nobody_else()
    {
        _table.Start(10, 111);
        using var counters = Create();

        counters.Count(NetMeterDirection.Send, 999, 500, _now); // exited before its event arrived
        counters.Count(NetMeterDirection.Receive, 999, 300, _now);
        counters.Count(NetMeterDirection.Send, 0, 40, _now); // pid 0 is not a process
        counters.Count(NetMeterDirection.Send, 10, 1, _now);

        var reading = Settled(counters);
        Assert.Equal((540UL, 300UL), counters.LostToExitedProcesses);
        Assert.Equal([new NetMeterEntry(10, 111, 1, 0)], reading.Entries);
    }

    [Fact]
    public void Pid_4_is_a_process_like_any_other_and_its_bytes_are_not_given_away()
    {
        _table.Start(4, 44);
        _table.Start(10, 111);
        using var counters = Create();

        counters.Count(NetMeterDirection.Receive, 4, 900, _now);
        counters.Count(NetMeterDirection.Receive, 10, 100, _now);

        var reading = Settled(counters);
        Assert.Equal(900UL, Find(reading, 4)!.Value.ReceiveBytes);
        Assert.Equal(100UL, Find(reading, 10)!.Value.ReceiveBytes);
    }

    [Fact]
    public void A_reused_pid_starts_new_counters_under_its_own_creation_time()
    {
        _table.Start(10, 111);
        using var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 1000, _now);

        _table.Exit(10); // the handle the meter holds keeps the pid from being reused until the entry is dropped
        Assert.Equal(1, counters.RemoveExited());
        _table.Start(10, 999); // another process gets the pid
        counters.Count(NetMeterDirection.Send, 10, 5, _now);

        var reading = Settled(counters);
        var entry = Assert.Single(reading.Entries);
        Assert.Equal(new NetMeterEntry(10, 999, 5, 0), entry);
    }

    [Fact]
    public void While_the_old_process_is_still_pinned_a_late_event_stays_with_the_old_one()
    {
        _table.Start(10, 111);
        using var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 1000, _now);

        _table.Exit(10);
        counters.Count(NetMeterDirection.Send, 10, 20, _now); // a delayed event of the exited process, before any pruning

        var entry = Assert.Single(Settled(counters).Entries);
        Assert.Equal(new NetMeterEntry(10, 111, 1020, 0), entry);
    }

    [Fact]
    public void An_event_after_the_process_is_gone_and_its_entry_dropped_is_lost_not_given_to_the_next_pid_owner()
    {
        _table.Start(10, 111);
        using var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 1000, _now);
        _table.Exit(10);
        counters.RemoveExited();
        _table.Forget(10);

        counters.Count(NetMeterDirection.Send, 10, 20, _now);

        Assert.Empty(Settled(counters).Entries);
        Assert.Equal(20UL, counters.LostToExitedProcesses.Send);
    }

    [Fact]
    public void Dropping_exited_processes_lets_go_of_their_handles_and_keeps_the_live_ones()
    {
        _table.Start(10, 111);
        _table.Start(20, 222);
        using var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 1, _now);
        counters.Count(NetMeterDirection.Send, 20, 1, _now);
        _table.Exit(10);

        Assert.Equal(1, counters.RemoveExited());

        Assert.Equal(1, counters.ProcessCount);
        Assert.Equal(20u, Assert.Single(Settled(counters).Entries).Pid);
        Assert.True(_table.Pins[0].Disposed);
        Assert.False(_table.Pins[1].Disposed);
        Assert.Equal(0, counters.RemoveExited());
    }

    [Fact]
    public void Disposing_the_counters_releases_every_handle()
    {
        _table.Start(10, 111);
        _table.Start(20, 222);
        var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 1, _now);
        counters.Count(NetMeterDirection.Send, 20, 1, _now);

        counters.Dispose();

        Assert.All(_table.Pins, p => Assert.True(p.Disposed));
        counters.Count(NetMeterDirection.Send, 10, 1, _now); // after disposal nothing is counted or opened
        Assert.Equal(2, _table.Pins.Count);
    }

    [Fact]
    public void The_table_is_bounded_and_the_exited_make_room_first()
    {
        using var counters = Create();
        for (var pid = 1u; pid <= NetMeterCounters.MaxTrackedProcesses; pid++)
        {
            _table.Start(pid, pid);
            counters.Count(NetMeterDirection.Send, pid, 1, _now);
        }

        _table.Start(50_000, 5);
        counters.Count(NetMeterDirection.Send, 50_000, 7, _now); // full: dropped into the lost total
        Assert.Equal(NetMeterCounters.MaxTrackedProcesses, counters.ProcessCount);
        Assert.Equal(7UL, counters.LostToExitedProcesses.Send);

        _table.Exit(1);
        counters.Count(NetMeterDirection.Send, 50_000, 3, _now); // the exited one is dropped to make room
        Assert.Equal(NetMeterCounters.MaxTrackedProcesses, counters.ProcessCount);
        Assert.Equal(3UL, Find(Settled(counters), 50_000)!.Value.SendBytes);
        Assert.Null(Find(Settled(counters), 1));
    }

    [Fact]
    public void Undecodable_events_are_counted_and_never_added_to_a_process()
    {
        _table.Start(10, 111);
        using var counters = Create();

        counters.NoteUndecodable();
        counters.NoteUndecodable();

        var reading = Settled(counters);
        Assert.Equal(2UL, reading.Undecodable);
        Assert.Empty(reading.Entries);
    }

    [Fact]
    public void Lost_events_and_the_session_state_are_carried_into_the_reading_as_given()
    {
        using var counters = Create();

        var reading = counters.Snapshot(eventsLost: 12, buffersLost: 3, sessionHealthy: false, errorCode: 87);

        Assert.Equal(12UL, reading.EventsLost);
        Assert.Equal(3UL, reading.BuffersLost);
        Assert.False(reading.SessionHealthy);
        Assert.Equal(87u, reading.ErrorCode);
    }

    // ---- the horizon: what a reading is complete up to ----

    private const long Slot = NetMeterCounters.SlotTicks;
    private const long Lag = NetMeterCounters.LagTicks;

    /// <summary>Moves the clock far enough that everything counted so far is before the horizon, then reads.</summary>
    private NetMeterReading Settled(NetMeterCounters counters)
    {
        _now += Lag + Second;
        return counters.Snapshot(0, 0, true, 0);
    }

    [Fact]
    public void The_horizon_is_the_read_time_minus_the_lag_on_a_slot_boundary()
    {
        using var counters = Create();
        _now += (30 * Second) + (37 * Slot / 100); // 130.037 s

        var reading = counters.Snapshot(0, 0, true, 0);

        var expected = (_now - Lag) / Slot * Slot;
        Assert.Equal(expected, reading.TimestampTicks);
        Assert.Equal(0, reading.TimestampTicks % Slot);
        Assert.True(reading.TimestampTicks <= _now - Lag);
        Assert.True(reading.TimestampTicks > _now - Lag - Slot);
    }

    [Fact]
    public void Events_newer_than_the_horizon_wait_and_do_not_show_in_the_reading_yet()
    {
        _table.Start(10, 111);
        using var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 500, _now);

        var early = counters.Snapshot(0, 0, true, 0);
        Assert.Equal(new NetMeterEntry(10, 111, 0, 0), Find(early, 10)); // seen, but its bytes are not before the horizon yet
        Assert.Equal(1, counters.PendingSlotCount);

        Assert.Equal(500UL, Find(Settled(counters), 10)!.Value.SendBytes);
        Assert.Equal(0, counters.PendingSlotCount);
    }

    [Fact]
    public void An_event_exactly_at_the_horizon_is_still_pending_and_one_tick_before_it_is_late()
    {
        _table.Start(10, 111);
        using var counters = Create();
        _now += 10 * Second;
        var horizon = counters.Snapshot(0, 0, true, 0).TimestampTicks;

        counters.Count(NetMeterDirection.Send, 10, 7, horizon); // belongs to the window after this reading
        Assert.Equal(0UL, counters.LateEvents);
        Assert.Equal(0UL, Find(counters.Snapshot(0, 0, true, 0), 10)!.Value.SendBytes);

        counters.Count(NetMeterDirection.Send, 10, 3, horizon - 1); // the reading above already claimed to be complete here
        Assert.Equal(1UL, counters.LateEvents);
    }

    [Fact]
    public void A_late_event_is_added_to_the_totals_at_once_and_counted_so_a_short_lag_shows()
    {
        _table.Start(10, 111);
        using var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 1000, _now);
        var before = Settled(counters);
        Assert.Equal(1000UL, Find(before, 10)!.Value.SendBytes);
        Assert.Equal(0UL, counters.LateEvents);

        // An event of a second ago, delivered only now: older than the horizon the reader was already told.
        counters.Count(NetMeterDirection.Send, 10, 250, before.TimestampTicks - Second);
        counters.Count(NetMeterDirection.Receive, 10, 40, before.TimestampTicks - Second);

        Assert.Equal((2UL, 290UL), counters.Late);
        Assert.Equal(2UL, counters.LateEvents);
        var after = counters.Snapshot(0, 0, true, 0);
        Assert.Equal(1250UL, Find(after, 10)!.Value.SendBytes); // the total stays exact: nothing is dropped, only the window moves
        Assert.Equal(40UL, Find(after, 10)!.Value.ReceiveBytes);
    }

    [Fact]
    public void An_event_from_before_the_clock_origin_is_late_and_not_put_in_slot_zero()
    {
        _now = 0;
        _table.Start(10, 111);
        using var counters = Create();

        counters.Count(NetMeterDirection.Send, 10, 9, -1);

        Assert.Equal(1UL, counters.LateEvents);
    }

    [Fact]
    public void An_event_time_ahead_of_the_clock_counts_as_now_instead_of_waiting_for_ever()
    {
        _table.Start(10, 111);
        using var counters = Create();

        counters.Count(NetMeterDirection.Send, 10, 77, _now + (3600 * Second)); // a timestamp on another base

        Assert.Equal(77UL, Find(Settled(counters), 10)!.Value.SendBytes);
        Assert.Equal(0, counters.PendingSlotCount);
    }

    [Fact]
    public void The_horizon_never_goes_backwards_and_the_totals_never_shrink()
    {
        _table.Start(10, 111);
        using var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 5, _now);
        var first = Settled(counters);

        _now -= 5 * Second; // a clock that stepped back (it should not, but the format promises monotonic)
        counters.Count(NetMeterDirection.Send, 10, 6, _now);
        var again = counters.Snapshot(0, 0, true, 0);

        Assert.True(again.TimestampTicks >= first.TimestampTicks);
        Assert.True(Find(again, 10)!.Value.SendBytes >= Find(first, 10)!.Value.SendBytes);
    }

    [Fact]
    public void Two_reads_inside_one_slot_see_the_same_horizon_so_the_reader_can_tell_nothing_new_came()
    {
        using var counters = Create();
        _now += 10 * Second;
        var first = counters.Snapshot(0, 0, true, 0);

        _now += Slot / 4;
        var second = counters.Snapshot(0, 0, true, 0);

        Assert.Equal(first.TimestampTicks, second.TimestampTicks);
    }

    [Fact]
    public void The_pending_buckets_are_bounded_even_when_nobody_ever_reads()
    {
        _table.Start(10, 111);
        _table.Start(20, 222);
        using var counters = Create();

        // An hour of traffic, one event every 50 ms, and not a single read.
        ulong sent = 0;
        for (var i = 0; i < 72_000; i++)
        {
            _now += Slot / 2;
            counters.Count(NetMeterDirection.Send, 10, 100, _now);
            counters.Count(NetMeterDirection.Receive, 20, 100, _now);
            sent += 100;
            Assert.True(counters.PendingSlotCount <= 2 * NetMeterCounters.MaxPendingSlots, $"{counters.PendingSlotCount} pending slots after {i} events");
        }

        Assert.Equal(0UL, counters.LateEvents); // forcing the horizon forward never makes in-order events late
        var reading = Settled(counters);
        Assert.Equal(sent, Find(reading, 10)!.Value.SendBytes); // and nothing was lost by folding
        Assert.Equal(sent, Find(reading, 20)!.Value.ReceiveBytes);
    }

    [Fact]
    public void The_pending_bucket_cap_leaves_room_for_the_lag_and_a_reader_that_comes_every_two_seconds()
    {
        Assert.True(
            NetMeterCounters.MaxPendingSlots * Slot >= Lag + (2 * Second) + (2 * Slot),
            "MaxPendingSlots is too small for LagTicks: a normal reader would force the horizon forward");
    }

    [Fact]
    public void The_lag_covers_the_flush_timer_with_room_to_spare()
    {
        // Measured delivery latency is up to about 3.2 flushes (SPEC 4.1); the lag has to cover that with room to spare, so
        // changing the flush timer without the lag (or the other way round) fails here.
        Assert.True(Lag >= 6 * NetMeterEvents.FlushTimerMilliseconds * 10_000, "the lag must be at least six flush timers long");
    }

    // ---- batching: the reason all of this exists ----

    /// <summary>
    /// A 100 KB/s stream (1000 bytes every 10 ms) of one process over <paramref name="seconds"/> seconds, handed to the counters
    /// in batches (a gap from <paramref name="nextGapMs"/> between deliveries, always shorter than the lag), and read about once a
    /// second with jitter. Returns the readings and the bytes that were sent in all.
    /// </summary>
    private (List<NetMeterReading> Readings, ulong Sent) RunStream(NetMeterCounters counters, Func<int> nextGapMs, int seconds, int stopAfterMs = int.MaxValue)
    {
        var jitter = new Random(7);
        var start = _now;
        var arrived = new List<long>();
        var readings = new List<NetMeterReading>();
        ulong sent = 0;
        var nextDelivery = nextGapMs();
        var nextRead = 1000;
        for (var ms = 1; ms <= seconds * 1000; ms++)
        {
            _now = start + (ms * 10_000L);
            if (ms % 10 == 0 && ms <= stopAfterMs)
            {
                arrived.Add(_now);
                sent += 1000;
            }

            if (ms >= nextDelivery)
            {
                foreach (var time in arrived)
                {
                    counters.Count(NetMeterDirection.Send, 10, 1000, time);
                }

                arrived.Clear();
                nextDelivery = ms + nextGapMs();
            }

            if (ms >= nextRead)
            {
                readings.Add(counters.Snapshot(0, 0, true, 0));
                nextRead = ms + 1000 + jitter.Next(-90, 91);
            }
        }

        foreach (var time in arrived)
        {
            counters.Count(NetMeterDirection.Send, 10, 1000, time); // the last batch, so that the sum can be compared
        }

        return (readings, sent);
    }

    /// <summary>The send rates the real calculator makes of the readings, one per reading after the first (the baseline).</summary>
    private static List<double> RatesOf(List<NetMeterReading> readings)
    {
        var calculator = new NetRateCalculator();
        var alive = new[] { new ProcessKey(10, 111) };
        var rates = new List<double>();
        var output = new NetRates[1];
        for (var i = 0; i < readings.Count; i++)
        {
            calculator.Compute(readings[i], 1, alive, output);
            if (i > 0)
            {
                Assert.True(output[0].Send.IsOk, $"reading {i}: {output[0].Send.Status} {output[0].Send.Detail}");
                rates.Add(output[0].Send.Data);
            }
        }

        return rates;
    }

    [Theory]
    [InlineData(50)]
    [InlineData(250)]
    [InlineData(500)]
    [InlineData(700)]
    public void A_steady_stream_delivered_in_batches_reads_as_the_same_rate_in_every_window(int batchMs)
    {
        _table.Start(10, 111);
        using var counters = Create();

        var (readings, sent) = RunStream(counters, () => batchMs, 30);
        var rates = RatesOf(readings);

        Assert.True(rates.Count >= 25);
        foreach (var rate in rates)
        {
            Assert.Equal(100_000.0, rate, 3); // exact in, exact out: not "about", whatever the batching and the jitter of the reads
        }

        Assert.Equal(0UL, counters.LateEvents);
        Assert.Equal(sent, Find(Settled(counters), 10)!.Value.SendBytes);
    }

    [Fact]
    public void Irregular_batches_and_irregular_reads_still_give_the_same_rate_in_every_window()
    {
        _table.Start(10, 111);
        using var counters = Create();
        var gaps = new Random(1234);

        var (readings, sent) = RunStream(counters, () => gaps.Next(20, 701), 40);
        var rates = RatesOf(readings);

        Assert.True(rates.Count >= 35);
        Assert.All(rates, rate => Assert.Equal(100_000.0, rate, 3));
        Assert.Equal(0UL, counters.LateEvents);
        Assert.Equal(sent, Find(Settled(counters), 10)!.Value.SendBytes);
    }

    [Fact]
    public void A_burst_is_attributed_to_the_window_it_happened_in_not_the_one_it_was_delivered_in()
    {
        _table.Start(10, 111);
        using var counters = Create();
        var start = _now;

        // 1 MB at t = 2.0 .. 2.3 s, delivered as one batch at 2.9 s. The reader sees nothing at 2.5 s (the horizon is 1.7 s), and
        // 3.5 s (horizon 2.7 s) holds the whole burst: it falls in a single window, and not half in the window that merely
        // happened to be open when the batch arrived.
        _now = start + (25 * Second / 10);
        var a = counters.Snapshot(0, 0, true, 0);
        _now = start + (29 * Second / 10);
        for (var i = 0; i < 10; i++)
        {
            counters.Count(NetMeterDirection.Send, 10, 100_000, start + (2 * Second) + (i * Slot / 3));
        }

        _now = start + (35 * Second / 10);
        var b = counters.Snapshot(0, 0, true, 0);
        _now = start + (45 * Second / 10);
        var c = counters.Snapshot(0, 0, true, 0);

        Assert.Equal(0UL, Find(a, 10)?.SendBytes ?? 0);
        Assert.Equal(1_000_000UL, Find(b, 10)!.Value.SendBytes);
        Assert.Equal(1_000_000UL, Find(c, 10)!.Value.SendBytes);
        Assert.Equal(0UL, counters.LateEvents);
    }

    [Fact]
    public void When_the_traffic_stops_the_horizon_keeps_moving_and_the_rate_falls_to_zero()
    {
        _table.Start(10, 111);
        using var counters = Create();

        var (readings, sent) = RunStream(counters, () => 250, 20, stopAfterMs: 5000);
        var rates = RatesOf(readings);

        for (var i = 1; i < readings.Count; i++)
        {
            Assert.True(readings[i].TimestampTicks > readings[i - 1].TimestampTicks, "the horizon did not move");
        }

        Assert.All(rates, rate => Assert.InRange(rate, 0.0, 100_000.0 + 0.001));
        Assert.Equal(100_000.0, rates[0], 3);
        Assert.Equal(0.0, rates[^1], 3); // not the last busy rate repeated
        Assert.Equal(0.0, rates[^5], 3);
        Assert.Equal(sent, Find(Settled(counters), 10)!.Value.SendBytes);
    }

    [Fact]
    public void Counting_from_many_threads_loses_nothing()
    {
        _table.Start(10, 111);
        using var counters = Create();

        Parallel.For(0, 8, worker =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                counters.Count(NetMeterDirection.Send, 10, 3, _now);
                counters.Count(NetMeterDirection.Receive, 10, 5, _now);
                if (i % 1000 == 0)
                {
                    _ = counters.Snapshot(0, 0, true, 0);
                }
            }
        });

        var entry = Find(Settled(counters), 10)!.Value;
        Assert.Equal(8UL * 10_000 * 3, entry.SendBytes);
        Assert.Equal(8UL * 10_000 * 5, entry.ReceiveBytes);
    }
}
