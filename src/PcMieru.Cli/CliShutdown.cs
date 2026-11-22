using System.Diagnostics;

namespace PcMieru.Cli;

/// <summary>
/// How long the process waits after Ctrl+C before it leaves (DESIGN-A 8). Ctrl+C only cancels the token; the grace period is for a read
/// blocked on stdin (<c>pcmi mcp</c> in a terminal), which a token cannot interrupt, and ends the process after 2 seconds. A running check is
/// different: a stopped check finishes its chunk, joins its workers and saves the stopped result, so cutting it at 2 seconds could leave
/// no result at all. A command that is in the middle of such work holds the shutdown for the while, and the grace is longer then
/// (<see cref="HeldGrace"/>). A second Ctrl+C always leaves at once.
/// </summary>
public static class CliShutdown
{
    /// <summary>The wait with nothing held (the old fixed 2 seconds).</summary>
    public static readonly TimeSpan BaseGrace = TimeSpan.FromSeconds(2);

    /// <summary>The longest the process waits for held work after Ctrl+C.</summary>
    public static readonly TimeSpan HeldGrace = TimeSpan.FromSeconds(20);

    private static int s_holds;

    /// <summary>True while a command holds the shutdown.</summary>
    public static bool IsHeld => Volatile.Read(ref s_holds) > 0;

    /// <summary>Held until the returned object is disposed (once; a second dispose changes nothing).</summary>
    public static IDisposable Hold()
    {
        Interlocked.Increment(ref s_holds);
        return new Release();
    }

    /// <summary>
    /// Completes when the process may leave: <see cref="BaseGrace"/> is over and nothing is held, or <see cref="HeldGrace"/> is over.
    /// Nothing held is answered after <see cref="BaseGrace"/> alone, exactly as before.
    /// </summary>
    public static Task WaitForLeaveAsync() => WaitForLeaveAsync(BaseGrace, HeldGrace, TimeSpan.FromMilliseconds(100));

    internal static async Task WaitForLeaveAsync(TimeSpan baseGrace, TimeSpan heldGrace, TimeSpan poll)
    {
        var started = Stopwatch.GetTimestamp();
        await Task.Delay(baseGrace).ConfigureAwait(false);
        while (IsHeld && Stopwatch.GetElapsedTime(started) < heldGrace)
        {
            await Task.Delay(poll).ConfigureAwait(false);
        }
    }

    private sealed class Release : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Decrement(ref s_holds);
            }
        }
    }
}
