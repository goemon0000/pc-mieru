using System.Buffers.Binary;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Hardware.Storage;

/// <summary>DRIVE_LAYOUT_INFORMATION_EX / PARTITION_INFORMATION_EX readers. Partition GUIDs and disk GUIDs are never copied out.</summary>
internal static class DriveLayout
{
    private const int HeaderSize = 48;
    private const int EntrySize = 144;

    public static string StyleName(int style) => style switch { 0 => "MBR", 1 => "GPT", 2 => "RAW", _ => "unknown" };

    public static (string Style, IReadOnlyList<StoragePartition> Partitions)? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderSize)
        {
            return null;
        }

        var style = BinaryPrimitives.ReadInt32LittleEndian(d);
        var count = BinaryPrimitives.ReadInt32LittleEndian(d[4..]);
        var styleName = StyleName(style);
        var list = new List<StoragePartition>();
        for (var i = 0; i < Math.Min(count, 256); i++)
        {
            var at = HeaderSize + (i * EntrySize);
            if (at + EntrySize > d.Length)
            {
                break;
            }

            var e = d.Slice(at, EntrySize);
            var length = BinaryPrimitives.ReadInt64LittleEndian(e[16..]);
            if (length <= 0)
            {
                continue;
            }

            var entryStyle = BinaryPrimitives.ReadInt32LittleEndian(e);
            var type = entryStyle == 1 ? GptTypeName(new Guid(e.Slice(32, 16))) : MbrTypeName(e[32]);
            list.Add(new StoragePartition(
                (int)BinaryPrimitives.ReadUInt32LittleEndian(e[24..]),
                BinaryPrimitives.ReadInt64LittleEndian(e[8..]),
                length,
                type,
                StyleName(entryStyle)));
        }

        return (styleName, list);
    }

    /// <summary>PARTITION_INFORMATION_EX (FILE_ANY_ACCESS on a volume handle): style at 0, start at 8, length at 16, number at 24.</summary>
    public static (string Style, StoragePartition Partition)? ParseSingle(ReadOnlySpan<byte> e)
    {
        if (e.Length < EntrySize)
        {
            return null;
        }

        var style = BinaryPrimitives.ReadInt32LittleEndian(e);
        var type = style == 1 ? GptTypeName(new Guid(e.Slice(32, 16))) : MbrTypeName(e[32]);
        return (StyleName(style), new StoragePartition(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(e[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(e[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(e[16..]),
            type,
            StyleName(style)));
    }

    public static string GptTypeName(Guid type) => type.ToString() switch
    {
        "c12a7328-f81f-11d2-ba4b-00a0c93ec93b" => "EFI System",
        "e3c9e316-0b5c-4db8-817d-f92df00215ae" => "Microsoft Reserved",
        "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" => "Basic data",
        "de94bba4-06d1-4d40-a16a-bfd50179d6ac" => "Recovery",
        "5808c8aa-7e8f-42e0-85d2-e1e90434cfb3" => "Dynamic disk metadata",
        "af9b60a0-1431-4f62-bc68-3311714a69ad" => "Dynamic disk data",
        "e75caf8f-f680-4cee-afa3-b001e56efc2d" => "Storage Spaces",
        "0fc63daf-8483-4772-8e79-3d69d8477de4" => "Linux filesystem",
        "0657fd6d-a4ab-43c4-84e5-0933c84b4f4f" => "Linux swap",
        "21686148-6449-6e6f-744e-656564454649" => "BIOS boot",
        _ => type.ToString(),
    };

    public static string MbrTypeName(byte type) => type switch
    {
        0x01 or 0x04 or 0x06 or 0x0B or 0x0C or 0x0E => "FAT",
        0x05 or 0x0F => "Extended",
        0x07 => "NTFS/exFAT",
        0x27 => "Recovery",
        0x82 => "Linux swap",
        0x83 => "Linux",
        0xEE => "GPT protective",
        0xEF => "EFI System",
        _ => $"0x{type:X2}",
    };
}
