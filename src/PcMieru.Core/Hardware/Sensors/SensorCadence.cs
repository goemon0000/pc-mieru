using System.Globalization;

namespace PcMieru.Core.Hardware.Sensors;

/// <summary>
/// The cadence control of PCMieru.SensorHost, kept in Core so it can be tested without starting the child (SensorHost uses it through
/// the internals grant). The parent writes one line to the child's stdin per change: <c>1000</c> or <c>2000</c> (milliseconds). The reader
/// thread only records the request; the sampling loop applies it with <see cref="ApplyPending"/> at the next sampling boundary, so a frame
/// is never half at one period and half at the other, and the frame says the period it was actually taken at. EOF is not a request: the
/// child ends itself when stdin closes.
/// </summary>
internal sealed class SensorCadence
{
    /// <summary>The period while a recording lease is held.</summary>
    public const int FastMs = 1000;

    /// <summary>The ordinary period.</summary>
    public const int SlowMs = 2000;

    private int _periodMs;
    private int _pendingMs;

    /// <param name="periodMs">The period the child starts at (its <c>--interval-ms</c>).</param>
    public SensorCadence(int periodMs)
    {
        _periodMs = periodMs;
    }

    /// <summary>The period the sampling loop is running at right now. Changes only inside <see cref="ApplyPending"/>.</summary>
    public int PeriodMs => Volatile.Read(ref _periodMs);

    /// <summary>The wire form of a request: the number of milliseconds, nothing else.</summary>
    public static string Line(int periodMs) => periodMs.ToString(CultureInfo.InvariantCulture);

    /// <summary>True when <paramref name="periodMs"/> is one of the two periods the control accepts.</summary>
    public static bool IsAllowed(int periodMs) => periodMs is FastMs or SlowMs;

    /// <summary>
    /// A line from stdin as a request. Only the two exact values count; anything else (an empty line, text, a number such as 1500) is
    /// ignored and returns false. Safe to call from the stdin reader thread while the sampling loop runs.
    /// </summary>
    public bool TryRequest(string? line)
    {
        if (line is null
            || !int.TryParse(line.AsSpan().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
            || !IsAllowed(ms))
        {
            return false;
        }

        Volatile.Write(ref _pendingMs, ms);
        return true;
    }

    /// <summary>
    /// Called by the sampling loop at a sampling boundary (just before it takes the next frame). Adopts the latest request, if any, and
    /// returns the period the next frame is taken at. The sampling loop is the only caller.
    /// </summary>
    public int ApplyPending()
    {
        var pending = Interlocked.Exchange(ref _pendingMs, 0);
        if (pending != 0)
        {
            Volatile.Write(ref _periodMs, pending);
        }

        return PeriodMs;
    }
}
