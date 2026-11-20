using System.Text.Json.Serialization;
using PcMieru.Wire;

namespace PcMieru.Core.Hardware.Model;

// Input of the storage check (DESIGN-A 5.1). None of these types is part of pcm-parts/1: they are never serialized and the
// existing StorageHealth / DriveHealth records are untouched.

/// <summary>Whether the drive's identity (the fields a baseline key is made of) could be read. Separate from whether its health could.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<StorageRecognition>))]
public enum StorageRecognition
{
    /// <summary>The device descriptor was read, so model, serial, bus and the rest are the drive's own values.</summary>
    Recognized,

    /// <summary>The drive exists but its descriptor could not be read: no identity at all. It cannot be compared or recorded.</summary>
    Unreadable,
}

/// <summary>
/// Whether the health values of a drive are known to belong to the identity the inspection saw before the read. The identity is read
/// again on the very handle the health bytes came through (in this process, or in the Helper) and compared with the earlier one.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<StorageIdentityConfirmation>))]
public enum StorageIdentityConfirmation
{
    /// <summary>No health bytes were taken, so there is nothing to tie to the identity.</summary>
    NoHealth,

    /// <summary>The identity seen on the health handle equals the identity seen before.</summary>
    Confirmed,

    /// <summary>The identity seen on the health handle differs: the bytes may belong to another disk (swapped or renumbered meanwhile).</summary>
    Mismatch,

    /// <summary>Health bytes came back but no identity came with them (an older Helper, a missing key, a failed re-read). Never read as "same disk".</summary>
    Unconfirmed,
}

/// <summary>What the first 16 entries of the NVMe Error Information log (0x01) hold. Counts only: no LBA, no entry, no page.</summary>
/// <param name="EntriesRead">Entries that hold an error (a non-zero error count).</param>
/// <param name="MediaErrors">Of those, the ones whose Status Code Type is 2 (media and data integrity).</param>
public sealed record StorageErrorLogCounts(int EntriesRead, int MediaErrors);

/// <summary>
/// The values the checks side needs that <see cref="StorageHealth"/> does not carry, read from the same bytes. NVMe values are full
/// precision (128 bit); <see cref="Saturated"/> and <see cref="UnrepresentableCounters"/> say where v0.7.4's ulong would have clipped.
/// </summary>
/// <param name="Kind">Which of the two groups below is filled.</param>
/// <param name="ErrorLogEntries">NVMe: the cumulative number of error log entries (health log page 0x02, offset 176).</param>
/// <param name="DataUnitsWritten">NVMe: the raw Data Units Written (health log page 0x02, offset 48); one unit is 512,000 bytes.</param>
/// <param name="RecentErrorLog">NVMe: counts from log page 0x01; null when that page was not read (never a zero in its place).</param>
/// <param name="Saturated">NVMe: v0.7.4's own flag, true when any of the ten 128-bit counters it read does not fit in 64 bits.</param>
/// <param name="UnrepresentableCounters">
/// NVMe: those of the baseline's counters (<see cref="StorageLegacyCounterKeys"/>) whose value does not fit in 64 bits. v0.7.4 stored
/// ulong.MaxValue for them, so no increase may be reported for them.
/// </param>
/// <param name="AtaPredictFailure">
/// SATA: the answer of the drive's failure-prediction window, or null when that window was not read. v0.7.4 reads it only when
/// SMART READ DATA fails, so for an ordinary SMART read this is null, not false.
/// </param>
/// <param name="AtaThresholdsRead">SATA: the SMART READ THRESHOLDS table was read, so the thresholds on the attributes are real (0 is a real threshold only then).</param>
/// <param name="NvmeCompositeKelvin">
/// NVMe: the composite temperature exactly as the health log page holds it (bytes 1 and 2, whole Kelvin); 0 when the drive reports none,
/// null when this is not an NVMe read. The checks judge and show <c>Kelvin - 273</c> (v0.7.4's whole degrees: 343 K is 70, a caution), not
/// <see cref="StorageHealth.TemperatureC"/>, whose <c>Kelvin - 273.15</c> (69.85) is for the screens and would move every verdict one step.
/// </param>
/// <param name="AtaThresholdsStatus">
/// SATA: why the SMART READ THRESHOLDS table is missing, when this process asked for it and it did not come: Denied, Timeout or Error
/// when the IOCTL failed, Unsupported when the drive answered without a table. Null when the table was read (<see cref="AtaThresholdsRead"/>),
/// when this is not a SATA read, and when the reading was taken elsewhere (the elevated Helper's answer does not carry the reason, so
/// "not said", never "unsupported").
/// </param>
public sealed record StorageInspectionCounters(
    StorageHealthKind Kind,
    U128? ErrorLogEntries,
    U128? DataUnitsWritten,
    StorageErrorLogCounts? RecentErrorLog,
    bool Saturated,
    IReadOnlyList<string> UnrepresentableCounters,
    bool? AtaPredictFailure,
    bool AtaThresholdsRead,
    int? NvmeCompositeKelvin = null,
    ValueStatus? AtaThresholdsStatus = null);

/// <summary>The names v0.7.4's baseline gave the NVMe counters it stored (BaselineEntry.Counters keys). They are part of the stored format.</summary>
public static class StorageLegacyCounterKeys
{
    public const string MediaErrors = "nvme.media_errors";
    public const string ErrorLogEntries = "nvme.error_log_entries";
    public const string UnsafeShutdowns = "nvme.unsafe_shutdowns";
    public const string PowerOnHours = "nvme.power_on_hours";
    public const string DataUnitsWritten = "nvme.data_units_written";
}

/// <summary>
/// One physical drive as one inspection saw it. Identity fields are the v0.7.4-compatible extraction (the model has its vendor in
/// front, the serial has no trailing dots), NOT the display strings of <see cref="StorageDevice"/>. They are empty / zero when
/// <see cref="Recognition"/> is <see cref="StorageRecognition.Unreadable"/>.
/// </summary>
/// <param name="SerialNumber">Named like the redactor's key "serialNumber" on purpose: these types are never written to a report, but if one ever is, the key is blanked.</param>
/// <param name="PhysicalDriveNumber">Only a label of this run: never a key, and never used to join this value to another run or to the display cache.</param>
/// <param name="BusType">The raw STORAGE_BUS_TYPE number of the descriptor (the v0.7.4 enum cast).</param>
/// <param name="SizeBytes">0 when the geometry could not be read.</param>
/// <param name="UniqueId">"eui.&lt;hex&gt;" / "naa.&lt;hex&gt;", or empty.</param>
/// <param name="Location">"Port:Path:Target:Lun", or empty.</param>
/// <param name="SampledAt">When this drive was inspected.</param>
/// <param name="Health">The health values with their own status (Ok, Denied, Unsupported, ...). Present even when <see cref="Confirmation"/> says they are not comparable.</param>
/// <param name="Confirmation">Whether <paramref name="Health"/> is tied to this identity.</param>
/// <param name="Counters">Null when no health bytes were read.</param>
public sealed record StorageInspectionDrive(
    int PhysicalDriveNumber,
    StorageRecognition Recognition,
    string Model,
    string SerialNumber,
    string Firmware,
    int BusType,
    long SizeBytes,
    string UniqueId,
    string Location,
    DateTimeOffset SampledAt,
    Value<StorageHealth> Health,
    StorageIdentityConfirmation Confirmation,
    StorageInspectionCounters? Counters)
{
    /// <summary>The drive has an identity and its health values are known to come from that very drive. Otherwise they may be shown but not compared or recorded.</summary>
    public bool HealthComparable =>
        Recognition == StorageRecognition.Recognized && Health.Status == ValueStatus.Ok && Confirmation == StorageIdentityConfirmation.Confirmed;
}

/// <summary>One fresh enumeration of the physical drives.</summary>
/// <param name="UnreadableSlots">
/// How many slots (PhysicalDrive0 to 63) exist but could not be looked at: the open failed with any error other than 2 or 3, or it opened
/// and its device descriptor was missing. A drive number nobody has (open error 2 or 3) is not a slot and is not counted. Each counted
/// slot is also in <see cref="Drives"/> as <see cref="StorageRecognition.Unreadable"/>, with no identity. See <see cref="InventoryComplete"/>
/// for what the number means to the baseline.
/// </param>
public sealed record StorageInspection(
    DateTimeOffset SampledAt,
    int UnreadableSlots,
    IReadOnlyList<StorageInspectionDrive> Drives)
{
    /// <summary>
    /// True when every slot that exists was read (<see cref="UnreadableSlots"/> is 0). False is NOT "do not compare anything" (DESIGN-A 5.2):
    /// an unreadable slot may be a drive that looks like another one (the same model, no serial, no unique id), so a drive whose key leaves
    /// that doubt open is held back (<c>baseline_inventory_incomplete</c>): one with a weak key (no usable serial and no unique id), and one
    /// told apart from a twin only by its location. A drive with a usable serial or a unique id has a key no unreadable slot can collide
    /// with, so it is compared as usual. That is also how v0.7.4 behaved: one dying drive never stopped the other drives' comparisons.
    /// </summary>
    public bool InventoryComplete => UnreadableSlots == 0;
}

/// <summary>
/// What one drive of an elevated batch came back with, besides its health bytes. A null member means "not delivered": an older Helper,
/// a damaged value, or a failed re-read, never "zero" or "the same".
/// </summary>
/// <param name="Bytes">The bytes the health parsers read (see <see cref="IElevatedStorageReader"/>).</param>
/// <param name="Identity">The identity as the Helper read it on the handle the health bytes came through.</param>
/// <param name="ErrorLog">The Helper's counts from log page 0x01.</param>
/// <param name="PredictFailure">The failure-prediction answer, when the Helper used that window.</param>
/// <param name="ThresholdsStatus">
/// SATA read in this process only: why the SMART READ THRESHOLDS table did not come after the SMART data did (null when it came). The
/// elevated Helper's answer does not carry it, so for a Helper read it is null, which means "not said".
/// </param>
public sealed record ElevatedDriveRead(byte[]? Bytes, DiskIdentity? Identity, StorageErrorLogCounts? ErrorLog, bool? PredictFailure, ValueStatus? ThresholdsStatus = null);
