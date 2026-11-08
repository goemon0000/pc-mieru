using System.Text;

namespace PcMieru.Core.Hardware.Smbios;

/// <summary>One SMBIOS structure: the formatted area (header included) and its string set. Offsets are the spec's.</summary>
internal sealed record SmbiosStructure(int Type, int Handle, byte[] Data, IReadOnlyList<string> Strings)
{
    public bool Has(int offset, int size) => offset >= 0 && offset + size <= Data.Length;

    public int? U8(int offset) => Has(offset, 1) ? Data[offset] : null;

    public int? U16(int offset) => Has(offset, 2) ? BitConverter.ToUInt16(Data, offset) : null;

    public long? U32(int offset) => Has(offset, 4) ? BitConverter.ToUInt32(Data, offset) : null;

    public ulong? U64(int offset) => Has(offset, 8) ? BitConverter.ToUInt64(Data, offset) : null;

    /// <summary>The string the byte at <paramref name="offset"/> points to (1-based index, 0 = none). Null when absent or empty.</summary>
    public string? Str(int offset)
    {
        if (U8(offset) is not { } index || index == 0 || index > Strings.Count)
        {
            return null;
        }

        var text = Strings[index - 1].Trim();
        return text.Length == 0 ? null : text;
    }
}

/// <summary>
/// Pure parser for the RawSMBIOSData block returned by GetSystemFirmwareTable('RSMB') and for a bare structure table.
/// It never throws on bad input: a truncated or malformed table just ends the walk.
/// </summary>
internal sealed class SmbiosTable
{
    public const uint RsmbSignature = 0x52534D42; // 'RSMB' as a DWORD, the value codex-r2 pins down.

    private SmbiosTable(int major, int minor, IReadOnlyList<SmbiosStructure> structures)
    {
        Major = major;
        Minor = minor;
        Structures = structures;
    }

    public int Major { get; }

    public int Minor { get; }

    public IReadOnlyList<SmbiosStructure> Structures { get; }

    public string VersionText => $"{Major}.{Minor}";

    public IEnumerable<SmbiosStructure> OfType(int type) => Structures.Where(s => s.Type == type);

    public SmbiosStructure? First(int type) => Structures.FirstOrDefault(s => s.Type == type);

    /// <summary>Parses the RawSMBIOSData layout: Used20CallingMethod, Major, Minor, DmiRevision, Length (DWORD), table.</summary>
    public static SmbiosTable? ParseRaw(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 8)
        {
            return null;
        }

        int major = raw[1];
        int minor = raw[2];
        var length = BitConverter.ToUInt32(raw[4..8]);
        var available = raw.Length - 8;
        var take = (int)Math.Min(length, (uint)available);
        return ParseStructures(raw.Slice(8, take), major, minor);
    }

    public static SmbiosTable ParseStructures(ReadOnlySpan<byte> table, int major, int minor)
    {
        var list = new List<SmbiosStructure>();
        var pos = 0;
        while (pos + 4 <= table.Length)
        {
            int type = table[pos];
            int length = table[pos + 1];
            if (length < 4 || pos + length > table.Length)
            {
                break;
            }

            var handle = BitConverter.ToUInt16(table.Slice(pos + 2, 2));
            var formatted = table.Slice(pos, length).ToArray();
            pos += length;

            // String set: NUL-terminated strings, ended by an extra NUL. No strings = two NULs.
            var strings = new List<string>();
            var start = pos;
            while (pos < table.Length)
            {
                if (table[pos] == 0)
                {
                    if (pos == start)
                    {
                        // No strings at all: the set is just two NULs.
                        pos = Math.Min(pos + 2, table.Length);
                        break;
                    }

                    strings.Add(Encoding.Latin1.GetString(table.Slice(start, pos - start)));
                    pos++;
                    start = pos;
                    if (pos < table.Length && table[pos] == 0)
                    {
                        pos++;
                        break;
                    }

                    continue;
                }

                pos++;
            }

            list.Add(new SmbiosStructure(type, handle, formatted, strings));
            if (type == 127)
            {
                break;
            }
        }

        return new SmbiosTable(major, minor, list);
    }
}
