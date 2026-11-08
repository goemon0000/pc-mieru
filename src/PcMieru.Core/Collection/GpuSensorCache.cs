using System.Diagnostics;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Collection;

/// <summary>
/// The latest GPU sensor reading (temperature, power, fan, clock) from <see cref="ISensorClient"/>, refreshed in the
/// background about every two seconds while GPU numbers are wanted. A tick never waits for SensorHost: it takes what the last
/// refresh brought. A reading older than <see cref="MaxAge"/> is reported as not collected, not shown as current.
/// The client is resolved on every refresh (the Hardware module registers after this one).
/// </summary>
internal sealed class GpuSensorCache
{
    internal static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);
    private const string Source = "SensorHost";

    private readonly Func<ISensorClient?> _resolve;
    private readonly object _lock = new();
    private Value<IReadOnlyList<GpuSensorReading>> _last = Value.NotCollected<IReadOnlyList<GpuSensorReading>>(Source, "not read yet");
    private long _lastOkAt;
    private long _requestedAt;
    private Task? _pending;

    public GpuSensorCache(Func<ISensorClient?> resolve) => _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));

    /// <summary>The newest reading, and a refresh when one is due. Never blocks.</summary>
    public Value<IReadOnlyList<GpuSensorReading>> Current()
    {
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            var due = _requestedAt == 0 || Stopwatch.GetElapsedTime(_requestedAt, now) >= RefreshEvery;
            if (due && (_pending is null || _pending.IsCompleted))
            {
                _requestedAt = now;
                _pending = Task.Run(RefreshAsync);
            }

            if (_last.IsOk && _lastOkAt != 0 && Stopwatch.GetElapsedTime(_lastOkAt, now) > MaxAge)
            {
                return Value.NotCollected<IReadOnlyList<GpuSensorReading>>(Source, "the last sensor reading is too old to show as current");
            }

            return _last;
        }
    }

    /// <summary>Forget what was read (GPU numbers are no longer wanted): the next use starts from "not read yet".</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _last = Value.NotCollected<IReadOnlyList<GpuSensorReading>>(Source, "not read yet");
            _lastOkAt = 0;
            _requestedAt = 0;
        }
    }

    private async Task RefreshAsync()
    {
        Value<IReadOnlyList<GpuSensorReading>> result;
        try
        {
            var client = _resolve();
            if (client is null)
            {
                result = Value.Unsupported<IReadOnlyList<GpuSensorReading>>(Source, "no sensor client is registered");
            }
            else
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                result = await client.ReadGpuAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            result = Value.Missing<IReadOnlyList<GpuSensorReading>>(ValueStatus.Timeout, Source, "the sensor client did not answer in time");
        }
        catch (Exception ex)
        {
            result = Value.Missing<IReadOnlyList<GpuSensorReading>>(ValueStatus.Error, Source, $"{ex.GetType().Name}: {ex.Message}");
        }

        lock (_lock)
        {
            _last = result;
            if (result.IsOk)
            {
                _lastOkAt = Stopwatch.GetTimestamp();
            }
        }
    }
}
