namespace PcMieru.Checks.Load;

/// <summary>
/// The memory check (DESIGN-A §3.2, §6.2, §14.3) as a procedure of the run framework: a part of the machine's free memory is borrowed, a pattern
/// is written over all of it and read back, pass after pass, for 30 seconds by default. It tests the memory Windows lent to this process, not all
/// of the memory; the note <c>memory.borrowed_region_only</c> says so on every memory result.
/// <list type="bullet">
/// <item>The load runs on its own long-running thread, started before the first await: the caller's thread (the app's UI, the CLI) never
/// waits for the allocation or for the first slice.</item>
/// <item>A stop by the caller ends the work at the next slice boundary and after every work thread has been joined and every block freed; the
/// rows found by then are kept, marked partial, and a mismatch that was already found stays a mismatch. The procedure returns then (it does not
/// throw), so the runner saves the partial result as a stopped one.</item>
/// <item>A fall of the free memory under half the floor stops the run the same way and adds the note <c>memory.pressure</c>; so does a run that
/// could not borrow one block.</item>
/// <item>Progress goes through <see cref="CheckContext.Load"/>: about once a second (never two within 0.9 s), with the MiB/s of the last second (during the allocation of the memory: with no rate yet).</item>
/// </list>
/// </summary>
internal sealed class MemoryCheckProcedure : ICheckProcedure
{
    /// <summary>The duration of a request that does not name one.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(30);

    public CheckKind Kind => CheckKind.Memory;

    public async Task ExecuteAsync(CheckContext context, CancellationToken ct)
    {
        var services = context.Services;
        var duration = context.Request.Duration ?? DefaultDuration;

        // On every memory result, a stopped or failed one too: written before anything that can go wrong, and first.
        context.Collector.Note(CheckNotes.MemoryBorrowedRegionOnly);
        context.Stage(CheckStage.Collecting);

        // The session and the load are made on a thread of their own, never on the caller's: nothing that can block runs here before the first
        // await. LongRunning: the run holds a thread for up to five minutes, and it must not take one from the pool.
        var load = await Task.Factory.StartNew(
            () => RunLoad(services, context, duration, ct),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).ConfigureAwait(false);

        // A cancel that came while the run was ending (after the last reading, during the join and the release) is a stop too: the rows are then
        // partial and the run is stopped, never stopped with whole rows. A cancel after this line is after the rows are built: the runner's seal point.
        if (ct.IsCancellationRequested)
        {
            load.Stopped = true;
        }

        context.Stage(CheckStage.Judging);
        if (load.Pressure)
        {
            context.Collector.Note(CheckNotes.MemoryPressure);
        }

        context.Collector.AddRange(MemoryJudge.Judge(load));
        if (load.Stopped)
        {
            context.Collector.MarkStopped();
        }
    }

    private static MemoryLoadResult RunLoad(CheckServices services, CheckContext context, TimeSpan duration, CancellationToken ct)
    {
        using var session = services.MemorySessionFactory();
        context.Stage(CheckStage.Loading, duration.TotalSeconds);
        return MemoryTest.Run(
            session,
            duration,
            (elapsedSeconds, mibPerSecond) => context.Load(elapsedSeconds, duration.TotalSeconds, mibPerSecond, mibPerSecond is null ? null : "MiB/s"), // no rate (the allocation): no unit either
            ct);
    }
}
