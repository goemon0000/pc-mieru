namespace PcMieru.Checks;

/// <summary>Where a run is. Stage changes and the final result are delivered separately from the (rate-limited) load notifications.</summary>
public enum CheckStage
{
    /// <summary>The run exists; nothing was read yet.</summary>
    Starting,

    /// <summary>Reading the machine (drives, WMI, ...).</summary>
    Collecting,

    /// <summary>A load test is running (CPU and memory): the elapsed and target seconds and the recent rate move.</summary>
    Loading,

    /// <summary>Comparing with the baseline and judging.</summary>
    Judging,

    /// <summary>The result is final; it is being written to the history.</summary>
    Saving,

    /// <summary>The run is over (saved or not). The last notification.</summary>
    Done,
}

/// <summary>
/// One notification of a run (DESIGN-A §2): the run ID, the stage, the elapsed seconds, the target seconds (0 when the stage has none),
/// and the recent rate with its unit (CPU "chunks/s", memory "MiB/s"; both null when there is none). Immutable.
/// </summary>
public sealed record CheckProgress(Guid RunId, CheckStage Stage, double ElapsedSeconds, double TargetSeconds, double? RecentRate = null, string? RateUnit = null);
