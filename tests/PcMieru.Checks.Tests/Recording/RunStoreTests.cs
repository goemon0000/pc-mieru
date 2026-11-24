using PcMieru.Core;

namespace PcMieru.Checks.Tests;

/// <summary>
/// The list, the detail and the delete of finished runs (DESIGN-B §2 <see cref="IRunStore"/>, §7.1): the listing reads JSON only and never writes,
/// a file that is not a run is a problem next to the good ones, a run that says "recording" without a holder is shown as interrupted, and a run
/// that is being recorded cannot be deleted. Everything is in memory; a fault is put in through the file-system seam.
/// </summary>
public class RunStoreTests
{
    private const long S = 1_000_000;

    private static RunStore StoreOf(RecHarness h) => new(h.Fs, RecHarness.Root, null);

    /// <summary>Puts a run the recorder did not write into the harness's folder: its JSON and (unless told not to) its CSV, all of it durable.</summary>
    private static RunHeader PutRun(
        RecHarness h,
        int n,
        RunStatus status,
        RunStopReason? stop,
        int rows = 3,
        DateTime? started = null,
        long durationUs = 0,
        bool withCsv = true,
        IReadOnlyList<RunMarker>? markers = null)
    {
        var id = RecHarness.IdOf(n);
        var layout = RunLayout.Create([SampleKit.Gpu()]);
        var samples = Enumerable.Range(1, rows)
            .Select(i => SampleKit.Sample(i, RecHarness.At(i * S), gpus: [SampleKit.Gpu(sequence: i, acquiredUs: RecHarness.At(i * S))]))
            .ToList();
        var begin = started ?? SampleKit.Wall;
        var header = RunFileKit.HeaderOf(
            layout,
            id,
            status,
            stop,
            durationUs,
            rows,
            begin,
            status == RunStatus.Completed ? begin.AddTicks(durationUs * 10) : null,
            markers);
        RunFileKit.PutRun(h.Fs, RecHarness.Root, header, withCsv ? RunFileKit.CsvText(layout, RecHarness.StartUs, samples) : null);
        return header;
    }

    private static int Writes(MemRunFs fs) =>
        fs.Count("Create") + fs.Count("CreateNew") + fs.Count("ReplaceAtomic") + fs.Count("Delete") + fs.Count("Write") + fs.Count("FlushToDisk");

    // ------------------------------------------------------------------ the list

    [Fact]
    public async Task The_list_is_newest_first_and_then_by_id()
    {
        using var h = new RecHarness();
        PutRun(h, 1, RunStatus.Completed, RunStopReason.User, started: SampleKit.Wall, durationUs: 3 * S);
        PutRun(h, 2, RunStatus.Completed, RunStopReason.User, started: SampleKit.Wall.AddHours(2), durationUs: 3 * S);
        PutRun(h, 3, RunStatus.Completed, RunStopReason.User, started: SampleKit.Wall.AddHours(1), durationUs: 3 * S);
        PutRun(h, 4, RunStatus.Completed, RunStopReason.User, started: SampleKit.Wall.AddHours(1), durationUs: 3 * S);

        var list = await StoreOf(h).ListAsync(CancellationToken.None);
        Assert.Empty(list.Problems);
        Assert.Equal(
            new[] { RecHarness.IdOf(2), RecHarness.IdOf(3), RecHarness.IdOf(4), RecHarness.IdOf(1) },
            list.Runs.Select(r => r.RunId).ToArray());
    }

    [Fact]
    public async Task A_listing_reads_no_csv_and_writes_nothing()
    {
        using var h = new RecHarness();
        PutRun(h, 1, RunStatus.Completed, RunStopReason.User, durationUs: 4 * S);
        PutRun(h, 2, RunStatus.Recording, null);
        var json1 = h.Fs.DurableBytes(h.JsonPath(RecHarness.IdOf(1)));
        var json2 = h.Fs.DurableBytes(h.JsonPath(RecHarness.IdOf(2)));

        var list = await StoreOf(h).ListAsync(CancellationToken.None);
        Assert.Equal(2, list.Runs.Count);
        Assert.Equal(0, Writes(h.Fs));
        Assert.Equal(json1, h.Fs.DurableBytes(h.JsonPath(RecHarness.IdOf(1))));
        Assert.Equal(json2, h.Fs.DurableBytes(h.JsonPath(RecHarness.IdOf(2))));
        Assert.DoesNotContain(h.Fs.Log, entry => entry.Contains(".csv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_that_says_recording_is_recording_while_its_lock_is_held_and_interrupted_when_it_is_not()
    {
        using var h = new RecHarness();
        var header = PutRun(h, 1, RunStatus.Recording, null);
        var store = StoreOf(h);

        var cutOff = Assert.Single((await store.ListAsync(CancellationToken.None)).Runs);
        Assert.Equal(RunStatus.Interrupted, cutOff.Status);
        Assert.Equal(RunStopReason.Unknown, cutOff.StopReason);
        Assert.Null(cutOff.EndedAtUtc);

        using (h.Fs.HoldLock(h.LockPath(header.RunId)))
        {
            var live = Assert.Single((await store.ListAsync(CancellationToken.None)).Runs);
            Assert.Equal(RunStatus.Recording, live.Status);
            Assert.Null(live.StopReason);
        }

        // The file itself is still the one the recorder wrote: only the view changed.
        Assert.Equal(RunStatus.Recording, h.Header(header.RunId).Status);
    }

    [Fact]
    public async Task A_file_that_is_not_a_run_is_a_problem_next_to_the_good_runs()
    {
        using var h = new RecHarness();
        PutRun(h, 1, RunStatus.Completed, RunStopReason.User, durationUs: 3 * S);
        h.Fs.Put(Path.Combine(RecHarness.Root, "run-notes.json"), "{}");
        h.Fs.Put(h.JsonPath(RecHarness.IdOf(7)), "{ this is not json");
        h.Fs.Put(h.JsonPath(RecHarness.IdOf(8)), RunJson.Write(h.Header(RecHarness.IdOf(1)))); // a copy of run 1 under another name

        var list = await StoreOf(h).ListAsync(CancellationToken.None);
        Assert.Equal(RecHarness.IdOf(1), Assert.Single(list.Runs).RunId);
        Assert.Contains(list.Problems, p => p.File == "run-notes.json" && p.Reason == "not_a_run");
        Assert.Contains(list.Problems, p => p.File == RunFiles.JsonName(RecHarness.IdOf(7)) && p.Reason == "bad_format");
        Assert.Contains(list.Problems, p => p.File == RunFiles.JsonName(RecHarness.IdOf(8)) && p.Reason == "bad_format");
        Assert.Equal(3, list.Problems.Count);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_a_problem_and_the_others_are_still_listed()
    {
        using var h = new RecHarness();
        PutRun(h, 1, RunStatus.Completed, RunStopReason.User, durationUs: 3 * S);
        PutRun(h, 2, RunStatus.Completed, RunStopReason.User, durationUs: 3 * S);
        var locked = RunFiles.JsonName(RecHarness.IdOf(2));
        h.Fs.Fail("ReadAllBytes", Kit.SharingViolation, name => name.EndsWith(locked, StringComparison.Ordinal));

        var list = await StoreOf(h).ListAsync(CancellationToken.None);
        Assert.Equal(RecHarness.IdOf(1), Assert.Single(list.Runs).RunId);
        var problem = Assert.Single(list.Problems);
        Assert.Equal(locked, problem.File);
        Assert.Equal("unreadable", problem.Reason);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_listed_is_one_problem_and_no_exception()
    {
        using var h = new RecHarness();
        h.Fs.Fail("ListFiles", Kit.SharingViolation);
        var list = await StoreOf(h).ListAsync(CancellationToken.None);
        Assert.Empty(list.Runs);
        Assert.Equal("unreadable", Assert.Single(list.Problems).Reason);
    }

    [Fact]
    public async Task An_empty_folder_is_an_empty_list()
    {
        using var h = new RecHarness();
        var list = await StoreOf(h).ListAsync(CancellationToken.None);
        Assert.Empty(list.Runs);
        Assert.Empty(list.Problems);
    }

    [Fact]
    public async Task A_cancelled_listing_throws_and_does_not_hand_back_half_a_list()
    {
        using var h = new RecHarness();
        PutRun(h, 1, RunStatus.Completed, RunStopReason.User, durationUs: 3 * S);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StoreOf(h).ListAsync(cts.Token));
    }

    [Fact]
    public async Task A_store_made_with_a_folder_and_no_old_folder_never_looks_at_the_real_ones()
    {
        using var temp = new TempRoot();
        var store = new RunStore(temp.Path);
        var list = await store.ListAsync(CancellationToken.None);
        Assert.Empty(list.Runs);
        Assert.Empty(list.Problems);
        Assert.Null(await store.ReadAsync(RecHarness.IdOf(1), CancellationToken.None));
        Assert.Equal(RunDeleteOutcome.NotFound, (await store.DeleteAsync(RecHarness.IdOf(1), CancellationToken.None)).Outcome);
    }

    // ------------------------------------------------------------------ the detail

    [Fact]
    public async Task Reading_a_run_that_is_not_there_gives_null()
    {
        using var h = new RecHarness();
        Assert.Null(await StoreOf(h).ReadAsync(RecHarness.IdOf(1), CancellationToken.None));
    }

    [Fact]
    public async Task The_detail_has_the_rows_of_the_csv_and_the_summary_of_its_seconds()
    {
        using var h = new RecHarness();
        PutRun(h, 1, RunStatus.Completed, RunStopReason.User, rows: 3, durationUs: 4 * S);

        var detail = await StoreOf(h).ReadAsync(RecHarness.IdOf(1), CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(3, detail.Info.Rows);
        Assert.False(detail.Info.IsLegacy);
        Assert.Equal(RunCodes.Format, detail.Info.Format);
        Assert.Equal(SummaryFreshness.Exact, detail.Summary.Freshness);
        Assert.Equal(4.0, detail.Summary.DurationSec, 6);
        var cpu = detail.Summary.Metric("core.cpu_usage_pct");
        Assert.NotNull(cpu);
        Assert.Equal(3.0, cpu.ObservedSeconds, 6); // 1 s to the first value is missing; then 1, 2, 3 s with one period for the last one
        Assert.Equal(1.0, cpu.MissingSeconds, 6);
    }

    [Fact]
    public async Task A_completed_runs_duration_is_the_longer_of_its_json_and_its_last_row_and_an_interrupted_one_ends_at_its_last_row()
    {
        using var h = new RecHarness();
        PutRun(h, 1, RunStatus.Completed, RunStopReason.User, rows: 3, durationUs: 2 * S); // the JSON is behind the CSV
        PutRun(h, 2, RunStatus.Interrupted, RunStopReason.Unknown, rows: 3, durationUs: 10 * S); // the JSON's figure is not trusted
        var store = StoreOf(h);

        var completed = await store.ReadAsync(RecHarness.IdOf(1), CancellationToken.None);
        var interrupted = await store.ReadAsync(RecHarness.IdOf(2), CancellationToken.None);
        Assert.NotNull(completed);
        Assert.NotNull(interrupted);
        Assert.Equal(3.0, completed.Summary.DurationSec, 6);
        Assert.Equal(3.0, interrupted.Summary.DurationSec, 6);
        Assert.True(interrupted.Summary.HasNote("not-completed"));
    }

    [Fact]
    public async Task A_run_whose_csv_is_gone_is_an_empty_run_with_a_note()
    {
        using var h = new RecHarness();
        PutRun(h, 1, RunStatus.Completed, RunStopReason.User, rows: 0, durationUs: 5 * S, withCsv: false);
        var detail = await StoreOf(h).ReadAsync(RecHarness.IdOf(1), CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(0, detail.Info.Rows);
        Assert.True(detail.Summary.HasNote("no-samples"));
        Assert.DoesNotContain(detail.Summary.Metrics, m => m.NewPoints > 0);
    }

    [Fact]
    public async Task A_json_that_cannot_be_parsed_or_names_another_id_throws_and_one_that_cannot_be_read_throws_io()
    {
        using var h = new RecHarness();
        h.Fs.Put(h.JsonPath(RecHarness.IdOf(1)), "not json at all");
        PutRun(h, 2, RunStatus.Completed, RunStopReason.User, durationUs: 3 * S);
        h.Fs.Put(h.JsonPath(RecHarness.IdOf(3)), h.Fs.Text(h.JsonPath(RecHarness.IdOf(2)))); // run 2's header under run 3's name
        PutRun(h, 4, RunStatus.Completed, RunStopReason.User, durationUs: 3 * S);
        var store = StoreOf(h);

        await Assert.ThrowsAsync<FormatException>(() => store.ReadAsync(RecHarness.IdOf(1), CancellationToken.None));
        await Assert.ThrowsAsync<FormatException>(() => store.ReadAsync(RecHarness.IdOf(3), CancellationToken.None));
        h.Fs.Fail("ReadAllBytes", Kit.SharingViolation, name => name.EndsWith(RunFiles.CsvName(RecHarness.IdOf(4)), StringComparison.Ordinal));
        await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(RecHarness.IdOf(4), CancellationToken.None));
    }

    [Fact]
    public async Task A_csv_that_is_cut_off_in_its_last_line_is_read_up_to_the_last_whole_line()
    {
        using var h = new RecHarness();
        var header = PutRun(h, 1, RunStatus.Interrupted, RunStopReason.Unknown, rows: 3);
        h.Fs.Put(h.CsvPath(header.RunId), h.Fs.Text(h.CsvPath(header.RunId)) + "4000000,2026-11-21T03:00:0");

        var detail = await StoreOf(h).ReadAsync(header.RunId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(3, detail.Info.Rows);
        Assert.Equal(3.0, detail.Summary.DurationSec, 6);
    }

    // ------------------------------------------------------------------ the delete

    [Fact]
    public async Task A_finished_run_is_deleted_with_its_csv_and_its_leftover_tmp()
    {
        using var h = new RecHarness();
        PutRun(h, 1, RunStatus.Completed, RunStopReason.User, durationUs: 3 * S);
        PutRun(h, 2, RunStatus.Completed, RunStopReason.User, durationUs: 3 * S);
        h.Fs.Put(h.TempPath(RecHarness.IdOf(1)), "half a save");

        var result = await StoreOf(h).DeleteAsync(RecHarness.IdOf(1), CancellationToken.None);
        Assert.Equal(RunDeleteOutcome.Deleted, result.Outcome);
        Assert.Equal(
            new[] { RunFiles.JsonName(RecHarness.IdOf(2)), RunFiles.CsvName(RecHarness.IdOf(2)) },
            h.Fs.Names(RecHarness.Root).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Null(await StoreOf(h).ReadAsync(RecHarness.IdOf(1), CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_a_run_that_is_not_there_says_so_and_deleting_one_whose_csv_is_gone_still_works()
    {
        using var h = new RecHarness();
        var store = StoreOf(h);
        Assert.Equal(RunDeleteOutcome.NotFound, (await store.DeleteAsync(RecHarness.IdOf(1), CancellationToken.None)).Outcome);

        PutRun(h, 2, RunStatus.Completed, RunStopReason.User, rows: 0, durationUs: 3 * S, withCsv: false);
        Assert.Equal(RunDeleteOutcome.Deleted, (await store.DeleteAsync(RecHarness.IdOf(2), CancellationToken.None)).Outcome);
        Assert.Empty(h.Fs.Names(RecHarness.Root));
    }

    [Fact]
    public async Task A_run_whose_lock_is_held_is_never_deleted()
    {
        using var h = new RecHarness();
        var header = PutRun(h, 1, RunStatus.Recording, null);
        using (h.Fs.HoldLock(h.LockPath(header.RunId)))
        {
            var result = await StoreOf(h).DeleteAsync(header.RunId, CancellationToken.None);
            Assert.Equal(RunDeleteOutcome.Recording, result.Outcome);
            Assert.True(h.Fs.Exists(h.JsonPath(header.RunId)));
            Assert.True(h.Fs.Exists(h.CsvPath(header.RunId)));
            Assert.Equal(0, h.Fs.Count("Delete"));
        }

        // Once nobody holds it (the program died), it can go.
        Assert.Equal(RunDeleteOutcome.Deleted, (await StoreOf(h).DeleteAsync(header.RunId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_file_that_cannot_be_deleted_is_a_failure_with_its_message_and_the_json_is_the_last_to_go()
    {
        using var h = new RecHarness();
        var header = PutRun(h, 1, RunStatus.Completed, RunStopReason.User, durationUs: 3 * S);
        h.Fs.Fail("Delete", Kit.SharingViolation, name => name.EndsWith(".csv", StringComparison.Ordinal));

        var result = await StoreOf(h).DeleteAsync(header.RunId, CancellationToken.None);
        Assert.Equal(RunDeleteOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.True(h.Fs.Exists(h.JsonPath(header.RunId))); // the CSV is tried first, so the JSON is still there to show the run
        Assert.False(h.Fs.IsLockHeld(h.LockPath(header.RunId)));

        h.Fs.ClearFaults();
        Assert.Equal(RunDeleteOutcome.Deleted, (await StoreOf(h).DeleteAsync(header.RunId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_finished_run_the_recorder_wrote_can_be_listed_read_and_deleted()
    {
        using var h = new RecHarness();
        var run = await h.StartAsync();
        h.Tick(1, 1 * S);
        h.Tick(2, 2 * S);
        h.Clock.Us = RecHarness.At(3 * S);
        await h.Recorder.StopAsync(run, RunStopReason.User);

        var store = StoreOf(h);
        var info = Assert.Single((await store.ListAsync(CancellationToken.None)).Runs);
        Assert.Equal(run.RunId, info.RunId);
        Assert.Equal(RunStatus.Completed, info.Status);
        Assert.Equal(RunStopReason.User, info.StopReason);
        var detail = await store.ReadAsync(run.RunId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Info.Rows);
        Assert.Equal(RunDeleteOutcome.Deleted, (await store.DeleteAsync(run.RunId, CancellationToken.None)).Outcome);
        Assert.Empty(h.Fs.Names(RecHarness.Root));
    }
}
