namespace PcMieru.Gui;

/// <summary>
/// Process list, virtual mode: the ListView asks for the rows it is about to paint, so 500 processes cost no more than
/// 30 visible rows. Every sample re-sorts the model and re-finds the selected process by its identity (PID + start time).
/// </summary>
internal sealed class ProcessesPage : PageBase
{
    private static readonly (ProcessColumn Column, string Key, int Width, HorizontalAlignment Align)[] Columns =
    [
        (ProcessColumn.Name, "gui.proc.col.name", 240, HorizontalAlignment.Left),
        (ProcessColumn.Pid, "gui.proc.col.pid", 70, HorizontalAlignment.Right),
        (ProcessColumn.Cpu, "gui.proc.col.cpu", 80, HorizontalAlignment.Right),
        (ProcessColumn.Memory, "gui.proc.col.memory", 100, HorizontalAlignment.Right),
        (ProcessColumn.Io, "gui.proc.col.io", 100, HorizontalAlignment.Right),
        (ProcessColumn.Gpu, "gui.proc.col.gpu", 70, HorizontalAlignment.Right),
        (ProcessColumn.Threads, "gui.proc.col.threads", 80, HorizontalAlignment.Right),
        (ProcessColumn.State, "gui.proc.col.state", 110, HorizontalAlignment.Left),
    ];

    private readonly ProcessTableModel _model = new();
    private readonly BufferedListView _list = new() { VirtualMode = true, Name = "processList" };
    private readonly TextBox _filter = new() { Name = "processFilter", Dock = DockStyle.Fill };
    private readonly Label _filterLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _count = new() { AutoSize = true, Anchor = AnchorStyles.Right };
    private bool _updating;
    private ProcessKey? _selected;

    public ProcessesPage(GuiContext context)
        : base(context, "processes", "gui.nav.processes")
    {
        foreach (var column in Columns)
        {
            _list.Columns.Add(new ColumnHeader { TextAlign = column.Align, Width = Px(column.Width), Tag = column.Column });
        }

        _list.RetrieveVirtualItem += OnRetrieveVirtualItem;
        _list.ColumnClick += (_, e) => SortBy(Columns[e.Column].Column);
        _list.SelectedIndexChanged += OnSelectionChanged;
        _filter.TextChanged += (_, _) =>
        {
            _model.Filter = _filter.Text;
            Reload(_model, keepSelection: true);
        };

        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(Px(8), Px(8), Px(8), Px(8)),
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(_filterLabel, 0, 0);
        bar.Controls.Add(_filter, 1, 0);
        bar.Controls.Add(_count, 2, 0);

        Controls.Add(_list);
        Controls.Add(bar);
        context.Pump.Sampled += OnSampled;
        ApplyLanguage();
    }

    /// <summary>Rows the list currently shows (after the filter). For the smoke run and tests.</summary>
    public int VisibleRowCount => _list.VirtualListSize;

    public override void OnActivated()
    {
        if (Context.Pump.Latest is { } latest)
        {
            OnSampled(latest);
        }
    }

    public override void ApplyLanguage()
    {
        for (var i = 0; i < Columns.Length; i++)
        {
            _list.Columns[i].Text = Strings.Get(Columns[i].Key);
        }

        _filterLabel.Text = Strings.Get("gui.proc.filter");
        _list.AccessibleName = Strings.Get("gui.nav.processes");
        _filter.AccessibleName = Strings.Get("gui.proc.filter");
        UpdateCount();
        _list.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Context.Pump.Sampled -= OnSampled;
        }

        base.Dispose(disposing);
    }

    private void OnSampled(Snapshot snapshot)
    {
        _model.Update(snapshot);
        Reload(_model, keepSelection: true);
    }

    private void SortBy(ProcessColumn column)
    {
        _model.SortBy(column);
        Reload(_model, keepSelection: true);
    }

    private void Reload(ProcessTableModel model, bool keepSelection)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        _updating = true;
        try
        {
            var top = _list.VirtualListSize > 0 && _list.TopItem is { } t ? t.Index : 0;
            if (_list.VirtualListSize != model.Count)
            {
                _list.VirtualListSize = model.Count;
            }

            if (model.Count > 0)
            {
                _list.RedrawItems(0, model.Count - 1, invalidateOnly: true);
                if (top > 0 && top < model.Count && _list.TopItem?.Index != top)
                {
                    _list.TopItem = _list.Items[top];
                }
            }

            if (keepSelection && _selected is { } key)
            {
                var index = model.IndexOf(key);
                var current = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
                if (index != current)
                {
                    _list.SelectedIndices.Clear();
                    if (index >= 0)
                    {
                        _list.SelectedIndices.Add(index);
                    }
                }
            }
        }
        finally
        {
            _updating = false;
        }

        UpdateCount();
    }

    private void UpdateCount()
    {
        _count.Text = Strings.Format("gui.proc.count", new Dictionary<string, string>
        {
            ["shown"] = _model.Count.ToString(System.Globalization.CultureInfo.CurrentCulture),
            ["total"] = _model.TotalCount.ToString(System.Globalization.CultureInfo.CurrentCulture),
        });
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _selected = _list.SelectedIndices.Count > 0 && _list.SelectedIndices[0] < _model.Count
            ? _model[_list.SelectedIndices[0]].Key
            : null;
    }

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _model.Count)
        {
            e.Item = new ListViewItem(string.Empty);
            return;
        }

        var p = _model[e.ItemIndex];
        var item = new ListViewItem(p.Name);
        item.SubItems.Add(p.Key.Pid.ToString(System.Globalization.CultureInfo.CurrentCulture));
        item.SubItems.Add(Fmt.Percent(p.CpuPercent));
        item.SubItems.Add(Fmt.Bytes(p.PrivateWorkingSetBytes));
        item.SubItems.Add(Fmt.Rate(p.IoReadBytesPerSec, p.IoWriteBytesPerSec));
        item.SubItems.Add(Fmt.Percent(p.GpuPercent));
        item.SubItems.Add(p.ThreadCount.ToString(System.Globalization.CultureInfo.CurrentCulture));
        item.SubItems.Add(StateText(p));
        e.Item = item;
    }

    private static string StateText(ProcessInfo p) => p.State.TryGet(out var state)
        ? Strings.Get("gui.proc.state." + state.ToString().ToLowerInvariant())
        : Fmt.Dash;
}
