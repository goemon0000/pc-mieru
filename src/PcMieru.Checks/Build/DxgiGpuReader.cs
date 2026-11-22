// Provenance: PartsLife.Checks.Gpu.GpuAdapters (pl-cloud 25e4571, src/PartsLife.Checks/Gpu/GpuAdapters.cs), MIT, same owner. The DXGI walk, the
// layout of DXGI_ADAPTER_DESC1, the PCI address from D3DKMT, the skip of the software adapter and the removal of aliases are the old ones.
//
// Why this is a port and not PcMieru.Core.Collection.GpuAdapters: Core's list has no PCI address, and the configuration code needs it (an
// adapter without one is an alias, see GpuAliasRule); Core also keeps the software adapter, caps the dedicated memory at long.MaxValue and
// has no D3DKMT. So Core's list is not the same on the same machine, and the old walk is kept. What is new: the walk runs on a lane of its
// own with the deadline of the category, so a driver that does not answer cannot hold the collector.
using System.Runtime.InteropServices;
using PcMieru.Checks.SystemInfo;

namespace PcMieru.Checks.Build;

/// <summary>Reads the graphics adapters (DXGI) for the configuration code, within a deadline. A seam: the tests give their own.</summary>
internal interface IGpuReader
{
    /// <summary>All the adapters that are not the software one, with the aliases of a virtual display removed. No adapter is an Ok empty list.</summary>
    Task<CheckReading<IReadOnlyList<RawGpu>>> ReadAsync(QueryDeadline deadline, CancellationToken ct);
}

/// <summary>One adapter as DXGI lists it. The PCI address is null when D3DKMT gives none (a virtual or software adapter).</summary>
internal sealed record DxgiAdapter(string Name, uint VendorId, ulong DedicatedBytes, uint LuidLow, int LuidHigh, uint? PciBus, uint? PciDevice);

internal sealed unsafe partial class DxgiGpuReader : IGpuReader
{
    internal const int SoftwareFlag = 2;
    private const int Desc1Size = 312;
    private const int KmtqAiTypeAdapterAddress = 6;
    private const int MaxAdapters = 64;

    private static readonly QueryLane Lane = new("Dxgi");

    public Task<CheckReading<IReadOnlyList<RawGpu>>> ReadAsync(QueryDeadline deadline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        return Lane.RunAsync(job => ReadOnThisThread(job, deadline), deadline, ct);
    }

    private static CheckReading<IReadOnlyList<RawGpu>> ReadOnThisThread(LaneJob job, QueryDeadline deadline)
    {
        var adapters = Enumerate(() => job.IsAbandoned || deadline.IsExpired, out var cut);
        if (cut)
        {
            return job.IsAbandoned ? CheckReading<IReadOnlyList<RawGpu>>.Stopped() : CheckReading<IReadOnlyList<RawGpu>>.TimedOut();
        }

        IReadOnlyList<RawGpu> gpus = adapters.Select(a => new RawGpu(a.Name, a.DedicatedBytes, a.PciBus is not null)).ToList();
        return CheckReading<IReadOnlyList<RawGpu>>.Ok(gpus);
    }

    /// <summary>
    /// The adapters in DXGI order, without the software adapter and without aliases. Empty (never an exception) when DXGI is missing or
    /// fails. <paramref name="stop"/> is asked between adapters; when it says yes the walk ends and <paramref name="cut"/> is true.
    /// </summary>
    internal static List<DxgiAdapter> Enumerate(Func<bool> stop, out bool cut)
    {
        cut = false;
        var list = new List<DxgiAdapter>();
        nint factory = 0;
        try
        {
            var iid = FactoryIid;
            if (CreateDXGIFactory1(&iid, &factory) < 0)
            {
                return list;
            }

            byte* desc = stackalloc byte[Desc1Size];
            for (uint i = 0; i < MaxAdapters; i++)
            {
                if (stop())
                {
                    cut = true;
                    return list;
                }

                nint adapter;
                // IDXGIFactory1::EnumAdapters1 = 12
                int hr = ((delegate* unmanaged<nint, uint, nint*, int>)Slot(factory, 12))(factory, i, &adapter);
                if (hr < 0)
                {
                    break; // DXGI_ERROR_NOT_FOUND ends the list
                }

                try
                {
                    new Span<byte>(desc, Desc1Size).Clear();
                    // IDXGIAdapter1::GetDesc1 = 10
                    if (((delegate* unmanaged<nint, byte*, int>)Slot(adapter, 10))(adapter, desc) < 0)
                    {
                        continue;
                    }

                    var d = ParseDesc1(new ReadOnlySpan<byte>(desc, Desc1Size));
                    if ((d.Flags & SoftwareFlag) != 0)
                    {
                        continue; // the Basic Render Driver
                    }

                    var (bus, dev) = PciAddress(d.LuidLow, d.LuidHigh);
                    list.Add(new DxgiAdapter(d.Name, d.VendorId, d.Dedicated, d.LuidLow, d.LuidHigh, bus, dev));
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // no DXGI: whatever was listed so far stays, as before
        }
        finally
        {
            if (factory != 0)
            {
                Release(factory);
            }
        }

        return Dedupe(list);
    }

    /// <summary>The same card shown again under another LUID by a virtual display driver is taken away (see <see cref="GpuAliasRule"/>).</summary>
    internal static List<DxgiAdapter> Dedupe(List<DxgiAdapter> list) =>
        GpuAliasRule.WithoutAliases(list, a => a.Name, a => a.DedicatedBytes, a => a.PciBus is not null);

    internal readonly record struct Desc1(string Name, uint VendorId, ulong Dedicated, uint LuidLow, int LuidHigh, uint Flags);

    /// <summary>
    /// DXGI_ADAPTER_DESC1: Description[128 WCHAR] 0, VendorId 256, DeviceId 260, SubSysId 264, Revision 268,
    /// DedicatedVideoMemory 272, DedicatedSystemMemory 280, SharedSystemMemory 288, AdapterLuid 296, Flags 304.
    /// </summary>
    internal static Desc1 ParseDesc1(ReadOnlySpan<byte> d)
    {
        var chars = MemoryMarshal.Cast<byte, char>(d[..256]);
        int end = chars.IndexOf('\0');
        string name = new string(end < 0 ? chars : chars[..end]).Trim();
        return new Desc1(
            name,
            BitConverter.ToUInt32(d[256..]),
            BitConverter.ToUInt64(d[272..]),
            BitConverter.ToUInt32(d[296..]),
            BitConverter.ToInt32(d[300..]),
            BitConverter.ToUInt32(d[304..]));
    }

    /// <summary>The PCI bus and device number of a LUID (D3DKMT; no administrator needed). Null, null when there is none.</summary>
    private static (uint?, uint?) PciAddress(uint luidLow, int luidHigh)
    {
        try
        {
            var open = new D3DKMT_OPENADAPTERFROMLUID { LuidLow = luidLow, LuidHigh = luidHigh };
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0)
            {
                return (null, null);
            }

            try
            {
                var addr = new D3DKMT_ADAPTERADDRESS();
                var q = new D3DKMT_QUERYADAPTERINFO
                {
                    hAdapter = open.hAdapter,
                    Type = KmtqAiTypeAdapterAddress,
                    pPrivateDriverData = (nint)(&addr),
                    PrivateDriverDataSize = (uint)sizeof(D3DKMT_ADAPTERADDRESS),
                };
                if (D3DKMTQueryAdapterInfo(ref q) != 0)
                {
                    return (null, null);
                }

                // An adapter with no address (virtual, software) gets 0xFFFFFFFF back.
                if (addr.BusNumber == uint.MaxValue || addr.DeviceNumber is 0xFFFF or uint.MaxValue)
                {
                    return (null, null);
                }

                return (addr.BusNumber, addr.DeviceNumber);
            }
            finally
            {
                var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                _ = D3DKMTCloseAdapter(ref close);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (null, null);
        }
    }

    private static void* Slot(nint obj, int index) => (*(void***)obj)[index];

    private static void Release(nint obj)
    {
        if (obj != 0)
        {
            _ = ((delegate* unmanaged<nint, uint>)Slot(obj, 2))(obj);
        }
    }

    private static readonly Guid FactoryIid = new("770aae78-f26f-4dba-a829-253c83d1b387"); // IID_IDXGIFactory1

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_OPENADAPTERFROMLUID
    {
        public uint LuidLow;
        public int LuidHigh;
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERADDRESS
    {
        public uint BusNumber;
        public uint DeviceNumber;
        public uint FunctionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public nint pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID p);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO p);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER p);

    [LibraryImport("dxgi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateDXGIFactory1(Guid* riid, nint* factory);
}
