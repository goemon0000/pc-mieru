using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Core.Hardware.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct DevPropKey(Guid fmtid, uint pid)
{
    public Guid Fmtid = fmtid;
    public uint Pid = pid;
}

internal static class DevKeys
{
    private static readonly Guid DriverGuid = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
    private static readonly Guid DeviceGuid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid PciGuid = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");

    public static readonly DevPropKey DriverDate = new(DriverGuid, 2);
    public static readonly DevPropKey DriverVersion = new(DriverGuid, 3);
    public static readonly DevPropKey DriverProvider = new(DriverGuid, 9);
    public static readonly DevPropKey DeviceDesc = new(DeviceGuid, 2);
    public static readonly DevPropKey HardwareIds = new(DeviceGuid, 3);
    public static readonly DevPropKey Service = new(DeviceGuid, 6);
    public static readonly DevPropKey Class = new(DeviceGuid, 9);
    public static readonly DevPropKey Manufacturer = new(DeviceGuid, 13);
    public static readonly DevPropKey FriendlyName = new(DeviceGuid, 14);
    public static readonly DevPropKey LocationInfo = new(DeviceGuid, 15);
    public static readonly DevPropKey Parent = new(new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), 8);
    public static readonly DevPropKey InstanceId = new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
    public static readonly DevPropKey BusReportedDeviceDesc = new(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);
    public static readonly DevPropKey PciCurrentLinkSpeed = new(PciGuid, 9);
    public static readonly DevPropKey PciCurrentLinkWidth = new(PciGuid, 10);
    public static readonly DevPropKey PciMaxLinkSpeed = new(PciGuid, 11);
    public static readonly DevPropKey PciMaxLinkWidth = new(PciGuid, 12);
}

internal static partial class SetupApiNative
{
    public const uint DigcfPresent = 0x2;
    public const uint DigcfAllClasses = 0x4;
    public const uint DigcfDeviceInterface = 0x10;
    public const uint DicsFlagGlobal = 1;
    public const uint DiregDrv = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwnd, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwnd, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevInfoData data);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDevicePropertyW(
        IntPtr set,
        ref SpDevInfoData data,
        ref DevPropKey key,
        out uint propertyType,
        byte[]? buffer,
        uint bufferSize,
        out uint requiredSize,
        uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr set,
        ref SpDevInfoData data,
        uint property,
        out uint propertyType,
        byte[]? buffer,
        uint bufferSize,
        out uint requiredSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SpDevInfoData data, uint scope, uint hwProfile, uint keyType, uint samDesired);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid interfaceGuid, uint index, ref SpDeviceInterfaceData data);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr set,
        ref SpDeviceInterfaceData data,
        IntPtr detail,
        uint detailSize,
        out uint requiredSize,
        ref SpDevInfoData devInfo);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SpDevInfoData data, char[]? buffer, uint size, out uint requiredSize);
}

internal static partial class CfgMgr
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_IDW(uint devInst, char[] buffer, uint length, uint flags);
}

/// <summary>One device in an enumeration. Valid only while its <see cref="DeviceInfoSet"/> is alive.</summary>
internal sealed class DeviceNode
{
    private readonly IntPtr _set;
    private SetupApiNative.SpDevInfoData _data;

    internal DeviceNode(IntPtr set, SetupApiNative.SpDevInfoData data)
    {
        _set = set;
        _data = data;
    }

    public uint DevInst => _data.DevInst;

    public Guid ClassGuid => _data.ClassGuid;

    public string? InstanceId
    {
        get
        {
            SetupApiNative.SetupDiGetDeviceInstanceIdW(_set, ref _data, null, 0, out var needed);
            if (needed == 0)
            {
                return null;
            }

            var buf = new char[needed];
            return SetupApiNative.SetupDiGetDeviceInstanceIdW(_set, ref _data, buf, needed, out _) ? new string(buf).TrimEnd('\0') : null;
        }
    }

    private byte[]? Raw(DevPropKey key, out uint type)
    {
        SetupApiNative.SetupDiGetDevicePropertyW(_set, ref _data, ref key, out type, null, 0, out var needed, 0);
        if (needed == 0)
        {
            return null;
        }

        var buf = new byte[needed];
        return SetupApiNative.SetupDiGetDevicePropertyW(_set, ref _data, ref key, out type, buf, needed, out _, 0) ? buf : null;
    }

    public string? GetString(DevPropKey key)
    {
        var raw = Raw(key, out var type);
        if (raw is null || type != 0x12)
        {
            return null;
        }

        var s = Encoding.Unicode.GetString(raw).TrimEnd('\0').Trim();
        return s.Length == 0 ? null : s;
    }

    public IReadOnlyList<string> GetStringList(DevPropKey key)
    {
        var raw = Raw(key, out var type);
        if (raw is null || (type & 0xFFF) != 0x12)
        {
            return [];
        }

        return Encoding.Unicode.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public long? GetInteger(DevPropKey key)
    {
        var raw = Raw(key, out _);
        return raw?.Length switch
        {
            1 => raw[0],
            2 => BitConverter.ToUInt16(raw, 0),
            4 => BitConverter.ToUInt32(raw, 0),
            8 => BitConverter.ToInt64(raw, 0),
            _ => null,
        };
    }

    public DateTime? GetFileTime(DevPropKey key)
    {
        var raw = Raw(key, out var type);
        if (raw is null || type != 0x10 || raw.Length < 8)
        {
            return null;
        }

        var ft = BitConverter.ToInt64(raw, 0);
        return ft <= 0 ? null : DateTime.FromFileTimeUtc(ft);
    }

    /// <summary>SPDRP_* legacy registry property (string or multi-string). Multi-strings are returned joined by '\n'.</summary>
    public string? GetLegacyString(uint property)
    {
        SetupApiNative.SetupDiGetDeviceRegistryPropertyW(_set, ref _data, property, out _, null, 0, out var needed);
        if (needed == 0)
        {
            return null;
        }

        var buf = new byte[needed];
        if (!SetupApiNative.SetupDiGetDeviceRegistryPropertyW(_set, ref _data, property, out _, buf, needed, out _))
        {
            return null;
        }

        var s = Encoding.Unicode.GetString(buf).TrimEnd('\0').Replace('\0', '\n').Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>The device's driver (software) registry key, read only.</summary>
    public RegistryKey? OpenDriverKey()
    {
        var h = SetupApiNative.SetupDiOpenDevRegKey(_set, ref _data, SetupApiNative.DicsFlagGlobal, 0, SetupApiNative.DiregDrv, 0x20019);
        if (h == IntPtr.Zero || h == new IntPtr(-1))
        {
            return null;
        }

        return RegistryKey.FromHandle(new SafeRegistryHandle(h, ownsHandle: true));
    }

    /// <summary>Device interface path (\\?\...) for this node's first interface of the given class, if any.</summary>
    public string? GetInterfacePath(Guid interfaceClass)
    {
        var ifData = new SetupApiNative.SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SetupApiNative.SpDeviceInterfaceData>() };
        var devInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SetupApiNative.SpDevInfoData>());
        try
        {
            Marshal.StructureToPtr(_data, devInfoPtr, false);
            if (!SetupApiNative.SetupDiEnumDeviceInterfaces(_set, devInfoPtr, ref interfaceClass, 0, ref ifData))
            {
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(devInfoPtr);
        }

        return DeviceInfoSet.InterfacePath(_set, ref ifData);
    }
}

/// <summary>A SetupAPI device information set. Dispose closes it.</summary>
internal sealed class DeviceInfoSet : IDisposable
{
    private static readonly IntPtr InvalidHandle = new(-1);
    private IntPtr _handle;

    private DeviceInfoSet(IntPtr handle) => _handle = handle;

    public static DeviceInfoSet? ForClass(Guid classGuid, string? enumerator = null, bool presentOnly = true)
    {
        var h = SetupApiNative.SetupDiGetClassDevsW(ref classGuid, enumerator, IntPtr.Zero, presentOnly ? SetupApiNative.DigcfPresent : 0);
        return h == InvalidHandle ? null : new DeviceInfoSet(h);
    }

    /// <summary>All present devices of one enumerator ("USB", "PCI", ...).</summary>
    public static DeviceInfoSet? ForEnumerator(string enumerator)
    {
        var h = SetupApiNative.SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, SetupApiNative.DigcfPresent | SetupApiNative.DigcfAllClasses);
        return h == InvalidHandle ? null : new DeviceInfoSet(h);
    }

    /// <summary>All present device interfaces of one interface class (disks, monitors, ...).</summary>
    public static DeviceInfoSet? ForInterface(Guid interfaceClass)
    {
        var h = SetupApiNative.SetupDiGetClassDevsW(ref interfaceClass, null, IntPtr.Zero, SetupApiNative.DigcfPresent | SetupApiNative.DigcfDeviceInterface);
        return h == InvalidHandle ? null : new DeviceInfoSet(h);
    }

    public IEnumerable<DeviceNode> Devices()
    {
        for (uint i = 0; ; i++)
        {
            var data = new SetupApiNative.SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SetupApiNative.SpDevInfoData>() };
            if (!SetupApiNative.SetupDiEnumDeviceInfo(_handle, i, ref data))
            {
                yield break;
            }

            yield return new DeviceNode(_handle, data);
        }
    }

    /// <summary>Enumerates device interfaces of a class in this set (the set must have been opened with ForInterface). Path + the device node.</summary>
    public IEnumerable<(string Path, DeviceNode Node)> Interfaces(Guid interfaceClass)
    {
        for (uint i = 0; ; i++)
        {
            var ifData = new SetupApiNative.SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SetupApiNative.SpDeviceInterfaceData>() };
            if (!SetupApiNative.SetupDiEnumDeviceInterfaces(_handle, IntPtr.Zero, ref interfaceClass, i, ref ifData))
            {
                yield break;
            }

            var path = InterfacePath(_handle, ref ifData, out var devInfo);
            if (path is not null)
            {
                yield return (path, new DeviceNode(_handle, devInfo));
            }
        }
    }

    internal static string? InterfacePath(IntPtr set, ref SetupApiNative.SpDeviceInterfaceData ifData) => InterfacePath(set, ref ifData, out _);

    private static string? InterfacePath(IntPtr set, ref SetupApiNative.SpDeviceInterfaceData ifData, out SetupApiNative.SpDevInfoData devInfo)
    {
        devInfo = new SetupApiNative.SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SetupApiNative.SpDevInfoData>() };
        SetupApiNative.SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out var needed, ref devInfo);
        if (needed < 8)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W: DWORD cbSize (8 on 64-bit, 6 on 32-bit) followed by the path.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupApiNative.SetupDiGetDeviceInterfaceDetailW(set, ref ifData, buffer, needed, out _, ref devInfo))
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            SetupApiNative.SetupDiDestroyDeviceInfoList(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

/// <summary>Property reads by device instance handle (parents, PCI link state). Read only.</summary>
internal static class DevInstProps
{
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type, byte[]? buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string? deviceId, uint flags);

    public static uint? Locate(string instanceId) => CM_Locate_DevNodeW(out var inst, instanceId, 0) == 0 ? inst : null;

    public static uint? Parent(uint devInst) => CfgMgr.CM_Get_Parent(out var parent, devInst, 0) == 0 ? parent : null;

    private static byte[]? Raw(uint devInst, DevPropKey key, out uint type)
    {
        uint size = 0;
        CM_Get_DevNode_PropertyW(devInst, ref key, out type, null, ref size, 0);
        if (size == 0)
        {
            return null;
        }

        var buf = new byte[size];
        return CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, 0) == 0 ? buf : null;
    }

    public static string? GetString(uint devInst, DevPropKey key)
    {
        var raw = Raw(devInst, key, out var type);
        if (raw is null || type != 0x12)
        {
            return null;
        }

        var s = Encoding.Unicode.GetString(raw).TrimEnd('\0').Trim();
        return s.Length == 0 ? null : s;
    }

    public static long? GetInteger(uint devInst, DevPropKey key)
    {
        var raw = Raw(devInst, key, out _);
        return raw?.Length switch
        {
            1 => raw[0],
            2 => BitConverter.ToUInt16(raw, 0),
            4 => BitConverter.ToUInt32(raw, 0),
            8 => BitConverter.ToInt64(raw, 0),
            _ => null,
        };
    }

    public static string? InstanceId(uint devInst)
    {
        var buf = new char[400];
        return CfgMgr.CM_Get_Device_IDW(devInst, buf, (uint)buf.Length, 0) == 0 ? new string(buf).TrimEnd('\0') : null;
    }
}
