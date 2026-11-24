using PcMieru.Core;
using static PcMieru.Checks.Tests.SeriesKit;

namespace PcMieru.Checks.Tests;

/// <summary>
/// The §7.2 summary: the rules of the old Summarizer (same thresholds, the old tests ported by name) on the new timing, where a value counts from the
/// moment it was ACQUIRED until the next acquisition and only when that is at most 3P. Series are built straight from the spec, so no recorder is involved.
/// </summary>
public class RunSummaryTests
{
    private static MetricTrack Track(string key, params (double Sec, double Value)[] points) => Track(key, 1000, points);

    private static MetricTrack Track(string key, int periodMs, params (double Sec, double Value)[] points)
    {
        var track = NewTrack(key);
        foreach (var (sec, value) in points)
        {
            Add(track, sec, value, periodMs);
        }

        return track;
    }

    private static RunSeries Run(double endSec, bool normal, params MetricTrack[] tracks)
    {
        var series = new RunSeries
        {
            EndUs = (long)(endSec * Us),
            NormalEnd = normal,
            Status = normal ? RunStatus.Completed : RunStatus.Interrupted,
            StopReason = normal ? RunStopReason.User : RunStopReason.Unknown,
        };
        series.Tracks.AddRange(tracks);
        return series;
    }

    // =====================================================================
    // Ported: max, average, sustained (old RecordSummaryTests, same names)
    // =====================================================================

    [Fact]
    public void Max_avg_and_sustained_clock_use_the_last_third_of_a_busy_run()
    {
        var sum = Summarize(ThermalRamp());

        var temp = sum.Metric("gpu1.temp_c")!;
        Assert.Equal(80, temp.Max, 3);
        Assert.Equal(40.33, temp.Min, 1);
        Assert.InRange(temp.Avg, 70, 76);

        Assert.Equal(100, sum.SustainedWindowSec, 3);
        Assert.Equal(2000, sum.Metric("gpu1.clock_mhz")!.Sustained!.Value, 3);
        Assert.Equal(80, temp.Sustained!.Value, 3);
        Assert.Equal(250, sum.Metric("gpu1.power_w")!.Sustained!.Value, 3);
        Assert.Equal(2793, sum.Metric("gpu1.clock_mhz")!.Max, 0);
        Assert.Equal(SummaryFreshness.Exact, sum.Freshness);
        Assert.Equal(300, sum.DurationSec, 3);
    }

    [Fact]
    public void Sustained_ignores_an_idle_tail_after_the_workload_ended()
    {
        var sum = Summarize(Series(
            300,
            ("core.gpu1_util_pct", t => t <= 200 ? 100 : 0),
            ("gpu1.clock_mhz", t => t <= 200 ? 2000 : 300),
            ("gpu1.temp_c", t => t <= 200 ? 75 : 45)));
        Assert.Equal(2000, sum.Metric("gpu1.clock_mhz")!.Sustained!.Value, 3);
        Assert.Equal(75, sum.Metric("gpu1.temp_c")!.Sustained!.Value, 3);
        Assert.Equal(75, sum.Metric("gpu1.temp_c")!.Max, 3);
    }

    [Fact]
    public void Runs_shorter_than_a_minute_give_no_sustained_values_and_say_so()
    {
        var sum = Summarize(Series(45, ("core.gpu1_util_pct", _ => 100), ("gpu1.clock_mhz", _ => 2500)));
        Assert.Null(sum.Metric("gpu1.clock_mhz")!.Sustained);
        Assert.Equal(0, sum.SustainedWindowSec);
        var note = Assert.Single(sum.Notes, n => n.Code == "too-short");
        Assert.Equal("45", note.Detail);
        Assert.Equal(2500, sum.Metric("gpu1.clock_mhz")!.Max, 3);
    }

    [Fact]
    public void A_device_that_never_got_busy_is_flagged_instead_of_judged()
    {
        var sum = Summarize(Series(
            120,
            ("core.cpu_usage_pct", _ => 90),
            ("core.gpu1_util_pct", t => t % 7 == 0 ? 6 : 1),
            ("gpu1.temp_c", _ => 35)));
        var note = Assert.Single(sum.Notes, n => n.Code == "device-idle");
        Assert.Equal("gpu1", note.Subject);
        Assert.Null(sum.Metric("gpu1.temp_c")!.Sustained);
        Assert.NotNull(sum.Metric("core.cpu_usage_pct")!.Sustained);
    }

    [Fact]
    public void Missing_values_are_skipped_and_a_sparse_column_is_reported()
    {
        // Every second value could not be read. The old test counted samples; the new numbers are seconds: 50 s known, 50 s missing.
        var sum = Summarize(Series(100, ("core.gpu1_util_pct", _ => 100), ("gpu1.temp_c", t => t % 2 == 0 ? double.NaN : 60)));
        var m = sum.Metric("gpu1.temp_c")!;
        Assert.Equal(50, m.NewPoints);
        Assert.Equal(60, m.Avg, 3);
        Assert.Equal(50, m.ObservedSeconds, 3);
        Assert.Equal(50, m.MissingSeconds, 3);
        var note = Assert.Single(sum.Notes, n => n.Code == "sparse");
        Assert.Equal("gpu1.temp_c", note.Subject);
        Assert.Equal("50", note.Detail);
    }

    [Fact]
    public void Empty_recording_is_reported_not_crashed()
    {
        var sum = Summarize(new RunSeries { EndUs = 0, NormalEnd = true });
        Assert.Equal(0, sum.Rows);
        Assert.Contains(sum.Notes, n => n.Code == "no-samples");
        Assert.Empty(sum.Metrics);
    }

    // =====================================================================
    // Ported: the limit marks
    // =====================================================================

    [Fact]
    public void Gpu_limit_bits_are_named_and_counted_in_seconds()
    {
        var s = Series(
            100,
            ("core.gpu1_util_pct", t => t <= 60 ? 100 : 0),
            ("gpu1.limit_reasons", t => t <= 30 ? 0x4 : t <= 60 ? 0x4 | 0x20 : 0x1));
        var limits = Assert.Single(Summarize(s).Limits, l => l.Device == "gpu1");

        double Secs(string name) => limits.Reasons.Single(r => r.Name == name).Seconds;
        Assert.Equal(60, Secs("sw-power-cap"));
        Assert.Equal(30, Secs("sw-thermal-slowdown"));
        Assert.Equal(40, Secs("gpu-idle"));
        Assert.DoesNotContain(limits.Reasons, r => r.Name == "hw-thermal-slowdown");
        Assert.InRange(limits.Reasons.Single(r => r.Name == "sw-power-cap").SecondsWhileLoaded, 60, 63);
        Assert.InRange(limits.Reasons.Single(r => r.Name == "gpu-idle").SecondsWhileLoaded, 0, 3);
    }

    [Fact]
    public void Cpu_limits_come_from_the_os_indicators_and_nothing_is_invented()
    {
        var s = Series(
            100,
            ("core.cpu_usage_pct", _ => 100),
            ("cpu.perf_limit_pct", t => t is > 20 and <= 50 ? 80 : 100),
            ("cpu.perf_limit_flags", t => t > 90 ? 1 : 0));
        var l = Assert.Single(Summarize(s).Limits, x => x.Device == "cpu");
        Assert.Equal(30, l.Reasons.Single(r => r.Name == "os-performance-limit-below-100").Seconds);
        Assert.Equal(10, l.Reasons.Single(r => r.Name == "os-performance-limit-flags-set").Seconds);

        // A mark that never came up leaves no reason (it does not say "no limit", only that none was set).
        var clean = Series(100, ("core.cpu_usage_pct", _ => 100), ("cpu.perf_limit_pct", _ => 100));
        Assert.Empty(Assert.Single(Summarize(clean).Limits).Reasons);
    }

    [Fact]
    public void A_limit_mask_with_every_bit_set_names_every_reason_and_the_bits_no_entry_names()
    {
        var mask = NewTrack("gpu1.limit_reasons");
        mask.AddValue(0, 1000, 0, ulong.MaxValue);
        mask.AddValue(1_000_000, 1000, 0, ulong.MaxValue);
        var sum = Summarize(Run(2, normal: true, mask));
        var limits = Assert.Single(sum.Limits);
        Assert.Equal(RunLimitBits.All.Length + 1, limits.Reasons.Count);
        Assert.All(limits.Reasons, r => Assert.Equal(2, r.Seconds, 3));
        Assert.Contains(limits.Reasons, r => r.Name == RunLimitBits.UnknownName);
        Assert.NotEqual(0UL, RunLimitBits.Unnamed);
    }

    [Fact]
    public void Limit_seconds_while_loaded_cross_the_bit_with_the_load()
    {
        // The power cap is set the whole run; the GPU is busy only for the first 50 s: the loaded part of the reason cannot be more than that.
        var s = Series(100, ("core.gpu1_util_pct", t => t <= 50 ? 100 : 0), ("gpu1.limit_reasons", _ => 0x4));
        var limits = Assert.Single(Summarize(s).Limits);
        var cap = limits.Reasons.Single(r => r.Name == "sw-power-cap");
        Assert.Equal(100, cap.Seconds, 3);
        Assert.InRange(cap.SecondsWhileLoaded, 50, 53);
        Assert.InRange(limits.LoadedSeconds, 50, 53);
        Assert.Equal(100, limits.ObservedSeconds, 3);
    }

    // =====================================================================
    // Ported: what happened in the same time window
    // =====================================================================

    [Fact]
    public void Temperature_rising_while_the_clock_drops_under_load_is_listed_as_a_co_occurrence()
    {
        var sum = Summarize(ThermalRamp());
        var c = Assert.Single(sum.CoOccurrences);
        Assert.Equal("gpu1", c.Device);
        Assert.True(c.TempToC - c.TempFromC >= 3);
        Assert.True(c.ClockFromMhz > c.ClockToMhz);
        Assert.InRange(c.StartSec, 0, 5);
        Assert.InRange(c.EndSec, 120, 160);
        Assert.True(c.AvgLoadPct >= 99);
    }

    [Fact]
    public void A_temperature_rise_without_a_clock_drop_is_not_a_co_occurrence()
    {
        Assert.Empty(Summarize(ThermalRamp(clock: _ => 2800)).CoOccurrences);
    }

    [Fact]
    public void A_clock_drop_without_a_temperature_rise_is_not_a_co_occurrence()
    {
        var s = Series(
            300,
            ("core.gpu1_util_pct", _ => 100),
            ("gpu1.temp_c", _ => 60),
            ("gpu1.clock_mhz", t => 2800 - (800 * Math.Min(t, 120) / 120.0)));
        Assert.Empty(Summarize(s).CoOccurrences);
    }

    [Fact]
    public void Co_occurrence_needs_real_load_so_idle_cooldowns_are_not_reported()
    {
        Assert.Empty(Summarize(ThermalRamp(util: _ => 10)).CoOccurrences);
    }

    [Fact]
    public void Co_occurrence_wording_says_it_happened_together_and_never_claims_a_cause()
    {
        var c = Assert.Single(Summarize(ThermalRamp()).CoOccurrences);
        var args = new Dictionary<string, string>
        {
            ["device"] = "GPU1",
            ["start"] = c.StartSec.ToString("0"),
            ["end"] = c.EndSec.ToString("0"),
            ["tempFrom"] = c.TempFromC.ToString("0"),
            ["tempTo"] = c.TempToC.ToString("0"),
            ["clockFrom"] = c.ClockFromMhz.ToString("0"),
            ["clockTo"] = c.ClockToMhz.ToString("0"),
            ["load"] = c.AvgLoadPct.ToString("0"),
        };
        Strings.Register(RecordStrings.All);
        var ja = Strings.Format("gui.rec.co.line", args, Language.Ja);
        var en = Strings.Format("gui.rec.co.line", args, Language.En);
        Assert.Contains("同じ時間帯", ja);
        Assert.Contains("原因は分かりません", ja);
        Assert.Contains("same time window", en);
        Assert.Contains("cause cannot be told", en);
        Assert.DoesNotContain("せいで", ja);
        Assert.DoesNotContain("because", en, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GPU1", ja);
    }

    // =====================================================================
    // Ported: what could not be read, and flaws of the recording
    // =====================================================================

    [Fact]
    public void Unreadable_items_become_notes_with_their_reason_codes()
    {
        // The old test fed the capability list; the new run carries the reason in its CSV, so a column with nothing readable becomes a no-data note.
        var temp = NewTrack("gpu1.temp_c");
        Add(temp, 1, double.NaN);
        temp.Reasons = "host_restart,read_failed";
        var cpu = Track("core.cpu_usage_pct", (0, 50), (1, 50), (2, 50));
        var sum = Summarize(Run(3, normal: true, cpu, temp));
        var note = Assert.Single(sum.Notes, n => n.Code == "no-data");
        Assert.Equal("gpu1.temp_c", note.Subject);
        Assert.Equal("host_restart,read_failed", note.Detail);
        Assert.Null(sum.Metric("gpu1.temp_c"));
        Assert.NotNull(sum.Metric("core.cpu_usage_pct"));
    }

    [Fact]
    public void A_cpu_clock_drop_without_a_cpu_temperature_says_the_cause_cannot_be_narrowed_down()
    {
        var sum = Summarize(Series(300, ("core.cpu_usage_pct", _ => 100), ("core.cpu_mhz", t => t <= 60 ? 5000 : 4000)));
        var note = Assert.Single(sum.Notes, n => n.Code == "cpu-clock-drop-no-temp");
        Assert.Equal("4000/5000", note.Detail);
        Assert.Empty(sum.CoOccurrences);
    }

    [Fact]
    public void Gaps_in_the_recording_and_an_unfinished_run_are_reported()
    {
        // 50 values at 0..49 s, then a 15 s hole and 50 values from 64 s; the run was cut off at 114 s (the last observation).
        var cpu = NewTrack("core.cpu_usage_pct");
        for (var i = 0; i < 50; i++)
        {
            Add(cpu, i, 50);
        }

        for (var i = 0; i < 50; i++)
        {
            Add(cpu, 64 + i, 50);
        }

        var series = new RunSeries { EndUs = 114 * 1_000_000L, NormalEnd = false, Status = RunStatus.Interrupted, StopReason = RunStopReason.Unknown, Rows = 100 };
        series.Tracks.Add(cpu);
        var sum = Summarize(series);
        var gaps = Assert.Single(sum.Notes, n => n.Code == "gaps");
        Assert.Equal("15/15", gaps.Detail);
        Assert.Contains(sum.Notes, n => n.Code == "not-completed" && n.Subject == "interrupted" && n.Detail == "unknown");
        Assert.Equal(15, sum.Metric("core.cpu_usage_pct")!.MissingSeconds, 3);
    }

    [Fact]
    public void Battery_use_is_noted_when_the_power_source_changed()
    {
        var s = Series(60, ("core.cpu_usage_pct", _ => 50), ("core.ac_connected", t => t < 30 ? 1 : 0));
        Assert.Contains(Summarize(s).Notes, n => n.Code == "battery-used");
        var ac = Series(60, ("core.cpu_usage_pct", _ => 50), ("core.ac_connected", _ => 1));
        Assert.DoesNotContain(Summarize(ac).Notes, n => n.Code == "battery-used");
    }

    [Fact]
    public void Dropped_ticks_are_a_note_of_their_own()
    {
        var series = new RunSeries { EndUs = 10_000_000, NormalEnd = true, DroppedTicks = 3, Rows = 7 };
        series.Tracks.Add(Track("core.cpu_usage_pct", (0, 1), (1, 1)));
        var sum = Summarize(series);
        Assert.Equal(3, sum.DroppedTicks);
        Assert.Equal(7, sum.Rows);
        Assert.Equal("3", Assert.Single(sum.Notes, n => n.Code == "dropped-ticks").Detail);
    }

    // =====================================================================
    // Ported: marks
    // =====================================================================

    [Fact]
    public void Markers_cut_out_bench_and_stress_spans_with_their_own_numbers()
    {
        var series = Series(
            100,
            RunStatus.Completed,
            RunStopReason.User,
            [Marker(MarkerKind.BenchStart, 20), Marker(MarkerKind.BenchEnd, 60), Marker(MarkerKind.StressStart, 80)],
            ("core.gpu1_util_pct", t => t is >= 21 and <= 60 ? 100 : 5),
            ("gpu1.power_w", t => t is >= 21 and <= 60 ? 200 : 30));
        var sum = Summarize(series);

        var bench = sum.Sections.Single(x => x.Kind == "bench");
        Assert.False(bench.Open);
        Assert.Equal(20, bench.StartSec, 3);
        Assert.Equal(60, bench.EndSec, 3);
        Assert.Equal(200, bench.Metrics.Single(m => m.Key == "gpu1.power_w").Avg, 3);
        Assert.Equal(100, bench.Metrics.Single(m => m.Key == "core.gpu1_util_pct").Median, 3);

        var stress = sum.Sections.Single(x => x.Kind == "stress");
        Assert.True(stress.Open);
        Assert.Equal(100, stress.EndSec, 3);
        Assert.Equal(30, stress.Metrics.Single(m => m.Key == "gpu1.power_w").Avg, 3);

        // A start with no end is shown as an open section (old lines 325-351); only an END with no start is a note.
        Assert.DoesNotContain(sum.Notes, n => n.Code == "marker-unpaired");
    }

    [Fact]
    public void An_end_mark_without_a_start_is_noted_and_does_not_make_a_span()
    {
        var series = Series(30, RunStatus.Completed, RunStopReason.User, [Marker(MarkerKind.BenchEnd, 10)], ("core.cpu_usage_pct", _ => 10));
        var sum = Summarize(series);
        Assert.Empty(sum.Sections);
        Assert.Contains(sum.Notes, n => n.Code == "marker-unpaired" && n.Subject == "bench-end");
    }

    [Fact]
    public void A_second_start_closes_the_first_section_open_and_plain_marks_make_no_section()
    {
        var series = Series(
            60,
            RunStatus.Completed,
            RunStopReason.User,
            [Marker(MarkerKind.BenchStart, 10), Marker(MarkerKind.Mark, 15), Marker(MarkerKind.BenchStart, 30), Marker(MarkerKind.BenchEnd, 50)],
            ("core.cpu_usage_pct", _ => 10));
        var sum = Summarize(series);
        Assert.Equal(2, sum.Sections.Count);
        Assert.True(sum.Sections[0].Open);
        Assert.Equal(30, sum.Sections[0].EndSec, 3);
        Assert.False(sum.Sections[1].Open);
        Assert.Equal(50, sum.Sections[1].EndSec, 3);
    }

    // =====================================================================
    // New in the timing: P = 2 s, the 3P boundary, the first and the last interval
    // =====================================================================

    [Fact]
    public void A_two_second_source_counts_its_own_period_and_a_hole_longer_than_three_periods_is_missing()
    {
        // Acquisitions at 0, 2, 4, 12, 14 s with P = 2 s; the run stops normally at 16 s. [4, 12) is 8 s > 6 s = 3P: the whole interval is missing.
        var cpu = Track("core.cpu_usage_pct", 2000, (0, 10), (2, 10), (4, 10), (12, 10), (14, 10));
        var sum = Summarize(Run(16, normal: true, cpu));
        var m = sum.Metric("core.cpu_usage_pct")!;
        Assert.Equal(8, m.ObservedSeconds, 3);
        Assert.Equal(8, m.MissingSeconds, 3);
        Assert.Equal(5, m.NewPoints);
        var gaps = Assert.Single(sum.Notes, n => n.Code == "gaps");
        Assert.Equal("8/8", gaps.Detail);
    }

    [Fact]
    public void A_one_second_source_with_the_same_hole_is_missing_too_but_two_seconds_of_hole_are_not()
    {
        // P = 1 s: a 2 s step is within 3P, a 4 s step is not.
        var within = Summarize(Run(6, normal: true, Track("core.cpu_usage_pct", (0, 5), (2, 5), (4, 5))));
        Assert.Equal(5, within.Metric("core.cpu_usage_pct")!.ObservedSeconds, 3);

        var over = Summarize(Run(8, normal: true, Track("core.cpu_usage_pct", (0, 5), (4, 5))));
        Assert.Equal(1, over.Metric("core.cpu_usage_pct")!.ObservedSeconds, 3);
        Assert.Equal(7, over.Metric("core.cpu_usage_pct")!.MissingSeconds, 3);
    }

    [Fact]
    public void A_gap_of_exactly_three_periods_is_valid_and_a_hair_over_is_missing()
    {
        var exact = Summarize(Run(4, normal: true, Track("core.cpu_usage_pct", (0, 5), (3, 5))));
        var m = exact.Metric("core.cpu_usage_pct")!;
        Assert.Equal(4, m.ObservedSeconds, 3);
        Assert.Equal(0, m.MissingSeconds, 3);
        Assert.DoesNotContain(exact.Notes, n => n.Code == "gaps");

        var over = Summarize(Run(4, normal: true, Track("core.cpu_usage_pct", (0, 5), (3.001, 5))));
        var o = over.Metric("core.cpu_usage_pct")!;
        Assert.Equal(0.999, o.ObservedSeconds, 3);
        Assert.Equal(3.001, o.MissingSeconds, 3);
        Assert.Contains(over.Notes, n => n.Code == "gaps");
    }

    [Fact]
    public void The_first_interval_starts_at_the_first_acquisition_not_at_the_start_of_the_run()
    {
        // Nothing was acquired before 2 s: the first two seconds are missing, not guessed.
        var sum = Summarize(Run(5, normal: true, Track("core.cpu_usage_pct", (2, 5), (3, 5), (4, 5))));
        var m = sum.Metric("core.cpu_usage_pct")!;
        Assert.Equal(3, m.ObservedSeconds, 3);
        Assert.Equal(2, m.MissingSeconds, 3);
    }

    [Fact]
    public void The_last_value_of_a_normal_stop_gets_one_period_and_the_rest_of_the_run_is_missing()
    {
        // Values at 0..8 s, stop at 12 s: the value at 8 s counts for P = 1 s only; 9..12 s is missing.
        var points = Enumerable.Range(0, 9).Select(i => ((double)i, 5.0)).ToArray();
        var sum = Summarize(Run(12, normal: true, Track("core.cpu_usage_pct", points)));
        var m = sum.Metric("core.cpu_usage_pct")!;
        Assert.Equal(9, m.ObservedSeconds, 3);
        Assert.Equal(3, m.MissingSeconds, 3);
    }

    [Fact]
    public void The_last_value_of_a_normal_stop_is_cut_at_the_stop_when_that_comes_sooner()
    {
        // Values at 0..8 s every second, stop at 8.4 s: the value at 8 s would get P = 1 s but the run ended 0.4 s later.
        var points = Enumerable.Range(0, 9).Select(i => ((double)i, 5.0)).ToArray();
        var sum = Summarize(Run(8.4, normal: true, Track("core.cpu_usage_pct", points)));
        Assert.Equal(8.4, sum.Metric("core.cpu_usage_pct")!.ObservedSeconds, 3);
    }

    [Fact]
    public void The_last_value_of_a_run_that_did_not_end_normally_runs_to_the_last_observation_if_that_is_within_three_periods()
    {
        var points = Enumerable.Range(0, 9).Select(i => ((double)i, 5.0)).ToArray();
        var within = Summarize(Run(9.5, normal: false, Track("core.cpu_usage_pct", points)));
        Assert.Equal(9.5, within.Metric("core.cpu_usage_pct")!.ObservedSeconds, 3);

        // 4 s after the last acquisition is more than 3P: that interval is not trusted.
        var over = Summarize(Run(12, normal: false, Track("core.cpu_usage_pct", points)));
        Assert.Equal(8, over.Metric("core.cpu_usage_pct")!.ObservedSeconds, 3);
    }

    [Fact]
    public void A_missing_mark_cuts_the_interval_where_it_was_observed()
    {
        // A value at 0 s, "could not be read" observed at 2 s, a value again at 3 s: [0, 2) known, [2, 3) not.
        var cpu = NewTrack("core.cpu_usage_pct");
        Add(cpu, 0, 5);
        Add(cpu, 2, double.NaN);
        Add(cpu, 3, 5);
        var sum = Summarize(Run(4, normal: true, cpu));
        var m = sum.Metric("core.cpu_usage_pct")!;
        Assert.Equal(3, m.ObservedSeconds, 3);
        Assert.Equal(1, m.MissingSeconds, 3);
    }

    [Fact]
    public void A_repeat_adds_no_seconds_and_no_new_value()
    {
        // The track holds only new acquisitions; a repeated frame never reaches it (the CSV reader counts it apart), so adding the same time again changes nothing.
        var cpu = NewTrack("core.cpu_usage_pct");
        Add(cpu, 0, 5);
        Add(cpu, 0, 9);
        Add(cpu, 1, 5);
        Assert.Equal(2, cpu.Points.Count);
        var sum = Summarize(Run(2, normal: true, cpu));
        Assert.Equal(2, sum.Metric("core.cpu_usage_pct")!.NewPoints);
        Assert.Equal(5, sum.Metric("core.cpu_usage_pct")!.Max, 3);
    }

    [Fact]
    public void Observed_and_missing_seconds_are_per_metric_and_never_one_run_total()
    {
        var series = Run(
            10,
            normal: true,
            Track("core.cpu_usage_pct", Enumerable.Range(0, 10).Select(i => ((double)i, 5.0)).ToArray()),
            Track("gpu1.temp_c", (0, 60), (1, 60), (2, 60)));
        var sum = Summarize(series);
        Assert.Equal(10, sum.Metric("core.cpu_usage_pct")!.ObservedSeconds, 3);
        Assert.Equal(3, sum.Metric("gpu1.temp_c")!.ObservedSeconds, 3);
        Assert.Equal(7, sum.Metric("gpu1.temp_c")!.MissingSeconds, 3);
        Assert.Equal(0, sum.Metric("core.cpu_usage_pct")!.MissingSeconds, 3);
    }
}
