// Adapted from goemon0000/parts-life (MIT) src/PartsLife.Checks/Gpu/Nvml.cs
// Changes for PC Mieru: the library is loaded from System32 or the driver store by full path only; more read-only functions (VBIOS, driver
// version, PCIe link, power limit range, memory total); results carry the NVML result code; no thread guard because this whole process
// is thrown away by its parent when a call hangs.

using System.Runtime.InteropServices;

namespace PcMieru.SensorHost;

/// <summary>A GPU as NVML lists it. The PCI position is what joins it to the display adapter Windows lists.</summary>
internal sealed record NvmlDevice(int Index, string Name, uint Domain, uint Bus, uint Device);

/// <summary>What changes from second to second. Power in W, memory in bytes, clocks in MHz.</summary>
internal sealed record NvmlLive(
    VendorNumber TemperatureC,
    VendorNumber GraphicsMhz,
    VendorNumber MemoryMhz,
    VendorNumber PowerW,
    VendorNumber PowerLimitW,
    VendorNumber FanPercent,
    VendorNumber UtilizationPercent,
    VendorNumber MemoryUsedBytes,
    VendorNumber MemoryTotalBytes,
    VendorNumber PcieGeneration,
    VendorNumber PcieWidth);

/// <summary>What does not change while the driver is loaded.</summary>
internal sealed record NvmlStatic(
    VendorText Vbios,
    VendorNumber PcieMaxGeneration,
    VendorNumber PcieMaxWidth,
    VendorNumber PowerLimitMinW,
    VendorNumber PowerLimitMaxW,
    VendorNumber PowerLimitDefaultW,
    VendorNumber TemperatureSlowdownC);

/// <summary>
/// Reads NVIDIA GPUs through nvml.dll, which the NVIDIA driver installs. No administrator rights are needed. Only reading functions are
/// called: nothing here can change a clock, a power limit or a fan. The functions beyond the basic six are optional (older drivers lack
/// some), and a missing one makes just that value "not supported" instead of failing everything. The library is never unloaded: the process
/// ends instead, which is safe even when a call inside the driver never returns.
/// </summary>
internal sealed unsafe class NvmlReader
{
    private readonly delegate* unmanaged<uint*, int> _count;
    private readonly delegate* unmanaged<uint, IntPtr*, int> _handle;
    private readonly delegate* unmanaged<IntPtr, byte*, uint, int> _name;
    private readonly delegate* unmanaged<IntPtr, byte*, int> _pci;
    private readonly delegate* unmanaged<IntPtr, int, uint*, int> _temperature;
    private readonly delegate* unmanaged<IntPtr, int, uint*, int> _temperatureThreshold;
    private readonly delegate* unmanaged<IntPtr, int, uint*, int> _clock;
    private readonly delegate* unmanaged<IntPtr, uint*, int> _power;
    private readonly delegate* unmanaged<IntPtr, uint*, int> _enforcedLimit;
    private readonly delegate* unmanaged<IntPtr, uint*, int> _defaultLimit;
    private readonly delegate* unmanaged<IntPtr, uint*, uint*, int> _limitRange;
    private readonly delegate* unmanaged<IntPtr, uint*, int> _fan;
    private readonly delegate* unmanaged<IntPtr, uint*, int> _utilization;
    private readonly delegate* unmanaged<IntPtr, ulong*, int> _memory;
    private readonly delegate* unmanaged<IntPtr, uint*, int> _pcieGeneration;
    private readonly delegate* unmanaged<IntPtr, uint*, int> _pcieWidth;
    private readonly delegate* unmanaged<IntPtr, uint*, int> _pcieMaxGeneration;
    private readonly delegate* unmanaged<IntPtr, uint*, int> _pcieMaxWidth;
    private readonly delegate* unmanaged<IntPtr, byte*, uint, int> _vbios;
    private readonly delegate* unmanaged<byte*, uint, int> _driverVersion;

    private NvmlReader(IntPtr lib, out int initResult)
    {
        _count = (delegate* unmanaged<uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetCount_v2");
        _handle = (delegate* unmanaged<uint, IntPtr*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetHandleByIndex_v2");
        _name = (delegate* unmanaged<IntPtr, byte*, uint, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetName");
        _pci = (delegate* unmanaged<IntPtr, byte*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetPciInfo_v3");
        _temperature = (delegate* unmanaged<IntPtr, int, uint*, int>)Optional(lib, "nvmlDeviceGetTemperature");
        _temperatureThreshold = (delegate* unmanaged<IntPtr, int, uint*, int>)Optional(lib, "nvmlDeviceGetTemperatureThreshold");
        _clock = (delegate* unmanaged<IntPtr, int, uint*, int>)Optional(lib, "nvmlDeviceGetClockInfo");
        _power = (delegate* unmanaged<IntPtr, uint*, int>)Optional(lib, "nvmlDeviceGetPowerUsage");
        _enforcedLimit = (delegate* unmanaged<IntPtr, uint*, int>)Optional(lib, "nvmlDeviceGetEnforcedPowerLimit");
        _defaultLimit = (delegate* unmanaged<IntPtr, uint*, int>)Optional(lib, "nvmlDeviceGetPowerManagementDefaultLimit");
        _limitRange = (delegate* unmanaged<IntPtr, uint*, uint*, int>)Optional(lib, "nvmlDeviceGetPowerManagementLimitConstraints");
        _fan = (delegate* unmanaged<IntPtr, uint*, int>)Optional(lib, "nvmlDeviceGetFanSpeed");
        _utilization = (delegate* unmanaged<IntPtr, uint*, int>)Optional(lib, "nvmlDeviceGetUtilizationRates");
        _memory = (delegate* unmanaged<IntPtr, ulong*, int>)Optional(lib, "nvmlDeviceGetMemoryInfo");
        _pcieGeneration = (delegate* unmanaged<IntPtr, uint*, int>)Optional(lib, "nvmlDeviceGetCurrPcieLinkGeneration");
        _pcieWidth = (delegate* unmanaged<IntPtr, uint*, int>)Optional(lib, "nvmlDeviceGetCurrPcieLinkWidth");
        _pcieMaxGeneration = (delegate* unmanaged<IntPtr, uint*, int>)Optional(lib, "nvmlDeviceGetMaxPcieLinkGeneration");
        _pcieMaxWidth = (delegate* unmanaged<IntPtr, uint*, int>)Optional(lib, "nvmlDeviceGetMaxPcieLinkWidth");
        _vbios = (delegate* unmanaged<IntPtr, byte*, uint, int>)Optional(lib, "nvmlDeviceGetVbiosVersion");
        _driverVersion = (delegate* unmanaged<byte*, uint, int>)Optional(lib, "nvmlSystemGetDriverVersion");
        var init = (delegate* unmanaged<int>)NativeLibrary.GetExport(lib, "nvmlInit_v2");
        initResult = init();
    }

    /// <summary>The path of the library that was loaded, for the diagnostic line.</summary>
    public string? LoadedFrom { get; private init; }

    /// <summary>Opens NVML. Null (with the reason) when there is no NVIDIA driver, the library is too old, or it refuses to start.</summary>
    public static NvmlReader? TryOpen(out string reason)
    {
        var nvsmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
        var candidates = VendorLibraries.Candidates("nvml.dll", ["nv*.inf_amd64_*"], nvsmi);
        if (!VendorLibraries.TryLoad(candidates, out var lib, out var path))
        {
            reason = "nvml.dll was not found (no NVIDIA driver)";
            return null;
        }

        try
        {
            var reader = new NvmlReader(lib, out var result) { LoadedFrom = path };
            if (result != 0)
            {
                reason = $"NVML did not start (result {result})";
                return null;
            }

            reason = string.Empty;
            return reader;
        }
        catch (EntryPointNotFoundException)
        {
            NativeLibrary.Free(lib);
            reason = "nvml.dll is too old (missing basic functions)";
            return null;
        }
    }

    public IReadOnlyList<NvmlDevice> Devices()
    {
        var list = new List<NvmlDevice>();
        uint count = 0;
        if (_count(&count) != 0)
        {
            return list;
        }

        var name = stackalloc byte[96];
        var pci = stackalloc byte[128];
        for (uint i = 0; i < count && i < 16; i++)
        {
            IntPtr h;
            if (_handle(i, &h) != 0)
            {
                continue;
            }

            new Span<byte>(name, 96).Clear();
            var text = _name(h, name, 96) == 0 ? Marshal.PtrToStringUTF8((IntPtr)name) ?? string.Empty : string.Empty;

            // nvmlPciInfo_t: busIdLegacy[16], domain, bus, device, pciDeviceId, pciSubSystemId, busId[32]
            new Span<byte>(pci, 128).Clear();
            if (_pci(h, pci) != 0)
            {
                continue;
            }

            list.Add(new NvmlDevice((int)i, text, *(uint*)(pci + 16), *(uint*)(pci + 20), *(uint*)(pci + 24)));
        }

        return list;
    }

    public VendorText DriverVersion()
    {
        if (_driverVersion == null)
        {
            return VendorText.Missing;
        }

        var buffer = stackalloc byte[80];
        new Span<byte>(buffer, 80).Clear();
        var rc = _driverVersion(buffer, 80);
        return rc == 0 ? VendorText.Of(Marshal.PtrToStringUTF8((IntPtr)buffer)) : VendorText.Failed(rc);
    }

    public NvmlStatic ReadStatic(int index)
    {
        IntPtr h;
        var rc = _handle((uint)index, &h);
        if (rc != 0)
        {
            var failed = VendorNumber.Failed(rc);
            return new NvmlStatic(VendorText.Failed(rc), failed, failed, failed, failed, failed, failed);
        }

        VendorText vbios;
        if (_vbios == null)
        {
            vbios = VendorText.Missing;
        }
        else
        {
            var buffer = stackalloc byte[64];
            new Span<byte>(buffer, 64).Clear();
            var r = _vbios(h, buffer, 64);
            vbios = r == 0 ? VendorText.Of(Marshal.PtrToStringUTF8((IntPtr)buffer)) : VendorText.Failed(r);
        }

        VendorNumber min, max;
        if (_limitRange == null)
        {
            min = max = VendorNumber.Missing;
        }
        else
        {
            var range = stackalloc uint[2];
            var r = _limitRange(h, &range[0], &range[1]);
            min = r == 0 ? VendorNumber.Of(range[0] / 1000.0) : VendorNumber.Failed(r);
            max = r == 0 ? VendorNumber.Of(range[1] / 1000.0) : VendorNumber.Failed(r);
        }

        return new NvmlStatic(
            vbios,
            Uint(_pcieMaxGeneration, h),
            Uint(_pcieMaxWidth, h),
            min,
            max,
            Uint(_defaultLimit, h, 1000.0),
            WithArgument(_temperatureThreshold, h, 1)); // NVML_TEMPERATURE_THRESHOLD_SLOWDOWN
    }

    public NvmlLive ReadLive(int index)
    {
        IntPtr h;
        var rc = _handle((uint)index, &h);
        if (rc != 0)
        {
            var failed = VendorNumber.Failed(rc);
            return new NvmlLive(failed, failed, failed, failed, failed, failed, failed, failed, failed, failed, failed);
        }

        VendorNumber used, total;
        if (_memory == null)
        {
            used = total = VendorNumber.Missing;
        }
        else
        {
            // nvmlMemory_t: total, free, used
            var memory = stackalloc ulong[3];
            var r = _memory(h, memory);
            total = r == 0 ? VendorNumber.Of(memory[0]) : VendorNumber.Failed(r);
            used = r == 0 ? VendorNumber.Of(memory[2]) : VendorNumber.Failed(r);
        }

        VendorNumber utilization;
        if (_utilization == null)
        {
            utilization = VendorNumber.Missing;
        }
        else
        {
            // nvmlUtilization_t: gpu, memory
            var rates = stackalloc uint[2];
            var r = _utilization(h, rates);
            utilization = r == 0 ? VendorNumber.Of(rates[0]) : VendorNumber.Failed(r);
        }

        return new NvmlLive(
            WithArgument(_temperature, h, 0), // NVML_TEMPERATURE_GPU
            WithArgument(_clock, h, 0), // NVML_CLOCK_GRAPHICS
            WithArgument(_clock, h, 2), // NVML_CLOCK_MEM
            Uint(_power, h, 1000.0), // milliwatts
            Uint(_enforcedLimit, h, 1000.0),
            Uint(_fan, h),
            utilization,
            used,
            total,
            Uint(_pcieGeneration, h),
            Uint(_pcieWidth, h));
    }

    private static void* Optional(IntPtr lib, string name) => NativeLibrary.TryGetExport(lib, name, out var p) ? (void*)p : null;

    private static VendorNumber Uint(delegate* unmanaged<IntPtr, uint*, int> function, IntPtr handle, double divisor = 1.0)
    {
        if (function == null)
        {
            return VendorNumber.Missing;
        }

        uint value = 0;
        var rc = function(handle, &value);
        return rc == 0 ? VendorNumber.Of(value / divisor) : VendorNumber.Failed(rc);
    }

    private static VendorNumber WithArgument(delegate* unmanaged<IntPtr, int, uint*, int> function, IntPtr handle, int argument)
    {
        if (function == null)
        {
            return VendorNumber.Missing;
        }

        uint value = 0;
        var rc = function(handle, argument, &value);
        return rc == 0 ? VendorNumber.Of(value) : VendorNumber.Failed(rc);
    }
}
