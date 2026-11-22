using System.Globalization;

namespace PcMieru.Checks;

/// <summary>
/// The fixed words of the run files (DESIGN-B §7.1): the codes written in the JSON and the CSV and the keys of the texts in <see cref="RecordStrings"/>.
/// </summary>
public static class RunCodes
{
    public const string Format = "pcm-run/1";
    public const string LegacyFormat = "partslife-run/1";

    public static string Of(RunStatus status) => status switch
    {
        RunStatus.Recording => "recording",
        RunStatus.Completed => "completed",
        RunStatus.Interrupted => "interrupted",
        _ => "failed",
    };

    public static bool TryParse(string? code, out RunStatus status)
    {
        switch (code)
        {
            case "recording": status = RunStatus.Recording; return true;
            case "completed": status = RunStatus.Completed; return true;
            case "interrupted": status = RunStatus.Interrupted; return true;
            case "failed": status = RunStatus.Failed; return true;
            default: status = RunStatus.Failed; return false;
        }
    }

    public static string Of(RunStopReason reason) => reason switch
    {
        RunStopReason.User => "user",
        RunStopReason.WindowClose => "window_close",
        RunStopReason.LowSpace => "low_space",
        RunStopReason.IoError => "io_error",
        RunStopReason.ProcessExit => "process_exit",
        _ => "unknown",
    };

    public static bool TryParse(string? code, out RunStopReason reason)
    {
        switch (code)
        {
            case "user": reason = RunStopReason.User; return true;
            case "window_close": reason = RunStopReason.WindowClose; return true;
            case "low_space": reason = RunStopReason.LowSpace; return true;
            case "io_error": reason = RunStopReason.IoError; return true;
            case "process_exit": reason = RunStopReason.ProcessExit; return true;
            case "unknown": reason = RunStopReason.Unknown; return true;
            default: reason = RunStopReason.Unknown; return false;
        }
    }

    public static string Of(MarkerKind kind) => kind switch
    {
        MarkerKind.BenchStart => "bench-start",
        MarkerKind.BenchEnd => "bench-end",
        MarkerKind.StressStart => "stress-start",
        MarkerKind.StressEnd => "stress-end",
        MarkerKind.Warning => "warning",
        _ => "mark",
    };

    public static bool TryParse(string? code, out MarkerKind kind)
    {
        switch (code)
        {
            case "bench-start": kind = MarkerKind.BenchStart; return true;
            case "bench-end": kind = MarkerKind.BenchEnd; return true;
            case "stress-start": kind = MarkerKind.StressStart; return true;
            case "stress-end": kind = MarkerKind.StressEnd; return true;
            case "mark": kind = MarkerKind.Mark; return true;
            case "warning": kind = MarkerKind.Warning; return true;
            default: kind = MarkerKind.Mark; return false;
        }
    }

    public static string Of(RunMetricKind kind) => kind switch
    {
        RunMetricKind.Flag => "flag",
        RunMetricKind.Bitmask => "bitmask",
        RunMetricKind.Text => "text",
        _ => "number",
    };

    public static bool TryParse(string? code, out RunMetricKind kind)
    {
        switch (code)
        {
            case "number": kind = RunMetricKind.Number; return true;
            case "flag": kind = RunMetricKind.Flag; return true;
            case "bitmask": kind = RunMetricKind.Bitmask; return true;
            case "text": kind = RunMetricKind.Text; return true;
            default: kind = RunMetricKind.Number; return false;
        }
    }

    /// <summary>The <c>&lt;src&gt;.state</c> cell values.</summary>
    public const string StateFresh = "fresh";

    public const string StateRepeat = "repeat";
    public const string StateMissing = "missing";

    /// <summary>The <c>&lt;src&gt;.reason</c> codes of a <c>missing</c> row.</summary>
    public const string ReasonHostRestart = "host_restart";

    public const string ReasonReadFailed = "read_failed";
    public const string ReasonNotStarted = "not_started";
    public const string ReasonUnsupported = "unsupported";

    /// <summary>The GPU is not in this sample at all (it vanished after the run began).</summary>
    public const string ReasonDeviceGone = "device_gone";

    /// <summary>A frame was expected and there was none, with no reason given.</summary>
    public const string ReasonNoFrame = "no_frame";

    public static IReadOnlyList<string> Reasons { get; } =
    [
        ReasonHostRestart, ReasonReadFailed, ReasonNotStarted, ReasonUnsupported, ReasonDeviceGone, ReasonNoFrame,
    ];

    internal static string ReasonOf(PcMieru.Core.SourceGap gap) => gap switch
    {
        PcMieru.Core.SourceGap.HostRestart => ReasonHostRestart,
        PcMieru.Core.SourceGap.ReadFailed => ReasonReadFailed,
        PcMieru.Core.SourceGap.NotStarted => ReasonNotStarted,
        PcMieru.Core.SourceGap.Unsupported => ReasonUnsupported,
        _ => ReasonNoFrame,
    };

    /// <summary>The run id of a file name part: the lower-case "D" form of the GUID.</summary>
    public static string IdText(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>UTC as the files write it: ISO 8601 with milliseconds and a trailing Z.</summary>
    public static string Utc(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
