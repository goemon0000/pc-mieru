using System.Globalization;

namespace PcMieru.Gui;

internal enum ProcessColumn
{
    Name,
    Pid,
    Cpu,
    Memory,
    IoRead,
    IoWrite,
    Gpu,
    State,
    Threads,
    Handles,
    WorkingSet,
    Commit,
    Priority,
    Session,
    Started,
}

internal enum ProcessViewMode
{
    Tree,
    List,
}

/// <summary>One row of the process list: a process (with its place in the tree) or a group header (list view).</summary>
internal readonly record struct ProcessRow(ProcessInfo? Process, int Depth, bool HasChildren, bool Expanded, ProcessGroup Group, int GroupCount)
{
    public bool IsGroup => Process is null;
}

/// <summary>
/// The rows behind the virtual process list: filter, sort, tree or grouped list, look up by identity. No UI types, so it
/// is testable. Selection is kept by <see cref="ProcessKey"/>, never by a bare PID or a row index.
/// While <see cref="Frozen"/> is set, a new sample updates the numbers but the rows stay where they are, so the row
/// under the mouse does not run away. An explicit <see cref="SortBy"/> or <see cref="Resort"/> still re-sorts.
/// </summary>
internal sealed class ProcessTableModel
{
    private static readonly ProcessGroup[] GroupOrder = [ProcessGroup.App, ProcessGroup.Background, ProcessGroup.Windows];

    private readonly HashSet<ProcessKey> _collapsed = [];
    private ProcessInfo[] _all = [];
    private ProcessRow[] _rows = [];
    private Dictionary<ProcessKey, int> _previous = [];
    private Func<ProcessInfo, ProcessGroup> _classify = static _ => ProcessGroup.Background;
    private string _filter = string.Empty;
    private ProcessViewMode _mode = ProcessViewMode.List;
    private bool _forceSort;

    public ProcessColumn SortColumn { get; private set; } = ProcessColumn.Cpu;

    public bool Descending { get; private set; } = true;

    public bool Frozen { get; set; }

    public int Count => _rows.Length;

    /// <summary>Processes (not group headers) that pass the filter.</summary>
    public int ShownProcesses { get; private set; }

    public int TotalCount => _all.Length;

    public ProcessViewMode Mode
    {
        get => _mode;
        set
        {
            if (_mode != value)
            {
                _mode = value;
                _forceSort = true;
                Rebuild();
            }
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value?.Trim() ?? string.Empty;
            _forceSort = true;
            Rebuild();
        }
    }

    public ProcessRow this[int index] => _rows[index];

    public void Update(Snapshot snapshot, Func<ProcessInfo, ProcessGroup>? classify = null)
    {
        _all = [.. snapshot.Processes];
        if (classify is not null)
        {
            _classify = classify;
        }

        Rebuild();
    }

    /// <summary>Same column again flips the direction; a new column starts with the natural one (numbers high first).</summary>
    public void SortBy(ProcessColumn column)
    {
        if (column == SortColumn)
        {
            Descending = !Descending;
        }
        else
        {
            SortColumn = column;
            Descending = column is not (ProcessColumn.Name or ProcessColumn.Pid or ProcessColumn.State or ProcessColumn.Session or ProcessColumn.Priority);
        }

        _forceSort = true;
        Rebuild();
    }

    public void SetSort(ProcessColumn column, bool descending)
    {
        SortColumn = column;
        Descending = descending;
        _forceSort = true;
        Rebuild();
    }

    /// <summary>Sorts once even if the order is frozen (F5, clicking a header, deselecting).</summary>
    public void Resort()
    {
        _forceSort = true;
        Rebuild();
    }

    public int IndexOf(ProcessKey key)
    {
        for (var i = 0; i < _rows.Length; i++)
        {
            if (_rows[i].Process is { } p && p.Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    public ProcessInfo? Find(ProcessKey key)
    {
        foreach (var p in _all)
        {
            if (p.Key == key)
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>Opens or closes a node of the tree view. Nodes are open by default.</summary>
    public void ToggleExpanded(ProcessKey key)
    {
        if (!_collapsed.Remove(key))
        {
            _collapsed.Add(key);
        }

        _forceSort = false;
        Rebuild();
    }

    public void ExpandAll()
    {
        _collapsed.Clear();
        Rebuild();
    }

    public void CollapseAll()
    {
        foreach (var p in _all)
        {
            _collapsed.Add(p.Key);
        }

        Rebuild();
    }

    private void Rebuild()
    {
        var previous = new Dictionary<ProcessKey, int>(_rows.Length);
        for (var i = 0; i < _rows.Length; i++)
        {
            if (_rows[i].Process is { } p)
            {
                previous[p.Key] = i;
            }
        }

        _previous = previous;
        var keepOrder = Frozen && !_forceSort && previous.Count > 0;
        _forceSort = false;
        var matches = Matching();
        _rows = _mode == ProcessViewMode.Tree ? BuildTree(matches, keepOrder) : BuildList(matches, keepOrder);
        ShownProcesses = matches.Count;
    }

    private HashSet<ProcessKey> Matching()
    {
        var set = new HashSet<ProcessKey>(_all.Length);
        foreach (var p in _all)
        {
            if (_filter.Length == 0
                || p.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || p.Key.Pid.ToString(CultureInfo.InvariantCulture) == _filter)
            {
                set.Add(p.Key);
            }
        }

        return set;
    }

    private ProcessRow[] BuildList(HashSet<ProcessKey> matches, bool keepOrder)
    {
        var rows = new List<ProcessRow>(matches.Count + 3);
        var comparer = Comparer(keepOrder);
        foreach (var group in GroupOrder)
        {
            var members = _all.Where(p => matches.Contains(p.Key) && _classify(p) == group).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            members.Sort(comparer);
            rows.Add(new ProcessRow(null, 0, false, false, group, members.Count));
            foreach (var p in members)
            {
                rows.Add(new ProcessRow(p, 0, false, false, group, 0));
            }
        }

        return [.. rows];
    }

    private ProcessRow[] BuildTree(HashSet<ProcessKey> matches, bool keepOrder)
    {
        var byKey = new Dictionary<ProcessKey, ProcessInfo>(_all.Length);
        foreach (var p in _all)
        {
            byKey[p.Key] = p;
        }

        var children = new Dictionary<ProcessKey, List<ProcessInfo>>();
        var roots = new List<ProcessInfo>();
        foreach (var p in _all)
        {
            if (p.ParentKey is { } parent && parent != p.Key && byKey.ContainsKey(parent))
            {
                if (!children.TryGetValue(parent, out var list))
                {
                    children[parent] = list = [];
                }

                list.Add(p);
            }
            else
            {
                roots.Add(p);
            }
        }

        // With a filter: a node shows when it matches or when something below it matches (so the path stays visible).
        HashSet<ProcessKey>? visible = null;
        if (_filter.Length > 0)
        {
            visible = [];
            foreach (var key in matches)
            {
                var at = key;
                for (var guard = 0; guard < 512; guard++)
                {
                    visible.Add(at);
                    if (byKey[at].ParentKey is not { } up || up == at || !byKey.ContainsKey(up))
                    {
                        break;
                    }

                    at = up;
                }
            }
        }

        var comparer = Comparer(keepOrder);
        var rows = new List<ProcessRow>(_all.Length);
        var seen = new HashSet<ProcessKey>();

        void Walk(ProcessInfo node, int depth)
        {
            if (!seen.Add(node.Key))
            {
                return;
            }

            var kids = children.TryGetValue(node.Key, out var list)
                ? (visible is null ? list : list.Where(k => visible.Contains(k.Key)).ToList())
                : [];
            var expanded = kids.Count > 0 && (visible is not null || !_collapsed.Contains(node.Key));
            rows.Add(new ProcessRow(node, depth, kids.Count > 0, expanded, ProcessGroup.Background, 0));
            if (!expanded)
            {
                return;
            }

            var ordered = kids.ToList();
            ordered.Sort(comparer);
            foreach (var kid in ordered)
            {
                Walk(kid, depth + 1);
            }
        }

        var top = visible is null ? roots : roots.Where(r => visible.Contains(r.Key)).ToList();
        top.Sort(comparer);
        foreach (var root in top)
        {
            Walk(root, 0);
        }

        return [.. rows];
    }

    private Comparison<ProcessInfo> Comparer(bool keepOrder) => keepOrder
        ? (a, b) =>
        {
            var pa = _previous.TryGetValue(a.Key, out var x) ? x : int.MaxValue;
            var pb = _previous.TryGetValue(b.Key, out var y) ? y : int.MaxValue;
            return pa != pb ? pa.CompareTo(pb) : Compare(a, b);
        }
    : Compare;

    private int Compare(ProcessInfo a, ProcessInfo b)
    {
        // A value that was not collected goes below every real one, whichever way the column runs.
        var aMissing = IsMissing(a);
        var bMissing = IsMissing(b);
        if (aMissing != bMissing)
        {
            return aMissing ? 1 : -1;
        }

        var result = SortColumn switch
        {
            ProcessColumn.Name => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            ProcessColumn.Pid => a.Key.Pid.CompareTo(b.Key.Pid),
            ProcessColumn.Cpu => Number(a.CpuPercent).CompareTo(Number(b.CpuPercent)),
            ProcessColumn.Memory => a.PrivateWorkingSetBytes.CompareTo(b.PrivateWorkingSetBytes),
            ProcessColumn.IoRead => Number(a.IoReadBytesPerSec).CompareTo(Number(b.IoReadBytesPerSec)),
            ProcessColumn.IoWrite => Number(a.IoWriteBytesPerSec).CompareTo(Number(b.IoWriteBytesPerSec)),
            ProcessColumn.Gpu => Number(a.GpuPercent).CompareTo(Number(b.GpuPercent)),
            ProcessColumn.Threads => a.ThreadCount.CompareTo(b.ThreadCount),
            ProcessColumn.Handles => a.HandleCount.CompareTo(b.HandleCount),
            ProcessColumn.WorkingSet => a.WorkingSetBytes.CompareTo(b.WorkingSetBytes),
            ProcessColumn.Commit => a.CommitBytes.CompareTo(b.CommitBytes),
            ProcessColumn.Priority => a.BasePriority.CompareTo(b.BasePriority),
            ProcessColumn.Session => a.SessionId.CompareTo(b.SessionId),
            ProcessColumn.Started => a.Key.CreateTime.CompareTo(b.Key.CreateTime),
            ProcessColumn.State => StateRank(a).CompareTo(StateRank(b)),
            _ => 0,
        };

        if (result != 0)
        {
            return Descending ? -result : result;
        }

        return a.Key.Pid.CompareTo(b.Key.Pid);
    }

    private bool IsMissing(ProcessInfo p) => SortColumn switch
    {
        ProcessColumn.Cpu => !p.CpuPercent.IsOk,
        ProcessColumn.Gpu => !p.GpuPercent.IsOk,
        ProcessColumn.IoRead => !p.IoReadBytesPerSec.IsOk,
        ProcessColumn.IoWrite => !p.IoWriteBytesPerSec.IsOk,
        ProcessColumn.State => !p.State.IsOk,
        _ => false,
    };

    private static double Number(Value<double> value) => value.TryGet(out var v) ? v : 0;

    private static int StateRank(ProcessInfo p) => p.State.TryGet(out var s) ? (int)s : 0;
}
