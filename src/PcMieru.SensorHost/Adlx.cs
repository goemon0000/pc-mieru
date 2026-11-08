// Adapted from goemon0000/parts-life (MIT) src/PartsLife.Checks/Recording/Sources.Adlx.cs
// Changes for PC Mieru: the library is loaded from System32 or the driver store by full path only; the reader returns the ADLX result code of
// every value instead of a yes/no; GPUs are paired to display adapters by SensorMapping.PairByName in the sampler; the same vtable slots
// (checked against AMD's public ADLX headers by the original) and read-only calls only.

using System.Runtime.InteropServices;

namespace PcMieru.SensorHost;

internal sealed record AdlxGpu(int Index, string Name);

/// <summary>What AMD's performance monitoring gives for one GPU right now. Power is the whole board's when the driver reports that.</summary>
internal sealed record AdlxLive(
    VendorNumber TemperatureC,
    VendorNumber HotspotC,
    VendorNumber GraphicsMhz,
    VendorNumber MemoryMhz,
    VendorNumber PowerW,
    bool PowerIsBoardTotal,
    VendorNumber FanRpm);

/// <summary>
/// Reads AMD GPUs through amdadlx64.dll (ADLX), which the AMD driver installs. No administrator rights; only the performance-monitoring
/// part of ADLX is called (no tuning, no settings). ADLX is a C vtable interface: the slot numbers below follow the SDK's declaration order.
/// IADLXSystem: 1 GetGPUs, 9 GetPerformanceMonitoringServices. IADLXGPUList: 3 Size, 11 At. IADLXGPU: 7 Name.
/// IADLXPerformanceMonitoringServices: 18 GetCurrentGPUMetrics. IADLXGPUMetrics: 5 GPUClockSpeed, 6 GPUVRAMClockSpeed, 7 GPUTemperature,
/// 8 GPUHotspotTemperature, 9 GPUPower, 10 GPUTotalBoardPower, 11 GPUFanSpeed. Every interface: slot 1 is Release.
/// The library is never freed: ADLX may keep threads of its own.
/// </summary>
internal sealed unsafe class AdlxReader
{
    private const int GetGpusSlot = 1;
    private const int GetPerformanceServicesSlot = 9;
    private const int GpuListSizeSlot = 3;
    private const int GpuListAtSlot = 11;
    private const int GpuNameSlot = 7;
    private const int GetCurrentMetricsSlot = 18;
    private const int ReleaseSlot = 1;
    private const int ClockSlot = 5;
    private const int MemoryClockSlot = 6;
    private const int TemperatureSlot = 7;
    private const int HotspotSlot = 8;
    private const int PowerSlot = 9;
    private const int BoardPowerSlot = 10;
    private const int FanSlot = 11;

    private readonly IntPtr _perf;
    private readonly List<IntPtr> _gpus;

    private AdlxReader(IntPtr perf, List<IntPtr> gpus, List<AdlxGpu> described, string? loadedFrom)
    {
        _perf = perf;
        _gpus = gpus;
        Gpus = described;
        LoadedFrom = loadedFrom;
    }

    public IReadOnlyList<AdlxGpu> Gpus { get; }

    public string? LoadedFrom { get; }

    // ADLX_RESULT: 0 OK, 1 already enabled, 2 already initialized.
    private static bool Succeeded(int rc) => rc is 0 or 1 or 2;

    /// <summary>Opens ADLX. Null (with the reason) when there is no AMD driver, ADLX refuses to start, or it has no performance monitoring.</summary>
    public static AdlxReader? TryOpen(out string reason)
    {
        var candidates = VendorLibraries.Candidates("amdadlx64.dll", ["u*.inf_amd64_*", "c0*.inf_amd64_*", "amd*.inf_amd64_*"]);
        if (!VendorLibraries.TryLoad(candidates, out var lib, out var path))
        {
            reason = "amdadlx64.dll was not found (no AMD driver)";
            return null;
        }

        if (!NativeLibrary.TryGetExport(lib, "ADLXInitialize", out var initPtr))
        {
            reason = "amdadlx64.dll does not export ADLXInitialize";
            return null;
        }

        // Ask for exactly the version the driver speaks (a newer one is refused). Version 1.0.0.0 if it cannot be asked.
        var version = 1UL << 48;
        if (NativeLibrary.TryGetExport(lib, "ADLXQueryFullVersion", out var queryVersion))
        {
            ulong v = 0;
            if (((delegate* unmanaged<ulong*, int>)queryVersion)(&v) == 0 && v != 0)
            {
                version = v;
            }
        }

        var system = IntPtr.Zero;
        var rc = ((delegate* unmanaged<ulong, IntPtr*, int>)initPtr)(version, &system);
        if (!Succeeded(rc) || system == IntPtr.Zero)
        {
            reason = $"ADLX did not start (result {rc})";
            return null;
        }

        var perf = IntPtr.Zero;
        if (!Succeeded(((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(system, GetPerformanceServicesSlot))(system, &perf)) || perf == IntPtr.Zero)
        {
            reason = "this ADLX has no performance monitoring";
            return null;
        }

        var gpus = new List<IntPtr>();
        var described = new List<AdlxGpu>();
        var list = IntPtr.Zero;
        if (Succeeded(((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(system, GetGpusSlot))(system, &list)) && list != IntPtr.Zero)
        {
            try
            {
                var count = ((delegate* unmanaged<IntPtr, uint>)Slot(list, GpuListSizeSlot))(list);
                for (uint i = 0; i < count && i < 16; i++)
                {
                    var gpu = IntPtr.Zero;
                    if (!Succeeded(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Slot(list, GpuListAtSlot))(list, i, &gpu)) || gpu == IntPtr.Zero)
                    {
                        continue;
                    }

                    byte* name = null;
                    var text = Succeeded(((delegate* unmanaged<IntPtr, byte**, int>)Slot(gpu, GpuNameSlot))(gpu, &name)) && name != null
                        ? Marshal.PtrToStringUTF8((IntPtr)name) ?? string.Empty
                        : string.Empty;
                    described.Add(new AdlxGpu(gpus.Count, text));
                    gpus.Add(gpu);
                }
            }
            finally
            {
                Release(list);
            }
        }

        reason = string.Empty;
        return new AdlxReader(perf, gpus, described, path);
    }

    public AdlxLive ReadLive(int index)
    {
        if (index < 0 || index >= _gpus.Count)
        {
            var gone = VendorNumber.Failed(14);
            return new AdlxLive(gone, gone, gone, gone, gone, false, gone);
        }

        var metrics = IntPtr.Zero;
        var rc = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Slot(_perf, GetCurrentMetricsSlot))(_perf, _gpus[index], &metrics);
        if (!Succeeded(rc) || metrics == IntPtr.Zero)
        {
            // rc 0 with no object is not a state ADLX documents; report it as a failure, never as a value.
            var failed = VendorNumber.Failed(rc is 0 or 1 or 2 ? 3 : rc);
            return new AdlxLive(failed, failed, failed, failed, failed, false, failed);
        }

        try
        {
            var board = Double(metrics, BoardPowerSlot);
            var power = board.Ok ? board : Double(metrics, PowerSlot);
            return new AdlxLive(
                Double(metrics, TemperatureSlot),
                Double(metrics, HotspotSlot),
                Int(metrics, ClockSlot),
                Int(metrics, MemoryClockSlot),
                power,
                board.Ok,
                Int(metrics, FanSlot));
        }
        finally
        {
            Release(metrics);
        }
    }

    private static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    private static VendorNumber Double(IntPtr obj, int slot)
    {
        double value = 0;
        var rc = ((delegate* unmanaged<IntPtr, double*, int>)Slot(obj, slot))(obj, &value);
        return Succeeded(rc) ? VendorNumber.Of(value) : VendorNumber.Failed(rc);
    }

    private static VendorNumber Int(IntPtr obj, int slot)
    {
        var value = 0;
        var rc = ((delegate* unmanaged<IntPtr, int*, int>)Slot(obj, slot))(obj, &value);
        return Succeeded(rc) ? VendorNumber.Of(value) : VendorNumber.Failed(rc);
    }

    private static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, int>)Slot(obj, ReleaseSlot))(obj);
        }
    }
}
