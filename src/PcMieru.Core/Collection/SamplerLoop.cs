using System.Diagnostics;
using System.Threading.Channels;

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
/// While a recording lease is held (<see cref="IRecordingSamples"/>) the loop runs at <see cref="RecordingPeriod"/> whatever the user's
/// interval, minimized state or pause say, and hands every sample to the recorder. What the window shows does not change: it still gets
/// a sample at the user's interval (the slow one while minimized) and none while paused. The user's settings are never written by a lease,
/// so the last release simply returns to whatever they are by then.
/// </summary>
public sealed class SamplerLoop : IDisposable, IRecordingSamples
{
    public const int HistoryLength = 60;

    /// <summary>How many recording samples wait for the handlers' dispatcher at most; past that the oldest waiting one is dropped.</summary>
    public const int RecordingQueueLength = 64;

    /// <summary>The period of the loop (and of every <see cref="RecordingSample"/>) while a recording lease is held.</summary>
    public static readonly TimeSpan RecordingPeriod = TimeSpan.FromSeconds(1);

    /// <summary>The loop re-reads pause, minimized and interval at least this often, so a change takes effect within a quarter second.</summary>
    private static readonly TimeSpan WaitSlice = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// While recording, a sample goes to the window when this much less than the user's interval has passed since the last one it got: a beat that
    /// came a few milliseconds early must not turn a 2 s window cadence into 3 s.
    /// </summary>
    private static readonly TimeSpan DisplaySlack = TimeSpan.FromMilliseconds(200);

    private readonly ISnapshotSource _source;
    private readonly ISamplerControl _control;
    private readonly Func<ISensorCadence?>? _cadence;
    private readonly TimeSpan _recordingPeriod;
    private readonly int _queueLength;
    private readonly HistoryRing<Snapshot> _history = new(HistoryLength);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _leaseGate = new();
    private Task? _task;
    private Snapshot? _latest;
    private int _started;
    private bool _disposed;

    // Recording lease state. _leases, _gpuLease, _sensorLease, _queue and _dispatcher change under _leaseGate; the loop reads _recording and _queue.
    private int _leases;
    private int _recording;
    private IDisposable? _gpuLease;
    private IDisposable? _sensorLease;
    private Channel<RecordingSample>? _queue;
    private Task? _dispatcher;
    private Action<RecordingSample>? _recordingHandlers;
    private long _recordingSequence;

    // Loop state: a refresh request is "sample now" while nobody records, and "show the next sample" while someone does.
    private int _sampleNow;
    private int _publishNext;
    private long _lastDisplayed;

    /// <param name="sensorCadence">
    /// Resolves the SensorHost cadence control when a recording lease needs it (the Hardware module registers after this one). Null, or
    /// returning null, means no sensor client: the lease then only changes the sampler's own period and GPU subscription.
    /// </param>
    public SamplerLoop(ISnapshotSource source, ISamplerControl control, Func<ISensorCadence?>? sensorCadence = null)
        : this(source, control, sensorCadence, RecordingPeriod, RecordingQueueLength)
    {
    }

    internal SamplerLoop(ISnapshotSource source, ISamplerControl control, Func<ISensorCadence?>? sensorCadence, TimeSpan recordingPeriod, int queueLength)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _cadence = sensorCadence;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(recordingPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueLength, 1);
        _recordingPeriod = recordingPeriod;
        _queueLength = queueLength;
    }

    /// <summary>A sample was taken (runs on a thread-pool thread; an exception in a handler is swallowed so one listener cannot stop the loop).</summary>
    public event Action<Snapshot>? Sampled;

    /// <inheritdoc />
    public event Action<RecordingSample> RecordingSampled
    {
        add
        {
            Action<RecordingSample>? seen;
            var current = Volatile.Read(ref _recordingHandlers);
            do
            {
                seen = current;
                current = Interlocked.CompareExchange(ref _recordingHandlers, (Action<RecordingSample>?)Delegate.Combine(seen, value), seen);
            }
            while (current != seen);
        }

        remove
        {
            Action<RecordingSample>? seen;
            var current = Volatile.Read(ref _recordingHandlers);
            do
            {
                seen = current;
                current = Interlocked.CompareExchange(ref _recordingHandlers, (Action<RecordingSample>?)Delegate.Remove(seen, value), seen);
            }
            while (current != seen);
        }
    }

    /// <summary>A sample failed. The loop carries on at the next beat.</summary>
    public event Action<Exception>? Failed;

    /// <summary>The settings this loop follows (pause, interval, minimized). Whoever drives the loop writes them here.</summary>
    public ISamplerControl Control => _control;

    public Snapshot? Latest => Volatile.Read(ref _latest);

    /// <summary>Oldest first, at most <see cref="HistoryLength"/> entries, a copy. Includes the no-baseline snapshots after a start or a pause.</summary>
    public IReadOnlyList<Snapshot> History => _history.ToArray();

    public bool Running => _task is { IsCompleted: false };

    /// <summary>True while at least one recording lease is held: the loop then runs at <see cref="RecordingPeriod"/> and samples even while paused.</summary>
    public bool IsRecording => Volatile.Read(ref _recording) != 0;

    /// <summary>The period the loop waits between samples right now: the recording period while leased, otherwise the user's (minimized-aware) interval, read fresh.</summary>
    internal TimeSpan EffectivePeriod => IsRecording ? _recordingPeriod : _control.EffectiveInterval;

    /// <summary>Held leases, for tests.</summary>
    internal int LeaseCount
    {
        get
        {
            lock (_leaseGate)
            {
                return _leases;
            }
        }
    }

    /// <summary>The loop does not sample while the user has paused, unless a recording needs it.</summary>
    private bool PausedForSampling => _control.Paused && !IsRecording;

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

    /// <summary>
    /// Takes a sample as soon as possible instead of waiting for the beat (refresh button). Ignored while paused. While a recording lease is
    /// held the loop keeps its own beat (an extra sample would put an uneven row in the recording), so the request only makes the NEXT sample
    /// of that beat go to the window.
    /// </summary>
    public void RequestSampleNow()
    {
        Volatile.Write(ref _sampleNow, 1);
        Wake();
    }

    /// <inheritdoc />
    public IDisposable AcquireRecordingLease()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_leaseGate)
        {
            if (_leases == 0)
            {
                EnterRecordingLocked();
            }

            _leases++;
        }

        Wake(); // a paused loop starts sampling, a long wait is cut to the recording beat
        return new RecordingLease(this);
    }

    /// <summary>Called with the gate held, for the first lease: one GPU subscription, one SensorHost one-second lease, the dispatcher.</summary>
    private void EnterRecordingLocked()
    {
        _gpuLease = _control.SubscribeGpu();
        try
        {
            _sensorLease = _cadence?.Invoke()?.AcquireOneSecondLease();
        }
        catch
        {
            _gpuLease.Dispose();
            _gpuLease = null;
            throw;
        }

        if (_queue is null)
        {
            var queue = Channel.CreateBounded<RecordingSample>(new BoundedChannelOptions(_queueLength)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
            _queue = queue;
            _dispatcher = Task.Run(() => DispatchAsync(queue.Reader));
        }

        Volatile.Write(ref _publishNext, 1); // the window gets the first recorded sample at once, as it does after a start
        Volatile.Write(ref _recording, 1);
    }

    private void ReleaseRecordingLease()
    {
        IDisposable? gpu;
        IDisposable? sensor;
        lock (_leaseGate)
        {
            if (_leases == 0 || --_leases > 0)
            {
                return;
            }

            // The last one: the loop goes back to the user's settings as they are NOW (nothing was copied at acquire time).
            Volatile.Write(ref _recording, 0);
            gpu = _gpuLease;
            sensor = _sensorLease;
            _gpuLease = null;
            _sensorLease = null;
        }

        sensor?.Dispose();
        gpu?.Dispose();
        Wake(); // re-read the settings now: a paused loop goes back to waiting, a minimized one to its slow beat
    }

    private void Wake()
    {
        try
        {
            _ = _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already waiting.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class RecordingLease(SamplerLoop owner) : IDisposable
    {
        private SamplerLoop? _owner = owner;

        public void Dispose()
        {
            // Disposing twice must not take someone else's lease away.
            if (Interlocked.Exchange(ref _owner, null) is { } o)
            {
                o.ReleaseRecordingLease();
            }
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

        // Whoever still holds a lease must not keep SensorHost at one second or the GPU counters on after the loop is gone.
        IDisposable? gpu;
        IDisposable? sensor;
        lock (_leaseGate)
        {
            _leases = 0;
            Volatile.Write(ref _recording, 0);
            gpu = _gpuLease;
            sensor = _sensorLease;
            _gpuLease = null;
            _sensorLease = null;
        }

        sensor?.Dispose();
        gpu?.Dispose();
        _ = _queue?.Writer.TryComplete();

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
                if (PausedForSampling)
                {
                    wasPaused = true;
                    _ = await WaitAsync(WaitSlice, ct).ConfigureAwait(false); // a lease wakes this at once; a refresh request while paused is dropped here
                    Volatile.Write(ref _sampleNow, 0);
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
        var period = EffectivePeriod;
        var rest = Stopwatch.GetElapsedTime(start, end) >= period ? period / 4 : TimeSpan.Zero;
        var earliest = end + (long)(rest.TotalSeconds * Stopwatch.Frequency);
        while (!ct.IsCancellationRequested && !PausedForSampling)
        {
            var now = Stopwatch.GetTimestamp();
            var due = Math.Max(start + (long)(EffectivePeriod.TotalSeconds * Stopwatch.Frequency), earliest);
            if (due <= now)
            {
                return;
            }

            var remaining = TimeSpan.FromSeconds((double)(due - now) / Stopwatch.Frequency);
            if (!await WaitAsync(remaining < WaitSlice ? remaining : WaitSlice, ct).ConfigureAwait(false))
            {
                continue;
            }

            // Woken: by a refresh request, or by a lease change (the loop only has to look at its settings again).
            if (Interlocked.Exchange(ref _sampleNow, 0) != 0)
            {
                if (!IsRecording)
                {
                    return; // RequestSampleNow
                }

                Volatile.Write(ref _publishNext, 1); // recording keeps its beat; the window gets the next sample of it
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

        var now = Stopwatch.GetTimestamp();
        if (Volatile.Read(ref _recording) != 0)
        {
            // Before the window's share, so a test (or a reader) that has seen the window's sample knows the recording one is queued.
            Record(snapshot);
            if (!ShowToWindow(now))
            {
                return;
            }
        }

        Volatile.Write(ref _lastDisplayed, now);
        Volatile.Write(ref _latest, snapshot);
        _history.Add(snapshot);
        Raise(Sampled, snapshot);
    }

    /// <summary>While recording the loop runs faster than the window wants (and while the user has paused it runs at all): the window gets a sample at its own cadence only.</summary>
    private bool ShowToWindow(long now)
    {
        if (_control.Paused)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _publishNext, 0) != 0)
        {
            return true;
        }

        var last = Volatile.Read(ref _lastDisplayed);
        return last == 0 || Stopwatch.GetElapsedTime(last, now) >= _control.EffectiveInterval - DisplaySlack;
    }

    /// <summary>Builds the immutable recording sample and queues it for the dispatcher. Never blocks: a full queue drops its oldest entry.</summary>
    private void Record(Snapshot snapshot)
    {
        var queue = Volatile.Read(ref _queue);
        if (queue is null)
        {
            return;
        }

        var sample = RecordingSampleBuilder.Build(snapshot, ++_recordingSequence, (int)_recordingPeriod.TotalMilliseconds);
        _ = queue.Writer.TryWrite(sample);
    }

    private async Task DispatchAsync(ChannelReader<RecordingSample> reader)
    {
        // One consumer, off the loop: a handler that takes seconds delays only the next recording sample's delivery.
        await foreach (var sample in reader.ReadAllAsync().ConfigureAwait(false))
        {
            Raise(Volatile.Read(ref _recordingHandlers), sample);
        }
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
