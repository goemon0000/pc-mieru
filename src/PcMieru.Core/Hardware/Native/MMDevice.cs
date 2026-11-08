using System.Runtime.InteropServices;

namespace PcMieru.Core.Hardware.Native;

/// <summary>One audio endpoint as the Windows Core Audio device list describes it. Nothing is opened or recorded.</summary>
internal sealed record RawAudioEndpoint(
    string Id,
    string? Name,
    string? Adapter,
    bool IsInput,
    uint State,
    bool IsDefault,
    uint? FormFactor,
    byte[]? DeviceFormat);

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid fmtid, uint pid)
{
    public Guid Fmtid = fmtid;
    public uint Pid = pid;
}

/// <summary>PROPVARIANT, 64-bit layout (24 bytes). Only the members the endpoint properties use.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    public const ushort VtUi4 = 19;
    public const ushort VtLpwstr = 31;
    public const ushort VtBlob = 65;

    [FieldOffset(0)]
    public ushort Vt;

    [FieldOffset(8)]
    public uint UInt;

    [FieldOffset(8)]
    public IntPtr Pointer;

    [FieldOffset(16)]
    public IntPtr BlobData;
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);

    [PreserveSig]
    int OpenPropertyStore(uint access, out IPropertyStore properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out uint state);
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int Commit();
}

internal static partial class Ole32Native
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int PropVariantClear(ref PropVariant value);
}

/// <summary>Reads the Core Audio endpoint list (MMDevice API). Property reads only: no audio client is activated, so nothing records.</summary>
internal static class MMDevice
{
    public const uint StateActive = 0x1;
    public const uint StateDisabled = 0x2;
    public const uint StateNotPresent = 0x4;
    public const uint StateUnplugged = 0x8;

    private static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid DeviceKeys = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly PropertyKey FriendlyName = new(DeviceKeys, 14);
    private static readonly PropertyKey InterfaceFriendlyName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    private static readonly PropertyKey FormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
    private static readonly PropertyKey DeviceFormat = new(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);

    /// <summary>Active and unplugged endpoints, render then capture. Null with the HRESULT when the audio service cannot be reached.</summary>
    public static List<RawAudioEndpoint>? Enumerate(out int hresult)
    {
        hresult = 0;
        if (IntPtr.Size != 8)
        {
            hresult = unchecked((int)0x80004001);
            return null;
        }

        IMMDeviceEnumerator? enumerator = null;
        try
        {
            var type = Type.GetTypeFromCLSID(EnumeratorClass);
            if (type is null || Activator.CreateInstance(type) is not IMMDeviceEnumerator created)
            {
                hresult = unchecked((int)0x80040154);
                return null;
            }

            enumerator = created;
            var list = new List<RawAudioEndpoint>();
            foreach (var input in new[] { false, true })
            {
                var flow = input ? 1 : 0;
                var defaultId = DefaultId(enumerator, flow);
                hresult = enumerator.EnumAudioEndpoints(flow, StateActive | StateUnplugged, out var devices);
                if (hresult < 0 || devices is null)
                {
                    return hresult < 0 ? null : list;
                }

                try
                {
                    devices.GetCount(out var count);
                    for (uint i = 0; i < count; i++)
                    {
                        if (devices.Item(i, out var device) < 0 || device is null)
                        {
                            continue;
                        }

                        try
                        {
                            list.Add(Read(device, input, defaultId));
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(device);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(devices);
                }
            }

            hresult = 0;
            return list;
        }
        catch (COMException ex)
        {
            hresult = ex.HResult;
            return null;
        }
        finally
        {
            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    private static string? DefaultId(IMMDeviceEnumerator enumerator, int flow)
    {
        // Role 0 = console: the device Windows uses unless an app asks for another one.
        if (enumerator.GetDefaultAudioEndpoint(flow, 0, out var device) < 0 || device is null)
        {
            return null;
        }

        try
        {
            return device.GetId(out var id) < 0 ? null : id;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private static RawAudioEndpoint Read(IMMDevice device, bool input, string? defaultId)
    {
        device.GetId(out var id);
        device.GetState(out var state);
        string? name = null;
        string? adapter = null;
        uint? factor = null;
        byte[]? format = null;
        if (device.OpenPropertyStore(0, out var store) >= 0 && store is not null)
        {
            try
            {
                name = ReadString(store, FriendlyName);
                adapter = ReadString(store, InterfaceFriendlyName);
                factor = ReadUInt(store, FormFactor);
                format = ReadBlob(store, DeviceFormat);
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        return new RawAudioEndpoint(id ?? string.Empty, name, adapter, input, state, id is not null && id == defaultId, factor, format);
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var v) < 0)
        {
            return null;
        }

        try
        {
            return v.Vt == PropVariant.VtLpwstr && v.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(v.Pointer) : null;
        }
        finally
        {
            Ole32Native.PropVariantClear(ref v);
        }
    }

    private static uint? ReadUInt(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var v) < 0)
        {
            return null;
        }

        try
        {
            return v.Vt == PropVariant.VtUi4 ? v.UInt : null;
        }
        finally
        {
            Ole32Native.PropVariantClear(ref v);
        }
    }

    private static byte[]? ReadBlob(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var v) < 0)
        {
            return null;
        }

        try
        {
            if (v.Vt != PropVariant.VtBlob || v.BlobData == IntPtr.Zero || v.UInt is 0 or > 4096)
            {
                return null;
            }

            var bytes = new byte[v.UInt];
            Marshal.Copy(v.BlobData, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Ole32Native.PropVariantClear(ref v);
        }
    }
}
