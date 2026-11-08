using PcMieru.Core;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;
using PcMieru.Core.Hardware.Sensors;

namespace PcMieru.SensorHost;

/// <summary>
/// Reads every GPU the vendor libraries know about and gives each the id the hardware list uses (the display adapter's LUID), so the app can
/// put temperature and power next to the right card. NVIDIA GPUs are tied to adapters by PCI bus/device; AMD GPUs (ADLX has no PCI address)
/// by name, and only when that is unambiguous. A GPU that cannot be tied to an adapter is still reported, under a vendor id with its PCI
/// position when known, and the app joins it by that instead.
/// </summary>
internal sealed class GpuSampler
{
    private const string Src = "sensorhost";
    private const uint NvidiaVendorId = 0x10DE;
    private const uint AmdVendorId = 0x1002;
    private const long MapLifetimeMs = 60_000;

    private readonly NvmlReader? _nvml;
    private readonly AdlxReader? _adlx;
    private readonly string _missingReason;
    private readonly Dictionary<int, NvmlStatic> _nvmlStatic = [];
    private VendorText _nvmlDriver;
    private bool _nvmlDriverSettled;
    private List<Slot>? _slots;
    private long _mapBuiltAt;

    public GpuSampler(Action<string> log)
    {
        _nvml = NvmlReader.TryOpen(out var nvmlWhy);
        _adlx = AdlxReader.TryOpen(out var adlxWhy);
        log(_nvml is null ? "nvml: " + nvmlWhy : $"nvml: loaded from {_nvml.LoadedFrom}");
        log(_adlx is null ? "adlx: " + adlxWhy : $"adlx: loaded from {_adlx.LoadedFrom}, {_adlx.Gpus.Count} gpu(s)");
        _missingReason = $"{nvmlWhy}; {adlxWhy}";
    }

    private enum Api
    {
        Nvml,
        Adlx,
    }

    /// <summary>One GPU of a vendor library, with the id it is reported under.</summary>
    private sealed record Slot(Api Api, int Index, string Name, string Id, string? PciBusId);

    public Value<IReadOnlyList<GpuSensorReading>> Sample()
    {
        if (_nvml is null && _adlx is null)
        {
            return Value.Unsupported<IReadOnlyList<GpuSensorReading>>(Src, "no NVIDIA or AMD vendor library on this PC (" + _missingReason + ")");
        }

        var now = Environment.TickCount64;
        if (_slots is null || now - _mapBuiltAt > MapLifetimeMs)
        {
            _slots = BuildMap();
            _mapBuiltAt = now;
        }

        var readings = new List<GpuSensorReading>(_slots.Count);
        foreach (var slot in _slots)
        {
            readings.Add(slot.Api == Api.Nvml ? ReadNvml(slot) : ReadAdlx(slot));
        }

        return Value.Ok<IReadOnlyList<GpuSensorReading>>(readings, Src);
    }

    private List<Slot> BuildMap()
    {
        var adapters = Dxgi.Enumerate(out _)?.Where(a => !a.IsSoftware).ToList() ?? [];
        var probed = adapters.Select(a => (Adapter: a, Kmt: Dxgi.QueryKmt(a.LuidLow, a.LuidHigh))).ToList();
        var slots = new List<Slot>();

        if (_nvml is not null)
        {
            foreach (var device in _nvml.Devices())
            {
                var matches = probed
                    .Where(p => p.Adapter.VendorId == NvidiaVendorId && p.Kmt is { HasPciAddress: true } k && k.Bus == device.Bus && k.Device == device.Device)
                    .ToList();
                var id = matches.Count == 1 ? matches[0].Adapter.LuidText : $"nvml:{device.Index}";
                slots.Add(new Slot(Api.Nvml, device.Index, device.Name, id, PciBusIds.Format(device.Domain, device.Bus, device.Device)));
            }
        }

        if (_adlx is not null)
        {
            // A virtual copy of a GPU (remote session, paravirtualized) has no PCI address and the same name: leave it out of the pairing.
            var candidates = probed
                .Where(p => p.Adapter.VendorId == AmdVendorId && (p.Kmt is null || p.Kmt.HasPciAddress))
                .ToList();
            var pairs = SensorMapping.PairByName(
                candidates.Select(c => c.Adapter.Name).ToList(),
                _adlx.Gpus.Select(g => g.Name).ToList(),
                out _);
            var adapterOfGpu = pairs.ToDictionary(kv => kv.Value, kv => candidates[kv.Key]);
            foreach (var gpu in _adlx.Gpus)
            {
                if (adapterOfGpu.TryGetValue(gpu.Index, out var found))
                {
                    var pci = found.Kmt is { HasPciAddress: true } k ? PciBusIds.Format(0, k.Bus, k.Device) : null;
                    slots.Add(new Slot(Api.Adlx, gpu.Index, gpu.Name, found.Adapter.LuidText, pci));
                }
                else
                {
                    slots.Add(new Slot(Api.Adlx, gpu.Index, gpu.Name, $"adlx:{gpu.Index}", null));
                }
            }
        }

        return slots;
    }

    private GpuSensorReading ReadNvml(Slot slot)
    {
        const VendorApi A = VendorApi.Nvml;
        var nvml = _nvml!;
        var live = nvml.ReadLive(slot.Index);
        var fixedValues = StaticOf(nvml, slot.Index);
        if (!_nvmlDriverSettled)
        {
            _nvmlDriver = nvml.DriverVersion();
            _nvmlDriverSettled = Settled(_nvmlDriver.Code);
        }

        var extras = new GpuSensorExtras(
            Vbios: SensorMapping.Text(A, fixedValues.Vbios.Text, fixedValues.Vbios.Code, "nvml:nvmlDeviceGetVbiosVersion"),
            DriverVersion: SensorMapping.Text(A, _nvmlDriver.Text, _nvmlDriver.Code, "nvml:nvmlSystemGetDriverVersion"),
            PcieGeneration: SensorMapping.Integer(A, live.PcieGeneration.Number, live.PcieGeneration.Code, "nvml:nvmlDeviceGetCurrPcieLinkGeneration", 1, 7),
            PcieWidth: SensorMapping.Integer(A, live.PcieWidth.Number, live.PcieWidth.Code, "nvml:nvmlDeviceGetCurrPcieLinkWidth", 1, 32),
            PcieMaxGeneration: SensorMapping.Integer(A, fixedValues.PcieMaxGeneration.Number, fixedValues.PcieMaxGeneration.Code, "nvml:nvmlDeviceGetMaxPcieLinkGeneration", 1, 7),
            PcieMaxWidth: SensorMapping.Integer(A, fixedValues.PcieMaxWidth.Number, fixedValues.PcieMaxWidth.Code, "nvml:nvmlDeviceGetMaxPcieLinkWidth", 1, 32),
            PowerLimitMinWatts: SensorMapping.Number(A, fixedValues.PowerLimitMinW.Number, fixedValues.PowerLimitMinW.Code, "nvml:nvmlDeviceGetPowerManagementLimitConstraints", 0, 2000),
            PowerLimitMaxWatts: SensorMapping.Number(A, fixedValues.PowerLimitMaxW.Number, fixedValues.PowerLimitMaxW.Code, "nvml:nvmlDeviceGetPowerManagementLimitConstraints", 0, 2000),
            PowerLimitDefaultWatts: SensorMapping.Number(A, fixedValues.PowerLimitDefaultW.Number, fixedValues.PowerLimitDefaultW.Code, "nvml:nvmlDeviceGetPowerManagementDefaultLimit", 0, 2000),
            FanRpm: Value.Unsupported<double>("nvml:nvmlDeviceGetFanSpeed", "NVIDIA's driver gives the fan speed in percent only"),
            MemoryClockMhz: SensorMapping.Number(A, live.MemoryMhz.Number, live.MemoryMhz.Code, "nvml:nvmlDeviceGetClockInfo", 0, 50_000),
            UtilizationPercent: SensorMapping.Number(A, live.UtilizationPercent.Number, live.UtilizationPercent.Code, "nvml:nvmlDeviceGetUtilizationRates", 0, 100),
            MemoryUsedBytes: SensorMapping.Bytes(A, live.MemoryUsedBytes.Number, live.MemoryUsedBytes.Code, "nvml:nvmlDeviceGetMemoryInfo"),
            MemoryTotalBytes: SensorMapping.Bytes(A, live.MemoryTotalBytes.Number, live.MemoryTotalBytes.Code, "nvml:nvmlDeviceGetMemoryInfo"),
            PciBusId: slot.PciBusId);

        return new GpuSensorReading(
            slot.Id,
            slot.Name,
            SensorMapping.Number(A, live.TemperatureC.Number, live.TemperatureC.Code, "nvml:nvmlDeviceGetTemperature", -50, 150),
            SensorMapping.Number(A, live.PowerW.Number, live.PowerW.Code, "nvml:nvmlDeviceGetPowerUsage", 0, 2000),
            SensorMapping.NumberNote(A, live.PowerLimitW.Number, live.PowerLimitW.Code, "nvml:nvmlDeviceGetEnforcedPowerLimit", 0, 2000, "the limit in force now"),
            SensorMapping.NumberNote(A, live.FanPercent.Number, live.FanPercent.Code, "nvml:nvmlDeviceGetFanSpeed", 0, 100, "percent of the fan's top speed; 0 can mean the fans stopped at low load"),
            SensorMapping.Number(A, live.GraphicsMhz.Number, live.GraphicsMhz.Code, "nvml:nvmlDeviceGetClockInfo", 0, 10_000),
            extras);
    }

    private GpuSensorReading ReadAdlx(Slot slot)
    {
        const VendorApi A = VendorApi.Adlx;
        const string None = "adlx";
        var live = _adlx!.ReadLive(slot.Index);
        var power = SensorMapping.NumberNote(
            A,
            live.PowerW.Number,
            live.PowerW.Code,
            live.PowerIsBoardTotal ? "adlx:GPUTotalBoardPower" : "adlx:GPUPower",
            0,
            2000,
            live.PowerIsBoardTotal ? "power of the whole board" : "the AMD driver's GPU power value; the board total is not reported");

        var extras = new GpuSensorExtras(
            Vbios: Value.Unsupported<string>(None, "ADLX does not give the VBIOS version"),
            DriverVersion: Value.Unsupported<string>(None, "ADLX does not give the driver version"),
            PcieGeneration: Value.Unsupported<int>(None, "ADLX performance monitoring does not give the PCIe link"),
            PcieWidth: Value.Unsupported<int>(None, "ADLX performance monitoring does not give the PCIe link"),
            PcieMaxGeneration: Value.Unsupported<int>(None, "ADLX performance monitoring does not give the PCIe link"),
            PcieMaxWidth: Value.Unsupported<int>(None, "ADLX performance monitoring does not give the PCIe link"),
            PowerLimitMinWatts: Value.Unsupported<double>(None, "ADLX monitoring does not give the power limit"),
            PowerLimitMaxWatts: Value.Unsupported<double>(None, "ADLX monitoring does not give the power limit"),
            PowerLimitDefaultWatts: Value.Unsupported<double>(None, "ADLX monitoring does not give the power limit"),
            FanRpm: SensorMapping.Number(A, live.FanRpm.Number, live.FanRpm.Code, "adlx:GPUFanSpeed", 0, 30_000),
            MemoryClockMhz: SensorMapping.Number(A, live.MemoryMhz.Number, live.MemoryMhz.Code, "adlx:GPUVRAMClockSpeed", 0, 50_000),
            UtilizationPercent: Value.Unsupported<double>(None, "usage is read from the Windows counters for AMD GPUs"),
            MemoryUsedBytes: Value.Unsupported<long>(None, "memory use is read from the Windows counters for AMD GPUs"),
            MemoryTotalBytes: Value.Unsupported<long>(None, "memory size is read from the Windows counters for AMD GPUs"),
            PciBusId: slot.PciBusId);

        return new GpuSensorReading(
            slot.Id,
            slot.Name,
            SensorMapping.Number(A, live.TemperatureC.Number, live.TemperatureC.Code, "adlx:GPUTemperature", -50, 150),
            power,
            Value.Unsupported<double>(None, "ADLX monitoring does not give the power limit"),
            Value.Unsupported<double>(None, "AMD reports the fan speed in rpm, not percent"),
            SensorMapping.Number(A, live.GraphicsMhz.Number, live.GraphicsMhz.Code, "adlx:GPUClockSpeed", 0, 10_000),
            extras);
    }

    /// <summary>VBIOS, link maximum and limit range do not change while the driver is loaded: read once, and again only if a read failed for a reason that may pass.</summary>
    private NvmlStatic StaticOf(NvmlReader nvml, int index)
    {
        if (_nvmlStatic.TryGetValue(index, out var cached))
        {
            return cached;
        }

        var read = nvml.ReadStatic(index);
        if (Settled(read.Vbios.Code) && Settled(read.PcieMaxGeneration.Code) && Settled(read.PcieMaxWidth.Code)
            && Settled(read.PowerLimitMinW.Code) && Settled(read.PowerLimitMaxW.Code) && Settled(read.PowerLimitDefaultW.Code))
        {
            _nvmlStatic[index] = read;
        }

        return read;
    }

    /// <summary>Read, or "not supported" for good. Anything else (timeout, no permission yet, a driver hiccup) is worth asking again.</summary>
    private static bool Settled(int code) => SensorMapping.StatusOf(VendorApi.Nvml, code) is ValueStatus.Ok or ValueStatus.Unsupported;
}
