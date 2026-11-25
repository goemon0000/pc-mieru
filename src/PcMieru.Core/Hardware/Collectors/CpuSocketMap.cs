using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>
/// Which entries of <c>CpuInfo.PerLogicalPercent</c> belong to which CPU package. The per-logical list comes from
/// NtQuerySystemInformation(SystemProcessorPerformanceInformation) (<c>SystemCpuSampler</c>), one element per logical processor read by position, and
/// the sampler's own comment says that on a machine with several processor groups the call "may describe only the caller's group". So the position is
/// the processor number inside group 0 only when the machine has one group; with more groups nothing in the code says how the positions run across
/// them, and the answer is "unknown" (null) rather than a guess. The total is never repeated for each socket: callers show the total only.
/// Pure over <see cref="CpuTopology"/>; <see cref="ReadMachine"/> is the one call that asks Windows.
/// </summary>
internal static class CpuSocketMap
{
    /// <summary>Reads the machine's topology and maps it onto <paramref name="logicalCount"/> per-logical entries; null when unknown.</summary>
    public static CpuSockets? ReadMachine(int logicalCount)
    {
        try
        {
            var topology = CpuTopology.Read();
            return topology is not null && TryMap(topology, logicalCount, out var sockets, out _) ? sockets : null;
        }
        // Sockets are optional: a topology that cannot be read or parsed means "total only", never a failed collection.
        // The sampler caches this in a Lazy, so an escaping exception would be rethrown on every tick.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public static CpuSockets? Map(CpuTopology topology, int logicalCount) =>
        TryMap(topology, logicalCount, out var sockets, out _) ? sockets : null;

    /// <summary>
    /// <paramref name="logicalCount"/> is the length of <c>PerLogicalPercent</c>. On success every index 0..logicalCount-1 is in exactly one socket.
    /// On failure <paramref name="unknownReason"/> says why the mapping cannot be confirmed.
    /// </summary>
    public static bool TryMap(CpuTopology topology, int logicalCount, [NotNullWhen(true)] out CpuSockets? sockets, out string unknownReason)
    {
        sockets = null;
        if (logicalCount <= 0)
        {
            unknownReason = "no logical processor count";
            return false;
        }

        if (topology.Packages <= 0 || topology.PackageAffinities.Count != topology.Packages)
        {
            unknownReason = "Windows listed no usable processor package record";
            return false;
        }

        if (topology.Packages == 1)
        {
            // One package holds every logical processor, in whatever group they sit, so no group-to-index mapping is needed. The package's own
            // processor count must still agree with the number of per-logical entries.
            var inPackage = 0;
            foreach (var a in topology.PackageAffinities[0])
            {
                inPackage += BitOperations.PopCount(a.Mask);
            }

            if (inPackage != logicalCount)
            {
                unknownReason = $"the package lists {inPackage} logical processors but the sampler counts {logicalCount}";
                return false;
            }

            sockets = new CpuSockets([Enumerable.Range(0, logicalCount).ToArray()]);
            unknownReason = string.Empty;
            return true;
        }

        if (topology.ProcessorGroups != 1 || topology.GroupInfos.Count != 1)
        {
            unknownReason = $"{topology.ProcessorGroups} processor groups: how the per-logical positions run across groups is not confirmed";
            return false;
        }

        // One group: position i is processor number i of group 0, which holds only if the active processors are exactly bits 0..n-1.
        var group = topology.GroupInfos[0];
        if (group.Group != 0 || group.ActiveProcessors != logicalCount || group.ActiveMask != LowBits(logicalCount))
        {
            unknownReason = "the active processors of the one group are not bits 0.." + (logicalCount - 1) + " or do not match the sampler's count";
            return false;
        }

        var owner = new int[logicalCount];
        Array.Fill(owner, -1);
        var lists = new List<int[]>(topology.Packages);
        for (var p = 0; p < topology.Packages; p++)
        {
            var indexes = new List<int>();
            foreach (var a in topology.PackageAffinities[p])
            {
                if (a.Group != 0 || (a.Mask & ~group.ActiveMask) != 0)
                {
                    unknownReason = $"package {p} names processors outside group 0's active set";
                    return false;
                }

                for (var bit = 0; bit < logicalCount; bit++)
                {
                    if ((a.Mask & (1UL << bit)) == 0)
                    {
                        continue;
                    }

                    if (owner[bit] >= 0)
                    {
                        unknownReason = $"logical processor {bit} belongs to more than one package";
                        return false;
                    }

                    owner[bit] = p;
                    indexes.Add(bit);
                }
            }

            if (indexes.Count == 0)
            {
                unknownReason = $"package {p} has no logical processor";
                return false;
            }

            indexes.Sort();
            lists.Add([.. indexes]);
        }

        if (Array.IndexOf(owner, -1) is var missing and >= 0)
        {
            unknownReason = $"logical processor {missing} belongs to no package";
            return false;
        }

        sockets = new CpuSockets(lists);
        unknownReason = string.Empty;
        return true;
    }

    private static ulong LowBits(int count) => count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
}
