using System.Diagnostics;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Sensors;

namespace PcMieru.Core.Collection;

/// <summary>
/// The latest GPU sensor reading (temperature, power, fan, clock) from <see cref="ISensorClient"/>, refreshed in the
/// background about every two seconds while GPU numbers are wanted (every second while the client is asked for one-second frames, that is
/// while a recording lease is held). A tick never waits for SensorHost: it takes what the last refresh brought. A reading older than
/// <see cref="MaxAge"/> is reported as not collected, not shown as current.
/// Next to the readings the cache keeps where they came from (<see cref="GpuSensorView.Origin"/>: SensorHost process generation, frame
/// sequence, capture time, nominal period), which is what the recorder needs and the display does not.
/// The client is resolved on every refresh (the Hardware module registers after this one).
/// </summary>
internal sealed class GpuSensorCache
{
    internal static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan FastRefreshEvery = TimeSpan.FromSeconds(1);

    /// <summary>The one-second refresh is due a little before a full second has passed: a sampler beat that came a few milliseconds early must not skip a whole frame.</summary>
    private static readonly TimeSpan FastSlack = TimeSpan.FromMilliseconds(200);

    internal static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);
    private const string Source = "SensorHost";
    private const int SlowPeriodMs = 2000;

    private readonly Func<ISensorClient?> _resolve;
    private readonly object _lock = new();
    private GpuSensorView _last = Initial();
    private long _lastOkAt;
    private long _requestedAt;
    private int _requestedPeriodMs = SlowPeriodMs;
    private long _unframedSequence;
    private Task? _pending;

    public GpuSensorCache(Func<ISensorClient?> resolve) => _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));

    /// <summary>How long after the last request the next refresh is due: two seconds, or just under one while the client is asked for one-second frames.</summary>
    internal static TimeSpan RefreshIntervalFor(int requestedPeriodMs) =>
        requestedPeriodMs <= SensorCadence.FastMs ? FastRefreshEvery - FastSlack : RefreshEvery;

    /// <summary>The newest reading, and a refresh when one is due. Never blocks.</summary>
    public Value<IReadOnlyList<GpuSensorReading>> Current() => CurrentView().Gpus;

    /// <summary>The newest reading with the frame it is from, and a refresh when one is due. Never blocks.</summary>
    public GpuSensorView CurrentView()
    {
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            var every = RefreshIntervalFor(_requestedPeriodMs);
            var due = _requestedAt == 0 || Stopwatch.GetElapsedTime(_requestedAt, now) >= every;
            if (due && (_pending is null || _pending.IsCompleted))
            {
                _requestedAt = now;
                _pending = Task.Run(RefreshAsync);
            }

            if (_last.Gpus.IsOk && _lastOkAt != 0 && Stopwatch.GetElapsedTime(_lastOkAt, now) > MaxAge)
            {
                return new GpuSensorView(
                    Value.NotCollected<IReadOnlyList<GpuSensorReading>>(Source, "the last sensor reading is too old to show as current"),
                    SourceOrigin.Missing(SourceGap.ReadFailed, _requestedPeriodMs));
            }

            return _last;
        }
    }

    /// <summary>Forget what was read (GPU numbers are no longer wanted): the next use starts from "not read yet".</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _last = Initial();
            _lastOkAt = 0;
            _requestedAt = 0;
            _requestedPeriodMs = SlowPeriodMs;
        }
    }

    private static GpuSensorView Initial() =>
        new(Value.NotCollected<IReadOnlyList<GpuSensorReading>>(Source, "not read yet"), SourceOrigin.Missing(SourceGap.NotStarted));

    private async Task RefreshAsync()
    {
        GpuSensorView view;
        var period = SlowPeriodMs;
        try
        {
            var client = _resolve();
            if (client is null)
            {
                view = Absent(Value.Unsupported<IReadOnlyList<GpuSensorReading>>(Source, "no sensor client is registered"), SourceGap.Unsupported, period);
            }
            else
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                if (client is IFramedSensorClient framed)
                {
                    view = await framed.ReadGpuFramedAsync(cts.Token).ConfigureAwait(false);
                    period = framed.RequestedPeriodMs;
                }
                else
                {
                    view = Unframed(await client.ReadGpuAsync(cts.Token).ConfigureAwait(false));
                }
            }
        }
        catch (OperationCanceledException)
        {
            view = Absent(
                Value.Missing<IReadOnlyList<GpuSensorReading>>(ValueStatus.Timeout, Source, "the sensor client did not answer in time"),
                SourceGap.ReadFailed,
                period);
        }
        catch (Exception ex)
        {
            view = Absent(
                Value.Missing<IReadOnlyList<GpuSensorReading>>(ValueStatus.Error, Source, $"{ex.GetType().Name}: {ex.Message}"),
                SourceGap.ReadFailed,
                period);
        }

        lock (_lock)
        {
            _last = view;
            _requestedPeriodMs = period;
            if (view.Gpus.IsOk)
            {
                _lastOkAt = Stopwatch.GetTimestamp();
            }
        }
    }

    private static GpuSensorView Absent(Value<IReadOnlyList<GpuSensorReading>> value, SourceGap gap, int periodMs) =>
        new(value, SourceOrigin.Missing(gap, periodMs));

    /// <summary>A client that does not number its frames (a test fake): each OK answer is stamped here as a new frame of generation 0 at the nominal two seconds.</summary>
    private GpuSensorView Unframed(Value<IReadOnlyList<GpuSensorReading>> answer) =>
        answer.IsOk
            ? new GpuSensorView(answer, new SourceOrigin(new FrameId(0, ++_unframedSequence), MonotonicClock.NowMicroseconds(), SlowPeriodMs))
            : Absent(answer, answer.Status == ValueStatus.Unsupported ? SourceGap.Unsupported : SourceGap.ReadFailed, SlowPeriodMs);
}
