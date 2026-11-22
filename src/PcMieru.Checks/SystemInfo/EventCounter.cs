namespace PcMieru.Checks.SystemInfo;

/// <summary>
/// Counts the events of one fixed System-log query within a deadline (DESIGN-A §6.1). The answer is a complete count or a reason;
/// never a partial count.
/// </summary>
internal interface IEventCounter
{
    Task<CheckReading<long>> CountAsync(EventCountQuery query, QueryDeadline deadline, CancellationToken ct);
}

/// <summary>The three wevtapi calls the count needs. The seam of the unit tests; <see cref="NativeEventLogApi"/> is the thin real one.</summary>
internal interface IEventLogApi
{
    /// <summary>EvtQuery on <paramref name="channel"/> by channel path. 0 and a non-zero handle, or a Win32 error and no handle.</summary>
    int Query(string channel, string xpath, int flags, out nint resultSet);

    /// <summary>
    /// EvtNext: fills <paramref name="events"/> with up to its length of event handles, waiting at most <paramref name="timeoutMs"/>.
    /// 0 with <paramref name="returned"/> handles; ERROR_NO_MORE_ITEMS (259) at the end; ERROR_TIMEOUT (1460) when nothing came in time;
    /// any other Win32 error is a failure.
    /// </summary>
    int Next(nint resultSet, int timeoutMs, Span<nint> events, out int returned);

    /// <summary>EvtClose of a query handle or an event handle.</summary>
    void Close(nint handle);
}

/// <summary>The fixed values of the event log API (winevt.h).</summary>
internal static class EventLogFlags
{
    /// <summary>EvtQueryChannelPath: the path of the query is a channel name (the single fixed channel "System").</summary>
    public const int QueryChannelPath = 0x1;

    /// <summary>
    /// EvtQueryTolerateQueryErrors. Documented here and NEVER passed (DESIGN-A §6.1): a single fixed expression is evaluated strictly,
    /// so a query that cannot be evaluated is an error and not a count of what was left. (The old code passed 0x100 for it; that value is EvtQueryForwardDirection.)
    /// </summary>
    public const int QueryTolerateQueryErrors = 0x1000;

    /// <summary>What the counter passes to EvtQuery.</summary>
    public const int Query = QueryChannelPath;

    public const int ErrorAccessDenied = 5;
    public const int ErrorNoMoreItems = 259;
    public const int ErrorTimeout = 1460;
    public const int ErrorEvtChannelNotFound = 15007;
}

/// <summary>
/// The real event counter: wevtapi on a worker thread of the event-log lane. On that one thread it queries the System log by channel
/// path, enumerates the events in batches with an EvtNext timeout of at most the time left, closes every handle it was given, and
/// returns a count only when EvtNext said ERROR_NO_MORE_ITEMS inside the deadline. Zero events end the same way. Nothing else is a count:
/// a deadline that passed, an error in the middle, a stop, or a count past the cap all discard what was counted so far.
/// </summary>
internal sealed class WevtEventCounter : IEventCounter
{
    /// <summary>Handles taken per EvtNext call.</summary>
    public const int BatchSize = 64;

    /// <summary>The count where it is cut off as truncated (the old code's cap). A week of an event log does not get near it.</summary>
    public const long DefaultMaxCount = 1_000_000;

    private readonly IEventLogApi _api;
    private readonly QueryLane _lane;
    private readonly long _maxCount;
    private readonly int _batchSize;

    public WevtEventCounter()
        : this(new NativeEventLogApi(), QueryLanes.EventLog)
    {
    }

    internal WevtEventCounter(IEventLogApi api, QueryLane lane, long maxCount = DefaultMaxCount, int batchSize = BatchSize)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1L);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        _api = api;
        _lane = lane;
        _maxCount = maxCount;
        _batchSize = batchSize;
    }

    public Task<CheckReading<long>> CountAsync(EventCountQuery query, QueryDeadline deadline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(deadline);
        return _lane.RunAsync(job => CountOnThisThread(query, deadline, job, ct), deadline, ct);
    }

    /// <summary>The whole count, on the calling thread: every call into the API, and every close, happens here. The lane runs it on its worker thread; a test can call it directly.</summary>
    internal CheckReading<long> CountOnThisThread(EventCountQuery query, QueryDeadline deadline, LaneJob job, CancellationToken ct)
    {
        if (Stopping(job, ct))
        {
            return CheckReading<long>.Stopped();
        }

        if (deadline.IsExpired)
        {
            return CheckReading<long>.TimedOut();
        }

        var status = _api.Query(query.Channel, query.XPath, EventLogFlags.Query, out var resultSet);
        if (status != 0)
        {
            return CheckReading<long>.Missing(ReasonOfQueryError(status));
        }

        if (resultSet == 0)
        {
            return CheckReading<long>.Failed();
        }

        try
        {
            return Enumerate(resultSet, deadline, job, ct);
        }
        finally
        {
            _api.Close(resultSet);
        }
    }

    private CheckReading<long> Enumerate(nint resultSet, QueryDeadline deadline, LaneJob job, CancellationToken ct)
    {
        var batch = new nint[_batchSize];
        long count = 0;
        while (true)
        {
            // Before every batch: stopped, or no time left (the time of the previous batch counts).
            if (Stopping(job, ct))
            {
                return CheckReading<long>.Stopped();
            }

            if (deadline.IsExpired)
            {
                return CheckReading<long>.TimedOut();
            }

            var status = _api.Next(resultSet, deadline.RemainingMilliseconds(), batch, out var returned);
            returned = Math.Clamp(returned, 0, batch.Length);
            for (var i = 0; i < returned; i++)
            {
                _api.Close(batch[i]); // the event is only counted, never read
            }

            if (status == 0)
            {
                count += returned;
                if (count > _maxCount)
                {
                    return CheckReading<long>.Stopped(); // cut off: not a count
                }

                continue;
            }

            if (status == EventLogFlags.ErrorNoMoreItems)
            {
                count += returned;

                // The end was reached, but it counts only if it was reached in time and the caller still wants it.
                if (Stopping(job, ct))
                {
                    return CheckReading<long>.Stopped();
                }

                if (deadline.IsExpired)
                {
                    return CheckReading<long>.TimedOut();
                }

                return count > _maxCount ? CheckReading<long>.Stopped() : CheckReading<long>.Ok(count);
            }

            return status == EventLogFlags.ErrorTimeout ? CheckReading<long>.TimedOut() : CheckReading<long>.Failed();
        }
    }

    private static bool Stopping(LaneJob job, CancellationToken ct) => job.IsAbandoned || ct.IsCancellationRequested;

    private static CheckReason ReasonOfQueryError(int status) => status switch
    {
        EventLogFlags.ErrorAccessDenied => CheckReason.Denied,
        EventLogFlags.ErrorEvtChannelNotFound => CheckReason.Unsupported,
        EventLogFlags.ErrorTimeout => CheckReason.Timeout,
        _ => CheckReason.Error,
    };
}
