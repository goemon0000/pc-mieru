using PcMieru.Core;

namespace PcMieru.Party.Hardware;

/// <summary>One GPU as the board reads it. A missing number is null, never 0 (DESIGN-C 2.3: a zero would still earn experience).</summary>
/// <param name="Id">Core's adapter id (the LUID); stable while the adapter stays, so the board can keep a slot on it.</param>
/// <param name="Percent">Busiest-engine utilization, 0..100.</param>
/// <param name="PciBus">The PCI bus number the kernel gave the adapter; null when it has none or Core could not read it. Tells two cards of one model apart across boots (the LUID cannot).</param>
public sealed record BoardGpu(string Id, string Name, double? Percent, long? VramUsedBytes, long? VramTotalBytes, uint? PciBus = null);

/// <summary>
/// One CPU package (socket) as the board reads it. Key on the board: cpu#i, i = the position in <see cref="BoardFrame.Sockets"/> (Windows' package order).
/// </summary>
/// <param name="Percent">Mean load of this socket's logical CPUs that gave a number (the whole-machine total when the machine has one socket), 0..100; null when none of them did, never 0.</param>
/// <param name="LogicalProcessors">How many logical CPUs sit in this socket.</param>
public sealed record BoardCpuSocket(double? Percent, int LogicalProcessors);

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
/// Per-socket CPU load (<see cref="Sockets"/>) is only sent when Core confirmed which logical CPUs sit in which package (<see cref="CpuInfo.Sockets"/>)
/// and that map covers every logical CPU exactly once; otherwise <see cref="Sockets"/> is null, the caller shows the total only, and no socket
/// earns experience from a total repeated for each of them.
///
/// TODO (DESIGN-C 2.3, not in this unit): disk-type (NVMe, SSD, HDD) split and the fullest local volume.
/// </summary>
/// <param name="LogicalProcessors">How many logical CPUs Core counted; 0 when it did not say.</param>
/// <param name="CpuPercent">Total CPU load, 0..100; null while Core has no baseline or could not read it.</param>
/// <param name="Memory">Null unless the total is positive and the available number is inside it.</param>
/// <param name="Gpus">
/// In board order, at most <see cref="MaxGpus"/>. Null when Core could not read the adapter list; an empty list only means "no GPU" when
/// Core said it read the list. This is the old board's fixed top six; with the slot map (<see cref="GpuSlotMap"/>) the caller feeds it
/// <see cref="AllGpus"/> instead, so a card pushed past the sixth place by a new card is still recognized.
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

    /// <summary>
    /// Every GPU Core listed, in board order, not cut at <see cref="MaxGpus"/>; null exactly when <see cref="Gpus"/> is. The input of
    /// <see cref="GpuSlotMap.Assign"/>: the map decides who gets one of the six slots, so it has to see all of them.
    /// </summary>
    public IReadOnlyList<BoardGpu>? AllGpus { get; init; }

    /// <summary>
    /// Per-socket CPU, in package order (the board keys cpu#0, cpu#1, ...). Null unless Core's package map is known AND covers every logical CPU
    /// exactly once (no index out of range, none twice, none left out, no empty socket). A machine with one package gives one socket whose
    /// percent is the total. Null means "show the total only", never "every socket has the total".
    /// </summary>
    public IReadOnlyList<BoardCpuSocket>? Sockets { get; init; }

    public static BoardFrame Project(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var system = snapshot.System;
        var ordered = OrderedGpus(system);
        var all = ordered?.Select(ToBoardGpu).ToArray();
        return new BoardFrame(
            Math.Max(0, system.Cpu.LogicalProcessors),
            Percent(system.Cpu.TotalPercent),
            MemoryOf(system.Memory),
            all?.Take(MaxGpus).ToArray(),
            ordered is null ? null : MaxPercent(ordered),
            DiskOf(system),
            NetOf(system))
        {
            AllGpus = all,
            Sockets = SocketsOf(system.Cpu),
        };
    }

    private static BoardCpuSocket[]? SocketsOf(CpuInfo cpu)
    {
        var map = cpu.Sockets?.LogicalIndexesBySocket;
        var logical = cpu.LogicalProcessors;
        if (map is null || map.Count == 0 || logical <= 0)
        {
            return null;
        }

        // The map must cover logical CPUs 0..logical-1 exactly once. Anything else (an index out of range, a CPU in two sockets, a CPU in none,
        // an empty socket) means the mapping is not trustworthy, so no socket is sent.
        var seen = new bool[logical];
        var covered = 0;
        foreach (var indexes in map)
        {
            if (indexes is null || indexes.Count == 0)
            {
                return null;
            }

            foreach (var i in indexes)
            {
                if (i < 0 || i >= logical || seen[i])
                {
                    return null;
                }

                seen[i] = true;
                covered++;
            }
        }

        if (covered != logical)
        {
            return null;
        }

        // One package holds every CPU: its load is the total (Core weights the total by elapsed CPU time, and the per-logical list may be shorter).
        if (map.Count == 1)
        {
            return [new BoardCpuSocket(Percent(cpu.TotalPercent), map[0].Count)];
        }

        var perLogical = cpu.PerLogicalPercent;
        if (perLogical.Count != logical)
        {
            return null;
        }

        var sockets = new BoardCpuSocket[map.Count];
        for (var s = 0; s < map.Count; s++)
        {
            double sum = 0;
            var counted = 0;
            foreach (var i in map[s])
            {
                if (Percent(perLogical[i]) is { } p)
                {
                    sum += p;
                    counted++;
                }
            }

            sockets[s] = new BoardCpuSocket(counted == 0 ? null : sum / counted, map[s].Count);
        }

        return sockets;
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
    /// sorts, with the roster's own sort (<see cref="GpuRoster.Order"/>: VRAM descending, PCI bus, name, LUID). The bus is Core's
    /// <see cref="GpuInfo.PciBus"/>; a card whose bus Core could not read sorts after the ones that have one, as in the old roster. The roster's
    /// alias rule is not run again here: it takes "no bus" for "no PCI address", and Core's null also means "could not be asked".
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
            adapters.Add(new GpuAdapterInfo(g.Id, g.Name.Length > 0 ? g.Name : g.Id, vram, g.PciBus, false));
        }

        return GpuRoster.Order(adapters).Select(a => byId[a.LuidKey]).ToList();
    }

    private static BoardGpu ToBoardGpu(GpuInfo g) => new(
        g.Id,
        g.Name,
        Percent(g.UtilizationPercent),
        Bytes(g.DedicatedUsedBytes),
        Bytes(g.DedicatedTotalBytes),
        g.PciBus);

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
