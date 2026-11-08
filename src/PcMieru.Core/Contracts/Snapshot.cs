using System.Text.Json.Serialization;

namespace PcMieru.Core;

/// <summary>
/// One observation of the whole machine. GUI, CLI and MCP all read this and nothing else for live numbers.
/// Rates and percentages are computed over the observation window [WindowStartUtc, TakenAtUtc].
/// Contract rule: add fields only at the end of a record, with a default value.
/// </summary>
public sealed record Snapshot(
    string SchemaVersion,
    [property: JsonConverter(typeof(UtcDateTimeJsonConverter))] DateTime TakenAtUtc,
    [property: JsonConverter(typeof(UtcDateTimeJsonConverter))] DateTime WindowStartUtc,
    int IntervalMs,
    SystemInfo System,
    IReadOnlyList<ProcessInfo> Processes)
{
    public const string CurrentSchemaVersion = "1";
}

/// <summary>
/// The machine-wide live numbers. <c>DisksState</c>, <c>NicsState</c> and <c>GpusState</c> say whether the matching list could be read
/// (see <see cref="ListStatus"/>): an empty list is only "none" when its state is Ok.
/// </summary>
public sealed record SystemInfo(
    CpuInfo Cpu,
    MemoryInfo Memory,
    IReadOnlyList<DiskInfo> Disks,
    IReadOnlyList<NicInfo> Nics,
    IReadOnlyList<GpuInfo> Gpus,
    long UptimeSec,
    Value<int> DisksState = default,
    Value<int> NicsState = default,
    Value<int> GpusState = default);

/// <summary>Live CPU numbers. Static facts (model name, cache) live in the hardware model.</summary>
public sealed record CpuInfo(
    int LogicalProcessors,
    Value<double> TotalPercent,
    IReadOnlyList<Value<double>> PerLogicalPercent,
    Value<double> CurrentMhz);

public sealed record MemoryInfo(
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    Value<long> CommitTotalBytes,
    Value<long> CommitLimitBytes,
    Value<long> CacheBytes,
    Value<long> PagedPoolBytes,
    Value<long> NonPagedPoolBytes);

public sealed record DiskInfo(
    string Id,
    string Name,
    Value<double> ActivePercent,
    Value<double> ReadBytesPerSec,
    Value<double> WriteBytesPerSec,
    Value<double> ResponseTimeMs);

public sealed record NicInfo(
    string Id,
    string Name,
    bool IsUp,
    Value<double> SendBytesPerSec,
    Value<double> ReceiveBytesPerSec,
    Value<long> LinkSpeedBitsPerSec);

/// <summary>Utilization is the busiest engine, as Task Manager does; Engines has the per-engine numbers.</summary>
public sealed record GpuInfo(
    string Id,
    string Name,
    Value<double> UtilizationPercent,
    IReadOnlyList<GpuEngineInfo> Engines,
    Value<long> DedicatedUsedBytes,
    Value<long> DedicatedTotalBytes,
    Value<long> SharedUsedBytes,
    Value<double> TemperatureC,
    Value<double> PowerWatts,
    Value<double> FanPercent,
    Value<double> ClockMhz);

public sealed record GpuEngineInfo(string Type, Value<double> Percent);
