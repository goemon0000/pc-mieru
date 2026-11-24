using System.Diagnostics;
using Xunit;

namespace PcMieru.TestSupport;

// Linked into the Update, Gui, Gate and Core test projects (tests/Shared): one place that says how a test waits for a child.

/// <summary>
/// Runs a child process of a test with a time limit. A test must never wait on a child without one: on a hosted CI runner a child that does not end (a console that waits for a key, a program that waits for a
/// handle) holds the whole test run until the job's limit, and the log does not say which test it was. Here the wait ends at the limit, the child and its children
/// are killed, and the test fails with the name of the program and what it had said.
/// </summary>
internal static class BoundedProcess
{
    /// <summary>The limit when a test gives none: far above what any helper of these tests needs (<c>mklink</c> is milliseconds), far below the job's.</summary>
    internal static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(60);

    /// <summary>How long the last words of an ended child may take to arrive (its pipes close when it ends, unless something it started holds them).</summary>
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(15);

    internal readonly record struct Result(int ExitCode, string Output);

    /// <summary>Starts the child with both output streams read, waits at most <paramref name="limit"/>, and returns its exit code and everything it wrote. Past the limit the child's whole tree is killed and the test fails.</summary>
    internal static Result Run(ProcessStartInfo info, TimeSpan? limit = null)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        var bound = limit ?? DefaultLimit;

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"'{info.FileName}' did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(checked((int)bound.TotalMilliseconds)))
        {
            Kill(process);
            Assert.Fail($"'{Path.GetFileName(info.FileName)}' did not end within {bound.TotalSeconds:0} s (killed). It had said: {Drained(stdout, stderr)}");
        }

        return new Result(process.ExitCode, Drained(stdout, stderr));
    }

    /// <summary>Waits for a child a test started itself and keeps a handle of. False at the limit; the child is then killed (its tree too), so nothing is left behind.</summary>
    internal static bool WaitOrKill(Process process, TimeSpan limit)
    {
        if (process.WaitForExit(checked((int)limit.TotalMilliseconds)))
        {
            return true;
        }

        Kill(process);
        return false;
    }

    internal static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(15_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // already gone
        }
    }

    /// <summary>What the child wrote; a pipe that is still held open by something the child started is given up after <see cref="DrainLimit"/> (what arrived so far is returned).</summary>
    internal static string Drained(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            Task.WaitAll([stdout, stderr], DrainLimit);
        }
        catch (AggregateException)
        {
            // a stream that failed has nothing more to say
        }

        return (stdout.IsCompletedSuccessfully ? stdout.Result : string.Empty) + (stderr.IsCompletedSuccessfully ? stderr.Result : string.Empty);
    }
}
