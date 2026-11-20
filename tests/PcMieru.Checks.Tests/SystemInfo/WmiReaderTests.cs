using System.Management;
using System.Runtime.InteropServices;
using PcMieru.Checks.SystemInfo;

namespace PcMieru.Checks.Tests.SystemInfo;

// Two kinds of test live here. The ones that call ReadHere run the question itself on a thread of its own and nothing else: the fake
// may move the clock in the middle of a call, and with no lane there is no caller to give up first, so what is asserted is what the
// READER decided. The ones that call ReadAsync go through the lane and show the lane and the reader working together.
public sealed class WmiReaderTests
{
    private const string Text = "SELECT V FROM Win32_Test";

    private static WmiQuery<int> Query(Func<IWmiRow, int>? map = null) =>
        new(WmiScopes.Cimv2, Text, map ?? (row => (int)row["V"]!));

    private static ManagementWmiReader Reader(FakeWmiExecutor executor, QueryLane? lane = null) =>
        new(executor, lane ?? new QueryLane("WmiTest"));

    /// <summary>The question on a thread of its own (not the test's), without a lane: what the reader itself decides.</summary>
    private static Task<CheckReading<IReadOnlyList<int>>> ReadHere(
        FakeWmiExecutor executor, QueryDeadline deadline, WmiQuery<int>? query = null, CancellationToken ct = default, LaneJob? job = null) =>
        Task.Factory.StartNew(
            () => Reader(executor).ReadOnThisThread(query ?? Query(), deadline, job ?? new LaneJob(), ct),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    [Fact]
    public async Task Every_row_is_mapped_in_order_and_the_list_is_complete()
    {
        var clock = new ManualTimeProvider();
        var executor = new FakeWmiExecutor(FakeWmiExecutor.Rows(3, 1, 2));

        var reading = await QueryTest.Done(Reader(executor).ReadAsync(Query(), QueryTest.Deadline(clock), CancellationToken.None));

        Assert.Equal(new[] { 3, 1, 2 }, reading.Value);
    }

    [Fact]
    public async Task No_rows_is_an_empty_list_and_not_a_reason()
    {
        var clock = new ManualTimeProvider();
        var executor = new FakeWmiExecutor(FakeWmiExecutor.Rows());

        var reading = await QueryTest.Done(Reader(executor).ReadAsync(Query(), QueryTest.Deadline(clock), CancellationToken.None));

        Assert.True(reading.HasValue);
        Assert.Empty(reading.Value);
    }

    [Fact]
    public async Task The_query_goes_to_the_executor_unchanged_with_the_time_that_is_left_and_on_a_worker_thread()
    {
        var clock = new ManualTimeProvider();
        var deadline = QueryTest.Deadline(clock, 5);
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        var executor = new FakeWmiExecutor(FakeWmiExecutor.Rows(1));
        var storage = new WmiQuery<int>(WmiScopes.Storage, "SELECT V FROM MSFT_Test", row => (int)row["V"]!);

        await QueryTest.Done(Reader(executor).ReadAsync(storage, deadline, CancellationToken.None));

        var (scope, text, timeout) = Assert.Single(executor.Calls);
        Assert.Equal(@"\\.\root\microsoft\windows\storage", scope);
        Assert.Equal("SELECT V FROM MSFT_Test", text);
        Assert.Equal(TimeSpan.FromMilliseconds(3500), timeout);
        Assert.NotEqual(Environment.CurrentManagedThreadId, Assert.Single(executor.Threads));
    }

    [Fact]
    public async Task A_connect_that_takes_longer_than_the_deadline_is_a_timeout()
    {
        var clock = new ManualTimeProvider();
        var executor = new FakeWmiExecutor((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(6)); // connecting took 6 s of 5
            throw new TimeoutException();
        });

        var reading = await QueryTest.Done(ReadHere(executor, QueryTest.Deadline(clock)));

        Assert.False(reading.HasValue);
        Assert.Equal(CheckReason.Timeout, reading.Reason);
    }

    [Fact]
    public async Task A_connect_that_returns_late_without_a_row_is_a_timeout()
    {
        var clock = new ManualTimeProvider();
        var executor = new FakeWmiExecutor((_, _) => clock.Advance(TimeSpan.FromSeconds(6)));

        var reading = await QueryTest.Done(ReadHere(executor, QueryTest.Deadline(clock)));

        Assert.Equal(CheckReason.Timeout, reading.Reason);
    }

    [Fact]
    public async Task An_enumeration_that_crosses_the_deadline_gives_a_timeout_and_no_rows()
    {
        var clock = new ManualTimeProvider();
        var mapped = new List<int>();
        var executor = new FakeWmiExecutor((_, visit) =>
        {
            Assert.True(visit(FakeRow.Of(1)));
            clock.Advance(TimeSpan.FromSeconds(6));
            Assert.False(visit(FakeRow.Of(2)), "no row may be taken after the deadline");
        });

        var reading = await QueryTest.Done(ReadHere(
            executor,
            QueryTest.Deadline(clock),
            Query(row =>
            {
                mapped.Add((int)row["V"]!);
                return (int)row["V"]!;
            })));

        Assert.False(reading.HasValue);
        Assert.Equal(CheckReason.Timeout, reading.Reason);
        Assert.Equal(new[] { 1 }, mapped); // the first row was mapped, but a list of one row is not the answer
    }

    [Fact]
    public async Task All_rows_delivered_but_after_the_deadline_is_a_timeout_too()
    {
        var clock = new ManualTimeProvider();
        var executor = new FakeWmiExecutor((_, visit) =>
        {
            Assert.True(visit(FakeRow.Of(1)));
            clock.Advance(TimeSpan.FromSeconds(5)); // the last row came in at the deadline
        });

        var reading = await QueryTest.Done(ReadHere(executor, QueryTest.Deadline(clock)));

        Assert.Equal(CheckReason.Timeout, reading.Reason);
    }

    [Fact]
    public async Task A_caller_that_gave_up_stops_the_enumeration_at_the_next_row_and_no_row_is_mapped()
    {
        var clock = new ManualTimeProvider();
        var call = new LaneCall<int>();
        var mapped = 0;
        var executor = new FakeWmiExecutor((_, visit) =>
        {
            Assert.True(call.TryAbandon());
            Assert.False(visit(FakeRow.Of(1)), "the enumeration must stop at the first row once nobody is waiting");
        });

        var reading = await QueryTest.Done(ReadHere(
            executor,
            QueryTest.Deadline(clock),
            Query(row =>
            {
                mapped++;
                return 1;
            }),
            job: call));

        Assert.Equal(CheckReason.Partial, reading.Reason);
        Assert.Equal(0, mapped);
    }

    [Fact]
    public async Task A_call_that_does_not_return_is_cut_at_the_deadline_by_the_outer_clock()
    {
        var clock = new ManualTimeProvider();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var executor = new FakeWmiExecutor((_, _) =>
        {
            entered.Set();
            release.Wait(QueryTest.Patience); // System.Management ignoring its own timeout
        });
        var lane = new QueryLane("WmiTest", slots: 1);

        var run = Reader(executor, lane).ReadAsync(Query(), QueryTest.Deadline(clock, 5), CancellationToken.None);
        QueryTest.Reached(entered);
        clock.Advance(TimeSpan.FromSeconds(5));
        var reading = await QueryTest.Done(run);

        Assert.Equal(CheckReason.Timeout, reading.Reason);
        Assert.Equal(1, lane.Held);
        release.Set();
        Assert.True(QueryTest.Eventually(() => lane.Held == 0));
    }

    [Fact]
    public async Task A_late_value_is_dropped_and_no_row_is_mapped_after_the_caller_gave_up()
    {
        var clock = new ManualTimeProvider();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var mappedAfterGiveUp = 0;
        var visited = new ManualResetEventSlim(false);
        var executor = new FakeWmiExecutor((_, visit) =>
        {
            entered.Set();
            release.Wait(QueryTest.Patience);
            Assert.False(visit(FakeRow.Of(1)), "the enumeration must stop at the first row once nobody is waiting");
            visited.Set();
        });
        var lane = new QueryLane("WmiTest", slots: 1);

        var run = Reader(executor, lane).ReadAsync(
            Query(row =>
            {
                Interlocked.Increment(ref mappedAfterGiveUp);
                return 1;
            }),
            QueryTest.Deadline(clock, 5),
            CancellationToken.None);
        QueryTest.Reached(entered);
        clock.Advance(TimeSpan.FromSeconds(5));
        var reading = await QueryTest.Done(run);
        release.Set();
        QueryTest.Reached(visited);
        Assert.True(QueryTest.Eventually(() => lane.Held == 0));

        Assert.Equal(CheckReason.Timeout, reading.Reason);
        Assert.Equal(0, Volatile.Read(ref mappedAfterGiveUp));
        Assert.Equal(CheckReason.Timeout, (await run).Reason);
    }

    [Fact]
    public async Task A_mapper_that_throws_is_an_error_and_the_enumeration_stops()
    {
        var clock = new ManualTimeProvider();
        var visitedAll = false;
        var executor = new FakeWmiExecutor((_, visit) =>
        {
            Assert.True(visit(FakeRow.Of(1)));
            Assert.False(visit(FakeRow.Of("not a number")), "the enumeration must stop at the row that did not map");
            visitedAll = true;
        });

        var reading = await QueryTest.Done(ReadHere(executor, QueryTest.Deadline(clock)));

        Assert.False(reading.HasValue);
        Assert.Equal(CheckReason.Error, reading.Reason);
        Assert.True(visitedAll);
    }

    [Fact]
    public async Task A_mapper_that_throws_through_the_lane_is_an_error_and_gives_its_slot_back()
    {
        var clock = new ManualTimeProvider();
        var lane = new QueryLane("WmiTest", slots: 1);
        var executor = new FakeWmiExecutor(FakeWmiExecutor.Rows("not a number"));

        var reading = await QueryTest.Done(Reader(executor, lane).ReadAsync(Query(), QueryTest.Deadline(clock), CancellationToken.None));

        Assert.Equal(CheckReason.Error, reading.Reason);
        Assert.True(QueryTest.Eventually(() => lane.Held == 0));
    }

    [Fact]
    public async Task A_failure_of_the_executor_gives_its_reason_and_nothing_of_its_message()
    {
        var clock = new ManualTimeProvider();

        async Task<CheckReading<IReadOnlyList<int>>> With(Exception ex) =>
            await QueryTest.Done(ReadHere(new FakeWmiExecutor((_, _) => throw ex), QueryTest.Deadline(clock)));

        Assert.Equal(CheckReason.Denied, (await With(new UnauthorizedAccessException("C:\\private"))).Reason);
        Assert.Equal(CheckReason.Timeout, (await With(new TimeoutException())).Reason);
        Assert.Equal(CheckReason.Error, (await With(new InvalidOperationException("machine-name"))).Reason);
        Assert.Equal(CheckReason.Denied, (await With(new COMException("denied", unchecked((int)0x80070005)))).Reason);
        Assert.Equal(CheckReason.Error, (await With(new COMException("other", unchecked((int)0x80004005)))).Reason);
        Assert.Equal(CheckReason.Error, (await With(new ManagementException("a message"))).Reason);
    }

    [Theory]
    [InlineData(ManagementStatus.AccessDenied, CheckReason.Denied)]
    [InlineData(ManagementStatus.InvalidNamespace, CheckReason.Unsupported)]
    [InlineData(ManagementStatus.InvalidClass, CheckReason.Unsupported)]
    [InlineData(ManagementStatus.Timedout, CheckReason.Timeout)]
    [InlineData(ManagementStatus.Failed, CheckReason.Error)]
    [InlineData(ManagementStatus.ProviderFailure, CheckReason.Error)]
    public void A_wmi_status_has_one_reason(ManagementStatus status, CheckReason expected)
    {
        Assert.Equal(expected, WmiFailure.Classify(status));
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_runs_nothing()
    {
        var clock = new ManualTimeProvider();
        var executor = new FakeWmiExecutor(FakeWmiExecutor.Rows(1));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var viaLane = await Reader(executor).ReadAsync(Query(), QueryTest.Deadline(clock), cts.Token);
        var direct = await QueryTest.Done(ReadHere(executor, QueryTest.Deadline(clock), ct: cts.Token));

        Assert.Equal(CheckReason.Partial, viaLane.Reason);
        Assert.Equal(CheckReason.Partial, direct.Reason);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task Cancelling_during_the_enumeration_gives_stopped_and_no_rows()
    {
        var clock = new ManualTimeProvider();
        using var cts = new CancellationTokenSource();
        var executor = new FakeWmiExecutor((_, visit) =>
        {
            Assert.True(visit(FakeRow.Of(1)));
            cts.Cancel();
            Assert.False(visit(FakeRow.Of(2)));
        });

        var reading = await QueryTest.Done(ReadHere(executor, QueryTest.Deadline(clock), ct: cts.Token));

        Assert.False(reading.HasValue);
        Assert.Equal(CheckReason.Partial, reading.Reason);
    }

    [Fact]
    public async Task A_deadline_that_has_already_passed_runs_nothing()
    {
        var clock = new ManualTimeProvider();
        var deadline = QueryTest.Deadline(clock, 5);
        clock.Advance(TimeSpan.FromSeconds(5));
        var executor = new FakeWmiExecutor(FakeWmiExecutor.Rows(1));

        var viaLane = await Reader(executor).ReadAsync(Query(), deadline, CancellationToken.None);
        var direct = await QueryTest.Done(ReadHere(executor, deadline));

        Assert.Equal(CheckReason.Timeout, viaLane.Reason);
        Assert.Equal(CheckReason.Timeout, direct.Reason);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task When_every_slot_is_held_by_stuck_calls_a_new_question_fails_fast_without_a_worker()
    {
        var counting = new CountingLane(slots: 2);
        var clock = new ManualTimeProvider();
        var release = new ManualResetEventSlim(false);
        var entered = new[] { new ManualResetEventSlim(false), new ManualResetEventSlim(false) };
        var stuck = entered.Select(e => new FakeWmiExecutor((_, _) =>
        {
            e.Set();
            release.Wait(QueryTest.Patience);
        })).ToArray();
        var runs = stuck.Select(x => Reader(x, counting.Lane).ReadAsync(Query(), QueryTest.Deadline(clock), CancellationToken.None)).ToArray();
        foreach (var e in entered)
        {
            QueryTest.Reached(e);
        }

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.All(await QueryTest.Done(Task.WhenAll(runs)), reading => Assert.Equal(CheckReason.Timeout, reading.Reason));

        var fresh = new FakeWmiExecutor(FakeWmiExecutor.Rows(1));
        var refused = Reader(fresh, counting.Lane).ReadAsync(Query(), QueryTest.Deadline(clock), CancellationToken.None);

        Assert.True(refused.IsCompleted, "a refused question must not wait for anything");
        Assert.Equal(CheckReason.Timeout, (await refused).Reason);
        Assert.Empty(fresh.Calls);
        Assert.Equal(2, counting.Started);
        Assert.Equal(2, counting.Lane.Held);

        release.Set();
        Assert.True(QueryTest.Eventually(() => counting.Lane.Held == 0));
    }

    [Fact]
    public async Task The_slot_comes_back_only_when_the_stuck_call_returns_and_then_a_question_is_served()
    {
        var counting = new CountingLane(slots: 1);
        var clock = new ManualTimeProvider();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var stuck = new FakeWmiExecutor((_, _) =>
        {
            entered.Set();
            release.Wait(QueryTest.Patience);
        });

        var run = Reader(stuck, counting.Lane).ReadAsync(Query(), QueryTest.Deadline(clock), CancellationToken.None);
        QueryTest.Reached(entered);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(CheckReason.Timeout, (await QueryTest.Done(run)).Reason);

        Assert.Equal(1, counting.Lane.Held); // the caller is long gone, the call is not
        var refused = await Reader(new FakeWmiExecutor(FakeWmiExecutor.Rows(1)), counting.Lane).ReadAsync(Query(), QueryTest.Deadline(clock), CancellationToken.None);
        Assert.Equal(CheckReason.Timeout, refused.Reason);

        release.Set();
        Assert.True(QueryTest.Eventually(() => counting.Lane.Held == 0));
        var served = await QueryTest.Done(Reader(new FakeWmiExecutor(FakeWmiExecutor.Rows(8)), counting.Lane).ReadAsync(Query(), QueryTest.Deadline(clock), CancellationToken.None));
        Assert.Equal(new[] { 8 }, served.Value);
        Assert.Equal(2, counting.Started);
    }
}
