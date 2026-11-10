using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace PcMieru.Wire;

/// <summary>What the front end may ask the network meter. There is nothing else: no setting, no path, no command.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<NetMeterRequestKind>))]
public enum NetMeterRequestKind : byte
{
    Read = 1,
    Stop = 2,
}

/// <summary>One process in a reading: cumulative bytes since the meter started, keyed by (pid, creation FILETIME).</summary>
public readonly record struct NetMeterEntry(uint Pid, long CreationFileTime, ulong SendBytes, ulong ReceiveBytes);

/// <summary>
/// The answer to a Read. All totals are cumulative since the meter process started.
/// <see cref="TimestampTicks"/> is the meter's own clock (100 ns units, monotonic, arbitrary origin) up to which the
/// counters are complete; only differences between two readings mean anything (SPEC 4.1).
/// </summary>
public sealed record NetMeterReading(
    long TimestampTicks,
    ulong EventsLost,
    ulong BuffersLost,
    ulong Undecodable,
    bool SessionHealthy,
    uint ErrorCode,
    IReadOnlyList<NetMeterEntry> Entries);

/// <summary>
/// The bytes on the named pipe between the front end and the elevated network meter: a tiny, versioned, little-endian
/// binary format. Each message is a UInt32 payload length and the payload. Pure functions only, so both ends and the
/// tests share one definition, and every decoder rejects what it does not understand (wrong version, unknown kind or
/// flag, a length that does not match, a count above <see cref="MaxEntries"/>) instead of guessing.
/// </summary>
/// <remarks>
/// Request payload (4 bytes): UInt16 version, byte kind, byte 0.
/// Reply payload: UInt16 version, byte kind (3), byte flags (bit 0 = session healthy), UInt32 error code, Int64 timestamp,
/// UInt64 events lost, UInt64 buffers lost, UInt64 undecodable, UInt32 entry count, then per entry
/// UInt32 pid, Int64 creation FILETIME, UInt64 send bytes, UInt64 receive bytes.
/// </remarks>
public static class NetMeterProtocol
{
    public const ushort Version = 1;

    /// <summary>More than any real machine has network-active processes; a count above it is a corrupt message.</summary>
    public const int MaxEntries = 16384;

    public const int PrefixSize = 4;
    public const int RequestPayloadSize = 4;
    public const int ReplyHeaderSize = 44;
    public const int EntrySize = 28;
    public const int MaxPayloadBytes = ReplyHeaderSize + (MaxEntries * EntrySize);

    private const byte ReplyKind = 3;
    private const byte FlagHealthy = 1;

    public static byte[] EncodeRequest(NetMeterRequestKind kind)
    {
        var frame = new byte[PrefixSize + RequestPayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, RequestPayloadSize);
        var payload = frame.AsSpan(PrefixSize);
        BinaryPrimitives.WriteUInt16LittleEndian(payload, Version);
        payload[2] = (byte)kind;
        return frame;
    }

    public static bool TryDecodeRequest(ReadOnlySpan<byte> payload, out NetMeterRequestKind kind, out string error)
    {
        kind = default;
        if (payload.Length != RequestPayloadSize)
        {
            error = "request has the wrong length";
            return false;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(payload) != Version)
        {
            error = "request has an unknown protocol version";
            return false;
        }

        if (payload[2] is not ((byte)NetMeterRequestKind.Read or (byte)NetMeterRequestKind.Stop) || payload[3] != 0)
        {
            error = "request kind is not known";
            return false;
        }

        kind = (NetMeterRequestKind)payload[2];
        error = string.Empty;
        return true;
    }

    public static byte[] EncodeReply(NetMeterReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var count = reading.Entries.Count;
        if (count > MaxEntries)
        {
            throw new ArgumentException("too many entries for one reply", nameof(reading));
        }

        var payloadSize = ReplyHeaderSize + (count * EntrySize);
        var frame = new byte[PrefixSize + payloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payloadSize);
        var p = frame.AsSpan(PrefixSize);
        BinaryPrimitives.WriteUInt16LittleEndian(p, Version);
        p[2] = ReplyKind;
        p[3] = reading.SessionHealthy ? FlagHealthy : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], reading.ErrorCode);
        BinaryPrimitives.WriteInt64LittleEndian(p[8..], reading.TimestampTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(p[16..], reading.EventsLost);
        BinaryPrimitives.WriteUInt64LittleEndian(p[24..], reading.BuffersLost);
        BinaryPrimitives.WriteUInt64LittleEndian(p[32..], reading.Undecodable);
        BinaryPrimitives.WriteUInt32LittleEndian(p[40..], (uint)count);
        var at = ReplyHeaderSize;
        for (var i = 0; i < count; i++)
        {
            var e = reading.Entries[i];
            BinaryPrimitives.WriteUInt32LittleEndian(p[at..], e.Pid);
            BinaryPrimitives.WriteInt64LittleEndian(p[(at + 4)..], e.CreationFileTime);
            BinaryPrimitives.WriteUInt64LittleEndian(p[(at + 12)..], e.SendBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(p[(at + 20)..], e.ReceiveBytes);
            at += EntrySize;
        }

        return frame;
    }

    public static bool TryDecodeReply(ReadOnlySpan<byte> payload, out NetMeterReading? reading, out string error)
    {
        reading = null;
        if (payload.Length < ReplyHeaderSize)
        {
            error = "reply is shorter than its header";
            return false;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(payload) != Version)
        {
            error = "reply has an unknown protocol version";
            return false;
        }

        if (payload[2] != ReplyKind || (payload[3] & ~FlagHealthy) != 0)
        {
            error = "reply kind or flags are not known";
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload[40..]);
        if (count > MaxEntries)
        {
            error = "reply claims more entries than the format allows";
            return false;
        }

        if (payload.Length != ReplyHeaderSize + ((long)count * EntrySize))
        {
            error = "reply length does not match its entry count";
            return false;
        }

        var entries = new NetMeterEntry[count];
        var at = ReplyHeaderSize;
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = new NetMeterEntry(
                BinaryPrimitives.ReadUInt32LittleEndian(payload[at..]),
                BinaryPrimitives.ReadInt64LittleEndian(payload[(at + 4)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(payload[(at + 12)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(payload[(at + 20)..]));
            at += EntrySize;
        }

        reading = new NetMeterReading(
            BinaryPrimitives.ReadInt64LittleEndian(payload[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[32..]),
            (payload[3] & FlagHealthy) != 0,
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
            entries);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates the 4-byte length prefix of an incoming message before any buffer is sized from it.
    /// <paramref name="maxPayload"/> is what the receiver is willing to accept (the meter takes only requests).
    /// </summary>
    public static bool TryReadLength(ReadOnlySpan<byte> prefix, int maxPayload, out int length)
    {
        length = 0;
        if (prefix.Length < PrefixSize)
        {
            return false;
        }

        var raw = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (raw == 0 || raw > maxPayload || raw > MaxPayloadBytes)
        {
            return false;
        }

        length = (int)raw;
        return true;
    }
}
