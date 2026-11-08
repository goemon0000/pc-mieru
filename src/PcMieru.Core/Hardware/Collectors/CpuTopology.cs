using System.Buffers.Binary;
using System.Numerics;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Hardware.Collectors;

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
                    break;
                case 4: // RelationGroup
                    if (record.Length >= 12)
                    {
                        groups = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
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
        return new CpuTopology(cores, logical, packages, numa, groups, smt, classes, caches);
    }
}
