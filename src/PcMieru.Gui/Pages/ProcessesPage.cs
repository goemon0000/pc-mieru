using System.Globalization;

namespace PcMieru.Gui;

/// <summary>
/// Process list. Virtual and owner-drawn, so 500 processes cost no more than the rows on screen. Every sample updates
/// the model, finds the selected process again by its identity (PID + start time, never a row number) and repaints.
/// While a row is selected or the menu is open the order is held, so the row under the pointer never runs away.
/// </summary>
internal sealed partial class ProcessesPage : PageBase
{
    private sealed record ColumnSpec(ProcessColumn Column, string Key, int Width, HorizontalAlignment Align, bool Default);

    private static readonly ColumnSpec[] Specs =
    [
        // What a first-time reader looks at: name, how busy, how big, state. PID and the fine I/O split are one click away
        // in the column chooser. (No per-process network column: Core does not collect one.)
        new(ProcessColumn.Name, "gui.proc.col.name", 220, HorizontalAlignment.Left, true),
        new(ProcessColumn.Pid, "gui.proc.col.pid", 64, HorizontalAlignment.Right, false),
        new(ProcessColumn.Cpu, "gui.proc.col.cpu", 72, HorizontalAlignment.Right, true),
        new(ProcessColumn.Memory, "gui.proc.col.memory", 92, HorizontalAlignment.Right, true),
        new(ProcessColumn.Disk, "gui.proc.col.disk", 100, HorizontalAlignment.Right, true),
        new(ProcessColumn.IoRead, "gui.proc.col.ioread", 100, HorizontalAlignment.Right, false),
        new(ProcessColumn.IoWrite, "gui.proc.col.iowrite", 100, HorizontalAlignment.Right, false),
        new(ProcessColumn.Gpu, "gui.proc.col.gpu", 64, HorizontalAlignment.Right, true),
        new(ProcessColumn.State, "gui.proc.col.state", 84, HorizontalAlignment.Left, true),
        new(ProcessColumn.Threads, "gui.proc.col.threads", 96, HorizontalAlignment.Right, false),
        new(ProcessColumn.Handles, "gui.proc.col.handles", 96, HorizontalAlignment.Right, false),
        new(ProcessColumn.WorkingSet, "gui.proc.col.workingset", 116, HorizontalAlignment.Right, false),
        new(ProcessColumn.Commit, "gui.proc.col.commit", 110, HorizontalAlignment.Right, false),
        new(ProcessColumn.Priority, "gui.proc.col.priority", 110, HorizontalAlignment.Left, false),
        new(ProcessColumn.Session, "gui.proc.col.session", 110, HorizontalAlignment.Right, false),
        new(ProcessColumn.Started, "gui.proc.col.started", 170, HorizontalAlignment.Left, false),
    ];

    private sealed record ViewOption(ProcessViewMode Mode, string Key, string Saved);

    /// <summary>The View box, in order. Grouped is the default; "no groups" is how to find the heaviest process overall.</summary>
    private static readonly ViewOption[] ViewOptions =
    [
        new(ProcessViewMode.List, "gui.proc.view.list", "list"),
        new(ProcessViewMode.Flat, "gui.proc.view.flat", "flat"),
        new(ProcessViewMode.Tree, "gui.proc.view.tree", "tree"),
    ];

    private static readonly ProcessColumn[] LegacyDefault =
    [
        ProcessColumn.Name, ProcessColumn.Pid, ProcessColumn.Cpu, ProcessColumn.Memory,
        ProcessColumn.IoRead, ProcessColumn.IoWrite, ProcessColumn.Gpu, ProcessColumn.State,
    ];

    /// <summary>CPU at or above this share is shown in the accent colour (always together with the number).</summary>
    private const double HighCpuPercent = 50;

    private const long HighMemoryBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// The load cue bars are drawn against the heaviest row on screen, but never against less than these: on a quiet PC a
    /// process at 2 % must not fill the cell as a full bar would.
    /// </summary>
    private const double BarCpuFloorPercent = 20;

    private const double BarMemoryFloorBytes = 1024.0 * 1024 * 1024;

    private double _barCpuScale = BarCpuFloorPercent;
    private double _barMemoryScale = BarMemoryFloorBytes;

    /// <summary>The memory columns in megabytes only (a column that can be compared by eye), instead of MB and GB as each size fits.</summary>
    private bool _memoryInMb;

    private readonly ProcessTableModel _model = new();
    private readonly ProcessClassifier _classifier;
    private readonly DetailsCache _details;
    private readonly ProcessIconCache _icons;
    private readonly List<ColumnSpec> _visible = [];
    private readonly Dictionary<ProcessColumn, int> _widths = [];
    private readonly HashSet<ProcessColumn> _shown = [];

    private readonly GridView _list = new() { Name = "processList" };
    private readonly ProcessDetailPane _pane = new();
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel2, Name = "processSplit" };
    private readonly TextBox _filter = new() { Name = "processFilter", Anchor = AnchorStyles.Left };
    private readonly Label _filterLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Name = "processFilterLabel" };
    private readonly Label _viewLabel = new() { AutoSize = true, Anchor = AnchorStyles.Right, Name = "processViewLabel" };
    private readonly ComboBox _view = new() { Name = "processView", DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Right };
    private readonly Button _columns = new() { Name = "processColumns", AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly Button _run = new() { Name = "processRun", AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly Button _detailToggle = new() { Name = "processDetailToggle", AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly Label _count = new() { AutoSize = true, Name = "processCount", Margin = new Padding(0, 0, 16, 0), Anchor = AnchorStyles.Left };
    private readonly ToneLabel _hint = new() { AutoSize = true, Name = "processHint", Anchor = AnchorStyles.Left };
    private readonly ContextMenuStrip _menu = new() { Name = "processMenu" };
    private readonly ContextMenuStrip _columnMenu = new() { Name = "processColumnMenu" };
    private readonly ToolTip _tips = new() { ShowAlways = true };

    private ProcessKey? _selectedKey;
    private string _selectedName = string.Empty;
    private ProtectionVerdict? _verdict;
    private bool _updating;
    private bool _menuOpen;
    private bool _resortNext;
    private bool _goneShown;
    private bool _splitReady;
    private bool _detailShown = true;

    /// <summary>False when the icon source is the sample-data stand-in: no icons will ever come, so no empty slot is kept in front of the names.</summary>
    private readonly bool _iconSlots;

    public ProcessesPage(GuiContext context)
        : base(context, "processes", "gui.nav.processes")
    {
        var groups = context.Services.ProcessGroups;
        _classifier = context.Services.Snapshots is IFakeImplementation
            ? ProcessClassifier.ForSampleData(groups)
            : new ProcessClassifier(WindowProbe.VisibleWindowPids, groups);
        _details = new DetailsCache(context.Services.ProcessDetails, this);
        _icons = new ProcessIconCache(context.Services.ProcessIcons, this);
        _iconSlots = context.Services.ProcessIcons is not IFakeImplementation;
        _details.Loaded += key =>
        {
            if (key == _selectedKey)
            {
                ShowSelected();
            }

            // The path is what the icon is looked up by, so a row whose details just arrived may now ask for its icon.
            QueueRepaint();
        };
        _icons.Loaded += () =>
        {
            QueueRepaint();
            ShowPaneIcon();
        };

        LoadColumnChoices();
        _detailShown = context.Settings.GetBool("proc.detail", true);
        _memoryInMb = context.Settings.GetBool("proc.memmb", false);
        _model.Mode = (ViewOptions.FirstOrDefault(o => o.Saved == context.Settings.GetValue("proc.view")) ?? ViewOptions[0]).Mode;
        if (Enum.TryParse<ProcessColumn>(context.Settings.GetValue("proc.sort"), out var sort) && Enum.IsDefined(sort))
        {
            _model.SetSort(sort, context.Settings.GetBool("proc.desc", true));
        }

        _list.Cells = Cell;
        _list.RowStyles = row => row >= 0 && row < _model.Count && _model[row].IsGroup ? RowStyle.Header : RowStyle.Normal;
        _list.CellTip = CellTip;
        _list.ContextMenuStrip = _menu;
        _list.ColumnClick += OnColumnClick;
        _list.SelectedIndexChanged += OnSelectionChanged;
        _list.ExpanderClicked += OnExpanderClicked;
        _list.KeyDown += OnListKeyDown;
        _list.TabIndex = 5;
        _menu.Opening += OnMenuOpening;
        _menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            QueueResort();
        };

        _filter.TextChanged += (_, _) =>
        {
            _model.Filter = _filter.Text;
            _resortNext = true;
            Reload();
            UpdateFooter();
        };
        _filter.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && _filter.Text.Length > 0)
            {
                _filter.Clear();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Down && _list.VirtualListSize > 0)
            {
                _list.Focus();
                e.Handled = true;
            }
        };
        _view.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _view.SelectedIndex < 0)
            {
                return;
            }

            _model.Mode = ViewOptions[Math.Min(_view.SelectedIndex, ViewOptions.Length - 1)].Mode;
            Reload();
        };
        _columns.Click += (_, _) => ShowColumnMenu();
        _detailToggle.Click += (_, _) => SetDetailShown(!_detailShown);
        _run.Click += (_, _) => RunNewTask();
        _pane.EndRequested += () => Run(TerminateSelectedAsync);
        _pane.FolderRequested += OpenSelectedFolder;
        _pane.WebRequested += SearchSelectedOnWeb;

        BuildLayout();
        _split.Panel2Collapsed = !_detailShown;
        RebuildColumns();
        _split.SplitterMoved += (_, _) =>
        {
            if (_splitReady)
            {
                _pane.Invalidate();
            }
        };
        ApplyLanguage();
    }

    /// <summary>Rows the list currently shows, group headers included. For the smoke run and tests.</summary>
    public int VisibleRowCount => _list.VirtualListSize;

    internal ProcessTableModel Model => _model;

    internal GridView List => _list;

    internal ProcessDetailPane Pane => _pane;

    internal ProcessKey? SelectedKey => _selectedKey;

    internal bool IsFrozen => _model.Frozen;

    /// <summary>The processes among the first rows from the top of the view whose program icon is ready (for the smoke run).</summary>
    internal IEnumerable<ProcessKey> KeysWithIcon(int rows = 30) =>
        Enumerable.Range(Math.Max(_list.TopItem?.Index ?? 0, 0), Math.Min(rows, _model.Count))
            .Where(i => i < _model.Count && _model[i].Process is { } p && IconOf(p.Key) is not null)
            .Select(i => _model[i].Process!.Key);

    internal int IconsReady(int rows = 30) => KeysWithIcon(rows).Count();

    internal IReadOnlyList<ProcessColumn> VisibleColumns => [.. _visible.Select(s => s.Column)];

    /// <summary>What the list would draw in the cell of <paramref name="column"/> on <paramref name="row"/> (for the self-check); empty if the column is hidden.</summary>
    internal GridCell CellAt(int row, ProcessColumn column)
    {
        var index = _visible.FindIndex(s => s.Column == column);
        return index < 0 ? new GridCell(string.Empty) : Cell(row, index);
    }

    internal TextBox FilterBox => _filter;

    public override void OnActivated()
    {
        Context.Pump.Sampled -= OnSampled;
        Context.Pump.Sampled += OnSampled;
        if (Context.Pump.Latest is { } latest)
        {
            OnSampled(latest);
        }
    }

    public override void OnDeactivated()
    {
        Context.Pump.Sampled -= OnSampled;
    }

    public override void RefreshNow()
    {
        _resortNext = true;
        if (Context.Pump.Latest is { } latest)
        {
            OnSampled(latest);
        }

        _ = Context.Pump.TickAsync();
    }

    public override bool FocusSearch()
    {
        _filter.Focus();
        _filter.SelectAll();
        return true;
    }

    public override bool HandleDelete()
    {
        if (_selectedKey is null)
        {
            return false;
        }

        Run(TerminateSelectedAsync);
        return true;
    }

    public override void OnNavigatedTo(ProcessKey? process)
    {
        if (process is not { } key)
        {
            return;
        }

        if (_filter.Text.Length > 0)
        {
            _filter.Clear();
        }

        if (Context.Pump.Latest is { } latest)
        {
            OnSampled(latest);
        }

        var index = _model.IndexOf(key);
        if (index >= 0)
        {
            _list.SelectRow(index);
            _list.Focus();
        }
    }

    public override void SaveState()
    {
        var settings = Context.Settings;
        settings.SetValue("proc.view", ViewOptions.First(o => o.Mode == _model.Mode).Saved);
        settings.SetValue("proc.sort", _model.SortColumn.ToString());
        settings.SetBool("proc.desc", _model.Descending);
        settings.SetValue("proc.cols", string.Join(",", Specs.Where(s => _shown.Contains(s.Column)).Select(s => s.Column.ToString())));
        CaptureWidths();
        foreach (var (column, width) in _widths)
        {
            settings.SetInt("proc.w." + column, width);
        }

        settings.SetBool("proc.detail", _detailShown);
        settings.SetBool("proc.memmb", _memoryInMb);
        if (_splitReady && !_split.Panel2Collapsed && _split.Panel2.Width > 0)
        {
            settings.SetInt("proc.split", ToLogical(_split.Panel2.Width));
        }
    }

    public override void ApplyLanguage()
    {
        _updating = true;
        try
        {
            _filterLabel.Text = Strings.Get("gui.proc.filter");
            _viewLabel.Text = Strings.Get("gui.proc.view.label");
            _columns.Text = Strings.Get("gui.proc.columns");
            _run.Text = Strings.Get("gui.proc.run");
            _detailToggle.Text = Strings.Get(_detailShown ? "gui.proc.detail.hide" : "gui.proc.detail.show");
            var mode = _model.Mode;
            _view.BeginUpdate();
            _view.Items.Clear();
            foreach (var option in ViewOptions)
            {
                _view.Items.Add(Strings.Get(option.Key));
            }

            _view.SelectedIndex = Math.Max(0, Array.FindIndex(ViewOptions, o => o.Mode == mode));
            _view.EndUpdate();
        }
        finally
        {
            _updating = false;
        }

        _filter.AccessibleName = Strings.Get("gui.proc.filter");
        _view.AccessibleName = Strings.Get("gui.proc.view.label");
        _list.AccessibleName = Strings.Get("gui.nav.processes");
        _tips.SetToolTip(_filter, Strings.Get("gui.proc.filter.tip"));
        _tips.SetToolTip(_view, Strings.Get("gui.proc.view.tip"));
        _tips.SetToolTip(_columns, Strings.Get("gui.proc.columns.tip"));
        _tips.SetToolTip(_detailToggle, Strings.Get("gui.proc.detail.toggle.tip"));
        foreach (ColumnHeader header in _list.Columns)
        {
            if (header.Tag is ColumnSpec spec)
            {
                header.Text = Strings.Get(spec.Key);
            }
        }

        _pane.ApplyLanguage();
        UpdateFooter();
        ShowSelected();
        _list.Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplySplit();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplySplit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Context.Pump.Sampled -= OnSampled;
            _tips.Dispose();
            _menu.Dispose();
            _columnMenu.Dispose();
            _icons.Dispose();
        }

        base.Dispose(disposing);
    }

    private int ToLogical(int device) => (int)Math.Round(device * 96.0 / Math.Max(DeviceDpi, 96));

    private void BuildLayout()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 8,
            RowCount = 1,
            Padding = new Padding(Px(12), Px(10), Px(12), Px(8)),
            Name = "processBar",
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(260)));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _filterLabel.Margin = new Padding(0, 0, Px(8), 0);
        _filter.Width = Px(250);
        _filter.Margin = new Padding(0);
        _viewLabel.Margin = new Padding(0, 0, Px(8), 0);
        _view.Width = Px(140);
        _view.Margin = new Padding(0, 0, Px(12), 0);
        _columns.MinimumSize = new Size(Px(72), Px(30));
        _columns.Margin = new Padding(0, 0, Px(8), 0);
        _detailToggle.MinimumSize = new Size(Px(96), Px(30));
        _detailToggle.Margin = new Padding(0, 0, Px(8), 0);
        _run.MinimumSize = new Size(Px(150), Px(30));
        _run.Margin = new Padding(0);
        _filter.TabIndex = 0;
        _view.TabIndex = 1;
        _columns.TabIndex = 2;
        _detailToggle.TabIndex = 3;
        _run.TabIndex = 4;
        bar.Controls.Add(_filterLabel, 0, 0);
        bar.Controls.Add(_filter, 1, 0);
        bar.Controls.Add(_viewLabel, 3, 0);
        bar.Controls.Add(_view, 4, 0);
        bar.Controls.Add(_columns, 5, 0);
        bar.Controls.Add(_detailToggle, 6, 0);
        bar.Controls.Add(_run, 7, 0);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = Px(32),
            WrapContents = false,
            Padding = new Padding(Px(12), Px(7), Px(12), 0),
            Name = "processFooter",
        };
        _hint.SetTone(CellTone.Dim);
        footer.Controls.Add(_count);
        footer.Controls.Add(_hint);

        _split.Panel1.Controls.Add(_list);
        _split.Panel2.Controls.Add(_pane);
        _split.SplitterWidth = Px(4);

        // Dock order: the last control added is laid out outermost, so the filling split goes in first.
        Controls.Add(_split);
        Controls.Add(footer);
        Controls.Add(bar);
    }

    /// <summary>The split can only be positioned once it has a size. The detail pane keeps its width when the window is resized.</summary>
    private void ApplySplit()
    {
        if (_splitReady || !IsHandleCreated || _split.Width <= Px(700))
        {
            return;
        }

        // The minimum sizes only fit once the split has a real width; set earlier they throw.
        // Opened up for the moment: the distance is worked out against both panels even when the user keeps the detail hidden.
        _split.Panel2Collapsed = false;
        _split.Panel1MinSize = Px(420);
        _split.Panel2MinSize = Px(250);
        var wanted = Px(Math.Clamp(Context.Settings.GetInt("proc.split", 290), 250, 700));
        var distance = _split.Width - wanted - _split.SplitterWidth;
        _split.SplitterDistance = Math.Clamp(distance, _split.Panel1MinSize, _split.Width - _split.Panel2MinSize - _split.SplitterWidth);
        _splitReady = true;
        _split.Panel2Collapsed = !_detailShown;
    }

    /// <summary>
    /// Hides or shows the detail pane. With it hidden the list gets the whole width (a wide table wants it); the choice is kept.
    /// Selecting a row never opens it by itself, so the list does not jump sideways under the pointer.
    /// </summary>
    internal void SetDetailShown(bool shown)
    {
        _detailShown = shown;
        _split.Panel2Collapsed = !shown;
        _detailToggle.Text = Strings.Get(shown ? "gui.proc.detail.hide" : "gui.proc.detail.show");
        _list.FitLastColumn();
        _list.Invalidate();
    }

    private void LoadColumnChoices()
    {
        _shown.Clear();
        var saved = Context.Settings.GetValue("proc.cols");
        if (!string.IsNullOrEmpty(saved))
        {
            foreach (var part in saved.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<ProcessColumn>(part, out var column) && Enum.IsDefined(column))
                {
                    _shown.Add(column);
                }
            }
        }

        // A saved set that is exactly the old factory default (PID and both I/O columns, no Disk) was never a choice:
        // it moves to the new default. Anything the user changed is kept as it is.
        if (_shown.Count == LegacyDefault.Length && LegacyDefault.All(_shown.Contains))
        {
            _shown.Clear();
        }

        if (_shown.Count == 0)
        {
            foreach (var spec in Specs.Where(s => s.Default))
            {
                _shown.Add(spec.Column);
            }
        }

        _shown.Add(ProcessColumn.Name);
        foreach (var spec in Specs)
        {
            _widths[spec.Column] = Math.Clamp(Context.Settings.GetInt("proc.w." + spec.Column, spec.Width), 40, 900);
        }
    }

    private void CaptureWidths()
    {
        for (var i = 0; i < _list.Columns.Count - 1; i++)
        {
            if (_list.Columns[i].Tag is ColumnSpec spec && _list.Columns[i].Width > 0)
            {
                _widths[spec.Column] = Math.Clamp(_list.ToLogicalWidth(_list.Columns[i].Width), 40, 900);
            }
        }
    }

    private void RebuildColumns()
    {
        CaptureWidths();
        _visible.Clear();
        _list.BeginUpdate();
        try
        {
            _list.Columns.Clear();
            foreach (var spec in Specs)
            {
                if (!_shown.Contains(spec.Column))
                {
                    continue;
                }

                _visible.Add(spec);
                _list.Columns.Add(new ColumnHeader
                {
                    Text = Strings.Get(spec.Key),
                    TextAlign = spec.Align,
                    Width = _list.ColumnWidth(_widths[spec.Column]),
                    Tag = spec,
                });
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        SyncSortIndicator();
        _list.FitLastColumn();
    }

    private void SyncSortIndicator()
    {
        var index = _visible.FindIndex(s => s.Column == _model.SortColumn);
        _list.SetSort(index, _model.Descending);
    }

    private void ShowColumnMenu()
    {
        _columnMenu.Items.Clear();
        foreach (var spec in Specs)
        {
            var item = new ToolStripMenuItem(Strings.Get(spec.Key))
            {
                Checked = _shown.Contains(spec.Column),
                Enabled = spec.Column != ProcessColumn.Name,
                Tag = spec.Column,
            };
            item.Click += (_, _) =>
            {
                var column = (ProcessColumn)item.Tag!;
                if (!_shown.Remove(column))
                {
                    _shown.Add(column);
                }

                RebuildColumns();
                _list.Invalidate();
            };
            _columnMenu.Items.Add(item);
        }

        _columnMenu.Items.Add(new ToolStripSeparator());
        _columnMenu.Items.Add(new ToolStripMenuItem(Strings.Get("gui.proc.memunit.label")) { Enabled = false });
        foreach (var (inMb, key) in new[] { (false, "gui.proc.memunit.auto"), (true, "gui.proc.memunit.mb") })
        {
            var unit = new ToolStripMenuItem(Strings.Get(key)) { Checked = _memoryInMb == inMb, CheckOnClick = false };
            unit.Click += (_, _) => SetMemoryInMb(inMb);
            _columnMenu.Items.Add(unit);
        }

        Context.Theme.StyleMenu(_columnMenu);
        _columnMenu.Show(_columns, new Point(0, _columns.Height));
    }

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (e.Column < 0 || e.Column >= _visible.Count)
        {
            return;
        }

        _model.SortBy(_visible[e.Column].Column);
        SyncSortIndicator();
        Reload();
    }

    private void OnExpanderClicked(int row)
    {
        if (row >= 0 && row < _model.Count && _model[row].Process is { } p && _model[row].HasChildren)
        {
            _model.ToggleExpanded(p.Key);
            Reload();
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model.Mode != ProcessViewMode.Tree || e.Modifiers != Keys.None || _list.SelectedRow is not (>= 0 and var row) || row >= _model.Count)
        {
            return;
        }

        var current = _model[row];
        if (current.Process is not { } p || !current.HasChildren)
        {
            return;
        }

        if ((e.KeyCode == Keys.Right && !current.Expanded) || (e.KeyCode == Keys.Left && current.Expanded))
        {
            _model.ToggleExpanded(p.Key);
            Reload();
            e.Handled = true;
        }
    }

    private void OnSampled(Snapshot snapshot)
    {
        _classifier.Refresh();
        _model.Frozen = ComputeFrozen();
        _model.Update(snapshot, _classifier.Classify);
        if (_resortNext)
        {
            _resortNext = false;
            _model.Resort();
        }

        Reload();
        if (_selectedKey is { } key)
        {
            if (_model.Find(key) is { } info)
            {
                _selectedName = info.Name;
                _pane.UpdateNumbers(info);
                _goneShown = false;
            }
            else if (!_goneShown)
            {
                _goneShown = true;
                _model.Frozen = false;
                _pane.ShowGone(_selectedName);
            }
        }

        UpdateFooter();
    }

    private bool ComputeFrozen() => _menuOpen || (_selectedKey is not null && _list.SelectedRow >= 0);

    /// <summary>Pushes the model into the list: row count, selection by identity, repaint. Never scrolls on its own.</summary>
    private void Reload()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        MeasureBars();
        _updating = true;
        try
        {
            if (_list.VirtualListSize != _model.Count)
            {
                _list.VirtualListSize = _model.Count;
            }

            if (_selectedKey is { } key)
            {
                var index = _model.IndexOf(key);
                if (index != _list.SelectedRow)
                {
                    _list.SelectRow(index, scroll: false);
                }
            }
        }
        finally
        {
            _updating = false;
        }

        _list.Invalidate();
    }

    /// <summary>
    /// What a full load cue bar means right now: the heaviest process among the rows of the list (not the idle one), or the
    /// floor if all are lighter. Once per refresh, not once per paint.
    /// </summary>
    private void MeasureBars()
    {
        var cpu = BarCpuFloorPercent;
        var memory = BarMemoryFloorBytes;
        for (var i = 0; i < _model.Count; i++)
        {
            if (_model[i].Process is not { } p || p.Key.Pid == 0)
            {
                continue;
            }

            if (p.CpuPercent.TryGet(out var share) && share > cpu)
            {
                cpu = share;
            }

            if (p.PrivateWorkingSetBytes > memory)
            {
                memory = p.PrivateWorkingSetBytes;
            }
        }

        _barCpuScale = cpu;
        _barMemoryScale = memory;
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_updating)
        {
            return;
        }

        var row = _list.SelectedRow;
        ProcessKey? key = row >= 0 && row < _model.Count && _model[row].Process is { } p ? p.Key : null;
        if (key == _selectedKey && key is not null)
        {
            return;
        }

        _selectedKey = key;
        _goneShown = false;
        _verdict = key is { } k ? Context.Services.Protection.Evaluate(k) : null;
        if (key is { } selected && _model.Find(selected) is { } info)
        {
            _selectedName = info.Name;
        }

        ShowSelected();
        _model.Frozen = ComputeFrozen();
        if (key is null)
        {
            QueueResort();
        }
    }

    /// <summary>
    /// A native list reports "nothing selected" a moment before "this row selected" when the user clicks another row.
    /// Sorting at that instant would move the row from under the click, so the re-sort waits until the dust settles.
    /// </summary>
    private void QueueResort()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        BeginInvoke(() =>
        {
            if (IsDisposed || _menuOpen || _list.SelectedRow >= 0 || (_selectedKey is not null && _model.IndexOf(_selectedKey.Value) >= 0))
            {
                return;
            }

            _model.Frozen = false;
            _model.Resort();
            Reload();
        });
    }

    private void ShowSelected()
    {
        if (_selectedKey is not { } key)
        {
            _pane.ShowNothing();
            return;
        }

        if (_model.Find(key) is not { } info)
        {
            _pane.ShowGone(_selectedName);
            return;
        }

        _pane.ShowProcess(info, _details.TryGet(key), _verdict, ParentText(info));
        ShowPaneIcon();
    }

    /// <summary>The selected process's icon in the detail pane (nothing while the path or the icon is not known yet).</summary>
    private void ShowPaneIcon() =>
        _pane.SetIcon(_selectedKey is { } key && _model.Find(key) is not null ? IconOf(key) : null);

    /// <summary>The icon of a process's image file. Asks for the details (for the path) and then the icon; null until both are known.</summary>
    private Image? IconOf(ProcessKey key) =>
        _details.TryGet(key) is { } details && details.Path.TryGet(out var path) ? _icons.TryGet(path) : null;

    private bool _repaintQueued;

    /// <summary>Icons and details arrive in bursts (one per visible row); one repaint covers the burst.</summary>
    private void QueueRepaint()
    {
        if (_repaintQueued || !IsHandleCreated || IsDisposed)
        {
            return;
        }

        _repaintQueued = true;
        BeginInvoke(() =>
        {
            _repaintQueued = false;
            if (!IsDisposed)
            {
                _list.Invalidate();
            }
        });
    }

    private string ParentText(ProcessInfo info)
    {
        if (info.ParentKey is { } parentKey && _model.Find(parentKey) is { } parent)
        {
            return parent.Name + " (" + parent.Key.Pid.ToString(CultureInfo.CurrentCulture) + ")";
        }

        return info.ParentPid > 0 ? "PID " + info.ParentPid.ToString(CultureInfo.CurrentCulture) : string.Empty;
    }

    private void UpdateFooter()
    {
        _count.Text = Strings.Format("gui.proc.count", Fmt.Dict(
            ("shown", _model.ShownProcesses.ToString(CultureInfo.CurrentCulture)),
            ("total", _model.TotalCount.ToString(CultureInfo.CurrentCulture))));
        _hint.Text = _model.Frozen ? Strings.Get("gui.proc.frozen") : string.Empty;
    }

    private GridCell Cell(int row, int column)
    {
        if (row < 0 || row >= _model.Count || column < 0 || column >= _visible.Count)
        {
            return new GridCell(string.Empty);
        }

        var line = _model[row];
        if (line.Process is not { } p)
        {
            if (column != 0)
            {
                return new GridCell(string.Empty);
            }

            return new GridCell(GroupName(line.Group) + " (" + line.GroupCount.ToString(CultureInfo.CurrentCulture) + ")") { Bold = true };
        }

        switch (_visible[column].Column)
        {
            case ProcessColumn.Name:
                return new GridCell(p.Name)
                {
                    Indent = _model.Mode == ProcessViewMode.Tree ? line.Depth : 0,
                    Glyph = line.HasChildren ? (line.Expanded ? ExpanderGlyph.Expanded : ExpanderGlyph.Collapsed) : ExpanderGlyph.None,
                    IconSlot = _iconSlots,
                    Icon = IconOf(p.Key),
                };
            case ProcessColumn.Pid:
                return new GridCell(p.Key.Pid.ToString(CultureInfo.CurrentCulture));
            case ProcessColumn.Cpu:
            {
                // "System Idle Process" is the CPU nobody used, not a load: no bar.
                var cpu = Number(p.CpuPercent, highlight: HighCpuPercent);
                cpu.Bar = p.Key.Pid != 0 && p.CpuPercent.TryGet(out var share) ? share / _barCpuScale : 0;
                return cpu;
            }

            case ProcessColumn.Memory:
                return new GridCell(MemoryText(p.PrivateWorkingSetBytes), p.PrivateWorkingSetBytes >= HighMemoryBytes ? CellTone.Accent : CellTone.Normal)
                {
                    Bar = p.PrivateWorkingSetBytes / _barMemoryScale,
                };
            case ProcessColumn.Disk:
                return p.IoReadBytesPerSec.IsOk || p.IoWriteBytesPerSec.IsOk
                    ? new GridCell(Fmt.Rate(p.IoReadBytesPerSec, p.IoWriteBytesPerSec))
                    : new GridCell(Fmt.Dash, CellTone.Dim);
            case ProcessColumn.IoRead:
                return Rate(p.IoReadBytesPerSec);
            case ProcessColumn.IoWrite:
                return Rate(p.IoWriteBytesPerSec);
            case ProcessColumn.Gpu:
                return Number(p.GpuPercent, highlight: double.MaxValue);
            case ProcessColumn.State:
                return State(p);
            case ProcessColumn.Threads:
                return new GridCell(Fmt.Int(p.ThreadCount));
            case ProcessColumn.Handles:
                return new GridCell(Fmt.Int(p.HandleCount));
            case ProcessColumn.WorkingSet:
                return new GridCell(MemoryText(p.WorkingSetBytes));
            case ProcessColumn.Commit:
                return new GridCell(MemoryText(p.CommitBytes));
            case ProcessColumn.Priority:
                return new GridCell(PriorityText(p.BasePriority));
            case ProcessColumn.Session:
                return new GridCell(p.SessionId.ToString(CultureInfo.CurrentCulture));
            case ProcessColumn.Started:
                return new GridCell(Fmt.Started(p.Key));
            default:
                return new GridCell(string.Empty);
        }
    }

    /// <summary>Chooses megabytes only (true) or MB and GB as each size fits (false) for the memory columns.</summary>
    internal void SetMemoryInMb(bool value)
    {
        _memoryInMb = value;
        _list.Invalidate();
    }

    private string MemoryText(long bytes) => _memoryInMb ? Fmt.Megabytes(bytes) : Fmt.Bytes(bytes);

    private static GridCell Number(Value<double> value, double highlight) =>
        value.TryGet(out var v)
            ? new GridCell(v.ToString("0.0", CultureInfo.CurrentCulture) + " %", v >= highlight ? CellTone.Accent : CellTone.Normal)
            : new GridCell(Fmt.Dash, CellTone.Dim);

    private static GridCell Rate(Value<double> value) =>
        value.TryGet(out _) ? new GridCell(Fmt.Rate(value)) : new GridCell(Fmt.Dash, CellTone.Dim);

    private static GridCell State(ProcessInfo p)
    {
        if (!p.State.TryGet(out var state))
        {
            return new GridCell(Fmt.Dash, CellTone.Dim);
        }

        // Not responding is shown in words and in the accent colour; the colour is never the only signal.
        return new GridCell(
            Strings.Get("gui.proc.state." + state.ToString().ToLowerInvariant()),
            state == ProcessState.NotResponding ? CellTone.Accent : state == ProcessState.Suspended ? CellTone.Dim : CellTone.Normal);
    }

    private static string PriorityText(int basePriority) => basePriority switch
    {
        4 or 6 or 8 or 10 or 13 or 24 => Strings.Get("gui.proc.priority." + basePriority.ToString(CultureInfo.InvariantCulture)),
        _ => basePriority.ToString(CultureInfo.CurrentCulture),
    };

    private static string GroupName(ProcessGroup group) => Strings.Get(group switch
    {
        ProcessGroup.App => "gui.proc.group.apps",
        ProcessGroup.Windows => "gui.proc.group.windows",
        _ => "gui.proc.group.background",
    });

    /// <summary>
    /// The program's own description ("Google Chrome") under the exe name, once its details are in. The list itself keeps the
    /// exe name, so sorting, searching and what is read aloud all agree with what is on screen.
    /// </summary>
    private string? NameTip(ProcessInfo p) =>
        ProcessDetailPane.FriendlyName(_details.TryGet(p.Key), p.Name) is { } friendly ? friendly : null;

    private string? CellTip(int row, int column)
    {
        if (row < 0 || row >= _model.Count || column < 0 || column >= _visible.Count)
        {
            return null;
        }

        var line = _model[row];
        if (line.Process is not { } p)
        {
            return column == 0 ? Strings.Get("gui.proc.group.help") : null;
        }

        return _visible[column].Column switch
        {
            ProcessColumn.Name => NameTip(p),
            ProcessColumn.Disk when !p.IoReadBytesPerSec.IsOk && !p.IoWriteBytesPerSec.IsOk => Fmt.MissingTip(p.IoReadBytesPerSec),
            ProcessColumn.Disk => Strings.Get("gui.proc.col.disk.hint"),
            ProcessColumn.Cpu when !p.CpuPercent.IsOk => Fmt.MissingTip(p.CpuPercent),
            ProcessColumn.Gpu when !p.GpuPercent.IsOk => Fmt.MissingTip(p.GpuPercent),
            ProcessColumn.IoRead when !p.IoReadBytesPerSec.IsOk => Fmt.MissingTip(p.IoReadBytesPerSec),
            ProcessColumn.IoWrite when !p.IoWriteBytesPerSec.IsOk => Fmt.MissingTip(p.IoWriteBytesPerSec),
            ProcessColumn.State when !p.State.IsOk => Fmt.MissingTip(p.State),
            ProcessColumn.IoRead or ProcessColumn.IoWrite => Strings.Get("gui.proc.col.io.hint"),
            ProcessColumn.Memory => Strings.Get("gui.proc.col.memory.hint"),
            _ => null,
        };
    }
}
