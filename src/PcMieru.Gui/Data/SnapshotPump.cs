namespace PcMieru.Gui;

/// <summary>
/// Samples on the UI thread's timer and hands the snapshot to whoever listens, on the UI thread. A tick that finds the
/// previous sample still running is dropped, so a slow machine gets a slower list, not a growing queue.
/// </summary>
internal sealed class SnapshotPump : IDisposable
{
    public const int HistoryLength = 60;

    private readonly ISnapshotSource _source;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<Snapshot> _history = [];
    private readonly CancellationTokenSource _cancel = new();
    private bool _busy;

    public SnapshotPump(ISnapshotSource source, int intervalMs = 1000)
    {
        _source = source;
        _timer = new System.Windows.Forms.Timer { Interval = intervalMs };
        _timer.Tick += async (_, _) => await TickAsync().ConfigureAwait(true);
    }

    public event Action<Snapshot>? Sampled;

    public event Action<Exception>? Failed;

    public Snapshot? Latest { get; private set; }

    /// <summary>Oldest first, at most <see cref="HistoryLength"/> entries.</summary>
    public IReadOnlyList<Snapshot> History => _history;

    public bool Running => _timer.Enabled;

    public void Start()
    {
        if (_timer.Enabled)
        {
            return;
        }

        _source.ResetBaseline();
        _history.Clear();
        _timer.Start();
        _ = TickAsync();
    }

    public void Stop() => _timer.Stop();

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
        _cancel.Cancel();
        _timer.Dispose();
        _cancel.Dispose();
    }
}
