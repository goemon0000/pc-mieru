// Provenance: the allocation, the page touch and the memory status are those of PartsLife.Checks.Load.MemoryTest (pl-cloud 25e4571,
// src/PartsLife.Checks/Load/MemoryTest.cs), MIT, same owner. NativeMemory.AlignedAlloc throws on failure instead of returning null (the old
// null check could never be true), so the allocation failure is an OutOfMemoryException here.
using System.Runtime.InteropServices;
using PcMieru.Core.Collection;

namespace PcMieru.Checks.Load;

/// <summary>
/// The memory test session of the real machine: 64 MiB blocks from <see cref="NativeMemory.AlignedAlloc"/> (4096-aligned, every page touched),
/// the physical memory from GlobalMemoryStatusEx, below-normal work threads and the monotonic clock. Only the RealMachine tests make one; every
/// other test passes a fake (DESIGN-A §6.2).
/// </summary>
internal sealed class RealMemoryTestSession : IMemoryTestSession
{
    /// <summary>The size of a block of the real session.</summary>
    public const long RealBlockBytes = 64L << 20;

    private const int PageBytes = 4096;

    public long BlockBytes => RealBlockBytes;

    public int LogicalProcessorCount => Environment.ProcessorCount;

    public ILoadThreadStarter Threads { get; } = new RealLoadThreadStarter();

    public ILoadClock Clock { get; } = new RealLoadClock();

    public MemoryStatus? ReadStatus()
    {
        var status = new Win32.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Win32.MemoryStatusEx>() };
        return Win32.GlobalMemoryStatusEx(ref status) && status.TotalPhys > 0 ? new MemoryStatus(status.TotalPhys, status.AvailPhys) : null;
    }

    public unsafe IMemoryBlock Allocate()
    {
        // Throws OutOfMemoryException when there is no memory; it never returns null.
        var block = NativeMemory.AlignedAlloc((nuint)BlockBytes, PageBytes);
        try
        {
            // A zero at the start of every page makes Windows hand over the physical memory now, before the next reading of the free memory.
            var bytes = (byte*)block;
            for (long offset = 0; offset < BlockBytes; offset += PageBytes)
            {
                Volatile.Write(ref bytes[offset], 0);
            }

            // Inside the try, as the last thing in it: the wrapper is an allocation too (and can fail with OutOfMemoryException like any), and
            // until it exists nothing but this method owns the 64 MiB. Once it is returned the wrapper owns them and nothing can throw any more.
            return new NativeBlock((nint)block, checked((int)(BlockBytes / sizeof(ulong))));
        }
        catch
        {
            NativeMemory.AlignedFree(block);
            throw;
        }
    }

    public ulong NextSeed() => (ulong)Random.Shared.NextInt64();

    public void Dispose()
    {
    }

    private sealed unsafe class NativeBlock(nint address, int words) : IMemoryBlock
    {
        private nint _address = address;

        public Span<ulong> Words(int firstWord, int count)
        {
            var at = Volatile.Read(ref _address);
            if (at == 0)
            {
                throw new ObjectDisposedException(nameof(NativeBlock));
            }

            if (firstWord < 0 || count < 0 || (long)firstWord + count > words)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            return new Span<ulong>((ulong*)at + firstWord, count);
        }

        public void Free()
        {
            var at = Interlocked.Exchange(ref _address, 0);
            if (at != 0)
            {
                NativeMemory.AlignedFree((void*)at);
            }
        }
    }
}
