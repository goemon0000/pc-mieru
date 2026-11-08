using System.Runtime.InteropServices;

namespace PcMieru.Core.Hardware.Native;

internal sealed record DxgiAdapter(
    string Name,
    uint VendorId,
    uint DeviceId,
    uint SubsystemId,
    uint Revision,
    ulong DedicatedVideoBytes,
    ulong DedicatedSystemBytes,
    ulong SharedSystemBytes,
    uint LuidLow,
    int LuidHigh,
    uint Flags)
{
    public const uint SoftwareFlag = 2;

    public bool IsSoftware => (Flags & SoftwareFlag) != 0;

    /// <summary>Same spelling as the PDH GPU engine counters, so the two can be joined.</summary>
    public string LuidText => $"luid_0x{(uint)LuidHigh:x8}_0x{LuidLow:x8}";
}

/// <summary>PCI bus/device/function the kernel reports for an adapter, and whether Windows labels it as part of a hybrid pair.</summary>
internal sealed record KmtAdapterInfo(uint Bus, uint Device, uint Function, bool? HybridIntegrated, bool? HybridDiscrete, uint? TypeFlags = null)
{
    /// <summary>The kernel gives a paravirtualized or remote copy of a GPU an all-ones address instead of a PCI location.</summary>
    public bool HasPciAddress => Bus != uint.MaxValue && Device != ushort.MaxValue && Function != ushort.MaxValue;
}

internal static partial class DxgiNative
{
    [DllImport("dxgi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Desc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}

internal static partial class D3dKmt
{
    [StructLayout(LayoutKind.Sequential)]
    public struct OpenAdapterFromLuid
    {
        public uint LuidLow;
        public int LuidHigh;
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public IntPtr Data;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CloseAdapter
    {
        public uint Adapter;
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid args);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo args);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int D3DKMTCloseAdapter(ref CloseAdapter args);
}

/// <summary>
/// DXGI adapter list through the COM vtable (no interop assembly). Only read calls: CreateDXGIFactory1, EnumAdapters1, GetDesc1, Release.
/// </summary>
internal static unsafe class Dxgi
{
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const int ReleaseSlot = 2;
    private const int EnumAdapters1Slot = 12;
    private const int GetDesc1Slot = 10;
    private const uint KmtAdapterAddress = 6;
    private const uint KmtAdapterType = 15;

    public static IReadOnlyList<DxgiAdapter>? Enumerate(out int hresult)
    {
        var iid = IidFactory1;
        hresult = DxgiNative.CreateDXGIFactory1(ref iid, out var factory);
        if (hresult < 0 || factory == IntPtr.Zero)
        {
            return null;
        }

        var list = new List<DxgiAdapter>();
        try
        {
            var vtbl = *(IntPtr**)factory;
            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtbl[EnumAdapters1Slot];
            for (uint i = 0; i < 32; i++)
            {
                IntPtr adapter;
                var hr = enumAdapters(factory, i, &adapter);
                if (hr < 0)
                {
                    break;
                }

                try
                {
                    var avtbl = *(IntPtr**)adapter;
                    var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, DxgiNative.Desc1*, int>)avtbl[GetDesc1Slot];
                    DxgiNative.Desc1 d = default;
                    if (getDesc(adapter, &d) >= 0)
                    {
                        list.Add(new DxgiAdapter(
                            new string(d.Description).TrimEnd('\0').Trim(),
                            d.VendorId,
                            d.DeviceId,
                            d.SubSysId,
                            d.Revision,
                            d.DedicatedVideoMemory,
                            d.DedicatedSystemMemory,
                            d.SharedSystemMemory,
                            d.LuidLow,
                            d.LuidHigh,
                            d.Flags));
                    }
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)adapter)[ReleaseSlot])(adapter);
                }
            }
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(factory)[ReleaseSlot])(factory);
        }

        return list;
    }

    private static IntPtr* Vtbl(IntPtr obj) => *(IntPtr**)obj;

    /// <summary>PCI address (and hybrid flags) from the display kernel driver. Read-only queries on an adapter opened by LUID.</summary>
    public static KmtAdapterInfo? QueryKmt(uint luidLow, int luidHigh)
    {
        var open = new D3dKmt.OpenAdapterFromLuid { LuidLow = luidLow, LuidHigh = luidHigh };
        if (D3dKmt.D3DKMTOpenAdapterFromLuid(ref open) != 0 || open.Adapter == 0)
        {
            return null;
        }

        try
        {
            var addr = stackalloc uint[3];
            var q = new D3dKmt.QueryAdapterInfo { Adapter = open.Adapter, Type = (int)KmtAdapterAddress, Data = (IntPtr)addr, Size = 12 };
            if (D3dKmt.D3DKMTQueryAdapterInfo(ref q) != 0)
            {
                return null;
            }

            uint bus = addr[0], device = addr[1], function = addr[2];
            uint? flags = null;
            bool? integrated = null;
            bool? discrete = null;
            var typeBuf = stackalloc uint[1];
            typeBuf[0] = 0;
            var qt = new D3dKmt.QueryAdapterInfo { Adapter = open.Adapter, Type = (int)KmtAdapterType, Data = (IntPtr)typeBuf, Size = 4 };
            if (D3dKmt.D3DKMTQueryAdapterInfo(ref qt) == 0)
            {
                // D3DKMT_ADAPTERTYPE flags: bit 4 HybridDiscrete, bit 5 HybridIntegrated. Only set on hybrid-graphics systems.
                flags = typeBuf[0];
                discrete = (typeBuf[0] & 0x10) != 0 ? true : null;
                integrated = (typeBuf[0] & 0x20) != 0 ? true : null;
            }

            return new KmtAdapterInfo(bus, device, function, integrated, discrete, flags);
        }
        finally
        {
            var close = new D3dKmt.CloseAdapter { Adapter = open.Adapter };
            D3dKmt.D3DKMTCloseAdapter(ref close);
        }
    }
}
