namespace PcMieru.Wire;

/// <summary>One property of an event as TDH describes it; <see cref="FixedSize"/> is -1 when its length depends on the data.</summary>
public readonly record struct NetMeterProperty(string Name, int FixedSize);

/// <summary>
/// Where the two fields the meter needs sit in the payload of one (event id, version): the process id and the byte count,
/// both UInt32. Found by walking the property list TDH reports and adding up the fixed sizes, so a provider that inserts a
/// field before them (event 10 carries start and end times, for one) does not shift the result silently. If a field is
/// missing, is not four bytes, or only follows a property of variable length, there is no layout and the event is reported
/// as undecodable rather than guessed.
/// </summary>
public readonly record struct NetMeterPayloadLayout(int PidOffset, int SizeOffset)
{
    public const int FieldSize = 4;

    /// <summary>The number of bytes of payload a decoder must have to read both fields.</summary>
    public int RequiredLength => Math.Max(PidOffset, SizeOffset) + FieldSize;

    public static bool TryCreate(IReadOnlyList<NetMeterProperty> properties, out NetMeterPayloadLayout layout)
    {
        int pid = -1, size = -1, offset = 0;
        foreach (var property in properties)
        {
            var isPid = string.Equals(property.Name, NetMeterEvents.PidField, StringComparison.Ordinal);
            var isSize = string.Equals(property.Name, NetMeterEvents.SizeField, StringComparison.Ordinal);
            if (isPid || isSize)
            {
                if (property.FixedSize != FieldSize)
                {
                    layout = default;
                    return false;
                }

                if (isPid && pid < 0)
                {
                    pid = offset;
                }
                else if (isSize && size < 0)
                {
                    size = offset;
                }
            }

            if (pid >= 0 && size >= 0)
            {
                break;
            }

            if (property.FixedSize < 0)
            {
                break;
            }

            offset += property.FixedSize;
        }

        layout = pid >= 0 && size >= 0 ? new NetMeterPayloadLayout(pid, size) : default;
        return pid >= 0 && size >= 0;
    }

    /// <summary>Reads both fields; false when the payload is shorter than the layout needs.</summary>
    public bool TryRead(ReadOnlySpan<byte> payload, out uint pid, out uint size)
    {
        if (payload.Length < RequiredLength)
        {
            pid = 0;
            size = 0;
            return false;
        }

        pid = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload[PidOffset..]);
        size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload[SizeOffset..]);
        return true;
    }
}

/// <summary>TDH_IN_TYPE sizes: what <see cref="NetMeterProperty.FixedSize"/> is made of.</summary>
public static class NetMeterTdh
{
    public const uint PropertyStruct = 0x1;
    public const uint PropertyParamLength = 0x2;
    public const uint PropertyParamCount = 0x4;

    private const ushort InTypeBinary = 14;

    /// <summary>The fixed size of a property in bytes, or -1 when it varies (strings, SIDs, arrays, structs, length from another field).</summary>
    public static int FixedSize(uint flags, ushort inType, ushort length, ushort count, int pointerSize)
    {
        if ((flags & (PropertyStruct | PropertyParamLength | PropertyParamCount)) != 0 || count != 1)
        {
            return -1;
        }

        return inType switch
        {
            3 or 4 => 1, // INT8, UINT8
            5 or 6 => 2, // INT16, UINT16
            7 or 8 or 11 or 13 or 20 => 4, // INT32, UINT32, FLOAT, BOOLEAN, HEXINT32
            9 or 10 or 12 or 17 or 21 => 8, // INT64, UINT64, DOUBLE, FILETIME, HEXINT64
            15 or 18 => 16, // GUID, SYSTEMTIME
            16 => pointerSize,
            InTypeBinary => length > 0 ? length : -1,
            _ => -1,
        };
    }
}
