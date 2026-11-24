using System.Globalization;
using System.Text;
using PcMieru.Core;
using static PcMieru.Checks.Tests.SampleKit;

namespace PcMieru.Checks.Tests;

/// <summary>The wide CSV (DESIGN-B §7.1): the columns, a row, fresh against repeat by frame id, the missing reasons, and the tolerant reader.</summary>
public class RunCsvTests
{
    private const long Start = 1_000_000;

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private static string[] Cells(RunLayout layout, string row)
    {
        var cells = RunCsvReader.SplitFields(row);
        Assert.Equal(layout.Header.Count, cells.Count);
        return [.. cells];
    }

    private static string Cell(RunLayout layout, string[] cells, string column)
    {
        var index = layout.Header.IndexOf(column);
        Assert.True(index >= 0, "no column " + column);
        return cells[index];
    }

    /// <summary>A CSV as the writer writes it: the header, then one row per sample (offset = acquired - start).</summary>
    private static (RunLayout Layout, string Text) Csv(IReadOnlyList<RecordedGpu> gpus, params RecordingSample[] samples) => (RunLayout.Create(gpus), RunFileKit.CsvText(RunLayout.Create(gpus), 0, samples));

    private static RunCsvData Read(IReadOnlyList<RecordedGpu> gpus, params RecordingSample[] samples)
    {
        var (layout, text) = Csv(gpus, samples);
        return RunCsvReader.Read(Utf8(text), layout.Metrics);
    }

    private static MetricTrack Track(RunCsvData data, string key) => data.Tracks.Single(t => t.Key == key);

    // ------------------------------------------------------------------ the columns

    [Fact]
    public void The_header_without_a_gpu_is_exactly_the_documented_columns()
    {
        var layout = RunLayout.Create([]);

        Assert.Equal(
            "offsetUs,utc,core.frame,core.acquiredOffsetUs,core.periodMs,core.state,core.reason,"
            + "core.cpu_usage_pct,core.cpu_mhz,core.cpu_package_w,core.mem_total_bytes,core.mem_available_bytes,core.mem_commit_bytes,"
            + "core.mem_commit_limit_bytes,core.mem_cache_bytes,core.mem_paged_pool_bytes,core.mem_nonpaged_pool_bytes,"
            + "core.ac_connected,core.battery_pct,core.battery_state\n",
            layout.HeaderLine());
        Assert.Equal([new RunSourceInfo("core", null, "core")], layout.SourceInfos);
    }

    [Fact]
    public void The_header_with_one_gpu_adds_its_core_columns_and_its_own_source()
    {
        var layout = RunLayout.Create([Gpu()]);

        Assert.Equal(
            "offsetUs,utc,core.frame,core.acquiredOffsetUs,core.periodMs,core.state,core.reason,"
            + "core.cpu_usage_pct,core.cpu_mhz,core.cpu_package_w,core.mem_total_bytes,core.mem_available_bytes,core.mem_commit_bytes,"
            + "core.mem_commit_limit_bytes,core.mem_cache_bytes,core.mem_paged_pool_bytes,core.mem_nonpaged_pool_bytes,"
            + "core.ac_connected,core.battery_pct,core.battery_state,core.gpu1_util_pct,core.gpu1_vram_used_bytes,"
            + "gpu1.frame,gpu1.acquiredOffsetUs,gpu1.periodMs,gpu1.state,gpu1.reason,"
            + "gpu1.temp_c,gpu1.power_w,gpu1.fan_pct,gpu1.clock_mhz,gpu1.temp_limit_c,gpu1.limit_reasons\n",
            layout.HeaderLine());
        Assert.Equal(new RunSourceInfo("gpu1", "gpu-a", "Test GPU A"), layout.SourceInfos[1]);

        // The JSON's metrics list is the value columns, with their unit, source and kind.
        Assert.Contains(new RunMetricInfo("core.gpu1_util_pct", "%", "core", RunMetricKind.Number), layout.Metrics);
        Assert.Contains(new RunMetricInfo("gpu1.limit_reasons", "", "gpu1", RunMetricKind.Bitmask), layout.Metrics);
        Assert.Contains(new RunMetricInfo("core.battery_state", "", "core", RunMetricKind.Text), layout.Metrics);
        Assert.Contains(new RunMetricInfo("core.ac_connected", "", "core", RunMetricKind.Flag), layout.Metrics);
    }

    [Fact]
    public void Gpus_are_numbered_from_one_in_the_order_of_their_stable_ids()
    {
        // Handed over as b then a; a is gpu1 whatever the order the sampler listed them in.
        var gpus = new[] { Gpu("b", "Beta", util: 22), Gpu("a", "Alpha", util: 11) };
        var layout = RunLayout.Create(gpus);

        Assert.Equal(new RunSourceInfo("gpu1", "a", "Alpha"), layout.SourceInfos[1]);
        Assert.Equal(new RunSourceInfo("gpu2", "b", "Beta"), layout.SourceInfos[2]);
        Assert.Equal(1, layout.SourceOfGpu("a"));
        Assert.Equal(2, layout.SourceOfGpu("b"));
        Assert.Equal(-1, layout.SourceOfGpu("c"));

        var cells = Cells(layout, new RunRowFormatter(layout).Format(Sample(1, 2_000_000, gpus: gpus), 1_000_000, Start));
        Assert.Equal("11", Cell(layout, cells, "core.gpu1_util_pct"));
        Assert.Equal("22", Cell(layout, cells, "core.gpu2_util_pct"));
    }

    // ------------------------------------------------------------------ a row

    [Fact]
    public void A_fresh_core_row_has_every_cell_where_the_header_says()
    {
        var layout = RunLayout.Create([]);

        var row = new RunRowFormatter(layout).Format(Sample(1, 3_500_000), 2_500_000, Start);

        Assert.Equal(
            "2500000,2026-11-21T03:00:03.500Z,c-1,2500000,1000,fresh,,10,3500,45,34359738368,21474836480,12884901888,42949672960,1073741824,300000000,200000000,1,80,",
            row);
    }

    [Fact]
    public void A_fresh_gpu_row_carries_the_pdh_values_in_core_and_the_vendor_values_under_its_own_frame()
    {
        var gpus = new[] { Gpu(sequence: 7, acquiredUs: 3_400_000) };
        var layout = RunLayout.Create(gpus);

        var row = new RunRowFormatter(layout).Format(Sample(1, 3_500_000, gpus: gpus), 2_500_000, Start);

        Assert.Equal(
            "2500000,2026-11-21T03:00:03.500Z,c-1,2500000,1000,fresh,,10,3500,45,34359738368,21474836480,12884901888,42949672960,1073741824,300000000,200000000,1,80,,"
            + "50,4000000000,g1-7,2400000,1000,fresh,,60,100,40,2000,83,0",
            row);
    }

    // ------------------------------------------------------------------ fresh and repeat

    [Fact]
    public void Repeat_is_decided_by_the_frame_id_and_each_source_has_its_own_frame()
    {
        var layout = RunLayout.Create([Gpu()]);
        var formatter = new RunRowFormatter(layout);

        // Core frame c-1 and gpu frame g1-1: both new.
        var first = Cells(layout, formatter.Format(Sample(1, 1_000_000, gpus: [Gpu(sequence: 1, acquiredUs: 1_000_000)]), 0, Start));
        // Core c-2 but the vendor values are the same frame g1-1 again: the core row is fresh, the gpu row a repeat.
        var second = Cells(layout, formatter.Format(Sample(2, 2_000_000, gpus: [Gpu(sequence: 1, acquiredUs: 1_000_000)]), 1_000_000, Start));
        // The same core frame again (and a new vendor frame): the core row is the repeat now.
        var third = Cells(layout, formatter.Format(Sample(2, 2_000_000, gpus: [Gpu(sequence: 2, acquiredUs: 2_500_000)]), 2_000_000, Start));

        Assert.Equal(["fresh", "fresh"], [Cell(layout, first, "core.state"), Cell(layout, first, "gpu1.state")]);
        Assert.Equal(["fresh", "repeat"], [Cell(layout, second, "core.state"), Cell(layout, second, "gpu1.state")]);
        Assert.Equal(["repeat", "fresh"], [Cell(layout, third, "core.state"), Cell(layout, third, "gpu1.state")]);

        // A repeat still writes the values of the frame it repeats; the meta cells say it is not a new acquisition.
        Assert.Equal("g1-1", Cell(layout, second, "gpu1.frame"));
        Assert.Equal("60", Cell(layout, second, "gpu1.temp_c"));
    }

    [Fact]
    public void A_new_generation_after_a_host_restart_is_fresh_even_with_the_same_sequence()
    {
        var layout = RunLayout.Create([Gpu()]);
        var formatter = new RunRowFormatter(layout);

        var a = Cells(layout, formatter.Format(Sample(1, 1_000_000, gpus: [Gpu(generation: 1, sequence: 5, acquiredUs: 1_000_000)]), 0, Start));
        var gone = Cells(layout, formatter.Format(Sample(2, 2_000_000, gpus: [Gpu(vendor: SourceOrigin.Missing(SourceGap.HostRestart, 1000))]), 1_000_000, Start));
        var b = Cells(layout, formatter.Format(Sample(3, 3_000_000, gpus: [Gpu(generation: 2, sequence: 5, acquiredUs: 3_000_000)]), 2_000_000, Start));

        Assert.Equal("fresh", Cell(layout, a, "gpu1.state"));
        Assert.Equal("missing", Cell(layout, gone, "gpu1.state"));
        Assert.Equal("host_restart", Cell(layout, gone, "gpu1.reason"));
        Assert.Equal("fresh", Cell(layout, b, "gpu1.state"));
        Assert.Equal("g2-5", Cell(layout, b, "gpu1.frame"));
    }

    [Fact]
    public void A_missing_row_in_between_does_not_make_the_same_frame_fresh_again()
    {
        var layout = RunLayout.Create([Gpu()]);
        var formatter = new RunRowFormatter(layout);

        _ = formatter.Format(Sample(1, 1_000_000, gpus: [Gpu(sequence: 5, acquiredUs: 1_000_000)]), 0, Start);
        _ = formatter.Format(Sample(2, 2_000_000, gpus: [Gpu(vendor: SourceOrigin.Missing(SourceGap.ReadFailed, 1000))]), 1_000_000, Start);
        var again = Cells(layout, formatter.Format(Sample(3, 3_000_000, gpus: [Gpu(sequence: 5, acquiredUs: 1_000_000)]), 2_000_000, Start));

        Assert.Equal("repeat", Cell(layout, again, "gpu1.state"));
    }

    // ------------------------------------------------------------------ missing

    [Theory]
    [InlineData(SourceGap.NotStarted, "not_started")]
    [InlineData(SourceGap.HostRestart, "host_restart")]
    [InlineData(SourceGap.ReadFailed, "read_failed")]
    [InlineData(SourceGap.Unsupported, "unsupported")]
    [InlineData(SourceGap.None, "no_frame")]
    public void A_source_without_a_frame_is_a_missing_row_with_its_reason_and_no_values(SourceGap gap, string reason)
    {
        var layout = RunLayout.Create([Gpu()]);
        var gpu = Gpu(vendor: SourceOrigin.Missing(gap, 1000));

        var cells = Cells(layout, new RunRowFormatter(layout).Format(Sample(1, 1_000_000, gpus: [gpu]), 0, Start));

        Assert.Equal("", Cell(layout, cells, "gpu1.frame"));
        Assert.Equal("", Cell(layout, cells, "gpu1.acquiredOffsetUs"));
        Assert.Equal("1000", Cell(layout, cells, "gpu1.periodMs"));
        Assert.Equal("missing", Cell(layout, cells, "gpu1.state"));
        Assert.Equal(reason, Cell(layout, cells, "gpu1.reason"));
        foreach (var column in new[] { "temp_c", "power_w", "fan_pct", "clock_mhz", "temp_limit_c", "limit_reasons" })
        {
            Assert.Equal("", Cell(layout, cells, "gpu1." + column));
        }

        // The core row of the same sample is untouched.
        Assert.Equal("fresh", Cell(layout, cells, "core.state"));
        Assert.Equal("50", Cell(layout, cells, "core.gpu1_util_pct"));
    }

    [Fact]
    public void A_period_of_zero_leaves_the_period_cell_empty()
    {
        var layout = RunLayout.Create([Gpu()]);

        var cells = Cells(layout, new RunRowFormatter(layout).Format(Sample(1, 1_000_000, gpus: [Gpu(vendor: SourceOrigin.Missing(SourceGap.NotStarted, 0))]), 0, Start));

        Assert.Equal("", Cell(layout, cells, "gpu1.periodMs"));
        Assert.Equal("not_started", Cell(layout, cells, "gpu1.reason"));
    }

    [Fact]
    public void A_core_sample_without_a_frame_is_a_missing_core_row_with_no_values_at_all()
    {
        var layout = RunLayout.Create([Gpu()]);
        var sample = Sample(1, 1_000_000, gpus: [Gpu()], core: SourceOrigin.Missing(SourceGap.None, 1000));

        var cells = Cells(layout, new RunRowFormatter(layout).Format(sample, 0, Start));

        Assert.Equal("missing", Cell(layout, cells, "core.state"));
        Assert.Equal("no_frame", Cell(layout, cells, "core.reason"));
        Assert.Equal("", Cell(layout, cells, "core.cpu_usage_pct"));
        Assert.Equal("", Cell(layout, cells, "core.gpu1_util_pct"));
        Assert.Equal("", Cell(layout, cells, "core.mem_total_bytes"));
    }

    [Fact]
    public void A_gpu_that_is_not_in_the_sample_any_more_is_device_gone()
    {
        var layout = RunLayout.Create([Gpu()]);

        var cells = Cells(layout, new RunRowFormatter(layout).Format(Sample(1, 1_000_000, gpus: []), 0, Start));

        Assert.Equal("missing", Cell(layout, cells, "gpu1.state"));
        Assert.Equal("device_gone", Cell(layout, cells, "gpu1.reason"));
        Assert.Equal("", Cell(layout, cells, "gpu1.periodMs"));
        Assert.Equal("", Cell(layout, cells, "core.gpu1_util_pct"));
        Assert.Equal("", Cell(layout, cells, "core.gpu1_vram_used_bytes"));
        Assert.Equal("", Cell(layout, cells, "gpu1.temp_c"));
        Assert.Equal("fresh", Cell(layout, cells, "core.state"));
    }

    // ------------------------------------------------------------------ the cells

    [Fact]
    public void A_value_that_is_not_a_finite_number_is_an_empty_cell()
    {
        var gpus = new[] { Gpu(temp: double.NaN, power: double.PositiveInfinity, fan: double.NegativeInfinity, clock: null) };
        var layout = RunLayout.Create(gpus);

        var cells = Cells(layout, new RunRowFormatter(layout).Format(Sample(1, 1_000_000, cpu: double.NaN, gpus: gpus), 0, Start));

        Assert.Equal("", Cell(layout, cells, "core.cpu_usage_pct"));
        Assert.Equal("", Cell(layout, cells, "gpu1.temp_c"));
        Assert.Equal("", Cell(layout, cells, "gpu1.power_w"));
        Assert.Equal("", Cell(layout, cells, "gpu1.fan_pct"));
        Assert.Equal("", Cell(layout, cells, "gpu1.clock_mhz"));
        Assert.Equal("83", Cell(layout, cells, "gpu1.temp_limit_c"));
    }

    [Fact]
    public void Numbers_are_written_with_a_decimal_point_whatever_the_culture_of_the_thread()
    {
        var gpus = new[] { Gpu(temp: 61.5, power: 1234.25) };
        var layout = RunLayout.Create(gpus);
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);

            var row = new RunRowFormatter(layout).Format(Sample(1, 1_000_000, cpu: 12.75, gpus: gpus), 0, Start);
            var cells = Cells(layout, row);

            Assert.Equal("12.75", Cell(layout, cells, "core.cpu_usage_pct"));
            Assert.Equal("61.5", Cell(layout, cells, "gpu1.temp_c"));
            Assert.Equal("1234.25", Cell(layout, cells, "gpu1.power_w"));
            Assert.Equal("4000000000", Cell(layout, cells, "core.gpu1_vram_used_bytes"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_flag_is_zero_or_one_and_an_unknown_one_is_empty()
    {
        var layout = RunLayout.Create([]);
        var sample = Sample(1, 1_000_000);

        var on = Cells(layout, new RunRowFormatter(layout).Format(sample, 0, Start));
        var off = Cells(layout, new RunRowFormatter(layout).Format(sample with { Power = sample.Power with { AcConnected = Value.Ok(false, "t") } }, 0, Start));
        var unknown = Cells(layout, new RunRowFormatter(layout).Format(sample with { Power = sample.Power with { AcConnected = Value.NotCollected<bool>("t") } }, 0, Start));

        Assert.Equal("1", Cell(layout, on, "core.ac_connected"));
        Assert.Equal("0", Cell(layout, off, "core.ac_connected"));
        Assert.Equal("", Cell(layout, unknown, "core.ac_connected"));
    }

    [Fact]
    public void The_memory_totals_are_empty_when_the_total_is_unknown()
    {
        var layout = RunLayout.Create([]);
        var sample = Sample(1, 1_000_000);
        var noTotal = sample with { Memory = sample.Memory with { TotalPhysicalBytes = 0, AvailablePhysicalBytes = 0 } };

        var cells = Cells(layout, new RunRowFormatter(layout).Format(noTotal, 0, Start));

        Assert.Equal("", Cell(layout, cells, "core.mem_total_bytes"));
        Assert.Equal("", Cell(layout, cells, "core.mem_available_bytes"));
        Assert.Equal("12884901888", Cell(layout, cells, "core.mem_commit_bytes"));
    }

    [Fact]
    public void Text_with_a_comma_or_a_quote_is_quoted_and_a_line_break_becomes_a_space()
    {
        var layout = RunLayout.Create([]);

        var plain = Cells(layout, new RunRowFormatter(layout).Format(Sample(1, 1_000_000, batteryState: "Charging"), 0, Start));
        var row = new RunRowFormatter(layout).Format(Sample(1, 1_000_000, batteryState: "Charging, 80% \"fast\"\r\nnow"), 0, Start);
        var tricky = Cells(layout, row);

        Assert.Equal("Charging", Cell(layout, plain, "core.battery_state"));
        Assert.EndsWith(",\"Charging, 80% \"\"fast\"\"  now\"", row);
        Assert.Equal("Charging, 80% \"fast\"  now", Cell(layout, tricky, "core.battery_state"));
    }

    // ------------------------------------------------------------------ the limit bit mask

    [Theory]
    [InlineData(0xFFFFFFFFFFFFFFFFUL, "18446744073709551615")]
    [InlineData(0x8000000000000001UL, "9223372036854775809")]
    [InlineData(0x40UL, "64")]
    [InlineData(0UL, "0")]
    public void The_limit_bits_are_a_decimal_string_and_read_back_exactly(ulong bits, string text)
    {
        var gpus = new[] { Gpu(bits: bits) };
        var (layout, csv) = Csv(gpus, Sample(1, 1_000_000, gpus: gpus));

        var row = csv.Split('\n')[1];
        Assert.Equal(text, Cell(layout, Cells(layout, row), "gpu1.limit_reasons"));

        // Read back with the file's own metric list and with none at all (the vocabulary then says it is a bit mask).
        foreach (var declared in new[] { layout.Metrics, null })
        {
            var track = Track(RunCsvReader.Read(Utf8(csv), declared), "gpu1.limit_reasons");
            var point = Assert.Single(track.Points);
            Assert.False(point.Missing);
            Assert.Equal(bits, point.Bits);
        }
    }

    [Fact]
    public void An_unknown_limit_mask_is_an_empty_cell_and_reads_as_missing()
    {
        var gpus = new[] { Gpu(bits: null) };
        var (layout, csv) = Csv(gpus, Sample(1, 1_000_000, gpus: gpus));

        Assert.Equal("", Cell(layout, Cells(layout, csv.Split('\n')[1]), "gpu1.limit_reasons"));
        Assert.True(Assert.Single(Track(RunCsvReader.Read(Utf8(csv), layout.Metrics), "gpu1.limit_reasons").Points).Missing);
    }

    // ------------------------------------------------------------------ the reader: a damaged file

    [Fact]
    public void A_last_line_that_was_cut_off_is_not_read()
    {
        var gpus = new[] { Gpu() };
        var layout = RunLayout.Create(gpus);
        var formatter = new RunRowFormatter(layout);
        var text = new StringBuilder(layout.HeaderLine());
        for (var i = 1; i <= 3; i++)
        {
            text.Append(formatter.Format(Sample(i, i * 1_000_000L, gpus: [Gpu(sequence: i, acquiredUs: i * 1_000_000L)]), i * 1_000_000L, 0)).Append('\n');
        }

        var fourth = formatter.Format(Sample(4, 4_000_000, gpus: [Gpu(sequence: 4, acquiredUs: 4_000_000)]), 4_000_000, 0);
        text.Append(fourth.AsSpan(0, 40)); // the writer was in the middle of this line

        var data = RunCsvReader.Read(Utf8(text.ToString()), layout.Metrics);

        Assert.Equal(3, data.Rows);
        Assert.Equal(3_000_000, data.LastOffsetUs);
        Assert.Equal(0, data.SkippedLines);
    }

    [Fact]
    public void A_file_without_a_single_newline_has_no_rows_and_is_not_an_error()
    {
        var data = RunCsvReader.Read(Utf8("offsetUs,utc,core.fra"), null);

        Assert.Equal(0, data.Rows);
        Assert.Empty(data.Tracks);
        Assert.Empty(RunCsvReader.Read([], null).Header);
    }

    [Fact]
    public void A_header_only_file_has_the_columns_and_no_rows()
    {
        var layout = RunLayout.Create([Gpu()]);

        var data = RunCsvReader.Read(Utf8(layout.HeaderLine()), layout.Metrics);

        Assert.Equal(0, data.Rows);
        Assert.Equal(layout.Header, data.Header);
        Assert.Equal(layout.Metrics.Count - 1, data.Tracks.Count); // every value column but the battery text
    }

    [Fact]
    public void A_byte_order_mark_is_tolerated()
    {
        var (layout, csv) = Csv([], Sample(1, 1_000_000), Sample(2, 2_000_000));
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8(csv)).ToArray();

        var data = RunCsvReader.Read(bytes, layout.Metrics);

        Assert.Equal(2, data.Rows);
        Assert.Equal(RunLayout.OffsetColumn, data.Header[0]);
    }

    [Fact]
    public void A_file_that_does_not_start_with_the_two_time_columns_is_refused()
    {
        Assert.Throws<FormatException>(() => RunCsvReader.Read(Utf8("t_s,utc,cpu.load_pct\n1,2,3\n"), null));
        Assert.Throws<FormatException>(() => RunCsvReader.Read(Utf8("hello\n"), null));
    }

    [Fact]
    public void A_line_with_the_wrong_number_of_columns_is_skipped_and_the_next_row_is_read()
    {
        var layout = RunLayout.Create([]);
        var formatter = new RunRowFormatter(layout);
        var text = layout.HeaderLine()
            + formatter.Format(Sample(1, 1_000_000), 1_000_000, 0) + "\n"
            + "2000000,2026-11-21T03:00:02.000Z,c-2,2000000,1000,fresh\n"
            + formatter.Format(Sample(3, 3_000_000), 3_000_000, 0) + "\n";

        var data = RunCsvReader.Read(Utf8(text), layout.Metrics);

        Assert.Equal(2, data.Rows);
        Assert.Equal(1, data.SkippedLines);
        Assert.Equal(3_000_000, data.LastOffsetUs);
    }

    [Fact]
    public void A_row_whose_offset_does_not_advance_is_skipped_and_the_clock_text_is_never_used_to_order_anything()
    {
        var layout = RunLayout.Create([]);
        var formatter = new RunRowFormatter(layout);
        string Row(long seq, long offset, DateTime? taken = null) => formatter.Format(Sample(seq, offset, taken: taken), offset, 0) + "\n";

        var text = layout.HeaderLine()
            + Row(1, 1_000_000)
            + Row(2, 2_000_000, SampleKit.Wall.AddHours(-1)) // the wall clock was set back: only the text, so it is harmless
            + Row(3, 1_500_000) // the monotonic offset went back: skipped
            + Row(4, 2_000_000) // stands still: skipped
            + Row(5, 3_000_000);

        var data = RunCsvReader.Read(Utf8(text), layout.Metrics);

        Assert.Equal(3, data.Rows);
        Assert.Equal(2, data.SkippedLines);
        Assert.Equal(3_000_000, data.LastOffsetUs);
        Assert.Equal([1_000_000L, 2_000_000L, 3_000_000L], Track(data, "core.cpu_usage_pct").Points.Select(p => p.AtUs));
    }

    // ------------------------------------------------------------------ the reader: what a row becomes

    [Fact]
    public void A_two_second_gpu_and_a_one_second_core_keep_their_own_periods_and_their_own_new_values()
    {
        var gpus = new[] { Gpu() };
        var samples = new List<RecordingSample>();
        for (var i = 0; i < 6; i++)
        {
            // The vendor frame changes every second row (P = 2000 ms); the core frame at every row (P = 1000 ms).
            var gpu = Gpu(sequence: (i / 2) + 1, acquiredUs: (i / 2) * 2_000_000L, periodMs: 2000, temp: 60 + (i / 2));
            samples.Add(Sample(i + 1, i * 1_000_000L, gpus: [gpu]));
        }

        var data = Read(gpus, [.. samples]);

        var cpu = Track(data, "core.cpu_usage_pct");
        var temp = Track(data, "gpu1.temp_c");
        Assert.Equal(6, cpu.Points.Count);
        Assert.All(cpu.Points, p => Assert.Equal(1000, p.PeriodMs));
        Assert.Equal(6, Track(data, "core.gpu1_util_pct").Points.Count);

        // The gpu track has ONE point per fresh frame, at the time that frame was acquired: the repeats add nothing.
        Assert.Equal([0L, 2_000_000L, 4_000_000L], temp.Points.Select(p => p.AtUs));
        Assert.Equal([60.0, 61.0, 62.0], temp.Points.Select(p => p.Value));
        Assert.All(temp.Points, p => Assert.Equal(2000, p.PeriodMs));

        var core = data.Sources["core"];
        var gpu1 = data.Sources["gpu1"];
        Assert.Equal(new[] { 6L, 0L, 0L }, new[] { core.Fresh, core.Repeat, core.Missing });
        Assert.Equal(new[] { 3L, 3L, 0L }, new[] { gpu1.Fresh, gpu1.Repeat, gpu1.Missing });
    }

    [Fact]
    public void A_fresh_vendor_value_is_placed_at_the_time_it_was_acquired_not_at_the_time_of_the_row()
    {
        var gpus = new[] { Gpu() };

        var data = Read(gpus, Sample(1, 1_000_000, gpus: [Gpu(sequence: 1, acquiredUs: 600_000)]));

        Assert.Equal(600_000, Assert.Single(Track(data, "gpu1.temp_c").Points).AtUs);
        Assert.Equal(1_000_000, Assert.Single(Track(data, "core.cpu_usage_pct").Points).AtUs);
    }

    [Fact]
    public void An_empty_cell_of_a_fresh_row_is_a_value_that_could_not_be_read_at_its_acquire_time()
    {
        var gpus = new[] { Gpu() };

        var data = Read(gpus, Sample(1, 1_000_000, gpus: [Gpu(sequence: 1, acquiredUs: 900_000, temp: null)]));

        var point = Assert.Single(Track(data, "gpu1.temp_c").Points);
        Assert.True(point.Missing);
        Assert.Equal(900_000, point.AtUs);
        Assert.False(Assert.Single(Track(data, "gpu1.power_w").Points).Missing);
    }

    [Fact]
    public void Missing_rows_are_counted_with_their_reasons_and_the_tracks_carry_them()
    {
        var gpus = new[] { Gpu() };
        var data = Read(
            gpus,
            Sample(1, 0, gpus: [Gpu(generation: 1, sequence: 1, acquiredUs: 0)]),
            Sample(2, 1_000_000, gpus: [Gpu(vendor: SourceOrigin.Missing(SourceGap.HostRestart, 1000))]),
            Sample(3, 2_000_000, gpus: [Gpu(generation: 2, sequence: 1, acquiredUs: 2_000_000)]),
            Sample(4, 3_000_000, gpus: [Gpu(vendor: SourceOrigin.Missing(SourceGap.ReadFailed, 1000))]),
            Sample(5, 4_000_000, gpus: [Gpu(vendor: SourceOrigin.Missing(SourceGap.HostRestart, 1000))]));

        var counts = data.Sources["gpu1"];
        Assert.Equal(2, counts.Fresh);
        Assert.Equal(3, counts.Missing);
        Assert.Equal(["host_restart", "read_failed"], counts.Reasons);
        Assert.Equal("host_restart,read_failed", Track(data, "gpu1.temp_c").Reasons);
        Assert.Null(Track(data, "core.cpu_usage_pct").Reasons);

        // fresh at 0, could not be read at 1 s, fresh at 2 s again, then the unreadable stretch (several in a row are one).
        var points = Track(data, "gpu1.temp_c").Points;
        Assert.Equal([false, true, false, true], points.Select(p => p.Missing));
        Assert.Equal([0L, 1_000_000L, 2_000_000L, 3_000_000L], points.Select(p => p.AtUs));
    }

    [Fact]
    public void A_text_column_is_not_a_track_and_the_unit_comes_from_the_files_own_list_first()
    {
        var layout = RunLayout.Create([]);
        var csv = RunFileKit.CsvText(layout, 0, [Sample(1, 1_000_000, batteryState: "Charging")]);
        var declared = layout.Metrics.Select(m => m.Key == "core.cpu_mhz" ? m with { Unit = "kHz" } : m).ToList();

        var data = RunCsvReader.Read(Utf8(csv), declared);

        Assert.DoesNotContain(data.Tracks, t => t.Key == "core.battery_state");
        Assert.Equal("kHz", Track(data, "core.cpu_mhz").Unit);
        Assert.Equal("%", Track(data, "core.cpu_usage_pct").Unit);
        Assert.Equal(RunMetricKind.Flag, Track(data, "core.ac_connected").Kind);
    }
}
