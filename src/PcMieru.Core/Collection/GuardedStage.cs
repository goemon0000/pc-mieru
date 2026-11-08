namespace PcMieru.Core.Collection;

/// <summary>
/// Runs one piece of a tick (PDH, GetIfTable2, ...) on the thread pool and waits for it with a limit. A call into a
/// perf-counter provider can hang; the tick must still come back, with that part reported as a timeout, and the next tick
/// must not start a second call into the same non-thread-safe sampler while the first is still stuck.
/// </summary>
internal sealed class GuardedStage<T>
    where T : class
{
    /// <summary>For a stage that was skipped because the previous call is still running.</summary>
    public const string StillRunning = "the previous call has not returned yet";

    private Task<T>? _task;
    private bool _skipped;

    /// <summary>True while a call that was started earlier has not returned (it timed out on a previous tick).</summary>
    public bool IsBusy => _task is { IsCompleted: false };

    /// <summary>Starts the work unless the previous call is still running. Returns whether it started.</summary>
    public bool Start(Func<T> work)
    {
        _skipped = IsBusy;
        if (_skipped)
        {
            return false;
        }

        var task = Task.Run(work);

        // A call that failed after its tick gave up on it must not surface later as an unobserved exception.
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _task = task;
        return true;
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the call started by the last <see cref="Start"/>. When that start was skipped
    /// it fails at once (no waiting on a stuck call). <paramref name="failure"/> is null on success.
    /// </summary>
    public bool TryJoin(TimeSpan timeout, out T? result, out string? failure, out ValueStatus failureStatus)
    {
        result = null;
        failure = null;
        failureStatus = ValueStatus.Error;
        if (_skipped)
        {
            failure = StillRunning;
            failureStatus = ValueStatus.Timeout;
            return false;
        }

        var task = _task;
        if (task is null)
        {
            failure = "no call was started";
            return false;
        }

        try
        {
            if (!task.Wait(timeout))
            {
                failure = $"did not answer within {timeout.TotalSeconds:0.#} s";
                failureStatus = ValueStatus.Timeout;
                return false;
            }

            result = task.Result;
            return true;
        }
        catch (AggregateException ex)
        {
            var inner = ex.GetBaseException();
            failure = $"{inner.GetType().Name}: {inner.Message}";
            return false;
        }
    }
}
