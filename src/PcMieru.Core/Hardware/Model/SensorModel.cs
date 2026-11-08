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
    Value<double> ClockMhz);

/// <summary>One stdout line of PCMieru.SensorHost (ndjson). A frame with no GPUs and a Gpus status of Unsupported is a normal answer.</summary>
public sealed record SensorFrame(
    string SchemaVersion,
    [property: JsonConverter(typeof(UtcDateTimeJsonConverter))] DateTime TakenAtUtc,
    Value<IReadOnlyList<GpuSensorReading>> Gpus)
{
    public const string CurrentSchemaVersion = "1";
}
