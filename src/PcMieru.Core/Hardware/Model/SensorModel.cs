using System.Text.Json.Serialization;

namespace PcMieru.Core.Hardware.Model;

/// <summary>One GPU as seen by the vendor API inside SensorHost. Id matches GpuInfo.Id (adapter LUID) when known.</summary>
public sealed record GpuSensorReading(
    string Id,
    string Name,
    Value<double> TemperatureC,
    Value<double> PowerWatts,
    Value<double> PowerLimitWatts,
    Value<double> FanPercent,
    Value<double> ClockMhz,
    GpuSensorExtras? Extras = null);

/// <summary>
/// One stdout line of PCMieru.SensorHost (ndjson). A frame with no GPUs and a Gpus status of Unsupported is a normal answer.
/// Schema 2 adds what the recorder needs to tell one acquisition from the next: the child process generation, the frame's sequence number
/// in that process, the nominal period the child was running at when it took the frame, and the monotonic capture time.
/// </summary>
/// <param name="GenerationId">Which SensorHost process wrote the frame (the parent numbers its children from 1 and passes the number on the command line).</param>
/// <param name="Sequence">1 for the first frame of that process, then +1 per frame.</param>
/// <param name="PeriodMs">The nominal period the frame stands for: the cadence the child was actually running at (not the one just requested), that is the length of the wait that led up to this frame; for the first frame, the period the child started at. 1000 or 2000 in practice.</param>
/// <param name="MonotonicUs">The system-wide monotonic clock (QueryPerformanceCounter) in microseconds when the readings were taken; the parent's <see cref="MonotonicClock"/> is the same clock.</param>
public sealed record SensorFrame(
    string SchemaVersion,
    [property: JsonConverter(typeof(UtcDateTimeJsonConverter))] DateTime TakenAtUtc,
    Value<IReadOnlyList<GpuSensorReading>> Gpus,
    long GenerationId = 0,
    long Sequence = 0,
    int PeriodMs = 0,
    long MonotonicUs = 0)
{
    public const string CurrentSchemaVersion = "2";
}
