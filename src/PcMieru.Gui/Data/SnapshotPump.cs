namespace PcMieru.Gui;

/// <summary>
/// Samples on the UI thread's timer and hands the snapshot to whoever listens, on the UI thread. A tick that finds the
/// previous sample still running is dropped, so a slow machine gets a slower list, not a growing queue.
/// The user picks the interval and may pause; the window asks for slow mode while minimized (5 s, never faster than the
/// user's own choice). After a pause or a system sleep the baseline is reset, so the first sample afterwards shows
/// "not collected" for rates instead of an average over the gap.
/// </summary>
internal sealed class SnapshotPump : IDisposable
{
    public const int HistoryLength = 60;

    /// <summary>The interval used while minimized or hidden.</summary>
    public const int SlowIntervalMs = 5000;

    private readonly ISnapshotSource _source;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<Snapshot> _history = [];
    private readonly CancellationTokenSource _cancel = new();
    private int _intervalMs;
    private bool _started;
    private bool _paused;
    private bool _slow;
    private bool _busy;
    private bool _powerHooked;
    private volatile bool _resetPending;

    public SnapshotPump(ISnapshotSource source, int intervalMs = 1000)
    {
        _source = source;
        _intervalMs = GuiSettings.Snap(intervalMs);
        _timer = new System.Windows.Forms.Timer { Interval = _intervalMs };
        _timer.Tick += async (_, _) => await TickAsync().ConfigureAwait(true);
    }

    public event Action<Snapshot>? Sampled;

    public event Action<Exception>? Failed;

    /// <summary>Raised when interval, pause or slow mode changed (the status bar and Settings listen).</summary>
    public event Action? StateChanged;

    public Snapshot? Latest { get; private set; }

    /// <summary>Oldest first, at most <see cref="HistoryLength"/> entries.</summary>
    public IReadOnlyList<Snapshot> History => _history;

    /// <summary>True while the timer is ticking: started, not paused.</summary>
    public bool Running => _timer.Enabled;

    public bool Paused => _paused;

    public bool SlowMode => _slow;

    /// <summary>The interval the user chose.</summary>
    public int IntervalMs => _intervalMs;

    /// <summary>The interval actually in force (slow mode raises it to 5 s, never lowers it).</summary>
    public int EffectiveIntervalMs => _slow ? Math.Max(_intervalMs, SlowIntervalMs) : _intervalMs;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        HookPower();
        _history.Clear();
        _source.ResetBaseline();
        if (!_paused)
        {
            _timer.Interval = EffectiveIntervalMs;
            _timer.Start();
            _ = TickAsync();
        }
    }

    public void Stop()
    {
        _started = false;
        _timer.Stop();
    }

    public void SetInterval(int intervalMs)
    {
        var snapped = GuiSettings.Snap(intervalMs);
        if (snapped == _intervalMs)
        {
            return;
        }

        _intervalMs = snapped;
        ApplyTimer(restartedNow: false);
        StateChanged?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused)
        {
            return;
        }

        _paused = paused;
        if (!paused)
        {
            _source.ResetBaseline();
        }

        ApplyTimer(restartedNow: !paused);
        StateChanged?.Invoke();
    }

    public void SetSlowMode(bool slow)
    {
        if (_slow == slow)
        {
            return;
        }

        _slow = slow;
        ApplyTimer(restartedNow: !slow);
        StateChanged?.Invoke();
    }

    /// <summary>Takes one sample now (the timer calls this too). Continues on the caller's UI context.</summary>
    public async Task TickAsync()
    {
        if (_busy || _cancel.IsCancellationRequested)
        {
            return;
        }

        _busy = true;
        try
        {
            if (_resetPending)
            {
                _resetPending = false;
                _source.ResetBaseline();
            }

            var snapshot = await _source.SampleAsync(_cancel.Token).ConfigureAwait(true);
            Latest = snapshot;
            _history.Add(snapshot);
            if (_history.Count > HistoryLength)
            {
                _history.RemoveAt(0);
            }

            Sampled?.Invoke(snapshot);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    public void Dispose()
    {
        if (_powerHooked)
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _powerHooked = false;
        }

        _cancel.Cancel();
        _timer.Dispose();
        _cancel.Dispose();
    }

    private void ApplyTimer(bool restartedNow)
    {
        if (!_started)
        {
            return;
        }

        if (_paused)
        {
            _timer.Stop();
            return;
        }

        _timer.Interval = EffectiveIntervalMs;
        if (!_timer.Enabled)
        {
            _timer.Start();
        }

        if (restartedNow)
        {
            _ = TickAsync();
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
            // Runs on SystemEvents' thread; the reset itself happens on the UI thread at the next tick.
            _resetPending = true;
        }
    }
}
