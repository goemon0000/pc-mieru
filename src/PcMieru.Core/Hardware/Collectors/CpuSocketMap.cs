using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>
/// Which entries of <c>CpuInfo.PerLogicalPercent</c> belong to which CPU package. The per-logical list comes from
/// NtQuerySystemInformation(SystemProcessorPerformanceInformation) (<c>SystemCpuSampler</c>): one element per logical processor read by position,
/// with no processor id on the rows, and nothing in the code guarantees their order on a multi-socket machine (the sampler's own comment says that
/// with several processor groups the call "may describe only the caller's group"). So a socket is confirmed only in the one case that needs no
/// ordering at all: exactly one package, in exactly one processor group, whose active count is the sampler's count. That one socket is the whole
/// machine. Every multi-package or multi-group machine is "unknown" (null, with a reason) rather than a guess. The total is never repeated for
/// each socket: callers show the total only.
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
    /// <paramref name="logicalCount"/> is the length of <c>PerLogicalPercent</c>. On success there is exactly one socket holding every index
    /// 0..logicalCount-1. On failure <paramref name="unknownReason"/> says why the mapping cannot be confirmed.
    /// </summary>
    // TODO: the multi-package and multi-group cases can be mapped once a labelled per-processor source feeds the sampler. The one to use is the PDH
    // counter set "Processor Information", whose instances are named "<group>,<number>" (one per logical processor, plus "_Total" rows), so every
    // row says which group and processor it is. Until then they stay unknown.
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

        if (topology.Packages != 1)
        {
            unknownReason = $"{topology.Packages} processor packages: the per-logical rows carry no processor id, so which package each belongs to is not confirmed";
            return false;
        }

        if (topology.ProcessorGroups != 1 || topology.GroupInfos.Count != 1)
        {
            unknownReason = $"{topology.ProcessorGroups} processor groups: how the per-logical positions run across groups is not confirmed";
            return false;
        }

        var group = topology.GroupInfos[0];
        if (group.ActiveProcessors != logicalCount)
        {
            unknownReason = $"the one group has {group.ActiveProcessors} active processors but the sampler counts {logicalCount}";
            return false;
        }

        // The one package must be exactly that group's active processors: every affinity entry in the group, inside its active set, together
        // holding as many processors as the sampler counted.
        var inPackage = 0;
        foreach (var a in topology.PackageAffinities[0])
        {
            if (a.Group != group.Group || (a.Mask & ~group.ActiveMask) != 0)
            {
                unknownReason = "the package names processors outside the one group's active set";
                return false;
            }

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
}
