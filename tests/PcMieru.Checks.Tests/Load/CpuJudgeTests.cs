using PcMieru.Checks.Load;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Collectors;

namespace PcMieru.Checks.Tests.Load;

/// <summary>
/// The rows and verdicts of the CPU check (DESIGN-A §3.2, §14.2) from a measured load: the peer comparison with its medians, the steadiness, the
/// effective clock and what a missing sample may never be, the observations, and what a stopped run keeps. No thread, no clock, no PDH.
/// </summary>
public sealed class CpuJudgeTests
{
    private static IReadOnlyList<CheckItem> Judge(CpuLoadResult load) => CpuJudge.Judge(load);

    private static IReadOnlyList<CheckItem> Peers(double[] rates, byte[]? classes = null, PinOutcome[]? pins = null, bool stopped = false, int seconds = 30) =>
        Rows.All(Judge(Loads.Make(classes is null ? Cpus.Uniform(rates.Length) : Cpus.OfClasses(classes), rates, seconds, pins, stopped: stopped)), "cpu.peer_ratio");

    private static string Subject(CheckItem item) => item.Subject!.Id;

    // ---- the rows of a healthy run ----

    [Fact]
    public void A_healthy_run_gives_the_rows_of_the_design_in_order_with_their_units_and_sources()
    {
        var items = Judge(Loads.Make(Cpus.Uniform(2), [1000, 1000]));

        Assert.Equal(
            [
                "cpu.duration", "cpu.chunks", "cpu.compute_errors", "cpu.throughput", "cpu.steadiness", "cpu.variation",
                "cpu.logical_throughput", "cpu.logical_throughput", "cpu.peer_ratio", "cpu.peer_ratio",
                "cpu.effective_clock", "cpu.utilization", "cpu.package_power",
            ],
            items.Select(static i => i.Id));
        Assert.Equal(["s", "count", "count", "chunks/s", "%", "%", "chunks/s", "chunks/s", "%", "%", "MHz", "%", "W"], items.Select(static i => i.Unit));
        Assert.Equal(
            [
                CheckSource.Load, CheckSource.Load, CheckSource.Load, CheckSource.Load, CheckSource.Load, CheckSource.Load,
                CheckSource.Load, CheckSource.Load, CheckSource.Load, CheckSource.Load, CheckSource.Pdh, CheckSource.Pdh, CheckSource.EnergyMeter,
            ],
            items.Select(static i => i.Source));
    }

    [Fact]
    public void In_a_healthy_run_every_judged_row_is_ok_and_every_observation_is_info_and_nothing_has_a_reason()
    {
        var items = Judge(Loads.Make(Cpus.Uniform(4), [1000, 1000, 1000, 1000]));

        Assert.All(items, static i => Assert.Null(i.Reason));
        Assert.Equal(ItemVerdict.Ok, Rows.One(items, "cpu.compute_errors").Verdict);
        Assert.Equal(ItemVerdict.Ok, Rows.One(items, "cpu.steadiness").Verdict);
        Assert.Equal(ItemVerdict.Ok, Rows.One(items, "cpu.effective_clock").Verdict);
        Assert.All(Rows.All(items, "cpu.peer_ratio"), static i => Assert.Equal(ItemVerdict.Ok, i.Verdict));
        foreach (var id in new[] { "cpu.duration", "cpu.chunks", "cpu.throughput", "cpu.variation", "cpu.logical_throughput", "cpu.utilization", "cpu.package_power" })
        {
            Assert.All(Rows.All(items, id), i => Assert.Equal(ItemVerdict.Info, i.Verdict));
        }
    }

    [Fact]
    public void The_observations_carry_what_was_measured()
    {
        var load = Loads.Make(Cpus.Uniform(2), [1000, 500]);

        var items = Judge(load);

        Assert.Equal(30.06, Rows.Number(Rows.One(items, "cpu.duration")), 6);
        Assert.Equal(load.Chunks, Rows.One(items, "cpu.chunks").Value!.AsInt);
        Assert.Equal(1500, Rows.Number(Rows.One(items, "cpu.throughput")), 6); // the sum of the steady averages of the logical CPUs
        Assert.Equal(3500, Rows.Number(Rows.One(items, "cpu.effective_clock")), 6);
        Assert.Equal(100, Rows.Number(Rows.One(items, "cpu.utilization")), 6);
        Assert.Equal(65, Rows.Number(Rows.One(items, "cpu.package_power")), 6);
    }

    [Fact]
    public void The_logical_throughput_is_one_row_per_cpu_with_its_number_and_its_class_as_an_int()
    {
        var cpus = Cpus.OfClasses(1, 1, 0);

        var rows = Rows.All(Judge(Loads.Make(cpus, [900, 800, 400])), "cpu.logical_throughput");

        Assert.Equal(["cpu-1", "cpu-2", "cpu-3"], rows.Select(Subject));
        Assert.Equal([900.0, 800.0, 400.0], rows.Select(Rows.Number));
        Assert.Equal([1, 1, 0], rows.Select(static r => r.Evidence!.EfficiencyClass!.Value));
        Assert.All(rows, static r => Assert.Equal(ItemVerdict.Info, r.Verdict));
    }

    [Fact]
    public void The_throughput_leaves_out_the_first_two_seconds_like_the_logical_ones()
    {
        // a ramp-up: 500 chunks/s in the first two seconds and 1000 after them
        var load = Loads.FromSeries(Cpus.Uniform(1), s => [s <= 2 ? 500.0 : 1000.0], 30);

        var items = Judge(load);

        Assert.Equal(1000, Rows.Number(Rows.One(items, "cpu.throughput")), 6);
        Assert.Equal(1000, Rows.Number(Rows.One(items, "cpu.logical_throughput")), 6);
    }

    [Fact]
    public void The_judge_needs_a_result()
    {
        Assert.Throws<ArgumentNullException>(() => CpuJudge.Judge(null!));
    }

    // ---- the compute errors ----

    [Fact]
    public void No_compute_error_is_ok_with_a_count_of_zero()
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(2), [1000, 1000])), "cpu.compute_errors");

        Assert.Equal(ItemVerdict.Ok, row.Verdict);
        Assert.Equal(0, row.Value!.AsInt);
        Assert.Null(row.Reason);
        Assert.Null(row.Evidence);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1000)]
    public void Computation_error_is_the_heaviest_finding(long errors)
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(2), [1000, 1000], errors: errors)), "cpu.compute_errors");

        Assert.Equal(ItemVerdict.Check, row.Verdict);
        Assert.Equal(errors, row.Value!.AsInt);
        Assert.Null(row.Reason);
        Assert.Equal(CheckEvidenceCode.ComputeMismatch, row.Evidence!.Code);
    }

    [Fact]
    public void A_run_that_compared_nothing_cannot_say_there_was_no_error_and_the_reason_is_partial()
    {
        // Not stopped, and nothing compared (DESIGN-A 14.2): the load did not get to compare, which is not an error of the check.
        var load = Loads.Make(Cpus.Uniform(2), [1000, 1000], seconds: 0);

        var row = Rows.One(Judge(load), "cpu.compute_errors");

        Assert.Null(row.Value);
        Assert.Equal(ItemVerdict.Unavailable, row.Verdict);
        Assert.Equal(CheckReason.Partial, row.Reason);
    }

    [Fact]
    public void A_stopped_run_that_compared_nothing_is_partial()
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(2), [1000, 1000], seconds: 0, stopped: true)), "cpu.compute_errors");

        Assert.Null(row.Value);
        Assert.Equal(ItemVerdict.Unavailable, row.Verdict);
        Assert.Equal(CheckReason.Partial, row.Reason);
    }

    [Fact]
    public void A_stopped_run_with_no_error_so_far_is_partial_and_never_ok()
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(2), [1000, 1000], seconds: 5, stopped: true)), "cpu.compute_errors");

        Assert.Equal(0, row.Value!.AsInt);
        Assert.Equal(ItemVerdict.Unavailable, row.Verdict);
        Assert.Equal(CheckReason.Partial, row.Reason);
    }

    [Fact]
    public void A_stopped_run_keeps_the_compute_error_it_found()
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(2), [1000, 1000], seconds: 5, errors: 3, stopped: true)), "cpu.compute_errors");

        Assert.Equal(3, row.Value!.AsInt);
        Assert.Equal(ItemVerdict.Check, row.Verdict);
        Assert.Equal(CheckReason.Partial, row.Reason);
        Assert.Equal(CheckEvidenceCode.ComputeMismatch, row.Evidence!.Code);
    }

    // ---- steadiness and variation ----

    [Fact]
    public void A_flat_run_is_a_steadiness_of_100_with_no_spread()
    {
        var items = Judge(Loads.Make(Cpus.Uniform(1), [1000]));

        var steadiness = Rows.One(items, "cpu.steadiness");
        var variation = Rows.One(items, "cpu.variation");

        Assert.Equal(100, Rows.Number(steadiness), 6);
        Assert.Equal(ItemVerdict.Ok, steadiness.Verdict);
        Assert.Equal(0, steadiness.Evidence!.Cv!.Value, 6);
        Assert.Equal(0, Rows.Number(variation), 6);
        Assert.Equal(ItemVerdict.Info, variation.Verdict);
    }

    [Fact]
    public void A_pace_pulled_down_in_the_second_half_is_a_check_and_the_variation_is_the_spread_times_100()
    {
        // 100 for the first 12 seconds, 60 after them (the old throttled series): a score of 60
        var load = Loads.FromSeries(Cpus.Uniform(1), s => [s <= 12 ? 100.0 : 60.0], 30);

        var items = Judge(load);
        var steadiness = Rows.One(items, "cpu.steadiness");
        var variation = Rows.One(items, "cpu.variation");

        Assert.Equal(60, Rows.Number(steadiness), 0);
        Assert.Equal(ItemVerdict.Check, steadiness.Verdict);
        Assert.InRange(Rows.Number(variation), 25.0, 26.5); // about 0.258 as a ratio, written as 25.8 (DESIGN-A 14.0)
        Assert.Equal(Rows.Number(variation), steadiness.Evidence!.Cv!.Value, 9);
    }

    [Fact]
    public void A_spread_over_ten_percent_alone_is_a_caution_and_the_threshold_is_on_the_ratio()
    {
        // alternating 80 and 120: the end is not below the start, but the spread is 0.2 of the mean
        var load = Loads.FromSeries(Cpus.Uniform(1), s => [s % 2 == 0 ? 120.0 : 80.0], 30);

        var steadiness = Rows.One(Judge(load), "cpu.steadiness");

        Assert.Equal(ItemVerdict.Caution, steadiness.Verdict);
        Assert.Equal(100, Rows.Number(steadiness), 6);
        Assert.InRange(steadiness.Evidence!.Cv!.Value, 19.0, 21.0);
    }

    [Fact]
    public void A_spread_of_exactly_ten_percent_with_a_steady_end_is_ok()
    {
        // 90 and 110 alternating: mean 100, sd 10, a ratio of 0.10 is not above 0.10
        var load = Loads.FromSeries(Cpus.Uniform(1), s => [s % 2 == 0 ? 110.0 : 90.0], 30);

        var steadiness = Rows.One(Judge(load), "cpu.steadiness");

        Assert.Equal(ItemVerdict.Ok, steadiness.Verdict);
    }

    [Fact]
    public void Under_eight_seconds_the_steadiness_is_too_short_to_say_and_the_variation_is_missing()
    {
        var items = Judge(Loads.Make(Cpus.Uniform(1), [1000], seconds: 7));

        var steadiness = Rows.One(items, "cpu.steadiness");
        var variation = Rows.One(items, "cpu.variation");

        Assert.Null(steadiness.Value);
        Assert.Equal(ItemVerdict.Unavailable, steadiness.Verdict);
        Assert.Equal(CheckReason.Partial, steadiness.Reason);
        Assert.Equal(CheckEvidenceCode.TooShort, steadiness.Evidence!.Code);
        Assert.Null(variation.Value);
        Assert.Equal(ItemVerdict.Info, variation.Verdict);
        Assert.Equal(CheckReason.Partial, variation.Reason);
    }

    [Fact]
    public void Eight_seconds_are_enough_to_judge_the_steadiness()
    {
        var steadiness = Rows.One(Judge(Loads.Make(Cpus.Uniform(1), [1000], seconds: 8)), "cpu.steadiness");

        Assert.Equal(ItemVerdict.Ok, steadiness.Verdict);
        Assert.NotNull(steadiness.Value);
    }

    // ---- the peer comparison ----

    [Fact]
    public void With_one_logical_cpu_there_is_nothing_to_compare_with_and_no_peer_row()
    {
        var items = Judge(Loads.Make(Cpus.Uniform(1), [1000]));

        Assert.Empty(Rows.All(items, "cpu.peer_ratio"));
        Assert.Single(Rows.All(items, "cpu.logical_throughput"));
    }

    [Fact]
    public void With_one_logical_cpu_that_was_not_pinned_there_is_still_no_peer_row()
    {
        var items = Judge(Loads.Make(Cpus.Uniform(1), [1000], pins: [PinOutcome.Failed]));

        Assert.Empty(Rows.All(items, "cpu.peer_ratio"));
    }

    [Fact]
    public void Peer_median_of_an_odd_number_is_the_middle_one()
    {
        var rows = Peers([100, 100, 60]);

        Assert.Equal([100.0, 100.0, 60.0], rows.Select(Rows.Number));
        Assert.Equal([ItemVerdict.Ok, ItemVerdict.Ok, ItemVerdict.Caution], rows.Select(static r => r.Verdict));
        Assert.Equal(["cpu-1", "cpu-2", "cpu-3"], rows.Select(Subject));
    }

    [Fact]
    public void Peer_median_of_an_even_number_is_the_upper_of_the_two_middle_ones()
    {
        // sorted 74 74 100 100: the upper median is 100 (the element at n/2), so 74 is 74 % of it: a caution.
        // With the lower median (74) nobody would be below the line.
        var rows = Peers([100, 100, 74, 74]);

        Assert.Equal([100.0, 100.0, 74.0, 74.0], rows.Select(Rows.Number));
        Assert.Equal([ItemVerdict.Ok, ItemVerdict.Ok, ItemVerdict.Caution, ItemVerdict.Caution], rows.Select(static r => r.Verdict));
    }

    [Fact]
    public void Peer_median_of_two_is_the_faster_one()
    {
        var rows = Peers([100, 60]);

        Assert.Equal([100.0, 60.0], rows.Select(Rows.Number));
        Assert.Equal([ItemVerdict.Ok, ItemVerdict.Caution], rows.Select(static r => r.Verdict));
    }

    [Fact]
    public void A_logical_cpu_at_exactly_75_percent_of_the_median_is_ok_and_just_below_is_a_caution()
    {
        var at = Peers([100, 100, 75, 75]);
        var below = Peers([100, 100, 74.99, 74.99]);

        Assert.Equal([ItemVerdict.Ok, ItemVerdict.Ok, ItemVerdict.Ok, ItemVerdict.Ok], at.Select(static r => r.Verdict));
        Assert.Equal(75, Rows.Number(at[2]), 9);
        Assert.Equal([ItemVerdict.Ok, ItemVerdict.Ok, ItemVerdict.Caution, ItemVerdict.Caution], below.Select(static r => r.Verdict));
    }

    [Fact]
    public void A_slow_core_is_compared_only_with_its_own_kind()
    {
        // Two kinds of core (P = 1, E = 0). Half the pace of the E cores is normal; one P core far below the others is not.
        byte[] classes = [1, 1, 1, 0, 0, 0];

        var even = Peers([100, 101, 99, 50, 51, 49], classes);
        var oneSlow = Peers([100, 40, 99, 50, 51, 49], classes);

        Assert.All(even, static r => Assert.Equal(ItemVerdict.Ok, r.Verdict));
        var slow = Assert.Single(oneSlow, static r => r.Verdict == ItemVerdict.Caution);
        Assert.Equal("cpu-2", Subject(slow));
        Assert.Equal(40.0 / 99.0 * 100, Rows.Number(slow), 6); // against the P median (99), not against everybody
        Assert.Equal(1, slow.Evidence!.EfficiencyClass);
    }

    [Fact]
    public void Each_peer_row_names_the_efficiency_class_of_its_cpu_as_an_int()
    {
        var rows = Peers([100, 100, 50, 50], [1, 1, 0, 0]);

        Assert.Equal([1, 1, 0, 0], rows.Select(static r => r.Evidence!.EfficiencyClass!.Value));
        Assert.All(rows, static r => Assert.Null(r.Evidence!.Code));
    }

    [Fact]
    public void A_kind_of_core_with_one_logical_cpu_is_its_own_median_and_cannot_be_a_caution()
    {
        var rows = Peers([100, 100, 10], [1, 1, 0]);

        Assert.Equal(100, Rows.Number(rows[2]), 9);
        Assert.Equal(ItemVerdict.Ok, rows[2].Verdict);
    }

    [Fact]
    public void A_median_of_zero_gives_no_ratio_and_the_row_is_unavailable_not_a_division_by_zero()
    {
        var rows = Peers([0, 0, 0]);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, static r =>
        {
            Assert.Null(r.Value);
            Assert.Equal(ItemVerdict.Unavailable, r.Verdict);
            Assert.Equal(CheckReason.Partial, r.Reason);
            Assert.Equal(0, r.Evidence!.EfficiencyClass);
        });
    }

    [Fact]
    public void A_median_of_zero_is_a_median_of_zero_even_when_one_cpu_did_work()
    {
        var rows = Peers([0, 0, 50]); // sorted 0 0 50: the upper median is 0

        Assert.All(rows, static r => Assert.Null(r.Value));
        Assert.All(rows, static r => Assert.Equal(ItemVerdict.Unavailable, r.Verdict));
    }

    [Fact]
    public void A_cpu_whose_pin_did_not_take_is_not_compared_and_is_not_in_anybodys_median()
    {
        // pinned: 100 and 50 (upper median 100). If the unpinned 10 were counted, the median of 10 50 100 would be 50.
        var rows = Peers([100, 50, 10], pins: [PinOutcome.Pinned, PinOutcome.Pinned, PinOutcome.Unknown]);

        Assert.Equal(100, Rows.Number(rows[0]), 9);
        Assert.Equal(ItemVerdict.Ok, rows[0].Verdict);
        Assert.Equal(50, Rows.Number(rows[1]), 9);
        Assert.Equal(ItemVerdict.Caution, rows[1].Verdict);
        Assert.Null(rows[2].Value);
        Assert.Equal(ItemVerdict.Unavailable, rows[2].Verdict);
        Assert.Equal(CheckReason.Partial, rows[2].Reason);
        Assert.Equal(CheckEvidenceCode.PinUnknown, rows[2].Evidence!.Code);
        Assert.Equal(0, rows[2].Evidence!.EfficiencyClass);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Unknown")]
    public void A_pin_that_failed_and_a_pin_that_is_not_known_are_both_pin_unknown(string outcomeName)
    {
        var rows = Peers([100, 100], pins: [PinOutcome.Pinned, Enum.Parse<PinOutcome>(outcomeName)]);

        Assert.Equal(CheckEvidenceCode.PinUnknown, rows[1].Evidence!.Code);
        Assert.Null(rows[1].Value);
        Assert.Equal(ItemVerdict.Unavailable, rows[1].Verdict);
        Assert.Equal(CheckReason.Partial, rows[1].Reason); // both when Windows refused the pin and when it is not known (DESIGN-A 14.2)
        Assert.Equal(ItemVerdict.Ok, rows[0].Verdict);
    }

    [Fact]
    public void A_cpu_that_was_not_pinned_still_has_its_logical_throughput()
    {
        var items = Judge(Loads.Make(Cpus.Uniform(2), [1000, 800], pins: [PinOutcome.Pinned, PinOutcome.Failed]));

        var rows = Rows.All(items, "cpu.logical_throughput");

        Assert.Equal([1000.0, 800.0], rows.Select(Rows.Number));
    }

    [Fact]
    public void Every_cpu_that_was_not_pinned_leaves_the_pinned_ones_with_no_median_so_none_of_them_has_a_ratio()
    {
        var rows = Peers([100, 100], pins: [PinOutcome.Failed, PinOutcome.Failed]);

        Assert.All(rows, static r => Assert.Equal(CheckEvidenceCode.PinUnknown, r.Evidence!.Code));
        Assert.All(rows, static r => Assert.Equal(CheckReason.Partial, r.Reason));
    }

    [Fact]
    public void Before_the_first_second_there_is_no_ratio_to_give_and_it_is_partial()
    {
        var rows = Peers([100, 100], seconds: 0, stopped: true);

        Assert.All(rows, static r =>
        {
            Assert.Null(r.Value);
            Assert.Equal(ItemVerdict.Unavailable, r.Verdict);
            Assert.Equal(CheckReason.Partial, r.Reason);
        });
    }

    [Fact]
    public void The_medians_are_taken_per_class_from_the_cpus_that_were_pinned()
    {
        var odd = Loads.Make(Cpus.OfClasses(1, 1, 1, 0, 0), [100, 40, 99, 20, 30]);
        var withUnpinned = Loads.Make(Cpus.Uniform(3), [100, 50, 10], pins: [PinOutcome.Pinned, PinOutcome.Pinned, PinOutcome.Failed]);

        var medians = CpuJudge.MediansByClass(odd);

        Assert.Equal(99, medians[1]);
        Assert.Equal(30, medians[0]); // two values: the upper one
        Assert.Equal(100, CpuJudge.MediansByClass(withUnpinned)[0]);
    }

    [Fact]
    public void The_median_of_an_even_number_of_pinned_cpus_is_the_upper_one_and_the_cpus_that_were_not_pinned_are_not_in_it()
    {
        // Class 0: pinned 100, 60, 20, 10 (even count: the upper median is the element at 2 of the sorted 10 20 60 100, which is 60), and two
        // CPUs that were not pinned with 1000 and 5000 (counted, the median of the six would be 100). Class 1: one pinned CPU of its own.
        var cpus = Cpus.OfClasses(0, 0, 0, 0, 0, 0, 1);
        var pins = new[] { PinOutcome.Pinned, PinOutcome.Pinned, PinOutcome.Pinned, PinOutcome.Pinned, PinOutcome.Failed, PinOutcome.Unknown, PinOutcome.Pinned };
        var load = Loads.Make(cpus, [100, 60, 20, 10, 1000, 5000, 7], pins: pins);

        var medians = CpuJudge.MediansByClass(load);
        var rows = Rows.All(Judge(load), "cpu.peer_ratio");

        Assert.Equal(60, medians[0]);
        Assert.Equal(7, medians[1]);
        Assert.Equal(100.0 / 60 * 100, Rows.Number(rows[0]), 9);
        Assert.Equal(10.0 / 60 * 100, Rows.Number(rows[3]), 9);
        Assert.Equal(ItemVerdict.Caution, rows[3].Verdict); // 10 is under 75 % of 60
        Assert.Equal(ItemVerdict.Caution, rows[2].Verdict); // 20 is under 75 % of 60
        Assert.Equal(ItemVerdict.Ok, rows[1].Verdict);
        Assert.All(new[] { rows[4], rows[5] }, static r => Assert.Equal(ItemVerdict.Unavailable, r.Verdict));
    }

    // ---- the effective clock ----

    [Fact]
    public void A_steady_clock_is_ok_with_its_average_and_the_clock_steadiness_in_the_evidence()
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(1), [1000], sample: _ => PdhSamples.Healthy(mhz: 4200))), "cpu.effective_clock");

        Assert.Equal(4200, Rows.Number(row), 6);
        Assert.Equal(ItemVerdict.Ok, row.Verdict);
        Assert.Null(row.Reason);
        Assert.Equal(CheckSource.Pdh, row.Source);
        Assert.Equal(100, row.Evidence!.ClockSteadiness!.Value, 6);
    }

    [Fact]
    public void A_clock_that_sags_below_85_percent_of_its_start_is_a_caution_and_the_value_is_the_average()
    {
        // 4000 MHz for the first 12 seconds and 2400 after them: the end is 60 % of the start
        var load = Loads.Make(Cpus.Uniform(1), [1000], sample: s => PdhSamples.Healthy(mhz: s <= 12 ? 4000 : 2400));

        var row = Rows.One(Judge(load), "cpu.effective_clock");

        Assert.Equal(ItemVerdict.Caution, row.Verdict);
        Assert.Equal(((12 * 4000.0) + (18 * 2400.0)) / 30, Rows.Number(row), 6);
        Assert.Equal(60, row.Evidence!.ClockSteadiness!.Value, 0);
    }

    [Fact]
    public void A_missing_clock_sample_is_never_counted_as_0_mhz()
    {
        // every second but one gives 3000 MHz. An average with a 0 in it would be 2900.
        var load = Loads.Make(Cpus.Uniform(1), [1000], sample: s => s == 5 ? PdhSamples.Nothing() : PdhSamples.Healthy(mhz: 3000));

        var row = Rows.One(Judge(load), "cpu.effective_clock");

        Assert.Equal(3000, Rows.Number(row), 9);
        Assert.Equal(ItemVerdict.Unavailable, row.Verdict);
        Assert.Equal(CheckReason.Partial, row.Reason);
        Assert.Null(row.Evidence); // the steadiness of the clock could not be computed with a second missing
    }

    [Fact]
    public void Several_missing_clock_samples_leave_the_average_of_the_ones_that_came()
    {
        var load = Loads.Make(Cpus.Uniform(1), [1000], seconds: 10, sample: s => s <= 4 ? PdhSamples.Nothing() : PdhSamples.Healthy(mhz: 2000 + s));

        var row = Rows.One(Judge(load), "cpu.effective_clock");

        Assert.Equal(2000 + ((5 + 6 + 7 + 8 + 9 + 10) / 6.0), Rows.Number(row), 9);
        Assert.Equal(CheckReason.Partial, row.Reason);
    }

    [Fact]
    public void When_no_sample_of_the_clock_ever_came_there_is_no_value_and_it_is_unsupported_not_zero()
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(1), [1000], sample: _ => PdhSamples.Nothing())), "cpu.effective_clock");

        Assert.Null(row.Value);
        Assert.Equal(ItemVerdict.Unavailable, row.Verdict);
        Assert.Equal(CheckReason.Unsupported, row.Reason);
        Assert.Equal(CheckSource.Pdh, row.Source);
    }

    [Fact]
    public void Before_the_first_second_the_clock_is_partial()
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(1), [1000], seconds: 0, stopped: true)), "cpu.effective_clock");

        Assert.Null(row.Value);
        Assert.Equal(ItemVerdict.Unavailable, row.Verdict);
        Assert.Equal(CheckReason.Partial, row.Reason);
    }

    [Fact]
    public void A_run_too_short_for_the_steadiness_keeps_the_clock_average_and_does_not_judge_it()
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(1), [1000], seconds: 7, sample: _ => PdhSamples.Healthy(mhz: 3300))), "cpu.effective_clock");

        Assert.Equal(3300, Rows.Number(row), 9);
        Assert.Equal(ItemVerdict.Unavailable, row.Verdict);
        Assert.Equal(CheckReason.Partial, row.Reason);
    }

    // ---- utilization and package power ----

    [Fact]
    public void The_utilization_is_the_average_of_the_seconds_and_an_observation()
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(1), [1000], seconds: 10, sample: s => PdhSamples.Healthy(load: s % 2 == 0 ? 100 : 90))), "cpu.utilization");

        Assert.Equal(95, Rows.Number(row), 9);
        Assert.Equal(ItemVerdict.Info, row.Verdict);
        Assert.Null(row.Reason);
    }

    [Fact]
    public void A_missing_utilization_sample_makes_the_average_partial_but_keeps_it()
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(1), [1000], seconds: 10, sample: s => s == 3 ? PdhSamples.Nothing() : PdhSamples.Healthy(load: 80))), "cpu.utilization");

        Assert.Equal(80, Rows.Number(row), 9);
        Assert.Equal(ItemVerdict.Info, row.Verdict);
        Assert.Equal(CheckReason.Partial, row.Reason);
    }

    [Fact]
    public void No_utilization_sample_at_all_is_unsupported()
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(1), [1000], sample: _ => PdhSamples.Nothing())), "cpu.utilization");

        Assert.Null(row.Value);
        Assert.Equal(ItemVerdict.Info, row.Verdict);
        Assert.Equal(CheckReason.Unsupported, row.Reason);
    }

    [Fact]
    public void The_package_power_is_the_average_of_the_watts_that_came_and_an_observation()
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(1), [1000], seconds: 10, sample: s => PdhSamples.Healthy(watts: s <= 5 ? 60 : 80))), "cpu.package_power");

        Assert.Equal(70, Rows.Number(row), 9);
        Assert.Equal(ItemVerdict.Info, row.Verdict);
        Assert.Equal(CheckSource.EnergyMeter, row.Source);
        Assert.Equal("W", row.Unit);
        Assert.Null(row.Reason);
    }

    [Theory]
    [InlineData("NoCounter", CheckReason.Unsupported)]
    [InlineData("NoPackageInstance", CheckReason.Unsupported)]
    [InlineData("NoValidSample", CheckReason.Error)]
    public void A_power_that_could_not_be_confirmed_has_no_value_and_says_why(string stateName, CheckReason reason)
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(1), [1000], sample: _ => PdhSamples.Nothing(Enum.Parse<PackagePowerState>(stateName)))), "cpu.package_power");

        Assert.Null(row.Value);
        Assert.Equal(ItemVerdict.Info, row.Verdict);
        Assert.Equal(reason, row.Reason);
    }

    [Fact]
    public void Some_seconds_without_a_power_leave_the_average_of_the_others_as_partial()
    {
        var row = Rows.One(Judge(Loads.Make(Cpus.Uniform(1), [1000], seconds: 10, sample: s => s > 8 ? PdhSamples.Nothing() : PdhSamples.Healthy(watts: 50))), "cpu.package_power");

        Assert.Equal(50, Rows.Number(row), 9);
        Assert.Equal(CheckReason.Partial, row.Reason);
        Assert.Equal(ItemVerdict.Info, row.Verdict);
    }

    // ---- a stopped run ----

    [Fact]
    public void A_stopped_run_keeps_every_value_and_says_partial_and_has_no_ok_judged_row()
    {
        var items = Judge(Loads.Make(Cpus.Uniform(3), [1000, 1000, 1000], seconds: 12, stopped: true));

        foreach (var id in new[] { "cpu.duration", "cpu.chunks", "cpu.throughput", "cpu.variation", "cpu.utilization", "cpu.package_power" })
        {
            var row = Rows.One(items, id);
            Assert.NotNull(row.Value);
            Assert.Equal(CheckReason.Partial, row.Reason);
            Assert.Equal(ItemVerdict.Info, row.Verdict);
        }

        Assert.All(Rows.All(items, "cpu.logical_throughput"), static r => Assert.Equal(CheckReason.Partial, r.Reason));
        Assert.DoesNotContain(items, static i => i.Verdict == ItemVerdict.Ok);
        foreach (var id in new[] { "cpu.steadiness", "cpu.effective_clock", "cpu.compute_errors", "cpu.peer_ratio" })
        {
            Assert.All(Rows.All(items, id), static r =>
            {
                Assert.NotNull(r.Value);
                Assert.Equal(ItemVerdict.Unavailable, r.Verdict);
                Assert.Equal(CheckReason.Partial, r.Reason);
            });
        }
    }

    [Fact]
    public void A_stopped_run_keeps_a_caution_it_found()
    {
        var rows = Peers([100, 100, 60], seconds: 12, stopped: true);

        Assert.Equal([ItemVerdict.Unavailable, ItemVerdict.Unavailable, ItemVerdict.Caution], rows.Select(static r => r.Verdict));
        Assert.All(rows, static r => Assert.Equal(CheckReason.Partial, r.Reason));
        Assert.Equal(60, Rows.Number(rows[2]), 9);
    }

    // ---- every shape of result is one the validator accepts ----

    public static TheoryData<string> Shapes { get; } = new()
    {
        "healthy", "one-cpu", "errors", "stopped-early", "stopped-no-second", "short", "no-pdh", "pin-failed", "throttled", "zero-median",
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Every_shape_of_load_gives_rows_that_pass_the_strict_validator(string shape)
    {
        var (load, outcome) = Make(shape);

        var result = CheckResult.Create(Samples.App, Samples.CpuRun, CheckKind.Cpu, Samples.Start, Samples.Start.AddSeconds(31), outcome, Judge(load), [CheckNotes.CpuTemperatureNotMeasured]);

        Assert.Empty(CheckResultValidator.Validate(result));
    }

    private static (CpuLoadResult Load, CheckOutcome Outcome) Make(string shape) => shape switch
    {
        "healthy" => (Loads.Make(Cpus.Uniform(4), [1000, 1000, 1000, 1000]), CheckOutcome.Completed),
        "one-cpu" => (Loads.Make(Cpus.Uniform(1), [1000]), CheckOutcome.Completed),
        "errors" => (Loads.Make(Cpus.Uniform(2), [1000, 1000], errors: 4), CheckOutcome.Completed),
        "stopped-early" => (Loads.Make(Cpus.Uniform(2), [1000, 1000], seconds: 5, errors: 1, stopped: true), CheckOutcome.Stopped),
        "stopped-no-second" => (Loads.Make(Cpus.Uniform(2), [1000, 1000], seconds: 0, stopped: true), CheckOutcome.Stopped),
        "short" => (Loads.Make(Cpus.Uniform(2), [1000, 1000], seconds: 6), CheckOutcome.Completed),
        "no-pdh" => (Loads.Make(Cpus.Uniform(2), [1000, 1000], sample: _ => PdhSamples.Nothing(PackagePowerState.NoCounter)), CheckOutcome.Completed),
        "pin-failed" => (Loads.Make(Cpus.Uniform(3), [1000, 1000, 1000], pins: [PinOutcome.Pinned, PinOutcome.Failed, PinOutcome.Unknown]), CheckOutcome.Completed),
        "throttled" => (Loads.FromSeries(Cpus.OfClasses(1, 1, 0), s => s <= 12 ? [100.0, 100.0, 50.0] : [60.0, 60.0, 30.0], 30, s => PdhSamples.Healthy(mhz: s <= 12 ? 4000 : 2400)), CheckOutcome.Completed),
        "zero-median" => (Loads.Make(Cpus.Uniform(3), [0, 0, 0]), CheckOutcome.Completed),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };
}
