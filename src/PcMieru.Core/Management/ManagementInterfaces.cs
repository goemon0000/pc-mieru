using PcMieru.Wire;

namespace PcMieru.Core;

// New interfaces of owner D. The frozen ones in Contracts/Interfaces.cs stay as they are; everything that does not
// fit them (administrator retries, CPU affinity, the Explorer restart, undo of a startup switch) lives here so the
// front ends can use it without a contract change. All of them are implemented in Core/Management.

/// <summary>
/// The same three process operations as <see cref="IProcessControl"/>, but run through the Helper (one UAC prompt per
/// call), for a process the current user is not allowed to change ("retry as administrator"). The protection rules
/// and the identity check are made again inside the Helper, so elevation never widens what may be touched.
/// </summary>
public interface IElevatedProcessActions
{
    Task<OperationResult> TerminateAsync(ProcessKey key, CancellationToken ct = default);

    Task<OperationResult> SetPriorityAsync(ProcessKey key, SettablePriority priority, CancellationToken ct = default);

    Task<OperationResult> SetEfficiencyModeAsync(ProcessKey key, bool enabled, CancellationToken ct = default);
}

/// <summary>The CPU affinity of a process: the bits it may run on and the bits the system has.</summary>
public sealed record AffinityInfo(ulong ProcessMask, ulong SystemMask);

/// <summary>CPU affinity. Never goes through the Helper: only processes of the current user can be changed.</summary>
public interface IProcessAffinityControl
{
    /// <summary>null when it cannot be read (the process ended, was replaced or may not be opened).</summary>
    AffinityInfo? QueryAffinity(ProcessKey key);

    /// <summary>
    /// Narrows or widens the allowed CPUs. An empty mask, a mask with CPUs the system does not have and a process that
    /// spans processor groups are refused. The protection rules apply as for any other operation.
    /// </summary>
    Task<OperationResult> SetAffinityAsync(ProcessKey key, ulong mask, CancellationToken ct = default);
}

/// <summary>The Explorer restart. Runs as the current user only; it is refused while PC Mieru runs elevated.</summary>
public interface IShellActions
{
    Task<OperationResult> RestartExplorerAsync(CancellationToken ct = default);
}

/// <summary>
/// Undo of <see cref="IStartupManager.SetEnabledAsync"/>: puts the approval value back to what it was before the FIRST
/// change PC Mieru made to that entry (byte for byte when the change was made in-process).
/// </summary>
public interface IStartupUndo
{
    bool HasUndo(string id);

    Task<OperationResult> UndoAsync(string id, CancellationToken ct = default);
}
