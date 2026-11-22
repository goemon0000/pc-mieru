using PcMieru.Core;
using static PcMieru.Checks.Tests.SeriesKit;

namespace PcMieru.Checks.Tests;

/// <summary>
/// The recorder (DESIGN-B §2, §7.1): one recording at a time, ONE stop path, the wide CSV the writer thread produces, the flush and the free-space
/// rules, the way a cut-off run is found again, and the numbers the summary makes of what was recorded. Everything is in memory (<see cref="MemRunFs"/>,
/// <see cref="FakeSamples"/>, <see cref="FakeClock"/>); a fault is put in through the file-system seam and a wait is a gate, never a sleep.
/// </summary>
public class RunRecorderTests
{
    private const long S = 1_000_000;

    private static RunStore StoreOf(RecHarness h) => new(h.Fs, RecHarness.Root, null);

    private static RecordedGpu G(long seq, long offsetUs, string id = "gpu-a", string name = "Test GPU A", double? temp = 60, long generation = 1, ulong? bits = 0) =>
        SampleKit.Gpu(id: id, name: name, generation: generation, sequence: seq, acquiredUs: RecHarness.At(offsetUs), temp: temp, bits: bits);

    /// <summary>A GPU whose vendor values are not there: the SensorHost restarted.</summary>
    private static RecordedGpu Gone(string id = "gpu-a", string name = "Test GPU A") =>
        SampleKit.Gpu(
            id: id,
            name: name,
            temp: null,
            power: null,
            fan: null,
            clock: null,
            limitC: null,
            bits: null,
            vendor: SourceOrigin.Missing(SourceGap.HostRestart, 1000));

    private static string[] CsvLines(RecHarness h, Guid id) => h.Fs.Text(h.CsvPath(id)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static async Task<RunResult> EndAt(RecHarness h, RunHandle run, long offsetUs, RunStopReason reason = RunStopReason.User)
    {
        h.Clock.Us = RecHarness.At(offsetUs);
        return await h.Recorder.StopAsync(run, reason).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task<RunDetail> Read(RecHarness h, Guid id) =>
        await StoreOf(h).ReadAsync(id, CancellationToken.None) ?? throw new InvalidOperationException("The run is not there.");

    private static void AssertNothingLeft(RecHarness h)
    {
        Assert.Equal(RunActivityState.Idle, h.Recorder.State);
        Assert.False(h.Recorder.IsBusy);
        Assert.Equal(0, h.Samples.LeasesHeld);
        Assert.Equal(0, h.Samples.Subscribers);
        Assert.Empty(h.Fs.Names(RecHarness.Root));
    }

    // ------------------------------------------------------------------ lightness and the one recording

    [Fact]
    public void Nothing_runs_or_is_created_before_a_recording_starts()
    {
        using var h = new RecHarness();
        Assert.Equal(RunActivityState.Idle, h.Recorder.State);
        Assert.False(h.Recorder.IsBusy);
        Assert.Empty(h.Fs.Log);
        Assert.Equal(0, h.Samples.LeasesTaken);
        Assert.Equal(0, h.Samples.Subscribers);
        Assert.IsAssignableFrom<IRunActivity>(h.Recorder);
        Assert.IsAssignableFrom<IRunRecorder>(h.Recorder);
    }

    [Fact]
    public async Task A_run_writes_its_files_and_ends_completed()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        var id = run.RunId;
        Assert.Equal(RecHarness.IdOf(1), id);
        Assert.Equal(RunActivityState.Recording, h.Recorder.State);
        Assert.True(h.Fs.IsLockHeld(h.LockPath(id)));
        var started = h.Header(id);
        Assert.Equal(RunStatus.Recording, started.Status);
        Assert.Null(started.StopReason);
        Assert.Null(started.EndedAtUtc);
        Assert.Equal(0, started.Rows);

        h.Tick(1, 1 * S);
        h.Tick(2, 2 * S);
        h.Tick(3, 3 * S);
        var result = await EndAt(h, run, 3_500_000);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(RunStopReason.User, result.StopReason);
        Assert.Equal(3, result.Rows);
        Assert.Equal(0, result.DroppedTicks);
        Assert.Equal(3_500_000, result.DurationUs);
        Assert.Equal(h.JsonPath(id), result.JsonPath);
        Assert.Equal(h.CsvPath(id), result.CsvPath);
        Assert.Equal(new[] { RunFiles.JsonName(id), RunFiles.CsvName(id) }, h.Fs.Names(RecHarness.Root).ToArray());
        Assert.Equal(0, h.Fs.PendingLength(h.CsvPath(id)));
        Assert.Same(result, await run.Completion);

        var ended = h.Header(id);
        Assert.Equal(RunStatus.Completed, ended.Status);
        Assert.Equal(RunStopReason.User, ended.StopReason);
        Assert.NotNull(ended.EndedAtUtc);
        Assert.Equal(3_500_000, ended.DurationUs);
        Assert.Equal(3, ended.Rows);
        Assert.Equal(4, CsvLines(h, id).Length);
        AssertNothingLeftButTheRun(h);
    }

    private static void AssertNothingLeftButTheRun(RecHarness h)
    {
        Assert.Equal(RunActivityState.Idle, h.Recorder.State);
        Assert.Equal(0, h.Samples.LeasesHeld);
        Assert.Equal(0, h.Samples.Subscribers);
    }

    [Fact]
    public async Task A_second_start_while_one_is_going_is_refused_and_takes_nothing()
    {
        using var h = new RecHarness();
        var first = h.Recorder.StartAsync(CancellationToken.None);
        var second = h.Recorder.StartAsync(CancellationToken.None);
        var refused = await Assert.ThrowsAsync<RunStartException>(() => second);
        Assert.Equal(RunStartFailure.AlreadyRunning, refused.Failure);

        var run = await first;
        Assert.Equal(1, h.Samples.LeasesTaken);
        Assert.Equal(1, h.Samples.Subscribers);

        // While it records the answer is the same, and after the stop a new run starts with a new id.
        var again = await Assert.ThrowsAsync<RunStartException>(() => h.Recorder.StartAsync(CancellationToken.None));
        Assert.Equal(RunStartFailure.AlreadyRunning, again.Failure);
        await h.Recorder.StopAsync(run, RunStopReason.User);
        var next = await h.StartAsync();
        Assert.Equal(RecHarness.IdOf(2), next.RunId);
        Assert.Equal(2, h.Samples.LeasesTaken);
    }

    [Fact]
    public async Task A_sampler_that_is_gone_means_no_lease_and_no_files()
    {
        using var h = new RecHarness();
        h.Samples.LeaseFault = new ObjectDisposedException("SamplerLoop");
        var ex = await Assert.ThrowsAsync<RunStartException>(() => h.Recorder.StartAsync(CancellationToken.None));
        Assert.Equal(RunStartFailure.SamplerUnavailable, ex.Failure);
        Assert.Equal(0, h.Samples.LeasesTaken);
        AssertNothingLeft(h);

        h.Samples.LeaseFault = null;
        var run = await h.StartAsync();
        Assert.Equal(1, h.Samples.LeasesHeld);
        await h.Recorder.StopAsync(run, RunStopReason.User);
    }

    [Theory]
    [InlineData("CreateDirectory", "", 0)]
    [InlineData("TryLock", ".lock", 1)]
    [InlineData("CreateNew", ".csv", 1)]
    [InlineData("Create", ".json.tmp", 1)]
    [InlineData("FlushToDisk", ".json.tmp", 1)]
    [InlineData("ReplaceAtomic", ".json", 1)]
    public async Task A_start_that_fails_returns_the_lease_and_leaves_no_file(string op, string suffix, int leasesTaken)
    {
        using var h = new RecHarness();
        h.Fs.Fail(op, Kit.DiskFull, name => name.EndsWith(suffix, StringComparison.Ordinal));
        var ex = await Assert.ThrowsAsync<RunStartException>(() => h.Recorder.StartAsync(CancellationToken.None));
        Assert.Equal(RunStartFailure.IoError, ex.Failure);
        Assert.Equal(leasesTaken, h.Samples.LeasesTaken);
        Assert.Equal(leasesTaken, h.Samples.LeaseDisposals);
        AssertNothingLeft(h);

        // The failure left nothing that stops the next one.
        h.Fs.ClearFaults();
        var run = await h.StartAsync();
        var result = await h.Recorder.StopAsync(run, RunStopReason.User);
        Assert.Equal(RunStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Less_than_200_MB_free_refuses_a_start_and_exactly_200_MB_or_unknown_starts()
    {
        using var h = new RecHarness();
        h.Fs.FreeBytes = RunRecorder.DefaultMinFreeBytes - 1;
        var ex = await Assert.ThrowsAsync<RunStartException>(() => h.Recorder.StartAsync(CancellationToken.None));
        Assert.Equal(RunStartFailure.LowSpace, ex.Failure);
        Assert.Equal(0, h.Samples.LeasesTaken);
        AssertNothingLeft(h);

        h.Fs.FreeBytes = RunRecorder.DefaultMinFreeBytes;
        var exact = await h.StartAsync();
        await h.Recorder.StopAsync(exact, RunStopReason.User);

        h.Fs.FreeBytes = null;
        var unknown = await h.StartAsync();
        await h.Recorder.StopAsync(unknown, RunStopReason.User);
    }

    // ------------------------------------------------------------------ the one stop path

    [Fact]
    public async Task Stopping_is_seen_from_outside_and_the_lease_and_the_subscription_are_already_back()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        using var gate = new FsGate((op, name) => op == "FlushToDisk" && name.EndsWith(".csv", StringComparison.Ordinal));
        h.Fs.Hook = gate.Hook;

        // A run without a row: the first CSV flush is the final one, so the stop is held after it unsubscribed and before it released the lock.
        var stop = h.Recorder.StopAsync(run, RunStopReason.User);
        gate.WaitEntered();

        Assert.Equal(RunActivityState.Stopping, h.Recorder.State);
        Assert.True(h.Recorder.IsBusy);
        Assert.Equal(0, h.Samples.LeasesHeld);
        Assert.Equal(0, h.Samples.Subscribers);
        Assert.True(h.Fs.IsLockHeld(h.LockPath(run.RunId)));
        var refused = await Assert.ThrowsAsync<RunStartException>(() => h.Recorder.StartAsync(CancellationToken.None));
        Assert.Equal(RunStartFailure.AlreadyRunning, refused.Failure);

        gate.Release();
        var result = await stop;
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(RunActivityState.Idle, h.Recorder.State);
        Assert.False(h.Recorder.IsBusy);
        Assert.False(h.Fs.IsLockHeld(h.LockPath(run.RunId)));
    }

    [Fact]
    public async Task After_a_stop_the_lease_is_returned_once_and_a_late_sample_changes_nothing()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        await EndAt(h, run, 2 * S);

        Assert.Equal(1, h.Samples.LeaseDisposals);
        Assert.Equal(0, h.Samples.LeasesHeld);
        Assert.Equal(0, h.Samples.Subscribers);
        Assert.False(h.Fs.IsLockHeld(h.LockPath(run.RunId)));
        Assert.Equal(RunActivityState.Idle, h.Recorder.State);

        var before = h.Fs.Text(h.CsvPath(run.RunId));
        h.Tick(2, 3 * S);
        Assert.Equal(before, h.Fs.Text(h.CsvPath(run.RunId)));
    }

    [Fact]
    public async Task Two_stops_wait_for_the_same_result_and_the_first_reason_wins()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        var first = h.Recorder.StopAsync(run, RunStopReason.User);
        var second = h.Recorder.StopAsync(run, RunStopReason.WindowClose);
        Assert.Same(first, second);

        var result = await first;
        Assert.Equal(RunStopReason.User, result.StopReason);
        Assert.Same(result, await h.Recorder.StopAsync(run, RunStopReason.ProcessExit));
        Assert.Same(result, await run.Completion);
        Assert.Equal(1, h.Samples.LeaseDisposals);
        Assert.Equal(RunStopReason.User, h.Header(run.RunId).StopReason);
    }

    [Fact]
    public async Task Two_stops_from_two_threads_agree()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        var a = Task.Run(() => h.Recorder.StopAsync(run, RunStopReason.User));
        var b = Task.Run(() => h.Recorder.StopAsync(run, RunStopReason.WindowClose));
        var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Same(results[0], results[1]);
        Assert.Equal(1, h.Samples.LeaseDisposals);
        Assert.Equal(0, h.Samples.LeasesHeld);
        Assert.Equal(RunActivityState.Idle, h.Recorder.State);
    }

    [Fact]
    public async Task A_stop_that_arrives_while_the_run_is_being_prepared_waits_for_it_and_then_stops_it()
    {
        using var h = new RecHarness();
        using var gate = new FsGate((op, name) => op == "Create" && name.EndsWith(".json.tmp", StringComparison.Ordinal));
        h.Fs.Hook = gate.Hook;
        var starting = h.Recorder.StartAsync(CancellationToken.None);
        gate.WaitEntered();
        Assert.Equal(RunActivityState.Preparing, h.Recorder.State);
        Assert.True(h.Recorder.IsBusy);

        var stopping = h.Recorder.StopCurrentAsync(RunStopReason.WindowClose);
        Assert.False(stopping.IsCompleted);
        var refused = await Assert.ThrowsAsync<RunStartException>(() => h.Recorder.StartAsync(CancellationToken.None));
        Assert.Equal(RunStartFailure.AlreadyRunning, refused.Failure);

        gate.Release();
        var run = await starting;
        var result = await stopping;
        Assert.NotNull(result);
        Assert.Equal(run.RunId, result.RunId);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(RunStopReason.WindowClose, result.StopReason);
        Assert.Equal(0, result.Rows);
        Assert.Equal(0, h.Samples.LeasesHeld);
        Assert.Equal(0, h.Samples.Subscribers);
        Assert.Equal(RunActivityState.Idle, h.Recorder.State);
    }

    [Fact]
    public async Task A_stop_while_the_start_fails_has_nothing_to_stop()
    {
        using var h = new RecHarness();
        using var gate = new FsGate((op, name) => op == "TryLock" && name.EndsWith(".lock", StringComparison.Ordinal), Kit.DiskFull);
        h.Fs.Hook = gate.Hook;
        var starting = h.Recorder.StartAsync(CancellationToken.None);
        gate.WaitEntered();
        var stopping = h.Recorder.StopCurrentAsync(RunStopReason.WindowClose);

        gate.Release();
        var ex = await Assert.ThrowsAsync<RunStartException>(() => starting);
        Assert.Equal(RunStartFailure.IoError, ex.Failure);
        Assert.Null(await stopping);
        AssertNothingLeft(h);
    }

    [Fact]
    public async Task StopCurrent_gives_null_when_nothing_is_going_and_the_result_when_a_run_is()
    {
        using var h = new RecHarness();
        Assert.Null(await h.Recorder.StopCurrentAsync(RunStopReason.ProcessExit));

        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        var result = await h.Recorder.StopCurrentAsync(RunStopReason.WindowClose);
        Assert.NotNull(result);
        Assert.Equal(run.RunId, result.RunId);
        Assert.Equal(RunStopReason.WindowClose, result.StopReason);
        Assert.Null(await h.Recorder.StopCurrentAsync(RunStopReason.ProcessExit));
    }

    [Fact]
    public async Task A_stop_or_a_mark_that_the_caller_may_not_make_is_refused()
    {
        using var h = new RecHarness();
        using var other = new RecHarness();
        var run = await h.StartAsync();
        var foreign = await other.StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Recorder.MarkAsync(run, MarkerKind.Warning, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => { _ = h.Recorder.StopAsync(run, RunStopReason.Unknown); });
        Assert.Throws<ArgumentException>(() => { _ = h.Recorder.StopAsync(foreign, RunStopReason.User); });
        await Assert.ThrowsAsync<ArgumentException>(() => h.Recorder.MarkAsync(foreign, MarkerKind.Mark, CancellationToken.None));

        // None of that stopped the run.
        Assert.Equal(RunActivityState.Recording, h.Recorder.State);
        await h.Recorder.StopAsync(run, RunStopReason.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Recorder.MarkAsync(run, MarkerKind.Mark, CancellationToken.None));
    }

    [Theory]
    [InlineData(RunStopReason.User, "user")]
    [InlineData(RunStopReason.WindowClose, "window_close")]
    [InlineData(RunStopReason.ProcessExit, "process_exit")]
    public async Task The_reason_of_a_stop_is_written_to_the_json(RunStopReason reason, string code)
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        var result = await EndAt(h, run, 2 * S, reason);
        Assert.Equal(reason, result.StopReason);
        Assert.Equal(code, RunCodes.Of(reason));
        Assert.Equal(reason, h.Header(run.RunId).StopReason);
        Assert.Contains("\"" + code + "\"", h.Fs.Text(h.JsonPath(run.RunId)), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the seconds a run observed

    [Fact]
    public async Task Minimizing_the_sampler_for_half_a_minute_leaves_that_time_missing()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        h.Tick(2, 2 * S);
        h.Tick(3, 3 * S);
        h.Tick(4, 33 * S); // the sampler was paused: nothing came for 30 s
        var result = await EndAt(h, run, 34 * S);
        Assert.Equal(4, result.Rows);

        var summary = (await Read(h, run.RunId)).Summary;
        var cpu = summary.Metric("core.cpu_usage_pct");
        Assert.NotNull(cpu);
        Assert.Equal(34.0, summary.DurationSec, 6);
        Assert.Equal(3.0, cpu.ObservedSeconds, 6);
        Assert.Equal(31.0, cpu.MissingSeconds, 6);
        Assert.Equal(4, cpu.NewPoints);
        Assert.True(summary.HasNote("gaps"));
    }

    [Fact]
    public async Task A_host_restart_is_missing_with_its_reason_and_the_new_generation_is_fresh_again()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S, gpus: [G(1, 1 * S)]);
        h.Tick(2, 2 * S, gpus: [G(2, 2 * S)]);
        h.Tick(3, 3 * S, gpus: [Gone()]);
        h.Tick(4, 4 * S, gpus: [Gone()]);
        h.Tick(5, 5 * S, gpus: [G(1, 5 * S, generation: 2)]);
        h.Tick(6, 6 * S, gpus: [G(2, 6 * S, generation: 2)]);
        var result = await EndAt(h, run, 7 * S);
        Assert.Equal(6, result.Rows);

        var lines = CsvLines(h, run.RunId);
        Assert.Contains("missing,host_restart", lines[3], StringComparison.Ordinal);
        Assert.Contains("missing,host_restart", lines[4], StringComparison.Ordinal);
        Assert.Contains("g2-1,", lines[5], StringComparison.Ordinal);
        Assert.DoesNotContain("missing", lines[5], StringComparison.Ordinal);

        var detail = await Read(h, run.RunId);
        var temp = detail.Summary.Metric("gpu1.temp_c");
        Assert.NotNull(temp);
        Assert.Equal(4, temp.NewPoints);
        Assert.Equal(4.0, temp.ObservedSeconds, 6);
        Assert.Equal(3.0, temp.MissingSeconds, 6);

        var counts = RunCsvReader.Read(h.Fs.DurableBytes(h.CsvPath(run.RunId)), h.Header(run.RunId).Metrics).Sources["gpu1"];
        Assert.Equal(4, counts.Fresh);
        Assert.Equal(2, counts.Missing);
        Assert.Contains("host_restart", counts.Reasons);
    }

    [Fact]
    public async Task A_frame_that_comes_again_is_a_repeat_and_adds_no_new_value()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S, gpus: [G(1, 1 * S)]);
        h.Tick(2, 2 * S, gpus: [G(1, 1 * S)]);
        h.Tick(3, 3 * S, gpus: [G(1, 1 * S)]);
        h.Tick(4, 4 * S, gpus: [G(2, 3_500_000)]);
        await EndAt(h, run, 5 * S);

        var lines = CsvLines(h, run.RunId);
        Assert.Contains(",fresh,", lines[1], StringComparison.Ordinal);
        Assert.Contains("g1-1,", lines[2], StringComparison.Ordinal);
        Assert.Contains(",repeat,", lines[2], StringComparison.Ordinal);
        Assert.Contains(",repeat,", lines[3], StringComparison.Ordinal);
        Assert.Contains("g1-2,", lines[4], StringComparison.Ordinal);

        var data = RunCsvReader.Read(h.Fs.DurableBytes(h.CsvPath(run.RunId)), h.Header(run.RunId).Metrics);
        Assert.Equal(2, data.Sources["gpu1"].Fresh);
        Assert.Equal(2, data.Sources["gpu1"].Repeat);
        Assert.Equal(4, data.Sources["core"].Fresh);

        var temp = (await Read(h, run.RunId)).Summary.Metric("gpu1.temp_c");
        Assert.NotNull(temp);
        Assert.Equal(2, temp.NewPoints);
        Assert.Equal(3.5, temp.ObservedSeconds, 6);
        Assert.Equal(1.5, temp.MissingSeconds, 6);
    }

    [Fact]
    public async Task A_gap_of_exactly_three_periods_still_counts_and_a_microsecond_more_does_not()
    {
        using var exact = new RecHarness();
        var runExact = await exact.StartAsync();
        exact.Tick(1, 1 * S);
        exact.Tick(2, 4 * S);
        await EndAt(exact, runExact, 5 * S);
        var atThree = (await Read(exact, runExact.RunId)).Summary.Metric("core.cpu_usage_pct");
        Assert.NotNull(atThree);
        Assert.Equal(4.0, atThree.ObservedSeconds, 6); // 1 -> 4 is 3 s = 3P: it counts; the last value gets the 1 s that was left
        Assert.Equal(1.0, atThree.MissingSeconds, 6);

        using var over = new RecHarness();
        var runOver = await over.StartAsync();
        over.Tick(1, 1 * S);
        over.Tick(2, (4 * S) + 1);
        await EndAt(over, runOver, 5 * S);
        var overThree = (await Read(over, runOver.RunId)).Summary.Metric("core.cpu_usage_pct");
        Assert.NotNull(overThree);
        Assert.Equal(0.999999, overThree.ObservedSeconds, 6); // the first value's interval is over 3P: only the last value's 0.999999 s is known
        Assert.Equal(4.000001, overThree.MissingSeconds, 6);
    }

    [Theory]
    [InlineData(2000, 4)]
    [InlineData(1000, 2)]
    public async Task The_period_the_sampler_reports_is_the_period_the_summary_uses(int periodMs, int stepSeconds)
    {
        // Ticks every <stepSeconds> s with P = <periodMs> ms: a step of up to 3P is observed time; the run's end gives the last value one period.
        var stepUs = stepSeconds * S;
        var periodUs = periodMs * 1000L;
        using var h = new RecHarness();
        var run = await h.StartAsync();
        for (var n = 1; n <= 4; n++)
        {
            h.Clock.Us = RecHarness.At(n * stepUs);
            h.Samples.Raise(SampleKit.Sample(n, RecHarness.At(n * stepUs), periodMs: periodMs));
        }

        await EndAt(h, run, (5 * stepUs));
        var cpu = (await Read(h, run.RunId)).Summary.Metric("core.cpu_usage_pct");
        Assert.NotNull(cpu);
        var step = stepUs / (double)S;
        var last = Math.Min(periodUs, stepUs) / (double)S;
        Assert.Equal((3 * step) + last, cpu.ObservedSeconds, 6);
        Assert.Equal((5 * step) - ((3 * step) + last), cpu.MissingSeconds, 6);
        Assert.Contains(periodMs.ToString(System.Globalization.CultureInfo.InvariantCulture), CsvLines(h, run.RunId)[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_and_the_last_interval_are_the_time_to_the_first_value_and_at_most_one_period_after_the_last()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        h.Tick(2, 2 * S);
        h.Tick(3, 3 * S);
        var result = await EndAt(h, run, 3_400_000);
        Assert.Equal(3_400_000, result.DurationUs);

        var summary = (await Read(h, run.RunId)).Summary;
        var cpu = summary.Metric("core.cpu_usage_pct");
        Assert.NotNull(cpu);
        Assert.Equal(3.4, summary.DurationSec, 6);
        Assert.Equal(2.4, cpu.ObservedSeconds, 6); // the first second is missing; the last value gets the 0.4 s that remain
        Assert.Equal(1.0, cpu.MissingSeconds, 6);
    }

    [Fact]
    public async Task A_clock_that_goes_back_does_not_reorder_or_lose_rows()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        var wall = SampleKit.Wall;
        h.Clock.Us = RecHarness.At(1 * S);
        h.Samples.Raise(SampleKit.Sample(1, RecHarness.At(1 * S), taken: wall.AddSeconds(1)));
        h.Clock.Us = RecHarness.At(2 * S);
        h.Samples.Raise(SampleKit.Sample(2, RecHarness.At(2 * S), taken: wall.AddSeconds(-3600))); // the wall clock was set back an hour
        h.Clock.Us = RecHarness.At(3 * S);
        h.Samples.Raise(SampleKit.Sample(3, RecHarness.At(3 * S), taken: wall.AddSeconds(3)));
        var result = await EndAt(h, run, 4 * S);
        Assert.Equal(3, result.Rows);

        var lines = CsvLines(h, run.RunId);
        Assert.StartsWith("1000000,", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("2000000,", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("3000000,", lines[3], StringComparison.Ordinal);
        var cpu = (await Read(h, run.RunId)).Summary.Metric("core.cpu_usage_pct");
        Assert.NotNull(cpu);
        Assert.Equal(3.0, cpu.ObservedSeconds, 6);
    }

    // ------------------------------------------------------------------ the backlog, replays and dropped ticks

    [Fact]
    public async Task A_sample_from_before_the_start_is_not_part_of_the_run()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();

        // The sampler's queue still held two samples from before the subscription: they are dropped by the time they were acquired, and not counted.
        h.Samples.Raise(SampleKit.Sample(1, RecHarness.StartUs - 2 * S));
        h.Samples.Raise(SampleKit.Sample(2, RecHarness.StartUs - 1));
        h.Tick(3, 1 * S);
        var result = await EndAt(h, run, 2 * S);

        Assert.Equal(1, result.Rows);
        Assert.Equal(0, result.DroppedTicks);
        var lines = CsvLines(h, run.RunId);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("1000000,", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sample_acquired_exactly_at_the_start_is_the_first_row_at_offset_zero()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Samples.Raise(SampleKit.Sample(1, RecHarness.StartUs));
        h.Tick(2, 1 * S);
        var result = await EndAt(h, run, 2 * S);
        Assert.Equal(2, result.Rows);
        Assert.StartsWith("0,", CsvLines(h, run.RunId)[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replayed_sequence_is_dropped_without_counting()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        h.Tick(1, 2 * S); // the same sequence again, at a later time
        h.Tick(1, 1 * S); // and again at the same time
        h.Tick(2, 3 * S);
        var result = await EndAt(h, run, 4 * S);
        Assert.Equal(2, result.Rows);
        Assert.Equal(0, result.DroppedTicks);
        Assert.Equal(3, CsvLines(h, run.RunId).Length);
    }

    [Fact]
    public async Task A_new_sequence_at_a_time_that_does_not_advance_gets_no_row_and_is_counted_as_one_dropped_tick_when_the_next_arrives()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        h.Tick(2, 1 * S); // a new tick whose acquire time is not after the last row: no row (the time axis must increase), the tick is lost
        h.Tick(3, 3 * S);
        var result = await EndAt(h, run, 4 * S);
        Assert.Equal(2, result.Rows);
        Assert.Equal(1, result.DroppedTicks);
        Assert.Equal(3, CsvLines(h, run.RunId).Length);
    }

    [Fact]
    public async Task A_jump_in_the_sequence_is_counted_as_dropped_ticks_and_no_row_is_invented()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        h.Tick(2, 2 * S);
        h.Tick(5, 5 * S); // 3 and 4 never arrived
        var result = await EndAt(h, run, 6 * S);

        Assert.Equal(3, result.Rows);
        Assert.Equal(2, result.DroppedTicks);
        Assert.Equal(4, CsvLines(h, run.RunId).Length);
        var ended = h.Header(run.RunId);
        Assert.Equal(2, ended.DroppedTicks);
        Assert.Equal(3, ended.Rows);
        Assert.Equal(2, (await Read(h, run.RunId)).Summary.DroppedTicks);
        Assert.True((await Read(h, run.RunId)).Summary.HasNote("dropped-ticks"));
    }

    [Fact]
    public async Task When_the_writer_cannot_keep_up_the_oldest_waiting_sample_goes_and_it_shows_as_dropped_ticks()
    {
        using var h = new RecHarness(queueCapacity: 2);
        using var gate = new FsGate((op, name) => op == "Write" && name.EndsWith(".csv", StringComparison.Ordinal));
        var run = await h.StartAsync();
        h.Fs.Hook = gate.Hook;

        h.Tick(1, 1 * S);
        gate.WaitEntered(); // the writer is inside the first row; the handler below must not wait for it
        for (var n = 2; n <= 6; n++)
        {
            h.Tick(n, n * S);
        }

        gate.Release();
        var result = await EndAt(h, run, 7 * S);

        // The queue held two: 5 and 6 are what is left of 2..6, so 2, 3 and 4 were dropped and the jump from 1 to 5 counts three.
        Assert.Equal(3, result.Rows);
        Assert.Equal(3, result.DroppedTicks);
        var lines = CsvLines(h, run.RunId);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("1000000,", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("5000000,", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("6000000,", lines[3], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the columns

    [Fact]
    public async Task The_first_row_fixes_the_columns_and_the_json_lists_them_with_the_gpu_behind_each_source()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        Assert.Empty(h.Header(run.RunId).Metrics);

        h.Tick(1, 1 * S, gpus: [G(1, 1 * S)]);
        await h.Barrier(run);

        var header = h.Header(run.RunId);
        Assert.Contains(header.Metrics, m => m.Key == "core.cpu_usage_pct" && m.Source == "core" && m.Kind == RunMetricKind.Number);
        Assert.Contains(header.Metrics, m => m.Key == "gpu1.temp_c" && m.Source == "gpu1");
        Assert.Contains(header.Metrics, m => m.Key == "gpu1.limit_reasons" && m.Kind == RunMetricKind.Bitmask);
        Assert.Contains(header.Sources, s => s.Source == "gpu1" && s.DeviceId == "gpu-a" && s.Name == "Test GPU A");
        Assert.Contains(header.Sources, s => s.Source == "core" && s.DeviceId is null);
        Assert.StartsWith("offsetUs,utc,core.frame,", CsvLines(h, run.RunId)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gpu_that_appears_after_the_columns_are_fixed_gets_no_column_and_one_warning()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S, gpus: [G(1, 1 * S)]);
        h.Tick(2, 2 * S, gpus: [G(2, 2 * S), G(1, 2 * S, id: "gpu-b", name: "Test GPU B")]);
        h.Tick(3, 3 * S, gpus: [G(3, 3 * S), G(2, 3 * S, id: "gpu-b", name: "Test GPU B")]);
        await h.Barrier(run);

        var header = h.Header(run.RunId);
        var warning = Assert.Single(header.Markers, m => m.Kind == MarkerKind.Warning);
        Assert.Equal("late_gpu:Test GPU B", warning.Detail);
        Assert.Equal(2 * S, warning.OffsetUs);
        Assert.DoesNotContain(header.Sources, s => s.Source == "gpu2");
        Assert.DoesNotContain("gpu2.", CsvLines(h, run.RunId)[0], StringComparison.Ordinal);
        await EndAt(h, run, 4 * S);
    }

    [Fact]
    public async Task A_bench_pair_in_order_with_the_samples_makes_one_section_in_the_summary()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S, cpu: 10);
        h.Tick(2, 2 * S, cpu: 90);
        var start = await h.Recorder.MarkAsync(run, MarkerKind.BenchStart, CancellationToken.None);
        h.Tick(3, 3 * S, cpu: 90);
        h.Tick(4, 4 * S, cpu: 90);
        h.Tick(5, 5 * S, cpu: 10);
        var end = await h.Recorder.MarkAsync(run, MarkerKind.BenchEnd, CancellationToken.None);
        h.Tick(6, 6 * S, cpu: 10);
        var result = await EndAt(h, run, 7 * S);

        Assert.Equal(MarkerKind.BenchStart, start.Kind);
        Assert.Equal(2 * S, start.OffsetUs);
        Assert.Equal(5 * S, end.OffsetUs);
        var markers = h.Header(run.RunId).Markers;
        Assert.Equal(new[] { MarkerKind.BenchStart, MarkerKind.BenchEnd }, markers.Select(m => m.Kind).ToArray());
        Assert.Equal(6, result.Rows);

        var section = Assert.Single((await Read(h, run.RunId)).Summary.Sections);
        Assert.Equal("bench", section.Kind);
        Assert.False(section.Open);
        Assert.Equal(2.0, section.StartSec, 6);
        Assert.Equal(5.0, section.EndSec, 6);
        var cpu = Assert.Single(section.Metrics, m => m.Key == "core.cpu_usage_pct");
        Assert.Equal(90.0, cpu.Avg, 6);
        Assert.Equal(3.0, cpu.ObservedSeconds, 6);
    }

    [Fact]
    public async Task A_bench_start_without_its_end_is_an_open_section_to_the_end_of_the_run()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        await h.Recorder.MarkAsync(run, MarkerKind.BenchStart, CancellationToken.None);
        h.Tick(2, 2 * S);
        await EndAt(h, run, 4 * S);

        var section = Assert.Single((await Read(h, run.RunId)).Summary.Sections);
        Assert.True(section.Open);
        Assert.Equal(4.0, section.EndSec, 6);
    }

    // ------------------------------------------------------------------ durability

    [Fact]
    public async Task The_first_row_is_forced_to_the_disk_at_once_and_a_later_flush_waits_for_ten_seconds()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        var csv = h.CsvPath(run.RunId);

        h.Tick(1, 1 * S);
        await h.Barrier(run);
        Assert.Equal(0, h.Fs.PendingLength(csv));
        Assert.NotEmpty(h.Fs.DurableBytes(csv));
        var durableAfterFirst = h.Fs.DurableBytes(csv).Length;

        // 9 s after the last flush: written, not yet forced to the disk.
        h.Tick(2, 10 * S);
        await h.Barrier(run);
        Assert.True(h.Fs.PendingLength(csv) > 0);
        Assert.Equal(durableAfterFirst, h.Fs.DurableBytes(csv).Length);

        // Exactly 10 s after it: flushed.
        h.Tick(3, 11 * S);
        await h.Barrier(run);
        Assert.Equal(0, h.Fs.PendingLength(csv));
        Assert.True(h.Fs.DurableBytes(csv).Length > durableAfterFirst);
        Assert.Equal(RunStatus.Recording, h.Header(run.RunId).Status);
    }

    [Fact]
    public async Task A_power_cut_keeps_the_flushed_rows_and_the_run_is_found_again_as_interrupted()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        var id = run.RunId;
        h.Tick(1, 1 * S); // flushed at once
        h.Tick(2, 2 * S);
        h.Tick(3, 3 * S); // written, not flushed
        await h.Barrier(run);

        h.Fs.PowerCut();
        Assert.Equal(2, CsvLines(h, id).Length);
        Assert.Equal(RunStatus.Recording, h.Header(id).Status);

        // The next recorder (the next start of the program) finds the run without a holder and takes it over.
        using var next = new RecHarness(firstId: 2);
        next.Fs.Put(next.CsvPath(id), h.Fs.DurableBytes(h.CsvPath(id)));
        next.Fs.Put(next.JsonPath(id), h.Fs.DurableBytes(h.JsonPath(id)));
        var started = await next.StartAsync();
        var found = next.Header(id);
        Assert.Equal(RunStatus.Interrupted, found.Status);
        Assert.Equal(RunStopReason.Unknown, found.StopReason);
        Assert.Null(found.EndedAtUtc);
        Assert.Equal(1, found.Rows);

        // One row is on the disk, but the Barrier's mark (at 3 s) was saved in the JSON, so the run is known to have lived until then.
        Assert.Equal(3 * S, found.DurationUs);
        await next.Recorder.StopAsync(started, RunStopReason.User);
    }

    [Fact]
    public async Task A_stop_forces_everything_to_the_disk()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        h.Tick(2, 2 * S);
        h.Tick(3, 3 * S);
        await EndAt(h, run, 4 * S);
        Assert.Equal(0, h.Fs.PendingLength(h.CsvPath(run.RunId)));
        Assert.Equal(h.Fs.Text(h.CsvPath(run.RunId)), System.Text.Encoding.UTF8.GetString(h.Fs.DurableBytes(h.CsvPath(run.RunId))));
        Assert.Equal(RunStatus.Completed, h.Header(run.RunId).Status);
        Assert.True(h.Fs.Count("FlushToDisk", ".csv") >= 2);
    }

    [Fact]
    public async Task Marks_are_saved_in_the_json_in_order_and_each_one_is_on_the_disk_when_its_call_returns()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        await h.Recorder.MarkAsync(run, MarkerKind.StressStart, CancellationToken.None);
        Assert.Equal(new[] { MarkerKind.StressStart }, h.Header(run.RunId).Markers.Select(m => m.Kind).ToArray());
        h.Tick(2, 2 * S);
        await h.Recorder.MarkAsync(run, MarkerKind.StressEnd, CancellationToken.None);
        Assert.Equal(new[] { MarkerKind.StressStart, MarkerKind.StressEnd }, h.Header(run.RunId).Markers.Select(m => m.Kind).ToArray());
        await EndAt(h, run, 3 * S);
    }

    [Fact]
    public async Task The_free_space_is_checked_at_every_flush_and_below_200_MB_the_run_stops_with_low_space()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        h.Tick(2, 2 * S);
        await h.Barrier(run);
        Assert.Equal(RunActivityState.Recording, h.Recorder.State);

        h.Fs.FreeBytes = RunRecorder.DefaultMinFreeBytes - 1;
        h.Tick(3, 12 * S); // 11 s after the last flush: a flush is due, and it finds the disk nearly full
        var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RunStopReason.LowSpace, result.StopReason);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(3, result.Rows);
        Assert.Equal(12 * S, result.DurationUs);
        Assert.Equal(RunActivityState.Idle, h.Recorder.State);
        Assert.Equal(0, h.Samples.LeasesHeld);
        Assert.Equal(0, h.Samples.Subscribers);
        Assert.Equal(RunStopReason.LowSpace, h.Header(run.RunId).StopReason);

        // A stop that comes afterwards gets the same result.
        Assert.Same(result, await h.Recorder.StopAsync(run, RunStopReason.User));
    }

    [Fact]
    public async Task Exactly_200_MB_free_at_a_flush_does_not_stop_the_run()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        await h.Barrier(run);
        h.Fs.FreeBytes = RunRecorder.DefaultMinFreeBytes;
        h.Tick(2, 12 * S);
        await h.Barrier(run);
        Assert.Equal(RunActivityState.Recording, h.Recorder.State);
        await EndAt(h, run, 13 * S);
    }

    [Theory]
    [InlineData("FlushToDisk", ".csv")]
    [InlineData("ReplaceAtomic", ".json")]
    public async Task A_failure_of_a_flush_ends_the_run_with_io_error_and_leaves_a_readable_json_and_csv(string op, string suffix)
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        await h.Barrier(run);

        h.Fs.Fail(op, Kit.DiskFull, name => name.EndsWith(suffix, StringComparison.Ordinal), times: 1);
        h.Tick(2, 12 * S); // a flush is due
        var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(RunStopReason.IoError, result.StopReason);
        Assert.NotNull(result.Error);
        Assert.Equal(0, h.Samples.LeasesHeld);
        Assert.Equal(0, h.Samples.Subscribers);
        Assert.Equal(RunActivityState.Idle, h.Recorder.State);
        Assert.False(h.Fs.IsLockHeld(h.LockPath(run.RunId)));

        var ended = h.Header(run.RunId);
        Assert.Equal(RunStatus.Failed, ended.Status);
        Assert.Equal(RunStopReason.IoError, ended.StopReason);
        var detail = await Read(h, run.RunId);
        Assert.Equal(RunStatus.Failed, detail.Info.Status);
        Assert.True(detail.Info.Rows >= 1);
        Assert.True(detail.Summary.HasNote("not-completed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Recorder.MarkAsync(run, MarkerKind.Mark, CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_write_of_a_row_ends_the_run_and_what_was_written_before_it_stays_readable()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        await h.Barrier(run);

        h.Fs.Fail("Write", Kit.DiskFull, name => name.EndsWith(".csv", StringComparison.Ordinal));
        h.Tick(2, 2 * S);
        h.Tick(3, 3 * S); // the writer has failed: this one is ignored
        var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(RunStopReason.IoError, result.StopReason);
        Assert.Equal(1, result.Rows);
        Assert.Equal(1 * S, result.DurationUs);
        var detail = await Read(h, run.RunId);
        Assert.Equal(RunStatus.Failed, detail.Info.Status);
        Assert.Equal(1, detail.Info.Rows);
        Assert.Equal(2, CsvLines(h, run.RunId).Length);
    }

    [Fact]
    public async Task A_failure_while_writing_the_columns_leaves_a_readable_empty_run()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Fs.Fail("Write", Kit.DiskFull, name => name.EndsWith(".csv", StringComparison.Ordinal));
        h.Tick(1, 1 * S);
        var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(0, result.Rows);
        var detail = await Read(h, run.RunId);
        Assert.Equal(RunStatus.Failed, detail.Info.Status);
        Assert.Equal(0, detail.Info.Rows);
        Assert.True(detail.Summary.HasNote("no-samples"));
    }

    [Fact]
    public async Task A_mark_whose_json_cannot_be_saved_fails_the_call_and_the_run_ends_with_io_error_without_the_mark()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        await h.Barrier(run);
        h.Fs.Fail("ReplaceAtomic", Kit.DiskFull, name => name.EndsWith(".json", StringComparison.Ordinal), times: 1);

        await Assert.ThrowsAsync<IOException>(() => h.Recorder.MarkAsync(run, MarkerKind.BenchStart, CancellationToken.None));
        var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RunStopReason.IoError, result.StopReason);
        Assert.DoesNotContain(h.Header(run.RunId).Markers, m => m.Kind == MarkerKind.BenchStart);
    }

    [Fact]
    public async Task A_run_that_is_still_recording_cannot_be_deleted_and_a_finished_one_can()
    {
        using var h = new RecHarness();
        var store = StoreOf(h);
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);

        var refused = await store.DeleteAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunDeleteOutcome.Recording, refused.Outcome);
        Assert.True(h.Fs.Exists(h.JsonPath(run.RunId)));
        Assert.True(h.Fs.Exists(h.CsvPath(run.RunId)));
        Assert.True(h.Fs.IsLockHeld(h.LockPath(run.RunId)));

        await EndAt(h, run, 2 * S);
        var done = await store.DeleteAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunDeleteOutcome.Deleted, done.Outcome);
        Assert.Empty(h.Fs.Names(RecHarness.Root));
    }

    // ------------------------------------------------------------------ a run that was cut off

    private static (Guid Id, string Csv) PutCutOffRun(RecHarness h, string tail = "", IReadOnlyList<RunMarker>? markers = null)
    {
        var id = RecHarness.IdOf(100);
        var layout = RunLayout.Create([G(1, 1 * S)]);
        var samples = Enumerable.Range(1, 3)
            .Select(n => SampleKit.Sample(n, RecHarness.At(n * S), gpus: [G(n, n * S)]))
            .ToList();
        var csv = RunFileKit.CsvText(layout, RecHarness.StartUs, samples);
        var header = RunFileKit.HeaderOf(layout, id, RunStatus.Recording, null, markers: markers);
        RunFileKit.PutRun(h.Fs, RecHarness.Root, header, csv + tail);
        return (id, csv + tail);
    }

    [Fact]
    public async Task A_recording_run_nobody_holds_becomes_interrupted_at_the_next_start_with_its_rows_from_the_csv()
    {
        using var h = new RecHarness();
        var (id, csv) = PutCutOffRun(h);
        var run = await h.StartAsync();

        var found = h.Header(id);
        Assert.Equal(RunStatus.Interrupted, found.Status);
        Assert.Equal(RunStopReason.Unknown, found.StopReason);
        Assert.Null(found.EndedAtUtc);
        Assert.Equal(3, found.Rows);
        Assert.Equal(3 * S, found.DurationUs);
        Assert.Equal(csv, h.Fs.Text(h.CsvPath(id)));
        Assert.False(h.Fs.IsLockHeld(h.LockPath(id)));
        await h.Recorder.StopAsync(run, RunStopReason.User);
    }

    [Fact]
    public async Task A_half_written_last_line_is_left_out_of_the_rows_of_an_interrupted_run()
    {
        using var h = new RecHarness();
        var (id, _) = PutCutOffRun(h, tail: "4000000,2026-11-21T03:00:0");
        var run = await h.StartAsync();
        var found = h.Header(id);
        Assert.Equal(RunStatus.Interrupted, found.Status);
        Assert.Equal(3, found.Rows);
        Assert.Equal(3 * S, found.DurationUs);

        var detail = await Read(h, id);
        Assert.Equal(3, detail.Info.Rows);
        Assert.Equal(RunStatus.Interrupted, detail.Info.Status);
        await h.Recorder.StopAsync(run, RunStopReason.User);
    }

    [Fact]
    public async Task The_duration_of_an_interrupted_run_covers_its_last_mark()
    {
        using var h = new RecHarness();
        var (id, _) = PutCutOffRun(h, markers: [new RunMarker(MarkerKind.Mark, 3_500_000, SampleKit.Wall.AddSeconds(3.5))]);
        var run = await h.StartAsync();
        Assert.Equal(3_500_000, h.Header(id).DurationUs);
        await h.Recorder.StopAsync(run, RunStopReason.User);
    }

    [Fact]
    public async Task A_run_whose_lock_is_held_by_another_process_is_left_alone()
    {
        using var h = new RecHarness();
        var (id, _) = PutCutOffRun(h);
        var before = h.Fs.DurableBytes(h.JsonPath(id));
        using var other = h.Fs.HoldLock(h.LockPath(id));
        var run = await h.StartAsync();

        Assert.Equal(before, h.Fs.DurableBytes(h.JsonPath(id)));
        Assert.Equal(RunStatus.Recording, h.Header(id).Status);
        await h.Recorder.StopAsync(run, RunStopReason.User);
    }

    [Fact]
    public async Task A_save_that_was_cut_off_leaves_a_tmp_that_the_next_start_removes()
    {
        using var h = new RecHarness();
        var orphan = RecHarness.IdOf(100);
        h.Fs.Put(h.TempPath(orphan), "{ \"format\": \"pcm-run/1\"");
        var run = await h.StartAsync();
        Assert.False(h.Fs.Exists(h.TempPath(orphan)));
        await h.Recorder.StopAsync(run, RunStopReason.User);
    }

    [Fact]
    public async Task A_recovery_that_cannot_write_does_not_stop_a_new_run_from_starting()
    {
        using var h = new RecHarness();
        var (id, _) = PutCutOffRun(h);
        h.Fs.Fail("ReplaceAtomic", Kit.DiskFull, name => name == RunFiles.JsonName(id));
        var run = await h.StartAsync();
        Assert.Equal(RunStatus.Recording, h.Header(id).Status);
        var result = await h.Recorder.StopAsync(run, RunStopReason.User);
        Assert.Equal(RunStatus.Completed, result.Status);
    }

    // ------------------------------------------------------------------ the limit bits

    [Fact]
    public async Task A_limit_mask_with_every_bit_set_is_written_as_a_decimal_string_and_reads_back_exactly()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S, gpus: [G(1, 1 * S, bits: ulong.MaxValue)]);
        h.Tick(2, 2 * S, gpus: [G(2, 2 * S, bits: 0x40)]);
        await EndAt(h, run, 3 * S);

        var lines = CsvLines(h, run.RunId);
        Assert.Contains("18446744073709551615", lines[1], StringComparison.Ordinal);
        var data = RunCsvReader.Read(h.Fs.DurableBytes(h.CsvPath(run.RunId)), h.Header(run.RunId).Metrics);
        var track = Assert.Single(data.Tracks, t => t.Key == "gpu1.limit_reasons");
        Assert.Equal(ulong.MaxValue, track.Points[0].Bits);
        Assert.Equal(0x40UL, track.Points[1].Bits);
    }
}
