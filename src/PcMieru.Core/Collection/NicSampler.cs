using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PcMieru.Core.Collection;

/// <summary>The part of a MIB_IF_ROW2 the sampler uses.</summary>
internal readonly record struct NicRow(
    ulong Luid,
    Guid Guid,
    string Alias,
    string Description,
    uint Type,
    uint OperStatus,
    byte Flags,
    ulong TransmitLinkSpeed,
    ulong ReceiveLinkSpeed,
    ulong InOctets,
    ulong OutOctets)
{
    public bool IsHardware => (Flags & 0x01) != 0;

    public bool IsFilter => (Flags & 0x02) != 0;

    public bool IsUp => OperStatus == NicRowParser.OperStatusUp;
}

/// <summary>What the adapter stage yields for one tick: the adapters and what the list itself says (see <see cref="ListStatus"/>).</summary>
internal sealed record NicReading(IReadOnlyList<NicInfo> Nics, Value<int> State);

/// <summary>
/// Reads MIB_IF_ROW2 (1352 bytes on x64/x86) at explicit offsets. The table is the kernel's own, but the offsets are the
/// ones from netioapi.h and are checked in tests against hand-built rows and, on the real machine, against what
/// Get-NetAdapterStatistics reports.
/// </summary>
internal static class NicRowParser
{
    public const int RowSize = 1352;
    public const int TableHeaderSize = 8;
    public const uint OperStatusUp = 1;
    public const uint OperStatusNotPresent = 6;
    public const uint TypeLoopback = 24;
    public const uint TypeTunnel = 131;

    private const int Luid = 0;
    private const int Guid = 12;
    private const int Alias = 28;
    private const int AliasChars = 257;
    private const int Description = 542;
    private const int DescriptionChars = 257;
    private const int Type = 1128;
    private const int OperStatus = 1156;
    private const int Flags = 1152;
    private const int TransmitLinkSpeed = 1192;
    private const int ReceiveLinkSpeed = 1200;
    private const int InOctets = 1208;
    private const int OutOctets = 1280;

    public static NicRow Parse(ReadOnlySpan<byte> row) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(row[Luid..]),
        new Guid(row.Slice(Guid, 16)),
        Utf16(row.Slice(Alias, AliasChars * 2)),
        Utf16(row.Slice(Description, DescriptionChars * 2)),
        BinaryPrimitives.ReadUInt32LittleEndian(row[Type..]),
        BinaryPrimitives.ReadUInt32LittleEndian(row[OperStatus..]),
        row[Flags],
        BinaryPrimitives.ReadUInt64LittleEndian(row[TransmitLinkSpeed..]),
        BinaryPrimitives.ReadUInt64LittleEndian(row[ReceiveLinkSpeed..]),
        BinaryPrimitives.ReadUInt64LittleEndian(row[InOctets..]),
        BinaryPrimitives.ReadUInt64LittleEndian(row[OutOctets..]));

    /// <summary>The rows of a MIB_IF_TABLE2 buffer (count at 0, rows from 8). A count that does not fit the buffer gives only the rows that do.</summary>
    public static int Rows(ReadOnlySpan<byte> table, List<NicRow> into)
    {
        into.Clear();
        if (table.Length < TableHeaderSize)
        {
            return 0;
        }

        var claimed = BinaryPrimitives.ReadUInt32LittleEndian(table);
        var fit = (table.Length - TableHeaderSize) / RowSize;
        var count = (int)Math.Min(claimed, (uint)fit);
        for (var i = 0; i < count; i++)
        {
            into.Add(Parse(table.Slice(TableHeaderSize + (i * RowSize), RowSize)));
        }

        return count;
    }

    /// <summary>
    /// Which rows are shown. Not the filter layers Windows stacks on every adapter (WFP, QoS, Npcap: same counters again),
    /// not loopback or tunnels, not ghosts of removed hardware. A real adapter is always shown, connected or not. A virtual one
    /// (Hyper-V, VPN, WAN Miniport) only once it has carried a byte: an unused one is a row of zeros nobody asked for, and it
    /// appears the moment it is used.
    /// </summary>
    public static bool IsShown(in NicRow row) =>
        !row.IsFilter
        && row.Type is not (TypeLoopback or TypeTunnel)
        && row.OperStatus != OperStatusNotPresent
        && (row.IsHardware || row.InOctets != 0 || row.OutOctets != 0);

    private static string Utf16(ReadOnlySpan<byte> bytes)
    {
        var chars = MemoryMarshal.Cast<byte, char>(bytes);
        var end = chars.IndexOf('\0');
        return (end < 0 ? chars : chars[..end]).ToString();
    }
}

/// <summary>
/// Network adapters from GetIfTable2 (send/receive from InOctets/OutOctets deltas over the real elapsed time, link speed,
/// up/down). GetAdaptersAddresses is not used: nothing in <see cref="NicInfo"/> needs addresses, and the friendly name
/// ("Ethernet", "Wi-Fi") is already in the same row.
/// </summary>
internal sealed partial class NicSampler
{
    private const string Source = "GetIfTable2";
    private readonly Dictionary<ulong, Previous> _previous = [];
    private readonly Dictionary<ulong, Previous> _next = [];
    private readonly List<NicRow> _rows = new(32);
    private bool _hasBaseline;

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetIfTable2(out nint table);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void FreeMibTable(nint memory);

    public void Reset()
    {
        _previous.Clear();
        _hasBaseline = false;
    }

    /// <summary>
    /// What the last <see cref="Sample"/> says about the list itself: Ok with the number of adapters shown (zero is a real answer),
    /// or Error with the reason the adapter table could not be read (an empty list then is not "no adapters").
    /// </summary>
    public Value<int> State { get; private set; } = Value.NotCollected<int>(Source, "not sampled yet");

    public IReadOnlyList<NicInfo> Sample()
    {
        var stamp = Stopwatch.GetTimestamp();
        var read = ReadTable(out var why);
        return Finish(read, why, stamp);
    }

    /// <summary>The part of a tick after the table read: an unreadable table is an Error state with the reason, never just an empty list.</summary>
    internal IReadOnlyList<NicInfo> Finish(bool tableRead, string why, long stamp)
    {
        if (!tableRead)
        {
            State = Value.Failed<int>(Source, why);
            return [];
        }

        var nics = Build(_rows, stamp);
        State = Value.Ok(nics.Count, Source);
        return nics;
    }

    /// <summary>Pure part of a tick: rows plus the time they were read give the infos. Public for tests.</summary>
    internal List<NicInfo> Build(IReadOnlyList<NicRow> rows, long stamp)
    {
        var result = new List<NicInfo>(rows.Count);
        _next.Clear();
        foreach (var row in rows)
        {
            if (!NicRowParser.IsShown(row))
            {
                continue;
            }

            Value<double> send;
            Value<double> receive;
            if (_hasBaseline && _previous.TryGetValue(row.Luid, out var before))
            {
                var seconds = Stopwatch.GetElapsedTime(before.Stamp, stamp).TotalSeconds;
                send = Rate(row.OutOctets, before.Out, seconds);
                receive = Rate(row.InOctets, before.In, seconds);
            }
            else
            {
                var detail = _hasBaseline ? "new since the previous sample" : "no baseline yet (first sample)";
                send = Value.NotCollected<double>(Source, detail);
                receive = Value.NotCollected<double>(Source, detail);
            }

            _next[row.Luid] = new Previous(row.InOctets, row.OutOctets, stamp);
            var name = row.Alias.Length > 0 ? row.Alias : row.Description;
            result.Add(new NicInfo(row.Guid.ToString("D"), name, row.IsUp, send, receive, LinkSpeed(row)));
        }

        // Only the adapters seen this tick stay in the baseline, so a removed adapter cannot grow it.
        _previous.Clear();
        _hasBaseline = true;
        foreach (var (luid, p) in _next)
        {
            _previous[luid] = p;
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return result;
    }

    private static Value<double> Rate(ulong now, ulong before, double seconds)
    {
        if (seconds <= 0)
        {
            return Value.NotCollected<double>(Source, "no time elapsed");
        }

        // An adapter reset starts its counters over: that is a gap, not negative traffic and not zero.
        return now < before
            ? Value.NotCollected<double>(Source, "counter went backwards")
            : Value.Ok((now - before) / seconds, Source);
    }

    private static Value<long> LinkSpeed(in NicRow row)
    {
        var speed = row.TransmitLinkSpeed != 0 && row.TransmitLinkSpeed != ulong.MaxValue ? row.TransmitLinkSpeed : row.ReceiveLinkSpeed;
        return speed is 0 or ulong.MaxValue || !row.IsUp
            ? Value.NotCollected<long>(Source, row.IsUp ? "link speed not reported" : "link is down")
            : Value.Ok((long)Math.Min(speed, long.MaxValue), Source);
    }

    private unsafe bool ReadTable(out string why)
    {
        why = string.Empty;
        var status = GetIfTable2(out var table);
        if (status != 0 || table == 0)
        {
            why = $"GetIfTable2 returned {status}";
            return false;
        }

        try
        {
            var count = *(uint*)table;
            if (count > 4096)
            {
                why = $"GetIfTable2 returned an implausible table ({count} rows)";
                return false;
            }

            var length = NicRowParser.TableHeaderSize + ((int)count * NicRowParser.RowSize);
            _ = NicRowParser.Rows(new ReadOnlySpan<byte>((void*)table, length), _rows);
            return true;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    private readonly record struct Previous(ulong In, ulong Out, long Stamp);
}
