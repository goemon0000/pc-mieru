namespace PcMieru.Core;

/// <summary>
/// Which logical CPUs sit in which CPU package (socket), as indexes into <see cref="CpuInfo.PerLogicalPercent"/>. Only built when Core could
/// confirm how the per-logical numbers map to the processor groups of the machine; otherwise <see cref="CpuInfo.Sockets"/> is null and a caller
/// shows the total only. The Parts Life board reads it from another assembly (it keys the slots cpu#0, cpu#1, ...).
/// </summary>
/// <param name="LogicalIndexesBySocket">
/// One list per package, in the order Windows lists the packages (the list position is the socket number). Each list has the indexes of
/// <see cref="CpuInfo.PerLogicalPercent"/> that belong to that package, ascending and never empty. Every logical processor appears in exactly one list.
/// </param>
public sealed record CpuSockets(IReadOnlyList<IReadOnlyList<int>> LogicalIndexesBySocket)
{
    /// <summary>How many sockets (packages) the machine has.</summary>
    public int Count => LogicalIndexesBySocket.Count;
}
