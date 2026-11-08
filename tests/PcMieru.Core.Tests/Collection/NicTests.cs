using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using PcMieru.Core.Collection;

namespace PcMieru.Core.Tests.Collection;

internal static class NicFixtures
{
    public const byte Hardware = 0x01;
    public const byte Filter = 0x02;
    public const uint Ethernet = 6;
    public const ulong Gigabit = 1_000_000_000;

    public static NicRow Row(
        ulong luid,
        string alias = "Ethernet",
        uint type = Ethernet,
        uint oper = 1,
        byte flags = Hardware,
        ulong tx = Gigabit,
        ulong rx = Gigabit,
        ulong inOctets = 0,
        ulong outOctets = 0,
        string description = "Intel(R) Ethernet Controller") =>
        new(luid, new Guid((int)luid, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), alias, description, type, oper, flags, tx, rx, inOctets, outOctets);

    public static byte[] RowBytes(in NicRow row)
    {
        var bytes = new byte[NicRowParser.RowSize];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0), row.Luid);
        Assert.True(row.Guid.TryWriteBytes(bytes.AsSpan(12)));
        Encoding.Unicode.GetBytes(row.Alias).CopyTo(bytes, 28);
        Encoding.Unicode.GetBytes(row.Description).CopyTo(bytes, 542);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1128), row.Type);
        bytes[1152] = row.Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1156), row.OperStatus);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1192), row.TransmitLinkSpeed);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1200), row.ReceiveLinkSpeed);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1208), row.InOctets);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1280), row.OutOctets);
        return bytes;
    }

    public static byte[] Table(uint claimedCount, params NicRow[] rows)
    {
        var table = new byte[NicRowParser.TableHeaderSize + (rows.Length * NicRowParser.RowSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(table, claimedCount);
        for (var i = 0; i < rows.Length; i++)
        {
            RowBytes(rows[i]).CopyTo(table, NicRowParser.TableHeaderSize + (i * NicRowParser.RowSize));
        }

        return table;
    }
}

public sealed class NicRowParserTests
{
    [Fact]
    public void A_row_is_read_at_the_netioapi_offsets()
    {
        var expected = NicFixtures.Row(
            luid: 0x0006_0000_0001_0000,
            alias: "イーサネット 2",
            type: 6,
            oper: 1,
            flags: 0x05,
            tx: 2_500_000_000,
            rx: 2_400_000_000,
            inOctets: 123_456_789_012,
            outOctets: 98_765_432_101,
            description: "Realtek Gaming 2.5GbE Family Controller");

        var parsed = NicRowParser.Parse(NicFixtures.RowBytes(expected));

        Assert.Equal(expected, parsed);
        Assert.True(parsed.IsHardware);
        Assert.False(parsed.IsFilter);
        Assert.True(parsed.IsUp);
    }

    [Fact]
    public void An_alias_that_fills_its_whole_field_with_no_terminator_stays_inside_the_field()
    {
        var bytes = NicFixtures.RowBytes(NicFixtures.Row(1));
        for (var i = 0; i < 257; i++)
        {
            bytes[28 + (i * 2)] = (byte)'A';
            bytes[29 + (i * 2)] = 0;
        }

        var row = NicRowParser.Parse(bytes);

        Assert.Equal(257, row.Alias.Length);
        Assert.Equal("Intel(R) Ethernet Controller", row.Description); // the next field is not swallowed
    }

    [Fact]
    public void Rows_reads_the_count_then_each_row()
    {
        var table = NicFixtures.Table(2, NicFixtures.Row(1, "A"), NicFixtures.Row(2, "B"));
        var into = new List<NicRow>();

        var count = NicRowParser.Rows(table, into);

        Assert.Equal(2, count);
        Assert.Equal(new[] { "A", "B" }, into.Select(r => r.Alias).ToArray());
    }

    [Fact]
    public void A_count_larger_than_the_buffer_gives_only_the_rows_that_fit()
    {
        var table = NicFixtures.Table(uint.MaxValue, NicFixtures.Row(1, "A"), NicFixtures.Row(2, "B"));
        var into = new List<NicRow>();

        Assert.Equal(2, NicRowParser.Rows(table, into));
        Assert.Equal(2, into.Count);
    }

    [Fact]
    public void A_partial_last_row_is_not_read_and_a_short_header_is_no_rows()
    {
        var table = NicFixtures.Table(2, NicFixtures.Row(1, "A"), NicFixtures.Row(2, "B"));
        var into = new List<NicRow> { NicFixtures.Row(99) };

        Assert.Equal(1, NicRowParser.Rows(table.AsSpan(0, table.Length - 1), into));
        Assert.Equal("A", Assert.Single(into).Alias);
        Assert.Equal(0, NicRowParser.Rows(table.AsSpan(0, 7), into));
        Assert.Empty(into); // the list is cleared even when nothing was read
        Assert.Equal(0, NicRowParser.Rows([], into));
    }

    [Theory]
    // flags, type, oper, in, out, shown
    [InlineData(NicFixtures.Hardware, 6u, 1u, 0UL, 0UL, true)] // real adapter, up
    [InlineData(NicFixtures.Hardware, 6u, 2u, 0UL, 0UL, true)] // real adapter, cable out: still a row the user wants to see
    [InlineData(NicFixtures.Hardware, 71u, 1u, 5UL, 5UL, true)] // Wi-Fi
    [InlineData(NicFixtures.Hardware, 6u, 6u, 0UL, 0UL, false)] // removed hardware (ghost)
    [InlineData((byte)(NicFixtures.Hardware | NicFixtures.Filter), 6u, 1u, 9UL, 9UL, false)] // a filter layer repeats the adapter's counters
    [InlineData(NicFixtures.Filter, 6u, 1u, 9UL, 9UL, false)]
    [InlineData((byte)0, 24u, 1u, 9UL, 9UL, false)] // loopback
    [InlineData((byte)0, 131u, 1u, 9UL, 9UL, false)] // tunnel
    [InlineData((byte)0, 6u, 1u, 0UL, 0UL, false)] // a virtual adapter nobody used is a row of zeros
    [InlineData((byte)0, 6u, 1u, 1UL, 0UL, true)] // ... until it carries a byte in
    [InlineData((byte)0, 6u, 1u, 0UL, 1UL, true)] // ... or out
    public void Which_rows_are_shown(byte flags, uint type, uint oper, ulong inOctets, ulong outOctets, bool shown)
    {
        var row = NicFixtures.Row(1, type: type, oper: oper, flags: flags, inOctets: inOctets, outOctets: outOctets);

        Assert.Equal(shown, NicRowParser.IsShown(row));
    }
}

public sealed class NicSamplerTests
{
    private static readonly long Second = Stopwatch.Frequency;

    private static NicInfo Only(IReadOnlyList<NicInfo> nics) => Assert.Single(nics);

    [Fact]
    public void The_first_sample_has_no_rates_and_says_why()
    {
        var nic = new NicSampler();

        var info = Only(nic.Build([NicFixtures.Row(1, inOctets: 100, outOctets: 50)], 1000));

        Assert.Equal(ValueStatus.NotCollected, info.SendBytesPerSec.Status);
        Assert.Equal(ValueStatus.NotCollected, info.ReceiveBytesPerSec.Status);
        Assert.Contains("first sample", info.SendBytesPerSec.Detail);
        Assert.True(info.IsUp);
        Assert.Equal("Ethernet", info.Name);
    }

    [Fact]
    public void The_rate_is_the_octet_delta_over_the_real_elapsed_time()
    {
        var nic = new NicSampler();
        _ = nic.Build([NicFixtures.Row(1, inOctets: 1_000, outOctets: 400)], 1000);

        var info = Only(nic.Build([NicFixtures.Row(1, inOctets: 1_000 + 2_000_000, outOctets: 400 + 500_000)], 1000 + (2 * Second)));

        Assert.True(info.ReceiveBytesPerSec.IsOk);
        Assert.InRange(info.ReceiveBytesPerSec.Data, 999_900, 1_000_100);
        Assert.InRange(info.SendBytesPerSec.Data, 249_900, 250_100);
    }

    [Fact]
    public void An_idle_adapter_is_zero_bytes_per_second_not_a_gap()
    {
        var nic = new NicSampler();
        _ = nic.Build([NicFixtures.Row(1, inOctets: 5, outOctets: 5)], 1000);

        var info = Only(nic.Build([NicFixtures.Row(1, inOctets: 5, outOctets: 5)], 1000 + Second));

        Assert.True(info.ReceiveBytesPerSec.IsOk);
        Assert.Equal(0, info.ReceiveBytesPerSec.Data);
        Assert.Equal(0, info.SendBytesPerSec.Data);
    }

    [Fact]
    public void A_counter_that_goes_backwards_is_a_gap_and_the_next_tick_recovers()
    {
        var nic = new NicSampler();
        _ = nic.Build([NicFixtures.Row(1, inOctets: 9_000, outOctets: 9_000)], 1000);

        var reset = Only(nic.Build([NicFixtures.Row(1, inOctets: 10, outOctets: 20)], 1000 + Second));
        var recovered = Only(nic.Build([NicFixtures.Row(1, inOctets: 1_010, outOctets: 20)], 1000 + (2 * Second)));

        Assert.Equal(ValueStatus.NotCollected, reset.ReceiveBytesPerSec.Status);
        Assert.Contains("backwards", reset.ReceiveBytesPerSec.Detail);
        Assert.Equal(ValueStatus.NotCollected, reset.SendBytesPerSec.Status);
        Assert.InRange(recovered.ReceiveBytesPerSec.Data, 990, 1010);
        Assert.Equal(0, recovered.SendBytesPerSec.Data);
    }

    [Fact]
    public void No_time_between_two_reads_is_a_gap_not_a_division_by_zero()
    {
        var nic = new NicSampler();
        _ = nic.Build([NicFixtures.Row(1)], 5000);

        var info = Only(nic.Build([NicFixtures.Row(1, inOctets: 10)], 5000));

        Assert.Equal(ValueStatus.NotCollected, info.ReceiveBytesPerSec.Status);
        Assert.Contains("no time", info.ReceiveBytesPerSec.Detail);
    }

    [Fact]
    public void An_adapter_that_appears_later_has_no_rate_until_the_tick_after()
    {
        var nic = new NicSampler();
        _ = nic.Build([NicFixtures.Row(1)], 1000);

        var both = nic.Build([NicFixtures.Row(1), NicFixtures.Row(2, "Wi-Fi", inOctets: 100)], 1000 + Second);

        Assert.Equal(2, both.Count);
        var added = both.Single(n => n.Name == "Wi-Fi");
        Assert.Equal(ValueStatus.NotCollected, added.ReceiveBytesPerSec.Status);
        Assert.Contains("new since", added.ReceiveBytesPerSec.Detail);
        Assert.True(both.Single(n => n.Name == "Ethernet").ReceiveBytesPerSec.IsOk);

        var later = nic.Build([NicFixtures.Row(1), NicFixtures.Row(2, "Wi-Fi", inOctets: 1_100)], 1000 + (2 * Second));
        Assert.InRange(later.Single(n => n.Name == "Wi-Fi").ReceiveBytesPerSec.Data, 990, 1010);
    }

    [Fact]
    public void A_removed_adapter_is_dropped_and_when_it_returns_it_is_new_again_not_a_rate_over_the_gap()
    {
        var nic = new NicSampler();
        _ = nic.Build([NicFixtures.Row(1), NicFixtures.Row(2, "USB", inOctets: 10)], 1000);

        var gone = nic.Build([NicFixtures.Row(1)], 1000 + Second);
        Assert.Equal("Ethernet", Only(gone).Name);

        var back = nic.Build([NicFixtures.Row(1), NicFixtures.Row(2, "USB", inOctets: 99_999_999)], 1000 + (60 * Second));
        Assert.Equal(ValueStatus.NotCollected, back.Single(n => n.Name == "USB").ReceiveBytesPerSec.Status);
    }

    [Fact]
    public void Reset_drops_the_baseline()
    {
        var nic = new NicSampler();
        _ = nic.Build([NicFixtures.Row(1)], 1000);
        Assert.True(Only(nic.Build([NicFixtures.Row(1)], 1000 + Second)).ReceiveBytesPerSec.IsOk);

        nic.Reset();
        var info = Only(nic.Build([NicFixtures.Row(1)], 1000 + (2 * Second)));

        Assert.Equal(ValueStatus.NotCollected, info.ReceiveBytesPerSec.Status);
        Assert.Contains("first sample", info.ReceiveBytesPerSec.Detail);
    }

    [Fact]
    public void Filter_layers_ghosts_and_loopback_are_not_in_the_result_and_the_rest_is_sorted_by_id()
    {
        var nic = new NicSampler();
        var rows = new[]
        {
            NicFixtures.Row(3, "Third"),
            NicFixtures.Row(1, "First"),
            NicFixtures.Row(2, "Second-QoS", flags: NicFixtures.Hardware | NicFixtures.Filter),
            NicFixtures.Row(4, "Lo", type: 24, flags: 0, inOctets: 5),
            NicFixtures.Row(5, "Ghost", oper: 6),
        };

        var result = nic.Build(rows, 1000);

        Assert.Equal(new[] { "First", "Third" }, result.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void The_id_is_the_adapter_guid_and_the_alias_falls_back_to_the_description()
    {
        var nic = new NicSampler();
        var row = NicFixtures.Row(0x1234, alias: string.Empty, description: "Hyper-V Virtual Ethernet Adapter");

        var info = Only(nic.Build([row], 1000));

        Assert.Equal(row.Guid.ToString("D"), info.Id);
        Assert.Equal("Hyper-V Virtual Ethernet Adapter", info.Name);
    }

    [Fact]
    public void Link_speed_is_the_transmit_speed_or_the_receive_speed_when_transmit_is_not_reported()
    {
        var nic = new NicSampler();

        Assert.Equal(2_500_000_000, Only(nic.Build([NicFixtures.Row(1, tx: 2_500_000_000, rx: 1)], 1000)).LinkSpeedBitsPerSec.Data);
        Assert.Equal(1_000_000_000, Only(nic.Build([NicFixtures.Row(1, tx: ulong.MaxValue, rx: 1_000_000_000)], 2000)).LinkSpeedBitsPerSec.Data);
        Assert.Equal(300_000_000, Only(nic.Build([NicFixtures.Row(1, tx: 0, rx: 300_000_000)], 3000)).LinkSpeedBitsPerSec.Data);
    }

    [Theory]
    [InlineData(0UL, 0UL, 1u, "not reported")]
    [InlineData(ulong.MaxValue, ulong.MaxValue, 1u, "not reported")]
    [InlineData(NicFixtures.Gigabit, NicFixtures.Gigabit, 2u, "down")]
    public void A_link_speed_that_is_not_known_or_not_meaningful_is_a_gap_with_a_reason_never_a_number(ulong tx, ulong rx, uint oper, string reason)
    {
        var nic = new NicSampler();

        var info = Only(nic.Build([NicFixtures.Row(1, tx: tx, rx: rx, oper: oper)], 1000));

        Assert.Equal(ValueStatus.NotCollected, info.LinkSpeedBitsPerSec.Status);
        Assert.Contains(reason, info.LinkSpeedBitsPerSec.Detail);
        Assert.Equal(oper == 1, info.IsUp);
    }

    [Fact]
    public void The_real_table_can_be_read_and_every_row_it_shows_has_a_name_and_a_unique_id()
    {
        var nic = new NicSampler();
        _ = nic.Sample();
        Thread.Sleep(300);

        var nics = nic.Sample();

        Assert.Equal(nics.Count, nics.Select(n => n.Id).Distinct().Count());
        Assert.All(nics, n =>
        {
            Assert.False(string.IsNullOrWhiteSpace(n.Name));

            // A virtual adapter that carried its first byte between the two reads is legitimately "new since the previous sample".
            var newlyShown = n.SendBytesPerSec.Detail?.Contains("new since", StringComparison.Ordinal) == true;
            Assert.True(newlyShown || (n.SendBytesPerSec.IsOk && n.SendBytesPerSec.Data >= 0), $"{n.Name}: {n.SendBytesPerSec.Status} {n.SendBytesPerSec.Detail}");
            Assert.True(newlyShown || (n.ReceiveBytesPerSec.IsOk && n.ReceiveBytesPerSec.Data >= 0), $"{n.Name}: {n.ReceiveBytesPerSec.Status} {n.ReceiveBytesPerSec.Detail}");
        });
    }
}
