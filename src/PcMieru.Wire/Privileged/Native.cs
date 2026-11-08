using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Wire;

internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle() => Native.CloseHandle(handle);
}

internal sealed class SafeScmHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeScmHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle() => Native.CloseServiceHandle(handle);
}

/// <summary>SERVICE_STATUS_PROCESS (nine DWORDs).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ServiceStatusProcess
{
    public uint ServiceType;
    public uint CurrentState;
    public uint ControlsAccepted;
    public uint Win32ExitCode;
    public uint ServiceSpecificExitCode;
    public uint CheckPoint;
    public uint WaitHint;
    public uint ProcessId;
    public uint ServiceFlags;
}

/// <summary>SERVICE_STATUS (seven DWORDs).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ServiceStatusRaw
{
    public uint ServiceType;
    public uint CurrentState;
    public uint ControlsAccepted;
    public uint Win32ExitCode;
    public uint ServiceSpecificExitCode;
    public uint CheckPoint;
    public uint WaitHint;
}

/// <summary>ENUM_SERVICE_STATUSW on x64: two string pointers then a SERVICE_STATUS.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EnumServiceStatus
{
    public nint ServiceName;
    public nint DisplayName;
    public ServiceStatusRaw Status;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PowerThrottlingState
{
    public uint Version;
    public uint ControlMask;
    public uint StateMask;
}

/// <summary>The few system calls the privileged operations need. Everything is resolved from System32.</summary>
internal static unsafe partial class Native
{
    internal const uint PROCESS_TERMINATE = 0x0001;
    internal const uint PROCESS_SET_INFORMATION = 0x0200;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint SYNCHRONIZE = 0x0010_0000;

    internal const int ProcessPowerThrottling = 4;
    internal const int ProcessProtectionLevelInfo = 7;
    internal const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    internal const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 1;
    internal const uint PROTECTION_LEVEL_NONE = 0xFFFFFFFE;

    internal const uint IDLE_PRIORITY_CLASS = 0x40;
    internal const uint BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
    internal const uint NORMAL_PRIORITY_CLASS = 0x20;
    internal const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x8000;
    internal const uint HIGH_PRIORITY_CLASS = 0x80;

    internal const uint WAIT_OBJECT_0 = 0;
    internal const uint WAIT_TIMEOUT = 258;

    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_INVALID_PARAMETER = 87;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;
    internal const int ERROR_MORE_DATA = 234;
    internal const int ERROR_DEPENDENT_SERVICES_RUNNING = 1051;
    internal const int ERROR_SERVICE_CANNOT_ACCEPT_CTRL = 1061;
    internal const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    internal const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    internal const int ERROR_SERVICE_DISABLED = 1058;
    internal const int ERROR_SERVICE_NOT_ACTIVE = 1062;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeKernelHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(SafeKernelHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageNameW(SafeKernelHandle process, uint flags, char* buffer, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsProcessCritical(SafeKernelHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessInformation(SafeKernelHandle process, int informationClass, void* information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessInformation(SafeKernelHandle process, int informationClass, void* information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetPriorityClass(SafeKernelHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetPriorityClass(SafeKernelHandle process, uint priorityClass);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(SafeKernelHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessAffinityMask(SafeKernelHandle process, out nuint processMask, out nuint systemMask);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessAffinityMask(SafeKernelHandle process, nuint mask);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessGroupAffinity(SafeKernelHandle process, ref ushort groupCount, ushort* groups);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentProcessId();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle device, uint code, void* input, int inputSize, void* output, int outputSize, out int returned, nint overlapped);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeScmHandle OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeScmHandle OpenService(SafeScmHandle manager, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint handle);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool StartService(SafeScmHandle service, uint argc, nint argv);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ControlService(SafeScmHandle service, uint control, out ServiceStatusRaw status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceStatusEx(SafeScmHandle service, int infoLevel, out ServiceStatusProcess status, uint size, out uint needed);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumDependentServicesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDependentServices(
        SafeScmHandle service, uint state, byte* buffer, uint size, out uint needed, out uint count);
}
