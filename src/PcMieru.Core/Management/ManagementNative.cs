using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Core.Management;

/// <summary>A kernel handle (process, snapshot) that is closed whatever happens.</summary>
internal sealed class MgmtKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public MgmtKernelHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle() => ManagementNative.CloseHandle(handle);
}

/// <summary>An SCM or service handle.</summary>
internal sealed class MgmtScHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public MgmtScHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle() => ManagementNative.CloseServiceHandle(handle);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MgmtServiceStatusProcess
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

/// <summary>ENUM_SERVICE_STATUS_PROCESSW: two string pointers, then the status.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MgmtEnumServiceStatusProcess
{
    public nint ServiceName;
    public nint DisplayName;
    public MgmtServiceStatusProcess Status;
}

/// <summary>QUERY_SERVICE_CONFIGW.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MgmtQueryServiceConfig
{
    public uint ServiceType;
    public uint StartType;
    public uint ErrorControl;
    public nint BinaryPathName;
    public nint LoadOrderGroup;
    public uint TagId;
    public nint Dependencies;
    public nint ServiceStartName;
    public nint DisplayName;
}

/// <summary>WTS_SESSION_INFOW.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MgmtWtsSessionInfo
{
    public uint SessionId;
    public nint WinStationName;
    public int State;
}

/// <summary>PROCESSENTRY32W.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MgmtProcessEntry
{
    public uint Size;
    public uint Usage;
    public uint ProcessId;
    public nuint DefaultHeapId;
    public uint ModuleId;
    public uint Threads;
    public uint ParentProcessId;
    public int PriorityClassBase;
    public uint Flags;
    public fixed char ExeFile[260];
}

/// <summary>
/// The system calls Management needs in Core, in one place. Every import is resolved from System32 only (the same rule
/// the Helper follows; it is set per method because an assembly-wide setting would also change the other areas' imports). Nothing here changes anything by itself: the callers decide.
/// </summary>
internal static unsafe partial class ManagementNative
{
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint SYNCHRONIZE = 0x0010_0000;
    internal const uint TH32CS_SNAPPROCESS = 2;
    internal const uint WAIT_OBJECT_0 = 0;
    internal const uint WAIT_TIMEOUT = 258;

    internal const uint SC_MANAGER_CONNECT = 0x1;
    internal const uint SC_MANAGER_ENUMERATE_SERVICE = 0x4;
    internal const uint SERVICE_QUERY_CONFIG = 0x1;
    internal const uint SERVICE_WIN32 = 0x30;
    internal const uint SERVICE_STATE_ALL = 3;
    internal const int ERROR_MORE_DATA = 234;
    internal const int ERROR_CANCELLED = 1223;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial MgmtKernelHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(MgmtKernelHandle process, out long created, out long exited, out long kernel, out long user);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(MgmtKernelHandle handle, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial MgmtKernelHandle CreateToolhelp32Snapshot(uint flags, uint pid);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Process32First(MgmtKernelHandle snapshot, ref MgmtProcessEntry entry);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Process32Next(MgmtKernelHandle snapshot, ref MgmtProcessEntry entry);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ProcessIdToSessionId(uint pid, out uint sessionId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageNameW(MgmtKernelHandle process, uint flags, char* name, ref uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial MgmtScHandle OpenSCManager(string? machine, string? database, uint access);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial MgmtScHandle OpenService(MgmtScHandle manager, string name, uint access);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumServicesStatusEx(
        MgmtScHandle manager, int infoLevel, uint serviceType, uint serviceState, byte* services, uint size,
        out uint needed, out uint returned, ref uint resumeHandle, string? groupName);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceConfig(MgmtScHandle service, byte* buffer, uint size, out uint needed);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceConfig2(MgmtScHandle service, uint level, byte* buffer, uint size, out uint needed);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSEnumerateSessions(nint server, uint reserved, uint version, out nint sessions, out uint count);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQuerySessionInformation(nint server, uint sessionId, int infoClass, out nint buffer, out uint bytes);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wtsapi32.dll")]
    internal static partial void WTSFreeMemory(nint memory);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("shlwapi.dll", EntryPoint = "SHLoadIndirectString", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHLoadIndirectString(string source, char* output, uint size, nint reserved);

    /// <summary>Reads a NUL-terminated UTF-16 string at <paramref name="pointer"/> (empty for a null pointer).</summary>
    internal static string ReadString(nint pointer) => pointer == 0 ? "" : Marshal.PtrToStringUni(pointer) ?? "";

    /// <summary>Reads a double-NUL-terminated list of strings (the service dependency format).</summary>
    internal static List<string> ReadMultiString(nint pointer)
    {
        var list = new List<string>();
        if (pointer == 0)
        {
            return list;
        }

        var p = (char*)pointer;
        while (*p != '\0')
        {
            var item = new string(p);
            list.Add(item);
            p += item.Length + 1;
        }

        return list;
    }
}
