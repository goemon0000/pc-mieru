using System.Diagnostics;
using PcMieru.Core.Collection;
using PcMieru.Core.Reporting;

namespace PcMieru.Core.Tests.Collection;

/// <summary>
/// One sampler, two consumers (B.md section 7): while the front end's loop is running a report reads the loop's history and
/// never samples the source itself, because two callers on one source shorten each other's deltas. Without a running loop
/// (pcmi, MCP, a paused window) the window samples for itself, as before.
/// </summary>
public sealed class ReportFromLoopTests
{
    private static readonly IObservationEngine Engine = new ObservationEngine();

    private static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5);
        }

        return condition();
    }

    private static bool InLoop(SamplerLoop loop, Snapshot snapshot) => loop.History.Any(h => ReferenceEquals(h, snapshot));

    [Fact]
    public async Task A_running_loop_supplies_the_window_and_nobody_else_samples_the_source()
    {
        var source = new ScriptedSource();
        using var loop = new SamplerLoop(source, new TestControl());
        var taken = 0;
        loop.Sampled += _ => Interlocked.Increment(ref taken);
        loop.Start();
        Assert.True(await UntilAsync(() => loop.Latest is not null));

        var (history, summary) = await ObservationRun.RunAsync(source, Engine, TimeSpan.FromMilliseconds(300), null, CancellationToken.None, loop);
        await loop.StopAsync();

        Assert.True(history.Count >= 3, history.Count + " snapshots in a 300 ms window at a 40 ms beat");
        Assert.All(history, s => Assert.True(InLoop(loop, s), "a snapshot that did not come from the loop"));
        Assert.Equal(taken, source.Calls); // every call on the source was the loop's own
        Assert.Equal(1, source.PeakConcurrency);
        Assert.True(history.Zip(history.Skip(1)).All(pair => pair.Second.IntervalMs == pair.First.IntervalMs + 1), "the window is a run of consecutive samples");
        Assert.Equal(history[^1].IntervalMs, summary.IntervalMs); // the summary describes the newest sample
    }

    [Fact]
    public async Task A_loop_that_was_never_started_leaves_the_window_to_sample_for_itself()
    {
        var source = new ScriptedSource();
        using var loop = new SamplerLoop(source, new TestControl());

        var (history, _) = await ObservationRun.RunAsync(source, Engine, TimeSpan.FromMilliseconds(100), null, CancellationToken.None, loop);

        Assert.True(history.Count >= 2);
        Assert.Equal(source.Calls, history.Count);
        Assert.Empty(loop.History);
    }

    [Fact]
    public async Task Without_any_loop_the_window_samples_for_itself_as_before()
    {
        var source = new ScriptedSource();

        var (history, _) = await ObservationRun.RunAsync(source, Engine, TimeSpan.FromMilliseconds(100), null, CancellationToken.None);

        Assert.True(history.Count >= 2);
        Assert.Equal(source.Calls, history.Count);
    }

    [Fact]
    public async Task A_paused_loop_takes_nothing_new_so_the_window_samples_for_itself()
    {
        var source = new ScriptedSource();
        var control = new TestControl();
        using var loop = new SamplerLoop(source, control);
        loop.Start();
        Assert.True(await UntilAsync(() => loop.Latest is not null));
        control.Paused = true;
        await Task.Delay(400); // the loop notices the pause within a quarter second and any sample in flight lands
        var before = source.Calls;

        var (history, _) = await ObservationRun.RunAsync(source, Engine, TimeSpan.FromMilliseconds(100), null, CancellationToken.None, loop);

        Assert.True(history.Count >= 2);
        Assert.Equal(history.Count, source.Calls - before);
        Assert.All(history, s => Assert.False(InLoop(loop, s)));
    }

    [Fact]
    public async Task A_beat_longer_than_the_window_gets_one_sample_asked_for_instead_of_a_long_wait()
    {
        var source = new ScriptedSource();
        using var loop = new SamplerLoop(source, new TestControl { Interval = TimeSpan.FromSeconds(30) });
        loop.Start();
        Assert.True(await UntilAsync(() => loop.Latest is not null));
        var clock = Stopwatch.StartNew();

        var (history, _) = await ObservationRun.RunAsync(source, Engine, TimeSpan.FromMilliseconds(100), null, CancellationToken.None, loop);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), clock.Elapsed.ToString());
        Assert.Equal(2, history.Count); // the one that was latest, and the one that was asked for
        Assert.Equal(2, source.Calls);
        Assert.All(history, s => Assert.True(InLoop(loop, s)));
    }

    [Fact]
    public async Task A_stuck_loop_is_given_the_timeout_and_then_the_window_samples_for_itself()
    {
        var source = new ScriptedSource();
        using var loop = new SamplerLoop(source, new TestControl());
        loop.Start();
        Assert.True(await UntilAsync(() => loop.Latest is not null));
        source.Hold = new TaskCompletionSource();
        Assert.True(await UntilAsync(() => source.Held == 1), "the loop's next sample did not block");
        source.Hold = null; // what the window samples itself must not block
        var clock = Stopwatch.StartNew();

        var (history, _) = await ObservationRun.RunAsync(source, Engine, TimeSpan.FromMilliseconds(50), null, CancellationToken.None, loop, TimeSpan.FromMilliseconds(300));

        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(300), "it did not wait for the loop first");
        Assert.True(history.Count >= 2);
        Assert.All(history, s => Assert.False(InLoop(loop, s)));
    }

    [Fact]
    public async Task The_report_builder_and_insights_hand_the_loop_to_the_window()
    {
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
        var source = new ScriptedSource();
        using var loop = new SamplerLoop(source, new TestControl());
        var taken = 0;
        loop.Sampled += _ => Interlocked.Increment(ref taken);
        var reports = new ReportBuilder(source, services.Observations, services.Hardware, services.StorageHealth, services.Redactor, static () => DateTime.UtcNow, loop);
        var insights = new Insights(source, services.ProcessDetails, services.Hardware, services.StorageHealth, services.Observations, reports, TimeSpan.FromSeconds(1), loop);
        loop.Start();
        Assert.True(await UntilAsync(() => loop.Latest is not null));

        var report = await reports.BuildAsync(new ReportOptions(DurationSec: 0, IncludeHardware: false, IncludeStorageHealth: false));
        var diagnosis = await insights.DiagnoseAsync(TimeSpan.FromMilliseconds(150));
        await loop.StopAsync();

        Assert.NotNull(report.Summary);
        Assert.NotNull(diagnosis.Summary);
        Assert.Equal(taken, source.Calls);
        Assert.Equal(1, source.PeakConcurrency);
    }

    [Fact]
    public void The_services_own_one_loop_on_the_shared_settings_and_stop_it_when_they_go()
    {
        var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
        var loop = services.Sampler;

        Assert.Same(services.Get<ISamplerControl>(), loop.Control);
        Assert.False(loop.Running); // creating the services starts nothing; the GUI starts it

        loop.Start();
        Assert.True(loop.Running);
        services.Dispose();

        Assert.False(loop.Running);
    }
}
