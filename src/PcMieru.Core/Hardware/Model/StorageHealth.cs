using System.Text.Json.Serialization;
using PcMieru.Wire;

namespace PcMieru.Core.Hardware.Model;

[JsonConverter(typeof(CamelCaseEnumConverter<StorageHealthKind>))]
public enum StorageHealthKind
{
    Nvme,
    Sata,
}

/// <summary>
/// Health of one physical drive. The whole Health value carries denied/unsupported/error (USB bridges and RAID
/// usually give unsupported). PercentageUsed is not clamped: NVMe may report more than 100.
/// Counters are 128-bit (decimal strings in JSON). Data written = Data Units x 512,000 bytes for NVMe.
/// </summary>
public sealed record DriveHealth(
    int PhysicalDriveNumber,
    string Model,
    Value<StorageHealth> Health);

public sealed record StorageHealth(
    StorageHealthKind Kind,
    Value<double> TemperatureC,
    Value<double> PercentageUsed,
    Value<double> AvailableSparePercent,
    Value<double> AvailableSpareThresholdPercent,
    Value<int> CriticalWarning,
    Value<U128> DataWrittenBytes,
    Value<U128> DataReadBytes,
    Value<U128> PowerOnHours,
    Value<U128> PowerCycles,
    Value<U128> UnsafeShutdowns,
    Value<U128> MediaErrors,
    IReadOnlyList<SmartAttribute> Attributes,
    NvmeHealthDetails? Nvme = null);

/// <summary>SATA SMART attribute as the drive reports it. Raw is not assumed to be a temperature or a count; UnitKnown says whether it is.</summary>
public sealed record SmartAttribute(
    int Id,
    string Name,
    int Current,
    int Worst,
    int Threshold,
    U128 Raw,
    bool UnitKnown);
