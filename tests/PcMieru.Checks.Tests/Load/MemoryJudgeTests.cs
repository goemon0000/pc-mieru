using PcMieru.Checks.Load;

namespace PcMieru.Checks.Tests.Load;

/// <summary>
/// The rows and verdicts of the memory check (DESIGN-A §3.2, §14.3) from a measured load: the one judged row (mismatches), the observations, what
/// a stopped run keeps and what may never be 0. No thread, no clock, no memory.
/// </summary>
public sealed class MemoryJudgeTests
{
    private const long Mib = 1L << 20;
    private const long Gib = 1L << 30;

    private static readonly string[] RowIds =
    [
        "memory.duration", "memory.total", "memory.available_at_start", "memory.borrowed", "memory.passes",
        "memory.verified", "memory.mismatches", "memory.write_rate", "memory.verify_rate",
    ];

    // A whole healthy run: 100 blocks of 64 MiB (6400 MiB) borrowed on an 8 GiB machine, 12 passes in 30 s, nothing found.
    private static MemoryLoadResult Healthy() => new()
    {
        StatusKnown = true,
        TotalBytes = 8 * Gib,
        AvailableAtStartBytes = 6 * Gib,
        BlockBytes = 64 * Mib,
        Blocks = 100,
        Workers = 4,
        Seconds = 30.012,
        Passes = 12,
        WriteBytes = 12 * 6400 * Mib,
        WriteSeconds = 12,
        VerifiedBytes = 12 * 6400 * Mib,
        VerifySeconds = 10,
        Mismatches = 0,
    };

    private static IReadOnlyList<CheckItem> Judge(MemoryLoadResult load) => MemoryJudge.Judge(load);

    private static CheckItem Row(IReadOnlyList<CheckItem> items, string id) => Rows.One(items, id);

    // ---- a healthy run ----

    [Fact]
    public void A_healthy_run_gives_the_rows_of_the_design_in_order_with_their_units_and_sources()
    {
        var items = Judge(Healthy());

        Assert.Equal(RowIds, items.Select(static i => i.Id));
        Assert.Equal(["s", "B", "B", "B", "count", "B", "count", "MiB/s", "MiB/s"], items.Select(static i => i.Unit));
        Assert.Equal(
            [
                CheckSource.Load, CheckSource.Win32, CheckSource.Win32, CheckSource.Load, CheckSource.Load,
                CheckSource.Load, CheckSource.Load, CheckSource.Load, CheckSource.Load,
            ],
            items.Select(static i => i.Source));
        Assert.All(items, static i => Assert.Null(i.Subject));
    }

    [Fact]
    public void In_a_healthy_run_the_mismatch_row_is_the_only_judged_row_it_is_ok_and_nothing_has_a_reason()
    {
        var items = Judge(Healthy());

        Assert.All(items, static i => Assert.Null(i.Reason));
        Assert.Equal(ItemVerdict.Ok, Row(items, "memory.mismatches").Verdict);
        Assert.Equal(CheckValue.Int(0), Row(items, "memory.mismatches").Value);
        Assert.Null(Row(items, "memory.mismatches").Evidence);
        Assert.Single(items, static i => i.Verdict == ItemVerdict.Ok);
        Assert.All(items.Where(static i => i.Id != "memory.mismatches"), static i => Assert.Equal(ItemVerdict.Info, i.Verdict));
    }

    [Fact]
    public void The_observations_carry_what_was_measured()
    {
        var load = Healthy();

        var items = Judge(load);

        Assert.Equal(30.012, Rows.Number(Row(items, "memory.duration")), 6);
        Assert.Equal(CheckValue.Dec((ulong)(8 * Gib)), Row(items, "memory.total").Value);
        Assert.Equal(CheckValue.Dec((ulong)(6 * Gib)), Row(items, "memory.available_at_start").Value);
        Assert.Equal(CheckValue.Dec((ulong)(6400 * Mib)), Row(items, "memory.borrowed").Value);
        Assert.Equal(12, Row(items, "memory.passes").Value!.AsInt);
        Assert.Equal(CheckValue.Dec((ulong)(12 * 6400 * Mib)), Row(items, "memory.verified").Value);
        Assert.Equal(12 * 6400.0 / 12, Rows.Number(Row(items, "memory.write_rate")), 6);
        Assert.Equal(12 * 6400.0 / 10, Rows.Number(Row(items, "memory.verify_rate")), 6);
    }

    [Fact]
    public void A_rate_is_the_mib_in_the_seconds_of_the_phases()
    {
        var load = Healthy();
        load.WriteBytes = 3 * Mib;
        load.WriteSeconds = 1.5;
        load.VerifiedBytes = 9 * Mib;
        load.VerifySeconds = 2;

        var items = Judge(load);

        Assert.Equal(2.0, Rows.Number(Row(items, "memory.write_rate")), 9);
        Assert.Equal(4.5, Rows.Number(Row(items, "memory.verify_rate")), 9);
    }

    // ---- a finding ----

    [Fact]
    public void A_mismatch_is_a_check_with_the_count_and_the_evidence_and_no_reason()
    {
        var load = Healthy();
        load.Mismatches = 3;

        var items = Judge(load);

        var row = Row(items, "memory.mismatches");
        Assert.Equal(ItemVerdict.Check, row.Verdict);
        Assert.Equal(CheckValue.Int(3), row.Value);
        Assert.Null(row.Reason);
        Assert.Equal(CheckEvidenceCode.PatternMismatch, row.Evidence!.Code);
        Assert.DoesNotContain(items, static i => i.Verdict == ItemVerdict.Ok);
    }

    [Fact]
    public void Nothing_that_identifies_a_place_is_kept_with_a_mismatch()
    {
        var load = Healthy();
        load.Mismatches = 3;

        var evidence = Row(Judge(load), "memory.mismatches").Evidence!;

        Assert.Equal(CheckEvidenceCode.PatternMismatch, evidence.Code);
        Assert.Equal(CheckEvidence.OfCode(CheckEvidenceCode.PatternMismatch), evidence); // the code and nothing else
    }

    [Fact]
    public void A_very_large_count_of_mismatches_is_kept_whole()
    {
        var load = Healthy();
        load.Mismatches = 9_000_000_000_000;

        var row = Row(Judge(load), "memory.mismatches");

        Assert.Equal(9_000_000_000_000, row.Value!.AsInt);
        Assert.Equal(ItemVerdict.Check, row.Verdict);
    }

    [Fact]
    public void A_mismatch_that_was_found_before_the_stop_stays_a_check_and_says_partial()
    {
        var load = Healthy();
        load.Stopped = true;
        load.Mismatches = 1;

        var items = Judge(load);

        var row = Row(items, "memory.mismatches");
        Assert.Equal(ItemVerdict.Check, row.Verdict);
        Assert.Equal(CheckValue.Int(1), row.Value);
        Assert.Equal(CheckReason.Partial, row.Reason);
        Assert.Equal(CheckEvidenceCode.PatternMismatch, row.Evidence!.Code);
        Assert.DoesNotContain(items, static i => i.Verdict == ItemVerdict.Ok);
    }

    // ---- a run that was cut short ----

    [Fact]
    public void A_stopped_run_with_nothing_found_is_unavailable_with_the_zero_and_partial_and_never_ok()
    {
        var load = Healthy();
        load.Stopped = true;

        var items = Judge(load);

        var row = Row(items, "memory.mismatches");
        Assert.Equal(ItemVerdict.Unavailable, row.Verdict);
        Assert.Equal(CheckValue.Int(0), row.Value);
        Assert.Equal(CheckReason.Partial, row.Reason);
        Assert.Null(row.Evidence);
        Assert.DoesNotContain(items, static i => i.Verdict == ItemVerdict.Ok);
    }

    [Fact]
    public void A_stopped_run_makes_every_row_that_has_a_value_partial()
    {
        var load = Healthy();
        load.Stopped = true;

        var items = Judge(load);

        Assert.Equal(RowIds, items.Select(static i => i.Id));
        Assert.All(items, static i =>
        {
            Assert.NotNull(i.Value);
            Assert.Equal(CheckReason.Partial, i.Reason);
        });
        Assert.DoesNotContain(items, static i => i.Verdict == ItemVerdict.Ok);
    }

    [Fact]
    public void A_stopped_run_keeps_the_sources_and_the_observation_verdicts()
    {
        var load = Healthy();
        load.Stopped = true;

        var items = Judge(load);

        Assert.Equal(CheckSource.Win32, Row(items, "memory.total").Source);
        Assert.Equal(CheckSource.Win32, Row(items, "memory.available_at_start").Source);
        Assert.Equal(CheckSource.Load, Row(items, "memory.verified").Source);
        Assert.Equal(ItemVerdict.Info, Row(items, "memory.borrowed").Verdict);
        Assert.Equal(ItemVerdict.Info, Row(items, "memory.write_rate").Verdict);
    }

    [Fact]
    public void A_stop_before_one_pass_is_over_has_no_passes_row_value_but_keeps_what_was_verified()
    {
        var load = Healthy();
        load.Stopped = true;
        load.Passes = 0;
        load.WriteBytes = 6400 * Mib;
        load.VerifiedBytes = 2000 * Mib;

        var items = Judge(load);

        var passes = Row(items, "memory.passes");
        Assert.Null(passes.Value);
        Assert.Equal(CheckReason.Partial, passes.Reason);
        Assert.Equal(CheckValue.Dec((ulong)(2000 * Mib)), Row(items, "memory.verified").Value);
        Assert.Equal(ItemVerdict.Unavailable, Row(items, "memory.mismatches").Verdict);
    }

    // ---- nothing verified ----

    [Fact]
    public void No_verified_byte_is_unavailable_with_no_value_and_partial_whether_the_run_was_stopped_or_not()
    {
        foreach (var stopped in new[] { false, true })
        {
            var load = Healthy();
            load.Stopped = stopped;
            load.VerifiedBytes = 0;
            load.VerifySeconds = 0;

            var items = Judge(load);

            var row = Row(items, "memory.mismatches");
            Assert.Equal(ItemVerdict.Unavailable, row.Verdict);
            Assert.Null(row.Value);
            Assert.Equal(CheckReason.Partial, row.Reason);
            var verified = Row(items, "memory.verified");
            Assert.Equal(CheckValue.Dec(0), verified.Value);
            Assert.Equal(CheckReason.Partial, verified.Reason);
            Assert.DoesNotContain(items, static i => i.Verdict == ItemVerdict.Ok);
        }
    }

    [Fact]
    public void A_mismatch_found_with_no_verified_byte_cannot_happen_but_is_still_a_finding()
    {
        // The counters cannot say that (a mismatch is found in a verified byte), but the judge does not hide a count that is there.
        var load = Healthy();
        load.VerifiedBytes = 0;
        load.Mismatches = 2;

        Assert.Equal(ItemVerdict.Check, Row(Judge(load), "memory.mismatches").Verdict);
    }

    [Theory]
    [InlineData(0L, 5.0)]
    [InlineData(1_048_576L, 0.0)]
    [InlineData(0L, 0.0)]
    public void A_rate_over_no_bytes_or_no_time_is_missing_with_a_reason_and_never_zero(long bytes, double seconds)
    {
        var load = Healthy();
        load.WriteBytes = bytes;
        load.WriteSeconds = seconds;
        load.VerifiedBytes = bytes;
        load.VerifySeconds = seconds;

        var items = Judge(load);

        foreach (var id in new[] { "memory.write_rate", "memory.verify_rate" })
        {
            var row = Row(items, id);
            Assert.Null(row.Value);
            Assert.Equal(CheckReason.Partial, row.Reason);
            Assert.Equal(ItemVerdict.Info, row.Verdict);
        }
    }

    // ---- nothing borrowed ----

    [Fact]
    public void A_run_that_could_borrow_nothing_says_zero_borrowed_and_what_was_not_measured_is_missing()
    {
        var load = new MemoryLoadResult
        {
            StatusKnown = true,
            TotalBytes = 8 * Gib,
            AvailableAtStartBytes = Gib + 32 * Mib,
            BlockBytes = 64 * Mib,
            Blocks = 0,
            Seconds = 0.001,
            Pressure = true,
        };

        var items = Judge(load);

        Assert.Equal(RowIds, items.Select(static i => i.Id));
        var borrowed = Row(items, "memory.borrowed");
        Assert.Equal(CheckValue.Dec(0), borrowed.Value);
        Assert.Equal(ItemVerdict.Info, borrowed.Verdict);
        Assert.Null(borrowed.Reason); // the run was not cut short: it could not borrow
        Assert.Null(Row(items, "memory.passes").Value);
        Assert.Equal(CheckReason.Partial, Row(items, "memory.passes").Reason);
        Assert.Equal(ItemVerdict.Unavailable, Row(items, "memory.mismatches").Verdict);
        Assert.Null(Row(items, "memory.mismatches").Value);
        Assert.Null(Row(items, "memory.write_rate").Value);
        Assert.Null(Row(items, "memory.verify_rate").Value);
        Assert.DoesNotContain(items, static i => i.Verdict == ItemVerdict.Ok);
    }

    // ---- windows would not say ----

    [Fact]
    public void A_status_that_could_not_be_read_has_total_and_free_missing_with_the_reason_error()
    {
        var load = new MemoryLoadResult { StatusKnown = false, BlockBytes = 64 * Mib };

        var items = Judge(load);

        foreach (var id in new[] { "memory.total", "memory.available_at_start" })
        {
            var row = Row(items, id);
            Assert.Null(row.Value);
            Assert.Equal(CheckReason.Error, row.Reason);
            Assert.Equal(ItemVerdict.Info, row.Verdict);
            Assert.Equal(CheckSource.Win32, row.Source);
        }

        Assert.DoesNotContain(items, static i => i.Verdict == ItemVerdict.Ok);
    }

    [Fact]
    public void No_load_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => MemoryJudge.Judge(null!));
    }
}
