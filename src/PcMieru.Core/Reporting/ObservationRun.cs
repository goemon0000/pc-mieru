using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Reporting;

/// <summary>Runs an observation window and evaluates it. Shared by Insights and the report builder.</summary>
internal static class ObservationRun
{
    /// <summary>How long a requested extra sample may take before the window falls back to sampling by itself.</summary>
    internal static readonly TimeSpan DefaultLoopTimeout = TimeSpan.FromSeconds(5);

    /// <param name="loop">
    /// The running sampler of the front end, if any. When it is running and not paused the window is read from what it
    /// samples; asking the same source again would shorten both sides' deltas (the baseline is shared). When it is not
    /// running (pcmi, MCP), paused, or does not deliver, the window samples the source itself, as it always did.
    /// </param>
    /// <param name="loopTimeout">Tests shorten the wait for a stalled loop; the default is <see cref="DefaultLoopTimeout"/>.</param>
    public static async Task<(IReadOnlyList<Snapshot> History, SystemSummary Summary)> RunAsync(
        ISnapshotSource source,
        IObservationEngine engine,
        TimeSpan duration,
        IReadOnlyList<DriveHealth>? storage,
        CancellationToken ct,
        SamplerLoop? loop = null,
        TimeSpan? loopTimeout = null)
    {
        if (loop is not null && await FromLoopAsync(loop, duration, loopTimeout ?? DefaultLoopTimeout, ct).ConfigureAwait(false) is { } window)
        {
            return (window, Summarize(window[^1], window, engine, storage));
        }

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

    /// <summary>
    /// The snapshots the loop took from the moment this was called until at least <paramref name="duration"/> later, starting with
    /// the one that was already the latest (so the window is never shorter than asked, and may be one interval longer).
    /// Null when the loop cannot answer: not running, paused (a paused loop takes nothing new), no sample yet, or no new sample
    /// within the timeout of asking for one.
    /// </summary>
    private static async Task<IReadOnlyList<Snapshot>?> FromLoopAsync(SamplerLoop loop, TimeSpan duration, TimeSpan timeout, CancellationToken ct)
    {
        if (!loop.Running || loop.Control.Paused || loop.Latest is not { } first)
        {
            return null;
        }

        var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSampled(Snapshot snapshot)
        {
            if (!ReferenceEquals(snapshot, first))
            {
                fresh.TrySetResult();
            }
        }

        loop.Sampled += OnSampled;
        try
        {
            if (!ReferenceEquals(loop.Latest, first))
            {
                fresh.TrySetResult(); // one arrived between reading "first" and listening
            }

            if (duration > TimeSpan.Zero)
            {
                await Task.Delay(duration, ct).ConfigureAwait(false);
            }

            if (!fresh.Task.IsCompleted)
            {
                if (loop.Control.Paused)
                {
                    return null; // paused during the window: a request would be ignored
                }

                // The beat is longer than the window (a 5 s interval and a 1 s window): ask for a sample instead of waiting for it.
                loop.RequestSampleNow();
                try
                {
                    await fresh.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return null;
                }
            }
        }
        finally
        {
            loop.Sampled -= OnSampled;
        }

        // The ring may have dropped "first" on a long window at a fast beat; then everything in it is newer than "first".
        var all = loop.History;
        var at = -1;
        for (var i = 0; i < all.Count; i++)
        {
            if (ReferenceEquals(all[i], first))
            {
                at = i;
                break;
            }
        }

        var window = at >= 0 ? all.Skip(at).ToArray() : all.ToArray();
        return window.Length >= 2 ? window : null;
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
