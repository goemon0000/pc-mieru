using PcMieru.Core.Collection;

namespace PcMieru.Gui;

/// <summary>
/// The window's view of the one sampler: Core's <see cref="SamplerLoop"/> takes the samples on its own thread, and this class
/// hands each snapshot to whoever listens, on the UI thread. It owns no timer and never samples while the loop is running, so a
/// report built at the same time reads the same history instead of shortening the loop's deltas. If the UI is busy the
/// newest snapshot replaces the one that was waiting (the list gets slower, not a growing queue).
/// The user picks the interval and may pause; the window asks for slow mode while minimized (5 s, never faster than the
/// user's own choice). After a pause the loop drops the baseline itself; after a system sleep this does, so the first
/// sample afterwards shows "not collected" for rates instead of an average over the gap.
/// </summary>
internal sealed class SnapshotPump : IDisposable
{
    public const int HistoryLength = SamplerLoop.HistoryLength;

    /// <summary>The interval used while minimized or hidden (the loop's own value).</summary>
    public const int SlowIntervalMs = 5000;

    /// <summary>How long <see cref="TickAsync"/> waits for the loop before it gives up and lets the next beat show the result.</summary>
    private static readonly TimeSpan TickTimeout = TimeSpan.FromSeconds(10);

    private readonly SamplerLoop _loop;
    private readonly ISnapshotSource _source;
    private readonly ISamplerControl _settings;
    private readonly Control _ui;
    private readonly List<Snapshot> _history = [];
    private readonly List<TaskCompletionSource> _waiters = [];
    private readonly CancellationTokenSource _cancel = new();
    private int _intervalMs;
    private bool _started;
    private bool _powerHooked;
    private bool _disposed;
    private Snapshot? _pending;

    public SnapshotPump(PcMieruServices services, int intervalMs = 1000)
        : this(services.Sampler, services.Snapshots, intervalMs)
    {
    }

    public SnapshotPump(SamplerLoop loop, ISnapshotSource source, int intervalMs = 1000)
    {
        _loop = loop;
        _source = source;
        _settings = loop.Control;
        _intervalMs = GuiSettings.Snap(intervalMs);
        _settings.Interval = TimeSpan.FromMilliseconds(_intervalMs);

        // Built on the UI thread: the loop's events come from a pool thread and are posted back through this handle.
        _ui = new Control();
        _ = _ui.Handle;
        _loop.Sampled += OnLoopSampled;
        _loop.Failed += OnLoopFailed;
    }

    public event Action<Snapshot>? Sampled;

    public event Action<Exception>? Failed;

    /// <summary>Raised when interval, pause or slow mode changed (the status bar and Settings listen).</summary>
    public event Action? StateChanged;

    public Snapshot? Latest { get; private set; }

    /// <summary>Oldest first, at most <see cref="HistoryLength"/> entries. Only the UI thread reads or writes it.</summary>
    public IReadOnlyList<Snapshot> History => _history;

    /// <summary>True while the loop is sampling: started, not paused.</summary>
    public bool Running => _started && !_settings.Paused && _loop.Running;

    public bool Paused => _settings.Paused;

    public bool SlowMode => _settings.Minimized;

    /// <summary>The interval the user chose.</summary>
    public int IntervalMs => _intervalMs;

    /// <summary>The interval actually in force (slow mode raises it to 5 s, never lowers it).</summary>
    public int EffectiveIntervalMs => (int)_settings.EffectiveInterval.TotalMilliseconds;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        HookPower();
        _history.Clear();
        _loop.Start(); // resets the baseline; a paused loop waits for the resume
    }

    /// <summary>Stops delivering. The loop itself belongs to the services and stops with them.</summary>
    public void Stop()
    {
        _started = false;
        Interlocked.Exchange(ref _pending, null);
        CompleteWaiters();
    }

    public void SetInterval(int intervalMs)
    {
        var snapped = GuiSettings.Snap(intervalMs);
        if (snapped == _intervalMs)
        {
            return;
        }

        _intervalMs = snapped;
        _settings.Interval = TimeSpan.FromMilliseconds(snapped);
        StateChanged?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        if (_settings.Paused == paused)
        {
            return;
        }

        _settings.Paused = paused; // the loop drops its baseline when it sees the resume
        if (!paused && _started)
        {
            _loop.RequestSampleNow();
        }

        StateChanged?.Invoke();
    }

    public void SetSlowMode(bool slow)
    {
        if (_settings.Minimized == slow)
        {
            return;
        }

        _settings.Minimized = slow;
        if (!slow && _started)
        {
            _loop.RequestSampleNow(); // back to the normal beat: show something current at once
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// Asks for a sample now (after an action, F5) and returns once it has been delivered, on the caller's UI context.
    /// Paused, the loop takes nothing, so one sample is taken here instead (nobody else is sampling then). Before
    /// <see cref="Start"/> there is nothing to refresh.
    /// </summary>
    public async Task TickAsync()
    {
        if (!_started || _disposed || _cancel.IsCancellationRequested)
        {
            return;
        }

        if (_settings.Paused)
        {
            await SampleWhilePausedAsync().ConfigureAwait(true);
            return;
        }

        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Add(waiter);
        _loop.RequestSampleNow();
        try
        {
            await waiter.Task.WaitAsync(TickTimeout, _cancel.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // The next beat delivers it; a refresh that finds the sampler stuck must not hang the window.
        }
        finally
        {
            _waiters.Remove(waiter);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _started = false;
        _loop.Sampled -= OnLoopSampled;
        _loop.Failed -= OnLoopFailed;
        if (_powerHooked)
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _powerHooked = false;
        }

        _cancel.Cancel();
        CompleteWaiters();
        _ui.Dispose();
        _cancel.Dispose();
    }

    private async Task SampleWhilePausedAsync()
    {
        try
        {
            Deliver(await _source.SampleAsync(_cancel.Token).ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    // Pool thread. Only the newest snapshot waits for the UI thread.
    private void OnLoopSampled(Snapshot snapshot)
    {
        if (Interlocked.Exchange(ref _pending, snapshot) is null)
        {
            Post(DeliverPending);
        }
    }

    private void OnLoopFailed(Exception exception) =>
        Post(() =>
        {
            if (_started)
            {
                Failed?.Invoke(exception);
            }

            CompleteWaiters();
        });

    private void DeliverPending()
    {
        if (Interlocked.Exchange(ref _pending, null) is { } snapshot && _started)
        {
            Deliver(snapshot);
        }
    }

    private void Deliver(Snapshot snapshot)
    {
        Latest = snapshot;
        _history.Add(snapshot);
        if (_history.Count > HistoryLength)
        {
            _history.RemoveAt(0);
        }

        Sampled?.Invoke(snapshot);
        CompleteWaiters();
    }

    private void CompleteWaiters()
    {
        foreach (var waiter in _waiters.ToArray())
        {
            waiter.TrySetResult();
        }
    }

    private void Post(Action action)
    {
        try
        {
            if (!_disposed && _ui.IsHandleCreated && !_ui.IsDisposed)
            {
                _ = _ui.BeginInvoke(action);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
            // The window handle went away while the loop was still delivering: shutdown.
        }
    }

    private void HookPower()
    {
        if (_powerHooked)
        {
            return;
        }

        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _powerHooked = true;
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume && _started)
        {
            // SystemEvents' thread. The reset only raises a flag the next sample reads, so any thread may do it.
            _source.ResetBaseline();
        }
    }
}
