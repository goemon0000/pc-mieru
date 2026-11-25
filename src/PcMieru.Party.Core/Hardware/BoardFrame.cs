using PcMieru.Core;

namespace PcMieru.Party.Hardware;

/// <summary>One GPU as the board reads it. A missing number is null, never 0 (DESIGN-C 2.3: a zero would still earn experience).</summary>
/// <param name="Id">Core's adapter id (the LUID); stable while the adapter stays, so the board can keep a slot on it.</param>
/// <param name="Percent">Busiest-engine utilization, 0..100.</param>
public sealed record BoardGpu(string Id, string Name, double? Percent, long? VramUsedBytes, long? VramTotalBytes);

/// <summary>Memory in use. Only built when the total is positive and the available number is inside it.</summary>
/// <param name="Percent">Used over total, 0..100.</param>
public sealed record BoardMemory(long UsedBytes, long TotalBytes, double Percent);

/// <summary>Whole-machine disk throughput (read + write). <paramref name="Partial"/> when some disks gave no number and were left out of the sum.</summary>
public sealed record BoardDisk(double? BytesPerSec, bool Partial);

/// <summary>Network totals over the adapters that are up. <paramref name="Partial"/> when an adapter that is up gave no number for one direction.</summary>
public sealed record BoardNet(double? SendBytesPerSec, double? ReceiveBytesPerSec, bool Partial);

/// <summary>
/// What the Parts Life board reads from one Core <see cref="Snapshot"/> (DESIGN-C 2.3), as a pure projection: no sampling, no clock, no state.
/// Units stay Core's (percent 0..100, bytes, bytes per second); the old board's ratios and the 200 MiB/s disk scale belong to the step that
/// ports Composition.
///
/// A value that Core could not read is missing here too (null, or a null list), never 0. The rules layer depends on that: the old
/// ExpRate adds experience even at zero load, so a missing value turned into 0 would still pay out.
///
/// TODO (DESIGN-C 2.3, not in this unit): per-socket CPU load and logical-CPU counts (needs Core's processor-group to socket mapping from
/// CpuTopology; until it is confirmed only the total is sent, never the total repeated for each socket), disk-type (NVMe, SSD, HDD) split
/// and the fullest local volume.
/// </summary>
/// <param name="LogicalProcessors">How many logical CPUs Core counted; 0 when it did not say.</param>
/// <param name="CpuPercent">Total CPU load, 0..100; null while Core has no baseline or could not read it.</param>
/// <param name="Memory">Null unless the total is positive and the available number is inside it.</param>
/// <param name="Gpus">
/// In board order, at most <see cref="MaxGpus"/>. Null when Core could not read the adapter list; an empty list only means "no GPU" when
/// Core said it read the list.
/// </param>
/// <param name="GpuMaxPercent">The busiest of all listed GPUs (not only the first six); null when none has a utilization.</param>
public sealed record BoardFrame(
    int LogicalProcessors,
    double? CpuPercent,
    BoardMemory? Memory,
    IReadOnlyList<BoardGpu>? Gpus,
    double? GpuMaxPercent,
    BoardDisk Disk,
    BoardNet Net)
{
    /// <summary>The board never shows more than this many GPUs (the old Parts Life limit).</summary>
    public const int MaxGpus = 6;

    public static BoardFrame Project(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var system = snapshot.System;
        var ordered = OrderedGpus(system);
        return new BoardFrame(
            Math.Max(0, system.Cpu.LogicalProcessors),
            Percent(system.Cpu.TotalPercent),
            MemoryOf(system.Memory),
            ordered is null ? null : ordered.Take(MaxGpus).Select(ToBoardGpu).ToArray(),
            ordered is null ? null : MaxPercent(ordered),
            DiskOf(system),
            NetOf(system));
    }

    private static double? Percent(Value<double> value) =>
        value.TryGet(out var v) && double.IsFinite(v) ? Math.Clamp(v, 0, 100) : null;

    private static BoardMemory? MemoryOf(MemoryInfo memory)
    {
        var total = memory.TotalPhysicalBytes;
        var available = memory.AvailablePhysicalBytes;
        if (total <= 0 || available < 0 || available > total)
        {
            return null;
        }

        var used = total - available;
        return new BoardMemory(used, total, (double)used / total * 100);
    }

    /// <summary>
    /// The GPUs in the old board's order. Core has already left out software adapters, copies of a card and a LUID listed twice, so this only
    /// sorts, with the roster's own sort (VRAM descending, PCI bus, name, LUID). Core's snapshot carries no PCI bus yet, so that step ties
    /// and the order falls through to name and LUID; TODO when Core exposes the bus (DESIGN-C 2.3).
    /// </summary>
    private static List<GpuInfo>? OrderedGpus(SystemInfo system)
    {
        var gpus = system.Gpus;
        if (gpus.Count == 0)
        {
            // An empty list is "no GPU" only when Core says it read the list; otherwise (not collected, failed, never filled) it is unknown.
            return system.GpusState.IsOk ? [] : null;
        }

        var byId = new Dictionary<string, GpuInfo>(gpus.Count, StringComparer.Ordinal);
        var adapters = new List<GpuAdapterInfo>(gpus.Count);
        foreach (var g in gpus)
        {
            if (!byId.TryAdd(g.Id, g))
            {
                continue;
            }

            var vram = g.DedicatedTotalBytes.TryGet(out var bytes) && bytes > 0 ? (ulong)bytes : 0UL;
            adapters.Add(new GpuAdapterInfo(g.Id, g.Name.Length > 0 ? g.Name : g.Id, vram, null, false));
        }

        return GpuRoster.Real(adapters).Select(a => byId[a.LuidKey]).ToList();
    }

    private static BoardGpu ToBoardGpu(GpuInfo g) => new(
        g.Id,
        g.Name,
        Percent(g.UtilizationPercent),
        Bytes(g.DedicatedUsedBytes),
        Bytes(g.DedicatedTotalBytes));

    private static long? Bytes(Value<long> value) => value.TryGet(out var v) && v >= 0 ? v : null;

    private static double? MaxPercent(List<GpuInfo> gpus)
    {
        double? max = null;
        foreach (var g in gpus)
        {
            if (Percent(g.UtilizationPercent) is { } p && (max is null || p > max))
            {
                max = p;
            }
        }

        return max;
    }

    private static BoardDisk DiskOf(SystemInfo system)
    {
        double sum = 0;
        var counted = 0;
        var skipped = 0;
        foreach (var d in system.Disks)
        {
            if (Rate(d.ReadBytesPerSec) is { } read && Rate(d.WriteBytesPerSec) is { } write)
            {
                sum += read + write;
                counted++;
            }
            else
            {
                skipped++;
            }
        }

        return counted == 0 ? new BoardDisk(null, false) : new BoardDisk(sum, skipped > 0);
    }

    private static BoardNet NetOf(SystemInfo system)
    {
        double send = 0;
        double receive = 0;
        var sendCounted = 0;
        var receiveCounted = 0;
        var partial = false;
        foreach (var n in system.Nics)
        {
            if (!n.IsUp)
            {
                continue;
            }

            if (Rate(n.SendBytesPerSec) is { } s)
            {
                send += s;
                sendCounted++;
            }
            else
            {
                partial = true;
            }

            if (Rate(n.ReceiveBytesPerSec) is { } r)
            {
                receive += r;
                receiveCounted++;
            }
            else
            {
                partial = true;
            }
        }

        return new BoardNet(sendCounted == 0 ? null : send, receiveCounted == 0 ? null : receive, partial);
    }

    private static double? Rate(Value<double> value) => value.TryGet(out var v) && double.IsFinite(v) && v >= 0 ? v : null;
}
