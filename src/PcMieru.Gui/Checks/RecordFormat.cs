using System.Globalization;
using PcMieru.Checks;
using PcMieru.Core;

namespace PcMieru.Gui;

/// <summary>
/// How the recording tab writes a time, a length, a state and a reading. One place, so the list, the summary, the status bar and the tests say
/// the same thing. A length is exact only for a run that ended normally under this app; every other length is "about" (see <see cref="ListLength"/>).
/// </summary>
internal static class RecordFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>When a run started, in the PC's local time and the order of the UI language (2026/11/22 14:05:09, 2026-11-22 14:05:09).</summary>
    public static string StartText(DateTime utc)
    {
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        return local.ToString(Strings.Current == Language.Ja ? "yyyy/MM/dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss", Inv);
    }

    /// <summary>The time a recording has been going: 1:05, or 1:02:03 from an hour on.</summary>
    public static string Elapsed(TimeSpan span)
    {
        var whole = (long)Math.Max(0, Math.Floor(span.TotalSeconds));
        var hours = whole / 3600;
        var minutes = whole % 3600 / 60;
        var seconds = whole % 60;
        return hours > 0
            ? hours.ToString(Inv) + ":" + minutes.ToString("00", Inv) + ":" + seconds.ToString("00", Inv)
            : minutes.ToString(Inv) + ":" + seconds.ToString("00", Inv);
    }

    /// <summary>"3 分 5 秒": a length that was counted from the recorded times (a finished run, or a run that was opened and read to its end).</summary>
    public static string ExactLength(double seconds)
    {
        var whole = (long)Math.Max(0, Math.Round(seconds, MidpointRounding.AwayFromZero));
        if (whole < 60)
        {
            return Strings.Format("gui.rtab.len.sec", Fmt.Dict(("s", whole.ToString(Inv))));
        }

        var minutes = whole / 60 % 60;
        var rest = whole % 60;
        return whole < 3600
            ? Strings.Format("gui.rtab.len.min", Fmt.Dict(("m", (whole / 60).ToString(Inv)), ("s", rest.ToString(Inv))))
            : Strings.Format("gui.rtab.len.hour", Fmt.Dict(("h", (whole / 3600).ToString(Inv)), ("m", minutes.ToString(Inv)), ("s", rest.ToString(Inv))));
    }

    /// <summary>"約 12 分", "約 1 時間 5 分", "1 分未満": a length the JSON gave, which may be a flush behind the CSV (a cut-off run, an old record).</summary>
    public static string ApproxLength(long durationUs)
    {
        var minutes = (long)Math.Max(0, Math.Round(durationUs / 60_000_000.0, MidpointRounding.AwayFromZero));
        if (minutes < 1)
        {
            return Strings.Get("gui.rtab.len.under");
        }

        if (minutes < 60)
        {
            return Strings.Format("gui.rtab.len.about.min", Fmt.Dict(("m", minutes.ToString(Inv))));
        }

        return minutes % 60 == 0
            ? Strings.Format("gui.rtab.len.about.hour0", Fmt.Dict(("h", (minutes / 60).ToString(Inv))))
            : Strings.Format("gui.rtab.len.about.hour", Fmt.Dict(("h", (minutes / 60).ToString(Inv)), ("m", (minutes % 60).ToString(Inv))));
    }

    /// <summary>
    /// The length a list row shows. Exact only for a run that completed under this app: the header of a run that is being recorded, was cut off,
    /// failed or comes from the old app can be behind the data (or estimated), so those are "about".
    /// </summary>
    public static string ListLength(RunInfo run) =>
        run is { Status: RunStatus.Completed, IsLegacy: false } ? ExactLength(run.DurationUs / 1_000_000.0) : ApproxLength(run.DurationUs);

    /// <summary>True when <see cref="ListLength"/> gives an estimate for this run (the list explains "about" in its footer).</summary>
    public static bool IsApproximate(RunInfo run) => run is not { Status: RunStatus.Completed, IsLegacy: false };

    public static string StatusWord(RunStatus status) => Strings.Get("gui.rtab.status." + RunCodes.Of(status));

    /// <summary>
    /// What to say about how a run ended, in plain words, or an empty text when it ended the ordinary way (the user stopped it). A run that was
    /// found without an end says that no end was saved; an old record says what it is.
    /// </summary>
    public static string NoteText(RunInfo run)
    {
        if (run.IsLegacy)
        {
            return Strings.Get("gui.rtab.legacy.tag");
        }

        return run.StopReason switch
        {
            null or RunStopReason.User => string.Empty,
            RunStopReason.Unknown => Strings.Get("gui.rtab.reason.unknown"),
            { } reason => Strings.Get(RecordStrings.KeyOf(reason)),
        };
    }

    /// <summary>Where recordings are kept, short enough for one line: the Documents folder is written by its name, a very long path loses its middle.</summary>
    public static string ShortFolder(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length > 0 && path.StartsWith(documents, StringComparison.OrdinalIgnoreCase)
            && (path.Length == documents.Length || path[documents.Length] is '\\' or '/'))
        {
            path = Strings.Get("gui.rtab.folder.documents") + path[documents.Length..];
        }

        const int Limit = 60;
        if (path.Length <= Limit)
        {
            return path;
        }

        var tail = path[^(Limit / 2)..];
        var cut = tail.IndexOfAny(['\\', '/']);
        if (cut > 0)
        {
            tail = tail[cut..];
        }

        return path[..(Limit / 3)] + "…" + tail;
    }

    /// <summary>A reading with its unit ("62.4 °C", "2,610 MHz", "12 %", "1.4 GB"). A value that is not a number is "not measured", never 0.</summary>
    public static string Value(double value, string unit)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return Strings.Get("gui.rtab.sum.notmeasured");
        }

        return unit switch
        {
            "B" => Fmt.Bytes((long)Math.Round(value)),
            "%" => value.ToString("0.#", CultureInfo.CurrentCulture) + " %",
            "MHz" => value.ToString("N0", CultureInfo.CurrentCulture) + " MHz",
            "W" => value.ToString("0.#", CultureInfo.CurrentCulture) + " W",
            "°C" => value.ToString("0.#", CultureInfo.CurrentCulture) + " °C",
            "" => value.ToString("0.##", CultureInfo.CurrentCulture),
            _ => value.ToString("0.##", CultureInfo.CurrentCulture) + " " + unit,
        };
    }

    /// <summary>A number of seconds as the summary counts them: whole seconds, one decimal under ten.</summary>
    public static string Seconds(double seconds) =>
        seconds < 10 ? seconds.ToString("0.#", CultureInfo.CurrentCulture) : seconds.ToString("N0", CultureInfo.CurrentCulture);

    public static string Whole(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
