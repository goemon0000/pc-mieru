using System.Text;
using PcMieru.Core;
using static PcMieru.Checks.Tests.SeriesKit;

namespace PcMieru.Checks.Tests;

/// <summary>
/// What the review of the recorder asked for (stage B step 7): recovery never blocks a start, a run is stopped when the process exits (and exit never
/// waits for it longer than a bound), a GPU that comes back under a new LUID goes on in its column, the columns wait for a completed GPU stage, a JSON
/// replace that meets a sharing violation is tried again and a periodic save that still fails is put off, and the smaller rules around them. Everything
/// is in memory and a wait is a gate, never a sleep.
/// </summary>
public class RunRecorderReviewTests
{
    private const long S = 1_000_000;
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static RunStore StoreOf(RecHarness h) => new(h.Fs, RecHarness.Root, null);

    private static RecordedGpu G(long seq, long offsetUs, string id = "gpu-a", string name = "Test GPU A", double? temp = 60, long generation = 1, ulong? bits = 0) =>
        SampleKit.Gpu(id: id, name: name, generation: generation, sequence: seq, acquiredUs: RecHarness.At(offsetUs), temp: temp, bits: bits);

    /// <summary>A GPU with its dedicated memory (what tells identical cards apart) and the frame it was read in.</summary>
    private static RecordedGpu Gd(string id, string name, long seq, long acquiredOffsetUs, long dedicatedBytes, double? temp = 60) =>
        SampleKit.Gpu(id: id, name: name, sequence: seq, acquiredUs: RecHarness.At(acquiredOffsetUs), temp: temp, dedicatedBytes: dedicatedBytes);

    /// <summary>A GPU frame of a source that reads every 2 s.</summary>
    private static RecordedGpu Slow(long seq, long acquiredOffsetUs) =>
        SampleKit.Gpu(sequence: seq, acquiredUs: RecHarness.At(acquiredOffsetUs), periodMs: 2000);

    private static string[] CsvLines(RecHarness h, Guid id) => h.Fs.Text(h.CsvPath(id)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static async Task<RunResult> EndAt(RecHarness h, RunHandle run, long offsetUs, RunStopReason reason = RunStopReason.User)
    {
        h.Clock.Us = RecHarness.At(offsetUs);
        return await h.Recorder.StopAsync(run, reason).WaitAsync(Limit);
    }

    private static async Task<RunDetail> Read(RecHarness h, Guid id) =>
        await StoreOf(h).ReadAsync(id, CancellationToken.None) ?? throw new InvalidOperationException("The run is not there.");

    /// <summary>A sample whose GPU stage did not complete (no GPUs in it): the columns must not be fixed from it.</summary>
    private static void TickWithoutGpuStage(RecHarness h, long seq, long offsetUs, IReadOnlyList<RecordedGpu>? gpus = null)
    {
        h.Clock.Us = RecHarness.At(offsetUs);
        h.Samples.Raise(SampleKit.Sample(seq, RecHarness.At(offsetUs), 10, gpus, gpuStage: false));
    }

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

    // ------------------------------------------------------------------ item 1: recovery never blocks a start

    [Fact]
    public void A_json_longer_than_the_reader_accepts_or_not_json_at_all_does_not_stop_the_recovery_of_the_next_run()
    {
        using var h = new RecHarness();
        h.Fs.Put(h.JsonPath(RecHarness.IdOf(1)), new byte[RunJson.MaxBytes + 1]); // sorts first: the runs after it must still be looked at
        h.Fs.Put(h.JsonPath(RecHarness.IdOf(2)), "{ this is not json");
        var (id, _) = PutCutOffRun(h);

        var recovered = RunRecovery.Recover(h.Fs, RecHarness.Root);

        Assert.Equal(1, recovered);
        Assert.Equal(RunStatus.Interrupted, h.Header(id).Status);
    }

    [Fact]
    public async Task A_start_goes_on_when_an_earlier_run_is_too_long_or_malformed_and_the_good_one_is_still_recovered()
    {
        using var h = new RecHarness(firstId: 500);
        h.Fs.Put(h.JsonPath(RecHarness.IdOf(1)), new byte[RunJson.MaxBytes + 1]);
        h.Fs.Put(h.JsonPath(RecHarness.IdOf(2)), "{ this is not json");
        var (id, _) = PutCutOffRun(h);

        var run = await h.StartAsync();

        Assert.Equal(RunActivityState.Recording, h.Recorder.State);
        Assert.Equal(RunStatus.Recording, h.Header(run.RunId).Status);
        Assert.Equal(RunStatus.Interrupted, h.Header(id).Status);
        await h.Recorder.StopAsync(run, RunStopReason.User);
    }

    [Fact]
    public async Task A_recovery_that_fails_in_any_way_never_blocks_a_start()
    {
        using var h = new RecHarness();
        h.Fs.Fail("ListFiles", () => new InvalidOperationException("the folder could not be listed"), times: 1);

        var run = await h.StartAsync();

        Assert.Equal(RunActivityState.Recording, h.Recorder.State);
        var result = await h.Recorder.StopAsync(run, RunStopReason.User);
        Assert.Equal(RunStatus.Completed, result.Status);
    }

    // ------------------------------------------------------------------ item 2: stop on exit

    [Fact]
    public async Task StopCurrentAsync_on_the_interface_gives_null_when_nothing_is_going_and_the_result_when_a_run_is()
    {
        using var h = new RecHarness();
        IRunRecorder recorder = h.Recorder;
        Assert.Null(await recorder.StopCurrentAsync(RunStopReason.WindowClose));

        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        h.Clock.Us = RecHarness.At(2 * S);
        var result = await recorder.StopCurrentAsync(RunStopReason.WindowClose).WaitAsync(Limit);

        Assert.NotNull(result);
        Assert.Equal(run.RunId, result.RunId);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(RunStopReason.WindowClose, result.StopReason);
        Assert.Equal(1, result.Rows);
        Assert.Null(await recorder.StopCurrentAsync(RunStopReason.WindowClose));
    }

    [Fact]
    public async Task The_exit_hook_exists_only_while_a_run_does()
    {
        using var h = new RecHarness();
        Assert.Equal(0, h.Exit.Live);

        var run = await h.StartAsync();
        Assert.Equal(1, h.Exit.Live);
        await EndAt(h, run, 2 * S);
        Assert.Equal(0, h.Exit.Live);

        h.Fs.Fail("CreateNew", Kit.DiskFull, name => name.EndsWith(".csv", StringComparison.Ordinal));
        await Assert.ThrowsAsync<RunStartException>(() => h.Recorder.StartAsync(CancellationToken.None));
        Assert.Equal(0, h.Exit.Live);
        Assert.Equal(1, h.Exit.Registered);
    }

    [Fact]
    public async Task The_process_exit_flushes_the_rows_and_marks_the_run_completed_with_process_exit()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        h.Tick(2, 2 * S);
        h.Tick(3, 3 * S);
        h.Clock.Us = RecHarness.At(3_500_000);

        h.Exit.Fire(); // the hook returns when the stop has ended (within its bound)

        Assert.True(run.Completion.IsCompleted);
        var result = await run.Completion;
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(RunStopReason.ProcessExit, result.StopReason);
        Assert.Equal(3, result.Rows);
        Assert.Equal(0, h.Fs.PendingLength(h.CsvPath(run.RunId)));
        var header = h.Header(run.RunId);
        Assert.Equal(RunStatus.Completed, header.Status);
        Assert.Equal(RunStopReason.ProcessExit, header.StopReason);
        Assert.Equal(3, header.Rows);
        Assert.Equal(4, CsvLines(h, run.RunId).Length);
        Assert.Equal(0, h.Exit.Live);
        Assert.Equal(RunActivityState.Idle, h.Recorder.State);
    }

    [Fact]
    public async Task A_writer_that_hangs_does_not_hold_the_exit_beyond_its_bound()
    {
        using var h = new RecHarness(exitWaitMs: 300);
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        await h.Barrier(run);
        using var gate = new FsGate((op, name) => op == "FlushToDisk" && name.EndsWith(".csv", StringComparison.Ordinal));
        h.Fs.Hook = gate.Hook;
        h.Tick(2, 2 * S);

        var fire = Task.Run(h.Exit.Fire);
        gate.WaitEntered(); // the writer is inside the last flush and does not come out

        // Unbounded, this would wait for the gate's own limit (30 s); bounded, it is back in about 300 ms.
        await fire.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(run.Completion.IsCompleted);

        gate.Release();
        var result = await run.Completion.WaitAsync(Limit);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(RunStopReason.ProcessExit, result.StopReason);
        Assert.Equal(2, result.Rows);
        Assert.Equal(0, h.Exit.Live);
    }

    [Fact]
    public async Task The_bounded_stop_gives_null_when_the_run_does_not_close_in_time_and_the_result_when_it_does()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        await h.Barrier(run);
        using var gate = new FsGate((op, name) => op == "FlushToDisk" && name.EndsWith(".csv", StringComparison.Ordinal));
        h.Fs.Hook = gate.Hook;
        h.Tick(2, 2 * S);

        var timedOut = h.Recorder.StopCurrentBlocking(RunStopReason.WindowClose, TimeSpan.FromMilliseconds(300));
        Assert.Null(timedOut);
        Assert.False(run.Completion.IsCompleted);

        gate.Release();
        var finished = await run.Completion.WaitAsync(Limit);
        Assert.Equal(RunStopReason.WindowClose, finished.StopReason); // the first caller's reason is the run's

        using var quick = new RecHarness();
        await quick.StartAsync();
        var result = quick.Recorder.StopCurrentBlocking(RunStopReason.WindowClose);
        Assert.NotNull(result);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Null(quick.Recorder.StopCurrentBlocking(RunStopReason.WindowClose));
    }

    // ------------------------------------------------------------------ item 3: GPU columns survive a driver restart

    [Fact]
    public async Task A_gpu_that_comes_back_under_a_new_id_goes_on_in_its_old_column()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S, gpus: [Gd("luid-1", "Test GPU", 1, 1 * S, 8 * Gb)]);
        h.Tick(2, 2 * S, gpus: [Gd("luid-1", "Test GPU", 2, 2 * S, 8 * Gb)]);
        h.Tick(3, 3 * S, gpus: []); // the driver restarted: the adapter is gone for a moment
        h.Tick(4, 4 * S, gpus: [Gd("luid-2", "Test GPU", 3, 4 * S, 8 * Gb)]);
        h.Tick(5, 5 * S, gpus: [Gd("luid-2", "Test GPU", 4, 5 * S, 8 * Gb)]);
        await h.Barrier(run);

        var header = h.Header(run.RunId);
        Assert.DoesNotContain(header.Sources, s => s.Source == "gpu2");
        var warning = Assert.Single(header.Markers, m => m.Kind == MarkerKind.Warning);
        Assert.Equal("gpu_rebound:Test GPU", warning.Detail);
        Assert.Equal(4 * S, warning.OffsetUs);
        var lines = CsvLines(h, run.RunId);
        Assert.DoesNotContain("gpu2.", lines[0], StringComparison.Ordinal);
        Assert.Contains("missing,device_gone", lines[3], StringComparison.Ordinal);
        Assert.Contains("g1-3,", lines[4], StringComparison.Ordinal);
        Assert.DoesNotContain("missing", lines[4], StringComparison.Ordinal);

        await EndAt(h, run, 6 * S);
        var temp = (await Read(h, run.RunId)).Summary.Metric("gpu1.temp_c");
        Assert.NotNull(temp);
        Assert.Equal(4, temp.NewPoints);
    }

    [Fact]
    public async Task A_late_gpu_never_takes_the_column_of_a_gpu_that_is_still_there()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S, gpus: [Gd("luid-1", "Twin", 1, 1 * S, 8 * Gb)]);
        h.Tick(2, 2 * S, gpus: [Gd("luid-1", "Twin", 2, 2 * S, 8 * Gb), Gd("luid-9", "Twin", 1, 2 * S, 8 * Gb, temp: 77.25)]);
        h.Tick(3, 3 * S, gpus: [Gd("luid-1", "Twin", 3, 3 * S, 8 * Gb), Gd("luid-9", "Twin", 2, 3 * S, 8 * Gb, temp: 77.25)]);
        await h.Barrier(run);

        var header = h.Header(run.RunId);
        var warning = Assert.Single(header.Markers, m => m.Kind == MarkerKind.Warning);
        Assert.Equal("late_gpu:Twin", warning.Detail);
        Assert.DoesNotContain(header.Markers, m => m.Detail is not null && m.Detail.StartsWith("gpu_rebound", StringComparison.Ordinal));
        Assert.DoesNotContain("77.25", string.Join('\n', CsvLines(h, run.RunId)), StringComparison.Ordinal);
        await EndAt(h, run, 4 * S);
    }

    [Fact]
    public async Task Twin_cards_of_different_memory_that_both_go_away_and_come_back_shuffled_each_find_their_own_column()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S, gpus: [Gd("luid-1", "Twin", 1, 1 * S, 8 * Gb, temp: 50), Gd("luid-2", "Twin", 1, 1 * S, 12 * Gb, temp: 70)]);
        h.Tick(2, 2 * S, gpus: []);
        h.Tick(3, 3 * S, gpus: [Gd("luid-8", "Twin", 2, 3 * S, 12 * Gb, temp: 70), Gd("luid-7", "Twin", 2, 3 * S, 8 * Gb, temp: 50)]);
        await EndAt(h, run, 4 * S);

        var header = h.Header(run.RunId);
        Assert.Equal(2, header.Markers.Count(m => m.Detail == "gpu_rebound:Twin"));
        Assert.DoesNotContain(header.Markers, m => m.Detail is not null && m.Detail.StartsWith("late_gpu", StringComparison.Ordinal));
        var summary = (await Read(h, run.RunId)).Summary;
        var first = summary.Metric("gpu1.temp_c");
        var second = summary.Metric("gpu2.temp_c");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, first.NewPoints);
        Assert.Equal(2, second.NewPoints);
        Assert.Equal(50.0, first.Min, 6);
        Assert.Equal(50.0, first.Max, 6);
        Assert.Equal(70.0, second.Min, 6);
        Assert.Equal(70.0, second.Max, 6);
    }

    [Fact]
    public async Task Of_identical_twins_the_one_that_is_gone_is_the_one_that_comes_back()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S, gpus: [Gd("luid-1", "Twin", 1, 1 * S, 8 * Gb), Gd("luid-2", "Twin", 1, 1 * S, 8 * Gb)]);
        h.Tick(2, 2 * S, gpus: [Gd("luid-1", "Twin", 2, 2 * S, 8 * Gb)]); // the second one vanished; the first is still there
        h.Tick(3, 3 * S, gpus: [Gd("luid-1", "Twin", 3, 3 * S, 8 * Gb), Gd("luid-3", "Twin", 2, 3 * S, 8 * Gb)]);
        await EndAt(h, run, 4 * S);

        var header = h.Header(run.RunId);
        Assert.Equal(1, header.Markers.Count(m => m.Detail == "gpu_rebound:Twin"));
        Assert.DoesNotContain(header.Sources, s => s.Source == "gpu3");
        var summary = (await Read(h, run.RunId)).Summary;
        Assert.Equal(3, summary.Metric("gpu1.temp_c")?.NewPoints);
        Assert.Equal(2, summary.Metric("gpu2.temp_c")?.NewPoints);
    }

    [Fact]
    public async Task Identical_twins_that_both_went_away_are_not_told_apart_and_nothing_is_rebound()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S, gpus: [Gd("luid-1", "Twin", 1, 1 * S, 8 * Gb), Gd("luid-2", "Twin", 1, 1 * S, 8 * Gb)]);
        h.Tick(2, 2 * S, gpus: []);
        h.Tick(3, 3 * S, gpus: [Gd("luid-5", "Twin", 2, 3 * S, 8 * Gb), Gd("luid-6", "Twin", 2, 3 * S, 8 * Gb)]);
        await EndAt(h, run, 4 * S);

        var header = h.Header(run.RunId);
        Assert.DoesNotContain(header.Markers, m => m.Detail is not null && m.Detail.StartsWith("gpu_rebound", StringComparison.Ordinal));
        Assert.Equal(2, header.Markers.Count(m => m.Detail == "late_gpu:Twin"));
        Assert.Equal(2, header.Sources.Count(s => s.Source.StartsWith("gpu", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Two_candidates_for_one_missing_column_bind_to_neither()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S, gpus: [Gd("luid-1", "Twin", 1, 1 * S, 8 * Gb)]);
        h.Tick(2, 2 * S, gpus: []);
        h.Tick(3, 3 * S, gpus: [Gd("luid-5", "Twin", 2, 3 * S, 8 * Gb), Gd("luid-6", "Twin", 2, 3 * S, 8 * Gb)]);
        await EndAt(h, run, 4 * S);

        var header = h.Header(run.RunId);
        Assert.DoesNotContain(header.Markers, m => m.Detail is not null && m.Detail.StartsWith("gpu_rebound", StringComparison.Ordinal));
        Assert.Equal(2, header.Markers.Count(m => m.Detail == "late_gpu:Twin"));
    }

    [Fact]
    public async Task A_late_gpu_of_another_memory_size_is_not_bound_to_the_missing_column()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S, gpus: [Gd("luid-1", "Twin", 1, 1 * S, 8 * Gb)]);
        h.Tick(2, 2 * S, gpus: []);
        h.Tick(3, 3 * S, gpus: [Gd("luid-5", "Twin", 2, 3 * S, 12 * Gb)]);
        await EndAt(h, run, 4 * S);

        var header = h.Header(run.RunId);
        Assert.DoesNotContain(header.Markers, m => m.Detail is not null && m.Detail.StartsWith("gpu_rebound", StringComparison.Ordinal));
        Assert.Single(header.Markers, m => m.Detail == "late_gpu:Twin");
    }

    // ------------------------------------------------------------------ item 4: the columns wait for a completed GPU stage

    [Fact]
    public async Task The_header_waits_for_a_sample_whose_gpu_stage_completed_and_the_first_row_is_not_lost()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        TickWithoutGpuStage(h, 1, 1 * S);
        h.Tick(2, 2 * S, gpus: [G(1, 2 * S)]);
        await h.Barrier(run);

        var header = h.Header(run.RunId);
        Assert.Contains(header.Sources, s => s.Source == "gpu1");
        Assert.DoesNotContain(header.Markers, m => m.Kind == MarkerKind.Warning);
        var lines = CsvLines(h, run.RunId);
        Assert.Equal(3, lines.Length);
        Assert.Contains("gpu1.temp_c", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("1000000,", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("2000000,", lines[2], StringComparison.Ordinal);
        Assert.Contains("missing,device_gone", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("device_gone", lines[2], StringComparison.Ordinal);
        await EndAt(h, run, 3 * S);
    }

    [Fact]
    public async Task The_header_waits_for_at_most_three_ticks_then_the_columns_are_fixed_with_what_there_is()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        TickWithoutGpuStage(h, 1, 1 * S);
        TickWithoutGpuStage(h, 2, 2 * S);
        await h.Barrier(run);
        Assert.Empty(h.Fs.Text(h.CsvPath(run.RunId))); // two samples are waiting: nothing is written yet, nothing is lost

        TickWithoutGpuStage(h, 3, 3 * S);
        await h.Barrier(run);
        var lines = CsvLines(h, run.RunId);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("1000000,", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("2000000,", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("3000000,", lines[3], StringComparison.Ordinal);
        Assert.Equal(3, h.Header(run.RunId).Rows);

        // The columns are fixed now: a GPU that shows up later gets no column and one warning, as before.
        h.Tick(4, 4 * S, gpus: [G(1, 4 * S)]);
        await h.Barrier(run);
        Assert.Single(h.Header(run.RunId).Markers, m => m.Detail == "late_gpu:Test GPU A");
        await EndAt(h, run, 5 * S);
    }

    [Fact]
    public async Task A_sample_that_waited_for_the_columns_is_written_when_the_run_stops()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        TickWithoutGpuStage(h, 1, 1 * S);

        var result = await EndAt(h, run, 2 * S);

        Assert.Equal(1, result.Rows);
        var lines = CsvLines(h, run.RunId);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("1000000,", lines[1], StringComparison.Ordinal);
        Assert.Equal(1, h.Header(run.RunId).Rows);
    }

    [Fact]
    public async Task A_sample_that_waits_for_the_columns_is_not_held_back_when_no_more_come()
    {
        using var h = new RecHarness(idleWaitMs: 5);
        var run = await h.StartAsync();
        TickWithoutGpuStage(h, 1, 1 * S);
        h.Clock.Us = RecHarness.At(12 * S); // longer than a flush interval later, and nothing else has come

        Assert.True(SpinWait.SpinUntil(() => h.Fs.DurableBytes(h.CsvPath(run.RunId)).Length > 0, Limit));
        Assert.Equal(2, CsvLines(h, run.RunId).Length);
        await EndAt(h, run, 13 * S);
    }

    // ------------------------------------------------------------------ item 5: the JSON replace is tried again

    [Fact]
    public void The_save_tries_again_after_each_of_its_delays_through_the_pause_it_is_given()
    {
        var fs = new MemRunFs();
        fs.CreateDirectory(RecHarness.Root);
        var temp = Path.Combine(RecHarness.Root, "run-x.json.tmp");
        var destination = Path.Combine(RecHarness.Root, "run-x.json");
        var pauses = new List<TimeSpan>();
        fs.Fail("ReplaceAtomic", Kit.SharingViolation, times: 2);

        RunFiles.SaveAtomic(fs, temp, destination, [1, 2, 3], pauses.Add);

        Assert.Equal(new[] { RunFiles.SaveRetryDelays[0], RunFiles.SaveRetryDelays[1] }, pauses);
        Assert.Equal(new byte[] { 1, 2, 3 }, fs.DurableBytes(destination));
        Assert.False(fs.Exists(temp));
    }

    [Fact]
    public void A_save_that_keeps_failing_is_tried_five_times_more_and_then_fails_with_nothing_left()
    {
        var fs = new MemRunFs();
        fs.CreateDirectory(RecHarness.Root);
        var temp = Path.Combine(RecHarness.Root, "run-x.json.tmp");
        var destination = Path.Combine(RecHarness.Root, "run-x.json");
        var pauses = new List<TimeSpan>();
        fs.Fail("ReplaceAtomic", Kit.SharingViolation);

        Assert.Throws<IOException>(() => RunFiles.SaveAtomic(fs, temp, destination, [1], pauses.Add));

        Assert.Equal(RunFiles.SaveRetryDelays, pauses);
        Assert.Equal(5, pauses.Count);
        Assert.All(pauses, p => Assert.InRange(p, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)));
        Assert.Equal(6, fs.Count("ReplaceAtomic"));
        Assert.False(fs.Exists(temp));
        Assert.False(fs.Exists(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_marker_save_that_meets_a_sharing_violation_or_an_access_denied_is_tried_again_and_succeeds(bool accessDenied)
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        if (accessDenied)
        {
            h.Fs.Fail("ReplaceAtomic", () => new UnauthorizedAccessException("denied"), name => name.EndsWith(".json", StringComparison.Ordinal), times: 3);
        }
        else
        {
            h.Fs.Fail("ReplaceAtomic", Kit.SharingViolation, name => name.EndsWith(".json", StringComparison.Ordinal), times: 3);
        }

        var marker = await h.Barrier(run);

        Assert.Equal(MarkerKind.Mark, marker.Kind);
        Assert.Equal(RunFiles.SaveRetryDelays.Take(3), h.Pauses);
        Assert.Single(h.Header(run.RunId).Markers);
        Assert.False(h.Fs.Exists(h.TempPath(run.RunId)));
        Assert.Equal(RunActivityState.Recording, h.Recorder.State);
        await EndAt(h, run, 2 * S);
    }

    [Fact]
    public async Task A_marker_save_and_the_final_save_that_keep_failing_retry_and_then_the_run_fails_with_io_error()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Fs.Fail("ReplaceAtomic", Kit.SharingViolation, name => name.EndsWith(".json", StringComparison.Ordinal));

        await Assert.ThrowsAsync<IOException>(() => h.Recorder.MarkAsync(run, MarkerKind.Mark, CancellationToken.None));
        var result = await run.Completion.WaitAsync(Limit);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(RunStopReason.IoError, result.StopReason);
        Assert.Equal(10, h.Pauses.Count); // the marker's save (5) and the final save (5)
        var onDisk = h.Header(run.RunId);
        Assert.Equal(RunStatus.Recording, onDisk.Status); // nothing could be written over it
        Assert.Empty(onDisk.Markers);
        Assert.Equal(RunActivityState.Idle, h.Recorder.State);

        // What the disk still says is "recording" with nobody holding the lock: the next start makes it interrupted.
        h.Fs.ClearFaults();
        var next = await h.StartAsync();
        Assert.Equal(RunStatus.Interrupted, h.Header(run.RunId).Status);
        await h.Recorder.StopAsync(next, RunStopReason.User);
    }

    [Fact]
    public async Task A_full_disk_on_the_replace_is_not_retried()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Fs.Fail("ReplaceAtomic", Kit.DiskFull, name => name.EndsWith(".json", StringComparison.Ordinal), times: 1);

        await Assert.ThrowsAsync<IOException>(() => h.Recorder.MarkAsync(run, MarkerKind.Mark, CancellationToken.None));
        var result = await run.Completion.WaitAsync(Limit);

        Assert.Empty(h.Pauses);
        Assert.Equal(RunStopReason.IoError, result.StopReason);
    }

    [Fact]
    public async Task A_periodic_flush_whose_json_replace_still_fails_is_put_off_to_the_next_flush_and_the_run_goes_on()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        await h.Barrier(run);
        var csv = h.CsvPath(run.RunId);
        Assert.Equal(1, h.Header(run.RunId).Rows);

        // One whole save (the first try and its five retries) fails; the flush after it has a working disk again.
        h.Fs.Fail("ReplaceAtomic", Kit.SharingViolation, name => name.EndsWith(".json", StringComparison.Ordinal), times: 6);
        h.Tick(2, 12 * S); // 11 s after the last flush: due

        var writer = run.Session.Writer!;
        Assert.True(SpinWait.SpinUntil(() => writer.DeferredSaves == 1, Limit));
        Assert.Equal(RunActivityState.Recording, h.Recorder.State);
        Assert.Equal(5, h.Pauses.Count);
        Assert.Equal(0, h.Fs.PendingLength(csv)); // the CSV is on the disk: it is the truth
        Assert.Equal(3, CsvLines(h, run.RunId).Length);
        var stale = h.Header(run.RunId);
        Assert.Equal(RunStatus.Recording, stale.Status);
        Assert.Equal(1, stale.Rows); // the JSON is still the one before

        // The next flush saves it.
        h.Tick(3, 23 * S);
        Assert.True(SpinWait.SpinUntil(() => h.Header(run.RunId).Rows == 3, Limit));
        Assert.Equal(1, writer.DeferredSaves);

        var result = await EndAt(h, run, 24 * S);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(3, result.Rows);
    }

    [Fact]
    public async Task A_recovery_whose_replace_meets_a_sharing_violation_for_a_moment_still_recovers_the_run()
    {
        using var h = new RecHarness();
        var (id, _) = PutCutOffRun(h);
        h.Fs.Fail("ReplaceAtomic", Kit.SharingViolation, name => name == RunFiles.JsonName(id), times: 2);

        var run = await h.StartAsync();

        Assert.Equal(RunStatus.Interrupted, h.Header(id).Status);
        Assert.Equal(2, h.Pauses.Count);
        await h.Recorder.StopAsync(run, RunStopReason.User);
    }

    // ------------------------------------------------------------------ item 6: the start's own marker, the size cap, idle flush, the end of a cut-off run, delete

    [Fact]
    public async Task A_start_that_ends_late_does_not_clear_the_marker_of_a_start_that_began_after_its_run_stopped()
    {
        using var prepared = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        var starts = 0;
        using var h = new RecHarness(afterPrepare: () =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                prepared.Set();
                releaseFirst.Wait(Limit);
            }
        });

        // Start 1 is prepared, and its run ends by itself (low space) before StartAsync has returned.
        var first = h.Recorder.StartAsync(CancellationToken.None);
        Assert.True(prepared.Wait(Limit));
        h.Fs.FreeBytes = 100L * 1024 * 1024;
        h.Tick(1, 1 * S);
        Assert.True(SpinWait.SpinUntil(() => h.Recorder.State == RunActivityState.Idle, Limit));

        // Start 2 begins and is held at its first file.
        h.Fs.FreeBytes = 10L * 1024 * 1024 * 1024;
        using var gate = new FsGate((op, name) => op == "CreateNew" && name.EndsWith(".csv", StringComparison.Ordinal));
        h.Fs.Hook = gate.Hook;
        var second = h.Recorder.StartAsync(CancellationToken.None);
        gate.WaitEntered();
        Assert.Equal(RunActivityState.Preparing, h.Recorder.State);

        // Start 1 now finishes. It must not clear the marker that is start 2's.
        releaseFirst.Set();
        var firstRun = await first.WaitAsync(Limit);
        Assert.Equal(RunStopReason.LowSpace, (await firstRun.Completion.WaitAsync(Limit)).StopReason);
        Assert.Equal(RunActivityState.Preparing, h.Recorder.State);

        var stop = h.Recorder.StopCurrentAsync(RunStopReason.ProcessExit);
        Assert.False(stop.IsCompleted); // it waits for the run that is being prepared; with the marker gone it would answer "nothing is going" at once

        gate.Release();
        var secondRun = await second.WaitAsync(Limit);
        var result = await stop.WaitAsync(Limit);
        Assert.NotNull(result);
        Assert.Equal(secondRun.RunId, result.RunId);
        Assert.Equal(RunStopReason.ProcessExit, result.StopReason);
        Assert.Equal(RunActivityState.Idle, h.Recorder.State);
        Assert.Equal(0, h.Samples.LeasesHeld);
    }

    [Fact]
    public async Task The_writer_flushes_when_it_is_idle_so_rows_reach_the_disk_even_if_samples_stop()
    {
        using var h = new RecHarness(idleWaitMs: 5);
        var run = await h.StartAsync();
        var csv = h.CsvPath(run.RunId);
        h.Tick(1, 1 * S);
        h.Tick(2, 2 * S);
        h.Tick(3, 3 * S);
        await h.Barrier(run);
        Assert.True(h.Fs.PendingLength(csv) > 0); // written, not yet forced to the disk: the flush is 10 s after the last one

        h.Clock.Us = RecHarness.At(12 * S); // 11 s on, and no sample comes

        Assert.True(SpinWait.SpinUntil(() => h.Fs.PendingLength(csv) == 0, Limit));
        Assert.Equal(4, CsvLines(h, run.RunId).Length);
        Assert.Equal(RunActivityState.Recording, h.Recorder.State);
        await EndAt(h, run, 13 * S);
    }

    [Fact]
    public async Task A_run_stops_at_the_recorders_own_size_cap_with_size_limit_and_stays_readable()
    {
        const long cap = 2000;
        var samples = Enumerable.Range(1, 80).Select(n => SampleKit.Sample(n, RecHarness.At(n * S))).ToList();
        var expectedRows = RowsUntil(cap, samples);
        Assert.InRange(expectedRows, 2, samples.Count - 1);

        using var h = new RecHarness(maxCsvBytes: cap);
        var run = await h.StartAsync();
        foreach (var sample in samples)
        {
            h.Clock.Us = sample.Core.AcquiredUs;
            h.Samples.Raise(sample);
        }

        var result = await run.Completion.WaitAsync(Limit);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(RunStopReason.SizeLimit, result.StopReason);
        Assert.Equal(expectedRows, result.Rows);
        Assert.Equal(expectedRows + 1, CsvLines(h, run.RunId).Length);
        var header = h.Header(run.RunId);
        Assert.Equal(RunStopReason.SizeLimit, header.StopReason);
        Assert.Equal(expectedRows, header.Rows);
        Assert.Contains("\"size_limit\"", h.Fs.Text(h.JsonPath(run.RunId)), StringComparison.Ordinal);
        var detail = await Read(h, run.RunId);
        Assert.Equal(RunStopReason.SizeLimit, detail.Info.StopReason);
        Assert.Equal(expectedRows, detail.Info.Rows);
        Assert.Equal(RunActivityState.Idle, h.Recorder.State);
    }

    /// <summary>The number of rows after which the CSV (its header included) has reached <paramref name="cap"/> bytes, worked out from the formatter alone.</summary>
    private static int RowsUntil(long cap, IReadOnlyList<RecordingSample> samples)
    {
        var layout = RunLayout.Create([]);
        var formatter = new RunRowFormatter(layout);
        var bytes = (long)Encoding.UTF8.GetByteCount(layout.HeaderLine());
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            bytes += Encoding.UTF8.GetByteCount(formatter.Format(sample, sample.Core.AcquiredUs - RecHarness.StartUs, RecHarness.StartUs) + "\n");
            if (bytes >= cap)
            {
                return i + 1;
            }
        }

        return samples.Count;
    }

    [Fact]
    public void The_recorders_size_cap_is_below_the_size_the_reader_accepts()
    {
        Assert.Equal(240L * 1024 * 1024, RunRecorder.DefaultMaxCsvBytes);
        Assert.Equal(256L * 1024 * 1024, RunFiles.MaxCsvBytes);
        Assert.True(RunFiles.MaxCsvBytes > RunRecorder.DefaultMaxCsvBytes);
        Assert.Equal(RunRecorder.DefaultMaxCsvBytes, new RunRecorderOptions().MaxCsvBytes);
    }

    [Fact]
    public void The_size_limit_has_its_code_and_a_text_in_both_languages()
    {
        Assert.Equal("size_limit", RunCodes.Of(RunStopReason.SizeLimit));
        Assert.True(RunCodes.TryParse("size_limit", out RunStopReason parsed));
        Assert.Equal(RunStopReason.SizeLimit, parsed);

        var entry = Assert.Single(RecordStrings.All, e => e.Key == RecordStrings.KeyOf(RunStopReason.SizeLimit));
        Assert.False(string.IsNullOrWhiteSpace(entry.Ja));
        Assert.False(string.IsNullOrWhiteSpace(entry.En));
        Assert.NotEqual(entry.Ja, entry.En);
    }

    [Fact]
    public async Task The_missing_seconds_of_a_run_that_was_cut_off_reach_its_last_durable_mark_while_the_values_close_at_the_last_row()
    {
        using var h = new RecHarness();
        var (id, _) = PutCutOffRun(h, markers: [new RunMarker(MarkerKind.Mark, 5 * S, SampleKit.Wall.AddSeconds(5))]);

        var summary = (await Read(h, id)).Summary;

        Assert.Equal(5.0, summary.DurationSec, 6);
        var cpu = summary.Metric("core.cpu_usage_pct");
        Assert.NotNull(cpu);
        Assert.Equal(2.0, cpu.ObservedSeconds, 6); // the values close at the last row (3 s): 1 -> 2 -> 3
        Assert.Equal(3.0, cpu.MissingSeconds, 6); // the rest of the 5 s is missing: before the first row (1 s) and after the last (2 s)
    }

    [Fact]
    public async Task A_mark_before_the_last_row_of_a_cut_off_run_does_not_shorten_it()
    {
        using var h = new RecHarness();
        var (id, _) = PutCutOffRun(h, markers: [new RunMarker(MarkerKind.Mark, 2 * S, SampleKit.Wall.AddSeconds(2))]);

        var summary = (await Read(h, id)).Summary;

        Assert.Equal(3.0, summary.DurationSec, 6);
    }

    [Fact]
    public async Task A_delete_tries_the_lock_once_more_before_it_answers_recording()
    {
        using var h = new RecHarness();
        var (id, _) = PutCutOffRun(h);
        var holder = h.Fs.HoldLock(h.LockPath(id)); // a holder that is not a recorder, and lets go within moments
        var calls = 0;
        h.Fs.Hook = (op, _) =>
        {
            if (op == "TryLock" && Interlocked.Increment(ref calls) == 2)
            {
                holder.Dispose();
            }

            return null;
        };

        var outcome = await StoreOf(h).DeleteAsync(id, CancellationToken.None);

        Assert.Equal(RunDeleteOutcome.Deleted, outcome.Outcome);
        Assert.Equal(2, calls);
        Assert.Empty(h.Fs.Names(RecHarness.Root));
    }

    // ------------------------------------------------------------------ item 13: the fixed sample of the summary rules (§7.2), end to end

    private static async Task<RunDetail> RecordSlowGpu(RecHarness h, int ticks, Func<int, (long Seq, long AcquiredOffsetUs)> frame, long stopAtUs)
    {
        var run = await h.StartAsync();
        for (var n = 1; n <= ticks; n++)
        {
            var (seq, acquired) = frame(n);
            h.Tick(n, n * S, gpus: [Slow(seq, acquired)]);
        }

        await EndAt(h, run, stopAtUs);
        return await Read(h, run.RunId);
    }

    [Fact]
    public async Task The_spec_sample_a_source_of_two_seconds_with_a_gap_of_eight_gives_eight_seconds_missing_and_eight_observed()
    {
        using var h = new RecHarness();

        // The CSV repeats the frame every second: acquires at 0, 2, 4, 12, 14 s, a normal stop at 16 s, P = 2 s.
        var detail = await RecordSlowGpu(
            h,
            16,
            n => n switch
            {
                <= 2 => (1, 0),
                <= 4 => (2, 2 * S),
                <= 12 => (3, 4 * S),
                <= 14 => (4, 12 * S),
                _ => (5, 14 * S),
            },
            16 * S);

        var temp = detail.Summary.Metric("gpu1.temp_c");
        Assert.NotNull(temp);
        Assert.Equal(16.0, detail.Summary.DurationSec, 6);
        Assert.Equal(5, temp.NewPoints);
        Assert.Equal(8.0, temp.ObservedSeconds, 6); // [0,4) and [12,16): the value at 4 s has no known time after it (the gap is over 3P)
        Assert.Equal(8.0, temp.MissingSeconds, 6);
    }

    [Fact]
    public async Task A_gap_of_exactly_three_periods_at_two_seconds_is_valid_and_a_microsecond_more_is_missing()
    {
        using var exact = new RecHarness();
        var valid = await RecordSlowGpu(exact, 10, n => n switch { <= 2 => (1, 0), <= 8 => (2, 2 * S), _ => (3, 8 * S) }, 10 * S);
        var atSix = valid.Summary.Metric("gpu1.temp_c");
        Assert.NotNull(atSix);
        Assert.Equal(3, atSix.NewPoints);
        Assert.Equal(10.0, atSix.ObservedSeconds, 6);
        Assert.Equal(0.0, atSix.MissingSeconds, 6);

        using var over = new RecHarness();
        var broken = await RecordSlowGpu(over, 10, n => n switch { <= 2 => (1, 0), <= 8 => (2, 2 * S), _ => (3, (8 * S) + 1) }, 10 * S);
        var overSix = broken.Summary.Metric("gpu1.temp_c");
        Assert.NotNull(overSix);
        Assert.Equal(3, overSix.NewPoints);
        Assert.Equal(3.999999, overSix.ObservedSeconds, 6); // [0,2) and the last value's 1.999999 s
        Assert.Equal(6.000001, overSix.MissingSeconds, 6);
    }
}
