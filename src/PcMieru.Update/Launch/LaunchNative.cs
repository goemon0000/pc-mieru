using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Update.Launch;

/// <summary>A kernel handle that is closed with CloseHandle (a process, a thread, a job object).</summary>
internal sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public KernelHandle()
        : base(ownsHandle: true)
    {
    }

    public KernelHandle(nint value, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(value);
    }

    protected override bool ReleaseHandle() => LaunchNative.CloseHandle(handle);
}

/// <summary>
/// The Windows calls of the hand-off and of the launch check that the BCL does not give: a pipe whose two ends are made here (and only here), a process started with a list of exactly the handles it may inherit
/// (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), a job object that kills what is in it when it is closed, and the questions about a handle that were handed down. Library-import P/Invoke from kernel32 only: the runner that
/// calls this is NativeAOT and its imports are checked by tools/publish.ps1 (kernel32, kernelbase, ntdll, advapi32, bcrypt, ucrtbase, ole32). The DLL is resolved from System32 only.
///
/// <b>A pipe in both directions, with one end given to the child.</b> An anonymous pipe (CreatePipe) goes one way; the conversation of the hand-off ("accepted") and of the launch check (ping and pong) goes both
/// ways, and the brief for the child is "exactly the gate handle and ONE pipe handle". So the pipe is a named pipe with a random name that is used for nothing but this: the first instance is created with
/// FILE_FLAG_FIRST_PIPE_INSTANCE and a limit of one instance, remote clients are refused, and the creator opens the only client end itself before anybody could (so nobody else can connect, and the name is not
/// given to anybody). The child is given the client end as an inherited handle and never learns the name.
/// </summary>
internal static unsafe partial class LaunchNative
{
    internal const uint PipeAccessDuplex = 0x3;
    internal const uint FlagOverlapped = 0x4000_0000;
    internal const uint FlagFirstPipeInstance = 0x0008_0000;
    internal const uint PipeRejectRemoteClients = 0x8;
    internal const uint GenericRead = 0x8000_0000;
    internal const uint GenericWrite = 0x4000_0000;
    internal const uint OpenExisting = 3;

    internal const uint ProcessSynchronize = 0x0010_0000;
    internal const uint ProcessQueryLimited = 0x1000;
    internal const uint ProcessTerminate = 0x1;

    internal const uint CreateSuspended = 0x4;
    internal const uint CreateNoWindow = 0x0800_0000;
    internal const uint ExtendedStartupInfoPresent = 0x0008_0000;

    internal const uint FileTypePipe = 0x3;
    internal const uint FileTypeDisk = 0x1;

    private const nuint ProcThreadAttributeHandleList = 0x0002_0002;
    private const uint HandleFlagInherit = 0x1;
    private const uint DuplicateSameAccess = 0x2;
    private const int ErrorPipeConnected = 535;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint WaitObject0 = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        public uint Length;
        public nint Descriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Overlapped
    {
        public nuint Internal;
        public nuint InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public nint Event;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Info;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimit
    {
        public JobBasicLimit Basic;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuffer, uint inBuffer, uint defaultTimeout, nint security);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, SecurityAttributes* security, uint disposition, uint flags, nint template);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConnectNamedPipe(SafeFileHandle pipe, Overlapped* overlapped);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(SafeFileHandle file, nint overlapped);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateEvent(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(SafeFileHandle file, byte* buffer, uint count, out uint read, Overlapped* overlapped);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(SafeFileHandle file, byte* buffer, uint count, out uint written, Overlapped* overlapped);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOverlappedResult(SafeFileHandle file, Overlapped* overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetHandleInformation(nint handle, out uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetFileType(nint file);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetProcessId(nint process);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint code);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateJobObject(nint attributes, string? name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int informationClass, JobExtendedLimit* information, uint length);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, uint count, uint flags, ref nuint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(string? application, char* commandLine, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string? directory, StartupInfoEx* startup, out ProcessInformation information);

    // ------------------------------------------------------------------------------------------------------------------------ the pipe

    /// <summary>
    /// Makes the pair: <paramref name="server"/> stays with the caller, <paramref name="client"/> is the end that is given to a child (inheritable, and the only inheritable thing made here). Both are
    /// overlapped handles, so that a read waiting on one thread does not hold up a write on another. False (and nothing is held) when the system refused.
    /// </summary>
    internal static bool TryCreatePipe(out SafeFileHandle server, out SafeFileHandle client)
    {
        client = new SafeFileHandle();
        var random = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(random);
        var name = @"\\.\pipe\pcm-" + Convert.ToHexStringLower(random);
        server = CreateNamedPipe(name, PipeAccessDuplex | FlagOverlapped | FlagFirstPipeInstance, PipeRejectRemoteClients, 1, 4096, 4096, 0, 0);
        if (server.IsInvalid)
        {
            return false;
        }

        var inheritable = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), Descriptor = 0, InheritHandle = 1 };
        client = CreateFile(name, GenericRead | GenericWrite, 0, &inheritable, OpenExisting, FlagOverlapped, 0);
        if (client.IsInvalid)
        {
            server.Dispose();
            return false;
        }

        var overlapped = default(Overlapped);
        if (!ConnectNamedPipe(server, &overlapped) && Marshal.GetLastPInvokeError() != ErrorPipeConnected)
        {
            client.Dispose();
            server.Dispose();
            return false;
        }

        return true;
    }

    private const int ErrorIoPending = 997;
    private const int ErrorBrokenPipe = 109;
    private const int ErrorNoData = 232;
    private const int ErrorPipeNotConnected = 233;
    private const uint WaitTimeout = 0x102;

    /// <summary>
    /// Reads what is there (at least one byte), waiting for it. The handle is an overlapped one (see <see cref="TryCreatePipe"/>): a read that waits must not hold up a write from another thread, which a synchronous
    /// handle would (the I/O manager serialises synchronous I/O on one file object). Returns the number of bytes; 0 when the other end closed; -1 when the read failed or was cancelled (<see cref="CancelPending"/>).
    /// </summary>
    internal static int ReadSome(SafeFileHandle handle, byte[] buffer)
    {
        var done = CreateEvent(0, true, false, null);
        if (done == 0)
        {
            return -1;
        }

        try
        {
            var overlapped = new Overlapped { Event = done };
            uint count;
            fixed (byte* data = buffer)
            {
                if (!ReadFile(handle, data, (uint)buffer.Length, out count, &overlapped))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == ErrorIoPending)
                    {
                        // The buffer and the OVERLAPPED stay where they are until the read has ended: it is waited for to its end, a cancel only ends it sooner.
                        if (!GetOverlappedResult(handle, &overlapped, out count, true))
                        {
                            error = Marshal.GetLastPInvokeError();
                            return error is ErrorBrokenPipe or ErrorPipeNotConnected ? 0 : -1;
                        }
                    }
                    else
                    {
                        return error is ErrorBrokenPipe or ErrorPipeNotConnected ? 0 : -1;
                    }
                }
            }

            return (int)count;
        }
        catch (ObjectDisposedException)
        {
            return -1; // the handle was closed under the read
        }
        finally
        {
            CloseHandle(done);
        }
    }

    /// <summary>
    /// Writes all of <paramref name="data"/>, giving up after <paramref name="limit"/> when the other end does not read (its pipe buffer is full): false then, and when the other end is gone or the write failed.
    /// </summary>
    internal static bool WriteAll(SafeFileHandle handle, byte[] data, TimeSpan limit)
    {
        var done = CreateEvent(0, true, false, null);
        if (done == 0)
        {
            return false;
        }

        try
        {
            var offset = 0;
            var until = Environment.TickCount64 + (long)limit.TotalMilliseconds;
            while (offset < data.Length)
            {
                var overlapped = new Overlapped { Event = done };
                uint written;
                fixed (byte* start = &data[offset])
                {
                    if (!WriteFile(handle, start, (uint)(data.Length - offset), out written, &overlapped))
                    {
                        if (Marshal.GetLastPInvokeError() != ErrorIoPending)
                        {
                            return false; // ERROR_BROKEN_PIPE, ERROR_NO_DATA (the other end is closing) and the rest all mean: not delivered
                        }

                        var left = (int)Math.Clamp(until - Environment.TickCount64, 0, int.MaxValue);
                        if (WaitForSingleObject(done, (uint)left) == WaitTimeout)
                        {
                            CancelIoEx(handle, (nint)(&overlapped));
                        }

                        // Always waited to its end: the buffer and the OVERLAPPED must outlive the write. A cancelled write ends with an error here.
                        if (!GetOverlappedResult(handle, &overlapped, out written, true))
                        {
                            return false;
                        }
                    }
                }

                if (written == 0)
                {
                    return false;
                }

                offset += (int)written;
            }

            return true;
        }
        catch (ObjectDisposedException)
        {
            return false; // the handle was closed under the write
        }
        finally
        {
            CloseHandle(done);
        }
    }

    /// <summary>Gives up the reads and writes that are waiting on the handle (it is about to be closed).</summary>
    internal static void CancelPending(SafeFileHandle handle)
    {
        if (!handle.IsClosed && !handle.IsInvalid)
        {
            try
            {
                CancelIoEx(handle, 0);
            }
            catch (ObjectDisposedException)
            {
                // closed by its reader between the check and the call: nothing is waiting any more
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------ questions about a handle that was handed down

    internal static bool IsPipe(nint handle) => handle != 0 && handle != -1 && GetFileType(handle) == FileTypePipe;

    /// <summary>The id of the process a handle stands for; 0 when it is not a process handle (or is no handle at all).</summary>
    internal static int ProcessIdOf(nint process) => process == 0 || process == -1 ? 0 : (int)GetProcessId(process);

    /// <summary>Whether the handle can be inherited (HANDLE_FLAG_INHERIT).</summary>
    internal static bool IsInheritable(nint handle) => GetHandleInformation(handle, out var flags) && (flags & HandleFlagInherit) != 0;

    /// <summary>Makes a handle that was inherited one that the programs started from here do NOT inherit (the swapper starts the GUI afterwards with an ordinary start).</summary>
    internal static void DisableInheritance(nint handle)
    {
        if (handle != 0 && handle != -1)
        {
            SetHandleInformation(handle, HandleFlagInherit, 0);
        }
    }

    /// <summary>Waits for a process handle to be signalled: true = the process has ended; false = the time is up. A handle that cannot be waited on counts as ended.</summary>
    internal static bool WaitForProcessHandle(nint process, TimeSpan timeout)
    {
        var milliseconds = (uint)Math.Clamp((long)timeout.TotalMilliseconds, 0, uint.MaxValue - 1);
        var result = WaitForSingleObject(process, milliseconds);
        return result != 0x102; // WAIT_TIMEOUT
    }

    /// <summary>An inheritable copy of this process's own handle, which the swapper waits on (the GUI that hands over is the process whose end it waits for). 0 when it could not be made.</summary>
    internal static nint DuplicateSelfInheritable()
    {
        var self = GetCurrentProcess();
        return DuplicateHandle(self, self, self, out var copy, ProcessSynchronize | ProcessQueryLimited, true, 0) ? copy : 0;
    }

    // ------------------------------------------------------------------------------------------------------------------------ starting a program

    /// <summary>A program that was started here: its process (and a job object, when one was asked for).</summary>
    internal sealed class Child : IDisposable
    {
        private readonly KernelHandle _process;
        private readonly KernelHandle? _job;

        internal Child(KernelHandle process, KernelHandle? job, int id)
        {
            _process = process;
            _job = job;
            Id = id;
        }

        public int Id { get; }

        /// <summary>True once the process has ended (or its handle cannot be asked).</summary>
        public bool HasExited => !_process.IsClosed && WaitForSingleObject(_process.DangerousGetHandle(), 0) == WaitObject0;

        public bool WaitForExit(TimeSpan timeout) => _process.IsClosed || WaitForProcessHandle(_process.DangerousGetHandle(), timeout);

        public int ExitCode => !_process.IsClosed && GetExitCodeProcess(_process.DangerousGetHandle(), out var code) ? unchecked((int)code) : -1;

        /// <summary>Ends the program and everything it started (the whole job when there is one).</summary>
        public void Kill()
        {
            if (_job is { IsClosed: false })
            {
                TerminateJobObject(_job.DangerousGetHandle(), 1);
            }

            if (!_process.IsClosed)
            {
                TerminateProcess(_process.DangerousGetHandle(), 1);
            }
        }

        public void Dispose()
        {
            _job?.Dispose(); // KILL_ON_JOB_CLOSE: closing the job is what ends whatever is still in it
            _process.Dispose();
        }
    }

    /// <summary>What <see cref="TryStart"/> was asked for.</summary>
    internal sealed record StartRequest(string Executable, string CommandLine, string WorkingFolder, IReadOnlyList<nint> InheritedHandles, bool NoWindow, bool InJob);

    /// <summary>
    /// Starts a program that inherits EXACTLY <see cref="StartRequest.InheritedHandles"/> (PROC_THREAD_ATTRIBUTE_HANDLE_LIST; every handle in the list must be inheritable) and nothing else of this process's handles.
    /// With <see cref="StartRequest.InJob"/> the process is created suspended, put into a new job object whose limit is KILL_ON_JOB_CLOSE and only then let run, so that it is never outside the job. Null (with the
    /// system's error in <paramref name="error"/>) when the program could not be started; nothing is left running then.
    /// </summary>
    internal static Child? TryStart(StartRequest request, out int error)
    {
        error = 0;
        var handles = request.InheritedHandles.ToArray();
        nuint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var list = Marshal.AllocHGlobal((nint)size);
        var gotList = false;
        var pinned = GCHandle.Alloc(handles, GCHandleType.Pinned);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                error = Marshal.GetLastPInvokeError();
                return null;
            }

            gotList = true;
            if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeHandleList, pinned.AddrOfPinnedObject(), (nuint)(handles.Length * sizeof(nint)), 0, 0))
            {
                error = Marshal.GetLastPInvokeError();
                return null;
            }

            var startup = default(StartupInfoEx);
            startup.Info.Size = (uint)sizeof(StartupInfoEx);
            startup.AttributeList = list;
            var flags = ExtendedStartupInfoPresent | (request.NoWindow ? CreateNoWindow : 0) | (request.InJob ? CreateSuspended : 0);
            var commandLine = (request.CommandLine + "\0").ToCharArray(); // CreateProcessW may write into it

            ProcessInformation info;
            bool made;
            fixed (char* text = commandLine)
            {
                made = CreateProcess(request.Executable, text, 0, 0, true, flags, 0, request.WorkingFolder, &startup, out info);
            }

            if (!made)
            {
                error = Marshal.GetLastPInvokeError();
                return null;
            }

            var process = new KernelHandle(info.Process, ownsHandle: true);
            using var thread = new KernelHandle(info.Thread, ownsHandle: true);
            KernelHandle? job = null;
            if (request.InJob)
            {
                job = MakeJob();
                if (job is null || !AssignProcessToJobObject(job.DangerousGetHandle(), process.DangerousGetHandle()) || ResumeThread(thread.DangerousGetHandle()) == uint.MaxValue)
                {
                    error = Marshal.GetLastPInvokeError();
                    TerminateProcess(process.DangerousGetHandle(), 1); // never left suspended or outside the job
                    job?.Dispose();
                    process.Dispose();
                    return null;
                }
            }

            return new Child(process, job, (int)info.ProcessId);
        }
        finally
        {
            pinned.Free();
            if (gotList)
            {
                DeleteProcThreadAttributeList(list);
            }

            Marshal.FreeHGlobal(list);
        }
    }

    private static KernelHandle? MakeJob()
    {
        var job = CreateJobObject(0, null);
        if (job == 0)
        {
            return null;
        }

        var handle = new KernelHandle(job, ownsHandle: true);
        var limit = default(JobExtendedLimit);
        limit.Basic.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, &limit, (uint)sizeof(JobExtendedLimit)))
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    /// <summary>The command-line spelling of one argument (the rules CommandLineToArgvW reads back).</summary>
    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        var text = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            if (c == '"')
            {
                text.Append('\\', slashes * 2 + 1).Append('"');
            }
            else
            {
                text.Append('\\', slashes).Append(c);
            }

            slashes = 0;
        }

        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
}
