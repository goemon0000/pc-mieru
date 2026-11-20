// Provenance: the logical-processor walk of PartsLife.Checks.Load.CpuTopology (pl-cloud 25e4571, Load/CpuTopology.cs), moved here as a read-only
// Core piece. The record walk is unchanged; the P/Invoke goes through Core's Kernel32 and the thread pin lives with the CPU check (Checks/Load).
using System.Buffers.Binary;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>
/// One logical processor: where Windows puts it (processor group and the bit in that group's mask) and what kind of core it is on.
/// <see cref="Group"/> and <see cref="Number"/> are what SetThreadGroupAffinity takes. A higher <see cref="EfficiencyClass"/> is the more
/// performant kind of core (P cores over E cores); all equal means a machine with one kind of core.
/// </summary>
internal sealed record LogicalCpu(int Index, ushort Group, byte Number, int Core, byte EfficiencyClass);

/// <summary>
/// The logical processors of the machine in order (group, then number), each with its core and its efficiency class. The CPU check needs this
/// to pin one thread to each logical processor and to compare a processor only with the ones of its own kind: an E core being slower than a
/// P core is normal, and must not read as a fault. <see cref="CpuTopology"/> only counts; this lists.
/// </summary>
internal static class LogicalCpuMap
{
    private const int RelationProcessorCore = 0;
    private const int MinCoreRecordSize = 48; // header 8 + the fixed part to the first GROUP_AFFINITY (32) + that entry (16)

    /// <summary>
    /// GetLogicalProcessorInformationEx(RelationProcessorCore). When Windows does not answer (or answers something unreadable) the list is
    /// <see cref="Fallback"/>: one processor per <see cref="Environment.ProcessorCount"/>, all of one class, so a check can still run.
    /// </summary>
    public static IReadOnlyList<LogicalCpu> Read()
    {
        try
        {
            uint length = 0;
            Kernel32.GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref length);
            if (length == 0)
            {
                return Fallback(Environment.ProcessorCount);
            }

            var buffer = new byte[length];
            if (!Kernel32.GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
            {
                return Fallback(Environment.ProcessorCount);
            }

            var cpus = Parse(buffer.AsSpan(0, (int)Math.Min(length, (uint)buffer.Length)));
            return cpus.Count > 0 ? cpus : Fallback(Environment.ProcessorCount);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return Fallback(Environment.ProcessorCount);
        }
    }

    /// <summary>
    /// Reads the SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX array of RelationProcessorCore records:
    /// 0 Relationship, 4 Size, 8 Flags, 9 EfficiencyClass, 30 GroupCount, 32 GroupMask[] (16 bytes each: Mask 8, Group 2).
    /// A record that runs past the buffer ends the walk; a core that spans two groups gives its bits in both. Pure.
    /// </summary>
    public static List<LogicalCpu> Parse(ReadOnlySpan<byte> data)
    {
        var found = new List<(ushort Group, byte Number, int Core, byte Efficiency)>();
        var core = 0;
        for (var offset = 0; offset + 8 <= data.Length;)
        {
            var relationship = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
            if (size <= 0 || offset + size > data.Length)
            {
                break;
            }

            if (relationship == RelationProcessorCore && size >= MinCoreRecordSize)
            {
                var efficiency = data[offset + 9];
                int groups = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 30)..]);
                for (var g = 0; g < groups; g++)
                {
                    var entry = offset + 32 + (g * 16);
                    if (entry + 16 > offset + size)
                    {
                        break;
                    }

                    var mask = BinaryPrimitives.ReadUInt64LittleEndian(data[entry..]);
                    var group = BinaryPrimitives.ReadUInt16LittleEndian(data[(entry + 8)..]);
                    for (var bit = 0; bit < 64; bit++)
                    {
                        if ((mask & (1UL << bit)) != 0)
                        {
                            found.Add((group, (byte)bit, core, efficiency));
                        }
                    }
                }

                core++;
            }

            offset += size;
        }

        return [.. found
            .OrderBy(static c => c.Group)
            .ThenBy(static c => c.Number)
            .Select(static (c, i) => new LogicalCpu(i, c.Group, c.Number, c.Core, c.Efficiency))];
    }

    /// <summary>One processor per <paramref name="count"/>, 64 to a group, one core each and one class: what is left when Windows gives no map.</summary>
    public static IReadOnlyList<LogicalCpu> Fallback(int count) =>
        [.. Enumerable.Range(0, Math.Max(1, count)).Select(static i => new LogicalCpu(i, (ushort)(i / 64), (byte)(i % 64), i, 0))];
}
