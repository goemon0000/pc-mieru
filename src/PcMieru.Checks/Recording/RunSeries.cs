namespace PcMieru.Checks;

/// <summary>
/// One acquisition of one metric, or the observation that it could not be read (<see cref="Missing"/>). <see cref="AtUs"/> is on the run's
/// monotonic clock (microseconds since the start); a missing point is placed at the time it was OBSERVED (the row), a value at the time it was
/// ACQUIRED. <see cref="PeriodMs"/> is the nominal period P of the frame the value came from, as the frame reported it.
/// </summary>
internal readonly record struct SeriesPoint(long AtUs, int PeriodMs, bool Missing, double Value, ulong Bits);

/// <summary>
/// The acquisitions of one column in time order (the repeats are not here: they add no value and no seconds). The summary reads nothing else
/// of a run. A bit mask keeps its exact value in <see cref="SeriesPoint.Bits"/>; <see cref="SeriesPoint.Value"/> is not used for it.
/// </summary>
internal sealed class MetricTrack(string key, string unit, string source, RunMetricKind kind)
{
    public string Key { get; } = key;

    public string Unit { get; } = unit;

    public string Source { get; } = source;

    public RunMetricKind Kind { get; } = kind;

    public List<SeriesPoint> Points { get; } = [];

    /// <summary>The reason codes of the source's missing rows (distinct, in the order they first appeared), comma separated; null when there were none.</summary>
    public string? Reasons { get; set; }

    /// <summary>
    /// Adds a new value. A point at or before the last one is ignored (a repeat or an out-of-order frame is not a new acquisition); a "could not
    /// be read" mark that was placed later than this value was acquired gives way to it.
    /// </summary>
    public void AddValue(long atUs, int periodMs, double value, ulong bits)
    {
        while (Points.Count > 0 && Points[^1].AtUs >= atUs)
        {
            if (!Points[^1].Missing)
            {
                return;
            }

            Points.RemoveAt(Points.Count - 1);
        }

        Points.Add(new SeriesPoint(atUs, periodMs, false, value, bits));
    }

    /// <summary>Adds "could not be read at this time" (the end of the known interval before it). Several in a row are one.</summary>
    public void AddMissing(long atUs)
    {
        if (Points.Count > 0)
        {
            var last = Points[^1];
            if (last.Missing)
            {
                return;
            }

            atUs = Math.Max(atUs, last.AtUs);
        }

        Points.Add(new SeriesPoint(atUs, 0, true, 0, 0));
    }
}

/// <summary>A run as the summary sees it: the tracks, the marks and how it ended.</summary>
internal sealed class RunSeries
{
    public List<MetricTrack> Tracks { get; } = [];

    public IReadOnlyList<RunMarker> Markers { get; init; } = [];

    /// <summary>The end of the recording range. For a normal stop, the stop time; for any other end, the last durable observation.</summary>
    public long EndUs { get; init; }

    /// <summary>A normal stop is a terminator: the last value of a column gets at most its P, the rest is missing. Any other end closes at the last observation.</summary>
    public bool NormalEnd { get; init; }

    public long Rows { get; init; }

    public long DroppedTicks { get; init; }

    public RunStatus Status { get; init; } = RunStatus.Completed;

    public RunStopReason? StopReason { get; init; }

    /// <summary>An old <c>partslife-run/1</c> run: the row time stands for the acquire time and the period is the old nominal one.</summary>
    public bool Legacy { get; init; }

    public MetricTrack? Find(string key)
    {
        foreach (var track in Tracks)
        {
            if (track.Key == key)
            {
                return track;
            }
        }

        return null;
    }
}
