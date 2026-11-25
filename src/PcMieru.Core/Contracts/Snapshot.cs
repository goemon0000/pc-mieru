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

    /// <summary>
    /// Where each stage's values came from and when (the recorder's input). Set by the real source; null from sources that do not track it.
    /// Internal and never serialized: it is not part of the snapshot contract.
    /// </summary>
    internal SnapshotProvenance? Provenance { get; init; }
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
    Value<int> GpusState = default)
{
    /// <summary>AC connected and battery charge/state, from the same Windows power status the hardware page reads. Missing values say why (a desktop has no battery).</summary>
    public PowerInfo Power { get; init; } = PowerInfo.NotCollected;
}

/// <summary>
/// The machine's power source: whether AC is connected and, on a PC with a battery, its charge and state. Each value is missing with the
/// reason when Windows does not report it (a desktop has no battery: Unsupported, never 0 %).
/// </summary>
public sealed record PowerInfo(Value<bool> AcConnected, Value<int> BatteryPercent, Value<string> BatteryState)
{
    internal static readonly PowerInfo NotCollected = new(
        Value.NotCollected<bool>("none"),
        Value.NotCollected<int>("none"),
        Value.NotCollected<string>("none"));
}

/// <summary>Live CPU numbers. Static facts (model name, cache) live in the hardware model.</summary>
public sealed record CpuInfo(
    int LogicalProcessors,
    Value<double> TotalPercent,
    IReadOnlyList<Value<double>> PerLogicalPercent,
    Value<double> CurrentMhz)
{
    /// <summary>
    /// The CPU package temperature. Reading it needs a kernel driver, which PC Mieru does not install, so the real sampler says
    /// Unsupported; NotCollected is what a source that never asked leaves. Never a guess.
    /// </summary>
    public Value<double> TemperatureC { get; init; } = Value.NotCollected<double>("none");

    /// <summary>
    /// The CPU package power in watts, from the Windows Energy Meter counters (the package instance, milliwatts divided by 1000). Missing with
    /// the reason when this PC's Windows has no such counters: never 0 W.
    /// </summary>
    public Value<double> PackagePowerWatts { get; init; } = Value.NotCollected<double>("none");
}

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
    Value<double> ClockMhz)
{
    /// <summary>
    /// The temperature at which the vendor API says the GPU starts to slow down (NVML slowdown threshold). NotCollected when
    /// nothing was asked, Unsupported (with the reason) when the API gives none. Never a guessed number.
    /// </summary>
    public Value<double> TemperatureLimitC { get; init; } = Value.NotCollected<double>("none");

    /// <summary>
    /// Why the GPU is held back right now: the NVML clocks-event reasons as the driver's bit mask (0 = nothing holds it back). NotCollected when
    /// nothing was asked, Unsupported (with the reason) when the vendor API gives none. Never worked out from temperature or power.
    /// </summary>
    public Value<ulong> LimitReasons { get; init; } = Value.NotCollected<ulong>("none");

    /// <summary>Where the vendor values (temperature, power, fan, clock, limits) of this adapter came from. Null when none were merged. Never serialized.</summary>
    internal SourceOrigin? VendorOrigin { get; init; }

    /// <summary>
    /// The PCI bus number the kernel reports for this adapter (read once per adapter-list enumeration, not per tick). Null when the adapter has no PCI
    /// address (a virtual copy) or the kernel could not be asked. Public because the Parts Life board (another assembly) tells two cards of one model apart
    /// with it; unlike <see cref="VendorOrigin"/> it must cross the assembly line, so it is kept out of the JSON with an attribute instead of with
    /// <c>internal</c>. Never serialized: the pcmi JSON, the recordings and the snapshot schema are exactly what they were without it.
    /// </summary>
    [JsonIgnore]
    public uint? PciBus { get; init; }
}

public sealed record GpuEngineInfo(string Type, Value<double> Percent);
