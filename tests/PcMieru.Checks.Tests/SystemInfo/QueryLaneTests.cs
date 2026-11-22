using PcMieru.Checks.SystemInfo;

namespace PcMieru.Checks.Tests.SystemInfo;

public sealed class QueryLaneTests
{
    /// <summary>A call whose work blocks until the test lets it go; <see cref="Entered"/> is set when the worker is inside.</summary>
    private sealed class StuckCall
    {
        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public bool SawAbandoned { get; private set; }

        public CheckReading<int> Work(LaneJob job)
        {
            Entered.Set();
            Release.Wait(QueryTest.Patience);
            SawAbandoned = job.IsAbandoned;
            return CheckReading<int>.Ok(99);
        }
    }

    [Fact]
    public async Task A_call_runs_on_a_named_background_thread_and_returns_its_reading()
    {
        var lane = new QueryLane("Probe");
        var clock = new ManualTimeProvider();
        (int Id, bool Background, string? Name) worker = default;

        var reading = await QueryTest.Done(lane.RunAsync(
            _ =>
            {
                worker = (Environment.CurrentManagedThreadId, Thread.CurrentThread.IsBackground, Thread.CurrentThread.Name);
                return CheckReading<int>.Ok(42);
            },
            QueryTest.Deadline(clock),
            CancellationToken.None));

        Assert.Equal(42, reading.Value);
        Assert.NotEqual(Environment.CurrentManagedThreadId, worker.Id);
        Assert.True(worker.Background);
        Assert.Equal("PCMieru.Checks.Probe", worker.Name);
    }

    [Fact]
    public async Task The_slot_is_free_before_the_caller_is_told()
    {
        var lane = new QueryLane("Probe", slots: 1);
        var clock = new ManualTimeProvider();

        var first = await QueryTest.Done(lane.RunAsync(_ => CheckReading<int>.Ok(1), QueryTest.Deadline(clock), CancellationToken.None));

        Assert.Equal(0, lane.Held); // no waiting for it: the worker gave the slot back before it handed over the value
        var second = await QueryTest.Done(lane.RunAsync(_ => CheckReading<int>.Ok(2), QueryTest.Deadline(clock), CancellationToken.None));
        Assert.Equal(1, first.Value);
        Assert.Equal(2, second.Value);
    }

    [Fact]
    public async Task When_every_slot_is_held_a_new_request_fails_fast_and_starts_no_worker()
    {
        var counting = new CountingLane(slots: 2);
        var clock = new ManualTimeProvider();
        var stuck = new[] { new StuckCall(), new StuckCall() };
        var running = stuck
            .Select(call => counting.Lane.RunAsync(call.Work, QueryTest.Deadline(clock), CancellationToken.None))
            .ToArray();
        foreach (var call in stuck)
        {
            QueryTest.Reached(call.Entered);
        }

        var refused = counting.Lane.RunAsync(_ => CheckReading<int>.Ok(1), QueryTest.Deadline(clock), CancellationToken.None);

        Assert.True(refused.IsCompleted, "a refused request must not wait for anything");
        Assert.Equal(CheckReason.Timeout, (await refused).Reason);
        Assert.Equal(2, counting.Started);
        Assert.Equal(2, counting.Lane.Held);

        foreach (var call in stuck)
        {
            call.Release.Set();
        }

        Assert.All(await QueryTest.Done(Task.WhenAll(running)), reading => Assert.Equal(99, reading.Value));
    }

    [Fact]
    public async Task A_slot_is_held_until_the_stuck_call_returns_even_after_the_caller_gave_up()
    {
        var counting = new CountingLane(slots: 2);
        var clock = new ManualTimeProvider();
        var a = new StuckCall();
        var b = new StuckCall();
        var runA = counting.Lane.RunAsync(a.Work, QueryTest.Deadline(clock), CancellationToken.None);
        var runB = counting.Lane.RunAsync(b.Work, QueryTest.Deadline(clock), CancellationToken.None);
        QueryTest.Reached(a.Entered);
        QueryTest.Reached(b.Entered);

        clock.Advance(TimeSpan.FromSeconds(6)); // past the 5 s deadline of both
        Assert.Equal(CheckReason.Timeout, (await QueryTest.Done(runA)).Reason);
        Assert.Equal(CheckReason.Timeout, (await QueryTest.Done(runB)).Reason);

        // The callers have given up; the Windows calls have not returned: both slots are still taken.
        Assert.Equal(2, counting.Lane.Held);
        var whileStuck = await counting.Lane.RunAsync(_ => CheckReading<int>.Ok(1), QueryTest.Deadline(clock), CancellationToken.None);
        Assert.Equal(CheckReason.Timeout, whileStuck.Reason);
        Assert.Equal(2, counting.Started);

        // One stuck call returns: exactly one slot comes back, and a new request is served.
        a.Release.Set();
        Assert.True(QueryTest.Eventually(() => counting.Lane.Held == 1), "the slot of the returned call was not given back");
        var served = await QueryTest.Done(counting.Lane.RunAsync(_ => CheckReading<int>.Ok(7), QueryTest.Deadline(clock), CancellationToken.None));
        Assert.Equal(7, served.Value);
        Assert.Equal(3, counting.Started);

        b.Release.Set();
        Assert.True(QueryTest.Eventually(() => counting.Lane.Held == 0));
    }

    [Fact]
    public async Task A_value_that_comes_back_after_the_caller_gave_up_is_dropped()
    {
        var counting = new CountingLane(slots: 1);
        var clock = new ManualTimeProvider();
        var stuck = new StuckCall();
        var run = counting.Lane.RunAsync(stuck.Work, QueryTest.Deadline(clock), CancellationToken.None);
        QueryTest.Reached(stuck.Entered);

        clock.Advance(TimeSpan.FromSeconds(5));
        var reading = await QueryTest.Done(run);
        stuck.Release.Set();
        Assert.True(QueryTest.Eventually(() => counting.Lane.Held == 0));

        Assert.False(reading.HasValue);
        Assert.Equal(CheckReason.Timeout, reading.Reason);
        Assert.True(stuck.SawAbandoned, "the worker must be able to see that nobody is waiting");
        Assert.Equal(CheckReason.Timeout, (await run).Reason); // still what the caller was given
    }

    [Fact]
    public async Task A_value_that_was_ready_in_time_is_not_taken_back_by_a_later_deadline()
    {
        var lane = new QueryLane("Probe");
        var clock = new ManualTimeProvider();

        var run = lane.RunAsync(_ => CheckReading<int>.Ok(5), QueryTest.Deadline(clock), CancellationToken.None);
        var reading = await QueryTest.Done(run);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(5, reading.Value);
        Assert.Equal(5, (await run).Value);
    }

    [Fact]
    public void The_call_state_is_settled_once_and_the_loser_is_dropped()
    {
        var abandonedFirst = new LaneCall<int>();
        Assert.True(abandonedFirst.TryAbandon());
        Assert.True(abandonedFirst.IsAbandoned);
        Assert.False(abandonedFirst.TryComplete(CheckReading<int>.Ok(1)));
        Assert.False(abandonedFirst.Done.IsCompleted, "a value that lost the race must not wake anybody");

        var completedFirst = new LaneCall<int>();
        Assert.True(completedFirst.TryComplete(CheckReading<int>.Ok(2)));
        Assert.False(completedFirst.TryAbandon());
        Assert.False(completedFirst.IsAbandoned);
        Assert.Equal(2, completedFirst.Result.Value);
        Assert.True(completedFirst.Done.IsCompleted);
    }

    [Fact]
    public async Task Cancelling_the_wait_returns_partial_at_once_and_the_worker_sees_it_was_abandoned()
    {
        var counting = new CountingLane(slots: 1);
        var clock = new ManualTimeProvider();
        var stuck = new StuckCall();
        using var cts = new CancellationTokenSource();
        var run = counting.Lane.RunAsync(stuck.Work, QueryTest.Deadline(clock), cts.Token);
        QueryTest.Reached(stuck.Entered);

        await cts.CancelAsync();
        var reading = await QueryTest.Done(run);
        Assert.Equal(1, counting.Lane.Held); // the Windows call is still out there
        stuck.Release.Set();

        Assert.Equal(CheckReason.Partial, reading.Reason);
        Assert.True(QueryTest.Eventually(() => counting.Lane.Held == 0));
        Assert.True(stuck.SawAbandoned);
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_starts_nothing()
    {
        var counting = new CountingLane();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var reading = await counting.Lane.RunAsync(_ => CheckReading<int>.Ok(1), QueryTest.Deadline(new ManualTimeProvider()), cts.Token);

        Assert.Equal(CheckReason.Partial, reading.Reason);
        Assert.Equal(0, counting.Started);
        Assert.Equal(0, counting.Lane.Held);
    }

    [Fact]
    public async Task A_deadline_that_has_already_passed_starts_nothing()
    {
        var counting = new CountingLane();
        var clock = new ManualTimeProvider();
        var deadline = QueryTest.Deadline(clock, 1);
        clock.Advance(TimeSpan.FromSeconds(2));

        var reading = await counting.Lane.RunAsync(_ => CheckReading<int>.Ok(1), deadline, CancellationToken.None);

        Assert.Equal(CheckReason.Timeout, reading.Reason);
        Assert.Equal(0, counting.Started);
        Assert.Equal(0, counting.Lane.Held);
    }

    [Fact]
    public async Task Work_that_throws_is_an_error_and_gives_its_slot_back()
    {
        var lane = new QueryLane("Probe", slots: 1);
        var clock = new ManualTimeProvider();

        var reading = await QueryTest.Done(lane.RunAsync<int>(_ => throw new InvalidOperationException("secret path C:\\Users\\someone"), QueryTest.Deadline(clock), CancellationToken.None));

        Assert.Equal(CheckReason.Error, reading.Reason);
        Assert.Equal(0, lane.Held);
        Assert.DoesNotContain("secret", reading.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_thread_that_cannot_be_started_is_an_error_and_takes_no_slot()
    {
        var lane = new QueryLane("Probe", slots: 1, start: (_, _) => throw new OutOfMemoryException());
        var clock = new ManualTimeProvider();

        var reading = await lane.RunAsync(_ => CheckReading<int>.Ok(1), QueryTest.Deadline(clock), CancellationToken.None);

        Assert.Equal(CheckReason.Error, reading.Reason);
        Assert.Equal(0, lane.Held);
    }

    [Fact]
    public async Task A_starter_that_runs_the_body_and_then_throws_gives_the_slot_back_once()
    {
        // The body runs to the end (and releases its slot), then the starter throws: the lane's own release must not be a second one.
        var lane = new QueryLane("Probe", slots: 2, start: (_, body) =>
        {
            body();
            throw new InvalidOperationException("the starter failed after the body ran");
        });
        var clock = new ManualTimeProvider();

        var reading = await lane.RunAsync(_ => CheckReading<int>.Ok(1), QueryTest.Deadline(clock), CancellationToken.None);

        Assert.Equal(CheckReason.Error, reading.Reason);
        Assert.Equal(0, lane.Held); // never -1: a slot given back twice would let a third call in
        Assert.Equal(CheckReason.Error, (await lane.RunAsync(_ => CheckReading<int>.Ok(2), QueryTest.Deadline(clock), CancellationToken.None)).Reason);
        Assert.Equal(0, lane.Held);
    }

    [Fact]
    public void The_slot_of_a_call_is_claimed_for_release_once()
    {
        var call = new LaneCall<int>();

        Assert.True(call.ClaimSlotRelease());
        Assert.False(call.ClaimSlotRelease());
        Assert.False(call.ClaimSlotRelease());
    }

    [Fact]
    public async Task Many_callers_at_once_never_run_more_than_the_slots_and_are_either_served_or_refused()
    {
        var lane = new QueryLane("Probe", slots: 2);
        var concurrent = 0;
        var peak = 0;
        var clock = TimeProvider.System;

        var runs = Enumerable.Range(0, 64).Select(i => Task.Run(() => lane.RunAsync(
            _ =>
            {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref peak, now);
                Thread.Sleep(5);
                Interlocked.Decrement(ref concurrent);
                return CheckReading<int>.Ok(i);
            },
            QueryDeadline.After(clock, TimeSpan.FromSeconds(30)),
            CancellationToken.None))).ToArray();

        var readings = await QueryTest.Done(Task.WhenAll(runs));

        Assert.InRange(peak, 1, 2);
        Assert.All(readings, reading => Assert.True(reading.HasValue || reading.Reason == CheckReason.Timeout));
        Assert.Contains(readings, reading => reading.HasValue);
        Assert.True(QueryTest.Eventually(() => lane.Held == 0));
    }

    [Fact]
    public void The_two_process_lanes_are_separate_and_small()
    {
        Assert.NotSame(QueryLanes.EventLog, QueryLanes.Wmi);
        Assert.Equal(2, QueryLanes.EventLog.Slots);
        Assert.Equal(2, QueryLanes.Wmi.Slots);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
