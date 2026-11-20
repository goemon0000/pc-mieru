// Provenance: the counter offsets and the ulong saturation rule are those of the owner's pl-sensors NvmeHealth.Parse
// (src/PartsLife.Checks/Storage/NvmeHealth.cs, MIT, same owner); the error-log counting is PcMieru.Wire's CountNvmeErrorLog.

using System.Buffers.Binary;
using PcMieru.Core.Hardware.Model;
using PcMieru.Wire;
using WireProtocol = PcMieru.Wire.StorageProtocol;

namespace PcMieru.Core.Hardware.Storage;

/// <summary>Pure builders of <see cref="StorageInspectionCounters"/> and the identity rules around them. Nothing here touches a device.</summary>
internal static class InspectionParsers
{
    private const int HealthLength = 512;
    private const int ErrorLogEntriesOffset = 176;
    private const int DataUnitsWrittenOffset = 48;

    // The ten 128-bit counters v0.7.4 read into ulong, whose high half decided its Saturated flag. The five it stored are named.
    private static readonly (int Offset, string? Key)[] LegacyCounters =
    [
        (32, null),
        (48, StorageLegacyCounterKeys.DataUnitsWritten),
        (64, null),
        (80, null),
        (96, null),
        (112, null),
        (128, StorageLegacyCounterKeys.PowerOnHours),
        (144, StorageLegacyCounterKeys.UnsafeShutdowns),
        (160, StorageLegacyCounterKeys.MediaErrors),
        (176, StorageLegacyCounterKeys.ErrorLogEntries),
    ];

    private static UInt128 Read128(ReadOnlySpan<byte> page, int offset) =>
        new(BinaryPrimitives.ReadUInt64LittleEndian(page.Slice(offset + 8, 8)), BinaryPrimitives.ReadUInt64LittleEndian(page.Slice(offset, 8)));

    /// <summary>
    /// From the 512-byte health log page and (when it was read) the counts of the error log. Null when the page is short: a short page
    /// says nothing, and zeros made up for it would be a lie.
    /// </summary>
    public static StorageInspectionCounters? FromNvme(ReadOnlySpan<byte> page, StorageErrorLogCounts? errorLog)
    {
        if (page.Length < HealthLength)
        {
            return null;
        }

        var saturated = false;
        var unrepresentable = new List<string>();
        foreach (var (offset, key) in LegacyCounters)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(page.Slice(offset + 8, 8)) == 0)
            {
                continue;
            }

            saturated = true;
            if (key is not null)
            {
                unrepresentable.Add(key);
            }
        }

        return new StorageInspectionCounters(
            StorageHealthKind.Nvme,
            new U128(Read128(page, ErrorLogEntriesOffset)),
            new U128(Read128(page, DataUnitsWrittenOffset)),
            errorLog,
            saturated,
            unrepresentable,
            AtaPredictFailure: null,
            AtaThresholdsRead: false,
            NvmeCompositeKelvin: BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(1, 2)));
    }

    /// <summary>
    /// SATA: only whether the failure-prediction window answered and whether the threshold table was read, and, when it was not and this
    /// process asked for it, why (<paramref name="thresholdsStatus"/>; ignored when the table was read, because then there is nothing to explain).
    /// </summary>
    public static StorageInspectionCounters ForAta(bool? predictFailure, bool thresholdsRead, ValueStatus? thresholdsStatus = null) =>
        new(StorageHealthKind.Sata, null, null, null, false, [], predictFailure, thresholdsRead, AtaThresholdsStatus: thresholdsRead ? null : thresholdsStatus);

    /// <summary>The counters of the bytes in the shape <see cref="IElevatedStorageReader"/> hands out: the NVMe page, or the SMART table with its thresholds after it.</summary>
    public static StorageInspectionCounters? FromBytes(StorageHealthKind kind, byte[]? bytes, StorageErrorLogCounts? errorLog, bool? predictFailure, ValueStatus? thresholdsStatus = null)
    {
        if (bytes is null)
        {
            return null;
        }

        return kind == StorageHealthKind.Nvme
            ? FromNvme(bytes, errorLog)
            : ForAta(predictFailure, bytes.Length >= 2 * StorageHealthParsers.SmartPageLength, thresholdsStatus);
    }

    public static StorageErrorLogCounts? ErrorLogOf(ReadOnlySpan<byte> page) =>
        WireProtocol.CountNvmeErrorLog(page) is { } c ? new StorageErrorLogCounts(c.Entries, c.MediaErrors) : null;

    /// <summary>
    /// The count keys of one drive in a batch answer, both or nothing. A value that is not a number or lies outside what 16 entries can
    /// hold is a damaged answer and counts as not delivered.
    /// </summary>
    public static StorageErrorLogCounts? ErrorLogFrom(IReadOnlyDictionary<string, string> data, int? drive)
    {
        string Key(string name) => drive is { } n ? HelperDataKeys.ForDrive(n, name) : name;

        if (!data.TryGetValue(Key(HelperDataKeys.NvmeErrorLogEntries), out var entriesText)
            || !data.TryGetValue(Key(HelperDataKeys.NvmeErrorLogMedia), out var mediaText)
            || !int.TryParse(entriesText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var entries)
            || !int.TryParse(mediaText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var media)
            || entries > WireProtocol.NvmeErrorEntryCount
            || media > entries)
        {
            return null;
        }

        return new StorageErrorLogCounts(entries, media);
    }

    /// <summary>
    /// Whether the identity seen on the health handle is the one seen before. All six fields must be equal: any difference, and any
    /// missing answer, is "not comparable" and never "the same".
    /// </summary>
    public static StorageIdentityConfirmation Confirm(DiskIdentity before, DiskIdentity? onHealthHandle) =>
        onHealthHandle is null ? StorageIdentityConfirmation.Unconfirmed
        : before == onHealthHandle ? StorageIdentityConfirmation.Confirmed
        : StorageIdentityConfirmation.Mismatch;
}
