using System.Runtime.InteropServices;

namespace PcMieru.Core.Collection;

/// <summary>Why a process could not be opened for a fact.</summary>
internal enum OpenOutcome
{
    Opened,

    /// <summary>No such PID, it has exited, or the PID now belongs to another process (the create time differs).</summary>
    Gone,

    Denied,

    Error,
}

/// <summary>
/// One process opened with PROCESS_QUERY_LIMITED_INFORMATION and proven to be the one the ProcessKey names (SPEC 8: the
/// creation time is read from the handle, so a reused PID is never mistaken for the old process). Every read goes
/// through this one handle, so all the facts describe the same process.
/// </summary>
internal sealed unsafe class ProcessHandle : IDisposable
{
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusProcessIsProtected = unchecked((int)0xC0000712);
    private const int StatusProcessIsTerminating = unchecked((int)0xC000010A);
    private const int StatusInvalidInfoClass = unchecked((int)0xC0000003);
    private const int StatusInvalidParameter = unchecked((int)0xC000000D);
    private const int StatusNotSupported = unchecked((int)0xC00000BB);
    private const int StatusNotFound = unchecked((int)0xC0000225);
    private const int StatusPartialCopy = unchecked((int)0x8000000D);

    private nint _handle;

    private ProcessHandle(nint handle, ProcessKey key)
    {
        _handle = handle;
        Key = key;
    }

    public ProcessKey Key { get; }

    public static OpenOutcome Open(ProcessKey key, out ProcessHandle? process, out string? detail)
    {
        process = null;
        detail = null;
        if (key.Pid == 0)
        {
            detail = "the idle process cannot be opened";
            return OpenOutcome.Denied;
        }

        var handle = Win32.OpenProcess(Win32.ProcessQueryLimitedInformation, false, key.Pid);
        if (handle == 0)
        {
            var error = (uint)Marshal.GetLastPInvokeError();
            switch (error)
            {
                case Win32.ErrorAccessDenied:
                    detail = "access denied opening the process";
                    return OpenOutcome.Denied;
                case Win32.ErrorInvalidParameter:
                    detail = "no process with this PID is running";
                    return OpenOutcome.Gone;
                default:
                    detail = $"OpenProcess failed (Win32 error {error})";
                    return OpenOutcome.Error;
            }
        }

        if (!Win32.GetProcessTimes(handle, out var created, out var exited, out _, out _))
        {
            var error = Marshal.GetLastPInvokeError();
            _ = Win32.CloseHandle(handle);
            detail = $"GetProcessTimes failed (Win32 error {error})";
            return OpenOutcome.Error;
        }

        if (exited != 0)
        {
            _ = Win32.CloseHandle(handle);
            detail = "the process has exited";
            return OpenOutcome.Gone;
        }

        if (created != key.CreateTime)
        {
            _ = Win32.CloseHandle(handle);
            detail = "the PID now belongs to a different process";
            return OpenOutcome.Gone;
        }

        process = new ProcessHandle(handle, key);
        return OpenOutcome.Opened;
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            _ = Win32.CloseHandle(handle);
        }
    }

    /// <summary>True once the process has exited (the handle outlives it).</summary>
    public bool HasExited() => Win32.GetProcessTimes(_handle, out _, out var exited, out _, out _) && exited != 0;

    /// <summary>Maps a failed read to Gone when the process is no longer there, otherwise to Denied or Error.</summary>
    private Value<T> Failed<T>(string source, string what, int win32Error)
    {
        if (HasExited())
        {
            return Value.Gone<T>(source, "the process exited while its details were being read");
        }

        return win32Error == (int)Win32.ErrorAccessDenied
            ? Value.Denied<T>(source, $"access denied reading {what}")
            : Value.Failed<T>(source, $"{what}: Win32 error {win32Error}");
    }

    private Value<T> FailedNt<T>(string source, string what, int status)
    {
        if (status is StatusProcessIsTerminating || HasExited())
        {
            return Value.Gone<T>(source, "the process exited while its details were being read");
        }

        return status switch
        {
            StatusAccessDenied or StatusProcessIsProtected => Value.Denied<T>(source, $"{what} is not readable for this process (protected or another user's)"),
            StatusInvalidInfoClass or StatusInvalidParameter or StatusNotSupported => Value.Unsupported<T>(source, $"{what} is not available on this Windows version"),
            // System, Registry, Memory Compression and Secure System have no user-mode environment block to read.
            StatusNotFound => Value.Unsupported<T>(source, $"{what} does not exist: this process has no user-mode environment (it is owned by the kernel)"),
            // The memory of an isolated (virtualization-protected) process cannot be read from outside it.
            StatusPartialCopy => Value.Denied<T>(source, $"{what} is not readable: the process memory is protected"),
            _ => Value.Failed<T>(source, $"{what}: NTSTATUS 0x{status:X8}"),
        };
    }

    public Value<string> ReadPath()
    {
        const string source = "QueryFullProcessImageName";
        var buffer = stackalloc char[1024];
        uint size = 1024;
        if (Win32.QueryFullProcessImageNameW(_handle, 0, buffer, ref size))
        {
            return Value.Ok(new string(buffer, 0, (int)size), source);
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == (int)Win32.ErrorInsufficientBuffer)
        {
            var big = new char[32768];
            size = (uint)big.Length;
            fixed (char* p = big)
            {
                if (Win32.QueryFullProcessImageNameW(_handle, 0, p, ref size))
                {
                    return Value.Ok(new string(p, 0, (int)size), source);
                }
            }

            error = Marshal.GetLastPInvokeError();
        }

        // Kernel-owned processes (System, Registry, Memory Compression) have no image file at all.
        var fromKernel = ProcessImagePath.Resolve(Key.Pid);
        if (fromKernel is not null)
        {
            return Value.Ok(fromKernel, "NtQuerySystemInformation(SystemProcessIdInformation)");
        }

        if (Key.Pid == 4 || (error == 31 && !HasExited()))
        {
            return Value.Unsupported<string>(source, "this process has no image file (it is owned by the kernel)");
        }

        return Failed<string>(source, "the image path", error);
    }

    /// <summary>
    /// The machine type of the image: x64, x86, ARM64 or ARM. Under emulation the emulated type is named.
    /// </summary>
    public Value<string> ReadArchitecture()
    {
        const string source = "IsWow64Process2";
        if (!Win32.IsWow64Process2(_handle, out var processMachine, out var nativeMachine))
        {
            return Failed<string>(source, "the process machine type", Marshal.GetLastPInvokeError());
        }

        // processMachine is IMAGE_FILE_MACHINE_UNKNOWN (0) for a process that is not running under WOW64.
        var machine = processMachine == 0 ? nativeMachine : processMachine;
        var name = machine switch
        {
            Win32.MachineAmd64 => "x64",
            Win32.MachineI386 => "x86",
            Win32.MachineArm64 => "ARM64",
            Win32.MachineArmNt => "ARM",
            _ => null,
        };
        if (name is null)
        {
            return Value.Unsupported<string>(source, $"unknown machine type 0x{machine:X4}");
        }

        var emulated = processMachine != 0 && nativeMachine == Win32.MachineArm64 && processMachine != Win32.MachineArm64;
        return emulated
            ? new Value<string>(name, ValueStatus.Ok, source, "emulated on an ARM64 machine")
            : Value.Ok(name, source);
    }

    /// <summary>"DOMAIN\name" of the token's user.</summary>
    public Value<string> ReadOwner()
    {
        const string source = "process token";
        if (!Win32.OpenProcessToken(_handle, Win32.TokenQuery, out var token))
        {
            return Failed<string>(source, "the process token", Marshal.GetLastPInvokeError());
        }

        try
        {
            var buffer = new byte[512];
            for (var attempt = 0; attempt < 2; attempt++)
            {
                uint needed;
                fixed (byte* p = buffer)
                {
                    if (Win32.GetTokenInformation(token, Win32.TokenUserClass, p, (uint)buffer.Length, out needed))
                    {
                        return LookupOwner(p, source);
                    }
                }

                var error = Marshal.GetLastPInvokeError();
                if (error != (int)Win32.ErrorInsufficientBuffer || needed == 0 || needed > 65536)
                {
                    return Failed<string>(source, "the token user", error);
                }

                buffer = new byte[needed];
            }

            return Value.Failed<string>(source, "the token user kept changing while it was read");
        }
        finally
        {
            _ = Win32.CloseHandle(token);
        }
    }

    private Value<string> LookupOwner(byte* tokenUser, string source)
    {
        // TOKEN_USER starts with the SID pointer.
        var sid = *(void**)tokenUser;
        var name = stackalloc char[256];
        var domain = stackalloc char[256];
        uint nameLength = 256;
        uint domainLength = 256;
        if (!Win32.LookupAccountSid(0, sid, name, ref nameLength, domain, ref domainLength, out _))
        {
            return Failed<string>(source, "the account name for the token's SID", Marshal.GetLastPInvokeError());
        }

        var user = new string(name, 0, (int)nameLength);
        return Value.Ok(domainLength == 0 ? user : new string(domain, 0, (int)domainLength) + "\\" + user, source);
    }

    public Value<string> ReadCommandLine()
    {
        const string source = "NtQueryInformationProcess(ProcessCommandLineInformation)";
        var size = 4096u;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                var status = Win32.NtQueryInformationProcess(_handle, Win32.ProcessCommandLineInformationClass, p, size, out var returned);
                if (status >= 0)
                {
                    // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer } with the text after it in the same buffer.
                    var length = *(ushort*)p;
                    var text = *(char**)(p + 8);
                    if (length == 0 || text is null)
                    {
                        return Value.Unsupported<string>(source, "this process has no command line");
                    }

                    if ((byte*)text < p || (byte*)text + length > p + size)
                    {
                        return Value.Failed<string>(source, "the command line pointed outside the returned buffer");
                    }

                    return Value.Ok(new string(text, 0, length / 2), source);
                }

                if ((status is Win32.StatusInfoLengthMismatch or Win32.StatusBufferTooSmall or Win32.StatusBufferOverflow) && returned > size)
                {
                    size = Math.Min(returned + 64, 1u << 20);
                    continue;
                }

                return FailedNt<string>(source, "the command line", status);
            }
        }

        return Value.Failed<string>(source, "the command line kept changing while it was read");
    }

    /// <summary>
    /// Whether the process asked Windows to run it at reduced power ("efficiency mode"). Cheap, so it is read on every
    /// request instead of being cached with the other facts (the user can change it at any time).
    /// </summary>
    public Value<bool> ReadEfficiencyMode()
    {
        const string source = "GetProcessInformation(ProcessPowerThrottling)";
        var state = new PowerThrottlingState { Version = 1 };
        if (!Win32.GetProcessInformation(_handle, Win32.ProcessPowerThrottlingClass, &state, (uint)sizeof(PowerThrottlingState)))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == (int)Win32.ErrorInvalidParameter && !HasExited())
            {
                return Value.Unsupported<bool>(source, "this Windows version has no per-process power throttling");
            }

            return Failed<bool>(source, "the power throttling state", error);
        }

        var controlled = (state.ControlMask & Win32.PowerThrottlingExecutionSpeed) != 0;
        var on = controlled && (state.StateMask & Win32.PowerThrottlingExecutionSpeed) != 0;
        return new Value<bool>(on, ValueStatus.Ok, source, controlled ? null : "not set by the process or the user (Windows decides)");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }
}

/// <summary>
/// The image path of a process by PID alone, without opening it: the kernel names it for any caller, so processes we are
/// denied (other users', protected) still get a path.
/// </summary>
internal static unsafe class ProcessImagePath
{
    private static readonly object DriveLock = new();
    private static KeyValuePair<string, char>[] _drives = [];

    /// <summary>Win32 path (C:\...), or null when the process has no image file or the name could not be mapped to a drive.</summary>
    public static string? Resolve(uint pid)
    {
        var nt = ReadNtPath(pid);
        return nt is null ? null : ToDosPath(nt);
    }

    private static string? ReadNtPath(uint pid)
    {
        var chars = 1024;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new char[chars];
            fixed (char* p = buffer)
            {
                var info = new SystemProcessIdInformation
                {
                    ProcessId = (nint)pid,
                    ImageNameLength = 0,
                    ImageNameMaximumLength = (ushort)Math.Min(chars * 2, ushort.MaxValue),
                    ImageNameBuffer = (nint)p,
                };
                var status = Win32.NtQuerySystemInformation(Win32.SystemProcessIdInformation, &info, (uint)sizeof(SystemProcessIdInformation), out _);
                if (status >= 0)
                {
                    return info.ImageNameLength == 0 ? null : new string(p, 0, info.ImageNameLength / 2);
                }

                if (status == Win32.StatusInfoLengthMismatch && info.ImageNameMaximumLength > chars * 2)
                {
                    chars = info.ImageNameMaximumLength / 2 + 16;
                    continue;
                }

                return null;
            }
        }

        return null;
    }

    private static string? ToDosPath(string nt)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var (device, letter) in Volatile.Read(ref _drives))
            {
                if (nt.Length > device.Length && nt[device.Length] == '\\' && nt.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                {
                    return letter + ":" + nt[device.Length..];
                }
            }

            if (pass == 0)
            {
                RefreshDrives(); // a drive mounted after the first lookup
            }
        }

        return null;
    }

    private static void RefreshDrives()
    {
        lock (DriveLock)
        {
            var found = new List<KeyValuePair<string, char>>();
            var target = stackalloc char[1024];
            for (var letter = 'A'; letter <= 'Z'; letter++)
            {
                var length = Win32.QueryDosDeviceW(letter + ":", target, 1024);
                if (length > 0)
                {
                    // The answer is a REG_MULTI_SZ; the first string is the device.
                    var first = new string(target);
                    if (first.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(new KeyValuePair<string, char>(first, letter));
                    }
                }
            }

            Volatile.Write(ref _drives, [.. found]);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessIdInformation
    {
        public nint ProcessId;
        public ushort ImageNameLength;
        public ushort ImageNameMaximumLength;
        public nint ImageNameBuffer;
    }
}
