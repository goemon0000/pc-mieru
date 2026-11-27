using PcMieru.Core;
using PcMieru.Party.Hardware;

namespace PcMieru.Party.Core.Tests;

/// <summary>The old Parts Life disk slots, kept: the keys the saved game knows and the scale each kind is full at.</summary>
public class DiskSlotsTests
{
    private const double MiB = 1024.0 * 1024.0;

    [Fact]
    public void The_old_slot_keys_and_scales_are_kept()
    {
        Assert.Equal(("m2", 2000 * MiB), (DiskSlots.Nvme.Key, DiskSlots.Nvme.FullScaleBytesPerSec));
        Assert.Equal(("ssd", 500 * MiB), (DiskSlots.Ssd.Key, DiskSlots.Ssd.FullScaleBytesPerSec));
        Assert.Equal(("hdd", 120 * MiB), (DiskSlots.Hdd.Key, DiskSlots.Hdd.FullScaleBytesPerSec));
        Assert.Equal(("ssd", 500 * MiB), (DiskSlots.Plain.Key, DiskSlots.Plain.FullScaleBytesPerSec));
        Assert.Equal("DISK", DiskSlots.Plain.Title);
        Assert.Equal(200 * MiB, DiskSlots.WorkScaleBytesPerSec);
    }

    [Fact]
    public void Each_kind_has_its_slot_and_an_unknown_kind_has_none()
    {
        Assert.Same(DiskSlots.Nvme, DiskSlots.For(DiskKind.Nvme));
        Assert.Same(DiskSlots.Ssd, DiskSlots.For(DiskKind.Ssd));
        Assert.Same(DiskSlots.Hdd, DiskSlots.For(DiskKind.Hdd));
        Assert.Null(DiskSlots.For(DiskKind.Unknown));
    }

    [Fact]
    public void A_slots_load_is_its_share_of_the_scale_clamped()
    {
        Assert.Equal(0.5, DiskSlots.Hdd.Load(60 * MiB)!.Value, 9);
        Assert.Equal(1, DiskSlots.Hdd.Load(10_000 * MiB));
        Assert.Equal(0, DiskSlots.Hdd.Load(0));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-0.0001)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_rate_that_is_not_a_rate_is_missing_never_zero(double bytesPerSec) =>
        Assert.Null(DiskSlots.Hdd.Load(bytesPerSec));
}
