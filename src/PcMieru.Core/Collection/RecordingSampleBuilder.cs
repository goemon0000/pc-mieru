namespace PcMieru.Core.Collection;

/// <summary>
/// Turns one <see cref="Snapshot"/> into the immutable <see cref="RecordingSample"/> the recorder gets. A pure function of the snapshot (and its
/// provenance), so the mapping is testable without a loop. A snapshot from a source that does not track provenance (a fake) gets the time the
/// sample was handed over as its acquisition time and no stage times: nothing is invented for a stage that was not timed.
/// </summary>
internal static class RecordingSampleBuilder
{
    public static RecordingSample Build(Snapshot snapshot, long sequence, int periodMs)
    {
        var provenance = snapshot.Provenance;
        var acquiredUs = provenance is { ProcessReadTicks: > 0 } p
            ? MonotonicClock.ToMicroseconds(p.ProcessReadTicks)
            : MonotonicClock.NowMicroseconds();
        var stages = provenance is null
            ? new CoreStageTimes(0, 0, 0, 0)
            : new CoreStageTimes(
                Us(provenance.CpuTicks),
                Us(provenance.MemoryTicks),
                Us(provenance.PdhTicks),
                Us(provenance.GpuCounterTicks));

        var system = snapshot.System;
        var gpus = new RecordedGpu[system.Gpus.Count];
        for (var i = 0; i < gpus.Length; i++)
        {
            var g = system.Gpus[i];
            gpus[i] = new RecordedGpu(
                g.Id,
                g.Name,
                g.UtilizationPercent,
                g.DedicatedUsedBytes,
                g.TemperatureC,
                g.PowerWatts,
                g.FanPercent,
                g.ClockMhz,
                g.TemperatureLimitC,
                g.LimitReasons,
                g.VendorOrigin ?? SourceOrigin.Missing(SourceGap.NotStarted));
        }

        return new RecordingSample(
            new SourceOrigin(new FrameId(0, sequence), acquiredUs, periodMs),
            stages,
            snapshot.TakenAtUtc,
            snapshot.IntervalMs,
            system.Cpu.TotalPercent,
            system.Cpu.CurrentMhz,
            system.Cpu.PackagePowerWatts,
            system.Memory,
            system.Power,
            Array.AsReadOnly(gpus));
    }

    private static long Us(long ticks) => ticks > 0 ? MonotonicClock.ToMicroseconds(ticks) : 0;
}
