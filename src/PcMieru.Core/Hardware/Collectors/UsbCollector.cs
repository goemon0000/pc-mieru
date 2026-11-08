using System.Globalization;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>One USB device node as the PnP database lists it. The instance ids carry serial numbers and never leave this layer.</summary>
internal sealed record RawUsb(
    string InstanceId,
    string? FirstHardwareId,
    string? BusReportedName,
    string? DeviceDescription,
    string? ClassName,
    string? Service,
    string? Location,
    string? ParentInstanceId,
    string? ParentName);

/// <summary>
/// USB devices (and the hubs they hang from) from the PnP database: product name, vendor and product id, hub, port, driver. Windows does
/// not expose what a device draws, so there is no power figure. Nothing is sent to any device.
/// </summary>
internal static class UsbCollector
{
    private const string Src = "setupapi:enumerator USB";

    public static Value<IReadOnlyList<UsbDevice>> Collect()
    {
        try
        {
            using var set = DeviceInfoSet.ForEnumerator("USB");
            if (set is null)
            {
                return Value.Failed<IReadOnlyList<UsbDevice>>(Src, "device list could not be opened");
            }

            var raw = new List<RawUsb>();
            foreach (var node in set.Devices())
            {
                var id = node.InstanceId;
                // A "&MI_nn" node is one function of a composite device (its parent is already listed), so skip it.
                if (id is null || UsbBuilder.IsInterfaceNode(id))
                {
                    continue;
                }

                string? parentId = null;
                string? parentName = null;
                if (DevInstProps.Parent(node.DevInst) is { } parent)
                {
                    parentId = DevInstProps.InstanceId(parent);
                    parentName = DevInstProps.GetString(parent, DevKeys.FriendlyName) ?? DevInstProps.GetString(parent, DevKeys.DeviceDesc);
                }

                raw.Add(new RawUsb(
                    id,
                    node.GetStringList(DevKeys.HardwareIds).FirstOrDefault(),
                    node.GetString(DevKeys.BusReportedDeviceDesc),
                    node.GetString(DevKeys.FriendlyName) ?? node.GetString(DevKeys.DeviceDesc),
                    node.GetString(DevKeys.Class),
                    node.GetString(DevKeys.Service),
                    node.GetString(DevKeys.LocationInfo),
                    parentId,
                    parentName));
            }

            if (raw.Count == 0)
            {
                return Value.Unsupported<IReadOnlyList<UsbDevice>>(Src, "no USB devices are present");
            }

            return Value.Ok(UsbBuilder.Build(raw), "composite:usb");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<IReadOnlyList<UsbDevice>>(ex, Src);
        }
    }
}

/// <summary>The pure part of the USB collector. Unit tested with made-up device nodes.</summary>
internal static class UsbBuilder
{
    private const string Src = "setupapi:enumerator USB";
    private const int MaxDepth = 16;

    /// <summary>"USB\VID_xxxx&amp;PID_xxxx&amp;MI_00\..." is one function of a composite device, not a device of its own.</summary>
    public static bool IsInterfaceNode(string instanceId) => instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the list in tree order (a hub, then what hangs from it), so a plain list reads as a tree.</summary>
    public static IReadOnlyList<UsbDevice> Build(IReadOnlyList<RawUsb> raw)
    {
        var byId = new Dictionary<string, RawUsb>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in raw)
        {
            byId.TryAdd(r.InstanceId, r);
        }

        bool IsChildOfListed(RawUsb r) => r.ParentInstanceId is { } p && byId.ContainsKey(p);

        var children = raw.Where(IsChildOfListed)
            .GroupBy(r => r.ParentInstanceId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Location, StringComparer.Ordinal).ThenBy(r => r.BusReportedName ?? r.DeviceDescription, StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);

        var result = new List<UsbDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(RawUsb node, int depth)
        {
            if (depth > MaxDepth || !seen.Add(node.InstanceId))
            {
                return;
            }

            result.Add(ToDevice(node, depth, byId));
            if (children.TryGetValue(node.InstanceId, out var kids))
            {
                foreach (var kid in kids)
                {
                    Visit(kid, depth + 1);
                }
            }
        }

        foreach (var root in raw.Where(r => !IsChildOfListed(r)).OrderBy(r => r.InstanceId, StringComparer.Ordinal))
        {
            Visit(root, 0);
        }

        return result;
    }

    private static UsbDevice ToDevice(RawUsb node, int depth, Dictionary<string, RawUsb> byId)
    {
        var (vid, pid) = VidPid(node.FirstHardwareId) is { } ids ? ids : (null, null);
        var name = !string.IsNullOrWhiteSpace(node.BusReportedName)
            ? Value.Ok(node.BusReportedName.Trim(), "setupapi:DEVPKEY_Device_BusReportedDeviceDesc")
            : !string.IsNullOrWhiteSpace(node.DeviceDescription)
                ? Value.Ok(node.DeviceDescription.Trim(), "setupapi:DEVPKEY_Device_DeviceDesc")
                : Value.Unsupported<string>(Src, "the device has no name");

        return new UsbDevice(
            name,
            vid is null ? Value.Unsupported<string>(Src, "no vendor id (a root hub is part of the controller)") : Value.Ok(vid, Src),
            pid is null ? Value.Unsupported<string>(Src, "no product id (a root hub is part of the controller)") : Value.Ok(pid, Src),
            Manufacturer(vid),
            ParentText(node, byId),
            new UsbDeviceDetails(
                string.IsNullOrWhiteSpace(node.ClassName)
                    ? Value.Unsupported<string>(Src, "device class not reported")
                    : Value.Ok(node.ClassName, "setupapi:DEVPKEY_Device_Class"),
                string.IsNullOrWhiteSpace(node.Service)
                    ? Value.Unsupported<string>(Src, "no driver is attached")
                    : Value.Ok(node.Service, "setupapi:DEVPKEY_Device_Service"),
                string.IsNullOrWhiteSpace(node.Location)
                    ? Value.Unsupported<string>(Src, "a root hub has no port")
                    : Value.Ok(node.Location, "setupapi:DEVPKEY_Device_LocationInfo"),
                Probe.Note(depth, "composite:usb tree", "0 is a root hub, 1 is plugged into it, 2 is behind one more hub")));
    }

    /// <summary>
    /// Where the device is plugged in, in words. Never the raw instance path, because for many devices that path contains the serial
    /// number. A hub is named with its vendor and product id so two different hubs are told apart.
    /// </summary>
    private static Value<string> ParentText(RawUsb node, Dictionary<string, RawUsb> byId)
    {
        const string src = "composite:usb tree";
        if (node.ParentInstanceId is { } id && byId.TryGetValue(id, out var parent))
        {
            var name = !string.IsNullOrWhiteSpace(parent.DeviceDescription) ? parent.DeviceDescription.Trim() : (parent.BusReportedName ?? "USB device").Trim();
            if (VidPid(parent.FirstHardwareId) is { } p)
            {
                return Value.Ok($"{name} (VID_{p.Vid}&PID_{p.Pid})", src);
            }

            var controller = string.IsNullOrWhiteSpace(parent.ParentName) ? "USB host controller" : parent.ParentName.Trim();
            return Value.Ok($"{name} on {controller}", src);
        }

        return string.IsNullOrWhiteSpace(node.ParentName)
            ? Value.Unsupported<string>(src, "the parent device is not reported")
            : Value.Ok(node.ParentName.Trim(), src);
    }

    /// <summary>"USB\VID_046D&amp;PID_C52B&amp;REV_2407" to ("046D", "C52B"). Null for ids with no vendor part (root hubs).</summary>
    public static (string Vid, string Pid)? VidPid(string? hardwareId)
    {
        if (hardwareId is null)
        {
            return null;
        }

        var v = hardwareId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        var p = hardwareId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (v < 0 || p < 0 || hardwareId.Length < v + 8 || hardwareId.Length < p + 8)
        {
            return null;
        }

        var vid = hardwareId.Substring(v + 4, 4);
        var pid = hardwareId.Substring(p + 4, 4);
        return IsHex(vid) && IsHex(pid) ? (vid.ToUpperInvariant(), pid.ToUpperInvariant()) : null;

        static bool IsHex(string s) => s.All(Uri.IsHexDigit);
    }

    private static Value<string> Manufacturer(string? vid)
    {
        const string src = "usb-ids:built-in vendor table";
        if (vid is null)
        {
            return Value.Unsupported<string>(src, "no vendor id");
        }

        return UsbVendors.Name(vid) is { } name
            ? Value.Ok(name, src)
            : Value.Unsupported<string>(src, $"vendor id {vid} is not in the built-in table");
    }
}

/// <summary>
/// USB vendor ids (the VID in a device id) for brands people actually own, from the public USB-IF assignment list. A vendor that is not
/// listed is reported as unknown, never guessed. Windows itself keeps only the driver publisher, which is often just "(Standard USB Host Controller)".
/// </summary>
internal static class UsbVendors
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["03F0"] = "HP",
        ["0403"] = "FTDI",
        ["0409"] = "NEC",
        ["0424"] = "Microchip (SMSC)",
        ["0461"] = "Primax Electronics",
        ["0471"] = "Philips",
        ["0483"] = "STMicroelectronics",
        ["0489"] = "Foxconn",
        ["04A9"] = "Canon",
        ["04B8"] = "Seiko Epson",
        ["04CA"] = "Lite-On",
        ["04D9"] = "Holtek",
        ["04E8"] = "Samsung",
        ["04F2"] = "Chicony",
        ["04F9"] = "Brother",
        ["0502"] = "Acer",
        ["054C"] = "Sony",
        ["056A"] = "Wacom",
        ["056E"] = "ELECOM",
        ["057E"] = "Nintendo",
        ["05AC"] = "Apple",
        ["05E3"] = "Genesys Logic",
        ["067B"] = "Prolific",
        ["0781"] = "SanDisk",
        ["07D1"] = "D-Link",
        ["0846"] = "NETGEAR",
        ["045E"] = "Microsoft",
        ["046A"] = "Cherry",
        ["046D"] = "Logitech",
        ["0930"] = "Toshiba",
        ["0951"] = "Kingston",
        ["09DA"] = "A4Tech",
        ["0A12"] = "Cambridge Silicon Radio",
        ["0A5C"] = "Broadcom",
        ["0B05"] = "ASUS",
        ["0B95"] = "ASIX",
        ["0BC2"] = "Seagate",
        ["0BDA"] = "Realtek",
        ["0C45"] = "Microdia",
        ["0CF3"] = "Qualcomm Atheros",
        ["0D8C"] = "C-Media",
        ["0E8D"] = "MediaTek",
        ["0FD9"] = "Elgato",
        ["1038"] = "SteelSeries",
        ["1058"] = "Western Digital",
        ["10C4"] = "Silicon Labs",
        ["12D1"] = "Huawei",
        ["13D3"] = "IMC Networks",
        ["148F"] = "Ralink",
        ["152D"] = "JMicron",
        ["1532"] = "Razer",
        ["174C"] = "ASMedia",
        ["17EF"] = "Lenovo",
        ["18D1"] = "Google",
        ["1A40"] = "Terminus Technology",
        ["1A86"] = "QinHeng (WCH)",
        ["1B1C"] = "Corsair",
        ["1D6B"] = "Linux Foundation",
        ["1E7D"] = "ROCCAT",
        ["2109"] = "VIA Labs",
        ["2341"] = "Arduino",
        ["2357"] = "TP-Link",
        ["239A"] = "Adafruit",
        ["2516"] = "Cooler Master",
        ["28DE"] = "Valve",
        ["2833"] = "Meta (Oculus)",
        ["413C"] = "Dell",
        ["8087"] = "Intel",
    };

    public static string? Name(string vid) => Names.GetValueOrDefault(vid);
}
