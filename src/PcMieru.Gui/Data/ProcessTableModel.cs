using System.Globalization;

namespace PcMieru.Gui;

internal enum ProcessColumn
{
    Name,
    Pid,
    Cpu,
    Memory,
    Io,
    Gpu,
    Threads,
    State,
}

/// <summary>
/// The rows behind the virtual process list: filter, sort, look up by identity. No UI types, so it is testable.
/// Selection is kept by <see cref="ProcessKey"/>, never by a bare PID or a row index.
/// </summary>
internal sealed class ProcessTableModel
{
    private ProcessInfo[] _all = [];
    private ProcessInfo[] _rows = [];
    private string _filter = string.Empty;

    public ProcessColumn SortColumn { get; private set; } = ProcessColumn.Cpu;

    public bool Descending { get; private set; } = true;

    public int Count => _rows.Length;

    public int TotalCount => _all.Length;

    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value?.Trim() ?? string.Empty;
            Rebuild();
        }
    }

    public ProcessInfo this[int index] => _rows[index];

    public void Update(Snapshot snapshot)
    {
        _all = [.. snapshot.Processes];
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
            Descending = column is not (ProcessColumn.Name or ProcessColumn.Pid or ProcessColumn.State);
        }

        Rebuild();
    }

    public int IndexOf(ProcessKey key)
    {
        for (var i = 0; i < _rows.Length; i++)
        {
            if (_rows[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    private void Rebuild()
    {
        IEnumerable<ProcessInfo> rows = _all;
        if (_filter.Length > 0)
        {
            rows = rows.Where(p => p.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || p.Key.Pid.ToString(CultureInfo.InvariantCulture) == _filter);
        }

        var list = rows.ToList();
        list.Sort(Compare);
        _rows = [.. list];
    }

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
            ProcessColumn.Io => (Number(a.IoReadBytesPerSec) + Number(a.IoWriteBytesPerSec)).CompareTo(Number(b.IoReadBytesPerSec) + Number(b.IoWriteBytesPerSec)),
            ProcessColumn.Gpu => Number(a.GpuPercent).CompareTo(Number(b.GpuPercent)),
            ProcessColumn.Threads => a.ThreadCount.CompareTo(b.ThreadCount),
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
        ProcessColumn.Io => !p.IoReadBytesPerSec.IsOk && !p.IoWriteBytesPerSec.IsOk,
        ProcessColumn.State => !p.State.IsOk,
        _ => false,
    };

    private static double Number(Value<double> value) => value.TryGet(out var v) ? v : 0;

    private static int StateRank(ProcessInfo p) => p.State.TryGet(out var s) ? (int)s : 0;
}
