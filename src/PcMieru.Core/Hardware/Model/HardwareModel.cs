using System.Text.Json.Serialization;
using PcMieru.Wire;

namespace PcMieru.Core.Hardware.Model;

// Frozen shape. Owner C may add fields at the END of a record with a default value, and may add new enums/records.
// Every leaf is a Value<T> (status + source): a field the platform cannot give is Unsupported/Denied/..., never a guess.

[JsonConverter(typeof(CamelCaseEnumConverter<HardwareCategory>))]
public enum HardwareCategory
{
    All,
    Cpu,
    Memory,
    Board,
    Gpu,
    Storage,
    Network,
    Display,
    Audio,
    Usb,
    Battery,
    Os,
}

/// <summary>Categories that were not requested are NotCollected ("not requested"), not Unsupported.</summary>
public sealed record HardwareInfo(
    string SchemaVersion,
    [property: JsonConverter(typeof(UtcDateTimeJsonConverter))] DateTime CollectedAtUtc,
    Value<CpuHardware> Cpu,
    Value<MemoryHardware> Memory,
    Value<BoardHardware> Board,
    Value<IReadOnlyList<GpuHardware>> Gpus,
    Value<IReadOnlyList<StorageDevice>> Storage,
    Value<IReadOnlyList<NetworkAdapter>> Network,
    Value<IReadOnlyList<DisplayDevice>> Displays,
    Value<IReadOnlyList<AudioEndpoint>> Audio,
    Value<IReadOnlyList<UsbDevice>> Usb,
    Value<BatteryHardware> Battery,
    Value<OsHardware> Os)
{
    public const string CurrentSchemaVersion = "1";
}

public sealed record CpuHardware(
    Value<string> Name,
    Value<string> Vendor,
    Value<int> Family,
    Value<int> Model,
    Value<int> Stepping,
    Value<int> PhysicalCores,
    Value<int> LogicalProcessors,
    Value<int> PerformanceCores,
    Value<int> EfficiencyCores,
    Value<int> BaseClockMhz,
    Value<string> Socket,
    Value<long> L1CacheBytes,
    Value<long> L2CacheBytes,
    Value<long> L3CacheBytes,
    Value<IReadOnlyList<string>> InstructionSets);

[JsonConverter(typeof(CamelCaseEnumConverter<MemoryType>))]
public enum MemoryType
{
    Unknown,
    Ddr3,
    Ddr4,
    Ddr5,
    Lpddr4,
    Lpddr5,
    Other,
}

public sealed record MemoryHardware(
    Value<long> TotalBytes,
    Value<int> SlotsTotal,
    Value<int> SlotsUsed,
    Value<long> MaxCapacityBytes,
    IReadOnlyList<MemoryModule> Modules);

/// <summary>Speeds are MT/s (SMBIOS Type 17). Not an SPD read: XMP/EXPO state and timings are not available.</summary>
public sealed record MemoryModule(
    Value<string> Slot,
    Value<long> CapacityBytes,
    Value<MemoryType> Type,
    Value<int> SpeedMtPerSec,
    Value<int> ConfiguredSpeedMtPerSec,
    Value<string> Manufacturer,
    Value<string> PartNumber,
    Value<string> SerialNumber,
    Value<int> Ranks,
    Value<int> VoltageMillivolts,
    Value<string> FormFactor);

public sealed record BoardHardware(
    Value<string> Manufacturer,
    Value<string> Product,
    Value<string> Revision,
    Value<string> SerialNumber,
    Value<string> BiosVendor,
    Value<string> BiosVersion,
    Value<string> BiosDate,
    Value<bool> UefiBoot,
    Value<bool> SecureBoot,
    Value<bool> TpmPresent,
    Value<string> TpmVersion);

public sealed record GpuHardware(
    Value<string> Name,
    Value<string> Vendor,
    Value<string> DriverVersion,
    Value<string> DriverDate,
    Value<string> PciId,
    Value<string> Luid,
    Value<long> DedicatedBytes,
    Value<long> SharedBytes,
    Value<string> Vbios,
    Value<int> PcieGeneration,
    Value<int> PcieWidth,
    Value<double> PowerLimitWatts);

[JsonConverter(typeof(CamelCaseEnumConverter<StorageBus>))]
public enum StorageBus
{
    Unknown,
    Nvme,
    Sata,
    Usb,
    Raid,
    Scsi,
    Other,
}

public sealed record StorageDevice(
    int PhysicalDriveNumber,
    Value<string> Model,
    Value<string> SerialNumber,
    Value<string> Firmware,
    Value<StorageBus> Bus,
    Value<long> CapacityBytes,
    Value<bool> TrimSupported,
    Value<string> PartitionStyle,
    IReadOnlyList<StorageVolume> Volumes);

public sealed record StorageVolume(
    string Letter,
    string Label,
    string FileSystem,
    Value<long> TotalBytes,
    Value<long> FreeBytes);

[JsonConverter(typeof(CamelCaseEnumConverter<NetworkKind>))]
public enum NetworkKind
{
    Ethernet,
    Wifi,
    Virtual,
    Loopback,
    Other,
}

/// <summary>LinkSpeed is what the adapter negotiated, not a measurement.</summary>
public sealed record NetworkAdapter(
    Value<string> Name,
    Value<NetworkKind> Kind,
    Value<long> LinkSpeedBitsPerSec,
    Value<string> MacAddress,
    Value<IReadOnlyList<string>> IpAddresses,
    Value<IReadOnlyList<string>> DnsServers,
    Value<string> DriverVersion,
    Value<string> Ssid,
    Value<string> WifiBand,
    Value<int> SignalPercent);

[JsonConverter(typeof(CamelCaseEnumConverter<DisplayConnection>))]
public enum DisplayConnection
{
    Unknown,
    Hdmi,
    DisplayPort,
    Dvi,
    Vga,
    Internal,
    Other,
}

/// <summary>PreferredResolution is the EDID preferred timing ("recommended"), not asserted to be native.</summary>
public sealed record DisplayDevice(
    Value<string> Manufacturer,
    Value<string> Model,
    Value<int> ManufactureYear,
    Value<int> ManufactureWeek,
    Value<int> PhysicalWidthMm,
    Value<int> PhysicalHeightMm,
    Value<string> PreferredResolution,
    Value<string> CurrentResolution,
    Value<double> RefreshHz,
    Value<bool> HdrEnabled,
    Value<DisplayConnection> Connection,
    Value<string> GpuName);

[JsonConverter(typeof(CamelCaseEnumConverter<AudioDirection>))]
public enum AudioDirection
{
    Output,
    Input,
}

public sealed record AudioEndpoint(
    Value<string> Name,
    Value<AudioDirection> Direction,
    Value<bool> IsDefault,
    Value<int> SampleRateHz,
    Value<int> BitsPerSample);

public sealed record UsbDevice(
    Value<string> Name,
    Value<string> VendorId,
    Value<string> ProductId,
    Value<string> Manufacturer,
    Value<string> ParentId);

/// <summary>On a desktop the whole Battery value is Unsupported ("no battery"), not zeros.</summary>
public sealed record BatteryHardware(
    Value<int> ChargePercent,
    Value<bool> OnAc,
    Value<long> DesignCapacityMilliwattHours,
    Value<long> FullChargeCapacityMilliwattHours,
    Value<double> WearPercent,
    Value<int> CycleCount);

public sealed record OsHardware(
    Value<string> Name,
    Value<string> Version,
    Value<string> Build,
    Value<string> Architecture,
    Value<long> UptimeSec,
    Value<string> PowerPlan,
    Value<bool> VirtualizationBasedSecurity,
    Value<bool> Hvci);
