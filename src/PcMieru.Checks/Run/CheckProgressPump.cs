using System.Threading.Channels;

namespace PcMieru.Checks;

/// <summary>
/// Delivers a run's notifications to the caller's <see cref="IProgress{T}"/> in order, on its own task, so that a slow or blocked handler
/// can never hold up the run, its stop, its result or its save (DESIGN-A §2: a notification does not make anything wait). A handler
/// that throws is ignored. Posting never blocks and never throws. With no handler nothing is queued.
/// </summary>
internal sealed class CheckProgressPump
{
    private readonly IProgress<CheckProgress>? _handler;
    private readonly Channel<CheckProgress>? _queue;
    private readonly Task _consumer = Task.CompletedTask;

    public CheckProgressPump(IProgress<CheckProgress>? handler)
    {
        _handler = handler;
        if (handler is null)
        {
            return;
        }

        _queue = Channel.CreateUnbounded<CheckProgress>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _consumer = Task.Run(Consume);
    }

    public void Post(CheckProgress progress) => _queue?.Writer.TryWrite(progress);

    /// <summary>
    /// Ends the queue and gives the handler up to <paramref name="patience"/> to take what is left. It is called after the result is final
    /// and saved, so a handler that does not come back costs the caller this much time at most and nothing else.
    /// </summary>
    public async Task CompleteAsync(TimeSpan patience)
    {
        if (_queue is null)
        {
            return;
        }

        _queue.Writer.TryComplete();
        try
        {
            await _consumer.WaitAsync(patience).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The handler is still busy. The run is over; what it has not taken is dropped with the queue's task.
        }
    }

    private async Task Consume()
    {
        try
        {
            await foreach (var progress in _queue!.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    _handler!.Report(progress);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A handler's own fault is not the run's.
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The queue itself cannot fail; this only keeps an unobserved task exception out of the process.
        }
    }
}
