using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Launch;

/// <summary>The times of the launch check that are not in <see cref="LaunchRequest"/> (which carries the 15 s of UI check and the 120 s of the whole attempt). A test shortens them.</summary>
public sealed record VerifyTimings
{
    /// <summary>The runner asks the program for a sign of life this often.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>An answer that is later than this is a failure (a UI thread that does not get to its queue for two seconds is a hung program).</summary>
    public TimeSpan PongDeadline { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The program must have drawn its window within this time of its start (or within the whole attempt, whichever is shorter).</summary>
    public TimeSpan DrawnBudget { get; init; } = TimeSpan.FromSeconds(120);
}

/// <summary>
/// The real "start the new version and see that it lives" (DESIGN-B 5, VerifyPending). It runs inside the runner, which holds the start gate EXCLUSIVELY for all of it, and starts
/// <c>I\PCMieru.exe --verify-update &lt;tx&gt; &lt;gate handle&gt; &lt;pipe handle&gt;</c> with limited inheritance (PROC_THREAD_ATTRIBUTE_HANDLE_LIST: exactly a duplicate of the exclusive gate handle and one end of a pipe
/// made for this attempt) in a job object that kills what is in it when it is closed (the program is created suspended, put in the job, and only then let run). Then:
/// <list type="number">
/// <item>the program must say <c>drawn</c> (its window is shown and painted) within <see cref="VerifyTimings.DrawnBudget"/>;</item>
/// <item>from then on the runner sends <c>ping n</c> every <see cref="VerifyTimings.PingInterval"/> and the program must answer <c>pong n</c> from its UI thread within <see cref="VerifyTimings.PongDeadline"/>;</item>
/// <item>after <see cref="LaunchRequest.UiCheck"/> (a monotonic clock) of good answers the runner sends <c>done</c>, the program closes, and its exit must come inside <see cref="LaunchRequest.Total"/> in all.</item>
/// </list>
/// A late answer, a dead program, a closed pipe, no <c>drawn</c> in time or an exit that does not come is <see cref="LaunchVerdict.Failed"/> and the job is killed. The transaction counts the attempts in its journal, so this
/// does not count them again. It names no network type and writes no file (the runner is NativeAOT; tools/publish.ps1 checks the imports of the built exe).
/// </summary>
public sealed class ProductionLaunchVerifier(GateEntry gate, VerifyTimings? timings = null) : ILaunchVerifier
{
    private readonly VerifyTimings _timings = timings ?? new VerifyTimings();

    /// <summary>The program to start. Null: <c>I\PCMieru.exe</c>. A test names a double that speaks the same protocol.</summary>
    internal string? Executable { get; init; }

    /// <summary>The arguments after the program's name, given the transaction id and the two handle values. Null: <see cref="VerifyAdmission.ArgumentsFor"/>.</summary>
    internal Func<string, nint, nint, string>? ArgumentsOf { get; init; }

    public LaunchVerdict LaunchAndVerify(LaunchRequest request)
    {
        var total = Stopwatch.StartNew();
        var exe = Executable ?? Path.Combine(request.InstallFolder, "PCMieru.exe");
        var gateValue = gate.DuplicateForChild();
        if (gateValue == 0)
        {
            return LaunchVerdict.Failed;
        }

        SafeFileHandle server;
        SafeFileHandle client;
        if (!LaunchNative.TryCreatePipe(out server, out client))
        {
            LaunchNative.CloseHandle(gateValue);
            return LaunchVerdict.Failed;
        }

        LaunchNative.Child? child = null;
        PipeChannel? channel = null;
        try
        {
            var clientValue = client.DangerousGetHandle();
            var arguments = (ArgumentsOf ?? VerifyAdmission.ArgumentsFor)(request.Tx, gateValue, clientValue);
            child = LaunchNative.TryStart(
                new LaunchNative.StartRequest(exe, LaunchNative.Quote(exe) + " " + arguments, request.InstallFolder, [gateValue, clientValue], NoWindow: false, InJob: true),
                out _);

            // The child has its own copies now (or there is no child): these two are closed here so that nothing of the runner keeps the exclusive gate or the pipe's far end alive after the child is gone.
            LaunchNative.CloseHandle(gateValue);
            gateValue = 0;
            client.Dispose();
            if (child is null)
            {
                return LaunchVerdict.Failed;
            }

            channel = new PipeChannel(server);
            server = new SafeFileHandle(); // owned by the channel from here
            var verdict = Judge(request, child, channel, total);
            if (verdict != LaunchVerdict.Ok)
            {
                child.Kill();
            }

            return verdict;
        }
        finally
        {
            if (gateValue != 0)
            {
                LaunchNative.CloseHandle(gateValue);
            }

            channel?.Dispose();
            if (!server.IsInvalid)
            {
                server.Dispose();
            }

            client.Dispose();
            child?.Dispose(); // closing the job is what ends whatever of the program is still running
        }
    }

    private LaunchVerdict Judge(LaunchRequest request, LaunchNative.Child child, PipeChannel channel, Stopwatch total)
    {
        var drawnBy = _timings.DrawnBudget < request.Total ? _timings.DrawnBudget : request.Total;
        var slice = TimeSpan.FromMilliseconds(50);

        // 1. drawn
        while (true)
        {
            var leftToDraw = drawnBy - total.Elapsed;
            if (leftToDraw <= TimeSpan.Zero)
            {
                return LaunchVerdict.Failed;
            }

            if (channel.TryReceive(leftToDraw < slice ? leftToDraw : slice, out var line))
            {
                if (line == VerifyProtocol.Drawn && total.Elapsed < drawnBy)
                {
                    break;
                }

                continue;
            }

            if (child.HasExited || channel.IsClosed)
            {
                return LaunchVerdict.Failed;
            }
        }

        // 2. pings, for UiCheck by the monotonic clock
        var check = Stopwatch.StartNew();
        var n = 0;
        while (check.Elapsed < request.UiCheck)
        {
            if (total.Elapsed >= request.Total)
            {
                return LaunchVerdict.Failed;
            }

            n++;
            var sent = Stopwatch.StartNew();
            var pongBy = request.Total - total.Elapsed;
            if (pongBy > _timings.PongDeadline)
            {
                pongBy = _timings.PongDeadline;
            }

            if (!channel.Send(VerifyProtocol.Ping(n), pongBy) || !AnsweredInTime(child, channel, n, sent, pongBy))
            {
                return LaunchVerdict.Failed;
            }

            var rest = _timings.PingInterval - sent.Elapsed;
            var leftInAttempt = request.Total - total.Elapsed;
            if (rest > leftInAttempt)
            {
                rest = leftInAttempt;
            }

            if (rest > TimeSpan.Zero && child.WaitForExit(rest))
            {
                return LaunchVerdict.Failed; // it ended while it was supposed to be alive
            }

            if (child.HasExited)
            {
                return LaunchVerdict.Failed;
            }
        }

        // 3. done, and the exit
        var remaining = request.Total - total.Elapsed;
        if (!channel.Send(VerifyProtocol.Done, remaining))
        {
            return LaunchVerdict.Failed;
        }

        var left = request.Total - total.Elapsed;
        return left > TimeSpan.Zero && child.WaitForExit(left) && child.ExitCode == VerifyExit.Done ? LaunchVerdict.Ok : LaunchVerdict.Failed;
    }

    private static bool AnsweredInTime(LaunchNative.Child child, PipeChannel channel, int n, Stopwatch sent, TimeSpan deadline)
    {
        while (deadline - sent.Elapsed is var left && left > TimeSpan.Zero)
        {
            var slice = TimeSpan.FromMilliseconds(50);
            if (channel.TryReceive(left < slice ? left : slice, out var line))
            {
                if (VerifyProtocol.NumberOf(line, VerifyProtocol.PongPrefix) == n)
                {
                    return sent.Elapsed < deadline;
                }

                continue;
            }

            if (child.HasExited || channel.IsClosed)
            {
                return false;
            }
        }

        return false;
    }
}
