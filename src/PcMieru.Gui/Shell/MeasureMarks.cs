using System.Globalization;
using System.Text;

namespace PcMieru.Gui;

/// <summary>
/// The two marks <c>--measure &lt;file&gt;</c> leaves for <c>tools/measure.ps1</c> (DESIGN-A 10): when the process list was first painted with
/// rows (<c>first_list</c>), and when it was first painted with CPU values, which only exist from the second sample on (<c>cpu_list</c>).
/// Test-only: a normal start builds none of this (<see cref="Create"/> is called only for the switch), so nothing is opened, created or written.
/// <para>
/// The file is created (emptied) when the object is made, UTF-8 without a BOM, and gets one JSON object per line, each mark at most once and
/// <c>first_list</c> always before <c>cpu_list</c>: <c>{"mark":"first_list","utc":"2026-11-21T01:02:03.4567890Z"}</c>.
/// The time is <see cref="DateTime.UtcNow"/>, which .NET on Windows reads with GetSystemTimePreciseAsFileTime (sub-microsecond reading, 100 ns
/// format), so it is comparable with the process creation time the OS records for the same process.
/// A mark that cannot be written is simply missing (the meter reports "missing", never 0); the window is not disturbed by it.
/// </para>
/// </summary>
internal sealed class MeasureMarks
{
    public const string FirstList = "first_list";
    public const string CpuList = "cpu_list";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The list stops being watched this long after its first paint, whether or not both marks came (a mark that never came stays missing).</summary>
    public static readonly TimeSpan WatchTime = TimeSpan.FromSeconds(60);

    /// <summary>The list stops being watched after this many samples that came after its first paint, whether or not both marks came.</summary>
    public const int WatchSamples = 30;

    private readonly Action<string> _writeLine;
    private readonly Func<DateTime> _clock;
    private readonly List<string> _written = [];
    private DateTime? _firstPaintAt;
    private int _samplesSincePaint;

    public MeasureMarks(Action<string> writeLine, Func<DateTime>? clock = null)
    {
        _writeLine = writeLine;
        _clock = clock ?? (static () => DateTime.UtcNow);
    }

    /// <summary>The marks written so far, in order.</summary>
    public IReadOnlyList<string> Written => _written;

    /// <summary>Both marks are written: nothing is left to watch for.</summary>
    public bool Complete => _written.Count == 2;

    /// <summary>
    /// The watch ran out: <see cref="WatchTime"/> passed since the first paint, or <see cref="WatchSamples"/> samples came after it. A mark that has not
    /// been written by then stays missing and nothing is written any more.
    /// </summary>
    public bool Expired { get; private set; }

    /// <summary>Nothing is left to watch: both marks are written or the watch ran out. The list then drops its paint observer.</summary>
    public bool Done => Complete || Expired;

    /// <summary>
    /// One sample reached the process page. Counted only after the first paint (a slow first paint does not eat the allowance); the watch ends after
    /// <see cref="WatchSamples"/> of them or <see cref="WatchTime"/> since that paint, even when the CPU mark never came.
    /// </summary>
    public void Sampled()
    {
        if (Done || _firstPaintAt is not { } firstPaint)
        {
            return;
        }

        _samplesSincePaint++;
        if (_samplesSincePaint >= WatchSamples || _clock() - firstPaint >= WatchTime)
        {
            Expired = true;
        }
    }

    /// <summary>
    /// Makes the marker file (emptied if it exists) and returns the marks that append to it. Throws what the file system throws, so the caller can say
    /// so before any window opens.
    /// </summary>
    public static MeasureMarks Create(string path)
    {
        var full = Path.GetFullPath(path);
        File.WriteAllText(full, string.Empty, Utf8NoBom);
        return new MeasureMarks(line => File.AppendAllText(full, line + "\n", Utf8NoBom));
    }

    /// <summary>
    /// A paint of the process list finished. <paramref name="hasRows"/>: it drew at least one process row. <paramref name="hasCpuValues"/>: it drew a CPU
    /// cell that shows a value (the first sample has none). Each mark is written the first time its condition holds, at most once, and
    /// <c>cpu_list</c> never before <c>first_list</c>.
    /// </summary>
    public void Painted(bool hasRows, bool hasCpuValues)
    {
        if (Done)
        {
            return;
        }

        // One reading of the clock per paint: it is the paint's time for the mark and the start of the watch.
        var now = _clock();
        _firstPaintAt ??= now;
        if (now - _firstPaintAt >= WatchTime)
        {
            Expired = true;
            return;
        }

        if (_written.Count == 0 && hasRows)
        {
            Write(FirstList, now);
        }

        if (_written.Count == 1 && hasCpuValues)
        {
            Write(CpuList, now);
        }
    }

    private void Write(string mark, DateTime now)
    {
        // Counted even when the write fails: a mark is taken once, and a retry later would be a later time than the paint it names.
        _written.Add(mark);
        var utc = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        try
        {
            _writeLine("{\"mark\":\"" + mark + "\",\"utc\":\"" + utc + "\"}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The mark is missing in the file; the meter says so.
        }
    }
}
