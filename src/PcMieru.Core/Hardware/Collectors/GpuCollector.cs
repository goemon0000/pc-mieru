using System.Globalization;
using Microsoft.Win32;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>
/// Graphics adapters from DXGI (name, ids, memory, LUID), the display kernel driver (PCI address), the PnP database (driver, PCIe link)
/// and the display class registry key (total video memory). Vendor-API values (VBIOS, power limit) are added later from the sensor host.
/// </summary>
internal static class GpuCollector
{
    private const string Src = "dxgi:IDXGIAdapter1::GetDesc1";
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");

    private sealed record PnpAdapter(DeviceNode Node, uint VendorId, uint DeviceId, uint? Bus, uint? Device, uint? Function);

    public static Value<IReadOnlyList<GpuHardware>> Collect()
    {
        try
        {
            var adapters = Native.Dxgi.Enumerate(out var hr);
            if (adapters is null)
            {
                return Value.Failed<IReadOnlyList<GpuHardware>>(Src, $"DXGI factory failed (HRESULT 0x{hr:X8})");
            }

            // The "Microsoft Basic Render Driver" is always listed next to a real GPU; hide it unless it is all there is.
            var real = adapters.Where(a => !a.IsSoftware).ToList();
            var chosen = real.Count > 0 ? real : adapters.ToList();
            using var set = DeviceInfoSet.ForClass(DisplayClass);
            var pnp = set is null ? [] : set.Devices().Select(ReadPnp).Where(p => p is not null).Select(p => p!).ToList();
            var used = new HashSet<uint>();
            var probed = chosen.Select(a => (Adapter: a, Kmt: Native.Dxgi.QueryKmt(a.LuidLow, a.LuidHigh))).ToList();
            // Hyper-V, WSL and remote sessions list the same card again as a virtual copy with no PCI address. Show the real one only.
            var visible = probed.Where(p => p.Kmt is null || p.Kmt.HasPciAddress || !probed.Any(o => o.Kmt is { HasPciAddress: true } && SameCard(o.Adapter, p.Adapter))).ToList();
            var list = visible.Select(p => Build(p.Adapter, p.Kmt, pnp, used)).ToList();
            return Value.Ok<IReadOnlyList<GpuHardware>>(list, "composite:gpu");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<IReadOnlyList<GpuHardware>>(ex, "composite:gpu");
        }
    }

    private static PnpAdapter? ReadPnp(DeviceNode node)
    {
        var ids = node.GetStringList(DevKeys.HardwareIds);
        var pci = ids.FirstOrDefault(i => i.StartsWith("PCI\\VEN_", StringComparison.OrdinalIgnoreCase));
        if (pci is null || pci.Length < 21)
        {
            return null;
        }

        var ven = ParseHex(pci.AsSpan(8, 4));
        var dev = pci.AsSpan(12, 5).Equals("&DEV_", StringComparison.OrdinalIgnoreCase) ? ParseHex(pci.AsSpan(17, 4)) : null;
        if (ven is null || dev is null)
        {
            return null;
        }

        var address = node.GetInteger(DevKeys.Address);
        var bus = node.GetInteger(DevKeys.BusNumber);
        return new PnpAdapter(
            node,
            ven.Value,
            dev.Value,
            bus is { } b ? (uint)b : null,
            address is { } a ? (uint)((a >> 16) & 0xFFFF) : null,
            address is { } f ? (uint)(f & 0xFFFF) : null);
    }

    private static uint? ParseHex(ReadOnlySpan<char> s) =>
        uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static string VendorName(uint id) => id switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        0x5143 => "Qualcomm",
        _ => $"PCI vendor 0x{id:X4}",
    };

    private static bool SameCard(DxgiAdapter x, DxgiAdapter y) =>
        x.VendorId == y.VendorId && x.DeviceId == y.DeviceId && x.SubsystemId == y.SubsystemId && x.Revision == y.Revision;

    private static GpuHardware Build(DxgiAdapter a, KmtAdapterInfo? kmt, List<PnpAdapter> pnp, HashSet<uint> used)
    {
        var match = Match(a, kmt, pnp, used);
        if (match is not null)
        {
            used.Add(match.Node.DevInst);
        }

        const string drvSource = "setupapi:DEVPKEY_Device_DriverVersion";
        var node = match?.Node;
        var version = node?.GetString(DevKeys.DriverVersion);
        var date = node?.GetFileTime(DevKeys.DriverDate);
        var provider = node?.GetString(DevKeys.DriverProvider);
        var noMatch = "no display device in the PnP database matches this adapter (software adapter or a driver that has not registered)";

        var pci = match is null ? null : PciNode.FindFrom(match.Node.DevInst);
        var (regBytes, regDetail) = match is null ? (null, null) : RegistryVideoMemory(match.Node);
        var dedicated = DedicatedMemory(a, regBytes, regDetail);

        var luid = Value.Ok(a.LuidText, Src + " AdapterLuid");
        var pciId = a.IsSoftware
            ? Value.Unsupported<string>(Src, "software adapter has no PCI id")
            : Value.Ok($"{a.VendorId:X4}:{a.DeviceId:X4}", Src + " VendorId:DeviceId");
        var location = kmt is null
            ? Value.Unsupported<string>("d3dkmt:ADAPTERADDRESS", "the display kernel driver did not report an address")
            : !kmt.HasPciAddress
            ? Value.Unsupported<string>("d3dkmt:KMTQAITYPE_ADAPTERADDRESS", "not a physical PCI function (virtual or remote copy of the adapter)")
            : Value.Ok($"bus {kmt.Bus}, device {kmt.Device}, function {kmt.Function}", "d3dkmt:KMTQAITYPE_ADAPTERADDRESS");

        var details = new GpuDetails(
            Kind(a, kmt),
            a.IsSoftware || a.SubsystemId == 0
                ? Value.Unsupported<string>(Src, "no subsystem id")
                : Value.Ok($"{a.SubsystemId & 0xFFFF:X4}:{a.SubsystemId >> 16:X4}", Src + " SubSysId (subsystem vendor:device)"),
            a.IsSoftware ? Value.Unsupported<int>(Src, "software adapter") : Value.Ok((int)a.Revision, Src + " Revision"),
            string.IsNullOrWhiteSpace(provider) ? Value.Unsupported<string>(drvSource, noMatch) : Value.Ok(provider, "setupapi:DEVPKEY_Device_DriverProvider"),
            Value.Ok((long)Math.Min(a.DedicatedSystemBytes, long.MaxValue), Src + " DedicatedSystemMemory"),
            location,
            pci?.MaxGeneration ?? Value.Unsupported<int>("setupapi:DEVPKEY_PciDevice_MaxLinkSpeed", "no PCI node found for this adapter"),
            pci?.MaxWidth ?? Value.Unsupported<int>("setupapi:DEVPKEY_PciDevice_MaxLinkWidth", "no PCI node found for this adapter"));

        return new GpuHardware(
            Native.Probe.Text(Src + " Description", a.Name, "adapter has no name"),
            Value.Ok(VendorName(a.VendorId), Src + " VendorId"),
            string.IsNullOrWhiteSpace(version) ? Value.Unsupported<string>(drvSource, noMatch) : Value.Ok(version, drvSource),
            date is null ? Value.Unsupported<string>("setupapi:DEVPKEY_Device_DriverDate", noMatch) : Value.Ok(date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "setupapi:DEVPKEY_Device_DriverDate"),
            pciId,
            luid,
            dedicated,
            Value.Ok((long)Math.Min(a.SharedSystemBytes, long.MaxValue), Src + " SharedSystemMemory (limit the driver may borrow from RAM, not reserved)"),
            Value.Unsupported<string>("vendor-api", "the VBIOS version comes from the vendor API (NVML/ADLX) in the sensor host"),
            pci?.CurrentGeneration ?? Value.Unsupported<int>("setupapi:DEVPKEY_PciDevice_CurrentLinkSpeed", "no PCI node found for this adapter"),
            pci?.CurrentWidth ?? Value.Unsupported<int>("setupapi:DEVPKEY_PciDevice_CurrentLinkWidth", "no PCI node found for this adapter"),
            Value.Unsupported<double>("vendor-api", "the power limit comes from the vendor API (NVML/ADLX) in the sensor host"),
            details);
    }

    private static PnpAdapter? Match(DxgiAdapter a, KmtAdapterInfo? kmt, List<PnpAdapter> pnp, HashSet<uint> used)
    {
        if (a.IsSoftware)
        {
            return null;
        }

        if (kmt is { HasPciAddress: true })
        {
            var byAddress = pnp.FirstOrDefault(p => !used.Contains(p.Node.DevInst) && p.VendorId == a.VendorId && p.Bus == kmt.Bus && p.Device == kmt.Device && p.Function == kmt.Function);
            if (byAddress is not null)
            {
                return byAddress;
            }
        }

        return pnp.FirstOrDefault(p => !used.Contains(p.Node.DevInst) && p.VendorId == a.VendorId && p.DeviceId == a.DeviceId);
    }

    private static (long? Bytes, string? Detail) RegistryVideoMemory(DeviceNode node)
    {
        try
        {
            using var key = node.OpenDriverKey();
            var value = key?.GetValue("HardwareInformation.qwMemorySize");
            return value switch
            {
                long l when l > 0 => (l, null),
                byte[] { Length: 8 } b when BitConverter.ToInt64(b, 0) > 0 => (BitConverter.ToInt64(b, 0), null),
                int i when i > 0 => ((long)(uint)i, "REG_DWORD"),
                _ => (null, null),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, ex.GetType().Name);
        }
    }

    private static Value<long> DedicatedMemory(DxgiAdapter a, long? registry, string? registryDetail)
    {
        var dxgi = (long)Math.Min(a.DedicatedVideoBytes, long.MaxValue);
        if (registry is { } r && r >= dxgi)
        {
            return new Value<long>(r, ValueStatus.Ok, "registry:HardwareInformation.qwMemorySize", $"DXGI reports {dxgi} bytes usable");
        }

        if (a.IsSoftware)
        {
            return Value.Unsupported<long>(Src, "software adapter has no video memory");
        }

        if (dxgi == 0 && registry is null)
        {
            return Value.Unsupported<long>(Src, "no dedicated video memory reported (shared-memory adapter)");
        }

        return new Value<long>(dxgi, ValueStatus.Ok, Src + " DedicatedVideoMemory", registryDetail);
    }

    private static Value<string> Kind(DxgiAdapter a, KmtAdapterInfo? kmt)
    {
        if (a.IsSoftware)
        {
            return Value.Ok("Software", Src + " Flags (software adapter)");
        }

        if (kmt?.HybridDiscrete == true)
        {
            return Value.Ok("Discrete", "d3dkmt:KMTQAITYPE_ADAPTERTYPE (hybrid discrete)");
        }

        if (kmt?.HybridIntegrated == true)
        {
            return Value.Ok("Integrated", "d3dkmt:KMTQAITYPE_ADAPTERTYPE (hybrid integrated)");
        }

        // Windows only labels integrated/discrete on hybrid-graphics systems. Otherwise judge by the memory that is physically on the card.
        return a.DedicatedVideoBytes >= 2UL << 30
            ? new Value<string>("Discrete", ValueStatus.Ok, Src + " DedicatedVideoMemory (inferred: 2 GiB or more of own memory)")
            : Value.Unsupported<string>(Src, "Windows does not say whether this adapter is built into the CPU or a separate card");
    }
}
