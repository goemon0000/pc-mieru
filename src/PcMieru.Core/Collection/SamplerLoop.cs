using System.Diagnostics;

namespace PcMieru.Core.Collection;

/// <summary>A fixed-size buffer that forgets its oldest entry when full. Thread-safe; readers get a copy, oldest first.</summary>
internal sealed class HistoryRing<T>
{
    private readonly T[] _items;
    private readonly object _lock = new();
    private int _start;
    private int _count;

    public HistoryRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            if (_count < _items.Length)
            {
                _items[(_start + _count) % _items.Length] = item;
                _count++;
            }
            else
            {
                _items[_start] = item;
                _start = (_start + 1) % _items.Length;
            }
        }
    }

    public T[] ToArray()
    {
        lock (_lock)
        {
            var copy = new T[_count];
            for (var i = 0; i < _count; i++)
            {
                copy[i] = _items[(_start + i) % _items.Length];
            }

            return copy;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_items);
            _start = 0;
            _count = 0;
        }
    }
}

/// <summary>
/// Drives an <see cref="ISnapshotSource"/> on a steady beat without a UI: samples at <see cref="ISamplerControl.EffectiveInterval"/>
/// (slower while minimized), stops while paused (and drops the baseline on resume so the pause never becomes a rate), keeps the
/// last <see cref="HistoryLength"/> snapshots (SPEC 6: interval x 60) and tells listeners. The GUI marshals to its own thread;
/// events here run on a thread-pool thread. One sample is in flight at a time and a slow one delays the next, never queues behind it.
/// </summary>
public sealed class SamplerLoop : IDisposable
{
    public const int HistoryLength = 60;

    /// <summary>The loop re-reads pause, minimized and interval at least this often, so a change takes effect within a quarter second.</summary>
    private static readonly TimeSpan WaitSlice = TimeSpan.FromMilliseconds(250);

    private readonly ISnapshotSource _source;
    private readonly ISamplerControl _control;
    private readonly HistoryRing<Snapshot> _history = new(HistoryLength);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _task;
    private Snapshot? _latest;
    private int _started;
    private bool _disposed;

    public SamplerLoop(ISnapshotSource source, ISamplerControl control)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _control = control ?? throw new ArgumentNullException(nameof(control));
    }

    /// <summary>A sample was taken (runs on a thread-pool thread; an exception in a handler is swallowed so one listener cannot stop the loop).</summary>
    public event Action<Snapshot>? Sampled;

    /// <summary>A sample failed. The loop carries on at the next beat.</summary>
    public event Action<Exception>? Failed;

    /// <summary>The settings this loop follows (pause, interval, minimized). Whoever drives the loop writes them here.</summary>
    public ISamplerControl Control => _control;

    public Snapshot? Latest => Volatile.Read(ref _latest);

    /// <summary>Oldest first, at most <see cref="HistoryLength"/> entries, a copy. Includes the no-baseline snapshots after a start or a pause.</summary>
    public IReadOnlyList<Snapshot> History => _history.ToArray();

    public bool Running => _task is { IsCompleted: false };

    /// <summary>Starts sampling (the first sample is taken at once). Calling it again does nothing.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _source.ResetBaseline();
        var token = _stop.Token;
        _task = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    /// <summary>Takes a sample as soon as possible instead of waiting for the beat (refresh button). Ignored while paused.</summary>
    public void RequestSampleNow()
    {
        try
        {
            _ = _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A request is already waiting.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_task is { } task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        try
        {
            _ = _task?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
        _wake.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var wasPaused = false;
            while (!ct.IsCancellationRequested)
            {
                if (_control.Paused)
                {
                    wasPaused = true;
                    _ = await WaitAsync(WaitSlice, ct).ConfigureAwait(false); // a refresh request while paused is dropped here
                    continue;
                }

                if (wasPaused)
                {
                    wasPaused = false;
                    _source.ResetBaseline();
                }

                var start = Stopwatch.GetTimestamp();
                await TakeOneAsync(ct).ConfigureAwait(false);
                await WaitForNextBeatAsync(start, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Waits until one interval after <paramref name="start"/> (a beat, not "an interval after the sample ended"), re-reading the settings as it goes.</summary>
    private async Task WaitForNextBeatAsync(long start, CancellationToken ct)
    {
        // A sample that overran its beat must not be followed by another at once: leave a quarter interval of rest.
        var end = Stopwatch.GetTimestamp();
        var rest = Stopwatch.GetElapsedTime(start, end) >= _control.EffectiveInterval ? _control.EffectiveInterval / 4 : TimeSpan.Zero;
        var earliest = end + (long)(rest.TotalSeconds * Stopwatch.Frequency);
        while (!ct.IsCancellationRequested && !_control.Paused)
        {
            var now = Stopwatch.GetTimestamp();
            var due = Math.Max(start + (long)(_control.EffectiveInterval.TotalSeconds * Stopwatch.Frequency), earliest);
            if (due <= now)
            {
                return;
            }

            var remaining = TimeSpan.FromSeconds((double)(due - now) / Stopwatch.Frequency);
            if (await WaitAsync(remaining < WaitSlice ? remaining : WaitSlice, ct).ConfigureAwait(false))
            {
                return; // RequestSampleNow
            }
        }
    }

    private async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await _wake.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task TakeOneAsync(CancellationToken ct)
    {
        Snapshot snapshot;
        try
        {
            snapshot = await _source.SampleAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Raise(Failed, ex);
            return;
        }

        Volatile.Write(ref _latest, snapshot);
        _history.Add(snapshot);
        Raise(Sampled, snapshot);
    }

    private static void Raise<T>(Action<T>? handlers, T argument)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)handler)(argument);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One listener's bug must not stop everyone else from getting snapshots.
            }
        }
    }
}
