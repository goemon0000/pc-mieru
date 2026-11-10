using System.Text.Json.Serialization;

namespace PcMieru.Wire;

[JsonConverter(typeof(CamelCaseEnumConverter<NetMeterDirection>))]
public enum NetMeterDirection : byte
{
    Send,
    Receive,
}

/// <summary>
/// Which Microsoft-Windows-Kernel-Network events carry the bytes of a TCP or UDP send or receive. The ids were read from the
/// provider's manifest and from a live trace on Windows 11 (see SPEC 4.1); every other event of the provider (connect,
/// accept, disconnect, retransmit, the TCP "copy" duplicate of a receive) is deliberately not counted, otherwise the same
/// bytes would be added twice.
/// </summary>
public static class NetMeterEvents
{
    public const string ProviderGuidText = "7dd42a49-5329-4832-8dfd-43d979153a88";

    public static readonly Guid ProviderGuid = new(ProviderGuidText);

    /// <summary>KERNEL_NETWORK_KEYWORD_IPV4 and _IPV6: the only two keywords the session asks for.</summary>
    public const ulong KeywordIPv4 = 0x10;

    public const ulong KeywordIPv6 = 0x20;
    public const ulong Keywords = KeywordIPv4 | KeywordIPv6;

    /// <summary>The payload field names the meter decodes by name (never by a fixed offset).</summary>
    public const string PidField = "PID";

    public const string SizeField = "size";

    /// <summary>
    /// How long a session buffer that holds events waits before ETW hands it over, in milliseconds (the session sets
    /// EVENT_TRACE_USE_MS_FLUSH_TIMER, so the unit is milliseconds, not the default seconds). It is what bounds the time from an
    /// event happening to its delivery, and so what <see cref="NetMeterCounters.LagTicks"/> has to cover.
    /// </summary>
    public const uint FlushTimerMilliseconds = 100;

    /// <summary>TCP send/recv IPv4 = 10/11, IPv6 = 26/27; UDP send/recv IPv4 = 42/43, IPv6 = 58/59.</summary>
    public static bool TryClassify(ushort eventId, out NetMeterDirection direction)
    {
        switch (eventId)
        {
            case 10 or 26 or 42 or 58:
                direction = NetMeterDirection.Send;
                return true;
            case 11 or 27 or 43 or 59:
                direction = NetMeterDirection.Receive;
                return true;
            default:
                direction = default;
                return false;
        }
    }
}
