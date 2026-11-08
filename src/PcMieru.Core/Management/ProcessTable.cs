using System.Runtime.InteropServices;

namespace PcMieru.Core.Management;

/// <summary>One row of the process list: who it is, who started it, and its image name.</summary>
internal readonly record struct ProcessRow(uint Pid, uint ParentPid, string Name);

/// <summary>What the tree and Explorer logic needs to know about running processes, so tests can supply their own.</summary>
internal interface IProcessTable
{
    IReadOnlyList<ProcessRow> Rows();

    /// <summary>The creation time (FILETIME), or null when the process is gone or cannot be opened even for queries.</summary>
    long? CreateTime(uint pid);

    /// <summary>True while that exact process (PID and creation time) is still running.</summary>
    bool IsAlive(ProcessKey key);
}

/// <summary>Toolhelp32 for the list, <c>PROCESS_QUERY_LIMITED_INFORMATION</c> for the creation time. Read-only.</summary>
internal sealed unsafe class ToolhelpProcessTable : IProcessTable
{
    public IReadOnlyList<ProcessRow> Rows()
    {
        var rows = new List<ProcessRow>(512);
        using var snapshot = ManagementNative.CreateToolhelp32Snapshot(ManagementNative.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
        {
            return rows;
        }

        var entry = new MgmtProcessEntry { Size = (uint)sizeof(MgmtProcessEntry) };
        if (!ManagementNative.Process32First(snapshot, ref entry))
        {
            return rows;
        }

        do
        {
            rows.Add(new ProcessRow(entry.ProcessId, entry.ParentProcessId, new string(entry.ExeFile)));
            entry.Size = (uint)sizeof(MgmtProcessEntry);
        }
        while (ManagementNative.Process32Next(snapshot, ref entry));

        return rows;
    }

    public long? CreateTime(uint pid)
    {
        if (pid == 0)
        {
            return null;
        }

        using var handle = ManagementNative.OpenProcess(ManagementNative.PROCESS_QUERY_LIMITED_INFORMATION | ManagementNative.SYNCHRONIZE, false, pid);
        if (handle.IsInvalid || !ManagementNative.GetProcessTimes(handle, out var created, out _, out _, out _))
        {
            return null;
        }

        return ManagementNative.WaitForSingleObject(handle, 0) == ManagementNative.WAIT_OBJECT_0 ? null : created;
    }

    public bool IsAlive(ProcessKey key) => CreateTime(key.Pid) == key.CreateTime;
}

/// <summary>
/// "Who are the children of this process" at one moment. A row only counts as a child when its parent PID matches AND
/// it was created after the parent: a PID that was reused leaves old children pointing at a stranger, and those must
/// never be taken as descendants. A process that cannot be opened is not a child here (fewer targets, never more).
/// </summary>
internal sealed class ProcessTreeIndex
{
    private readonly IProcessTable _table;
    private readonly Dictionary<uint, List<uint>> _childrenOf = [];

    public ProcessTreeIndex(IProcessTable table)
    {
        _table = table;
        foreach (var row in table.Rows())
        {
            if (row.Pid == 0 || row.Pid == row.ParentPid)
            {
                continue;
            }

            if (!_childrenOf.TryGetValue(row.ParentPid, out var list))
            {
                _childrenOf[row.ParentPid] = list = [];
            }

            list.Add(row.Pid);
        }
    }

    public IEnumerable<ProcessKey> ChildrenOf(ProcessKey parent)
    {
        if (!_childrenOf.TryGetValue(parent.Pid, out var children))
        {
            yield break;
        }

        foreach (var pid in children)
        {
            if (_table.CreateTime(pid) is { } created && created > parent.CreateTime)
            {
                yield return new ProcessKey(pid, created);
            }
        }
    }
}
