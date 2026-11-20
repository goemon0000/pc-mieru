using System.Management;
using System.Runtime.InteropServices;

namespace PcMieru.Checks.SystemInfo;

/// <summary>
/// The real WMI reader: the whole question (connect, execute, enumerate, map every row) runs on a worker thread of the WMI lane, and
/// the caller waits for it only until the deadline, on the monotonic clock. The executor is also given the time that is left, as the
/// timeout of the connection and of the enumeration, so a call that honours it comes back by itself; a call that does not is
/// left on its thread, holds its slot until it returns, and its late rows are dropped.
/// </summary>
internal sealed class ManagementWmiReader : IWmiReader
{
    private readonly IWmiExecutor _executor;
    private readonly QueryLane _lane;

    public ManagementWmiReader()
        : this(new ManagementWmiExecutor(), QueryLanes.Wmi)
    {
    }

    internal ManagementWmiReader(IWmiExecutor executor, QueryLane lane)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(lane);
        _executor = executor;
        _lane = lane;
    }

    public Task<CheckReading<IReadOnlyList<T>>> ReadAsync<T>(WmiQuery<T> query, QueryDeadline deadline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(deadline);
        return _lane.RunAsync(job => ReadOnThisThread(query, deadline, job, ct), deadline, ct);
    }

    /// <summary>The whole question, on the calling thread. The lane runs it on its worker thread; a test can call it directly.</summary>
    internal CheckReading<IReadOnlyList<T>> ReadOnThisThread<T>(WmiQuery<T> query, QueryDeadline deadline, LaneJob job, CancellationToken ct)
    {
        if (Stopping(job, ct))
        {
            return CheckReading<IReadOnlyList<T>>.Stopped();
        }

        if (deadline.IsExpired)
        {
            return CheckReading<IReadOnlyList<T>>.TimedOut();
        }

        var rows = new List<T>();
        var mapperFailed = false;
        try
        {
            _executor.Run(query.Scope, query.Text, deadline.RemainingAtLeastOneMillisecond(), row =>
            {
                // Between rows: the caller gave up, or no time is left. A row that is already mapped is kept only if the whole list is.
                if (Stopping(job, ct) || deadline.IsExpired)
                {
                    return false;
                }

                try
                {
                    rows.Add(query.Map(row));
                }
                catch (Exception)
                {
                    mapperFailed = true; // the row did not map: no list, whatever came before
                    return false;
                }

                return true;
            });
        }
        catch (Exception ex)
        {
            return CheckReading<IReadOnlyList<T>>.Missing(WmiFailure.Classify(ex));
        }

        if (mapperFailed)
        {
            return CheckReading<IReadOnlyList<T>>.Failed();
        }

        // The enumeration ended (or was cut short by the checks above). A list is a list only if it ended in time and nobody stopped it.
        if (Stopping(job, ct))
        {
            return CheckReading<IReadOnlyList<T>>.Stopped();
        }

        if (deadline.IsExpired)
        {
            return CheckReading<IReadOnlyList<T>>.TimedOut();
        }

        return CheckReading<IReadOnlyList<T>>.Ok(rows);
    }

    private static bool Stopping(LaneJob job, CancellationToken ct) => job.IsAbandoned || ct.IsCancellationRequested;
}

/// <summary>What a failed WMI call means for the reading: only the reason is kept, never the message (it can name a machine or a path).</summary>
internal static class WmiFailure
{
    private const int EAccessDenied = unchecked((int)0x80070005);

    public static CheckReason Classify(Exception ex) => ex switch
    {
        UnauthorizedAccessException => CheckReason.Denied,
        TimeoutException => CheckReason.Timeout,
        ManagementException m => Classify(m.ErrorCode),
        COMException c when c.HResult == EAccessDenied => CheckReason.Denied,
        _ => CheckReason.Error,
    };

    public static CheckReason Classify(ManagementStatus status) => status switch
    {
        ManagementStatus.AccessDenied => CheckReason.Denied,
        ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass => CheckReason.Unsupported,
        ManagementStatus.Timedout => CheckReason.Timeout,
        _ => CheckReason.Error,
    };
}
