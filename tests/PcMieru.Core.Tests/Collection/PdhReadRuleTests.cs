using System.Buffers.Binary;
using System.Text;
using PcMieru.Core.Collection;

namespace PcMieru.Core.Tests.Collection;

/// <summary>
/// How a PDH counter array is read (the rules the always-on sampler and the CPU check's own query share): the effective clock from the two
/// values of one collection, the first valid item, and the package power of the Energy Meter. Pure functions over the bytes of one answer:
/// no PDH, no counter, no machine.
/// </summary>
public sealed class PdhReadRuleTests
{
    internal const ulong Base = 0x0000_7FF6_1234_0000;

    private const uint InvalidData = 0xC0000BBA; // PDH_CSTATUS_INVALID_DATA

    /// <summary>The layout PdhGetFormattedCounterArrayW writes: 24-byte items first, then the name strings, pointers absolute.</summary>
    internal static byte[] Build(params (string Name, double Value, uint CStatus)[] items)
    {
        var namesStart = items.Length * PdhArray.ItemSize;
        var names = new MemoryStream();
        var pointers = new ulong[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            pointers[i] = Base + (ulong)(namesStart + names.Length);
            names.Write(Encoding.Unicode.GetBytes(items[i].Name + "\0"));
        }

        var buffer = new byte[namesStart + (int)names.Length];
        for (var i = 0; i < items.Length; i++)
        {
            var item = buffer.AsSpan(i * PdhArray.ItemSize, PdhArray.ItemSize);
            BinaryPrimitives.WriteUInt64LittleEndian(item, pointers[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(item[8..], items[i].CStatus);
            BinaryPrimitives.WriteInt64LittleEndian(item[16..], BitConverter.DoubleToInt64Bits(items[i].Value));
        }

        names.ToArray().CopyTo(buffer, namesStart);
        return buffer;
    }

    internal static PackagePowerReading Power(params (string Name, double Value, uint CStatus)[] items) =>
        PdhReadRule.PackagePower(Build(items), Base, items.Length);

    internal static (string, double, uint) Valid(string name, double value) => (name, value, Pdh.CStatusValidData);

    // ---- the effective clock ----

    [Theory]
    [InlineData(3500, 100, 3500)]
    [InlineData(3500, 80, 2800)]
    [InlineData(3000, 133, 3990)]
    [InlineData(4200, 50.5, 2121)]
    public void The_effective_clock_is_the_nominal_frequency_scaled_by_the_performance(double mhz, double percent, double expected)
    {
        Assert.Equal(expected, PdhReadRule.EffectiveMhz(mhz, percent)!.Value, 9);
    }

    [Theory]
    [InlineData(3500, 0)]
    [InlineData(0, 100)]
    [InlineData(0, 0)]
    [InlineData(-3500, 100)]
    [InlineData(3500, -10)]
    [InlineData(double.NaN, 100)]
    [InlineData(3500, double.NaN)]
    [InlineData(double.PositiveInfinity, 100)]
    [InlineData(3500, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 100)]
    public void A_clock_that_is_not_a_positive_finite_number_is_missing_and_never_zero_mhz(double mhz, double percent)
    {
        Assert.Null(PdhReadRule.EffectiveMhz(mhz, percent));
    }

    // ---- the first valid item ----

    [Fact]
    public void The_first_item_with_data_is_taken()
    {
        var buffer = Build(("0,_Total", 12.5, Pdh.CStatusValidData), ("1,_Total", 99, Pdh.CStatusValidData));

        Assert.True(PdhReadRule.TryPickFirstValid(buffer, Base, 2, out var value, out _));
        Assert.Equal(12.5, value);
    }

    [Fact]
    public void An_item_with_new_data_counts_and_an_item_without_data_is_passed_over()
    {
        var buffer = Build(("a", 1, InvalidData), ("b", 2, Pdh.CStatusNewData), ("c", 3, Pdh.CStatusValidData));

        Assert.True(PdhReadRule.TryPickFirstValid(buffer, Base, 3, out var value, out _));
        Assert.Equal(2, value);
    }

    [Fact]
    public void No_item_with_data_is_false_and_says_the_status_of_the_last_one_tried()
    {
        var buffer = Build(("a", 1, InvalidData), ("b", 2, 0x800007D5));

        Assert.False(PdhReadRule.TryPickFirstValid(buffer, Base, 2, out var value, out var why));
        Assert.Equal(0, value);
        Assert.Contains("800007D5", why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_item_at_all_is_false_and_the_value_is_not_a_reading()
    {
        Assert.False(PdhReadRule.TryPickFirstValid([], Base, 0, out var value, out var why));
        Assert.Equal(0, value);
        Assert.False(string.IsNullOrEmpty(why));
    }

    // ---- the package power ----

    [Fact]
    public void A_package_instance_in_milliwatts_is_watts()
    {
        var reading = Power(Valid("RAPL_Package0_PKG", 65_000));

        Assert.Equal(PackagePowerState.Valid, reading.State);
        Assert.Equal(65.0, reading.Watts, 9);
    }

    [Fact]
    public void Only_the_instances_that_end_in_pkg_are_the_package()
    {
        // The DRAM and the cores are parts of the package or something else: counting them would count the same watts twice.
        var reading = Power(
            Valid("RAPL_Package0_PP0", 20_000),
            Valid("RAPL_Package0_PKG", 65_000),
            Valid("RAPL_Package0_DRAM", 5_000),
            Valid("RAPL_Package0_PKG_extra", 9_000),
            Valid("PKG_first", 7_000));

        Assert.Equal(PackagePowerState.Valid, reading.State);
        Assert.Equal(65.0, reading.Watts, 9);
    }

    [Fact]
    public void The_suffix_is_compared_without_regard_to_case()
    {
        var reading = Power(Valid("rapl_package0_pkg", 40_000));

        Assert.Equal(PackagePowerState.Valid, reading.State);
        Assert.Equal(40.0, reading.Watts, 9);
    }

    [Fact]
    public void The_packages_of_a_machine_with_two_sockets_are_summed()
    {
        var reading = Power(Valid("RAPL_Package0_PKG", 30_000), Valid("RAPL_Package1_PKG", 35_000));

        Assert.Equal(PackagePowerState.Valid, reading.State);
        Assert.Equal(65.0, reading.Watts, 9);
    }

    [Theory]
    [InlineData(500, "Valid")]
    [InlineData(499.9, "NoValidSample")]
    [InlineData(1_500_000, "Valid")]
    [InlineData(1_500_001, "NoValidSample")]
    [InlineData(0, "NoValidSample")]
    public void A_package_power_outside_half_a_watt_to_fifteen_hundred_is_not_a_reading(double milliwatts, string expectedName)
    {
        var reading = Power(Valid("RAPL_Package0_PKG", milliwatts));
        var expected = Enum.Parse<PackagePowerState>(expectedName);

        Assert.Equal(expected, reading.State);
        if (expected != PackagePowerState.Valid)
        {
            Assert.Equal(0, reading.Watts);
        }
    }

    [Fact]
    public void One_package_that_cannot_be_read_spoils_the_sum_of_a_two_socket_machine()
    {
        // Half of a total is a wrong number that looks right: nothing is reported.
        var reading = Power(Valid("RAPL_Package0_PKG", 30_000), ("RAPL_Package1_PKG", 35_000, InvalidData));

        Assert.Equal(PackagePowerState.NoValidSample, reading.State);
        Assert.Equal(0, reading.Watts);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-5)]
    public void A_package_value_that_is_not_a_finite_non_negative_number_is_not_a_reading(double value)
    {
        var reading = Power(Valid("RAPL_Package0_PKG", value));

        Assert.Equal(PackagePowerState.NoValidSample, reading.State);
    }

    [Fact]
    public void A_package_instance_with_an_invalid_status_is_not_a_reading()
    {
        var reading = Power(("RAPL_Package0_PKG", 65_000, InvalidData));

        Assert.Equal(PackagePowerState.NoValidSample, reading.State);
    }

    [Fact]
    public void An_item_with_new_data_is_a_reading()
    {
        var reading = Power(("RAPL_Package0_PKG", 65_000, Pdh.CStatusNewData));

        Assert.Equal(PackagePowerState.Valid, reading.State);
    }

    [Fact]
    public void A_counter_with_no_package_instance_says_so_and_does_not_guess_from_the_others()
    {
        var reading = Power(Valid("RAPL_Package0_DRAM", 5_000), Valid("RAPL_Package0_PP0", 20_000));

        Assert.Equal(PackagePowerState.NoPackageInstance, reading.State);
        Assert.Equal(0, reading.Watts);
    }

    [Fact]
    public void An_empty_answer_has_no_package_instance()
    {
        Assert.Equal(PackagePowerState.NoPackageInstance, PdhReadRule.PackagePower([], Base, 0).State);
    }

    [Fact]
    public void A_count_that_does_not_fit_the_buffer_has_no_package_instance_and_reads_nothing_outside()
    {
        var buffer = Build(Valid("RAPL_Package0_PKG", 65_000));

        Assert.Equal(PackagePowerState.NoPackageInstance, PdhReadRule.PackagePower(buffer, Base, 1000).State);
    }
}
