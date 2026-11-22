using System.ComponentModel;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Hardware.Sensors;

/// <summary>Timings of <see cref="SensorHostClient"/>. The defaults are the product's; tests shorten them.</summary>
internal sealed record SensorHostOptions
{
    public static readonly SensorHostOptions Default = new();

    /// <summary>No line for this long means SensorHost (or a vendor driver inside it) is hung: kill and restart.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>How long a read waits for the very first reading of a freshly launched SensorHost.</summary>
    public TimeSpan FirstReadingWait { get; init; } = TimeSpan.FromSeconds(4);

    public TimeSpan BackoffStart { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>SensorHost is stopped when nobody asked for a reading for this long, and started again by the next ask.</summary>
    public TimeSpan IdleStopAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the watchdog looks. Zero turns the timer off (tests call Tick themselves).</summary>
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// <see cref="ISensorClient"/> that talks to PCMieru.SensorHost.exe. NVML and ADLX are loaded only inside that process: when a vendor
/// driver hangs, this side notices that no line arrived for six seconds, kills the process and starts a new one (waiting 1, 2, 4 ... 30 s
/// between failed starts). The process is launched by the first read, not at construction, so a program that never asks for GPU
/// sensors never starts it. A reading older than the timeout is never returned as if it were current.
/// While a one-second lease is held (<see cref="AcquireOneSecondLease"/>) the process is asked for a frame every second instead of every two,
/// and a process started or restarted meanwhile starts at one second.
/// </summary>
public sealed class SensorHostClient : ISensorClient, IFramedSensorClient, ISensorCadence
{
    private const string Src = "sensorhost";

    private readonly ISensorProcessFactory _factory;
    private readonly SensorHostOptions _options;
    private readonly Func<long> _nowMs;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Timer? _watchdog;

    private ISensorProcess? _process;
    private long _startedAt;
    private long _lastFrameAt = -1;
    private Value<IReadOnlyList<GpuSensorReading>>? _latest;
    private SourceOrigin? _latestOrigin;
    private SourceGap _gap = SourceGap.NotStarted;
    private long _generation;
    private int _leases;
    private string? _incompatible;
    private TaskCompletionSource _firstFrame = NewSignal();
    private int _failures;
    private long _nextStartAt;
    private string _problem = "SensorHost has not been started";
    private bool _missing;
    private bool _wanted;
    private long _lastAskedAt;
    private bool _disposed;

    /// <param name="exePath">PCMieru.SensorHost.exe. By default the one next to the running program.</param>
    public SensorHostClient(string? exePath = null)
        : this(new SystemSensorProcessFactory(exePath ?? SystemSensorProcessFactory.DefaultPath()), SensorHostOptions.Default)
    {
    }

    internal SensorHostClient(ISensorProcessFactory factory, SensorHostOptions options, Func<long>? nowMs = null)
    {
        _factory = factory;
        _options = options;
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
        if (options.WatchdogInterval > TimeSpan.Zero)
        {
            _watchdog = new Timer(_ => Tick(), null, options.WatchdogInterval, options.WatchdogInterval);
        }
    }

    /// <summary>The number of failed launches, hangs and exits in a row. Zero while readings are arriving.</summary>
    internal int ConsecutiveFailures
    {
        get
        {
            lock (_gate)
            {
                return _failures;
            }
        }
    }

    internal bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is not null;
            }
        }
    }

    /// <summary>The period (ms) currently requested from SensorHost: 1000 while a one-second lease is held, otherwise 2000.</summary>
    int IFramedSensorClient.RequestedPeriodMs
    {
        get
        {
            lock (_gate)
            {
                return RequestedPeriodLocked();
            }
        }
    }

    public async Task<Value<IReadOnlyList<GpuSensorReading>>> ReadGpuAsync(CancellationToken ct = default) =>
        (await ReadFramedAsync(ct).ConfigureAwait(false)).Gpus;

    Task<GpuSensorView> IFramedSensorClient.ReadGpuFramedAsync(CancellationToken ct) => ReadFramedAsync(ct);

    public IDisposable AcquireOneSecondLease()
    {
        lock (_gate)
        {
            _leases++;
            if (_leases == 1)
            {
                // The running child changes at its next sampling boundary. A child that is not running (or dies before then) is started
                // at one second, because every start reads RequestedPeriodLocked.
                _process?.SetPeriod(SensorCadence.FastMs);
            }
        }

        return new CadenceLease(this);
    }

    private void ReleaseLease()
    {
        lock (_gate)
        {
            if (_leases > 0 && --_leases == 0)
            {
                _process?.SetPeriod(SensorCadence.SlowMs);
            }
        }
    }

    private int RequestedPeriodLocked() => _leases > 0 ? SensorCadence.FastMs : SensorCadence.SlowMs;

    private GpuSensorView Missing(Value<IReadOnlyList<GpuSensorReading>> value, SourceGap gap) =>
        new(value, SourceOrigin.Missing(gap, RequestedPeriodLocked()));

    private async Task<GpuSensorView> ReadFramedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task wait;
        lock (_gate)
        {
            if (_disposed)
            {
                return Missing(Value.Gone<IReadOnlyList<GpuSensorReading>>(Src, "the sensor client was shut down"), SourceGap.NotStarted);
            }

            if (_incompatible is not null)
            {
                return Missing(Value.Failed<IReadOnlyList<GpuSensorReading>>(Src, _incompatible), SourceGap.ReadFailed);
            }

            if (_missing)
            {
                return Missing(Value.Unsupported<IReadOnlyList<GpuSensorReading>>(Src, _problem), SourceGap.Unsupported);
            }

            var now = _nowMs();
            _wanted = true;
            _lastAskedAt = now;
            StartIfDueLocked(now);
            if (_missing)
            {
                // The launch just found the executable missing: say so now, not only on the next ask.
                return Missing(Value.Unsupported<IReadOnlyList<GpuSensorReading>>(Src, _problem), SourceGap.Unsupported);
            }

            if (Fresh(now, out var fresh))
            {
                return fresh;
            }

            if (_process is null)
            {
                return Missing(NotRunning(now), _gap);
            }

            wait = _firstFrame.Task;
        }

        // A process was launched (now or earlier) and has not produced a reading yet.
        try
        {
            await wait.WaitAsync(_options.FirstReadingWait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        lock (_gate)
        {
            if (_disposed)
            {
                // Dispose woke us up: say so, instead of "not started".
                return Missing(Value.Gone<IReadOnlyList<GpuSensorReading>>(Src, "the sensor client was shut down"), SourceGap.NotStarted);
            }

            if (_incompatible is not null)
            {
                return Missing(Value.Failed<IReadOnlyList<GpuSensorReading>>(Src, _incompatible), SourceGap.ReadFailed);
            }

            var now = _nowMs();
            if (Fresh(now, out var fresh))
            {
                return fresh;
            }

            if (_process is null)
            {
                return Missing(NotRunning(now), _gap);
            }

            return Missing(
                Value.Missing<IReadOnlyList<GpuSensorReading>>(
                    ValueStatus.Timeout,
                    Src,
                    _lastFrameAt >= 0 ? "SensorHost has stopped answering and will be restarted" : "SensorHost has not produced a reading yet"),
                _lastFrameAt >= 0 ? SourceGap.HostRestart : SourceGap.NotStarted);
        }
    }

    /// <summary>One look of the watchdog. Public for tests through InternalsVisibleTo; the timer calls it twice a second.</summary>
    internal void Tick()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var now = _nowMs();
                var process = _process;
                if (_wanted && now - _lastAskedAt >= (long)_options.IdleStopAfter.TotalMilliseconds)
                {
                    // Nobody has looked at GPU sensors for a while. This is not a failure and does not back off. Checked before the
                    // "restart a dead host" branch so a host that keeps failing is not relaunched for an app that stopped asking.
                    _wanted = false;
                    if (process is not null)
                    {
                        StopLocked("stopped because nothing asked for GPU readings", false, now);
                    }

                    return;
                }

                if (process is null)
                {
                    if (_wanted)
                    {
                        StartIfDueLocked(now);
                    }

                    return;
                }

                if (process.HasExited)
                {
                    var tail = process.StderrTail;
                    FailLocked(now, $"SensorHost exited (code {process.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}){(tail.Length > 0 ? ": " + tail : string.Empty)}");
                    return;
                }

                var reference = _lastFrameAt >= 0 ? _lastFrameAt : _startedAt;
                if (now - reference > (long)_options.StaleAfter.TotalMilliseconds)
                {
                    FailLocked(now, $"SensorHost gave no reading for {_options.StaleAfter.TotalSeconds:0.#} s and was restarted");
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The watchdog must never take the process down.
            lock (_gate)
            {
                _problem = "the SensorHost watchdog failed: " + ex.GetType().Name;
            }
        }
    }

    public void Dispose()
    {
        ISensorProcess? process;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            process = _process;
            _process = null;
            _latest = null;
            _firstFrame.TrySetResult();
        }

        _watchdog?.Dispose();
        _lifetime.Cancel();
        if (process is not null)
        {
            // Closing the input is the polite stop; a host stuck inside a vendor driver will not notice, so it is killed after a short wait.
            process.CloseInput();
            if (!process.WaitForExit(300))
            {
                process.Kill();
            }

            process.Dispose();
        }

        _lifetime.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool Fresh(long now, out GpuSensorView view)
    {
        if (_latest is { } latest && _latestOrigin is { } origin && _lastFrameAt >= 0 && now - _lastFrameAt <= (long)_options.StaleAfter.TotalMilliseconds)
        {
            view = new GpuSensorView(latest, origin);
            return true;
        }

        view = default;
        return false;
    }

    private Value<IReadOnlyList<GpuSensorReading>> NotRunning(long now)
    {
        var wait = Math.Max(0, _nextStartAt - now);
        var retry = wait > 0 ? $"; trying again in {Math.Ceiling(wait / 1000.0):0} s" : string.Empty;
        return Value.Failed<IReadOnlyList<GpuSensorReading>>(Src, _problem + retry);
    }

    private void StartIfDueLocked(long now)
    {
        if (_disposed || _process is not null || _missing || _incompatible is not null || now < _nextStartAt)
        {
            return;
        }

        ISensorProcess process;
        var generation = _generation + 1;
        try
        {
            // A restart with a lease held starts at one second at once: the new child never runs at two seconds and waits for a request.
            process = _factory.Start(RequestedPeriodLocked(), generation);
        }
        catch (FileNotFoundException ex)
        {
            // Not a transient failure: retrying every second would only spend CPU. A restart of the app is what fixes a missing file.
            _missing = true;
            _problem = ex.Message;
            return;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            FailLocked(now, "SensorHost could not be started: " + ex.Message);
            return;
        }

        _process = process;
        _generation = generation;
        _startedAt = now;
        _lastFrameAt = -1;
        _latest = null;
        _latestOrigin = null;
        _firstFrame = NewSignal();
        _problem = "SensorHost is starting";
        _ = Task.Run(() => ReadLoopAsync(process), _lifetime.Token);
    }

    private async Task ReadLoopAsync(ISensorProcess process)
    {
        try
        {
            while (await process.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var ok = SensorFrameParser.TryParse(line, out var frame, out var error, out var incompatible);
                lock (_gate)
                {
                    if (_process != process)
                    {
                        return;
                    }

                    if (incompatible)
                    {
                        // A SensorHost of another build (old child + new parent, or the reverse). Restarting it cannot make it speak this
                        // schema, so this is a standing failure with its own words, not a silent restart loop that hides the reason.
                        _incompatible = "SensorHost does not match this app: " + error;
                        StopLocked(_incompatible, true, _nowMs());
                        return;
                    }

                    if (!ok)
                    {
                        _problem = "SensorHost wrote something unreadable: " + error;
                        continue;
                    }

                    _latest = frame!.Gpus;
                    _latestOrigin = new SourceOrigin(
                        new FrameId(frame.GenerationId > 0 ? frame.GenerationId : _generation, frame.Sequence),
                        frame.MonotonicUs,
                        frame.PeriodMs);
                    _lastFrameAt = _nowMs();
                    _failures = 0;
                    _problem = "SensorHost is running";
                    _firstFrame.TrySetResult();
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process went away; the watchdog notices and restarts it.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not expected. The loop ends here, so the host will be silent and the watchdog restarts it; keep the reason for the message.
            lock (_gate)
            {
                if (_process == process)
                {
                    _problem = "reading the SensorHost output failed: " + ex.GetType().Name;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_process == process)
                {
                    // Wake anybody waiting for a first reading that will not come.
                    _firstFrame.TrySetResult();
                }
            }
        }
    }

    /// <summary>Kills and drops the current process and schedules the next start with exponential backoff.</summary>
    private void FailLocked(long now, string reason)
    {
        StopLocked(reason, true, now);
        _failures++;
        var factor = Math.Pow(2, Math.Min(_failures - 1, 16));
        var delayMs = Math.Min(_options.BackoffMax.TotalMilliseconds, _options.BackoffStart.TotalMilliseconds * factor);
        _nextStartAt = now + (long)delayMs;
    }

    private void StopLocked(string reason, bool failure, long now)
    {
        var process = _process;
        _process = null;
        _latest = null;
        _latestOrigin = null;
        _lastFrameAt = -1;
        _problem = reason;
        _gap = failure ? SourceGap.HostRestart : SourceGap.NotStarted;
        if (!failure)
        {
            _nextStartAt = now;
        }

        _firstFrame.TrySetResult();
        if (process is not null)
        {
            process.CloseInput();
            process.Kill();
            process.Dispose();
        }
    }

    /// <summary>One one-second lease. Disposing twice gives back one count only.</summary>
    private sealed class CadenceLease(SensorHostClient owner) : IDisposable
    {
        private SensorHostClient? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseLease();
    }
}
