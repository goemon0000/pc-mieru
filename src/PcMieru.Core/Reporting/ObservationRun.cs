using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Reporting;

/// <summary>Runs an observation window and evaluates it. Shared by Insights and the report builder.</summary>
internal static class ObservationRun
{
    public static async Task<(IReadOnlyList<Snapshot> History, SystemSummary Summary)> RunAsync(
        ISnapshotSource source,
        IObservationEngine engine,
        TimeSpan duration,
        IReadOnlyList<DriveHealth>? storage,
        CancellationToken ct)
    {
        var history = new List<Snapshot> { await source.SampleAsync(ct).ConfigureAwait(false) };
        var until = DateTime.UtcNow + duration;
        do
        {
            var step = until - DateTime.UtcNow;
            if (step > TimeSpan.FromSeconds(1))
            {
                step = TimeSpan.FromSeconds(1);
            }

            if (step > TimeSpan.Zero)
            {
                await Task.Delay(step, ct).ConfigureAwait(false);
            }

            history.Add(await source.SampleAsync(ct).ConfigureAwait(false));
        }
        while (DateTime.UtcNow < until);

        return (history, Summarize(history[^1], history, engine, storage));
    }

    public static SystemSummary Summarize(Snapshot current, IReadOnlyList<Snapshot> history, IObservationEngine engine, IReadOnlyList<DriveHealth>? storage) =>
        new(
            SystemSummary.CurrentSchemaVersion,
            current.TakenAtUtc,
            current.IntervalMs,
            current.System,
            engine.Evaluate(new ObservationInput(current, history, storage)),
            TopByCpu(current, 5));

    public static IReadOnlyList<ProcessInfo> TopByCpu(Snapshot snapshot, int count) =>
        snapshot.Processes
            .Where(p => p.Key.Pid != 0)
            .OrderByDescending(p => p.CpuPercent.GetOr(-1))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToArray();
}
