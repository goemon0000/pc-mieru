using System.Runtime.InteropServices;
using PcMieru.Wire;

namespace PcMieru.NetMeter;

/// <summary>Exit codes of the meter. The front end only sees "the process ended"; these are for people running it by hand.</summary>
internal static class MeterExit
{
    public const int Normal = 0;
    public const int NativeSearch = 1;
    public const int InvalidArguments = 2;
    public const int NotOurParent = 3;
    public const int PipeFailed = 4;
    public const int Internal = 5;
}

/// <summary>
/// The meter's life: serve the pipe to the one front end that started it, count until it is told to stop, and always leave
/// the ETW session behind it stopped. It ends when
/// <list type="bullet">
/// <item>the parent process exits (the parent's handle is part of every wait),</item>
/// <item>the client disconnects and does not come back within <see cref="ReconnectGraceMs"/>,</item>
/// <item>nobody connects within <see cref="FirstConnectMs"/> of the start,</item>
/// <item>a Stop request arrives, or Ctrl+C / close / logoff / shutdown is signalled.</item>
/// </list>
/// It writes nothing to disk, makes no network call and runs no command.
/// </summary>
internal static unsafe class MeterHost
{
    internal const int FirstConnectMs = 30_000;
    internal const int ReconnectGraceMs = 5_000;
    internal const int WriteTimeoutMs = 10_000;
    private const long TicksPerMs = 10_000;
    private const uint CtrlHandlerWaitMs = 5_000;

    private static SafeKernelHandle? s_stop;
    private static SafeKernelHandle? s_done;

    public static int Run(NetMeterArguments arguments)
    {
        using var stop = Native.CreateEventW(0, true, false, 0);
        using var done = Native.CreateEventW(0, true, false, 0);
        if (stop.IsInvalid || done.IsInvalid)
        {
            return MeterExit.Internal;
        }

        // Only the process that started us is ever served, and only while it is the same process (a pid can be reused).
        using var parent = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.SYNCHRONIZE, false, arguments.ParentPid);
        if (parent.IsInvalid
            || !ProcessResolver.TryTimes(parent, out var parentCreated, out var parentExited)
            || parentCreated != arguments.ParentCreatedFileTime
            || parentExited != 0)
        {
            return MeterExit.NotOurParent;
        }

        s_stop = stop;
        s_done = done;
        var handler = (nint)(delegate* unmanaged<uint, int>)&OnControl;
        var handlerSet = Native.SetConsoleCtrlHandler(handler, true);
        try
        {
            using var counters = new NetMeterCounters(new ProcessResolver(), MeterClock.Now);

            // The pipe first, so that a session that cannot start is reported to the front end instead of just vanishing.
            using var pipe = PipeServer.Create(arguments.PipeName, arguments.UserSid, parent, stop, () => counters.RemoveExited(), out _);
            if (pipe is null)
            {
                return MeterExit.PipeFailed;
            }

            var session = new EtwSession();
            try
            {
                _ = session.Start(arguments.SessionName, counters);
                Serve(arguments, pipe, session, counters);
                return MeterExit.Normal;
            }
            finally
            {
                // However we got here: the session is ours, and it must not outlive us.
                session.Stop();
            }
        }
        finally
        {
            if (handlerSet)
            {
                _ = Native.SetConsoleCtrlHandler(handler, false);
            }

            _ = Native.SetEvent(done);
            s_stop = null;
            s_done = null;
        }
    }

    private static void Serve(NetMeterArguments arguments, PipeServer pipe, EtwSession session, NetMeterCounters counters)
    {
        var deadline = MeterClock.Now() + (FirstConnectMs * TicksPerMs);
        while (true)
        {
            if (pipe.Connect(deadline) != PipeOutcome.Done)
            {
                return; // nobody came in time, the parent or a stop ended the wait, or the pipe failed
            }

            if (IsParent(pipe, arguments))
            {
                var outcome = ServeClient(pipe, session, counters);
                pipe.Disconnect();
                if (outcome is PipeOutcome.ParentGone or PipeOutcome.StopRequested)
                {
                    return;
                }

                // The front end went away: it has a few seconds to come back (a restart of its reader), then we are done.
                deadline = MeterClock.Now() + (ReconnectGraceMs * TicksPerMs);
            }
            else
            {
                // Somebody else of the same user. They never get a reply, and they do not extend our life: the deadline stays.
                pipe.Disconnect();
            }
        }
    }

    /// <summary>The client must be the process that started the meter: same pid and the same creation time.</summary>
    private static bool IsParent(PipeServer pipe, NetMeterArguments arguments)
    {
        var pid = pipe.ClientProcessId();
        if (pid == 0 || pid != arguments.ParentPid)
        {
            return false;
        }

        using var client = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        return !client.IsInvalid
            && ProcessResolver.TryTimes(client, out var created, out var exited)
            && created == arguments.ParentCreatedFileTime
            && exited == 0;
    }

    private static PipeOutcome ServeClient(PipeServer pipe, EtwSession session, NetMeterCounters counters)
    {
        while (true)
        {
            var outcome = pipe.ReadRequest(out var kind);
            if (outcome != PipeOutcome.Done)
            {
                return outcome;
            }

            if (kind == NetMeterRequestKind.Stop)
            {
                return PipeOutcome.StopRequested;
            }

            var status = session.Query();
            var reading = counters.Snapshot(status.EventsLost, status.BuffersLost, status.Healthy, status.ErrorCode);
            outcome = pipe.Write(NetMeterProtocol.EncodeReply(reading), WriteTimeoutMs);
            if (outcome != PipeOutcome.Done)
            {
                return outcome;
            }
        }
    }

    /// <summary>Ctrl+C, Ctrl+Break, close, logoff and shutdown: stop, and give the cleanup a moment before Windows ends the process.</summary>
    [UnmanagedCallersOnly]
    private static int OnControl(uint type)
    {
        try
        {
            var stop = s_stop;
            var done = s_done;
            if (stop is null || done is null)
            {
                return 0;
            }

            _ = Native.SetEvent(stop);
            _ = Native.WaitForSingleObject(done, CtrlHandlerWaitMs);
            return 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
