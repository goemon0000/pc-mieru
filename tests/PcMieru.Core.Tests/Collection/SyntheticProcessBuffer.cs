using System.Buffers.Binary;
using System.Text;
using PcMieru.Core.Collection;

namespace PcMieru.Core.Tests.Collection;

/// <summary>Builds a SystemProcessInformation buffer by hand, so the parser is tested without a real machine.</summary>
internal static class SyntheticProcessBuffer
{
    public sealed record Proc(
        uint Pid,
        string Name,
        uint Ppid = 4,
        long Create = 133_000_000_000_000_000,
        long User = 0,
        long Kernel = 0,
        uint Handles = 10,
        uint Session = 1,
        long WorkingSet = 1000,
        long PrivateWorkingSet = 500,
        long PrivatePages = 800,
        ulong Read = 0,
        ulong Write = 0,
        (int State, int Reason)[]? Threads = null,
        int? ClaimedThreads = null);

    public const ulong DefaultBase = 0x0000_01F4_0000_0000;

    public static byte[] Build(ulong baseAddress, params Proc[] procs)
    {
        using var stream = new MemoryStream();
        for (var i = 0; i < procs.Length; i++)
        {
            var p = procs[i];
            var threads = p.Threads ?? [];
            var nameBytes = Encoding.Unicode.GetBytes(p.Name);
            var threadsEnd = SystemProcessParser.EntrySize + (threads.Length * SystemProcessParser.ThreadSize);
            var size = (threadsEnd + nameBytes.Length + 7) & ~7;
            var entry = new byte[size];
            var start = (ulong)stream.Position;

            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(SystemProcessParser.NextEntryOffset), i == procs.Length - 1 ? 0u : (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(SystemProcessParser.NumberOfThreads), (uint)(p.ClaimedThreads ?? threads.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(SystemProcessParser.WorkingSetPrivateSize), (ulong)p.PrivateWorkingSet);
            BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(SystemProcessParser.CreateTime), p.Create);
            BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(SystemProcessParser.UserTime), p.User);
            BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(SystemProcessParser.KernelTime), p.Kernel);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(SystemProcessParser.ImageNameLength), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(SystemProcessParser.ImageNameLength + 2), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(SystemProcessParser.ImageNameBuffer), baseAddress + start + (ulong)threadsEnd);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(SystemProcessParser.BasePriority), 8);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(SystemProcessParser.UniqueProcessId), p.Pid);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(SystemProcessParser.InheritedFromUniqueProcessId), p.Ppid);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(SystemProcessParser.HandleCount), p.Handles);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(SystemProcessParser.SessionId), p.Session);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(SystemProcessParser.WorkingSetSize), (ulong)p.WorkingSet);
            // PagefileUsage is set to a decoy so a parser that reads it for "commit" is caught.
            BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(SystemProcessParser.PagefileUsage), 0xDEAD_0000);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(SystemProcessParser.PrivatePageCount), (ulong)p.PrivatePages);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(SystemProcessParser.ReadTransferCount), p.Read);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(SystemProcessParser.WriteTransferCount), p.Write);

            for (var t = 0; t < threads.Length; t++)
            {
                var thread = entry.AsSpan(SystemProcessParser.EntrySize + (t * SystemProcessParser.ThreadSize));
                BinaryPrimitives.WriteInt32LittleEndian(thread[SystemProcessParser.ThreadState..], threads[t].State);
                BinaryPrimitives.WriteInt32LittleEndian(thread[SystemProcessParser.ThreadWaitReason..], threads[t].Reason);
            }

            nameBytes.CopyTo(entry, threadsEnd);
            stream.Write(entry);
        }

        return stream.ToArray();
    }
}
