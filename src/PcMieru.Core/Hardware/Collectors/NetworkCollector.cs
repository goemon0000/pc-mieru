using System.Globalization;
using Microsoft.Win32;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>What the PnP database knows about one network card, found through its NetCfgInstanceId.</summary>
internal sealed record NetworkPnp(string? InstanceId, string? DeviceDescription, string? DriverVersion);

/// <summary>
/// Network adapters from GetAdaptersAddresses (state, speed, addresses), the PnP database (product name, driver, what kind of card it is)
/// and the WLAN API (current Wi-Fi connection). Nothing here sends a packet, scans for networks or reads a saved profile.
/// </summary>
internal static class NetworkCollector
{
    private const string Src = "iphlpapi:GetAdaptersAddresses";
    private static readonly Guid NetClass = new("4d36e972-e325-11ce-bfc1-08002be10318");

    public static Value<IReadOnlyList<NetworkAdapter>> Collect()
    {
        try
        {
            var raw = IpHelper.Read(out var error);
            if (raw is null)
            {
                return Probe.FromWin32<IReadOnlyList<NetworkAdapter>>((int)error, Src, "GetAdaptersAddresses");
            }

            var pnp = ReadPnp();
            var wlan = Wlan.Read(out var wlanError);
            var adapters = raw
                .Where(a => a.IfType is not RawAdapter.IfTypeLoopback and not RawAdapter.IfTypeTunnel)
                .Select(a => NetworkBuilder.Build(a, pnp.GetValueOrDefault(NetworkBuilder.NormalizeGuid(a.AdapterGuid)), wlan, wlanError, ConnectionInstanceId(a.AdapterGuid)))
                .Select((a, i) => (Adapter: a, Index: i))
                .OrderBy(x => NetworkBuilder.SortRank(x.Adapter.Kind.Data))
                .ThenBy(x => x.Index)
                .Select(x => x.Adapter)
                .ToList();
            return Value.Ok<IReadOnlyList<NetworkAdapter>>(adapters, "composite:network");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<IReadOnlyList<NetworkAdapter>>(ex, "composite:network");
        }
    }

    /// <summary>
    /// The PnP instance id Windows keeps next to the connection (Control\Network\{class}\{guid}\Connection). It also exists for adapters
    /// SetupAPI does not list, and is empty for adapters with no device behind them (a Hyper-V virtual switch port).
    /// </summary>
    private static string? ConnectionInstanceId(string adapterGuid)
    {
        var guid = NetworkBuilder.NormalizeGuid(adapterGuid);
        if (guid.Length == 0)
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Network\{{4D36E972-E325-11CE-BFC1-08002BE10318}}\{{{guid}}}\Connection");
            return key?.GetValue("PnpInstanceId") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static Dictionary<string, NetworkPnp> ReadPnp()
    {
        var map = new Dictionary<string, NetworkPnp>(StringComparer.OrdinalIgnoreCase);
        using var set = DeviceInfoSet.ForClass(NetClass);
        if (set is null)
        {
            return map;
        }

        foreach (var node in set.Devices())
        {
            string? guid;
            using (var key = node.OpenDriverKey())
            {
                guid = key?.GetValue("NetCfgInstanceId") as string;
            }

            var normalized = NetworkBuilder.NormalizeGuid(guid);
            if (normalized.Length == 0 || map.ContainsKey(normalized))
            {
                continue;
            }

            map[normalized] = new NetworkPnp(
                node.InstanceId,
                node.GetString(DevKeys.FriendlyName) ?? node.GetString(DevKeys.DeviceDesc),
                node.GetString(DevKeys.DriverVersion));
        }

        return map;
    }
}

/// <summary>The pure part of the network collector: turns raw readings into model values. Unit tested without a network card.</summary>
internal static class NetworkBuilder
{
    private const string Src = "iphlpapi:GetAdaptersAddresses";
    private const string WlanSrc = "wlanapi:WlanQueryInterface";
    private const string NotWireless = NetworkAdapter.NotWirelessReason;
    private const ulong UnknownSpeed = ulong.MaxValue;
    private const int ErrorServiceNotActive = 1062;

    public static string NormalizeGuid(string? text) =>
        Guid.TryParse(text, out var g) ? g.ToString("D") : string.Empty;

    public static NetworkAdapter Build(
        RawAdapter raw,
        NetworkPnp? pnp,
        IReadOnlyDictionary<string, WlanConnection>? wlan,
        uint wlanError,
        string? connectionInstanceId = null)
    {
        var kind = Classify(raw.IfType, pnp?.InstanceId ?? connectionInstanceId);
        var name = !string.IsNullOrWhiteSpace(pnp?.DeviceDescription)
            ? Value.Ok(pnp!.DeviceDescription!.Trim(), "setupapi:DEVPKEY_Device_FriendlyName")
            : !string.IsNullOrWhiteSpace(raw.Description)
                ? Value.Ok(raw.Description!.Trim(), Src)
                : Value.Unsupported<string>(Src, "adapter has no name");

        var isWifi = kind.Data == NetworkKind.Wifi;
        WlanConnection? connection = null;
        if (isWifi && wlan is not null)
        {
            wlan.TryGetValue(NormalizeGuid(raw.AdapterGuid), out connection);
        }

        var link = LinkSpeed(raw, connection);
        return new NetworkAdapter(
            name,
            kind,
            link.Speed,
            Mac(raw),
            AddressList(raw.Addresses, raw.IsUp, "no address assigned"),
            AddressList(raw.DnsServers, raw.IsUp, "no DNS server assigned"),
            string.IsNullOrWhiteSpace(pnp?.DriverVersion)
                ? Value.Unsupported<string>("setupapi:DEVPKEY_Device_DriverVersion", "driver version not reported")
                : Value.Ok(pnp!.DriverVersion!, "setupapi:DEVPKEY_Device_DriverVersion"),
            isWifi ? Ssid(connection, wlan is null, wlanError) : Value.Unsupported<string>(WlanSrc, NotWireless),
            isWifi ? Band(connection, wlan is null, wlanError) : Value.Unsupported<string>(WlanSrc, NotWireless),
            isWifi ? Signal(connection, wlan is null, wlanError) : Value.Unsupported<int>(WlanSrc, NotWireless),
            new NetworkDetails(
                string.IsNullOrWhiteSpace(raw.FriendlyName)
                    ? Value.Unsupported<string>(Src, "no connection name")
                    : Value.Ok(raw.FriendlyName!.Trim(), Src),
                Value.Ok(raw.IsUp, Src),
                raw.Mtu is 0 or uint.MaxValue ? Value.Unsupported<int>(Src, "MTU not reported") : Value.Ok((int)Math.Min(raw.Mtu, int.MaxValue), Src),
                Probe.Note((raw.Flags & RawAdapter.FlagDhcpv4) != 0, Src, "IPv4 address by DHCP"),
                isWifi ? WifiStandard(connection, wlan is null, wlanError) : Value.Unsupported<string>(WlanSrc, NotWireless),
                link.Transmit,
                link.Receive));
    }

    /// <summary>Order in the list: real cards first, then Wi-Fi, then the rest, virtual adapters last.</summary>
    public static int SortRank(NetworkKind kind) => kind switch
    {
        NetworkKind.Ethernet => 0,
        NetworkKind.Wifi => 1,
        NetworkKind.Other => 2,
        NetworkKind.Virtual => 3,
        _ => 4,
    };

    /// <summary>
    /// Wi-Fi by interface type; a wired type is a real card only when its device node sits on PCI or USB. Virtual switches, VPN adapters and
    /// software NICs sit under ROOT or SWD and are called virtual. An adapter with no device at all (a Hyper-V virtual switch port) is virtual too.
    /// </summary>
    public static Value<NetworkKind> Classify(int ifType, string? instanceId)
    {
        const string src = "composite:iftype+devnode";
        if (ifType == RawAdapter.IfTypeLoopback)
        {
            return Value.Ok(NetworkKind.Loopback, Src);
        }

        if (ifType == RawAdapter.IfTypeWifi)
        {
            return Value.Ok(NetworkKind.Wifi, Src);
        }

        var bus = BusOf(instanceId);
        if (ifType == 53)
        {
            return Value.Ok(NetworkKind.Virtual, Src);
        }

        if (bus is "BTH" or "BTHENUM")
        {
            return Probe.Note(NetworkKind.Other, src, "Bluetooth network");
        }

        if (ifType is 6 or 117)
        {
            return bus switch
            {
                "PCI" or "USB" => Value.Ok(NetworkKind.Ethernet, src),
                null => Probe.Note(NetworkKind.Virtual, src, "no hardware device behind this adapter"),
                _ => Probe.Note(NetworkKind.Virtual, src, $"software adapter ({bus})"),
            };
        }

        return Probe.Note(NetworkKind.Other, src, $"interface type {ifType.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string? BusOf(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
        {
            return null;
        }

        var slash = instanceId.IndexOf('\\', StringComparison.Ordinal);
        return slash <= 0 ? null : instanceId[..slash].ToUpperInvariant();
    }

    private static (Value<long> Speed, Value<long> Transmit, Value<long> Receive) LinkSpeed(RawAdapter raw, WlanConnection? wlan)
    {
        const string how = "negotiated by the adapter, not measured";
        if (!raw.IsUp)
        {
            var down = Value.Unsupported<long>(Src, "link is down");
            return (down, down, down);
        }

        var tx = raw.TransmitLinkSpeed;
        var rx = raw.ReceiveLinkSpeed;
        if (wlan is { TxRateKbps: > 0 } || wlan is { RxRateKbps: > 0 })
        {
            tx = wlan.TxRateKbps is > 0 ? (ulong)wlan.TxRateKbps.Value * 1000UL : UnknownSpeed;
            rx = wlan.RxRateKbps is > 0 ? (ulong)wlan.RxRateKbps.Value * 1000UL : UnknownSpeed;
        }

        var src = wlan is null ? Src : WlanSrc;
        Value<long> One(ulong speed) =>
            speed is UnknownSpeed or 0 || speed > long.MaxValue
                ? Value.Unsupported<long>(src, "speed not reported")
                : Probe.Note((long)speed, src, how);

        var transmit = One(tx);
        var receive = One(rx);
        return (transmit, transmit, receive);
    }

    private static Value<string> Mac(RawAdapter raw)
    {
        if (raw.PhysicalAddress.Length != 6 || raw.PhysicalAddress.All(b => b == 0))
        {
            return Value.Unsupported<string>(Src, "no hardware address");
        }

        return Value.Ok(string.Join('-', raw.PhysicalAddress.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))), Src);
    }

    private static Value<IReadOnlyList<string>> AddressList(IReadOnlyList<System.Net.IPAddress> addresses, bool isUp, string none)
    {
        if (!isUp)
        {
            return Value.Unsupported<IReadOnlyList<string>>(Src, "link is down, so nothing is in use");
        }

        if (addresses.Count == 0)
        {
            return Value.Unsupported<IReadOnlyList<string>>(Src, none);
        }

        IReadOnlyList<string> text = addresses.Select(a => a.ToString()).ToList();
        return Value.Ok(text, Src);
    }

    private static Value<T> WlanState<T>(WlanConnection? c, bool apiMissing, uint apiError, Func<WlanConnection, Value<T>> read)
    {
        if (apiMissing)
        {
            return apiError == ErrorServiceNotActive
                ? Value.Unsupported<T>(WlanSrc, "the Windows WLAN service is not running")
                : Probe.FromWin32<T>((int)apiError, WlanSrc, "WLAN API");
        }

        if (c is null)
        {
            return Value.Unsupported<T>(WlanSrc, "Windows does not list this adapter as a Wi-Fi interface");
        }

        if (c.State != Wlan.StateConnected)
        {
            return Value.Unsupported<T>(WlanSrc, "not connected to a Wi-Fi network");
        }

        if (c.Error is { } e)
        {
            return e == Probe.ErrorAccessDenied
                ? Value.Denied<T>(WlanSrc, "Windows hides Wi-Fi details from apps that have no location permission")
                : Probe.FromWin32<T>(e, WlanSrc, "WlanQueryInterface");
        }

        return read(c);
    }

    private static Value<string> Ssid(WlanConnection? c, bool apiMissing, uint apiError) =>
        WlanState(c, apiMissing, apiError, x => string.IsNullOrEmpty(x.Ssid)
            ? Value.Unsupported<string>(WlanSrc, "the network name was not reported")
            : Value.Ok(x.Ssid!, WlanSrc));

    private static Value<int> Signal(WlanConnection? c, bool apiMissing, uint apiError) =>
        WlanState(c, apiMissing, apiError, x => x.SignalQuality is { } q
            ? Probe.Note(q, WlanSrc, "signal quality as Windows reports it, not dBm")
            : Value.Unsupported<int>(WlanSrc, "signal quality not reported"));

    private static Value<string> Band(WlanConnection? c, bool apiMissing, uint apiError) =>
        WlanState(c, apiMissing, apiError, x => WifiBandOf(x.PhyType, x.Channel) is { } band
            ? Probe.Note(band, WlanSrc, $"from the channel number ({x.Channel?.ToString(CultureInfo.InvariantCulture)})")
            : Value.Unsupported<string>(WlanSrc, "the band cannot be told from the channel number alone"));

    private static Value<string> WifiStandard(WlanConnection? c, bool apiMissing, uint apiError) =>
        WlanState(c, apiMissing, apiError, x => WifiStandardName(x.PhyType) is { } s
            ? Value.Ok(s, WlanSrc)
            : Value.Unsupported<string>(WlanSrc, "the connection type was not reported"));

    /// <summary>DOT11_PHY_TYPE to the name people know. Null for types that have no common name.</summary>
    public static string? WifiStandardName(int? phyType) => phyType switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "Wi-Fi 4 (802.11n)",
        8 => "Wi-Fi 5 (802.11ac)",
        10 => "Wi-Fi 6 (802.11ax)",
        11 => "Wi-Fi 7 (802.11be)",
        _ => null,
    };

    /// <summary>
    /// The band, only when the connection type and channel number leave no doubt. Channel numbers repeat across bands (Wi-Fi 6E channel 1 is
    /// 6 GHz, channel 1 of 2.4 GHz is another thing), so an ambiguous pair gives null instead of a guess.
    /// </summary>
    public static string? WifiBandOf(int? phyType, int? channel)
    {
        switch (phyType)
        {
            case 4 or 8:
                return "5 GHz";
            case 5 or 6:
                return "2.4 GHz";
        }

        if (channel is not { } c || c < 1)
        {
            return null;
        }

        if (phyType == 7)
        {
            return c <= 14 ? "2.4 GHz" : "5 GHz";
        }

        // Wi-Fi 6 / 7: 6 GHz channels are 1, 5, 9 ... 233 (one more than a multiple of four).
        var sixGhzNumber = c % 4 == 1;
        if (c is >= 1 and <= 14)
        {
            return sixGhzNumber ? null : "2.4 GHz";
        }

        if (c is >= 36 and <= 177)
        {
            return sixGhzNumber ? null : "5 GHz";
        }

        return sixGhzNumber && c is > 177 and <= 233 ? "6 GHz" : null;
    }
}
