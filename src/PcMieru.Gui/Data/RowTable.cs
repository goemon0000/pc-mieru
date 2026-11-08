namespace PcMieru.Gui;

/// <summary>
/// The rows behind a simple sortable, filterable list (services, start-up items, sessions). Holds the full set, applies
/// the text filter and the sort, and hands out rows by index to a virtual <see cref="GridView"/>. No UI in here.
/// </summary>
internal sealed class RowTable<T>
{
    private readonly List<T> _all = [];
    private readonly List<T> _shown = [];
    private readonly Func<T, string> _searchText;
    private readonly Func<int, Comparison<T>?> _comparer;
    private string _filter = string.Empty;

    public RowTable(Func<T, string> searchText, Func<int, Comparison<T>?> comparer, int sortColumn = 0)
    {
        _searchText = searchText;
        _comparer = comparer;
        SortColumn = sortColumn;
    }

    public int SortColumn { get; private set; }

    public bool Descending { get; private set; }

    public int Count => _shown.Count;

    public int TotalCount => _all.Count;

    public T this[int index] => _shown[index];

    public IReadOnlyList<T> Shown => _shown;

    public IReadOnlyList<T> All => _all;

    public string Filter
    {
        get => _filter;
        set
        {
            var trimmed = value.Trim();
            if (trimmed != _filter)
            {
                _filter = trimmed;
                Rebuild();
            }
        }
    }

    public void Set(IEnumerable<T> rows)
    {
        _all.Clear();
        _all.AddRange(rows);
        Rebuild();
    }

    /// <summary>Same column again flips the direction; a new column starts ascending.</summary>
    public void SortBy(int column)
    {
        if (column == SortColumn)
        {
            Descending = !Descending;
        }
        else
        {
            SortColumn = column;
            Descending = false;
        }

        Rebuild();
    }

    public void SetSort(int column, bool descending)
    {
        SortColumn = column;
        Descending = descending;
        Rebuild();
    }

    public int IndexOf(Predicate<T> match) => _shown.FindIndex(match);

    private void Rebuild()
    {
        _shown.Clear();
        foreach (var row in _all)
        {
            if (_filter.Length == 0 || _searchText(row).Contains(_filter, StringComparison.CurrentCultureIgnoreCase))
            {
                _shown.Add(row);
            }
        }

        if (_comparer(SortColumn) is { } compare)
        {
            // List.Sort is not stable, so ties fall back to the original order through an index.
            var order = _shown.Select((row, index) => (row, index)).ToList();
            order.Sort((a, b) =>
            {
                var result = compare(a.row, b.row);
                if (Descending)
                {
                    result = -result;
                }

                return result != 0 ? result : a.index.CompareTo(b.index);
            });
            _shown.Clear();
            _shown.AddRange(order.Select(o => o.row));
        }
    }
}
