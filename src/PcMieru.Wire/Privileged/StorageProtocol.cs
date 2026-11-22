// Provenance: the byte layouts and the read-only command whitelist below are copied from the owner's own pl-sensors
// project (src/PartsLife.Checks/Storage/StorageProtocol.cs, MIT, same owner) and trimmed to what the Helper needs: no
// parsing of the health pages (that is the storage-health code's job). The identity parsers (descriptor strings, device id,
// SCSI address, geometry) and the error-log counter are ports of the same project's StorageProtocol.cs, DiskReport.cs and
// NvmeHealth.cs: they MUST keep producing what v0.7.4 produced, because the baseline file names are derived from them.
using System.Buffers.Binary;
using System.Text;

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

    /// <summary>STORAGE_PROPERTY_ID StorageDeviceIdProperty: the list of VPD 0x83 identifiers.</summary>
    public const int StorageDeviceIdProperty = 2;

    /// <summary>IOCTL_STORAGE_QUERY_PROPERTY (FILE_ANY_ACCESS).</summary>
    public const uint IoctlStorageQueryProperty = 0x002D1400;

    /// <summary>IOCTL_DISK_GET_DRIVE_GEOMETRY_EX (FILE_ANY_ACCESS): empty input, 256 bytes of output.</summary>
    public const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;

    /// <summary>IOCTL_SCSI_GET_ADDRESS (FILE_ANY_ACCESS): empty input, 8 bytes of output.</summary>
    public const uint IoctlScsiGetAddress = 0x00041018;

    /// <summary>The first 16 entries of the NVMe Error Information log (0x01): 16 x 64 bytes.</summary>
    public const int NvmeErrorLogLength = NvmeErrorEntryLength * NvmeErrorEntryCount;

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

    // -----------------------------------------------------------------------------------------------------------------
    // Disk identity. Ports of the v0.7.4 extraction (NOT the display helpers in Core): the baseline file names are hashes of
    // strings built from these values, so any change in what they return orphans every stored baseline.
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>STORAGE_PROPERTY_QUERY asking for StorageDeviceIdProperty (12 bytes).</summary>
    public static byte[] BuildDeviceIdQuery()
    {
        var q = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(0), StorageDeviceIdProperty);
        BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(4), PropertyStandardQuery);
        return q;
    }

    /// <summary>
    /// The four strings and the bus of a STORAGE_DEVICE_DESCRIPTOR as v0.7.4 read them: ASCII, up to the first NUL, trimmed.
    /// Null when the answer is shorter than the 36 fixed bytes (v0.7.4 threw there). <see cref="BusType"/> is the raw
    /// DWORD cast to a signed int, exactly as the old enum cast did.
    /// </summary>
    public readonly record struct DescriptorFields(string Vendor, string Product, string Revision, string Serial, int BusType);

    /// <summary>Layout: 4 Size, 12 VendorIdOffset, 16 ProductIdOffset, 20 ProductRevisionOffset, 24 SerialNumberOffset, 28 BusType.</summary>
    public static DescriptorFields? ParseDescriptorFields(ReadOnlySpan<byte> d)
    {
        if (d.Length < 36)
        {
            return null;
        }

        var size = (int)Math.Min((uint)d.Length, BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4, 4)));
        var span = d[..Math.Max(36, size)];
        return new DescriptorFields(
            AsciiAt(span, 12),
            AsciiAt(span, 16),
            AsciiAt(span, 20),
            AsciiAt(span, 24),
            unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(28, 4))));
    }

    private static string AsciiAt(ReadOnlySpan<byte> span, int field)
    {
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(field, 4));
        if (offset == 0 || offset >= span.Length)
        {
            return "";
        }

        var rest = span[(int)offset..];
        var end = rest.IndexOf((byte)0);
        var bytes = end < 0 ? rest : rest[..end];
        return Encoding.ASCII.GetString(bytes).Trim();
    }

    /// <summary>
    /// The model v0.7.4 stored: the vendor is put in front unless the product already starts with it (case-insensitive).
    /// ATA drives are NOT excluded, so "ATA" + "Example SSD" gives "ATA Example SSD".
    /// </summary>
    public static string ComposeModel(string vendor, string product) =>
        (vendor.Length > 0 && !product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? vendor + " " : "") + product;

    /// <summary>The serial v0.7.4 stored: trimmed, then trailing dots dropped. Underscores are kept.</summary>
    public static string ComposeSerial(string serial) => serial.Trim().TrimEnd('.');

    /// <summary>
    /// One unique identifier of the device itself out of a STORAGE_DEVICE_ID_DESCRIPTOR: "eui.&lt;hex&gt;" (EUI-64 / NGUID) first, else
    /// "naa.&lt;hex&gt;" (WWN), lower-case. Only binary identifiers of association 0 (the device, not its port or target) count, and an
    /// all-zero or all-FF value is no identifier. Empty when nothing qualifies or the answer is too short.
    /// </summary>
    public static string ParseDeviceUniqueId(ReadOnlySpan<byte> d)
    {
        if (d.Length < 12)
        {
            return "";
        }

        var size = (int)Math.Min((uint)d.Length, BinaryPrimitives.ReadUInt32LittleEndian(d[4..]));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(d[8..]);
        string eui = "", naa = "";
        var at = 12;
        for (uint i = 0; i < Math.Min(count, 64u) && at + 16 <= size; i++)
        {
            var e = d[at..size];
            var codeSet = BinaryPrimitives.ReadInt32LittleEndian(e);
            var type = BinaryPrimitives.ReadInt32LittleEndian(e[4..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(e[8..]);
            int next = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]);
            var assoc = BinaryPrimitives.ReadInt32LittleEndian(e[12..]);
            if (16 + len <= e.Length && codeSet == 1 && assoc == 0)
            {
                var v = e.Slice(16, len);
                var junk = v.IndexOfAnyExcept((byte)0) < 0 || v.IndexOfAnyExcept((byte)0xFF) < 0;
                if (!junk && type == 2 && len is 8 or 12 or 16 && eui.Length == 0)
                {
                    eui = "eui." + Convert.ToHexStringLower(v);
                }
                else if (!junk && type == 3 && len is 8 or 16 && naa.Length == 0)
                {
                    naa = "naa." + Convert.ToHexStringLower(v);
                }
            }

            if (next < 16)
            {
                break;
            }

            at += next;
        }

        return eui.Length > 0 ? eui : naa;
    }

    /// <summary>A SCSI_ADDRESS (Length, Port, Path, Target, Lun) as "Port:Path:Target:Lun"; empty when it does not fit.</summary>
    public static string ParseScsiAddress(ReadOnlySpan<byte> d) =>
        d.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(d) < 8 ? "" : $"{d[4]}:{d[5]}:{d[6]}:{d[7]}";

    /// <summary>The disk size of a DISK_GEOMETRY_EX (the int64 at byte 24); 0 when the answer is shorter than 32 bytes.</summary>
    public static long ParseGeometrySize(ReadOnlySpan<byte> d) =>
        d.Length < 32 ? 0 : BinaryPrimitives.ReadInt64LittleEndian(d[24..]);

    /// <summary>What the first 16 entries of the NVMe Error Information log hold, counted. Nothing else of them is kept.</summary>
    public readonly record struct NvmeErrorLogCounts(int Entries, int MediaErrors);

    /// <summary>
    /// Counts the entries of an Error Information log page: an entry is "used" when its 64-bit error count (byte 0) is not 0, and
    /// it is a media error when its Status Code Type (bits 9-11 of the 16-bit status field at byte 12) is 2. Only counts leave
    /// this method: no LBA, no command id, no page. A page shorter than 16 x 64 bytes gives null (NOT zero), because a short
    /// answer says nothing about the entries it does not have. Bytes beyond the first 16 entries are ignored.
    /// </summary>
    public static NvmeErrorLogCounts? CountNvmeErrorLog(ReadOnlySpan<byte> page)
    {
        if (page.Length < NvmeErrorLogLength)
        {
            return null;
        }

        int entries = 0, media = 0;
        for (var i = 0; i < NvmeErrorEntryCount; i++)
        {
            var e = page.Slice(i * NvmeErrorEntryLength, NvmeErrorEntryLength);
            if (BinaryPrimitives.ReadUInt64LittleEndian(e) == 0)
            {
                continue;
            }

            entries++;
            if (((BinaryPrimitives.ReadUInt16LittleEndian(e[12..]) >> 9) & 0x7) == 2)
            {
                media++;
            }
        }

        return new NvmeErrorLogCounts(entries, media);
    }
}
