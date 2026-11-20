using System.Text.Json.Serialization;

namespace PcMieru.Checks;

// Every enum here is written as a lowercase string: single words as they are ("completed"), several words in
// snake_case ("energy_meter"), digits stay attached to the word ("win32"). One generic converter does both ways
// and rejects any string that is not a member (SnakeCaseEnumConverter).

/// <summary>What a run checks. DESIGN-A §3: stage A has no gpu (added by the version that opens the gate).</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<CheckKind>))]
public enum CheckKind
{
    System,
    Cpu,
    Memory,
    Storage,
}

/// <summary>How the run ended. Individual missing readings do not make a run failed (DESIGN-A §2).</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<CheckOutcome>))]
public enum CheckOutcome
{
    Completed,
    Stopped,
    Failed,
}

/// <summary>The verdict of a whole result: four values (SPEC-unify §4.0). An observation-only item has no say in it.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<CheckVerdict>))]
public enum CheckVerdict
{
    Ok,
    Caution,
    Check,
    Unavailable,
}

/// <summary>The verdict of one item: the four overall values plus Info (observation only, never counted in the overall verdict).</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<ItemVerdict>))]
public enum ItemVerdict
{
    Ok,
    Caution,
    Check,
    Unavailable,
    Info,
}

/// <summary>
/// Why an item has no (complete) value. A complete reading has no reason (null). A missing value always has one;
/// a value that is there with a reason is only allowed with Partial (a partial value, e.g. a stopped run).
/// </summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<CheckReason>))]
public enum CheckReason
{
    /// <summary>The machine or the connection has no such thing, or does not expose it.</summary>
    Unsupported,

    /// <summary>Permission or UAC refused.</summary>
    Denied,

    /// <summary>The deadline passed.</summary>
    Timeout,

    /// <summary>Reading or verification failed.</summary>
    Error,

    /// <summary>Stopped, not enough data, or not comparable.</summary>
    Partial,
}

/// <summary>Where a value comes from. A fixed list (DESIGN-A §14.0): add to it only after the design table is changed.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<CheckSource>))]
public enum CheckSource
{
    Os,
    Win32,
    Smbios,
    Setupapi,
    Dxgi,
    Wmi,
    Eventlog,
    Pdh,
    EnergyMeter,
    Load,
    Nvme,
    Ata,
    StorageQuery,
    Baseline,
}

/// <summary>The fixed values of evidence.code (DESIGN-A §14.0). Written as snake_case strings.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<CheckEvidenceCode>))]
public enum CheckEvidenceCode
{
    // Why a .delta could not be computed (DESIGN-A §14.5). CheckEvidenceCodes.IsBaseline() is true for exactly these.
    BaselineFirst,
    BaselineNoIdentity,
    BaselineDuplicate,
    BaselineDeviceChanged,
    BaselineCounterReset,
    BaselineClock,
    BaselineConcurrent,
    BaselineMissingCounter,
    BaselineReadFailed,
    BaselineLegacyRefused,

    // What a verdict rests on.
    CounterIncreased,
    CounterDecreased,
    NewBits,
    LikelyCable,
    MixedCapacity,
    BelowRated,
    AboveRated,
    DisabledCode22,
    DeviceError,
    TooShort,
    PinUnknown,
    PatternMismatch,
    ComputeMismatch,
    PastThresholdBreach,
    CurrentThresholdBreach,
    TempOverCriticalBefore,
    MediaErrorsInLog,
}

public static class CheckEvidenceCodes
{
    /// <summary>The ten codes that say a .delta was not comparable (value null).</summary>
    public static bool IsBaseline(CheckEvidenceCode code) => code <= CheckEvidenceCode.BaselineLegacyRefused;
}
