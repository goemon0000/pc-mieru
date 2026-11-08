using System.Diagnostics;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Tests.Collection;

public sealed class GuardedStageTests
{
    private static async Task<bool> WaitUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.ElapsedMilliseconds > milliseconds)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    [Fact]
    public void A_call_that_returns_in_time_gives_its_result()
    {
        var stage = new GuardedStage<string>();

        Assert.True(stage.Start(static () => "done"));
        Assert.True(stage.TryJoin(TimeSpan.FromSeconds(5), out var result, out var failure, out _));

        Assert.Equal("done", result);
        Assert.Null(failure);
    }

    [Fact]
    public void Joining_with_nothing_started_fails_with_a_reason()
    {
        var stage = new GuardedStage<string>();

        Assert.False(stage.TryJoin(TimeSpan.FromMilliseconds(10), out var result, out var failure, out var status));

        Assert.Null(result);
        Assert.Equal("no call was started", failure);
        Assert.Equal(ValueStatus.Error, status);
    }

    [Fact]
    public void An_exception_is_a_failure_with_the_exception_named_and_not_a_throw()
    {
        var stage = new GuardedStage<string>();
        _ = stage.Start(static () => throw new InvalidOperationException("boom"));

        Assert.False(stage.TryJoin(TimeSpan.FromSeconds(5), out var result, out var failure, out var status));

        Assert.Null(result);
        Assert.Equal("InvalidOperationException: boom", failure);
        Assert.Equal(ValueStatus.Error, status);
    }

    [Fact]
    public async Task A_stuck_call_times_out_and_blocks_a_second_call_until_it_finishes()
    {
        using var release = new ManualResetEventSlim(false);
        var stage = new GuardedStage<string>();
        var started = 0;
        Assert.True(stage.Start(() =>
        {
            Interlocked.Increment(ref started);
            release.Wait();
            return "late";
        }));

        var waited = Stopwatch.StartNew();
        Assert.False(stage.TryJoin(TimeSpan.FromMilliseconds(80), out _, out var failure, out var status));
        Assert.InRange(waited.ElapsedMilliseconds, 60, 3000);
        Assert.Equal(ValueStatus.Timeout, status);
        Assert.Contains("did not answer", failure);
        Assert.True(stage.IsBusy);

        // The next tick must not start a second call into the same sampler, and must not wait on the stuck one either.
        Assert.False(stage.Start(static () => "second"));
        var quick = Stopwatch.StartNew();
        Assert.False(stage.TryJoin(TimeSpan.FromSeconds(30), out _, out var again, out var againStatus));
        Assert.True(quick.ElapsedMilliseconds < 1000, "a skipped stage must fail at once");
        Assert.Equal(GuardedStage<string>.StillRunning, again);
        Assert.Equal(ValueStatus.Timeout, againStatus);
        Assert.Equal(1, Volatile.Read(ref started));

        release.Set();
        Assert.True(await WaitUntil(() => !stage.IsBusy));

        Assert.True(stage.Start(static () => "third"));
        Assert.True(stage.TryJoin(TimeSpan.FromSeconds(5), out var third, out _, out _));
        Assert.Equal("third", third);
    }

    [Fact]
    public async Task A_call_that_fails_after_its_tick_gave_up_does_not_surface_as_an_unobserved_exception()
    {
        var unobserved = 0;

        // Only this test's own exception counts: other tests in the process may leave unrelated ones around.
        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerExceptions.Any(static x => x.Message == "after the tick left"))
            {
                Interlocked.Increment(ref unobserved);
            }
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            using var release = new ManualResetEventSlim(false);
            var stage = new GuardedStage<string>();
            _ = stage.Start(() =>
            {
                release.Wait();
                throw new InvalidOperationException("after the tick left");
            });
            Assert.False(stage.TryJoin(TimeSpan.FromMilliseconds(30), out _, out _, out _));

            release.Set();
            Assert.True(await WaitUntil(() => !stage.IsBusy));
            await Task.Delay(50);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }
}

public sealed class GpuSensorCacheTests
{
    private sealed class FakeSensors(Func<CancellationToken, Task<Value<IReadOnlyList<GpuSensorReading>>>> read) : ISensorClient
    {
        public int Calls;

        public Task<Value<IReadOnlyList<GpuSensorReading>>> ReadGpuAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return read(ct);
        }

        public void Dispose()
        {
        }
    }

    private static GpuSensorReading Reading() => new(
        "luid_0x00000000_0x000166ca",
        "NVIDIA GeForce RTX 5070",
        Value.Ok(61.0, "NVML"),
        Value.Ok(90.0, "NVML"),
        Value.Ok(250.0, "NVML"),
        Value.Ok(35.0, "NVML"),
        Value.Ok(2400.0, "NVML"));

    private static Task<Value<IReadOnlyList<GpuSensorReading>>> Ok() =>
        Task.FromResult(Value.Ok<IReadOnlyList<GpuSensorReading>>([Reading()], "SensorHost"));

    private static async Task<Value<IReadOnlyList<GpuSensorReading>>> Settled(GpuSensorCache cache)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var now = cache.Current();
            if (now.Status != ValueStatus.NotCollected || deadline.ElapsedMilliseconds > 5000)
            {
                return now;
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public void The_first_call_never_waits_and_says_nothing_was_read_yet()
    {
        using var client = new FakeSensors(static _ => new TaskCompletionSource<Value<IReadOnlyList<GpuSensorReading>>>().Task);
        var cache = new GpuSensorCache(() => client);

        var waited = Stopwatch.StartNew();
        var first = cache.Current();

        Assert.True(waited.ElapsedMilliseconds < 1000, "a stuck sensor client must never hold up a tick");
        Assert.Equal(ValueStatus.NotCollected, first.Status);
    }

    [Fact]
    public async Task A_good_answer_is_served_from_the_next_call()
    {
        using var client = new FakeSensors(static _ => Ok());
        var cache = new GpuSensorCache(() => client);

        var settled = await Settled(cache);

        Assert.True(settled.IsOk);
        Assert.Equal(61.0, Assert.Single(settled.Data!).TemperatureC.Data);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task Calls_inside_the_refresh_interval_do_not_start_more_reads()
    {
        using var client = new FakeSensors(static _ => Ok());
        var cache = new GpuSensorCache(() => client);
        _ = await Settled(cache);

        for (var i = 0; i < 20; i++)
        {
            _ = cache.Current();
        }

        await Task.Delay(100);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task No_registered_client_is_unsupported_with_a_reason()
    {
        var cache = new GpuSensorCache(static () => null);

        var settled = await Settled(cache);

        Assert.Equal(ValueStatus.Unsupported, settled.Status);
        Assert.Contains("no sensor client", settled.Detail);
    }

    [Fact]
    public async Task A_resolver_that_throws_is_an_error_with_the_exception_named()
    {
        var cache = new GpuSensorCache(static () => throw new InvalidOperationException("registry is not ready"));

        var settled = await Settled(cache);

        Assert.Equal(ValueStatus.Error, settled.Status);
        Assert.Contains("InvalidOperationException", settled.Detail);
        Assert.Contains("registry is not ready", settled.Detail);
    }

    [Fact]
    public async Task A_client_that_throws_is_an_error_and_one_that_is_cancelled_is_a_timeout()
    {
        using var broken = new FakeSensors(static _ => throw new IOException("pipe closed"));
        var errorCache = new GpuSensorCache(() => broken);
        var error = await Settled(errorCache);
        Assert.Equal(ValueStatus.Error, error.Status);
        Assert.Contains("pipe closed", error.Detail);

        using var cancelled = new FakeSensors(static _ => throw new OperationCanceledException());
        var timeoutCache = new GpuSensorCache(() => cancelled);
        var timeout = await Settled(timeoutCache);
        Assert.Equal(ValueStatus.Timeout, timeout.Status);
    }

    [Fact]
    public async Task The_clients_own_failed_answer_is_kept_as_it_is()
    {
        using var client = new FakeSensors(static _ =>
            Task.FromResult(Value.Unsupported<IReadOnlyList<GpuSensorReading>>("SensorHost", "no vendor library found")));
        var cache = new GpuSensorCache(() => client);

        var settled = await Settled(cache);

        Assert.Equal(ValueStatus.Unsupported, settled.Status);
        Assert.Equal("no vendor library found", settled.Detail);
    }

    [Fact]
    public async Task Clear_goes_back_to_not_read_yet_at_once()
    {
        using var client = new FakeSensors(static _ => Ok());
        var cache = new GpuSensorCache(() => client);
        Assert.True((await Settled(cache)).IsOk);

        cache.Clear();
        var after = cache.Current();

        Assert.Equal(ValueStatus.NotCollected, after.Status);
    }
}
