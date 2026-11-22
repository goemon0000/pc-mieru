using PcMieru.Checks.Load;

namespace PcMieru.Checks.Tests.Load;

/// <summary>
/// The steadiness of a per-second series and its two verdicts (DESIGN-A §14.2): the old boundaries, exactly. The first four tests are the old
/// SteadinessTests of the old project, carried over.
/// </summary>
public sealed class SteadinessTests
{
    // ---- ports of the old SteadinessTests ----

    [Fact]
    public void Flat_series_scores_100()
    {
        var s = Steadiness.Compute(Enumerable.Repeat(100.0, 30).ToList())!;

        Assert.Equal(100, s.Score, 3);
        Assert.Equal(0, s.Cv, 3);
        Assert.Equal(ItemVerdict.Ok, Steadiness.Judge(s));
    }

    [Fact]
    public void Throttled_series_is_flagged()
    {
        // 100 at the start, 60 at the end (the shape of a pace pulled down by heat)
        var series = Enumerable.Range(0, 30).Select(i => i < 12 ? 100.0 : 60.0).ToList();

        var s = Steadiness.Compute(series)!;

        Assert.Equal(60, s.Score, 0);
        Assert.Equal(ItemVerdict.Check, Steadiness.Judge(s));
    }

    [Fact]
    public void Warm_up_samples_are_skipped_and_short_series_is_not_judged()
    {
        var series = new List<double> { 10, 20, 100, 100, 100, 100, 100, 100 };

        Assert.Equal(100, Steadiness.Compute(series)!.Score, 3);
        Assert.Null(Steadiness.Compute(new List<double> { 1, 2, 3, 4, 5, 6, 7 }));
    }

    [Fact]
    public void Rising_series_is_capped_at_100()
    {
        var s = Steadiness.Compute(Enumerable.Range(0, 20).Select(i => 50.0 + i).ToList())!;

        Assert.Equal(100, s.Score, 3);
    }

    // ---- the number of samples ----

    [Fact]
    public void Two_are_dropped_and_six_are_needed_so_seven_is_too_short_and_eight_is_enough()
    {
        Assert.Null(Steadiness.Compute([.. Enumerable.Repeat(100.0, 7)]));
        Assert.NotNull(Steadiness.Compute([.. Enumerable.Repeat(100.0, 8)]));
        Assert.Equal(2, Steadiness.DefaultSkip);
        Assert.Equal(6, Steadiness.MinimumSamples);
    }

    [Fact]
    public void The_skip_can_be_given_and_what_is_left_is_counted_after_it()
    {
        var series = Enumerable.Repeat(100.0, 6).ToList();

        Assert.NotNull(Steadiness.Compute(series, skip: 0));
        Assert.Null(Steadiness.Compute(series, skip: 1));
        Assert.Equal(6, Steadiness.Compute(Enumerable.Repeat(100.0, 8).ToList())!.Samples);
    }

    [Fact]
    public void The_two_samples_of_the_warm_up_do_not_enter_the_averages_or_the_spread()
    {
        var s = Steadiness.Compute([1, 999, 50, 50, 50, 50, 50, 50])!;

        Assert.Equal(50, s.FirstAverage, 9);
        Assert.Equal(50, s.LastAverage, 9);
        Assert.Equal(0, s.Cv, 9);
    }

    [Fact]
    public void An_empty_series_and_a_missing_one_are_handled()
    {
        Assert.Null(Steadiness.Compute([]));
        Assert.Throws<ArgumentNullException>(() => Steadiness.Compute(null!));
    }

    // ---- the score: the end against the start, by thirds ----

    [Fact]
    public void The_score_is_the_last_third_over_the_first_third_and_at_least_two_samples_make_a_third()
    {
        // 6 samples after the skip: a third is 2 (max(2, 6/3)). First two 100, last two 80: 80 %.
        var six = Steadiness.Compute([0, 0, 100, 100, 90, 90, 80, 80])!;
        // 9 samples: a third is 3. First three 100, last three 50: 50 %.
        var nine = Steadiness.Compute([0, 0, 100, 100, 100, 75, 75, 75, 50, 50, 50])!;

        Assert.Equal(80, six.Score, 9);
        Assert.Equal(50, nine.Score, 9);
        Assert.Equal(6, six.Samples);
        Assert.Equal(9, nine.Samples);
    }

    [Fact]
    public void A_start_of_zero_scores_zero_and_a_mean_of_zero_has_no_spread()
    {
        var s = Steadiness.Compute([0, 0, 0, 0, 0, 0, 0, 0])!;

        Assert.Equal(0, s.Score);
        Assert.Equal(0, s.Cv);
    }

    [Fact]
    public void The_spread_is_the_standard_deviation_over_the_mean_of_the_whole_series()
    {
        // 6 samples alternating 90 / 110: mean 100, every deviation 10, sd 10: cv 0.10
        var s = Steadiness.Compute([0, 0, 90, 110, 90, 110, 90, 110])!;

        Assert.Equal(0.10, s.Cv, 9);
    }

    // ---- the verdict: < 70 check, < 90 or cv > 0.10 caution ----

    private static SteadinessResult Result(double score, double cv) => new(score, cv, 100, score, 20);

    [Theory]
    [InlineData(100, 0.0, ItemVerdict.Ok)]
    [InlineData(90.0, 0.10, ItemVerdict.Ok)] // both boundaries are not past
    [InlineData(89.999, 0.0, ItemVerdict.Caution)]
    [InlineData(90.0, 0.1001, ItemVerdict.Caution)]
    [InlineData(100, 0.1001, ItemVerdict.Caution)]
    [InlineData(70.0, 0.0, ItemVerdict.Caution)] // 70 is not below 70
    [InlineData(69.999, 0.0, ItemVerdict.Check)]
    [InlineData(69.999, 0.5, ItemVerdict.Check)] // the lower line wins over the spread
    [InlineData(0, 0.0, ItemVerdict.Check)]
    public void The_steadiness_verdict_has_the_old_boundaries(double score, double cv, ItemVerdict expected)
    {
        Assert.Equal(expected, Steadiness.Judge(Result(score, cv)));
    }

    [Theory]
    [InlineData(100, ItemVerdict.Ok)]
    [InlineData(85.0, ItemVerdict.Ok)]
    [InlineData(84.999, ItemVerdict.Caution)]
    [InlineData(0, ItemVerdict.Caution)]
    public void The_clock_verdict_is_caution_below_85_and_never_check(double score, ItemVerdict expected)
    {
        Assert.Equal(expected, Steadiness.JudgeClock(Result(score, 0)));
    }

    [Fact]
    public void The_clock_verdict_ignores_the_spread()
    {
        Assert.Equal(ItemVerdict.Ok, Steadiness.JudgeClock(Result(95, 0.9)));
    }

    [Fact]
    public void The_verdicts_need_a_result()
    {
        Assert.Throws<ArgumentNullException>(() => Steadiness.Judge(null!));
        Assert.Throws<ArgumentNullException>(() => Steadiness.JudgeClock(null!));
    }
}
