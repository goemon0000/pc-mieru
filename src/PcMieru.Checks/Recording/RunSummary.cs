namespace PcMieru.Checks;

/// <summary>How exact the seconds of a summary are. An old <c>partslife-run/1</c> run only has "the row time", so its seconds are an estimate.</summary>
public enum SummaryFreshness
{
    Exact,

    /// <summary>"old format, estimated": the row time stands for the acquire time and the old nominal period for P (nothing was recorded about repeats).</summary>
    LegacyEstimated,
}

/// <summary>
/// One metric over the whole run (DESIGN-B §7.2). <see cref="ObservedSeconds"/> is the time a value was known (an interval that starts at a new
/// acquisition and is no longer than 3P); <see cref="MissingSeconds"/> is the rest of the run for THIS metric. The two are never added across
/// metrics into one run total. <see cref="NewPoints"/> counts new values only (a repeat of the same frame adds nothing).
/// <see cref="Sustained"/> is the time-weighted median over the loaded part of the last window (see <see cref="RunSummarizer"/>); null when there is none.
/// </summary>
public sealed record RunMetricSummary(
    string Key,
    string Unit,
    string Source,
    long NewPoints,
    double ObservedSeconds,
    double MissingSeconds,
    double Min,
    double Avg,
    double Max,
    double? Sustained);

/// <summary>One reason of a limit. <see cref="SecondsWhileLoaded"/> is the part of <see cref="Seconds"/> where the device was under load.</summary>
public sealed record RunLimitReason(string Name, double Seconds, double SecondsWhileLoaded);

/// <summary>
/// The limit marks of a device. A GPU's are the driver's clock-event reasons (a bit per reason); the CPU's come from the OS indicators of an old run.
/// It only says the mark was set, not that anything is wrong. <see cref="ObservedSeconds"/> is the denominator of <see cref="RunLimitReason.Seconds"/>;
/// <see cref="LoadObservedSeconds"/> (the part where the load was also observed) is the denominator of the loaded breakdown, and <see cref="LoadedSeconds"/> is the loaded part of it.
/// </summary>
public sealed record RunLimitSummary(
    string Device,
    double ObservedSeconds,
    double LoadObservedSeconds,
    double LoadedSeconds,
    IReadOnlyList<RunLimitReason> Reasons);

/// <summary>
/// A time window where the temperature rose and the clock fell. It only says they happened in the same time window; the cause cannot be told from this record.
/// </summary>
public sealed record RunCoOccurrence(
    string Device,
    double StartSec,
    double EndSec,
    double TempFromC,
    double TempToC,
    double ClockFromMhz,
    double ClockToMhz,
    double AvgLoadPct);

public sealed record RunSectionMetric(string Key, string Unit, double ObservedSeconds, double Min, double Avg, double Max, double Median);

/// <summary>
/// A bench or stress section cut out by its marks. A section whose end mark never came (or that the next start mark closed) is <see cref="Open"/> and runs to the next start or the end of the run.
/// </summary>
public sealed record RunSection(string Kind, double StartSec, double EndSec, bool Open, IReadOnlyList<RunSectionMetric> Metrics);

/// <summary>A note on a summary. <see cref="Code"/> and <see cref="Subject"/> are language independent; the sentence is built from <c>gui.rec.note.*</c>.</summary>
public sealed record RunNote(string Code, string Subject, string Detail = "");

/// <summary>The summary of a run, built from the recorded seconds only (never from a row count). See <see cref="RunSummarizer"/>.</summary>
public sealed class RunSummary
{
    public double DurationSec { get; init; }

    public long Rows { get; init; }

    public long DroppedTicks { get; init; }

    public SummaryFreshness Freshness { get; init; }

    /// <summary>The length of the window the sustained values were taken from, 0 when none could be.</summary>
    public double SustainedWindowSec { get; init; }

    public IReadOnlyList<RunMetricSummary> Metrics { get; init; } = [];

    public IReadOnlyList<RunLimitSummary> Limits { get; init; } = [];

    public IReadOnlyList<RunCoOccurrence> CoOccurrences { get; init; } = [];

    public IReadOnlyList<RunSection> Sections { get; init; } = [];

    public IReadOnlyList<RunNote> Notes { get; init; } = [];

    public RunMetricSummary? Metric(string key)
    {
        foreach (var metric in Metrics)
        {
            if (metric.Key == key)
            {
                return metric;
            }
        }

        return null;
    }

    public bool HasNote(string code) => Notes.Any(n => n.Code == code);
}

/// <summary>The driver's clock-event reasons (the old <c>ThrottleBits</c>, Summarizer.cs lines 5-20) and the name for a bit this version does not name.</summary>
public static class RunLimitBits
{
    public const string UnknownName = "unknown-bits";

    public static readonly (ulong Bit, string Name)[] All =
    [
        (0x1, "gpu-idle"),
        (0x2, "applications-clocks-setting"),
        (0x4, "sw-power-cap"),
        (0x8, "hw-slowdown"),
        (0x10, "sync-boost"),
        (0x20, "sw-thermal-slowdown"),
        (0x40, "hw-thermal-slowdown"),
        (0x80, "hw-power-brake-slowdown"),
        (0x100, "display-clock-setting"),
    ];

    /// <summary>The bits no entry of <see cref="All"/> names.</summary>
    public static ulong Unnamed { get; } = ~All.Aggregate(0UL, (mask, e) => mask | e.Bit);
}
