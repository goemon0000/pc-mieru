using System.Buffers.Binary;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.NetMeter;

public sealed class NetMeterProtocolTests
{
    private static NetMeterReading Sample(int entries = 3, bool healthy = true) => new(
        TimestampTicks: 123_456_789_012,
        EventsLost: 7,
        BuffersLost: 2,
        Undecodable: 5,
        SessionHealthy: healthy,
        ErrorCode: healthy ? 0u : 5u,
        Entries: [.. Enumerable.Range(1, entries).Select(i => new NetMeterEntry((uint)(1000 + i), 133_800_000_000_000_000L + i, (ulong)i * 1_000_000_000_000UL, ulong.MaxValue - (ulong)i))]);

    private static byte[] Payload(byte[] frame) => frame[NetMeterProtocol.PrefixSize..];

    [Theory]
    [InlineData(NetMeterRequestKind.Read)]
    [InlineData(NetMeterRequestKind.Stop)]
    public void A_request_round_trips_through_its_frame(NetMeterRequestKind kind)
    {
        var frame = NetMeterProtocol.EncodeRequest(kind);

        Assert.True(NetMeterProtocol.TryReadLength(frame, NetMeterProtocol.RequestPayloadSize, out var length));
        Assert.Equal(frame.Length - NetMeterProtocol.PrefixSize, length);
        Assert.True(NetMeterProtocol.TryDecodeRequest(Payload(frame), out var decoded, out var error), error);
        Assert.Equal(kind, decoded);
    }

    [Fact]
    public void The_request_bytes_are_little_endian_and_fixed()
    {
        Assert.Equal(new byte[] { 4, 0, 0, 0, 1, 0, 1, 0 }, NetMeterProtocol.EncodeRequest(NetMeterRequestKind.Read));
        Assert.Equal(new byte[] { 4, 0, 0, 0, 1, 0, 2, 0 }, NetMeterProtocol.EncodeRequest(NetMeterRequestKind.Stop));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 0, 1 })] // truncated
    [InlineData(new byte[] { 1, 0, 1, 0, 0 })] // too long
    [InlineData(new byte[] { 2, 0, 1, 0 })] // another version
    [InlineData(new byte[] { 0, 0, 1, 0 })] // version 0
    [InlineData(new byte[] { 1, 0, 0, 0 })] // no such kind
    [InlineData(new byte[] { 1, 0, 3, 0 })] // a reply kind is not a request
    [InlineData(new byte[] { 1, 0, 1, 9 })] // reserved byte set
    public void A_malformed_request_is_refused(byte[] payload)
    {
        Assert.False(NetMeterProtocol.TryDecodeRequest(payload, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(250)]
    public void A_reply_round_trips_with_every_field(int entries)
    {
        var reading = Sample(entries);
        var frame = NetMeterProtocol.EncodeReply(reading);

        Assert.True(NetMeterProtocol.TryReadLength(frame, NetMeterProtocol.MaxPayloadBytes, out var length));
        Assert.Equal(NetMeterProtocol.ReplyHeaderSize + (entries * NetMeterProtocol.EntrySize), length);
        Assert.True(NetMeterProtocol.TryDecodeReply(Payload(frame), out var decoded, out var error), error);

        Assert.NotNull(decoded);
        Assert.Equal(reading.TimestampTicks, decoded.TimestampTicks);
        Assert.Equal(reading.EventsLost, decoded.EventsLost);
        Assert.Equal(reading.BuffersLost, decoded.BuffersLost);
        Assert.Equal(reading.Undecodable, decoded.Undecodable);
        Assert.Equal(reading.SessionHealthy, decoded.SessionHealthy);
        Assert.Equal(reading.ErrorCode, decoded.ErrorCode);
        Assert.Equal(reading.Entries, decoded.Entries);
    }

    [Fact]
    public void An_unhealthy_session_and_its_error_code_survive_the_trip()
    {
        var frame = NetMeterProtocol.EncodeReply(Sample(0, healthy: false));

        Assert.True(NetMeterProtocol.TryDecodeReply(Payload(frame), out var decoded, out _));
        Assert.False(decoded!.SessionHealthy);
        Assert.Equal(5u, decoded.ErrorCode);
    }

    [Fact]
    public void Every_truncation_of_a_reply_is_refused()
    {
        var payload = Payload(NetMeterProtocol.EncodeReply(Sample(2)));

        for (var cut = 0; cut < payload.Length; cut++)
        {
            Assert.False(NetMeterProtocol.TryDecodeReply(payload.AsSpan(0, cut), out var reading, out _), $"cut at {cut}");
            Assert.Null(reading);
        }
    }

    [Fact]
    public void A_reply_with_extra_bytes_after_its_entries_is_refused()
    {
        var payload = Payload(NetMeterProtocol.EncodeReply(Sample(2))).Concat(new byte[] { 0 }).ToArray();

        Assert.False(NetMeterProtocol.TryDecodeReply(payload, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(65535)]
    public void A_reply_of_another_protocol_version_is_refused_with_a_clear_reason(ushort version)
    {
        var payload = Payload(NetMeterProtocol.EncodeReply(Sample(1)));
        BinaryPrimitives.WriteUInt16LittleEndian(payload, version);

        Assert.False(NetMeterProtocol.TryDecodeReply(payload, out _, out var error));
        Assert.Contains("version", error);
    }

    [Fact]
    public void An_unknown_kind_or_flag_in_a_reply_is_refused()
    {
        var kind = Payload(NetMeterProtocol.EncodeReply(Sample(1)));
        kind[2] = 1;
        Assert.False(NetMeterProtocol.TryDecodeReply(kind, out _, out _));

        var flags = Payload(NetMeterProtocol.EncodeReply(Sample(1)));
        flags[3] |= 0x80;
        Assert.False(NetMeterProtocol.TryDecodeReply(flags, out _, out _));
    }

    [Theory]
    [InlineData(NetMeterProtocol.MaxEntries + 1)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)] // 0xFFFFFFFF
    public void A_reply_that_claims_too_many_entries_is_refused_without_allocating_for_them(int claimed)
    {
        var payload = Payload(NetMeterProtocol.EncodeReply(Sample(1)));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(40), unchecked((uint)claimed));

        Assert.False(NetMeterProtocol.TryDecodeReply(payload, out _, out var error));
        Assert.Contains("more entries", error);
    }

    [Fact]
    public void A_count_that_does_not_match_the_length_is_refused_both_ways()
    {
        var shortByOne = Payload(NetMeterProtocol.EncodeReply(Sample(2)));
        BinaryPrimitives.WriteUInt32LittleEndian(shortByOne.AsSpan(40), 3);
        Assert.False(NetMeterProtocol.TryDecodeReply(shortByOne, out _, out _));

        var longByOne = Payload(NetMeterProtocol.EncodeReply(Sample(2)));
        BinaryPrimitives.WriteUInt32LittleEndian(longByOne.AsSpan(40), 1);
        Assert.False(NetMeterProtocol.TryDecodeReply(longByOne, out _, out _));
    }

    [Fact]
    public void The_largest_allowed_reply_round_trips_and_one_more_entry_cannot_even_be_encoded()
    {
        var full = Sample(NetMeterProtocol.MaxEntries);
        var frame = NetMeterProtocol.EncodeReply(full);

        Assert.Equal(NetMeterProtocol.PrefixSize + NetMeterProtocol.MaxPayloadBytes, frame.Length);
        Assert.True(NetMeterProtocol.TryDecodeReply(Payload(frame), out var decoded, out _));
        Assert.Equal(NetMeterProtocol.MaxEntries, decoded!.Entries.Count);
        Assert.Throws<ArgumentException>(() => NetMeterProtocol.EncodeReply(Sample(NetMeterProtocol.MaxEntries + 1)));
    }

    [Theory]
    [InlineData(0u, 100)] // an empty message
    [InlineData(101u, 100)] // above what the receiver accepts
    [InlineData(uint.MaxValue, int.MaxValue)] // above the format's own limit
    [InlineData((uint)NetMeterProtocol.MaxPayloadBytes + 1, int.MaxValue)]
    public void A_length_prefix_outside_what_the_receiver_accepts_is_refused(uint raw, int accepted)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, raw);

        Assert.False(NetMeterProtocol.TryReadLength(prefix, accepted, out var length));
        Assert.Equal(0, length);
    }

    [Fact]
    public void A_length_prefix_that_is_itself_truncated_is_refused()
    {
        Assert.False(NetMeterProtocol.TryReadLength(new byte[3], 100, out _));
    }

    [Fact]
    public void The_format_is_little_endian_byte_for_byte()
    {
        var reading = new NetMeterReading(0x0102030405060708, 0x11, 0x22, 0x33, true, 0x0A0B0C0D, [new NetMeterEntry(0x01020304, 0x1112131415161718, 0x2122232425262728, 0x3132333435363738)]);
        var payload = Payload(NetMeterProtocol.EncodeReply(reading));

        Assert.Equal(new byte[] { 1, 0, 3, 1, 0x0D, 0x0C, 0x0B, 0x0A }, payload[..8]);
        Assert.Equal(new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }, payload[8..16]);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, payload[40..44]);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, payload[44..48]);
        Assert.Equal(new byte[] { 0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11 }, payload[48..56]);
        Assert.Equal(new byte[] { 0x28, 0x27, 0x26, 0x25, 0x24, 0x23, 0x22, 0x21 }, payload[56..64]);
        Assert.Equal(new byte[] { 0x38, 0x37, 0x36, 0x35, 0x34, 0x33, 0x32, 0x31 }, payload[64..72]);
    }
}

public sealed class NetMeterArgumentsTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";

    private static string[] Good(uint pid = 4321, long created = 133_800_000_000_000_000) =>
        ["--parent", pid.ToString(), "--parent-created", created.ToString(), "--pipe", "PCMieru.NetMeter." + Token, "--user-sid", Sid];

    [Fact]
    public void The_command_line_the_front_end_builds_parses_back_to_the_same_values()
    {
        var line = NetMeterArguments.BuildCommandLine(4321, 133_800_000_000_000_000, Token, Sid);

        Assert.True(NetMeterArguments.TryParse(line.Split(' '), out var parsed, out var error), error);
        Assert.Equal(new NetMeterArguments(4321, 133_800_000_000_000_000, "PCMieru.NetMeter." + Token, Sid), parsed);
        Assert.Equal("PCMieru.NetMeter." + Sid, parsed!.SessionName);
    }

    [Fact]
    public void The_options_may_come_in_any_order()
    {
        var args = Good();
        string[] reordered = [args[6], args[7], args[2], args[3], args[0], args[1], args[4], args[5]];

        Assert.True(NetMeterArguments.TryParse(reordered, out var parsed, out var error), error);
        Assert.Equal(4321u, parsed!.ParentPid);
    }

    public static TheoryData<string[]> Refused()
    {
        var good = Good();
        string[] With(int index, string value) => [.. good.Select((v, i) => i == index ? value : v)];
        string[][] rows =
        [
            [],
            good[..6],
            [.. good, "--extra", "x"],
            [good[0], good[1], good[0], good[3], good[4], good[5], good[6], good[7]], // --parent twice, --parent-created missing
            With(0, "--Parent"),
            With(0, "-parent"),
            With(0, "--exec"),
            With(1, "0"),
            With(1, "-5"),
            With(1, "4294967296"),
            With(1, "0x10"),
            With(1, "007"),
            With(1, " 12"),
            With(1, "12abc"),
            With(1, ""),
            With(3, "0"),
            With(3, "-1"),
            With(3, "9223372036854775808"),
            With(3, "99999999999999999999999"),
            With(5, "PCMieru.NetMeter."),
            With(5, "PCMieru.NetMeter.abc"), // token too short
            With(5, "PCMieru.NetMeter." + new string('a', 65)), // token too long
            With(5, "PCMieru.NetMeter." + new string('g', 32)), // not hex
            With(5, "pcmieru.netmeter." + Token), // prefix is exact
            With(5, @"\\.\pipe\PCMieru.NetMeter." + Token), // no path
            With(5, "PCMieru.NetMeter." + Token + @"\..\x"),
            With(5, "Other." + Token),
            With(7, "S-1-5"),
            With(7, "S-2-5-21-1"),
            With(7, "S-1-5-21-"),
            With(7, "S-1-5-21-abc"),
            With(7, "S-1-5-21-4294967296"),
            With(7, "S-1-5-021-1"),
            With(7, "S-1-5--21"),
            With(7, "s-1-5-21-1"),
            With(7, "S-1-5-21-1)(A;;GA;;;WD"), // an extra ACE smuggled into the SDDL
            With(7, "S-1-5-21-1 "),
            With(7, "WD"),
            With(7, "S-1-5-" + string.Join('-', Enumerable.Repeat("1", 16))), // 16 sub-authorities
            With(7, ""),
        ];
        return new TheoryData<string[]>(rows);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Anything_off_the_exact_shape_is_refused(string[] args)
    {
        Assert.False(NetMeterArguments.TryParse(args, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-5-21-1004336348-1177238915-682003330-1001")]
    [InlineData("S-1-12-1-1-2-3-4")]
    public void Well_formed_SIDs_pass(string sid)
    {
        Assert.True(NetMeterArguments.IsSidText(sid));
    }

    [Fact]
    public void The_pipe_name_takes_16_to_64_hex_digits_of_either_case()
    {
        Assert.True(NetMeterArguments.IsPipeName("PCMieru.NetMeter." + new string('A', 16)));
        Assert.True(NetMeterArguments.IsPipeName("PCMieru.NetMeter." + new string('f', 64)));
        Assert.False(NetMeterArguments.IsPipeName("PCMieru.NetMeter." + new string('f', 15)));
    }
}

public sealed class NetMeterEventsTests
{
    [Fact]
    public void Only_the_eight_tcp_and_udp_data_events_are_counted_and_in_the_right_direction()
    {
        var send = new ushort[] { 10, 26, 42, 58 };
        var receive = new ushort[] { 11, 27, 43, 59 };

        for (ushort id = 0; id < 400; id++)
        {
            var counted = NetMeterEvents.TryClassify(id, out var direction);
            Assert.Equal(send.Contains(id) || receive.Contains(id), counted);
            if (counted)
            {
                Assert.Equal(send.Contains(id) ? NetMeterDirection.Send : NetMeterDirection.Receive, direction);
            }
        }
    }

    [Theory]
    [InlineData(12)] // TCPv4 connect attempted
    [InlineData(13)] // TCPv4 disconnect
    [InlineData(14)] // TCPv4 retransmit
    [InlineData(18)] // TCPv4 copy: carries the same bytes as the receive, so it must not be counted
    [InlineData(15)] // TCPv4 accept
    [InlineData(28)] // TCPv6 connect
    [InlineData(34)] // TCPv6 copy
    public void The_neighbouring_events_are_not_counted(ushort id)
    {
        Assert.False(NetMeterEvents.TryClassify(id, out _));
    }

    [Fact]
    public void The_provider_is_the_kernel_network_one_and_the_keywords_are_ipv4_and_ipv6_only()
    {
        Assert.Equal(new Guid("{7DD42A49-5329-4832-8DFD-43D979153A88}"), NetMeterEvents.ProviderGuid);
        Assert.Equal(0x30UL, NetMeterEvents.Keywords);
    }
}

public sealed class NetMeterPayloadLayoutTests
{
    private static NetMeterProperty P(string name, int size = 4) => new(name, size);

    [Fact]
    public void The_fields_are_found_by_name_with_the_sizes_before_them_added_up()
    {
        // The shape of TCPv4 send on Windows 11: PID, size, daddr, saddr, dport, sport, seqnum, connid.
        Assert.True(NetMeterPayloadLayout.TryCreate([P("PID"), P("size"), P("daddr"), P("saddr"), P("dport", 2), P("sport", 2), P("seqnum"), P("connid", 8)], out var layout));
        Assert.Equal(new NetMeterPayloadLayout(0, 4), layout);
    }

    [Fact]
    public void A_field_inserted_before_them_moves_the_offsets()
    {
        Assert.True(NetMeterPayloadLayout.TryCreate([P("startime"), P("endtime", 4), P("size"), P("pad", 2), P("PID")], out var layout));
        Assert.Equal(new NetMeterPayloadLayout(14, 8), layout);
        Assert.Equal(18, layout.RequiredLength);
    }

    [Fact]
    public void A_missing_field_gives_no_layout()
    {
        Assert.False(NetMeterPayloadLayout.TryCreate([P("PID"), P("daddr")], out _));
        Assert.False(NetMeterPayloadLayout.TryCreate([P("size"), P("daddr")], out _));
        Assert.False(NetMeterPayloadLayout.TryCreate([], out _));
        Assert.False(NetMeterPayloadLayout.TryCreate([P("pid"), P("Size")], out _)); // names are exact
    }

    [Fact]
    public void A_field_that_is_not_four_bytes_gives_no_layout()
    {
        Assert.False(NetMeterPayloadLayout.TryCreate([P("PID", 8), P("size")], out _));
        Assert.False(NetMeterPayloadLayout.TryCreate([P("PID"), P("size", 2)], out _));
    }

    [Fact]
    public void A_field_after_a_variable_length_property_gives_no_layout()
    {
        Assert.False(NetMeterPayloadLayout.TryCreate([P("PID"), P("name", -1), P("size")], out _));
        Assert.True(NetMeterPayloadLayout.TryCreate([P("PID"), P("size"), P("name", -1)], out var layout)); // after the two it does not matter
        Assert.Equal(new NetMeterPayloadLayout(0, 4), layout);
    }

    [Fact]
    public void The_fields_are_read_little_endian_and_a_short_payload_is_refused()
    {
        var layout = new NetMeterPayloadLayout(0, 4);
        var payload = new byte[] { 0x39, 0x30, 0, 0, 0x00, 0x00, 0x40, 0x00, 1, 2, 3 };

        Assert.True(layout.TryRead(payload, out var pid, out var size));
        Assert.Equal(12345u, pid);
        Assert.Equal(4u * 1024 * 1024, size);
        Assert.False(layout.TryRead(payload.AsSpan(0, 7), out _, out _));
        Assert.False(layout.TryRead([], out _, out _));
    }

    [Theory]
    [InlineData(0u, (ushort)8, (ushort)0, (ushort)1, 4)] // UINT32
    [InlineData(0u, (ushort)4, (ushort)0, (ushort)1, 1)] // UINT8
    [InlineData(0u, (ushort)10, (ushort)0, (ushort)1, 8)] // UINT64
    [InlineData(0u, (ushort)16, (ushort)0, (ushort)1, 8)] // POINTER on x64
    [InlineData(0u, (ushort)14, (ushort)16, (ushort)1, 16)] // BINARY of 16 (an IPv6 address)
    [InlineData(0u, (ushort)14, (ushort)0, (ushort)1, -1)] // BINARY with no length
    [InlineData(0u, (ushort)1, (ushort)0, (ushort)1, -1)] // UNICODESTRING
    [InlineData(NetMeterTdh.PropertyParamLength, (ushort)14, (ushort)3, (ushort)1, -1)] // length from another field
    [InlineData(NetMeterTdh.PropertyStruct, (ushort)8, (ushort)0, (ushort)1, -1)]
    [InlineData(0u, (ushort)8, (ushort)0, (ushort)2, -1)] // an array
    [InlineData(0u, (ushort)999, (ushort)0, (ushort)1, -1)] // a type this code does not know
    public void Fixed_sizes_come_from_the_tdh_in_type(uint flags, ushort inType, ushort length, ushort count, int expected)
    {
        Assert.Equal(expected, NetMeterTdh.FixedSize(flags, inType, length, count, 8));
    }
}
