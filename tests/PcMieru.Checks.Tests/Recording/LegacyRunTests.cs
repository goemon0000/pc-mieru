using PcMieru.Core;

namespace PcMieru.Checks.Tests;

/// <summary>
/// The old <c>partslife-run/1</c> runs (DESIGN-B §7.1, step 7): they are only ever read. The row time stands for the acquire time, the file's own
/// period stands for the period, and the summary says "old format, estimated". Nothing is rewritten or migrated and a delete is refused.
/// </summary>
public class LegacyRunTests
{
    private const string Root = @"C:\mem\pcmieru\runs";
    private const string LegacyRoot = @"C:\mem\partslife\runs";
    private const string LegacyId = "abcd1234efgh";

    private const string Columns = "\"cpu.load_pct\",\"cpu.clock_mhz\",\"gpu1.temp_c\",\"gpu1.throttle\",\"cpu.lp0.load_pct\"";

    private const string CsvHeader = "t_s,utc,cpu.load_pct,cpu.clock_mhz,gpu1.temp_c,gpu1.throttle,cpu.lp0.load_pct\n";

    private const string CsvRows =
        "1,2026-11-21T03:00:01Z,10,3500,60,0,5\n" +
        "2,2026-11-21T03:00:02Z,20,3400,61,64,6\n" +
        "3,2026-11-21T03:00:03Z,30,3300,,0,7\n";

    private static string LegacyJson(
        string id = LegacyId,
        string status = "completed",
        string? stop = "user",
        int intervalMs = 1000,
        string endUtc = "2026-11-21T03:00:05.000Z",
        string columns = Columns,
        string markers = "[{\"kind\":\"bench-start\",\"tSec\":1.5,\"utc\":\"2026-11-21T03:00:01.500Z\"},{\"kind\":\"bench-end\",\"tSec\":2.5,\"utc\":\"2026-11-21T03:00:02.500Z\"}]") =>
        $$"""
        {
          "format": "partslife-run",
          "formatVersion": 1,
          "runId": "{{id}}",
          "app": { "name": "PartsLife", "version": "1.2.3" },
          "startUtc": "2026-11-21T03:00:00.000Z",
          "endUtc": "{{endUtc}}",
          "intervalMs": {{intervalMs}},
          "status": "{{status}}",
          "stopReason": "{{stop}}",
          "markers": {{markers}},
          "gpus": [ { "id": "gpu1", "name": "Legacy GPU" } ],
          "stream": { "columns": [ {{columns}} ] },
          "stats": { "samples": 3 }
        }
        """;

    private static MemRunFs PutLegacy(string json, string csv = CsvHeader + CsvRows, string id = LegacyId, bool withCsv = true)
    {
        var fs = new MemRunFs();
        fs.Put(Path.Combine(LegacyRoot, LegacyRun.JsonName(id)), json);
        if (withCsv)
        {
            fs.Put(Path.Combine(LegacyRoot, LegacyRun.CsvName(id)), csv);
        }

        return fs;
    }

    private static RunStore StoreOf(MemRunFs fs) => new(fs, Root, LegacyRoot);

    private static LegacyHeader HeaderOf(string json) =>
        LegacyRun.ParseHeader(System.Text.Encoding.UTF8.GetBytes(json), "run-x.json", "sensors-x.csv");

    // ------------------------------------------------------------------ the header and the list

    [Fact]
    public async Task An_old_run_is_listed_with_its_header_and_a_stable_new_id()
    {
        var fs = PutLegacy(LegacyJson());
        var list = await StoreOf(fs).ListAsync(CancellationToken.None);
        Assert.Empty(list.Problems);
        var run = Assert.Single(list.Runs);

        Assert.True(run.IsLegacy);
        Assert.Equal(LegacyId, run.LegacyId);
        Assert.Equal(RunCodes.LegacyFormat, run.Format);
        Assert.Equal(LegacyRun.IdOf(LegacyId), run.RunId);
        Assert.Equal("PartsLife 1.2.3", run.App);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(RunStopReason.User, run.StopReason);
        Assert.Equal(5_000_000, run.DurationUs);
        Assert.Equal(new[] { MarkerKind.BenchStart, MarkerKind.BenchEnd }, run.Markers.Select(m => m.Kind).ToArray());
        Assert.Equal(1_500_000, run.Markers[0].OffsetUs);
        Assert.Contains(run.Sources, s => s.Source == "gpu1" && s.Name == "Legacy GPU");

        // The same listing gives the same id (it is derived from the old name, nothing was written).
        Assert.Equal(run.RunId, Assert.Single((await StoreOf(fs).ListAsync(CancellationToken.None)).Runs).RunId);
        Assert.NotEqual(LegacyRun.IdOf(LegacyId), LegacyRun.IdOf("zzzz9999yyyy"));
    }

    [Fact]
    public async Task New_and_old_runs_are_listed_together_newest_first()
    {
        var fs = PutLegacy(LegacyJson());
        var newer = RecHarness.IdOf(1);
        var layout = RunLayout.Create([SampleKit.Gpu()]);
        RunFileKit.PutRun(
            fs,
            Root,
            RunFileKit.HeaderOf(layout, newer, RunStatus.Completed, RunStopReason.User, 3_000_000, 0, SampleKit.Wall.AddDays(1), SampleKit.Wall.AddDays(1).AddSeconds(3)),
            layout.HeaderLine());

        var list = await StoreOf(fs).ListAsync(CancellationToken.None);
        Assert.Equal(new[] { false, true }, list.Runs.Select(r => r.IsLegacy).ToArray());
        Assert.Equal(newer, list.Runs[0].RunId);
    }

    [Fact]
    public async Task The_old_folder_is_not_read_unless_it_is_given()
    {
        var fs = PutLegacy(LegacyJson());
        var store = new RunStore(fs, Root, null);
        Assert.Empty((await store.ListAsync(CancellationToken.None)).Runs);
        Assert.Null(await store.ReadAsync(LegacyRun.IdOf(LegacyId), CancellationToken.None));
    }

    [Theory]
    [InlineData("completed", "user", RunStatus.Completed, RunStopReason.User)]
    [InlineData("completed", "", RunStatus.Completed, null)]
    [InlineData("recording", "", RunStatus.Interrupted, RunStopReason.Unknown)]
    [InlineData("interrupted", "io-error", RunStatus.Interrupted, RunStopReason.IoError)]
    [InlineData("something-else", "", RunStatus.Failed, RunStopReason.Unknown)]
    public void The_old_status_and_stop_reason_are_read_into_the_new_ones(string status, string stop, RunStatus expectedStatus, RunStopReason? expectedStop)
    {
        var header = HeaderOf(LegacyJson(status: status, stop: stop)).Info;
        Assert.Equal(expectedStatus, header.Status);
        Assert.Equal(expectedStop, header.StopReason);
    }

    [Theory]
    [InlineData("run-notes.json")]
    [InlineData("run-ABCD1234EFGH.json")]
    [InlineData("run-short.json")]
    public async Task An_old_file_name_with_no_valid_id_is_not_a_run(string name)
    {
        var fs = new MemRunFs();
        fs.Put(Path.Combine(LegacyRoot, name), LegacyJson());
        var list = await StoreOf(fs).ListAsync(CancellationToken.None);
        Assert.Empty(list.Runs);
        Assert.Equal("not_a_run", Assert.Single(list.Problems).Reason);
    }

    [Theory]
    [InlineData("{\"format\":\"pcm-run/1\"}")]
    [InlineData("{\"format\":\"partslife-run\",\"formatVersion\":2,\"runId\":\"abcd1234efgh\",\"startUtc\":\"2026-11-21T03:00:00Z\"}")]
    [InlineData("{\"format\":\"partslife-run\",\"formatVersion\":1,\"runId\":\"abcd1234efgh\"}")]
    [InlineData("not json")]
    public async Task An_old_json_that_is_not_readable_as_one_is_a_problem(string json)
    {
        var fs = PutLegacy(json);
        var list = await StoreOf(fs).ListAsync(CancellationToken.None);
        Assert.Empty(list.Runs);
        Assert.Equal("bad_format", Assert.Single(list.Problems).Reason);
    }

    // ------------------------------------------------------------------ the series

    [Fact]
    public async Task The_summary_of_an_old_run_says_old_format_estimated_and_uses_the_row_time_as_the_acquire_time()
    {
        var fs = PutLegacy(LegacyJson());
        var detail = await StoreOf(fs).ReadAsync(LegacyRun.IdOf(LegacyId), CancellationToken.None);
        Assert.NotNull(detail);

        Assert.Equal(SummaryFreshness.LegacyEstimated, detail.Summary.Freshness);
        Assert.True(detail.Summary.HasNote("legacy-estimated"));
        Assert.Equal(3, detail.Info.Rows);
        Assert.Equal(4.0, detail.Summary.DurationSec, 6); // the last row's time and one period
        var cpu = detail.Summary.Metric("core.cpu_usage_pct");
        Assert.NotNull(cpu);
        Assert.Equal(3.0, cpu.ObservedSeconds, 6);
        Assert.Equal(1.0, cpu.MissingSeconds, 6);
        Assert.Equal(3, cpu.NewPoints);
        var temp = detail.Summary.Metric("gpu1.temp_c");
        Assert.NotNull(temp);
        Assert.Equal(2, temp.NewPoints); // the third cell was empty: could not be read
        Assert.Equal(2.0, temp.ObservedSeconds, 6);
        Assert.Equal(2.0, temp.MissingSeconds, 6);
        Assert.Contains(detail.Summary.Limits.SelectMany(l => l.Reasons), r => r.Name == "hw-thermal-slowdown" && r.Seconds > 0);
    }

    [Fact]
    public void The_columns_are_the_new_keys_and_the_per_logical_processor_columns_are_left_out()
    {
        var header = HeaderOf(LegacyJson());
        Assert.Equal(
            new[] { "core.cpu_usage_pct", "core.cpu_mhz", "gpu1.temp_c", "gpu1.limit_reasons" },
            header.Info.Metrics.Select(m => m.Key).ToArray());
        Assert.Equal(RunMetricKind.Bitmask, header.Info.Metrics[3].Kind);

        var data = LegacyRun.ReadSeries(header, System.Text.Encoding.UTF8.GetBytes(CsvHeader + CsvRows));
        Assert.Equal(4, data.Metrics.Count);
        Assert.Equal(4, data.Series.Tracks.Count);
        Assert.DoesNotContain(data.Series.Tracks, t => t.Key.Contains(".lp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("cpu.load_pct", "core.cpu_usage_pct", "core")]
    [InlineData("cpu.clock_mhz", "core.cpu_mhz", "core")]
    [InlineData("cpu.pkg_power_w", "core.cpu_package_w", "core")]
    [InlineData("power.ac", "core.ac_connected", "core")]
    [InlineData("power.battery_pct", "core.battery_pct", "core")]
    [InlineData("gpu2.util_pct", "core.gpu2_util_pct", "core")]
    [InlineData("gpu2.temp_c", "gpu2.temp_c", "gpu2")]
    [InlineData("gpu2.power_w", "gpu2.power_w", "gpu2")]
    [InlineData("gpu2.fan_pct", "gpu2.fan_pct", "gpu2")]
    [InlineData("gpu2.clock_mhz", "gpu2.clock_mhz", "gpu2")]
    [InlineData("gpu2.throttle", "gpu2.limit_reasons", "gpu2")]
    [InlineData("cpu.temp_c", "cpu.temp_c", "legacy")]
    [InlineData("cpu.perf_limit_pct", "cpu.perf_limit_pct", "legacy")]
    public void An_old_column_is_mapped_to_its_new_key_or_keeps_its_own(string old, string key, string source)
    {
        var metric = LegacyRun.Map(old);
        Assert.Equal(key, metric.Key);
        Assert.Equal(source, metric.Source);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(2000, 2000)]
    [InlineData(500, 500)]
    [InlineData(50, 100)]
    public void The_file_s_own_period_is_the_period_of_every_value_and_it_is_at_least_100_ms(int intervalMs, int expectedMs)
    {
        var header = HeaderOf(LegacyJson(intervalMs: intervalMs));
        var data = LegacyRun.ReadSeries(header, System.Text.Encoding.UTF8.GetBytes(CsvHeader + CsvRows));
        var cpu = Assert.Single(data.Series.Tracks, t => t.Key == "core.cpu_usage_pct");
        Assert.Equal(expectedMs, cpu.Points[0].PeriodMs);
        Assert.Equal(1_000_000, cpu.Points[0].AtUs); // the row time is the acquire time
        Assert.Equal(2_000_000, cpu.Points[1].AtUs);
        Assert.True(data.Series.Legacy);
    }

    [Fact]
    public void A_bit_mask_that_went_through_a_double_is_the_number_it_holds()
    {
        const string csv =
            "t_s,utc,gpu1.throttle\n" +
            "1,2026-11-21T03:00:01Z,64\n" +
            "2,2026-11-21T03:00:02Z,1.8446744073709552E+19\n" +
            "3,2026-11-21T03:00:03Z,0\n";
        var header = HeaderOf(LegacyJson(columns: "\"gpu1.throttle\""));
        var data = LegacyRun.ReadSeries(header, System.Text.Encoding.UTF8.GetBytes(csv));
        var track = Assert.Single(data.Series.Tracks);
        Assert.Equal(new[] { 64UL, ulong.MaxValue, 0UL }, track.Points.Select(p => p.Bits).ToArray());
    }

    [Fact]
    public async Task A_damaged_last_line_and_bad_lines_are_skipped_and_the_rest_is_read()
    {
        const string csv =
            CsvHeader +
            "1,2026-11-21T03:00:01Z,10,3500,60,0,5\n" +
            "2,2026-11-21T03:00:02Z,20,3400\n" + // short
            "x,2026-11-21T03:00:03Z,20,3400,61,0,5\n" + // no time
            "1,2026-11-21T03:00:03Z,20,3400,61,0,5\n" + // the time stood still
            "3,2026-11-21T03:00:03Z,30,3300,62,0,7\n" +
            "4,2026-11-21T03:00:04Z,40,32"; // cut off
        var fs = PutLegacy(LegacyJson(status: "interrupted", stop: ""), csv);
        var detail = await StoreOf(fs).ReadAsync(LegacyRun.IdOf(LegacyId), CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Info.Rows);
        Assert.Equal(RunStatus.Interrupted, detail.Info.Status);
        Assert.Equal(3.0, detail.Summary.DurationSec, 6); // an interrupted run ends at its last good row
        Assert.True(detail.Summary.HasNote("not-completed"));
    }

    [Fact]
    public async Task An_old_run_whose_csv_is_missing_cannot_be_read()
    {
        var fs = PutLegacy(LegacyJson(), withCsv: false);
        await Assert.ThrowsAsync<FileNotFoundException>(() => StoreOf(fs).ReadAsync(LegacyRun.IdOf(LegacyId), CancellationToken.None));
    }

    [Fact]
    public async Task An_old_csv_whose_first_columns_are_not_t_s_and_utc_is_not_read()
    {
        var fs = PutLegacy(LegacyJson(), "time,when,cpu.load_pct\n1,x,2\n");
        await Assert.ThrowsAsync<FormatException>(() => StoreOf(fs).ReadAsync(LegacyRun.IdOf(LegacyId), CancellationToken.None));
    }

    // ------------------------------------------------------------------ read only

    [Fact]
    public async Task Listing_reading_and_asking_for_a_delete_never_change_an_old_run()
    {
        var fs = PutLegacy(LegacyJson());
        var jsonPath = Path.Combine(LegacyRoot, LegacyRun.JsonName(LegacyId));
        var csvPath = Path.Combine(LegacyRoot, LegacyRun.CsvName(LegacyId));
        var jsonBefore = fs.DurableBytes(jsonPath);
        var csvBefore = fs.DurableBytes(csvPath);
        var store = StoreOf(fs);
        var id = LegacyRun.IdOf(LegacyId);

        await store.ListAsync(CancellationToken.None);
        Assert.NotNull(await store.ReadAsync(id, CancellationToken.None));
        var refused = await store.DeleteAsync(id, CancellationToken.None);

        Assert.Equal(RunDeleteOutcome.ReadOnlyLegacy, refused.Outcome);
        Assert.Equal(jsonBefore, fs.DurableBytes(jsonPath));
        Assert.Equal(csvBefore, fs.DurableBytes(csvPath));
        Assert.Equal(0, fs.Count("Delete") + fs.Count("Create") + fs.Count("CreateNew") + fs.Count("ReplaceAtomic") + fs.Count("Write") + fs.Count("FlushToDisk") + fs.Count("TryLock"));
        Assert.Equal(new[] { LegacyRun.JsonName(LegacyId), LegacyRun.CsvName(LegacyId) }, fs.Names(LegacyRoot).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_recorder_that_starts_leaves_the_old_folder_alone()
    {
        // The recorder knows only its own folder: starting a run neither recovers nor lists anything of the old format.
        using var h = new RecHarness();
        h.Fs.Put(Path.Combine(LegacyRoot, LegacyRun.JsonName(LegacyId)), LegacyJson(status: "recording", stop: ""));
        h.Fs.Put(Path.Combine(LegacyRoot, LegacyRun.CsvName(LegacyId)), CsvHeader + CsvRows);
        var before = h.Fs.DurableBytes(Path.Combine(LegacyRoot, LegacyRun.JsonName(LegacyId)));

        var run = await h.StartAsync();
        await h.Recorder.StopAsync(run, RunStopReason.User);

        Assert.Equal(before, h.Fs.DurableBytes(Path.Combine(LegacyRoot, LegacyRun.JsonName(LegacyId))));
        Assert.DoesNotContain(h.Fs.Log, e => e.Contains(LegacyId, StringComparison.Ordinal) && !e.StartsWith("ListFiles", StringComparison.Ordinal));
    }
}
