using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace PcMieru.Core.Hardware.Native;

/// <summary>One network interface as GetAdaptersAddresses lists it.</summary>
internal sealed record RawAdapter(
    string AdapterGuid,
    string? FriendlyName,
    string? Description,
    int IfType,
    int OperStatus,
    uint Mtu,
    byte[] PhysicalAddress,
    uint Flags,
    ulong TransmitLinkSpeed,
    ulong ReceiveLinkSpeed,
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyList<IPAddress> DnsServers)
{
    public const int IfTypeLoopback = 24;
    public const int IfTypeTunnel = 131;
    public const int IfTypeWifi = 71;
    public const int OperUp = 1;
    public const uint FlagDhcpv4 = 0x4;

    public bool IsUp => OperStatus == OperUp;
}

internal static partial class IpHelperNative
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    public static extern uint GetAdaptersAddresses(uint family, uint flags, IntPtr reserved, IntPtr addresses, ref uint size);
}

/// <summary>GetAdaptersAddresses parsed by fixed offsets (x64 / ARM64 layout of IP_ADAPTER_ADDRESSES_LH).</summary>
internal static unsafe class IpHelper
{
    private const uint SkipAnycast = 0x2;
    private const uint SkipMulticast = 0x4;
    private const uint ErrorBufferOverflow = 111;

    public static IReadOnlyList<RawAdapter>? Read(out uint error)
    {
        error = 0;
        if (IntPtr.Size != 8)
        {
            error = 50;
            return null;
        }

        uint size = 32 * 1024;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                buffer = Marshal.AllocHGlobal((int)size);
                error = IpHelperNative.GetAdaptersAddresses(0, SkipAnycast | SkipMulticast, IntPtr.Zero, buffer, ref size);
                if (error != ErrorBufferOverflow)
                {
                    break;
                }

                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
            }

            if (error != 0 || buffer == IntPtr.Zero)
            {
                return null;
            }

            var list = new List<RawAdapter>();
            for (var p = (byte*)buffer; p != null; p = *(byte**)(p + 8))
            {
                var macLength = (int)*(uint*)(p + 88);
                var mac = new byte[Math.Min(macLength, 8)];
                for (var i = 0; i < mac.Length; i++)
                {
                    mac[i] = p[80 + i];
                }

                list.Add(new RawAdapter(
                    Marshal.PtrToStringAnsi(*(IntPtr*)(p + 16)) ?? string.Empty,
                    Marshal.PtrToStringUni(*(IntPtr*)(p + 72)),
                    Marshal.PtrToStringUni(*(IntPtr*)(p + 64)),
                    (int)*(uint*)(p + 100),
                    (int)*(uint*)(p + 104),
                    *(uint*)(p + 96),
                    mac,
                    *(uint*)(p + 92),
                    *(ulong*)(p + 184),
                    *(ulong*)(p + 192),
                    Addresses(*(byte**)(p + 24), unicast: true),
                    Addresses(*(byte**)(p + 48), unicast: false)));
            }

            return list;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// Walks a linked list of IP_ADAPTER_UNICAST_ADDRESS / IP_ADAPTER_DNS_SERVER_ADDRESS (same leading layout). Unicast addresses that
    /// failed or have not finished duplicate address detection (DadState at +40: invalid 0, tentative 1, duplicate 2) are not in use and are skipped.
    /// </summary>
    private static List<IPAddress> Addresses(byte* first, bool unicast)
    {
        var list = new List<IPAddress>();
        for (var p = first; p != null; p = *(byte**)(p + 8))
        {
            var sockaddr = *(byte**)(p + 16);
            var length = *(int*)(p + 24);
            if (sockaddr == null || (unicast && *(int*)(p + 40) < 3))
            {
                continue;
            }

            var family = *(ushort*)sockaddr;
            if (family == 2 && length >= 8)
            {
                list.Add(new IPAddress(new ReadOnlySpan<byte>(sockaddr + 4, 4)));
            }
            else if (family == 23 && length >= 24)
            {
                list.Add(new IPAddress(new ReadOnlySpan<byte>(sockaddr + 8, 16)));
            }
        }

        return list;
    }
}

/// <summary>What the WLAN API says about one wireless interface right now.</summary>
internal sealed record WlanConnection(
    int State,
    string? Ssid,
    int? PhyType,
    int? SignalQuality,
    int? Channel,
    int? Error,
    int? RxRateKbps = null,
    int? TxRateKbps = null);

internal static partial class WlanNative
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    public static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    public static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    public static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    public static extern uint WlanQueryInterface(IntPtr handle, ref Guid interfaceGuid, int opcode, IntPtr reserved, out uint size, out IntPtr data, IntPtr opcodeValueType);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    public static extern void WlanFreeMemory(IntPtr memory);
}

/// <summary>
/// WLAN API reads of the current connection only (no scan, no profile). When Windows hides Wi-Fi details because desktop apps
/// lack the location permission, the call fails with access denied and the caller reports Denied.
/// </summary>
internal static unsafe class Wlan
{
    private const int OpcodeCurrentConnection = 7;
    private const int OpcodeChannelNumber = 8;
    public const int StateConnected = 1;

    /// <summary>Keyed by interface GUID (format D, no braces). Null when the WLAN service or API is unavailable.</summary>
    public static Dictionary<string, WlanConnection>? Read(out uint error)
    {
        error = WlanNative.WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
        if (error != 0)
        {
            return null;
        }

        try
        {
            error = WlanNative.WlanEnumInterfaces(handle, IntPtr.Zero, out var list);
            if (error != 0 || list == IntPtr.Zero)
            {
                return null;
            }

            var result = new Dictionary<string, WlanConnection>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var count = *(uint*)list;
                var item = (byte*)list + 8;
                for (var i = 0; i < count; i++, item += 532)
                {
                    var guid = *(Guid*)item;
                    var state = *(int*)(item + 16 + 512);
                    result[guid.ToString("D")] = Query(handle, guid, state);
                }
            }
            finally
            {
                WlanNative.WlanFreeMemory(list);
            }

            error = 0;
            return result;
        }
        finally
        {
            WlanNative.WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static WlanConnection Query(IntPtr handle, Guid guid, int state)
    {
        if (state != StateConnected)
        {
            return new WlanConnection(state, null, null, null, null, null);
        }

        var status = WlanNative.WlanQueryInterface(handle, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out var size, out var data, IntPtr.Zero);
        if (status != 0 || data == IntPtr.Zero)
        {
            return new WlanConnection(state, null, null, null, null, (int)status);
        }

        try
        {
            // WLAN_CONNECTION_ATTRIBUTES: state(4) mode(4) profile name(512), then WLAN_ASSOCIATION_ATTRIBUTES at 520.
            if (size < 520 + 68)
            {
                return new WlanConnection(state, null, null, null, null, 13);
            }

            var p = (byte*)data + 520;
            var ssidLength = (int)Math.Min(*(uint*)p, 32);
            var ssid = ssidLength == 0 ? null : Encoding.UTF8.GetString(p + 4, ssidLength);
            var phy = (int)*(uint*)(p + 48);
            var quality = (int)*(uint*)(p + 56);
            var rx = (int)*(uint*)(p + 60);
            var tx = (int)*(uint*)(p + 64);
            int? channel = null;
            var channelStatus = WlanNative.WlanQueryInterface(handle, ref guid, OpcodeChannelNumber, IntPtr.Zero, out var csize, out var cdata, IntPtr.Zero);
            if (channelStatus == 0 && cdata != IntPtr.Zero)
            {
                if (csize >= 4)
                {
                    var c = (int)*(uint*)cdata;
                    channel = c > 0 ? c : null;
                }

                WlanNative.WlanFreeMemory(cdata);
            }

            return new WlanConnection(state, ssid, phy, quality is >= 0 and <= 100 ? quality : null, channel, null, rx > 0 ? rx : null, tx > 0 ? tx : null);
        }
        finally
        {
            WlanNative.WlanFreeMemory(data);
        }
    }
}
