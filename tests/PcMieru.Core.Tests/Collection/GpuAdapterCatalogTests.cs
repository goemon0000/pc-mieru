using PcMieru.Core.Collection;

namespace PcMieru.Core.Tests.Collection;

/// <summary>
/// The adapter list of the GPU sampler after a graphics driver restart: it is read again when PDH names a LUID the list does not have or a LUID it had
/// is gone, never more than once per ten seconds. A fake enumeration and a fake clock; nothing is real.
/// </summary>
public sealed class GpuAdapterCatalogTests
{
    private const ulong Old = 0x166CA;
    private const ulong New = 0x2A001;

    private static GpuAdapter Card(ulong luid, string name = "Test GPU", long dedicated = 8L * 1024 * 1024 * 1024) => new(luid, name, dedicated, 0x10DE, false);

    private sealed class Fixture
    {
        public IReadOnlyList<GpuAdapter> Adapters { get; set; } = [Card(Old)];

        public long Ms { get; set; } = 1_000_000;

        public GpuAdapterCatalog Catalog { get; }

        public Fixture() => Catalog = new GpuAdapterCatalog(() => Adapters, () => Ms);
    }

    [Fact]
    public void The_list_is_read_on_first_use_and_only_then()
    {
        var f = new Fixture();
        Assert.Equal(0, f.Catalog.Enumerations);

        Assert.Equal(Old, Assert.Single(f.Catalog.Adapters).Luid);
        Assert.Equal(Old, Assert.Single(f.Catalog.Adapters).Luid);

        Assert.Equal(1, f.Catalog.Enumerations);
    }

    [Fact]
    public void A_sample_whose_luids_are_all_known_reads_nothing_again()
    {
        var f = new Fixture();
        _ = f.Catalog.Adapters;
        f.Ms += 60_000;

        Assert.False(f.Catalog.Observe([Old]));
        f.Ms += 60_000;
        Assert.False(f.Catalog.Observe([Old]));

        Assert.Equal(1, f.Catalog.Enumerations);
    }

    [Fact]
    public void A_luid_pdh_reports_that_the_list_does_not_have_reads_the_list_again()
    {
        var f = new Fixture();
        _ = f.Catalog.Adapters;
        f.Adapters = [Card(New)]; // the driver restarted: the same card under a new LUID
        f.Ms += 11_000;

        Assert.True(f.Catalog.Observe([New]));

        Assert.Equal(2, f.Catalog.Enumerations);
        Assert.Equal(New, Assert.Single(f.Catalog.Adapters).Luid);
        Assert.False(f.Catalog.Observe([New]));
        Assert.Equal(2, f.Catalog.Enumerations);
    }

    [Fact]
    public void A_luid_that_was_reported_and_is_gone_reads_the_list_again()
    {
        var f = new Fixture();
        _ = f.Catalog.Adapters;
        Assert.False(f.Catalog.Observe([Old]));
        f.Adapters = [Card(New)];
        f.Ms += 11_000;

        Assert.True(f.Catalog.Observe([])); // the card has vanished for a moment

        Assert.Equal(2, f.Catalog.Enumerations);
        Assert.Equal(New, Assert.Single(f.Catalog.Adapters).Luid);
    }

    [Fact]
    public void The_list_is_read_at_most_once_per_ten_seconds_and_the_asking_luid_is_tried_again_after_that()
    {
        var f = new Fixture();
        _ = f.Catalog.Adapters;
        f.Adapters = [Card(New)];

        f.Ms += 9_000;
        Assert.False(f.Catalog.Observe([New])); // too soon after the first read
        f.Ms += 500;
        Assert.False(f.Catalog.Observe([New]));
        Assert.Equal(1, f.Catalog.Enumerations);

        f.Ms += 1_000; // 10.5 s since the read
        Assert.True(f.Catalog.Observe([New]));
        Assert.Equal(2, f.Catalog.Enumerations);
    }

    [Fact]
    public void A_vanished_luid_that_could_not_be_read_for_the_interval_is_still_looked_at_when_the_interval_is_over()
    {
        var f = new Fixture();
        _ = f.Catalog.Adapters;
        Assert.False(f.Catalog.Observe([Old]));
        f.Adapters = [Card(New)];

        f.Ms += 2_000;
        Assert.False(f.Catalog.Observe([])); // gone, but a read is too soon
        f.Ms += 9_000;
        Assert.True(f.Catalog.Observe([New])); // the interval is over: the pending read happens

        Assert.Equal(2, f.Catalog.Enumerations);
        Assert.Equal(New, Assert.Single(f.Catalog.Adapters).Luid);
    }

    [Fact]
    public void An_adapter_only_pdh_knows_is_asked_for_once_and_not_every_ten_seconds()
    {
        var f = new Fixture();
        _ = f.Catalog.Adapters;
        const ulong Ghost = 0xDEAD;

        f.Ms += 11_000;
        Assert.True(f.Catalog.Observe([Old, Ghost])); // DXGI does not have it either
        for (var i = 0; i < 5; i++)
        {
            f.Ms += 11_000;
            Assert.False(f.Catalog.Observe([Old, Ghost]));
        }

        Assert.Equal(2, f.Catalog.Enumerations);
    }

    [Fact]
    public void A_read_that_finds_no_adapter_does_not_wipe_the_list_that_had_one()
    {
        var f = new Fixture();
        _ = f.Catalog.Adapters;
        f.Adapters = [];
        f.Ms += 11_000;

        Assert.True(f.Catalog.Observe([New]));

        Assert.Equal(Old, Assert.Single(f.Catalog.Adapters).Luid);
    }
}
