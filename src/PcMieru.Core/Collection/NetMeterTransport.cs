using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PcMieru.Wire;

namespace PcMieru.Core.Collection;

// What NetMeterControl needs from the system, as three small seams (the launcher, the pipe, who we are), so every
// transition of the control can be tested with fakes that start nothing and touch no pipe. The real ones are below.

/// <summary>This process, as the meter needs to know it: it only ever serves the process with this pid AND creation time.</summary>
internal readonly record struct NetMeterIdentity(uint ProcessId, long CreatedFileTime, string UserSid)
{
    public static NetMeterIdentity Current()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("the current user has no SID");
        if (!NetMeterNative.GetProcessTimes(NetMeterNative.GetCurrentProcess(), out var created, out _, out _, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new NetMeterIdentity((uint)Environment.ProcessId, created, sid);
    }
}

internal enum NetMeterLaunchStatus
{
    Started,

    /// <summary>The user said no at the UAC prompt (Win32 error 1223).</summary>
    Declined,

    /// <summary>The meter could not be started; <see cref="NetMeterLaunch.Error"/> is the Win32 error (0 when there is none).</summary>
    Failed,
}

internal readonly record struct NetMeterLaunch(NetMeterLaunchStatus Status, INetMeterProcess? Process = null, int Error = 0);

/// <summary>The started meter, as far as a non-elevated front end can see it.</summary>
internal interface INetMeterProcess : IDisposable
{
    uint Id { get; }

    /// <summary>False when it is still running OR when that cannot be told (the handle of an elevated process is a limited one).</summary>
    bool HasExited { get; }

    int ExitCode { get; }

    bool WaitForExit(TimeSpan timeout);
}

/// <summary>Starts the meter. With <c>elevate</c> it is the UAC prompt ("runas"); without, an ordinary child with the same token.</summary>
internal interface INetMeterLauncher
{
    NetMeterLaunch Launch(string exePath, string arguments, bool elevate);
}

internal enum NetMeterFailure
{
    None,

    /// <summary>No complete reply in time. The stream may hold half an answer, so the connection is not used again.</summary>
    Timeout,

    /// <summary>The pipe ended or errored.</summary>
    Broken,

    /// <summary>A reply arrived that the protocol does not allow (wrong version, length, flags).</summary>
    BadReply,
}

internal readonly record struct NetMeterExchange(NetMeterReading? Reading, NetMeterFailure Failure);

/// <summary>One client end of the meter's pipe.</summary>
internal interface INetMeterChannel : IDisposable
{
    /// <summary>Tries to connect for at most <paramref name="timeout"/>. Never throws.</summary>
    bool TryConnect(TimeSpan timeout);

    /// <summary>The pid of the process serving the other end (0 when unknown). Only a connected channel has one.</summary>
    uint ServerProcessId { get; }

    /// <summary>Sends a Read and waits for the reply. Never throws.</summary>
    NetMeterExchange Read(TimeSpan timeout);

    /// <summary>Asks the meter to stop, best effort and without waiting for anything. Never throws.</summary>
    void SendStop();
}

internal interface INetMeterChannelFactory
{
    INetMeterChannel Create(string pipeName);
}

internal static partial class NetMeterNative
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);
}

/// <summary>
/// <c>runas</c> through the shell (ShellExecuteEx under Process.Start), the same way the Helper is started: one UAC prompt, and
/// the call returns once the prompt is answered. Without <c>elevate</c> the same code starts an ordinary child.
/// </summary>
internal sealed class SystemNetMeterLauncher : INetMeterLauncher
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the UAC prompt was answered no

    public NetMeterLaunch Launch(string exePath, string arguments, bool elevate)
    {
        try
        {
            var info = new ProcessStartInfo(exePath, arguments)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
            };
            if (elevate)
            {
                info.Verb = "runas";
            }

            var process = Process.Start(info);
            return process is null
                ? new NetMeterLaunch(NetMeterLaunchStatus.Failed)
                : new NetMeterLaunch(NetMeterLaunchStatus.Started, new StartedProcess(process));
        }
        catch (Win32Exception ex)
        {
            return ex.NativeErrorCode == ErrorCancelled
                ? new NetMeterLaunch(NetMeterLaunchStatus.Declined, Error: ErrorCancelled)
                : new NetMeterLaunch(NetMeterLaunchStatus.Failed, Error: ex.NativeErrorCode);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new NetMeterLaunch(NetMeterLaunchStatus.Failed, Error: ex.HResult & 0xFFFF);
        }
    }

    private sealed class StartedProcess(Process process) : INetMeterProcess
    {
        public uint Id { get; } = (uint)process.Id;

        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    return false;
                }
            }
        }

        public int ExitCode
        {
            get
            {
                try
                {
                    return process.ExitCode;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    return -1;
                }
            }
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            try
            {
                return process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return false;
            }
        }

        public void Dispose() => process.Dispose();
    }
}

internal sealed class NamedPipeNetMeterChannelFactory : INetMeterChannelFactory
{
    public INetMeterChannel Create(string pipeName) => new NamedPipeNetMeterChannel(pipeName);
}

/// <summary>
/// The client end of the meter's pipe. It connects with the identification impersonation level (the meter can see who we
/// are, it can never act as us) and talks the fixed protocol of <see cref="NetMeterProtocol"/>: write a request, read a
/// length-prefixed reply. A reply that is late is never waited for again: the caller drops the channel and reconnects.
/// </summary>
internal sealed class NamedPipeNetMeterChannel(string pipeName) : INetMeterChannel
{
    private readonly NamedPipeClientStream _pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
    private readonly object _writeLock = new();
    private volatile bool _connected;

    public uint ServerProcessId
    {
        get
        {
            try
            {
                return _connected && NetMeterNative.GetNamedPipeServerProcessId(_pipe.SafePipeHandle, out var pid) ? pid : 0;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
            {
                return 0;
            }
        }
    }

    public bool TryConnect(TimeSpan timeout)
    {
        try
        {
            _pipe.Connect((int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue));
            _connected = true;
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    public NetMeterExchange Read(TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            Write(NetMeterProtocol.EncodeRequest(NetMeterRequestKind.Read), cts.Token);

            var prefix = new byte[NetMeterProtocol.PrefixSize];
            _pipe.ReadExactlyAsync(prefix, cts.Token).AsTask().GetAwaiter().GetResult();
            if (!NetMeterProtocol.TryReadLength(prefix, NetMeterProtocol.MaxPayloadBytes, out var length))
            {
                return new NetMeterExchange(null, NetMeterFailure.BadReply);
            }

            var payload = new byte[length];
            _pipe.ReadExactlyAsync(payload, cts.Token).AsTask().GetAwaiter().GetResult();
            return NetMeterProtocol.TryDecodeReply(payload, out var reading, out _) && reading is not null
                ? new NetMeterExchange(reading, NetMeterFailure.None)
                : new NetMeterExchange(null, NetMeterFailure.BadReply);
        }
        catch (OperationCanceledException)
        {
            return new NetMeterExchange(null, NetMeterFailure.Timeout);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new NetMeterExchange(null, NetMeterFailure.Broken);
        }
    }

    public void SendStop()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            Write(NetMeterProtocol.EncodeRequest(NetMeterRequestKind.Stop), cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Nothing more to do: a meter that cannot be told ends by itself when this process ends or stops answering.
        }
    }

    public void Dispose() => _pipe.Dispose();

    private void Write(byte[] frame, CancellationToken ct)
    {
        // A Stop from another thread may arrive while a Read is on its way: one frame at a time.
        lock (_writeLock)
        {
            _pipe.WriteAsync(frame, ct).AsTask().GetAwaiter().GetResult();
        }
    }
}
