// Adapted from goemon0000/parts-life (MIT) src/PartsLife.Checks/Storage/StorageProtocol.cs
// Pure byte builders and readers for the storage IOCTLs. Nothing here touches a device.

using System.Buffers.Binary;
using System.Text;

namespace PcMieru.Core.Hardware.Storage;

internal sealed record DeviceDescriptor(string Vendor, string Product, string Revision, string Serial, int BusType, bool Removable, int DeviceType);

internal static class StorageProtocol
{
    public const int StorageDeviceProperty = 0;
    public const int StorageAdapterProperty = 1;
    public const int StorageAccessAlignmentProperty = 6;
    public const int StorageDeviceSeekPenaltyProperty = 7;
    public const int StorageDeviceTrimProperty = 8;
    public const int StorageDeviceProtocolSpecificProperty = 50;

    public const int ProtocolTypeNvme = 3;
    public const int NvmeDataTypeLogPage = 2;
    public const byte NvmeLogErrorInfo = 0x01;
    public const byte NvmeLogHealthInfo = 0x02;

    private const int QueryHeader = 8;
    public const int ProtocolSpecificDataSize = 40;
    public const int ProtocolDataDescriptorSize = 48;

    public const byte SmartReadData = 0xD0;
    public const byte SmartReadThresholds = 0xD1;

    /// <summary>STORAGE_PROPERTY_QUERY for a plain property (PropertyStandardQuery).</summary>
    public static byte[] BuildPropertyQuery(int propertyId)
    {
        var q = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(0), propertyId);
        return q;
    }

    /// <summary>
    /// A Get Log Page request through StorageDeviceProtocolSpecificProperty. Only a log page number and a length can be
    /// expressed, so the request cannot be turned into a write or a format.
    /// </summary>
    public static byte[] BuildNvmeLogQuery(byte logPage, int length)
    {
        var buf = new byte[QueryHeader + ProtocolSpecificDataSize + length];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s[0..], StorageDeviceProtocolSpecificProperty);
        var p = s[QueryHeader..];
        BinaryPrimitives.WriteInt32LittleEndian(p[0..], ProtocolTypeNvme);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], NvmeDataTypeLogPage);
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], logPage);
        BinaryPrimitives.WriteUInt32LittleEndian(p[16..], ProtocolSpecificDataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(p[20..], (uint)length);
        return buf;
    }

    /// <summary>Pulls the data out of a STORAGE_PROTOCOL_DATA_DESCRIPTOR. Null when the shape does not match.</summary>
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

    /// <summary>STORAGE_DEVICE_DESCRIPTOR: offsets to vendor/product/revision/serial strings, bus type at 28.</summary>
    public static DeviceDescriptor? ParseDeviceDescriptor(ReadOnlySpan<byte> d)
    {
        if (d.Length < 36)
        {
            return null;
        }

        var used = (int)Math.Min((uint)d.Length, BinaryPrimitives.ReadUInt32LittleEndian(d[4..]));
        if (used < 36)
        {
            return null;
        }

        d = d[..used];

        static string Str(ReadOnlySpan<byte> d, int fieldOffset)
        {
            var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[fieldOffset..]);
            if (off <= 0 || off >= d.Length)
            {
                return "";
            }

            var tail = d[off..];
            var end = tail.IndexOf((byte)0);
            return Encoding.ASCII.GetString(end < 0 ? tail : tail[..end]).Trim();
        }

        return new DeviceDescriptor(Str(d, 12), Str(d, 16), Str(d, 20), Str(d, 24), BinaryPrimitives.ReadInt32LittleEndian(d[28..]), d[10] != 0, d[8]);
    }

    /// <summary>NVMe and some SATA bridges pad the serial with dots/underscores. Strip the padding, keep the value.</summary>
    public static string CleanSerial(string serial) => serial.Trim().TrimEnd('.', '_').Trim();

    public static string JoinVendorModel(string vendor, string product) =>
        vendor.Length > 0 && !product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) && !vendor.Equals("ATA", StringComparison.OrdinalIgnoreCase)
            ? vendor + " " + product
            : product;

    /// <summary>DEVICE_SEEK_PENALTY_DESCRIPTOR: IncursSeekPenalty byte at 8.</summary>
    public static bool? ParseSeekPenalty(ReadOnlySpan<byte> d) => d.Length >= 9 ? d[8] != 0 : null;

    /// <summary>DEVICE_TRIM_DESCRIPTOR: TrimEnabled byte at 8.</summary>
    public static bool? ParseTrimEnabled(ReadOnlySpan<byte> d) => d.Length >= 9 ? d[8] != 0 : null;

    /// <summary>STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR: logical sector at 16, physical sector at 20.</summary>
    public static (int Logical, int Physical)? ParseAlignment(ReadOnlySpan<byte> d) =>
        d.Length >= 24 ? (BinaryPrimitives.ReadInt32LittleEndian(d[16..]), BinaryPrimitives.ReadInt32LittleEndian(d[20..])) : null;

    /// <summary>DISK_GEOMETRY_EX: DiskSize (int64) at 24.</summary>
    public static long? ParseGeometrySize(ReadOnlySpan<byte> d) => d.Length >= 32 ? BinaryPrimitives.ReadInt64LittleEndian(d[24..]) : null;

    /// <summary>VOLUME_DISK_EXTENTS: count at 0, DISK_EXTENT (24 bytes) from 8 (DiskNumber, StartingOffset at +8, ExtentLength at +16).</summary>
    public static IReadOnlyList<(int Disk, long Start, long Length)> ParseVolumeExtents(ReadOnlySpan<byte> d)
    {
        var list = new List<(int, long, long)>();
        if (d.Length < 8)
        {
            return list;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(d);
        for (var i = 0; i < Math.Min(count, 64u); i++)
        {
            var at = 8 + (i * 24);
            if (at + 24 > d.Length)
            {
                break;
            }

            list.Add(((int)BinaryPrimitives.ReadUInt32LittleEndian(d[at..]), BinaryPrimitives.ReadInt64LittleEndian(d[(at + 8)..]), BinaryPrimitives.ReadInt64LittleEndian(d[(at + 16)..])));
        }

        return list;
    }
}
