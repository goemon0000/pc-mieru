namespace PcMieru.Checks.Load;

/// <summary>
/// The CPU check (DESIGN-A §3.2, §6.2, §14.2) as a procedure of the run framework: a load of 30, 120 or 300 seconds on every logical
/// processor, the answers compared, the pace of every processor and of the whole, the effective clock and the package power on the side.
/// <list type="bullet">
/// <item>The load runs on its own long-running thread, started before the first await: the caller's thread (the app's UI, the CLI) never
/// waits for the PDH query to open or for the first chunk.</item>
/// <item>A stop by the caller ends the load at the next chunk boundary and after every load thread has been joined; the rows found by then are
/// kept, marked partial, and a compute error that was already found stays a compute error. The procedure returns then (it does not throw), so
/// the runner saves the partial result as a stopped one.</item>
/// <item>The note <c>cpu.temperature_not_measured</c> is written first, on every CPU result, whatever the outcome: no driver is installed, so
/// no temperature is read.</item>
/// <item>Progress goes through <see cref="CheckContext.Load"/>: about once a second (never two within 0.9 s), with the chunks per second of the last second.</item>
/// </list>
/// </summary>
internal sealed class CpuCheckProcedure : ICheckProcedure
{
    /// <summary>The duration of a request that does not name one.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(30);

    public CheckKind Kind => CheckKind.Cpu;

    public async Task ExecuteAsync(CheckContext context, CancellationToken ct)
    {
        var services = context.Services;
        var duration = context.Request.Duration ?? DefaultDuration;

        // On every CPU result, a stopped or failed one too: written before anything that can go wrong.
        context.Collector.Note(CheckNotes.CpuTemperatureNotMeasured);
        context.Stage(CheckStage.Collecting);

        // The session (the PDH query opens in it) and the load are made on a thread of their own, never on the caller's: nothing that can block
        // runs here before the first await. LongRunning: the load holds a thread for up to five minutes, and it must not take one from the pool.
        var load = await Task.Factory.StartNew(
            () => RunLoad(services, context, duration, ct),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).ConfigureAwait(false);

        // A cancel that came while the load was ending (after the last reading, during the join) is a stop too: the rows are then partial and
        // the run is stopped, never stopped with whole rows. A cancel after this line is after the rows are built: the runner's seal point.
        if (ct.IsCancellationRequested)
        {
            load.Stopped = true;
        }

        context.Stage(CheckStage.Judging);
        context.Collector.AddRange(CpuJudge.Judge(load));
        if (load.Stopped)
        {
            context.Collector.MarkStopped();
        }
    }

    private static CpuLoadResult RunLoad(CheckServices services, CheckContext context, TimeSpan duration, CancellationToken ct)
    {
        using var session = services.CpuSessionFactory();
        context.Stage(CheckStage.Loading, duration.TotalSeconds);
        return CpuLoadTest.Run(
            session,
            duration,
            (elapsedSeconds, chunksPerSecond) => context.Load(elapsedSeconds, duration.TotalSeconds, chunksPerSecond, "chunks/s"),
            ct);
    }
}
