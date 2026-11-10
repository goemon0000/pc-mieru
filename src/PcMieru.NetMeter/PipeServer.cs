using System.Runtime.InteropServices;
using PcMieru.Wire;

namespace PcMieru.NetMeter;

/// <summary>How a wait or a pipe operation ended.</summary>
internal enum PipeOutcome
{
    /// <summary>It completed.</summary>
    Done,

    /// <summary>The front end (the parent process) has exited.</summary>
    ParentGone,

    /// <summary>A stop was requested (Ctrl, close, logoff, or the request on the pipe).</summary>
    StopRequested,

    /// <summary>The time given ran out.</summary>
    TimedOut,

    /// <summary>The client went away, sent nonsense, or the operation failed.</summary>
    Broken,
}

/// <summary>
/// The one named pipe the meter serves its counters on: created first (so nobody can squat the name; there is only ever one
/// instance), reachable by the user's own SID and nobody else, local clients only, overlapped so every wait can also notice
/// "the parent exited" and "stop". Requests are an 8-byte frame (see <see cref="NetMeterProtocol"/>); the reply is a frame
/// of its own. One client at a time.
/// </summary>
internal sealed unsafe class PipeServer : IDisposable
{
    private const int PipeBuffer = 64 * 1024;
    private const int WaitSliceMs = 5000;
    private const long TicksPerMs = 10_000;
    private const int SecurityAttributesSize = 24;

    private readonly SafeKernelHandle _pipe;
    private readonly SafeKernelHandle _io;
    private readonly SafeKernelHandle _parent;
    private readonly SafeKernelHandle _stop;
    private readonly Action _housekeeping;
    private readonly OverlappedIo* _overlapped;
    private readonly byte* _buffer;

    private PipeServer(SafeKernelHandle pipe, SafeKernelHandle io, SafeKernelHandle parent, SafeKernelHandle stop, Action housekeeping)
    {
        _pipe = pipe;
        _io = io;
        _parent = parent;
        _stop = stop;
        _housekeeping = housekeeping;
        _overlapped = (OverlappedIo*)NativeMemory.AllocZeroed((nuint)sizeof(OverlappedIo));
        _buffer = (byte*)NativeMemory.AllocZeroed(NetMeterProtocol.PrefixSize + NetMeterProtocol.RequestPayloadSize);
    }

    /// <summary>The SDDL of the pipe: a protected DACL with one entry, generic read and write for the front end's user.</summary>
    internal static string SddlFor(string userSid) => "D:P(A;;GRGW;;;" + userSid + ")";

    /// <summary>
    /// Creates the pipe. <paramref name="userSid"/> must already have passed <see cref="NetMeterArguments"/>' strict parser:
    /// it is put into an SDDL string, so it must be a plain SID and nothing that could carry another entry.
    /// </summary>
    public static PipeServer? Create(string pipeName, string userSid, SafeKernelHandle parent, SafeKernelHandle stop, Action housekeeping, out uint error)
    {
        error = 0;
        nint descriptor = 0;
        uint descriptorSize = 0;
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor(SddlFor(userSid), Native.SDDL_REVISION_1, &descriptor, &descriptorSize))
        {
            error = (uint)Marshal.GetLastWin32Error();
            return null;
        }

        var attributes = (byte*)NativeMemory.AllocZeroed(SecurityAttributesSize);
        try
        {
            *(uint*)attributes = SecurityAttributesSize;
            *(nint*)(attributes + 8) = descriptor;

            var path = @"\\.\pipe\" + pipeName;
            var pipe = Native.CreateNamedPipe(
                path,
                Native.PIPE_ACCESS_DUPLEX | Native.FILE_FLAG_FIRST_PIPE_INSTANCE | Native.FILE_FLAG_OVERLAPPED,
                Native.PIPE_TYPE_BYTE | Native.PIPE_READMODE_BYTE | Native.PIPE_WAIT | Native.PIPE_REJECT_REMOTE_CLIENTS,
                1,
                PipeBuffer,
                PipeBuffer,
                0,
                (nint)attributes);
            if (pipe.IsInvalid)
            {
                error = (uint)Marshal.GetLastWin32Error();
                pipe.Dispose();
                return null;
            }

            var io = Native.CreateEventW(0, true, false, 0);
            if (io.IsInvalid)
            {
                error = (uint)Marshal.GetLastWin32Error();
                pipe.Dispose();
                io.Dispose();
                return null;
            }

            return new PipeServer(pipe, io, parent, stop, housekeeping);
        }
        finally
        {
            NativeMemory.Free(attributes);
            _ = Native.LocalFree(descriptor);
        }
    }

    /// <summary>Waits for a client to connect, until <paramref name="deadline"/> (a <see cref="MeterClock"/> time).</summary>
    public PipeOutcome Connect(long deadline)
    {
        Begin();
        if (Native.ConnectNamedPipe(_pipe, _overlapped))
        {
            return PipeOutcome.Done;
        }

        var error = (uint)Marshal.GetLastWin32Error();
        if (error == Native.ERROR_PIPE_CONNECTED)
        {
            return PipeOutcome.Done;
        }

        return error == Native.ERROR_IO_PENDING ? Finish(deadline, out _) : PipeOutcome.Broken;
    }

    /// <summary>The process id of the connected client, or 0.</summary>
    public uint ClientProcessId()
    {
        uint pid = 0;
        return Native.GetNamedPipeClientProcessId(_pipe, &pid) ? pid : 0;
    }

    /// <summary>Drops the current client so the pipe can take the next one.</summary>
    public void Disconnect() => _ = Native.DisconnectNamedPipe(_pipe);

    /// <summary>Reads one request. Anything that is not a well-formed request breaks the connection.</summary>
    public PipeOutcome ReadRequest(out NetMeterRequestKind kind)
    {
        kind = default;
        var outcome = ReadExactly(_buffer, NetMeterProtocol.PrefixSize, deadline: 0);
        if (outcome != PipeOutcome.Done)
        {
            return outcome;
        }

        if (!NetMeterProtocol.TryReadLength(new ReadOnlySpan<byte>(_buffer, NetMeterProtocol.PrefixSize), NetMeterProtocol.RequestPayloadSize, out var length)
            || length != NetMeterProtocol.RequestPayloadSize)
        {
            return PipeOutcome.Broken;
        }

        outcome = ReadExactly(_buffer + NetMeterProtocol.PrefixSize, length, deadline: 0);
        if (outcome != PipeOutcome.Done)
        {
            return outcome;
        }

        return NetMeterProtocol.TryDecodeRequest(new ReadOnlySpan<byte>(_buffer + NetMeterProtocol.PrefixSize, length), out kind, out _)
            ? PipeOutcome.Done
            : PipeOutcome.Broken;
    }

    /// <summary>Writes one whole frame. A client that does not take it within the time is dropped.</summary>
    public PipeOutcome Write(byte[] frame, int timeoutMs)
    {
        var deadline = MeterClock.Now() + (timeoutMs * TicksPerMs);
        fixed (byte* data = frame)
        {
            var written = 0;
            while (written < frame.Length)
            {
                Begin();
                uint count;
                if (!Native.WriteFile(_pipe, data + written, (uint)(frame.Length - written), null, _overlapped))
                {
                    if ((uint)Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING)
                    {
                        return PipeOutcome.Broken;
                    }
                }

                var outcome = Finish(deadline, out count);
                if (outcome != PipeOutcome.Done)
                {
                    return outcome;
                }

                written += (int)count;
            }
        }

        return PipeOutcome.Done;
    }

    public void Dispose()
    {
        // No operation is pending here (every wait that gave up cancelled its own), so the buffers can go.
        _pipe.Dispose();
        _io.Dispose();
        NativeMemory.Free(_overlapped);
        NativeMemory.Free(_buffer);
    }

    private PipeOutcome ReadExactly(byte* target, int count, long deadline)
    {
        var read = 0;
        while (read < count)
        {
            Begin();
            if (!Native.ReadFile(_pipe, target + read, (uint)(count - read), null, _overlapped))
            {
                if ((uint)Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING)
                {
                    return PipeOutcome.Broken;
                }
            }

            var outcome = Finish(deadline, out var got);
            if (outcome != PipeOutcome.Done)
            {
                return outcome;
            }

            if (got == 0)
            {
                return PipeOutcome.Broken;
            }

            read += (int)got;
        }

        return PipeOutcome.Done;
    }

    private void Begin()
    {
        _ = Native.ResetEvent(_io);
        *_overlapped = default;
        _overlapped->Event = _io.DangerousGetHandle();
    }

    /// <summary>
    /// Waits for the operation just started (the pipe handle, the parent, the stop event, in that priority order). While it
    /// waits it runs the housekeeping about every 5 s. If it gives up it cancels the operation and waits for the
    /// cancellation, so nothing the system still holds a pointer to is freed.
    /// </summary>
    private PipeOutcome Finish(long deadline, out uint transferred)
    {
        transferred = 0;
        var handles = stackalloc nint[3];
        handles[0] = _parent.DangerousGetHandle();
        handles[1] = _stop.DangerousGetHandle();
        handles[2] = _io.DangerousGetHandle();

        var outcome = PipeOutcome.Done;
        while (true)
        {
            var slice = WaitSliceMs;
            if (deadline != 0)
            {
                var left = (deadline - MeterClock.Now()) / TicksPerMs;
                if (left <= 0)
                {
                    outcome = PipeOutcome.TimedOut;
                    break;
                }

                slice = (int)Math.Min(slice, left);
            }

            var result = Native.WaitForMultipleObjects(3, handles, false, (uint)slice);
            if (result == Native.WAIT_OBJECT_0 + 2)
            {
                uint count = 0;
                if (Native.GetOverlappedResult(_pipe, _overlapped, &count, false))
                {
                    transferred = count;
                    return PipeOutcome.Done;
                }

                return PipeOutcome.Broken;
            }

            if (result == Native.WAIT_OBJECT_0)
            {
                outcome = PipeOutcome.ParentGone;
                break;
            }

            if (result == Native.WAIT_OBJECT_0 + 1)
            {
                outcome = PipeOutcome.StopRequested;
                break;
            }

            if (result == Native.WAIT_TIMEOUT)
            {
                _housekeeping();
                continue;
            }

            outcome = PipeOutcome.Broken;
            break;
        }

        uint ignored = 0;
        _ = Native.CancelIoEx(_pipe, _overlapped);
        _ = Native.GetOverlappedResult(_pipe, _overlapped, &ignored, true);
        return outcome;
    }
}
