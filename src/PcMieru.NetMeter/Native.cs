using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.NetMeter;

internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle() => Native.CloseHandle(handle);
}

/// <summary>OVERLAPPED on x64: two pointer-sized status words, a 64-bit offset (two DWORDs) and the event handle.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OverlappedIo
{
    public nuint Internal;
    public nuint InternalHigh;
    public uint Offset;
    public uint OffsetHigh;
    public nint Event;
}

/// <summary>
/// The system calls the meter makes, all resolved from System32 (see AssemblyInfo.cs and Program.cs). Structures that ETW
/// and TDH define with unions and nested blocks (EVENT_TRACE_PROPERTIES, EVENT_TRACE_LOGFILEW, EVENT_RECORD,
/// TRACE_EVENT_INFO) are read and written at their documented x64 offsets in <see cref="EtwSession"/> instead of being
/// declared here, with the offsets stated once, next to the code that uses them.
/// </summary>
internal static unsafe partial class Native
{
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint SYNCHRONIZE = 0x0010_0000;

    internal const uint WAIT_OBJECT_0 = 0;
    internal const uint WAIT_TIMEOUT = 258;
    internal const uint INFINITE = 0xFFFF_FFFF;

    internal const uint ERROR_SUCCESS = 0;
    internal const uint ERROR_ACCESS_DENIED = 5;
    internal const uint ERROR_BROKEN_PIPE = 109;
    internal const uint ERROR_INSUFFICIENT_BUFFER = 122;
    internal const uint ERROR_ALREADY_EXISTS = 183;
    internal const uint ERROR_PIPE_CONNECTED = 535;
    internal const uint ERROR_OPERATION_ABORTED = 995;
    internal const uint ERROR_IO_PENDING = 997;
    internal const uint ERROR_NOT_FOUND = 1168;
    internal const uint ERROR_WMI_INSTANCE_NOT_FOUND = 4201;

    internal const uint PIPE_ACCESS_DUPLEX = 0x0000_0003;
    internal const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x0008_0000;
    internal const uint FILE_FLAG_OVERLAPPED = 0x4000_0000;
    internal const uint PIPE_TYPE_BYTE = 0;
    internal const uint PIPE_READMODE_BYTE = 0;
    internal const uint PIPE_WAIT = 0;
    internal const uint PIPE_REJECT_REMOTE_CLIENTS = 0x8;

    internal const uint EVENT_TRACE_CONTROL_QUERY = 0;
    internal const uint EVENT_TRACE_CONTROL_STOP = 1;
    internal const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    internal const byte TRACE_LEVEL_VERBOSE = 5;
    internal const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;

    internal const uint SDDL_REVISION_1 = 1;

    // ---- kernel32 ----

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeKernelHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(SafeKernelHandle process, long* creation, long* exit, long* kernel, long* user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeKernelHandle CreateEventW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetEvent(SafeKernelHandle handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ResetEvent(SafeKernelHandle handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForMultipleObjects(uint count, nint* handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeKernelHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuffer, uint inBuffer, uint defaultTimeout, nint securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConnectNamedPipe(SafeKernelHandle pipe, OverlappedIo* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DisconnectNamedPipe(SafeKernelHandle pipe);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadFile(SafeKernelHandle file, byte* buffer, uint count, uint* read, OverlappedIo* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WriteFile(SafeKernelHandle file, byte* buffer, uint count, uint* written, OverlappedIo* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetOverlappedResult(SafeKernelHandle file, OverlappedIo* overlapped, uint* transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CancelIoEx(SafeKernelHandle file, OverlappedIo* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(SafeKernelHandle pipe, uint* clientPid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryPerformanceCounter(long* count);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryPerformanceFrequency(long* frequency);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleCtrlHandler(nint handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint memory);

    // ---- advapi32 ----

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, nint* descriptor, uint* size);

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint StartTrace(ulong* sessionHandle, string sessionName, byte* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint ControlTrace(ulong sessionHandle, string? sessionName, byte* properties, uint controlCode);

    [LibraryImport("advapi32.dll")]
    internal static partial uint EnableTraceEx2(ulong sessionHandle, Guid* provider, uint controlCode, byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, nint parameters);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    internal static partial ulong OpenTrace(byte* logfile);

    [LibraryImport("advapi32.dll")]
    internal static partial uint ProcessTrace(ulong* handles, uint count, long* startTime, long* endTime);

    [LibraryImport("advapi32.dll")]
    internal static partial uint CloseTrace(ulong traceHandle);

    // ---- tdh ----

    [LibraryImport("tdh.dll")]
    internal static partial uint TdhGetEventInformation(byte* eventRecord, uint tdhContextCount, nint tdhContext, byte* buffer, uint* bufferSize);
}
