using System.Diagnostics;
using System.Globalization;
using System.IO;
using PcMieru.Party.Import;

namespace PcMieru.Party;

/// <summary>
/// The pieces of the measuring start of PCMieru.Party.exe (<c>--measure &lt;dir&gt;</c>, DESIGN-C 7 and 8 step 7). WPF-free on purpose: the test project compiles this file in.
/// A measuring start works in one fresh folder and touches nothing else of the user: its state is <c>&lt;dir&gt;\state.json</c>, its resident mutex has its own name (a real Parts Life of the
/// user is never blocked or confused by it), and the time of its first measured value goes to <c>&lt;dir&gt;\first-value.txt</c> for tools/measure.ps1 to read.
/// </summary>
internal static class PartyMeasure
{
    /// <summary>The name of the state file inside the measuring folder (the same file name as the real home uses, in a different folder).</summary>
    internal const string StateFileName = "state.json";

    /// <summary>One line: milliseconds from process start to the first non-missing value on the board.</summary>
    internal const string FirstValueFileName = "first-value.txt";

    /// <summary>Appended to the real resident mutex name.</summary>
    internal const string MutexSuffix = ".measure";

    /// <summary>The resident mutex of a measuring start for the user this process runs as. Never the real one.</summary>
    internal static string ResidentName() => PartyMutexNames.CurrentResidentName() + MutexSuffix;

    /// <summary>The same for the user whose SID string is <paramref name="sid"/> (tests).</summary>
    internal static string ResidentNameFor(string sid) => PartyMutexNames.ResidentNameFor(sid) + MutexSuffix;

    /// <summary>Where the measuring start keeps its state: only inside <paramref name="dir"/>.</summary>
    internal static string StatePath(string dir) => Path.Combine(dir, StateFileName);

    internal static string FirstValuePath(string dir) => Path.Combine(dir, FirstValueFileName);

    /// <summary>When this process was created (UTC), from the OS.</summary>
    internal static DateTime ProcessStartUtc()
    {
        using var self = Process.GetCurrentProcess();
        return self.StartTime.ToUniversalTime();
    }

    /// <summary>Writes the one line of <see cref="FirstValueFileName"/> (whole milliseconds, invariant digits). Only the first call of a run should come here.</summary>
    internal static void WriteFirstValue(string dir, double milliseconds)
    {
        var text = Math.Max(0, Math.Round(milliseconds)).ToString("0", CultureInfo.InvariantCulture) + "\n";
        File.WriteAllText(FirstValuePath(dir), text);
    }
}
