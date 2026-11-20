using PcMieru.Checks.SystemInfo;

namespace PcMieru.Checks.Tests.SystemInfo;

// Two kinds of test live here. The ones that call CountHere run the count itself on a thread of their own and nothing else: the fake
// may move the clock in the middle of a call, and with no lane there is no caller to give up first, so what is asserted is what the
// COUNT decided. The ones that call CountAsync go through the lane and show the lane and the count working together.
public sealed class EventCounterTests
{
    private static readonly DateTime WindowStart = new(2026, 11, 14, 3, 4, 5, 678, DateTimeKind.Utc);

    private static EventCountQuery Disk => EventCountQuery.Create(EventCountKind.Disk, WindowStart);

    private static WevtEventCounter Counter(FakeEventLogApi api, QueryLane? lane = null, long maxCount = WevtEventCounter.DefaultMaxCount) =>
        new(api, lane ?? new QueryLane("EventTest"), maxCount);

    private static Func<int, NextResult> Script(params NextResult[] steps) =>
        index => index < steps.Length ? steps[index] : throw new InvalidOperationException("EvtNext was called more often than the script has steps");

    /// <summary>The count on a thread of its own (not the test's), without a lane: what the count itself decides.</summary>
    private static Task<CheckReading<long>> CountHere(
        FakeEventLogApi api, QueryDeadline deadline, CancellationToken ct = default, long maxCount = WevtEventCounter.DefaultMaxCount, LaneJob? job = null) =>
        Task.Factory.StartNew(
            () => Counter(api, maxCount: maxCount).CountOnThisThread(Disk, deadline, job ?? new LaneJob(), ct),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>Everything the fake was called with ran on one thread, and it was not the test's: the thread that owns the handles.</summary>
    private static void AssertOwnedByOneWorker(FakeEventLogApi api)
    {
        var worker = Assert.Single(api.CallThreads.Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, worker);
    }

    [Fact]
    public async Task A_count_is_returned_after_the_end_of_the_enumeration()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeEventLogApi(Script(NextResult.Events(64), NextResult.Events(64), NextResult.Events(10), NextResult.End));

        var reading = await QueryTest.Done(Counter(api).CountAsync(Disk, QueryTest.Deadline(clock), CancellationToken.None));

        Assert.Equal(138, reading.Value);
        Assert.Equal(4, api.NextCalls);
        Assert.True(api.EveryHandleClosedExactlyOnce);
        Assert.Equal(1 + 138, api.Opened.Count); // the result set and every event
        AssertOwnedByOneWorker(api);
    }

    [Fact]
    public async Task Zero_events_is_a_count_of_zero_reached_through_the_same_end()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeEventLogApi(Script(NextResult.End));

        var reading = await QueryTest.Done(Counter(api).CountAsync(Disk, QueryTest.Deadline(clock), CancellationToken.None));

        Assert.True(reading.HasValue);
        Assert.Equal(0, reading.Value);
        Assert.Equal(1, api.NextCalls);
        Assert.Equal(new[] { FakeEventLogApi.ResultSetHandle }, api.Opened);
        Assert.True(api.EveryHandleClosedExactlyOnce);
        AssertOwnedByOneWorker(api);
    }

    [Fact]
    public async Task The_query_is_the_fixed_xpath_on_the_system_channel_by_channel_path_without_tolerating_errors()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeEventLogApi(Script(NextResult.End));

        await QueryTest.Done(Counter(api).CountAsync(Disk, QueryTest.Deadline(clock), CancellationToken.None));

        var (channel, xpath, flags) = Assert.Single(api.Queries);
        Assert.Equal("System", channel);
        Assert.Equal(Disk.XPath, xpath);
        Assert.Equal(0x1, flags);
        Assert.Equal(0, flags & 0x1000);
    }

    [Fact]
    public async Task Every_wait_is_at_most_the_time_that_is_left()
    {
        var clock = new ManualTimeProvider();
        var deadline = QueryTest.Deadline(clock, 5);
        var api = new FakeEventLogApi(index =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(1500));
            return index < 2 ? NextResult.Events(3) : NextResult.End;
        });

        var reading = await QueryTest.Done(CountHere(api, deadline));

        Assert.Equal(6, reading.Value);
        Assert.Equal(new[] { 5000, 3500, 2000 }, api.NextTimeoutsMs);
    }

    [Fact]
    public async Task Batches_that_cross_the_shared_deadline_give_a_timeout_and_no_count()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeEventLogApi(_ =>
        {
            clock.Advance(TimeSpan.FromSeconds(3)); // each batch takes 3 s of a 5 s deadline
            return NextResult.Events(10);
        });

        var reading = await QueryTest.Done(CountHere(api, QueryTest.Deadline(clock, 5)));

        Assert.False(reading.HasValue);
        Assert.Equal(CheckReason.Timeout, reading.Reason);
        Assert.Equal(2, api.NextCalls); // no third batch after the deadline
        Assert.True(api.EveryHandleClosedExactlyOnce);
        AssertOwnedByOneWorker(api);
    }

    [Fact]
    public async Task The_end_reached_after_the_deadline_is_not_a_count()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeEventLogApi(_ =>
        {
            clock.Advance(TimeSpan.FromSeconds(6));
            return NextResult.End;
        });

        var reading = await QueryTest.Done(CountHere(api, QueryTest.Deadline(clock, 5)));

        Assert.Equal(CheckReason.Timeout, reading.Reason);
        Assert.True(api.EveryHandleClosedExactlyOnce);
    }

    [Fact]
    public async Task Queries_that_share_one_deadline_share_its_remaining_time()
    {
        var clock = new ManualTimeProvider();
        var shared = QueryTest.Deadline(clock, 5);
        var slow = new FakeEventLogApi(_ =>
        {
            clock.Advance(TimeSpan.FromSeconds(4));
            return NextResult.End;
        });
        var late = new FakeEventLogApi(_ => NextResult.End);
        var tooLate = new FakeEventLogApi(Script());

        var first = await QueryTest.Done(CountHere(slow, shared));
        var second = await QueryTest.Done(CountHere(late, shared));
        clock.Advance(TimeSpan.FromSeconds(2));
        var third = await QueryTest.Done(CountHere(tooLate, shared));

        Assert.Equal(0, first.Value);
        Assert.Equal(0, second.Value);
        Assert.Equal(new[] { 1000 }, late.NextTimeoutsMs); // the second query got only what the first left
        Assert.Equal(CheckReason.Timeout, third.Reason);
        Assert.Empty(tooLate.Queries);
    }

    [Fact]
    public async Task A_win32_timeout_of_the_wait_is_a_timeout()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeEventLogApi(Script(NextResult.Events(5), NextResult.Timeout));

        var reading = await QueryTest.Done(CountHere(api, QueryTest.Deadline(clock)));

        Assert.Equal(CheckReason.Timeout, reading.Reason);
        Assert.True(api.EveryHandleClosedExactlyOnce);
    }

    [Fact]
    public async Task An_error_in_the_middle_discards_the_count_and_closes_everything()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeEventLogApi(Script(NextResult.Events(64), NextResult.Events(64), NextResult.Fail(1117)));

        var reading = await QueryTest.Done(CountHere(api, QueryTest.Deadline(clock)));

        Assert.False(reading.HasValue);
        Assert.Equal(CheckReason.Error, reading.Reason);
        Assert.Equal(3, api.NextCalls);
        Assert.True(api.EveryHandleClosedExactlyOnce);
        AssertOwnedByOneWorker(api);
    }

    [Theory]
    [InlineData(5, CheckReason.Denied)]
    [InlineData(15007, CheckReason.Unsupported)]
    [InlineData(15001, CheckReason.Error)] // the query itself is invalid
    [InlineData(1117, CheckReason.Error)]
    public async Task A_query_that_cannot_be_made_gives_the_reason_and_has_nothing_to_close(int win32Error, CheckReason expected)
    {
        var clock = new ManualTimeProvider();
        var api = new FakeEventLogApi(Script()) { QueryStatus = win32Error };

        var reading = await QueryTest.Done(CountHere(api, QueryTest.Deadline(clock)));

        Assert.Equal(expected, reading.Reason);
        Assert.Equal(0, api.NextCalls);
        Assert.Empty(api.Opened);
    }

    [Fact]
    public async Task A_count_past_the_cap_is_truncated_and_discarded()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeEventLogApi(Script(NextResult.Events(64), NextResult.Events(64), NextResult.End));

        var reading = await QueryTest.Done(CountHere(api, QueryTest.Deadline(clock), maxCount: 100));

        Assert.False(reading.HasValue);
        Assert.Equal(CheckReason.Partial, reading.Reason);
        Assert.Equal(2, api.NextCalls); // stopped at the cap, did not read on
        Assert.True(api.EveryHandleClosedExactlyOnce);
    }

    [Fact]
    public async Task A_count_exactly_at_the_cap_is_a_count()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeEventLogApi(Script(NextResult.Events(50), NextResult.Events(50), NextResult.End));

        var reading = await QueryTest.Done(CountHere(api, QueryTest.Deadline(clock), maxCount: 100));

        Assert.Equal(100, reading.Value);
    }

    [Fact]
    public async Task Cancelling_between_batches_stops_the_count_before_the_next_batch()
    {
        var clock = new ManualTimeProvider();
        using var cts = new CancellationTokenSource();
        var api = new FakeEventLogApi(_ =>
        {
            cts.Cancel();
            return NextResult.Events(10);
        });

        var reading = await QueryTest.Done(CountHere(api, QueryTest.Deadline(clock), cts.Token));

        Assert.Equal(CheckReason.Partial, reading.Reason);
        Assert.Equal(1, api.NextCalls);
        Assert.True(api.EveryHandleClosedExactlyOnce);
    }

    [Fact]
    public async Task Cancelling_as_the_end_arrives_is_stopped_and_not_a_count()
    {
        var clock = new ManualTimeProvider();
        using var cts = new CancellationTokenSource();
        var api = new FakeEventLogApi(_ =>
        {
            cts.Cancel();
            return NextResult.End;
        });

        var reading = await QueryTest.Done(CountHere(api, QueryTest.Deadline(clock), cts.Token));

        Assert.Equal(CheckReason.Partial, reading.Reason);
    }

    [Fact]
    public async Task A_caller_that_gave_up_stops_the_count_at_the_next_batch()
    {
        var clock = new ManualTimeProvider();
        var call = new LaneCall<long>();
        var api = new FakeEventLogApi(_ =>
        {
            Assert.True(call.TryAbandon());
            return NextResult.Events(10);
        });

        var reading = await QueryTest.Done(Task.Factory.StartNew(
            () => Counter(api).CountOnThisThread(Disk, QueryTest.Deadline(clock), call, CancellationToken.None),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default));

        Assert.Equal(CheckReason.Partial, reading.Reason);
        Assert.Equal(1, api.NextCalls);
        Assert.True(api.EveryHandleClosedExactlyOnce);
    }

    [Fact]
    public async Task Cancelling_during_a_wait_gives_stopped_at_once_and_the_worker_closes_everything_when_it_returns()
    {
        var clock = new ManualTimeProvider();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var api = new FakeEventLogApi(index =>
        {
            if (index == 0)
            {
                return NextResult.Events(10);
            }

            entered.Set();
            release.Wait(QueryTest.Patience);
            return NextResult.Events(10);
        });
        var lane = new QueryLane("EventTest", slots: 1);
        using var cts = new CancellationTokenSource();

        var run = Counter(api, lane).CountAsync(Disk, QueryTest.Deadline(clock), cts.Token);
        QueryTest.Reached(entered);
        await cts.CancelAsync();
        var reading = await QueryTest.Done(run);

        Assert.Equal(CheckReason.Partial, reading.Reason);
        Assert.Equal(1, lane.Held); // EvtNext has not returned
        release.Set();
        Assert.True(QueryTest.Eventually(() => lane.Held == 0));
        Assert.Equal(2, api.NextCalls); // the worker did not ask for a third batch
        Assert.True(api.EveryHandleClosedExactlyOnce);
        AssertOwnedByOneWorker(api);
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_calls_nothing()
    {
        var clock = new ManualTimeProvider();
        var api = new FakeEventLogApi(Script());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var reading = await Counter(api).CountAsync(Disk, QueryTest.Deadline(clock), cts.Token);

        Assert.Equal(CheckReason.Partial, reading.Reason);
        Assert.Empty(api.Queries);
    }

    [Fact]
    public async Task A_deadline_that_has_already_passed_calls_nothing()
    {
        var clock = new ManualTimeProvider();
        var deadline = QueryTest.Deadline(clock, 5);
        clock.Advance(TimeSpan.FromSeconds(5));
        var api = new FakeEventLogApi(Script());

        var direct = await QueryTest.Done(CountHere(api, deadline));
        var viaLane = await Counter(api).CountAsync(Disk, deadline, CancellationToken.None);

        Assert.Equal(CheckReason.Timeout, direct.Reason);
        Assert.Equal(CheckReason.Timeout, viaLane.Reason);
        Assert.Empty(api.Queries);
    }

    [Fact]
    public async Task A_stuck_wait_is_cut_at_the_deadline_by_the_outer_clock_and_the_late_end_is_dropped()
    {
        var clock = new ManualTimeProvider();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var api = new FakeEventLogApi(_ =>
        {
            entered.Set();
            release.Wait(QueryTest.Patience); // the native call does not honour its timeout
            return NextResult.End;
        });
        var lane = new QueryLane("EventTest", slots: 1);

        var run = Counter(api, lane).CountAsync(Disk, QueryTest.Deadline(clock, 5), CancellationToken.None);
        QueryTest.Reached(entered);
        clock.Advance(TimeSpan.FromSeconds(5));
        var reading = await QueryTest.Done(run);

        Assert.Equal(CheckReason.Timeout, reading.Reason);
        Assert.Equal(1, lane.Held);
        release.Set();
        Assert.True(QueryTest.Eventually(() => lane.Held == 0));
        Assert.True(api.EveryHandleClosedExactlyOnce); // the result set is closed once the call returns
        Assert.Equal(CheckReason.Timeout, (await run).Reason);
    }

    [Fact]
    public async Task When_both_slots_are_held_by_stuck_queries_the_next_one_fails_fast_without_a_worker()
    {
        var counting = new CountingLane(slots: 2);
        var clock = new ManualTimeProvider();
        var entered = new[] { new ManualResetEventSlim(false), new ManualResetEventSlim(false) };
        var release = new ManualResetEventSlim(false);
        var stuckApis = entered.Select(e => new FakeEventLogApi(_ =>
        {
            e.Set();
            release.Wait(QueryTest.Patience);
            return NextResult.End;
        })).ToArray();
        var runs = stuckApis.Select(api => Counter(api, counting.Lane).CountAsync(Disk, QueryTest.Deadline(clock), CancellationToken.None)).ToArray();
        foreach (var e in entered)
        {
            QueryTest.Reached(e);
        }

        var fresh = new FakeEventLogApi(Script(NextResult.End));
        var refused = Counter(fresh, counting.Lane).CountAsync(Disk, QueryTest.Deadline(clock), CancellationToken.None);

        Assert.True(refused.IsCompleted);
        Assert.Equal(CheckReason.Timeout, (await refused).Reason);
        Assert.Empty(fresh.Queries);
        Assert.Equal(2, counting.Started);

        release.Set();
        Assert.All(await QueryTest.Done(Task.WhenAll(runs)), reading => Assert.Equal(0, reading.Value));
    }

    [Fact]
    public async Task A_failure_below_the_wrapper_is_an_error_and_the_slot_comes_back()
    {
        var clock = new ManualTimeProvider();
        var lane = new QueryLane("EventTest", slots: 1);
        var api = new FakeEventLogApi(Script()) { QueryThrows = new InvalidOperationException("boom") };

        var reading = await QueryTest.Done(Counter(api, lane).CountAsync(Disk, QueryTest.Deadline(clock), CancellationToken.None));

        Assert.Equal(CheckReason.Error, reading.Reason);
        Assert.Equal(0, lane.Held);
    }

    [Fact]
    public async Task A_query_that_answers_ok_without_a_handle_is_an_error()
    {
        var clock = new ManualTimeProvider();

        var reading = await QueryTest.Done(new WevtEventCounter(new NoHandleApi(), new QueryLane("EventTest")).CountAsync(Disk, QueryTest.Deadline(clock), CancellationToken.None));

        Assert.Equal(CheckReason.Error, reading.Reason);
    }

    private sealed class NoHandleApi : IEventLogApi
    {
        public int Query(string channel, string xpath, int flags, out nint resultSet)
        {
            resultSet = 0;
            return 0;
        }

        public int Next(nint resultSet, int timeoutMs, Span<nint> events, out int returned) => throw new InvalidOperationException();

        public void Close(nint handle) => throw new InvalidOperationException();
    }
}
