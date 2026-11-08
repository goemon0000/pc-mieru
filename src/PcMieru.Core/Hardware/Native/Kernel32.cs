using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Core.Hardware.Native;

/// <summary>kernel32 / advapi32 entry points used by the hardware collectors. System32 only, never the app directory.</summary>
internal static partial class Kernel32
{
    public const uint FileShareReadWrite = 3;
    public const uint OpenExisting = 3;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetSystemFirmwareTable(uint signature, uint tableId, byte[]? buffer, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLogicalProcessorInformationEx(int relationshipType, byte[]? buffer, ref uint returnedLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsProcessorFeaturePresent(uint feature);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[]? inBuffer,
        uint inBufferSize,
        byte[]? outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformationW(
        string rootPath,
        char[]? volumeName,
        uint volumeNameSize,
        out uint serial,
        out uint maxComponentLength,
        out uint fileSystemFlags,
        char[]? fileSystemName,
        uint fileSystemNameSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    public static extern uint GetLogicalDrives();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetDriveTypeW(string rootPath);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFirmwareType(out int firmwareType);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetUserDefaultLocaleName([Out] char[] name, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    /// <summary>Opens a device for IOCTLs with zero access rights (metadata only), which works for a standard user.</summary>
    public static SafeFileHandle OpenDeviceNoAccess(string path) =>
        CreateFileW(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    /// <summary>Opens a device for read-only IOCTLs (needed by some health queries). Still no write access requested.</summary>
    public static SafeFileHandle OpenDeviceRead(string path) =>
        CreateFileW(path, 0x80000000, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    /// <summary>Opens a device with both read and write access, which the storage protocol IOCTL for NVMe logs documents as required.</summary>
    public static SafeFileHandle OpenDeviceReadWrite(string path) =>
        CreateFileW(path, 0xC0000000, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
}

internal static partial class Ntdll
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll")]
    public static extern int RtlGetVersion(ref OsVersionInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct OsVersionInfo
    {
        public uint Size;
        public uint Major;
        public uint Minor;
        public uint Build;
        public uint PlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string CsdVersion;
    }
}

internal static partial class Tbs
{
    public const uint TbsETpmNotFound = 0x8028400F;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("tbs.dll")]
    public static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

    [StructLayout(LayoutKind.Sequential)]
    public struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImpRevision;
    }
}

internal static partial class Kernel32Errors
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadErrorMode(uint newMode, out uint oldMode);

    /// <summary>Runs <paramref name="action"/> with the "no critical-error dialog" mode (empty card readers, dead network drives).</summary>
    public static T Quiet<T>(Func<T> action)
    {
        SetThreadErrorMode(1, out var old);
        try
        {
            return action();
        }
        finally
        {
            SetThreadErrorMode(old, out _);
        }
    }
}
