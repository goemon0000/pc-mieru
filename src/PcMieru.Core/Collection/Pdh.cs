using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PcMieru.Core.Collection;

/// <summary>PDH entry points. English counter paths only (PdhAddEnglishCounterW), so a Japanese Windows works like any other.</summary>
internal static unsafe partial class Pdh
{
    public const uint FormatDouble = 0x00000200;
    public const uint FormatNoCap100 = 0x00008000;

    public const uint Success = 0;
    public const uint MoreData = 0x800007D2;
    public const uint NoInstance = 0x800007D1;
    public const uint CStatusValidData = 0;
    public const uint CStatusNewData = 1;

    [LibraryImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint PdhOpenQueryW(nint dataSource, nuint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, byte* buffer);

    [LibraryImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint PdhCloseQuery(nint query);
}

/// <summary>One value out of a PDH counter array.</summary>
internal readonly record struct PdhValue(string Name, double Value, uint CStatus)
{
    public bool IsValid => CStatus is Pdh.CStatusValidData or Pdh.CStatusNewData;
}

/// <summary>
/// Reads the byte layout PdhGetFormattedCounterArrayW writes: an array of PDH_FMT_COUNTERVALUE_ITEM_W (24 bytes: name
/// pointer, CStatus, padding, double) followed by the name strings, all inside the one buffer. Every pointer is checked
/// against the buffer, so a bad buffer gives "no name" and never a read outside it.
/// </summary>
internal static class PdhArray
{
    public const int ItemSize = 24;
    private const int StatusOffset = 8;
    private const int ValueOffset = 16;

    public static bool TryGetItem(ReadOnlySpan<byte> buffer, ulong baseAddress, int index, int count, out ReadOnlySpan<char> name, out double value, out uint cstatus)
    {
        name = default;
        value = 0;
        cstatus = 0;
        if (index < 0 || index >= count || (long)count * ItemSize > buffer.Length)
        {
            return false;
        }

        var item = buffer.Slice(index * ItemSize, ItemSize);
        cstatus = BinaryPrimitives.ReadUInt32LittleEndian(item[StatusOffset..]);
        value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(item[ValueOffset..]));
        var pointer = BinaryPrimitives.ReadUInt64LittleEndian(item);
        if (pointer < baseAddress)
        {
            return false;
        }

        var offset = pointer - baseAddress;
        var itemsEnd = (ulong)count * ItemSize;
        if (offset < itemsEnd || offset >= (ulong)buffer.Length || (offset & 1) != 0)
        {
            return false;
        }

        // The name is NUL-terminated; find the terminator without leaving the buffer.
        var chars = MemoryMarshal.Cast<byte, char>(buffer.Slice((int)offset, (buffer.Length - (int)offset) & ~1));
        var end = chars.IndexOf('\0');
        if (end < 0)
        {
            return false;
        }

        name = chars[..end];
        return true;
    }
}

/// <summary>A reusable pinned buffer plus the call that fills it, growing until PDH is satisfied.</summary>
internal sealed unsafe class PdhArrayReader
{
    private const int MaxBytes = 16 << 20;
    private byte[] _buffer = GC.AllocateUninitializedArray<byte>(64 << 10, pinned: true);

    public int Count { get; private set; }

    public uint Status { get; private set; }

    public ReadOnlySpan<byte> Bytes => _buffer;

    public ulong BaseAddress => (ulong)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_buffer));

    /// <summary>True when the counter array was read. On false, <see cref="Status"/> is the PDH error.</summary>
    public bool Read(nint counter)
    {
        Count = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var size = (uint)_buffer.Length;
            uint count;
            uint status;
            fixed (byte* p = _buffer)
            {
                status = Pdh.PdhGetFormattedCounterArrayW(counter, Pdh.FormatDouble | Pdh.FormatNoCap100, ref size, out count, p);
            }

            Status = status;
            if (status == Pdh.Success)
            {
                Count = (int)count;
                return true;
            }

            if (status != Pdh.MoreData)
            {
                return false;
            }

            // Instances come and go between the size answer and the next call, so leave headroom.
            var wanted = (long)size + (size / 4) + 4096;
            if (wanted > MaxBytes)
            {
                return false;
            }

            _buffer = GC.AllocateUninitializedArray<byte>((int)wanted, pinned: true);
        }

        return false;
    }

    public bool TryGet(int index, out ReadOnlySpan<char> name, out double value, out uint cstatus) =>
        PdhArray.TryGetItem(_buffer, BaseAddress, index, Count, out name, out value, out cstatus);
}

/// <summary>A PDH query that stays open between ticks (rates need two collections).</summary>
internal sealed class PdhQuery : IDisposable
{
    private nint _query;

    public PdhQuery()
    {
        var status = Pdh.PdhOpenQueryW(0, 0, out _query);
        if (status != Pdh.Success)
        {
            _query = 0;
            OpenStatus = status;
        }
    }

    public uint OpenStatus { get; }

    public bool IsOpen => _query != 0;

    /// <summary>Adds a counter by its English path. Zero means it is not available here (no such object or counter).</summary>
    public nint Add(string englishPath, out uint status)
    {
        status = Pdh.PdhAddEnglishCounterW(_query, englishPath, 0, out var counter);
        return status is Pdh.Success or Pdh.NoInstance ? counter : 0;
    }

    public uint Collect() => Pdh.PdhCollectQueryData(_query);

    public void Dispose()
    {
        var query = Interlocked.Exchange(ref _query, 0);
        if (query != 0)
        {
            _ = Pdh.PdhCloseQuery(query);
        }
    }
}
