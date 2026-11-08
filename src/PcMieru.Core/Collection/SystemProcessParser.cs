using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PcMieru.Core.Collection;

/// <summary>
/// One process as NtQuerySystemInformation(SystemProcessInformation) reports it. Raw numbers only, nothing derived.
/// Times are 100 ns units, CreateTime is a FILETIME.
/// </summary>
internal readonly record struct RawProcess(
    uint Pid,
    uint ParentPid,
    long CreateTime,
    long UserTime,
    long KernelTime,
    int ThreadCount,
    int HandleCount,
    int BasePriority,
    uint SessionId,
    long WorkingSet,
    long PrivateWorkingSet,
    long PrivatePageCount,
    ulong ReadTransferCount,
    ulong WriteTransferCount,
    string Name,
    bool AllThreadsSuspended,
    int ThreadsRead)
{
    public ProcessKey Key => new(Pid, CreateTime);

    public long CpuTime => UserTime + KernelTime;
}

internal enum ParseStatus
{
    Ok,

    /// <summary>The buffer ended inside an entry or an offset pointed outside it; the entries before that point are kept.</summary>
    Malformed,
}

/// <summary>Returns the same string instance for the same text, without allocating a string for a lookup.</summary>
internal sealed class NameInterner
{
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public NameInterner() => _lookup = _set.GetAlternateLookup<ReadOnlySpan<char>>();

    public int Count => _set.Count;

    public string Get(ReadOnlySpan<char> text)
    {
        if (_lookup.TryGetValue(text, out var existing))
        {
            return existing;
        }

        var created = text.ToString();
        _set.Add(created);
        return created;
    }

    /// <summary>Image names are few (hundreds); a bound keeps a machine with endless unique names from growing it.</summary>
    public void TrimTo(int max)
    {
        if (_set.Count > max)
        {
            _set.Clear();
        }
    }
}

/// <summary>
/// Parses the SystemProcessInformation buffer by explicit offsets. Layout (x64) from phnt ntexapi.h
/// SYSTEM_PROCESS_INFORMATION / SYSTEM_THREAD_INFORMATION; the public winternl.h declares only a reserved-bytes
/// prefix, so these offsets are an undocumented-but-stable contract, checked against this PC by the harness
/// and fixed by the tests. Every read is bounds-checked: NextEntryOffset, the returned length and the
/// UNICODE_STRING (Length/Buffer) all have to stay inside the buffer. ImageName is length-prefixed, never
/// NUL-terminated, and is a bare file name, not a path.
/// </summary>
internal static class SystemProcessParser
{
    public const int NextEntryOffset = 0x00;
    public const int NumberOfThreads = 0x04;
    public const int WorkingSetPrivateSize = 0x08;
    public const int CreateTime = 0x20;
    public const int UserTime = 0x28;
    public const int KernelTime = 0x30;
    public const int ImageNameLength = 0x38;
    public const int ImageNameBuffer = 0x40;
    public const int BasePriority = 0x48;
    public const int UniqueProcessId = 0x50;
    public const int InheritedFromUniqueProcessId = 0x58;
    public const int HandleCount = 0x60;
    public const int SessionId = 0x64;
    public const int WorkingSetSize = 0x90;
    public const int PagefileUsage = 0xB8;
    public const int PrivatePageCount = 0xC8;
    public const int ReadTransferCount = 0xE8;
    public const int WriteTransferCount = 0xF0;
    public const int OtherTransferCount = 0xF8;

    /// <summary>Size of the fixed part; the thread array follows it.</summary>
    public const int EntrySize = 0x100;

    // SYSTEM_THREAD_INFORMATION, 0x50 bytes each.
    public const int ThreadSize = 0x50;
    public const int ThreadState = 0x44;
    public const int ThreadWaitReason = 0x48;

    public const int StateWaiting = 5;
    public const int WaitReasonSuspended = 5;
    public const int WaitReasonWrSuspended = 12;

    /// <param name="data">The filled part of the buffer.</param>
    /// <param name="baseAddress">The address <paramref name="data"/>[0] had when the kernel wrote it (the UNICODE_STRING pointers point into it).</param>
    public static ParseStatus Parse(ReadOnlySpan<byte> data, ulong baseAddress, NameInterner names, List<RawProcess> into)
    {
        into.Clear();
        var offset = 0;
        while (true)
        {
            if ((uint)offset > (uint)data.Length || data.Length - offset < EntrySize)
            {
                return into.Count == 0 && data.Length == 0 ? ParseStatus.Ok : ParseStatus.Malformed;
            }

            var entry = data[offset..];
            var next = BinaryPrimitives.ReadUInt32LittleEndian(entry[NextEntryOffset..]);
            var entryLength = next != 0 ? (long)next : data.Length - offset;
            if (entryLength < EntrySize || offset + entryLength > data.Length)
            {
                return ParseStatus.Malformed;
            }

            var threads = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[NumberOfThreads..]);
            var pid = (uint)BinaryPrimitives.ReadUInt64LittleEndian(entry[UniqueProcessId..]);

            // Threads that fit inside the entry. A claimed count that does not fit is clipped, not trusted.
            var fit = (int)Math.Min((uint)threads, (uint)((entryLength - EntrySize) / ThreadSize));
            var allSuspended = fit > 0 && fit == threads;
            for (var i = 0; i < fit && allSuspended; i++)
            {
                var thread = entry[(EntrySize + (i * ThreadSize))..];
                var state = BinaryPrimitives.ReadInt32LittleEndian(thread[ThreadState..]);
                var reason = BinaryPrimitives.ReadInt32LittleEndian(thread[ThreadWaitReason..]);
                allSuspended = state == StateWaiting && reason is WaitReasonSuspended or WaitReasonWrSuspended;
            }

            into.Add(new RawProcess(
                pid,
                (uint)BinaryPrimitives.ReadUInt64LittleEndian(entry[InheritedFromUniqueProcessId..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[CreateTime..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[UserTime..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[KernelTime..]),
                threads,
                (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[HandleCount..]),
                BinaryPrimitives.ReadInt32LittleEndian(entry[BasePriority..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[SessionId..]),
                Clamp(BinaryPrimitives.ReadUInt64LittleEndian(entry[WorkingSetSize..])),
                Clamp(BinaryPrimitives.ReadUInt64LittleEndian(entry[WorkingSetPrivateSize..])),
                Clamp(BinaryPrimitives.ReadUInt64LittleEndian(entry[PrivatePageCount..])),
                BinaryPrimitives.ReadUInt64LittleEndian(entry[ReadTransferCount..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entry[WriteTransferCount..]),
                ReadName(data, baseAddress, entry, pid, names),
                allSuspended,
                fit));

            if (next == 0)
            {
                return ParseStatus.Ok;
            }

            offset += (int)next;
        }
    }

    private static long Clamp(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    private static string ReadName(ReadOnlySpan<byte> data, ulong baseAddress, ReadOnlySpan<byte> entry, uint pid, NameInterner names)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(entry[ImageNameLength..]);
        var pointer = BinaryPrimitives.ReadUInt64LittleEndian(entry[ImageNameBuffer..]);
        if (length > 0 && (length & 1) == 0 && pointer >= baseAddress)
        {
            var start = pointer - baseAddress;
            if (start <= (ulong)data.Length && (ulong)data.Length - start >= length)
            {
                return names.Get(MemoryMarshal.Cast<byte, char>(data.Slice((int)start, length)));
            }
        }

        // PID 0 legitimately has no name (the kernel gives it an empty string); Task Manager calls it this.
        return pid == 0 && length == 0 ? "System Idle Process" : "[unknown]";
    }
}
