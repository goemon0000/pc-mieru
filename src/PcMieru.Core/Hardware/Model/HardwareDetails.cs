namespace PcMieru.Core.Hardware.Model;

// Optional "Details" records hung off the frozen hardware model (trailing, default null, omitted from JSON when null).
// They carry the per-part depth the owner wants (SPEC section 7) without changing the frozen record shapes.
// Sensitive leaves are named exactly "SerialNumber" so the redactor's key-name rule (serialNumber) blanks them.

public sealed record CacheInfo(int Level, string Type, long SizeBytes, int Associativity, int LineSizeBytes, int Instances);

public sealed record CpuDetails(
    Value<int> MaxClockMhz,
    Value<int> CurrentClockMhz,
    Value<int> ThreadsPerCore,
    Value<int> NumaNodes,
    Value<int> ProcessorGroups,
    Value<bool> VirtualizationFirmwareEnabled,
    Value<bool> HypervisorPresent,
    IReadOnlyList<CacheInfo> Caches);

/// <summary>SMBIOS Type 16 facts and the live free amount. Not an SPD read.</summary>
public sealed record MemoryDetails(
    Value<long> AvailableBytes,
    Value<string> EccType,
    Value<string> Location,
    Value<int> ArrayCount);

public sealed record MemoryModuleDetails(
    Value<string> BankLocator,
    Value<int> DataWidthBits,
    Value<int> TotalWidthBits,
    Value<int> MinVoltageMillivolts,
    Value<int> MaxVoltageMillivolts,
    Value<int> ConfiguredVoltageMillivolts,
    Value<string> MemoryTechnology);

public sealed record SystemIdentity(
    Value<string> Manufacturer,
    Value<string> Product,
    Value<string> Family,
    Value<string> SkuNumber,
    Value<string> Version,
    Value<string> SerialNumber);

public sealed record ChassisIdentity(
    Value<string> Type,
    Value<string> Manufacturer,
    Value<string> Version,
    Value<string> SerialNumber);

public sealed record BoardDetails(
    SystemIdentity System,
    ChassisIdentity Chassis,
    Value<string> SmbiosVersion,
    Value<long> BiosRomSizeBytes,
    Value<string> BiosRelease);

public sealed record GpuDetails(
    Value<string> Kind,
    Value<string> SubsystemId,
    Value<int> Revision,
    Value<string> DriverProvider,
    Value<long> DedicatedSystemBytes,
    Value<string> PciLocation,
    Value<int> PcieMaxGeneration,
    Value<int> PcieMaxWidth);

/// <summary>What the vendor API (NVML/ADLX, inside SensorHost) adds on top of the DXGI facts.</summary>
public sealed record GpuSensorExtras(
    Value<string> Vbios,
    Value<string> DriverVersion,
    Value<int> PcieGeneration,
    Value<int> PcieWidth,
    Value<int> PcieMaxGeneration,
    Value<int> PcieMaxWidth,
    Value<double> PowerLimitMinWatts,
    Value<double> PowerLimitMaxWatts,
    Value<double> PowerLimitDefaultWatts,
    Value<double> FanRpm,
    Value<double> MemoryClockMhz,
    Value<double> UtilizationPercent,
    Value<long> MemoryUsedBytes,
    Value<long> MemoryTotalBytes,
    string? PciBusId = null);

public sealed record StoragePartition(int Number, long StartBytes, long LengthBytes, string Type, string Style);

public sealed record StorageDetails(
    Value<string> MediaType,
    Value<int> LogicalSectorBytes,
    Value<int> PhysicalSectorBytes,
    Value<bool> Removable,
    IReadOnlyList<StoragePartition> Partitions,
    Value<string> DriverVersion,
    Value<int> PcieGeneration,
    Value<int> PcieWidth,
    Value<int> PcieMaxGeneration,
    Value<int> PcieMaxWidth);

public sealed record NetworkDetails(
    Value<string> Description,
    Value<bool> IsUp,
    Value<int> Mtu,
    Value<bool> Dhcp,
    Value<string> WifiStandard,
    Value<long> TransmitRateBitsPerSec,
    Value<long> ReceiveRateBitsPerSec);

public sealed record DisplayDetails(
    Value<string> PnpId,
    Value<int> ProductCode,
    Value<string> SerialNumber,
    Value<string> EdidVersion,
    Value<bool> EdidChecksumOk,
    Value<int> EdidExtensionBlocks,
    Value<double> DiagonalInches,
    Value<string> InputType,
    Value<int> BitsPerColor,
    Value<string> MonitorName,
    Value<double> MinVerticalRefreshHz,
    Value<double> MaxVerticalRefreshHz,
    Value<bool> HdrSupported,
    Value<bool> HdrStaticMetadata,
    Value<int> MaxLuminanceNits);

public sealed record AudioDetails(
    Value<string> State,
    Value<string> FormFactor,
    Value<string> Adapter,
    Value<int> Channels);

public sealed record UsbDeviceDetails(
    Value<string> Class,
    Value<string> Service,
    Value<string> Location,
    Value<int> Depth);

public sealed record BatteryDetails(
    Value<string> Chemistry,
    Value<string> Status,
    Value<int> VoltageMillivolts,
    Value<int> RemainingMinutes);

public sealed record OsDetails(
    Value<string> Edition,
    Value<string> DisplayVersion,
    Value<int> Ubr,
    Value<string> BootTimeUtc,
    Value<string> Locale);

/// <summary>NVMe SMART / Health log fields beyond the frozen StorageHealth leaves (128-bit counters stay strings in JSON).</summary>
public sealed record NvmeHealthDetails(
    Value<U128> HostReadCommands,
    Value<U128> HostWriteCommands,
    Value<U128> ControllerBusyTimeMinutes,
    Value<U128> ErrorLogEntries,
    Value<long> WarningCompositeTempMinutes,
    Value<long> CriticalCompositeTempMinutes,
    Value<IReadOnlyList<double>> TemperatureSensorsC,
    Value<IReadOnlyList<string>> CriticalWarningFlags);
