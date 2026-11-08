// Provenance: the byte layouts and the read-only command whitelist below are copied from the owner's own pl-sensors
// project (src/PartsLife.Checks/Storage/StorageProtocol.cs, MIT, same owner) and trimmed to what the Helper needs:
// no parsing of the pages (that is the storage-health code's job), no identifier / geometry queries.
using System.Buffers.Binary;

namespace PcMieru.Wire;

/// <summary>
/// The bytes handed to and received from the disk IOCTLs. Pure functions only; the Helper's operations build these
/// and nothing else. It can only build read commands: an NVMe Get Log Page, the two SMART read features.
/// </summary>
public static class StorageProtocol
{
    public const int StorageDeviceProperty = 0;
    public const int StorageDeviceProtocolSpecificProperty = 50;
    public const int PropertyStandardQuery = 0;

    public const int ProtocolTypeNvme = 3;
    public const int NvmeDataTypeLogPage = 2;

    public const byte NvmeLogErrorInfo = 0x01;
    public const byte NvmeLogHealthInfo = 0x02;

    public const int NvmeHealthLength = 512;
    public const int NvmeErrorEntryLength = 64;
    public const int NvmeErrorEntryCount = 16;

    private const int QueryHeader = 8;

    /// <summary>sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA) = 10 DWORDs.</summary>
    public const int ProtocolSpecificDataSize = 40;

    /// <summary>sizeof(STORAGE_PROTOCOL_DATA_DESCRIPTOR) = Version + Size + the 40 bytes above.</summary>
    public const int ProtocolDataDescriptorSize = 48;

    /// <summary>STORAGE_BUS_TYPE values the Helper acts on.</summary>
    public const int BusTypeAta = 3;

    public const int BusTypeSata = 11;
    public const int BusTypeNvme = 17;

    /// <summary>Offset of BusType in STORAGE_DEVICE_DESCRIPTOR.</summary>
    public const int DescriptorBusTypeOffset = 28;

    /// <summary>STORAGE_PROPERTY_QUERY asking for StorageDeviceProperty (12 bytes).</summary>
    public static byte[] BuildDeviceQuery()
    {
        var q = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(0), StorageDeviceProperty);
        BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(4), PropertyStandardQuery);
        return q;
    }

    /// <summary>The bus type of a STORAGE_DEVICE_DESCRIPTOR, or -1 when the answer is too short to hold it.</summary>
    public static int ParseBusType(ReadOnlySpan<byte> descriptor) =>
        descriptor.Length >= DescriptorBusTypeOffset + 4
            ? BinaryPrimitives.ReadInt32LittleEndian(descriptor[DescriptorBusTypeOffset..])
            : -1;

    public static string BusName(int busType) => busType switch
    {
        BusTypeAta or BusTypeSata => "ata",
        BusTypeNvme => "nvme",
        0 => "unknown",
        1 => "scsi",
        7 => "usb",
        8 => "raid",
        10 => "sas",
        14 or 15 => "virtual",
        16 => "spaces",
        _ => "bus-" + busType.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// An NVMe Get Log Page through Windows' StorageDeviceProtocolSpecificProperty. Only the page number and the length
    /// are parameters, so nothing but a read can be built. Input and output share one buffer.
    /// </summary>
    public static byte[] BuildNvmeLogQuery(byte logPage, int length)
    {
        if (logPage is not (NvmeLogErrorInfo or NvmeLogHealthInfo) || length is <= 0 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(logPage), "only the error and health log pages are allowed");
        }

        var buf = new byte[QueryHeader + ProtocolSpecificDataSize + length];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s[0..], StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], PropertyStandardQuery);
        var p = s[QueryHeader..];
        BinaryPrimitives.WriteInt32LittleEndian(p[0..], ProtocolTypeNvme);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], NvmeDataTypeLogPage);
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], logPage);
        BinaryPrimitives.WriteUInt32LittleEndian(p[12..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(p[16..], ProtocolSpecificDataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(p[20..], (uint)length);
        return buf;
    }

    /// <summary>The data part of a STORAGE_PROTOCOL_DATA_DESCRIPTOR. A shape that does not fit gives null: never read other bytes as numbers.</summary>
    public static byte[]? ExtractProtocolData(ReadOnlySpan<byte> output, int expectedLength)
    {
        if (output.Length < ProtocolDataDescriptorSize)
        {
            return null;
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(output[0..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(output[4..]);
        if (version != ProtocolDataDescriptorSize || size != ProtocolDataDescriptorSize)
        {
            return null;
        }

        var p = output[8..];
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(p[16..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(p[20..]);
        if (offset < ProtocolSpecificDataSize || length < expectedLength)
        {
            return null;
        }

        var start = 8L + offset;
        return start + expectedLength > output.Length ? null : output.Slice((int)start, expectedLength).ToArray();
    }

    public const byte SmartReadData = 0xD0;
    public const byte SmartReadThresholds = 0xD1;

    /// <summary>sizeof(SENDCMDINPARAMS) - 1.</summary>
    public const int SendCmdInSize = 32;

    /// <summary>sizeof(SENDCMDOUTPARAMS) - 1 + 512; the data starts at byte 16.</summary>
    public const int SendCmdOutSize = 16 + 512;

    /// <summary>SMART READ DATA or READ THRESHOLDS; any other feature (writes, self tests, enable/disable) cannot be built.</summary>
    public static byte[] BuildSmartRead(byte feature, byte driveNumber)
    {
        if (feature is not (SmartReadData or SmartReadThresholds))
        {
            throw new ArgumentOutOfRangeException(nameof(feature), "only SMART READ DATA / READ THRESHOLDS are allowed");
        }

        var b = new byte[SendCmdInSize + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), 512);
        b[4] = feature;
        b[5] = 1;
        b[6] = 1;
        b[7] = 0x4F;
        b[8] = 0xC2;
        b[9] = (byte)(0xA0 | ((driveNumber & 1) << 4));
        b[10] = 0xB0;
        b[12] = driveNumber;
        return b;
    }

    /// <summary>The 512 bytes of a SENDCMDOUTPARAMS, or null when the driver reported an error or the table is all zero (some bridges).</summary>
    public static byte[]? ExtractSmart(ReadOnlySpan<byte> output)
    {
        if (output.Length < SendCmdOutSize)
        {
            return null;
        }

        if (output[4] != 0 || output[5] != 0)
        {
            return null;
        }

        var data = output.Slice(16, 512);
        return data.IndexOfAnyExcept((byte)0) < 0 ? null : data.ToArray();
    }

    /// <summary>STORAGE_PREDICT_FAILURE: PredictFailure (4 bytes) + 512 vendor bytes.</summary>
    public static (bool Predict, byte[]? Data) ExtractPredictFailure(ReadOnlySpan<byte> output)
    {
        if (output.Length < 516)
        {
            return (false, null);
        }

        var predict = BinaryPrimitives.ReadUInt32LittleEndian(output) != 0;
        var data = output.Slice(4, 512);
        return (predict, data.IndexOfAnyExcept((byte)0) < 0 ? null : data.ToArray());
    }
}
