using System.Buffers.Binary;
using System.Numerics;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>One GROUP_AFFINITY: a processor group and the mask of logical processors in it (bit n = processor number n of that group).</summary>
internal readonly record struct GroupAffinity(int Group, ulong Mask);

/// <summary>One PROCESSOR_GROUP_INFO of the RelationGroup record: how many processors the group has active and which bits they are.</summary>
internal readonly record struct ProcessorGroupInfo(int Group, int ActiveProcessors, ulong ActiveMask);

internal sealed record CpuTopology(
    int PhysicalCores,
    int LogicalProcessors,
    int Packages,
    int NumaNodes,
    int ProcessorGroups,
    int SmtCores,
    IReadOnlyDictionary<int, int> CoresByEfficiencyClass,
    IReadOnlyList<CacheInfo> Caches)
{
    /// <summary>
    /// The GROUP_AFFINITY list of each RelationProcessorPackage record, in the order Windows listed the packages (so the index is the package
    /// number the Parts Life board keys as cpu#i). Always one entry per counted package; a record Windows cut short gives an empty list.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<GroupAffinity>> PackageAffinities { get; init; } = [];

    /// <summary>The per-group entries of the RelationGroup record (group number, active processor count, active mask); empty when Windows gave none.</summary>
    public IReadOnlyList<ProcessorGroupInfo> GroupInfos { get; init; } = [];

    public int ThreadsPerCore => PhysicalCores == 0 ? 0 : (int)Math.Round((double)LogicalProcessors / PhysicalCores);

    public bool IsHybrid => CoresByEfficiencyClass.Count > 1;

    /// <summary>Windows: a higher EfficiencyClass is the more performant one. Only meaningful when <see cref="IsHybrid"/>.</summary>
    public int PerformanceCores => IsHybrid ? CoresByEfficiencyClass[CoresByEfficiencyClass.Keys.Max()] : 0;

    public int EfficiencyCores => IsHybrid ? PhysicalCores - PerformanceCores : 0;

    public long TotalCacheBytes(int level) => Caches.Where(c => c.Level == level).Sum(c => c.SizeBytes * c.Instances);

    /// <summary>Reads GetLogicalProcessorInformationEx(RelationAll).</summary>
    public static CpuTopology? Read()
    {
        uint length = 0;
        Kernel32.GetLogicalProcessorInformationEx(0xFFFF, null, ref length);
        if (length == 0)
        {
            return null;
        }

        var buffer = new byte[length];
        return Kernel32.GetLogicalProcessorInformationEx(0xFFFF, buffer, ref length) ? Parse(buffer.AsSpan(0, (int)length)) : null;
    }

    /// <summary>Pure parser over the SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX array.</summary>
    public static CpuTopology Parse(ReadOnlySpan<byte> data)
    {
        var cores = 0;
        var logical = 0;
        var packages = 0;
        var numa = 0;
        var groups = 0;
        var smt = 0;
        var classes = new SortedDictionary<int, int>();
        var cacheRecords = new Dictionary<(int Level, int Type, long Size, int Assoc, int Line), int>();
        var packageAffinities = new List<IReadOnlyList<GroupAffinity>>();
        var groupInfos = new List<ProcessorGroupInfo>();

        var pos = 0;
        while (pos + 8 <= data.Length)
        {
            var relationship = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(data[(pos + 4)..]);
            if (size < 8 || pos + size > data.Length)
            {
                break;
            }

            var record = data.Slice(pos, size);
            switch (relationship)
            {
                case 0: // RelationProcessorCore
                    if (record.Length >= 32)
                    {
                        cores++;
                        var flags = record[8];
                        var efficiency = record[9];
                        if ((flags & 1) != 0)
                        {
                            smt++;
                        }

                        classes[efficiency] = classes.GetValueOrDefault(efficiency) + 1;
                        int groupCount = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]);
                        for (var g = 0; g < groupCount && 32 + (g * 16) + 8 <= record.Length; g++)
                        {
                            logical += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(record[(32 + (g * 16))..]));
                        }
                    }

                    break;
                case 1: // RelationNumaNode
                    numa++;
                    break;
                case 2: // RelationCache
                    if (record.Length >= 40)
                    {
                        var level = record[8];
                        var assoc = record[9];
                        int line = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
                        long cacheSize = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
                        var type = BinaryPrimitives.ReadInt32LittleEndian(record[16..]);
                        var key = (Level: (int)level, Type: type, Size: cacheSize, Assoc: (int)assoc, Line: line);
                        cacheRecords[key] = cacheRecords.GetValueOrDefault(key) + 1;
                    }

                    break;
                case 3: // RelationProcessorPackage
                    packages++;
                    packageAffinities.Add(ReadAffinities(record));
                    break;
                case 4: // RelationGroup
                    if (record.Length >= 12)
                    {
                        groups = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
                        groupInfos.AddRange(ReadGroupInfos(record, groups));
                    }

                    break;
            }

            pos += size;
        }

        var caches = cacheRecords
            .OrderBy(kv => kv.Key.Level)
            .ThenBy(kv => kv.Key.Type)
            .Select(kv => new CacheInfo(
                kv.Key.Level,
                kv.Key.Type switch { 0 => "unified", 1 => "instruction", 2 => "data", 3 => "trace", _ => "other" },
                kv.Key.Size,
                kv.Key.Assoc,
                kv.Key.Line,
                kv.Value))
            .ToArray();
        return new CpuTopology(cores, logical, packages, numa, groups, smt, classes, caches)
        {
            PackageAffinities = packageAffinities,
            GroupInfos = groupInfos,
        };
    }

    /// <summary>
    /// The GROUP_AFFINITY array of a PROCESSOR_RELATIONSHIP record (the layout RelationProcessorCore and RelationProcessorPackage share):
    /// 30 GroupCount, 32 GroupMask[] (16 bytes each: Mask 8, Group 2). An entry that runs past the record ends the list.
    /// </summary>
    private static List<GroupAffinity> ReadAffinities(ReadOnlySpan<byte> record)
    {
        var list = new List<GroupAffinity>();
        if (record.Length < 32)
        {
            return list;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]);
        for (var g = 0; g < count && 32 + (g * 16) + 10 <= record.Length; g++)
        {
            var at = 32 + (g * 16);
            list.Add(new GroupAffinity(
                BinaryPrimitives.ReadUInt16LittleEndian(record[(at + 8)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(record[at..])));
        }

        return list;
    }

    /// <summary>
    /// The GroupInfo array of a GROUP_RELATIONSHIP record: 8 MaximumGroupCount, 10 ActiveGroupCount, 32 GroupInfo[] (48 bytes each:
    /// MaximumProcessorCount 1, ActiveProcessorCount 1, ActiveProcessorMask at 40). The group number is the position in the array.
    /// </summary>
    private static IEnumerable<ProcessorGroupInfo> ReadGroupInfos(ReadOnlySpan<byte> record, int activeGroups)
    {
        var list = new List<ProcessorGroupInfo>();
        for (var g = 0; g < activeGroups && 32 + (g * 48) + 48 <= record.Length; g++)
        {
            var at = 32 + (g * 48);
            list.Add(new ProcessorGroupInfo(g, record[at + 1], BinaryPrimitives.ReadUInt64LittleEndian(record[(at + 40)..])));
        }

        return list;
    }
}
