using System.ComponentModel;
using System.Globalization;
using PcMieru.Wire;

namespace PcMieru.Gui;

/// <summary>
/// Windows services: name, display name, state, start type, PID and description. Start, stop and restart ask first and
/// list the services that depend on this one (stopping takes them down too) or that it needs (starting brings them up).
/// The PID opens that process on the Processes page.
/// </summary>
internal sealed class ServicesPage : PageBase
{
    private const int NameColumn = 0;
    private const int DisplayColumn = 1;
    private const int StateColumn = 2;
    private const int StartColumn = 3;
    private const int PidColumn = 4;
    private const int DescriptionColumn = 5;

    /// <summary>Every sixth sample (about six seconds at the default interval) the list is read again while the page is open.</summary>
    private const int RefreshEvery = 6;

    private readonly RowTable<ServiceInfo> _table = new(
        s => s.Name + " " + s.DisplayName + " " + (s.Pid?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
        column => column switch
        {
            NameColumn => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            DisplayColumn => (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase),
            StateColumn => (a, b) => a.State.CompareTo(b.State),
            StartColumn => (a, b) => a.StartType.CompareTo(b.StartType),
            PidColumn => (a, b) => (a.Pid ?? uint.MaxValue).CompareTo(b.Pid ?? uint.MaxValue),
            DescriptionColumn => (a, b) => string.Compare(a.Description.GetOr(string.Empty), b.Description.GetOr(string.Empty), StringComparison.CurrentCultureIgnoreCase),
            _ => null,
        });

    private readonly GridView _grid = new() { Name = "serviceList" };
    private readonly TextBox _filter = new() { Name = "serviceFilter" };
    private readonly Label _filterLabel = new() { AutoSize = true, Name = "serviceFilterLabel" };
    private readonly Button _start = new() { Name = "serviceStart", AutoSize = true };
    private readonly Button _stop = new() { Name = "serviceStop", AutoSize = true };
    private readonly Button _restart = new() { Name = "serviceRestart", AutoSize = true };
    private readonly Button _reload = new() { Name = "serviceReload", AutoSize = true };
    private readonly Label _count = new() { AutoSize = true, Name = "serviceCount", Margin = new Padding(0, 0, 16, 0) };
    private readonly ToneLabel _hint = new() { AutoSize = true, Name = "serviceHint" };
    private readonly ContextMenuStrip _menu = new() { Name = "serviceMenu" };
    private readonly ToolTip _tips = new() { ShowAlways = true };

    private string? _selectedName;
    private string? _pendingSelect;
    private bool _loaded;
    private bool _busy;
    private bool _updating;
    private int _ticks;

    public ServicesPage(GuiContext context)
        : base(context, "services", "gui.nav.services")
    {
        PageLayout.Prepare(
            _grid,
            Strings.Get("gui.nav.services"),
            (string.Empty, Px(150), HorizontalAlignment.Left),
            (string.Empty, Px(210), HorizontalAlignment.Left),
            (string.Empty, Px(90), HorizontalAlignment.Left),
            (string.Empty, Px(120), HorizontalAlignment.Left),
            (string.Empty, Px(70), HorizontalAlignment.Right),
            (string.Empty, Px(420), HorizontalAlignment.Left));
        _grid.Cells = Cell;
        _grid.CellTip = CellTip;
        _grid.ContextMenuStrip = _menu;
        _grid.ColumnClick += (_, e) =>
        {
            _table.SortBy(e.Column);
            _grid.SetSort(_table.SortColumn, _table.Descending);
            Reload();
        };
        _grid.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _grid.MouseClick += OnGridClick;
        _grid.MouseMove += OnGridMove;
        _grid.DoubleClick += (_, _) => ShowProcess();
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && ShowProcess())
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        _grid.SizeChanged += (_, _) => _grid.FitLastColumn();
        _menu.Opening += OnMenuOpening;

        _filter.Width = Px(260);
        _start.MinimumSize = _stop.MinimumSize = _restart.MinimumSize = new Size(Px(72), Px(30));
        _reload.MinimumSize = new Size(Px(88), Px(30));
        _filter.TextChanged += (_, _) =>
        {
            _table.Filter = _filter.Text;
            Reload();
        };
        _filter.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && _filter.Text.Length > 0)
            {
                _filter.Clear();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Down && _grid.VirtualListSize > 0)
            {
                _grid.Focus();
                e.Handled = true;
            }
        };
        _start.Click += (_, _) => Run(() => OperateAsync(ServiceOperation.Start));
        _stop.Click += (_, _) => Run(() => OperateAsync(ServiceOperation.Stop));
        _restart.Click += (_, _) => Run(() => OperateAsync(ServiceOperation.Restart));
        _reload.Click += (_, _) => RefreshNow();

        _filter.TabIndex = 0;
        _start.TabIndex = 1;
        _stop.TabIndex = 2;
        _restart.TabIndex = 3;
        _reload.TabIndex = 4;
        _grid.TabIndex = 5;
        var pad = Px(12);
        Controls.Add(_grid);
        Controls.Add(PageLayout.Footer("serviceFooter", pad, _count, _hint));
        Controls.Add(PageLayout.Bar("serviceBar", pad, [_filterLabel, _filter], [_start, _stop, _restart, _reload]));
        _table.SetSort(NameColumn, false);
        _grid.SetSort(NameColumn, false);
        ApplyLanguage();
        UpdateButtons();
    }

    private enum ServiceOperation
    {
        Start,
        Stop,
        Restart,
    }

    public override bool IsReady => _loaded;

    internal GridView Grid => _grid;

    internal RowTable<ServiceInfo> Table => _table;

    internal Button StartButton => _start;

    internal Button StopButton => _stop;

    internal Button RestartButton => _restart;

    internal TextBox FilterBox => _filter;

    public override void OnActivated()
    {
        Context.Pump.Sampled -= OnSampled;
        Context.Pump.Sampled += OnSampled;
        if (!_loaded)
        {
            Run(LoadAsync);
        }
    }

    public override void OnDeactivated() => Context.Pump.Sampled -= OnSampled;

    public override void RefreshNow() => Run(LoadAsync);

    public override bool FocusSearch()
    {
        _filter.Focus();
        _filter.SelectAll();
        return true;
    }

    public override void OnNavigatedTo(ProcessKey? process)
    {
        if (Context.TakeNavigationArgument() is not { Length: > 0 } name)
        {
            return;
        }

        _pendingSelect = name;
        if (_filter.Text.Length > 0)
        {
            _filter.Clear();
        }

        SelectPending();
    }

    public override void ApplyLanguage()
    {
        _filterLabel.Text = Strings.Get("gui.svc.filter");
        _start.Text = Strings.Get("gui.svc.act.start");
        _stop.Text = Strings.Get("gui.svc.act.stop");
        _restart.Text = Strings.Get("gui.svc.act.restart");
        _reload.Text = Strings.Get("gui.common.reload");
        _filter.AccessibleName = Strings.Get("gui.svc.filter");
        _grid.AccessibleName = Strings.Get("gui.nav.services");
        _tips.SetToolTip(_filter, Strings.Get("gui.svc.filter.tip"));
        _tips.SetToolTip(_reload, Strings.Get("gui.svc.reload.tip"));
        string[] keys = ["name", "display", "state", "start", "pid", "desc"];
        for (var i = 0; i < keys.Length && i < _grid.Columns.Count; i++)
        {
            _grid.Columns[i].Text = Strings.Get("gui.svc.col." + keys[i]);
        }

        _hint.Text = Strings.Get("gui.svc.hint");
        UpdateFooter();
        UpdateButtons();
        _grid.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Context.Pump.Sampled -= OnSampled;
            _tips.Dispose();
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnSampled(Snapshot snapshot)
    {
        if (++_ticks % RefreshEvery == 0 && !_busy && IsHandleCreated)
        {
            Run(LoadAsync);
        }
    }

    private async Task LoadAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var list = await Context.Services.Services.ListAsync().ConfigureAwait(true);
            _table.Set(list);
            _loaded = true;
            Reload();
            SelectPending();
        }
        finally
        {
            _busy = false;
        }
    }

    private void Reload()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        _updating = true;
        try
        {
            if (_grid.VirtualListSize != _table.Count)
            {
                _grid.VirtualListSize = _table.Count;
            }

            if (_selectedName is { } name)
            {
                var index = _table.IndexOf(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                if (index != _grid.SelectedRow)
                {
                    _grid.SelectRow(index, scroll: false);
                }
            }
        }
        finally
        {
            _updating = false;
        }

        _grid.Invalidate();
        UpdateFooter();
        UpdateButtons();
    }

    private void SelectPending()
    {
        if (_pendingSelect is not { } name || !_loaded)
        {
            return;
        }

        var index = _table.IndexOf(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        _pendingSelect = null;
        _selectedName = _table[index].Name;
        if (_grid.VirtualListSize != _table.Count)
        {
            _grid.VirtualListSize = _table.Count;
        }

        _grid.SelectRow(index);
        _grid.Focus();
    }

    private ServiceInfo? Selected => _grid.SelectedRow is >= 0 and var row && row < _table.Count ? _table[row] : null;

    private void OnSelectionChanged()
    {
        if (_updating)
        {
            return;
        }

        _selectedName = Selected?.Name;
        UpdateButtons();
    }

    private GridCell Cell(int row, int column)
    {
        if (row < 0 || row >= _table.Count)
        {
            return default;
        }

        var s = _table[row];
        switch (column)
        {
            case NameColumn:
                return new GridCell(s.Name);
            case DisplayColumn:
                return new GridCell(s.DisplayName);
            case StateColumn:
                return new GridCell(Strings.Get("gui.svc.state." + s.State.ToString().ToLowerInvariant()), s.State == ServiceState.Running ? CellTone.Normal : CellTone.Dim);
            case StartColumn:
                return new GridCell(Strings.Get("gui.svc.start." + s.StartType.ToString().ToLowerInvariant()), s.StartType == ServiceStartType.Disabled ? CellTone.Dim : CellTone.Normal);
            case PidColumn:
                return s.Pid is { } pid && pid != 0
                    ? new GridCell(pid.ToString(CultureInfo.CurrentCulture))
                    : new GridCell(Fmt.Dash, CellTone.Dim);
            case DescriptionColumn:
                return s.Description.TryGet(out var text) ? new GridCell(text) : new GridCell(Fmt.Dash, CellTone.Dim);
            default:
                return default;
        }
    }

    private string? CellTip(int row, int column)
    {
        if (row < 0 || row >= _table.Count)
        {
            return null;
        }

        var s = _table[row];
        return column switch
        {
            DescriptionColumn when !s.Description.TryGet(out _) => Fmt.MissingTip(s.Description),
            DescriptionColumn => s.Description.GetOr(string.Empty),
            PidColumn when s.Pid is > 0 => Strings.Get("gui.svc.pid.tip"),
            DisplayColumn => s.DisplayName,
            _ => null,
        };
    }

    private bool OverPid(Point location) =>
        _grid.ColumnAt(location) == PidColumn && _grid.RowAt(location) is >= 0 and var row && row < _table.Count && _table[row].Pid is > 0;

    private void OnGridMove(object? sender, MouseEventArgs e) => _grid.Cursor = OverPid(e.Location) ? Cursors.Hand : Cursors.Default;

    private void OnGridClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && OverPid(e.Location))
        {
            _grid.SelectRow(_grid.RowAt(e.Location), scroll: false);
            ShowProcess();
        }
    }

    /// <summary>Opens the service's process on the Processes page. Returns false when the service is not running.</summary>
    internal bool ShowProcess()
    {
        if (Selected is not { Pid: > 0 } service || Context.Pump.Latest is not { } latest)
        {
            return false;
        }

        var process = latest.Processes.FirstOrDefault(p => p.Key.Pid == service.Pid);
        if (process is null)
        {
            Context.ReportKey("gui.svc.noprocess");
            return false;
        }

        Context.Navigate("processes", process.Key);
        return true;
    }

    internal static bool CanStart(ServiceInfo s) => s.State == ServiceState.Stopped && s.StartType != ServiceStartType.Disabled;

    internal static bool CanStop(ServiceInfo s) => s.State is ServiceState.Running or ServiceState.Paused;

    private void UpdateButtons()
    {
        var s = Selected;
        _start.Enabled = !_busy && s is not null && CanStart(s);
        _stop.Enabled = !_busy && s is not null && CanStop(s);
        _restart.Enabled = !_busy && s is not null && CanStop(s);
        _tips.SetToolTip(_start, s is not null && !CanStart(s) ? Strings.Get(s.StartType == ServiceStartType.Disabled ? "gui.svc.off.disabled" : "gui.svc.off.running") : Strings.Get("gui.svc.act.start.tip"));
        _tips.SetToolTip(_stop, s is not null && !CanStop(s) ? Strings.Get("gui.svc.off.stopped") : Strings.Get("gui.svc.act.stop.tip"));
        _tips.SetToolTip(_restart, s is not null && !CanStop(s) ? Strings.Get("gui.svc.off.stopped") : Strings.Get("gui.svc.act.restart.tip"));
    }

    private void UpdateFooter() =>
        _count.Text = Strings.Format("gui.svc.count", Fmt.Dict(
            ("shown", _table.Count.ToString(CultureInfo.CurrentCulture)),
            ("total", _table.TotalCount.ToString(CultureInfo.CurrentCulture))));

    private static List<DialogFact> Facts(ServiceInfo s)
    {
        List<DialogFact> facts =
        [
            new(Strings.Get("gui.svc.col.name"), s.Name),
            new(Strings.Get("gui.svc.col.display"), s.DisplayName),
        ];
        if (s.Pid is > 0)
        {
            facts.Add(new DialogFact(Strings.Get("gui.svc.col.pid"), s.Pid.Value.ToString(CultureInfo.CurrentCulture)));
        }

        return facts;
    }

    /// <summary>The names to show for a list of service names: the display name when the list knows it.</summary>
    private List<string> Pretty(IEnumerable<string> names, Func<ServiceInfo, bool> keep)
    {
        var result = new List<string>();
        foreach (var name in names)
        {
            var known = _table.All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (known is not null && !keep(known))
            {
                continue;
            }

            result.Add(known is null || known.DisplayName.Length == 0 ? name : known.DisplayName + " (" + name + ")");
        }

        return result;
    }

    /// <summary>The services that stop together with this one (running dependents) and the ones starting it brings up (stopped requirements).</summary>
    internal List<string> AffectedBy(ServiceInfo s, bool stopping) =>
        stopping
            ? Pretty(s.Dependents, d => d.State != ServiceState.Stopped)
            : Pretty(s.DependsOn, d => d.State == ServiceState.Stopped);

    private async Task OperateAsync(ServiceOperation operation)
    {
        if (Selected is not { } service || _busy)
        {
            return;
        }

        var stopping = operation != ServiceOperation.Start;
        if ((stopping && !CanStop(service)) || (!stopping && !CanStart(service)))
        {
            return;
        }

        var affected = AffectedBy(service, stopping);
        var key = operation.ToString().ToLowerInvariant();
        var message = Strings.Get("gui.svc.confirm." + key + ".msg");
        if (affected.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine + Strings.Get(stopping ? "gui.svc.confirm.dependents" : "gui.svc.confirm.needs");
        }

        var confirmed = Dialogs.Confirm(
            FindForm(),
            Context.Theme,
            Strings.Get("gui.svc.confirm." + key + ".title"),
            message,
            Facts(service),
            Strings.Get("gui.svc.act." + key),
            destructive: stopping,
            lines: affected);
        if (!confirmed)
        {
            return;
        }

        _busy = true;
        UpdateButtons();
        try
        {
            var manager = Context.Services.Services;
            var result = operation switch
            {
                ServiceOperation.Start => await manager.StartAsync(service.Name).ConfigureAwait(true),
                ServiceOperation.Stop => await manager.StopAsync(service.Name).ConfigureAwait(true),
                _ => await manager.RestartAsync(service.Name).ConfigureAwait(true),
            };
            var line = Fmt.OperationLine(service.DisplayName.Length > 0 ? service.DisplayName : service.Name, result);
            Context.Report(line);
            if (result.Code is not (OperationCode.Ok or OperationCode.Gone))
            {
                Dialogs.Info(FindForm(), Context.Theme, Strings.Get("gui.svc.act." + key), line, Facts(service));
            }
        }
        finally
        {
            _busy = false;
        }

        await LoadAsync().ConfigureAwait(true);
        _ = Context.Pump.TickAsync();
    }

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        if (Selected is not { } s)
        {
            e.Cancel = true;
            return;
        }

        _menu.Items.Clear();
        Add(Strings.Get("gui.svc.act.start"), CanStart(s), () => Run(() => OperateAsync(ServiceOperation.Start)));
        Add(Strings.Get("gui.svc.act.stop"), CanStop(s), () => Run(() => OperateAsync(ServiceOperation.Stop)));
        Add(Strings.Get("gui.svc.act.restart"), CanStop(s), () => Run(() => OperateAsync(ServiceOperation.Restart)));
        _menu.Items.Add(new ToolStripSeparator());
        Add(Strings.Get("gui.svc.act.showprocess"), s.Pid is > 0, () => ShowProcess());
        Add(Strings.Get("gui.svc.act.copy"), true, () => Context.CopyText(s.Name));
        Context.Theme.StyleMenu(_menu);
    }

    private void Add(string text, bool enabled, Action action)
    {
        var item = new ToolStripMenuItem(text) { Enabled = enabled };
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
    }
}

internal static class ServiceStrings
{
    public static readonly StringEntry[] All =
    [
        new("gui.svc.col.name", "名前", "Name"),
        new("gui.svc.col.display", "表示名", "Display name"),
        new("gui.svc.col.state", "状態", "Status"),
        new("gui.svc.col.start", "開始の種類", "Start type"),
        new("gui.svc.col.pid", "PID", "PID"),
        new("gui.svc.col.desc", "説明", "Description"),
        new("gui.svc.filter", "検索", "Search"),
        new("gui.svc.filter.tip", "名前・表示名・PID で絞り込みます（Ctrl+F）", "Filter by name, display name or PID (Ctrl+F)"),
        new("gui.svc.reload.tip", "サービスの一覧を読み直します（F5）", "Read the service list again (F5)"),
        new("gui.svc.count", "{shown} / {total} 件のサービス", "{shown} of {total} services"),
        new("gui.svc.hint", "サービスは Windows が裏で動かしている部品です。止めると、ほかの機能が動かなくなることがあります。", "Services are parts Windows runs in the background. Stopping one can make other features stop working."),
        new("gui.svc.pid.tip", "クリックで、このサービスのプロセスを表示します", "Click to show this service's process"),
        new("gui.svc.noprocess", "このサービスのプロセスは、いまの一覧に見つかりません", "This service's process is not in the current list"),
        new("gui.svc.act.start", "開始", "Start"),
        new("gui.svc.act.stop", "停止", "Stop"),
        new("gui.svc.act.restart", "再起動", "Restart"),
        new("gui.svc.act.start.tip", "選んだサービスを開始します", "Start the selected service"),
        new("gui.svc.act.stop.tip", "選んだサービスを停止します", "Stop the selected service"),
        new("gui.svc.act.restart.tip", "選んだサービスを止めて、もう一度開始します", "Stop the selected service and start it again"),
        new("gui.svc.act.showprocess", "プロセスを表示", "Show process"),
        new("gui.svc.act.copy", "名前をコピー", "Copy name"),
        new("gui.svc.off.disabled", "開始の種類が「無効」のため、開始できません", "Its start type is Disabled, so it cannot be started"),
        new("gui.svc.off.running", "すでに動いています", "It is already running"),
        new("gui.svc.off.stopped", "いまは止まっているため選べません", "It is not running, so this is not available"),
        new("gui.svc.confirm.start.title", "サービスを開始しますか", "Start this service?"),
        new("gui.svc.confirm.start.msg", "このサービスを開始します。", "This service will be started."),
        new("gui.svc.confirm.stop.title", "サービスを停止しますか", "Stop this service?"),
        new("gui.svc.confirm.stop.msg", "停止すると、このサービスに頼っている機能が動かなくなることがあります。管理者の権限が要ることもあります。", "Features that rely on it may stop working. Administrator rights may be needed."),
        new("gui.svc.confirm.restart.title", "サービスを再起動しますか", "Restart this service?"),
        new("gui.svc.confirm.restart.msg", "いったん停止して、もう一度開始します。少しのあいだ、このサービスを使う機能が止まります。", "It is stopped and started again. Features that use it pause for a moment."),
        new("gui.svc.confirm.dependents", "あわせて止まるサービス:", "Services that stop with it:"),
        new("gui.svc.confirm.needs", "先に開始されるサービス:", "Services that are started first:"),
        new("gui.svc.state.stopped", "停止", "Stopped"),
        new("gui.svc.state.startpending", "開始中", "Starting"),
        new("gui.svc.state.stoppending", "停止中", "Stopping"),
        new("gui.svc.state.running", "実行中", "Running"),
        new("gui.svc.state.continuepending", "再開中", "Resuming"),
        new("gui.svc.state.pausepending", "一時停止中", "Pausing"),
        new("gui.svc.state.paused", "一時停止", "Paused"),
        new("gui.svc.state.unknown", "不明", "Unknown"),
        new("gui.svc.start.automatic", "自動", "Automatic"),
        new("gui.svc.start.automaticdelayed", "自動（遅延開始）", "Automatic (delayed)"),
        new("gui.svc.start.manual", "手動", "Manual"),
        new("gui.svc.start.disabled", "無効", "Disabled"),
        new("gui.svc.start.boot", "ブート", "Boot"),
        new("gui.svc.start.system", "システム", "System"),
        new("gui.svc.start.unknown", "不明", "Unknown"),
    ];
}
