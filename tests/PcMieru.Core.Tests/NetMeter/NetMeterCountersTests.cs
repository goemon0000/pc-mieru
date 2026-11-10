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

        counters.Count(NetMeterDirection.Send, 10, 100);
        counters.Count(NetMeterDirection.Send, 10, 50);
        counters.Count(NetMeterDirection.Receive, 10, 7);
        counters.Count(NetMeterDirection.Receive, 20, 1_000_000);

        var reading = counters.Snapshot(0, 0, true, 0);
        Assert.Equal(new NetMeterEntry(10, 111, 150, 7), Find(reading, 10));
        Assert.Equal(new NetMeterEntry(20, 222, 0, 1_000_000), Find(reading, 20));
        Assert.Equal(2, reading.Entries.Count);
    }

    [Fact]
    public void The_counters_are_cumulative_and_a_snapshot_does_not_reset_them()
    {
        _table.Start(10, 111);
        using var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 100);
        _ = counters.Snapshot(0, 0, true, 0);

        counters.Count(NetMeterDirection.Send, 10, 25);

        Assert.Equal(125UL, Find(counters.Snapshot(0, 0, true, 0), 10)!.Value.SendBytes);
    }

    [Fact]
    public void Four_gigabytes_do_not_wrap_the_total()
    {
        _table.Start(10, 111);
        using var counters = Create();

        counters.Count(NetMeterDirection.Receive, 10, uint.MaxValue);
        counters.Count(NetMeterDirection.Receive, 10, uint.MaxValue);

        Assert.Equal(2UL * uint.MaxValue, Find(counters.Snapshot(0, 0, true, 0), 10)!.Value.ReceiveBytes);
    }

    [Fact]
    public void A_process_that_cannot_be_opened_goes_to_the_lost_total_and_to_nobody_else()
    {
        _table.Start(10, 111);
        using var counters = Create();

        counters.Count(NetMeterDirection.Send, 999, 500); // exited before its event arrived
        counters.Count(NetMeterDirection.Receive, 999, 300);
        counters.Count(NetMeterDirection.Send, 0, 40); // pid 0 is not a process
        counters.Count(NetMeterDirection.Send, 10, 1);

        var reading = counters.Snapshot(0, 0, true, 0);
        Assert.Equal((540UL, 300UL), counters.LostToExitedProcesses);
        Assert.Equal([new NetMeterEntry(10, 111, 1, 0)], reading.Entries);
    }

    [Fact]
    public void Pid_4_is_a_process_like_any_other_and_its_bytes_are_not_given_away()
    {
        _table.Start(4, 44);
        _table.Start(10, 111);
        using var counters = Create();

        counters.Count(NetMeterDirection.Receive, 4, 900);
        counters.Count(NetMeterDirection.Receive, 10, 100);

        var reading = counters.Snapshot(0, 0, true, 0);
        Assert.Equal(900UL, Find(reading, 4)!.Value.ReceiveBytes);
        Assert.Equal(100UL, Find(reading, 10)!.Value.ReceiveBytes);
    }

    [Fact]
    public void A_reused_pid_starts_new_counters_under_its_own_creation_time()
    {
        _table.Start(10, 111);
        using var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 1000);

        _table.Exit(10); // the handle the meter holds keeps the pid from being reused until the entry is dropped
        Assert.Equal(1, counters.RemoveExited());
        _table.Start(10, 999); // another process gets the pid
        counters.Count(NetMeterDirection.Send, 10, 5);

        var reading = counters.Snapshot(0, 0, true, 0);
        var entry = Assert.Single(reading.Entries);
        Assert.Equal(new NetMeterEntry(10, 999, 5, 0), entry);
    }

    [Fact]
    public void While_the_old_process_is_still_pinned_a_late_event_stays_with_the_old_one()
    {
        _table.Start(10, 111);
        using var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 1000);

        _table.Exit(10);
        counters.Count(NetMeterDirection.Send, 10, 20); // a delayed event of the exited process, before any pruning

        var entry = Assert.Single(counters.Snapshot(0, 0, true, 0).Entries);
        Assert.Equal(new NetMeterEntry(10, 111, 1020, 0), entry);
    }

    [Fact]
    public void An_event_after_the_process_is_gone_and_its_entry_dropped_is_lost_not_given_to_the_next_pid_owner()
    {
        _table.Start(10, 111);
        using var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 1000);
        _table.Exit(10);
        counters.RemoveExited();
        _table.Forget(10);

        counters.Count(NetMeterDirection.Send, 10, 20);

        Assert.Empty(counters.Snapshot(0, 0, true, 0).Entries);
        Assert.Equal(20UL, counters.LostToExitedProcesses.Send);
    }

    [Fact]
    public void Dropping_exited_processes_lets_go_of_their_handles_and_keeps_the_live_ones()
    {
        _table.Start(10, 111);
        _table.Start(20, 222);
        using var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 1);
        counters.Count(NetMeterDirection.Send, 20, 1);
        _table.Exit(10);

        Assert.Equal(1, counters.RemoveExited());

        Assert.Equal(1, counters.ProcessCount);
        Assert.Equal(20u, Assert.Single(counters.Snapshot(0, 0, true, 0).Entries).Pid);
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
        counters.Count(NetMeterDirection.Send, 10, 1);
        counters.Count(NetMeterDirection.Send, 20, 1);

        counters.Dispose();

        Assert.All(_table.Pins, p => Assert.True(p.Disposed));
        counters.Count(NetMeterDirection.Send, 10, 1); // after disposal nothing is counted or opened
        Assert.Equal(2, _table.Pins.Count);
    }

    [Fact]
    public void The_table_is_bounded_and_the_exited_make_room_first()
    {
        using var counters = Create();
        for (var pid = 1u; pid <= NetMeterCounters.MaxTrackedProcesses; pid++)
        {
            _table.Start(pid, pid);
            counters.Count(NetMeterDirection.Send, pid, 1);
        }

        _table.Start(50_000, 5);
        counters.Count(NetMeterDirection.Send, 50_000, 7); // full: dropped into the lost total
        Assert.Equal(NetMeterCounters.MaxTrackedProcesses, counters.ProcessCount);
        Assert.Equal(7UL, counters.LostToExitedProcesses.Send);

        _table.Exit(1);
        counters.Count(NetMeterDirection.Send, 50_000, 3); // the exited one is dropped to make room
        Assert.Equal(NetMeterCounters.MaxTrackedProcesses, counters.ProcessCount);
        Assert.Equal(3UL, Find(counters.Snapshot(0, 0, true, 0), 50_000)!.Value.SendBytes);
        Assert.Null(Find(counters.Snapshot(0, 0, true, 0), 1));
    }

    [Fact]
    public void Undecodable_events_are_counted_and_never_added_to_a_process()
    {
        _table.Start(10, 111);
        using var counters = Create();

        counters.NoteDelivery(undecodable: true);
        counters.NoteDelivery(undecodable: true);
        counters.NoteDelivery();

        var reading = counters.Snapshot(0, 0, true, 0);
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

    // ---- the timestamp the reading is complete up to ----

    [Fact]
    public void While_events_keep_arriving_the_timestamp_is_the_latest_delivery_not_the_read_time()
    {
        _table.Start(10, 111);
        using var counters = Create();

        _now += 1 * Second;
        counters.Count(NetMeterDirection.Send, 10, 1); // a batch is delivered at t = 101 s
        _now += Second / 2; // and the front end reads half a second later

        Assert.Equal(101 * Second, counters.Snapshot(0, 0, true, 0).TimestampTicks);
    }

    [Fact]
    public void Two_reads_inside_one_flush_interval_see_the_same_timestamp_so_the_reader_can_tell_nothing_new_came()
    {
        _table.Start(10, 111);
        using var counters = Create();
        _now += Second;
        counters.Count(NetMeterDirection.Send, 10, 1);

        _now += Second / 4;
        var first = counters.Snapshot(0, 0, true, 0);
        _now += Second / 4;
        var second = counters.Snapshot(0, 0, true, 0);

        Assert.Equal(first.TimestampTicks, second.TimestampTicks);
    }

    [Fact]
    public void When_nothing_has_arrived_for_longer_than_a_flush_the_timestamp_follows_the_clock_at_a_lag()
    {
        _table.Start(10, 111);
        using var counters = Create();
        counters.Count(NetMeterDirection.Send, 10, 1); // at t = 100 s

        _now += 10 * Second;
        var reading = counters.Snapshot(0, 0, true, 0);

        Assert.Equal(_now - NetMeterCounters.IdleLagTicks, reading.TimestampTicks);
    }

    [Fact]
    public void The_timestamp_never_goes_backwards()
    {
        using var counters = Create();
        _now += 20 * Second;
        var late = counters.Snapshot(0, 0, true, 0).TimestampTicks;

        _now -= 5 * Second; // a clock that stepped back (it should not, but the format promises monotonic)
        var again = counters.Snapshot(0, 0, true, 0).TimestampTicks;

        Assert.True(again >= late);
    }

    [Fact]
    public void An_uncounted_event_still_proves_a_flush_happened()
    {
        using var counters = Create();
        _now += Second;
        counters.NoteDelivery(); // e.g. a connect event, which is not counted

        _now += Second / 2;
        Assert.Equal(101 * Second, counters.Snapshot(0, 0, true, 0).TimestampTicks);
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
                counters.Count(NetMeterDirection.Send, 10, 3);
                counters.Count(NetMeterDirection.Receive, 10, 5);
                if (i % 1000 == 0)
                {
                    _ = counters.Snapshot(0, 0, true, 0);
                }
            }
        });

        var entry = Find(counters.Snapshot(0, 0, true, 0), 10)!.Value;
        Assert.Equal(8UL * 10_000 * 3, entry.SendBytes);
        Assert.Equal(8UL * 10_000 * 5, entry.ReceiveBytes);
    }
}
