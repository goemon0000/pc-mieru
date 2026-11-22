using System.Diagnostics;
using PcMieru.Checks.Load;
using PcMieru.Core.Collection;

namespace PcMieru.Checks.Tests.Load;

/// <summary>
/// The PDH sample off the control path (DESIGN-A 6.2): a counter call that hangs is a missing sample and holds nothing, and the end of a
/// session never waits for it. The calls here are scripted; nothing reads a counter.
/// </summary>
public sealed class PdhSamplerTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    // ---- the sampler ----

    [Fact]
    public void A_sample_that_arrives_in_time_is_returned_as_it_is()
    {
        using var sampler = new PdhSampler(static () => PdhSamples.Healthy(mhz: 3000), CancellationToken.None);

        var sample = sampler.Take(Bound);

        Assert.Equal(3000, sample.EffectiveMhz);
        Assert.False(sampler.Busy);
    }

    [Fact]
    public void A_sample_that_does_not_arrive_in_time_is_missing_and_never_a_zero()
    {
        var gate = new ManualResetEventSlim(false);
        using var sampler = new PdhSampler(() => Blocked(gate), CancellationToken.None);
        try
        {
            var sample = sampler.Take(Short);

            Assert.Null(sample.EffectiveMhz);
            Assert.Null(sample.UtilizationPercent);
            Assert.True(sampler.Busy);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public void While_a_call_is_stuck_the_next_asks_are_missing_at_once_and_no_second_call_is_made()
    {
        var gate = new ManualResetEventSlim(false);
        var calls = 0;
        using var sampler = new PdhSampler(
            () =>
            {
                Interlocked.Increment(ref calls);
                return Blocked(gate);
            },
            CancellationToken.None);
        try
        {
            sampler.Take(Short);
            var clock = Stopwatch.StartNew();

            for (var i = 0; i < 20; i++)
            {
                var sample = sampler.Take(Bound); // a long wait that must not be waited for: the call is out there already

                Assert.Null(sample.EffectiveMhz);
            }

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"20 asks took {clock.Elapsed}");
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public void A_late_answer_is_dropped_and_the_next_ask_gets_an_answer_of_its_own()
    {
        var gate = new ManualResetEventSlim(false);
        var calls = 0;
        using var sampler = new PdhSampler(
            () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    gate.Wait();
                    return PdhSamples.Healthy(mhz: 1111); // the answer of the call that was given up on
                }

                return PdhSamples.Healthy(mhz: 2222);
            },
            CancellationToken.None);
        Assert.Null(sampler.Take(Short).EffectiveMhz);

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => !sampler.Busy, Bound));
        var next = sampler.Take(Bound);

        Assert.Equal(2222, next.EffectiveMhz); // never the stale 1111
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task A_cancel_cuts_the_wait_short_even_when_the_wait_has_no_end()
    {
        var gate = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();
        using var sampler = new PdhSampler(() => Blocked(gate), cts.Token);
        try
        {
            var taking = Task.Run(() => sampler.Take(Timeout.InfiniteTimeSpan));
            Assert.True(SpinWait.SpinUntil(() => sampler.Busy, Bound));

            await cts.CancelAsync();
            var sample = await taking.WaitAsync(Bound);

            Assert.Null(sample.EffectiveMhz);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public void A_call_that_throws_is_a_missing_sample_and_the_next_ask_still_works()
    {
        var calls = 0;
        using var sampler = new PdhSampler(
            () => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("the fake PDH failed") : PdhSamples.Healthy(mhz: 2500),
            CancellationToken.None);

        var first = sampler.Take(Bound);
        var second = sampler.Take(Bound);

        Assert.Null(first.EffectiveMhz);
        Assert.Equal(2500, second.EffectiveMhz);
    }

    [Fact]
    public void An_out_of_memory_in_the_call_is_not_a_missing_sample_it_reaches_the_caller()
    {
        using var sampler = new PdhSampler(static () => throw new OutOfMemoryException(), CancellationToken.None);

        Assert.Throws<OutOfMemoryException>(() => sampler.Take(Bound));
        Assert.Throws<OutOfMemoryException>(() => sampler.Take(Bound)); // and it stays an error: the run is not trusted after it
    }

    [Fact]
    public void Dispose_does_not_wait_for_a_call_that_is_stuck_and_a_later_ask_is_missing_without_a_call()
    {
        var gate = new ManualResetEventSlim(false);
        var calls = 0;
        var sampler = new PdhSampler(
            () =>
            {
                Interlocked.Increment(ref calls);
                return Blocked(gate);
            },
            CancellationToken.None);
        try
        {
            sampler.Take(Short);

            var clock = Stopwatch.StartNew();
            sampler.Dispose();

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Dispose took {clock.Elapsed}");
            Assert.Null(sampler.Take(Bound).EffectiveMhz);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public void The_sampler_thread_is_a_background_one_so_a_stuck_call_cannot_keep_the_process_alive()
    {
        var gate = new ManualResetEventSlim(false);
        bool? background = null;
        using var sampler = new PdhSampler(
            () =>
            {
                background = Thread.CurrentThread.IsBackground;
                return Blocked(gate);
            },
            CancellationToken.None);
        try
        {
            sampler.Take(Short);
            Assert.True(SpinWait.SpinUntil(() => background is not null, Bound));

            Assert.True(background);
        }
        finally
        {
            gate.Set();
        }
    }

    // ---- the guard of the real session ----

    [Fact]
    public void A_guard_with_nothing_in_flight_closes_the_query_at_once_on_dispose()
    {
        var closes = 0;
        var guard = new PdhSampleGuard(static () => PdhSamples.Healthy(), () => Interlocked.Increment(ref closes));

        Assert.Equal(PdhSamples.Healthy().EffectiveMhz, guard.Sample().EffectiveMhz);
        guard.Dispose();

        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task A_guard_with_a_call_in_flight_does_not_wait_on_dispose_and_the_call_closes_the_query_when_it_returns()
    {
        var gate = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        var closes = 0;
        var guard = new PdhSampleGuard(
            () =>
            {
                entered.Set();
                gate.Wait();
                return PdhSamples.Healthy(mhz: 1234);
            },
            () => Interlocked.Increment(ref closes));
        var sampling = Task.Run(guard.Sample);
        try
        {
            Assert.True(entered.Wait(Bound));

            var clock = Stopwatch.StartNew();
            guard.Dispose();

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Dispose took {clock.Elapsed}");
            Assert.Equal(0, Volatile.Read(ref closes)); // not closed under a call that is still inside the counters
        }
        finally
        {
            gate.Set();
        }

        var sample = await sampling.WaitAsync(Bound);

        Assert.Equal(1234, sample.EffectiveMhz);
        Assert.Equal(1, Volatile.Read(ref closes)); // closed by the call that was in flight, once
    }

    [Fact]
    public void A_sample_asked_for_after_dispose_is_missing_and_makes_no_call()
    {
        var calls = 0;
        var guard = new PdhSampleGuard(
            () =>
            {
                Interlocked.Increment(ref calls);
                return PdhSamples.Healthy();
            },
            static () => { });
        guard.Dispose();

        var sample = guard.Sample();

        Assert.Null(sample.EffectiveMhz);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_guard_that_is_disposed_twice_closes_the_query_once()
    {
        var closes = 0;
        var guard = new PdhSampleGuard(static () => PdhSamples.Healthy(), () => Interlocked.Increment(ref closes));

        guard.Dispose();
        guard.Dispose();

        Assert.Equal(1, closes);
    }

    private static CpuPdhSample Blocked(ManualResetEventSlim gate)
    {
        gate.Wait();
        return PdhSamples.Healthy();
    }
}
