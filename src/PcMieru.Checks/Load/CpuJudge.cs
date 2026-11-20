using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Collectors;

namespace PcMieru.Checks.Load;

/// <summary>
/// The CPU check's rows and verdicts (DESIGN-A §3.2, §14.2) from what the load measured. A pure function: no clock, no thread, no PDH.
/// <list type="bullet">
/// <item>A run that was stopped is partial: every row that rests on the whole run keeps what it had and says <see cref="CheckReason.Partial"/>.
/// A judged row that would have been ok is <see cref="ItemVerdict.Unavailable"/> then (a partial row is never ok, §14.0); a finding that was
/// made (a compute mismatch, a caution) stays.</item>
/// <item>A value that was not measured is a missing value with a reason, never 0.</item>
/// </list>
/// The temperature is not a row: the note <c>cpu.temperature_not_measured</c> says it, and the procedure always writes it.
/// </summary>
internal static class CpuJudge
{
    /// <summary>A logical CPU below this fraction of the median of its own efficiency class is a caution (DESIGN-A 14.2).</summary>
    public const double PeerCautionFraction = 0.75;

    public static IReadOnlyList<CheckItem> Judge(CpuLoadResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var items = new List<CheckItem>();
        var stopped = r.Stopped;
        var ticks = r.Ticks.Count;
        var hasTicks = ticks > 0;

        items.Add(Info("cpu.duration", r.Seconds, CheckSource.Load, stopped));
        items.Add(Info("cpu.chunks", r.Chunks, CheckSource.Load, stopped));
        items.Add(ComputeErrors(r, stopped));

        var throughput = hasTicks ? r.PerCpuAverage.Sum() : (double?)null;
        items.Add(Info("cpu.throughput", throughput, CheckSource.Load, stopped));

        AddSteadiness(items, r, stopped);
        AddLogicalThroughput(items, r, stopped, hasTicks);
        AddPeers(items, r, hasTicks, stopped);
        items.Add(EffectiveClock(r, stopped));
        items.Add(Utilization(r, stopped));
        items.Add(PackagePower(r, stopped));
        return items;
    }

    private static CheckItem ComputeErrors(CpuLoadResult r, bool stopped)
    {
        const string id = "cpu.compute_errors";
        if (r.ComputeErrors >= 1)
        {
            // A mismatch that was found stays a finding, whatever happened to the rest of the run.
            return CheckItem.Create(id, CheckValue.Int(r.ComputeErrors), ItemVerdict.Check, CheckSource.Load, stopped ? CheckReason.Partial : null,
                evidence: CheckEvidence.OfCode(CheckEvidenceCode.ComputeMismatch));
        }

        if (r.Chunks <= 0)
        {
            // Nothing was compared, so "no errors" says nothing.
            return CheckItem.Create(id, null, ItemVerdict.Unavailable, CheckSource.Load, stopped ? CheckReason.Partial : CheckReason.Error);
        }

        return stopped
            ? CheckItem.Create(id, CheckValue.Int(0), ItemVerdict.Unavailable, CheckSource.Load, CheckReason.Partial)
            : CheckItem.Create(id, CheckValue.Int(0), ItemVerdict.Ok, CheckSource.Load);
    }

    private static void AddSteadiness(List<CheckItem> items, CpuLoadResult r, bool stopped)
    {
        if (r.Steadiness is not { } s)
        {
            // Under 8 seconds of load (the first 2 are dropped, 6 are needed): too short to say anything about steadiness.
            items.Add(CheckItem.Create("cpu.steadiness", null, ItemVerdict.Unavailable, CheckSource.Load, CheckReason.Partial,
                evidence: CheckEvidence.OfCode(CheckEvidenceCode.TooShort)));
            items.Add(Info("cpu.variation", null, CheckSource.Load, stopped));
            return;
        }

        var cv = Math.Abs(s.Cv * 100);
        items.Add(Judged("cpu.steadiness", s.Score, Steadiness.Judge(s), CheckSource.Load, stopped, evidence: new CheckEvidence { Cv = cv }));
        items.Add(Info("cpu.variation", cv, CheckSource.Load, stopped));
    }

    private static void AddLogicalThroughput(List<CheckItem> items, CpuLoadResult r, bool stopped, bool hasTicks)
    {
        for (var i = 0; i < r.Cpus.Count; i++)
        {
            items.Add(Info("cpu.logical_throughput", hasTicks ? r.PerCpuAverage[i] : null, CheckSource.Load, stopped,
                subject: SubjectOf(i), evidence: new CheckEvidence { EfficiencyClass = r.Cpus[i].EfficiencyClass }));
        }
    }

    /// <summary>
    /// One row per logical CPU, each against the median of its own kind of core (an E core being slower than a P core is not a finding). With one
    /// logical CPU there is nothing to compare with and no row. A CPU whose pin did not take is not compared (its thread was not on it) and
    /// is not in anybody's median.
    /// </summary>
    private static void AddPeers(List<CheckItem> items, CpuLoadResult r, bool hasTicks, bool stopped)
    {
        var n = r.Cpus.Count;
        if (n <= 1)
        {
            return;
        }

        var medians = hasTicks ? MediansByClass(r) : [];
        for (var i = 0; i < n; i++)
        {
            var cpu = r.Cpus[i];
            var subject = SubjectOf(i);
            var pinned = i < r.Pins.Count && r.Pins[i] == PinOutcome.Pinned;
            if (!pinned)
            {
                items.Add(CheckItem.Create("cpu.peer_ratio", null, ItemVerdict.Unavailable, CheckSource.Load, CheckReason.Error, subject,
                    new CheckEvidence { Code = CheckEvidenceCode.PinUnknown, EfficiencyClass = cpu.EfficiencyClass }));
                continue;
            }

            var evidence = new CheckEvidence { EfficiencyClass = cpu.EfficiencyClass };
            if (!hasTicks || !medians.TryGetValue(cpu.EfficiencyClass, out var median) || !(median > 0) || !double.IsFinite(median))
            {
                // No second was measured, or the kind of core did nothing at all: there is no ratio to give.
                items.Add(CheckItem.Create("cpu.peer_ratio", null, ItemVerdict.Unavailable, CheckSource.Load, CheckReason.Partial, subject, evidence));
                continue;
            }

            var value = r.PerCpuAverage[i];
            var verdict = value < median * PeerCautionFraction ? ItemVerdict.Caution : ItemVerdict.Ok;
            items.Add(Judged("cpu.peer_ratio", value / median * 100, verdict, CheckSource.Load, stopped, subject, evidence));
        }
    }

    /// <summary>The upper median (the element at n/2 of the sorted values) of each efficiency class, over the CPUs whose pin took.</summary>
    internal static Dictionary<byte, double> MediansByClass(CpuLoadResult r)
    {
        var medians = new Dictionary<byte, double>();
        var pinned = Enumerable.Range(0, r.Cpus.Count).Where(i => i < r.Pins.Count && r.Pins[i] == PinOutcome.Pinned);
        foreach (var group in pinned.GroupBy(i => r.Cpus[i].EfficiencyClass))
        {
            var sorted = group.Select(i => r.PerCpuAverage[i]).OrderBy(static v => v);
            medians[group.Key] = sorted.ElementAt(group.Count() / 2);
        }

        return medians;
    }

    /// <summary>The average of the samples that came. The verdict only when every second has a clock and its steadiness could be computed.</summary>
    private static CheckItem EffectiveClock(CpuLoadResult r, bool stopped)
    {
        const string id = "cpu.effective_clock";
        if (r.Ticks.Count == 0)
        {
            return CheckItem.Create(id, null, ItemVerdict.Unavailable, CheckSource.Pdh, CheckReason.Partial);
        }

        var clocks = r.Ticks.Where(static t => t.Sample.EffectiveMhz is not null).Select(static t => t.Sample.EffectiveMhz!.Value).ToList();
        if (clocks.Count == 0)
        {
            return CheckItem.Create(id, null, ItemVerdict.Unavailable, CheckSource.Pdh, CheckReason.Unsupported);
        }

        var average = clocks.Average();
        if (r.ClockSteadiness is not { } steadiness)
        {
            // The average is there but not how steady the clock was (a second is missing, or too few seconds): kept, not judged.
            return CheckItem.Create(id, CheckValue.Number(average), ItemVerdict.Unavailable, CheckSource.Pdh, CheckReason.Partial);
        }

        return Judged(id, average, Steadiness.JudgeClock(steadiness), CheckSource.Pdh, stopped, evidence: new CheckEvidence { ClockSteadiness = Math.Abs(steadiness.Score) });
    }

    private static CheckItem Utilization(CpuLoadResult r, bool stopped)
    {
        const string id = "cpu.utilization";
        if (r.Ticks.Count == 0)
        {
            return Info(id, null, CheckSource.Pdh, stopped);
        }

        var loads = r.Ticks.Where(static t => t.Sample.UtilizationPercent is not null).Select(static t => t.Sample.UtilizationPercent!.Value).ToList();
        return loads.Count == 0
            ? CheckItem.Create(id, null, ItemVerdict.Info, CheckSource.Pdh, CheckReason.Unsupported)
            : Info(id, loads.Average(), CheckSource.Pdh, stopped || loads.Count < r.Ticks.Count);
    }

    /// <summary>
    /// The package power: Energy Meter's package instances only (Core confirmed them and the milliwatt unit), averaged over the seconds that gave
    /// one. No package instance at all is "not supported"; instances that never gave a usable sample are an error. An observation, never a verdict.
    /// </summary>
    private static CheckItem PackagePower(CpuLoadResult r, bool stopped)
    {
        const string id = "cpu.package_power";
        if (r.Ticks.Count == 0)
        {
            return Info(id, null, CheckSource.EnergyMeter, stopped);
        }

        var watts = r.Ticks.Where(static t => t.Sample.Power.State == PackagePowerState.Valid).Select(static t => t.Sample.Power.Watts).ToList();
        if (watts.Count > 0)
        {
            return Info(id, watts.Average(), CheckSource.EnergyMeter, stopped || watts.Count < r.Ticks.Count);
        }

        var seen = r.Ticks.Any(static t => t.Sample.Power.State == PackagePowerState.NoValidSample);
        return CheckItem.Create(id, null, ItemVerdict.Info, CheckSource.EnergyMeter, seen ? CheckReason.Error : CheckReason.Unsupported);
    }

    private static CheckSubject SubjectOf(int index) => new($"cpu-{index + 1}");

    // An observation. A value that is not there is "partial" (not enough was measured), unless the caller gives the reason itself.
    private static CheckItem Info(string id, double? value, CheckSource source, bool partial, CheckSubject? subject = null, CheckEvidence? evidence = null) =>
        value is { } v
            ? CheckItem.Create(id, CheckValue.Number(v), ItemVerdict.Info, source, partial ? CheckReason.Partial : null, subject, evidence)
            : CheckItem.Create(id, null, ItemVerdict.Info, source, CheckReason.Partial, subject, evidence);

    private static CheckItem Info(string id, long value, CheckSource source, bool partial) =>
        CheckItem.Create(id, CheckValue.Int(value), ItemVerdict.Info, source, partial ? CheckReason.Partial : null);

    // A judged row with a value. Partial: kept, never ok.
    private static CheckItem Judged(string id, double value, ItemVerdict verdict, CheckSource source, bool partial, CheckSubject? subject = null, CheckEvidence? evidence = null) =>
        partial
            ? CheckItem.Create(id, CheckValue.Number(value), verdict == ItemVerdict.Ok ? ItemVerdict.Unavailable : verdict, source, CheckReason.Partial, subject, evidence)
            : CheckItem.Create(id, CheckValue.Number(value), verdict, source, null, subject, evidence);
}
