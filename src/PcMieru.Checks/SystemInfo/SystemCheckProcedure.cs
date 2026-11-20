namespace PcMieru.Checks.SystemInfo;

/// <summary>
/// The system check (DESIGN-A §3.1, §14.1) as a procedure of the run framework. Three groups of questions are asked at the same time, each
/// with its own deadline, and none of them can fail the run: what did not come is a reason on the rows that rest on it.
/// <list type="number">
/// <item>The hardware of Core (<see cref="HardwareFacts"/>): the OS, the CPU, the memory, the GPUs and the drives, all under one 10 s deadline.</item>
/// <item>WMI (<see cref="SystemWmiFacts"/>), one question after the other: the page files (one 5 s deadline for the two questions), the video
/// controllers and the disks' health (5 s each).</item>
/// <item>The event log: the eleven fixed counts, one after the other under ONE shared 5 s deadline, over ONE window of seven days that is fixed
/// when the run starts. A count that finds the deadline gone is a timeout without being asked.</item>
/// </list>
/// Nothing here needs elevation, and no value that names the user's machine (serial, path, drive letter, label) is written.
/// </summary>
internal sealed class SystemCheckProcedure : ICheckProcedure
{
    /// <summary>The one deadline of the whole event group (DESIGN-A 6.1).</summary>
    public static readonly TimeSpan EventBudget = TimeSpan.FromSeconds(5);

    public CheckKind Kind => CheckKind.System;

    public async Task ExecuteAsync(CheckContext context, CancellationToken ct)
    {
        var services = context.Services;

        context.Stage(CheckStage.Collecting);

        // One moment for the whole run: the window of the events starts at it, and the driver's age is counted to it.
        var now = context.Time.GetUtcNow().UtcDateTime;
        var windowStart = EventCountQuery.WindowStartFor(now);

        // A system check reads the machine as it is now, not what an earlier look left in the provider's cache.
        services.Hardware.Invalidate();

        var hardware = HardwareFacts.ReadAsync(services.Hardware, context.Time, ct);
        var wmi = SystemWmiFacts.ReadAsync(services.Wmi, context.Time, ct);
        var events = ReadEventsAsync(services.Events, windowStart, context.Time, ct);
        await Task.WhenAll(hardware, wmi, events).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        context.Stage(CheckStage.Judging);

        var inputs = SystemInputsBuilder.Build(hardware.Result, wmi.Result, events.Result);
        context.Collector.AddRange(SystemJudge.Judge(inputs, now));
    }

    /// <summary>
    /// The eleven counts, in the order of <see cref="EventCountQuery.AllKinds"/> (one at a time: the lane has two slots, and a stuck query must
    /// not hold both). A count that fails is its reason; a deadline that has passed before the next one starts is a timeout for it and for all
    /// the ones after it, and they are not asked. A stop by the caller is the only exception.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, CheckReading<long>>> ReadEventsAsync(
        IEventCounter counter, DateTime windowStartUtc, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(time);
        var deadline = QueryDeadline.After(time, EventBudget);
        var counts = new Dictionary<string, CheckReading<long>>(EventCountQuery.AllKinds.Count, StringComparer.Ordinal);
        foreach (var kind in EventCountQuery.AllKinds)
        {
            ct.ThrowIfCancellationRequested();
            var query = EventCountQuery.Create(kind, windowStartUtc);
            counts[query.ItemId] = deadline.IsExpired ? CheckReading<long>.TimedOut() : await CountAsync(counter, query, deadline, ct).ConfigureAwait(false);
        }

        return counts;
    }

    private static async Task<CheckReading<long>> CountAsync(IEventCounter counter, EventCountQuery query, QueryDeadline deadline, CancellationToken ct)
    {
        try
        {
            return await counter.CountAsync(query, deadline, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CheckReading<long>.Failed();
        }
    }
}
