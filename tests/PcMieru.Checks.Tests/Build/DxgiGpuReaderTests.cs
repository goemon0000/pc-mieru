// New in this repository. The walk itself needs a GPU driver and is tried by Pcb1RealMachineTests; here are the parts that need no hardware:
// the layout of DXGI_ADAPTER_DESC1 (the offsets of the old reader), the removal of aliases, and the lane a walk runs on.
using System.Text;
using PcMieru.Checks.Build;
using PcMieru.Checks.SystemInfo;
using PcMieru.Checks.Tests.SystemInfo;

namespace PcMieru.Checks.Tests.Build;

public class DxgiGpuReaderTests
{
    private static byte[] Desc(string name, uint vendor, ulong dedicated, uint luidLow, int luidHigh, uint flags)
    {
        var d = new byte[312];
        var chars = Encoding.Unicode.GetBytes(name);
        Assert.True(chars.Length <= 254);
        chars.CopyTo(d, 0);
        BitConverter.GetBytes(vendor).CopyTo(d, 256);
        BitConverter.GetBytes(0x2F04u).CopyTo(d, 260); // DeviceId: not read
        BitConverter.GetBytes(dedicated).CopyTo(d, 272);
        BitConverter.GetBytes(0x1111UL).CopyTo(d, 280); // DedicatedSystemMemory: not read
        BitConverter.GetBytes(0x2222UL).CopyTo(d, 288); // SharedSystemMemory: not read
        BitConverter.GetBytes(luidLow).CopyTo(d, 296);
        BitConverter.GetBytes(luidHigh).CopyTo(d, 300);
        BitConverter.GetBytes(flags).CopyTo(d, 304);
        return d;
    }

    [Fact]
    public void The_description_is_read_at_the_offsets_of_dxgi_adapter_desc1()
    {
        var d = DxgiGpuReader.ParseDesc1(Desc("  NVIDIA GeForce RTX 5070  ", 0x10DE, 12_817_793_024UL, 0xABCD, -2, 0));
        Assert.Equal("NVIDIA GeForce RTX 5070", d.Name);
        Assert.Equal(0x10DEu, d.VendorId);
        Assert.Equal(12_817_793_024UL, d.Dedicated); // above 4 GiB: not capped
        Assert.Equal(0xABCDu, d.LuidLow);
        Assert.Equal(-2, d.LuidHigh);
        Assert.Equal(0u, d.Flags);
    }

    [Fact]
    public void The_name_ends_at_the_first_nul_and_a_full_buffer_without_nul_is_all_the_name()
    {
        Assert.Equal("Intel(R) Arc(TM) A770 Graphics", DxgiGpuReader.ParseDesc1(Desc("Intel(R) Arc(TM) A770 Graphics", 0x8086, 1, 1, 0, 0)).Name);

        var full = new byte[312];
        Encoding.Unicode.GetBytes(new string('G', 128)).CopyTo(full, 0);
        Assert.Equal(new string('G', 128), DxgiGpuReader.ParseDesc1(full).Name);
    }

    [Fact]
    public void The_software_flag_is_bit_two_and_is_what_the_walk_skips()
    {
        Assert.Equal(2, DxgiGpuReader.SoftwareFlag);
        var d = DxgiGpuReader.ParseDesc1(Desc("Microsoft Basic Render Driver", 0x1414, 0, 0x1234, 0, 2));
        Assert.NotEqual(0u, d.Flags & DxgiGpuReader.SoftwareFlag);
    }

    private static DxgiAdapter Adapter(string name, ulong dedicated, bool pci, uint luid = 1) =>
        new(name, 0x10DE, dedicated, luid, 0, pci ? 1u : null, pci ? 0u : null);

    [Fact]
    public void An_adapter_without_a_pci_address_that_repeats_a_card_is_an_alias_and_is_removed()
    {
        var list = new List<DxgiAdapter>
        {
            Adapter("NVIDIA GeForce RTX 5070", 12_817_793_024UL, true, 1),
            Adapter("NVIDIA GeForce RTX 5070", 12_817_793_024UL, false, 2), // the same card again, from a virtual display driver
            Adapter("", 12_817_793_024UL, false, 3), // D3DKMT gives an alias no name
            Adapter("Meta Virtual Monitor", 0, false, 4), // not an alias of anything: stays (BuildRules drops it by its name)
        };
        var kept = DxgiGpuReader.Dedupe(list);
        Assert.Equal(new uint[] { 1, 4 }, kept.Select(a => a.LuidLow).ToArray());
    }

    [Fact]
    public void Two_cards_of_the_same_model_that_both_have_an_address_are_both_kept()
    {
        var list = new List<DxgiAdapter>
        {
            Adapter("NVIDIA GeForce RTX 3080 Ti", 12_621_709_312UL, true, 1),
            Adapter("NVIDIA GeForce RTX 3080 Ti", 12_621_709_312UL, true, 2),
        };
        Assert.Equal(2, DxgiGpuReader.Dedupe(list).Count);
    }

    [Fact]
    public void The_alias_rule_needs_the_same_memory_and_the_same_name_or_an_empty_one()
    {
        var card = Adapter("NVIDIA GeForce RTX 5070", 12_817_793_024UL, true, 1);
        Assert.Equal(2, DxgiGpuReader.Dedupe([card, Adapter("NVIDIA GeForce RTX 5070", 8_000_000_000UL, false, 2)]).Count); // other memory
        Assert.Equal(2, DxgiGpuReader.Dedupe([card, Adapter("NVIDIA GeForce RTX 4060", 12_817_793_024UL, false, 2)]).Count); // other name
        Assert.Single(DxgiGpuReader.Dedupe([card, Adapter("", 12_817_793_024UL, false, 2)]));
    }

    [Fact]
    public async Task A_deadline_that_is_gone_is_a_timeout_and_no_walk_is_started()
    {
        var clock = new ManualTimeProvider();
        var deadline = QueryTest.Deadline(clock, 5);
        clock.Advance(TimeSpan.FromSeconds(6));
        var reading = await QueryTest.Done(new DxgiGpuReader().ReadAsync(deadline, CancellationToken.None));
        Assert.False(reading.HasValue);
        Assert.Equal(CheckReason.Timeout, reading.Reason);
    }

    [Fact]
    public async Task A_cancelled_token_is_a_stop_and_no_walk_is_started()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var reading = await QueryTest.Done(new DxgiGpuReader().ReadAsync(QueryTest.Deadline(new ManualTimeProvider()), cts.Token));
        Assert.False(reading.HasValue);
        Assert.Equal(CheckReason.Partial, reading.Reason);
    }
}
