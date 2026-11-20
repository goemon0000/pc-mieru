using PcMieru.Checks.Storage;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Tests.Storage;

/// <summary>
/// The counters the baseline takes from a drive and the continuity check between two records (v0.7.4's Counters, BaselineStore.Continuity
/// and the Monotonic list), with the boundaries written out (DESIGN-A §5.2, §14.5).
/// </summary>
public class BaselineCountersTests
{
    private static readonly DateTimeOffset T0 = Drives.T0;

    private static SmartAttribute Attribute(int id, ulong raw) => new(id, "A" + id, 100, 100, 0, new U128(raw), true);

    private static SmartAttribute Wide(int id, UInt128 raw) => new(id, "A" + id, 100, 100, 0, new U128(raw), true);

    private static StorageInspectionDrive SataWith(params SmartAttribute[] attributes)
    {
        var drive = Drives.Sata();
        return drive with { Health = Value.Ok(drive.Health.Data! with { Attributes = attributes }, "test") };
    }

    private static BaselineEntry Entry(DateTimeOffset at, params (string Name, ulong Value)[] counters)
    {
        var entry = new BaselineEntry { At = at };
        foreach (var (name, value) in counters)
        {
            entry.Counters[name] = value;
        }

        return entry;
    }

    private static CounterSet Set(params (string Name, ulong Value)[] counters)
    {
        var set = new CounterSet();
        foreach (var (name, value) in counters)
        {
            set.Set(name, value);
        }

        return set;
    }

    // ---- what is taken from a drive ----

    [Fact]
    public void An_nvme_drive_gives_the_eight_names_v074_stored_with_their_values()
    {
        var set = BaselineCounters.From(Drives.Nvme(media: 2, poh: 700, written: 9000, unsafeShutdowns: 8, errorLog: 4, used: 12, spare: 96, critical: 3));

        var expected = new Dictionary<string, ulong>
        {
            ["nvme.media_errors"] = 2,
            ["nvme.error_log_entries"] = 4,
            ["nvme.unsafe_shutdowns"] = 8,
            ["nvme.power_on_hours"] = 700,
            ["nvme.data_units_written"] = 9000,
            ["nvme.percentage_used"] = 12,
            ["nvme.available_spare"] = 96,
            ["nvme.critical_warning"] = 3,
        };
        Assert.Equal(expected.OrderBy(static p => p.Key, StringComparer.Ordinal), set.Values.OrderBy(static p => p.Key, StringComparer.Ordinal));
        Assert.Empty(set.Inexact);
    }

    [Fact]
    public void A_sata_drive_gives_the_stored_ata_ids_only()
    {
        var set = BaselineCounters.From(SataWith(
            Attribute(1, 77), Attribute(5, 3), Attribute(9, 1234), Attribute(12, 40), Attribute(187, 1), Attribute(188, 9), Attribute(190, 41),
            Attribute(194, 33), Attribute(196, 2), Attribute(197, 4), Attribute(198, 5), Attribute(199, 6), Attribute(231, 100)));

        Assert.Equal(new[] { "ata.187", "ata.190", "ata.194", "ata.196", "ata.197", "ata.198", "ata.199", "ata.5", "ata.9" }, set.Values.Keys.ToArray());
        Assert.Equal(3UL, set.Values["ata.5"]);
        Assert.Equal(1234UL, set.Values["ata.9"]);
        Assert.Equal(6UL, set.Values["ata.199"]);
    }

    [Fact]
    public void Ata_9_keeps_the_low_32_bits_and_the_temperatures_the_low_8_bits()
    {
        var set = BaselineCounters.From(SataWith(
            Attribute(9, 0x0000_0123_0000_4567UL),
            Attribute(194, 0x0030_0020_0000_0021UL),
            Attribute(190, 0x0000_0000_0000_0142UL),
            Attribute(5, 0x0000_0123_0000_4567UL)));

        Assert.Equal(0x4567UL, set.Values["ata.9"]);
        Assert.Equal(0x21UL, set.Values["ata.194"]);
        Assert.Equal(0x42UL, set.Values["ata.190"]);
        Assert.Equal(0x0000_0123_0000_4567UL, set.Values["ata.5"]); // the others are whole
    }

    [Fact]
    public void A_repeated_ata_attribute_keeps_the_last_value()
    {
        var set = BaselineCounters.From(SataWith(Attribute(5, 3), Attribute(5, 9)));

        Assert.Equal(9UL, set.Values["ata.5"]);
    }

    [Fact]
    public void An_ata_raw_value_that_does_not_fit_in_64_bits_is_clipped_and_inexact()
    {
        var set = BaselineCounters.From(SataWith(Wide(5, (UInt128)ulong.MaxValue + 10)));

        Assert.Equal(ulong.MaxValue, set.Values["ata.5"]);
        Assert.Contains("ata.5", set.Inexact);
    }

    [Fact]
    public void A_drive_whose_health_could_not_be_read_gives_nothing()
    {
        Assert.True(BaselineCounters.From(Drives.NoHealth()).IsEmpty);
        Assert.True(BaselineCounters.From(Drives.Unreadable()).IsEmpty);
    }

    [Theory]
    [InlineData(12.5)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_percentage_that_is_not_a_whole_number_is_left_out_not_rounded(double used)
    {
        var set = BaselineCounters.From(Drives.Nvme(used: used, spare: used));

        Assert.DoesNotContain("nvme.percentage_used", set.Values.Keys);
        Assert.DoesNotContain("nvme.available_spare", set.Values.Keys);
        Assert.Contains("nvme.media_errors", set.Values.Keys); // the rest is still taken
    }

    [Fact]
    public void A_percentage_above_100_is_kept_as_it_is()
    {
        var set = BaselineCounters.From(Drives.Nvme(used: 130));

        Assert.Equal(130UL, set.Values["nvme.percentage_used"]);
    }

    [Fact]
    public void A_negative_critical_warning_is_left_out()
    {
        var set = BaselineCounters.From(Drives.Nvme(critical: -1));

        Assert.DoesNotContain("nvme.critical_warning", set.Values.Keys);
    }

    [Fact]
    public void A_counter_the_reader_could_not_represent_is_inexact_even_when_the_number_looks_small()
    {
        var set = BaselineCounters.From(Drives.WithWrittenUnits(Drives.Nvme(), 5, unrepresentable: true));

        Assert.Equal(5UL, set.Values["nvme.data_units_written"]);
        Assert.Contains("nvme.data_units_written", set.Inexact);
    }

    [Fact]
    public void A_128_bit_media_error_count_is_clipped_to_the_maximum_and_inexact()
    {
        var set = BaselineCounters.From(Drives.WithWideMedia(Drives.Nvme(), (UInt128)ulong.MaxValue + 1));

        Assert.Equal(ulong.MaxValue, set.Values["nvme.media_errors"]);
        Assert.Contains("nvme.media_errors", set.Inexact);
    }

    [Fact]
    public void The_error_log_count_comes_from_the_counters_and_falls_back_to_the_health_details()
    {
        var withCounters = Drives.Nvme(errorLog: 7);
        var withoutCounters = withCounters with { Counters = null };

        Assert.Equal(7UL, BaselineCounters.From(withCounters).Values["nvme.error_log_entries"]);
        Assert.DoesNotContain("nvme.data_units_written", BaselineCounters.From(withoutCounters).Values.Keys);
        Assert.DoesNotContain("nvme.error_log_entries", BaselineCounters.From(withoutCounters).Values.Keys); // no details in this fixture
    }

    // ---- CounterSet.Set ----

    [Fact]
    public void The_maximum_ulong_is_inexact_and_one_below_it_is_exact()
    {
        var set = new CounterSet();

        set.Set("a", ulong.MaxValue);
        set.Set("b", ulong.MaxValue - 1);
        set.Set("c", (UInt128)ulong.MaxValue + 1);
        set.Set("d", 5, exact: false);

        Assert.Equal(ulong.MaxValue, set.Values["a"]);
        Assert.Equal(ulong.MaxValue - 1, set.Values["b"]);
        Assert.Equal(ulong.MaxValue, set.Values["c"]);
        Assert.Equal(new[] { "a", "c", "d" }, set.Inexact.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Setting_an_exact_value_after_an_inexact_one_makes_the_name_exact_again()
    {
        var set = new CounterSet();
        set.Set("a", ulong.MaxValue);

        set.Set("a", 5);

        Assert.Equal(5UL, set.Values["a"]);
        Assert.Empty(set.Inexact);
    }

    // ---- continuity ----

    [Fact]
    public void Hours_that_advance_by_the_elapsed_time_plus_three_hours_are_the_same_disk_and_one_more_is_not()
    {
        var previous = Entry(T0, ("nvme.power_on_hours", 1000));

        Assert.Equal(BaselineCounters.Continuity.Same, BaselineCounters.Check(previous, Set(("nvme.power_on_hours", 1013)), T0.AddHours(10)));
        Assert.Equal(BaselineCounters.Continuity.AheadOfClock, BaselineCounters.Check(previous, Set(("nvme.power_on_hours", 1014)), T0.AddHours(10)));
    }

    [Fact]
    public void The_allowance_is_two_hours_of_slack_and_one_hour_of_clock_skew()
    {
        Assert.Equal(TimeSpan.FromHours(2), BaselineCounters.HoursSlack);
        Assert.Equal(TimeSpan.FromHours(1), BaselineCounters.ClockSkew);
    }

    [Fact]
    public void With_the_clock_behind_the_record_only_the_three_hours_are_allowed()
    {
        var previous = Entry(T0, ("nvme.power_on_hours", 1000));

        Assert.Equal(BaselineCounters.Continuity.Same, BaselineCounters.Check(previous, Set(("nvme.power_on_hours", 1003)), T0.AddHours(-5)));
        Assert.Equal(BaselineCounters.Continuity.AheadOfClock, BaselineCounters.Check(previous, Set(("nvme.power_on_hours", 1004)), T0.AddHours(-5)));
    }

    [Fact]
    public void Hours_that_went_back_are_a_reset_even_when_the_clock_also_looks_wrong()
    {
        var previous = Entry(T0, ("nvme.power_on_hours", 1000));

        Assert.Equal(BaselineCounters.Continuity.WentBackwards, BaselineCounters.Check(previous, Set(("nvme.power_on_hours", 999)), T0.AddHours(-5)));
        Assert.Equal(BaselineCounters.Continuity.Same, BaselineCounters.Check(previous, Set(("nvme.power_on_hours", 1000)), T0));
    }

    [Theory]
    [InlineData("nvme.data_units_written")]
    [InlineData("ata.9")]
    public void Each_counter_that_never_goes_down_going_down_is_a_reset(string name)
    {
        var previous = Entry(T0, (name, 500));

        Assert.Equal(BaselineCounters.Continuity.WentBackwards, BaselineCounters.Check(previous, Set((name, 499)), T0.AddHours(1)));
        Assert.Equal(BaselineCounters.Continuity.Same, BaselineCounters.Check(previous, Set((name, 500)), T0.AddHours(1)));
    }

    [Fact]
    public void Ata_9_is_never_ahead_of_the_clock_because_its_unit_depends_on_the_model()
    {
        var previous = Entry(T0, ("ata.9", 10));

        Assert.Equal(BaselineCounters.Continuity.Same, BaselineCounters.Check(previous, Set(("ata.9", 10_000_000)), T0.AddHours(1)));
    }

    [Theory]
    [InlineData("nvme.media_errors")]
    [InlineData("nvme.unsafe_shutdowns")]
    [InlineData("nvme.error_log_entries")]
    [InlineData("nvme.percentage_used")]
    [InlineData("ata.5")]
    public void A_counter_that_may_go_down_going_down_does_not_make_the_record_another_disks(string name)
    {
        var previous = Entry(T0, (name, 500));

        Assert.Equal(BaselineCounters.Continuity.Same, BaselineCounters.Check(previous, Set((name, 1)), T0.AddHours(1)));
    }

    [Fact]
    public void A_value_the_reader_could_not_represent_takes_no_part_in_the_check()
    {
        var current = new CounterSet();
        current.Set("nvme.data_units_written", 10, exact: false);
        var previous = Entry(T0, ("nvme.data_units_written", 500));

        Assert.Equal(BaselineCounters.Continuity.Same, BaselineCounters.Check(previous, current, T0));
        Assert.False(BaselineCounters.TryBoth(previous, current, "nvme.data_units_written", out _, out _));
    }

    [Fact]
    public void A_stored_maximum_takes_no_part_in_the_check()
    {
        var previous = Entry(T0, ("nvme.power_on_hours", ulong.MaxValue), ("nvme.data_units_written", ulong.MaxValue));

        Assert.Equal(BaselineCounters.Continuity.Same, BaselineCounters.Check(previous, Set(("nvme.power_on_hours", 5), ("nvme.data_units_written", 5)), T0));
        Assert.False(BaselineCounters.TryBoth(previous, Set(("nvme.power_on_hours", 5)), "nvme.power_on_hours", out _, out _));
    }

    [Fact]
    public void A_counter_missing_from_either_side_is_not_checked()
    {
        var previous = Entry(T0, ("nvme.power_on_hours", 1000));

        Assert.Equal(BaselineCounters.Continuity.Same, BaselineCounters.Check(previous, Set(("nvme.media_errors", 0)), T0));
        Assert.Equal(BaselineCounters.Continuity.Same, BaselineCounters.Check(Entry(T0), Set(("nvme.power_on_hours", 1)), T0));
    }

    // ---- what growing means, and the 15 counters ----

    [Theory]
    [InlineData("nvme.critical_warning", ItemVerdict.Check)]
    [InlineData("nvme.media_errors", ItemVerdict.Check)]
    [InlineData("nvme.error_log_entries", ItemVerdict.Caution)]
    [InlineData("ata.5", ItemVerdict.Check)]
    [InlineData("ata.187", ItemVerdict.Check)]
    [InlineData("ata.196", ItemVerdict.Check)]
    [InlineData("ata.197", ItemVerdict.Check)]
    [InlineData("ata.198", ItemVerdict.Check)]
    [InlineData("ata.199", ItemVerdict.Caution)]
    public void The_counters_that_should_not_grow_say_check_or_caution_when_they_do(string name, ItemVerdict expected)
    {
        Assert.Equal(expected, BaselineCounters.GrowthVerdict(name));
    }

    [Theory]
    [InlineData("nvme.available_spare")]
    [InlineData("nvme.percentage_used")]
    [InlineData("nvme.unsafe_shutdowns")]
    [InlineData("nvme.power_on_hours")]
    [InlineData("nvme.data_units_written")]
    [InlineData("ata.9")]
    [InlineData("ata.190")]
    [InlineData("ata.194")]
    public void The_counters_that_grow_all_the_time_have_no_growth_verdict(string name)
    {
        Assert.Null(BaselineCounters.GrowthVerdict(name));
    }

    [Fact]
    public void The_delta_counters_are_the_registrys_delta_rows_in_the_registrys_order_and_judged_means_a_growth_verdict()
    {
        var specs = CheckItemRegistry.ForKind(CheckKind.Storage).Where(static s => s.IsDelta).ToList();

        Assert.Equal(15, specs.Count);
        Assert.Equal(specs.Select(static s => s.DeltaOf!).ToList(), BaselineCounters.DeltaCounters.Select(BaselineCounters.ItemId).ToList());
        foreach (var spec in specs)
        {
            var name = spec.DeltaOf!["storage.".Length..];
            Assert.True(BaselineCounters.HasDelta(name));
            Assert.Equal(BaselineCounters.GrowthVerdict(name) is not null, spec.Class == ItemClass.Judged);
            Assert.Equal(CheckItemRegistry.DeltaId(spec.DeltaOf!), spec.Id);
        }
    }

    [Fact]
    public void The_temperatures_have_no_delta_row_and_the_hours_counters_are_the_monotonic_ones()
    {
        Assert.False(BaselineCounters.HasDelta("ata.190"));
        Assert.False(BaselineCounters.HasDelta("ata.194"));
        Assert.True(BaselineCounters.HasDelta("ata.9"));

        var monotonic = BaselineCounters.DeltaCounters.Where(BaselineCounters.IsMonotonic).ToList();
        Assert.Equal(new[] { "nvme.power_on_hours", "nvme.data_units_written", "ata.9" }, monotonic);
    }

    [Fact]
    public void The_item_id_of_a_counter_is_storage_dot_the_stored_name()
    {
        Assert.Equal("storage.nvme.media_errors", BaselineCounters.ItemId("nvme.media_errors"));
        Assert.Equal("storage.ata.199", BaselineCounters.ItemId(BaselineCounters.Ata(199)));
    }
}
