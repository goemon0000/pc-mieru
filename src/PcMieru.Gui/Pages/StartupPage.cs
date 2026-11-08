using System.ComponentModel;
using System.Globalization;
using PcMieru.Wire;

namespace PcMieru.Gui;

/// <summary>
/// What starts with Windows. Every item the providers know is listed; the on/off button is offered only for items
/// that can really be toggled (<see cref="StartupItem.CanToggle"/>), and one that needs the Helper (machine-wide entries)
/// says so before it asks. Run-once keys and policy-based start-up are not covered, and the page says that.
/// </summary>
internal sealed class StartupPage : PageBase
{
    private const int NameColumn = 0;
    private const int LocationColumn = 1;
    private const int StateColumn = 2;
    private const int CommandColumn = 3;

    private readonly RowTable<StartupItem> _table = new(
        i => i.Name + " " + i.Command,
        column => column switch
        {
            NameColumn => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            LocationColumn => (a, b) => a.Provider.CompareTo(b.Provider),
            StateColumn => (a, b) => a.State.CompareTo(b.State),
            CommandColumn => (a, b) => string.Compare(a.Command, b.Command, StringComparison.CurrentCultureIgnoreCase),
            _ => null,
        });

    private readonly GridView _grid = new() { Name = "startupList" };
    private readonly TextBox _filter = new() { Name = "startupFilter" };
    private readonly Label _filterLabel = new() { AutoSize = true, Name = "startupFilterLabel" };
    private readonly Button _toggle = new() { Name = "startupToggle", AutoSize = true };
    private readonly Button _reload = new() { Name = "startupReload", AutoSize = true };
    private readonly Label _count = new() { AutoSize = true, Name = "startupCount", Margin = new Padding(0, 0, 16, 0) };
    private readonly ToneLabel _hint = new() { AutoSize = true, Name = "startupHint" };
    private readonly ToolTip _tips = new() { ShowAlways = true };

    private string? _selectedId;
    private bool _loaded;
    private bool _busy;
    private bool _updating;

    public StartupPage(GuiContext context)
        : base(context, "startup", "gui.nav.startup")
    {
        PageLayout.Prepare(
            _grid,
            Strings.Get("gui.nav.startup"),
            (string.Empty, Px(240), HorizontalAlignment.Left),
            (string.Empty, Px(230), HorizontalAlignment.Left),
            (string.Empty, Px(100), HorizontalAlignment.Left),
            (string.Empty, Px(520), HorizontalAlignment.Left));
        _grid.Cells = Cell;
        _grid.CellTip = CellTip;
        _grid.ColumnClick += (_, e) =>
        {
            _table.SortBy(e.Column);
            _grid.SetSort(_table.SortColumn, _table.Descending);
            Reload();
        };
        _grid.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _grid.SizeChanged += (_, _) => _grid.FitLastColumn();
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space && Selected is not null && _toggle.Enabled)
            {
                _toggle.PerformClick();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        _filter.Width = Px(260);
        _toggle.MinimumSize = new Size(Px(160), Px(30));
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
        _toggle.Click += (_, _) => Run(ToggleAsync);
        _reload.Click += (_, _) => RefreshNow();

        _filter.TabIndex = 0;
        _toggle.TabIndex = 1;
        _reload.TabIndex = 2;
        _grid.TabIndex = 3;
        var pad = Px(12);
        Controls.Add(_grid);
        Controls.Add(PageLayout.Footer("startupFooter", pad, _count, _hint));
        Controls.Add(PageLayout.Bar("startupBar", pad, [_filterLabel, _filter], [_toggle, _reload]));
        _table.SetSort(NameColumn, false);
        _grid.SetSort(NameColumn, false);
        ApplyLanguage();
    }

    public override bool IsReady => _loaded;

    internal GridView Grid => _grid;

    internal RowTable<StartupItem> Table => _table;

    internal Button ToggleButton => _toggle;

    internal TextBox FilterBox => _filter;

    public override void OnActivated()
    {
        if (!_loaded)
        {
            Run(LoadAsync);
        }
    }

    public override void RefreshNow() => Run(LoadAsync);

    public override bool FocusSearch()
    {
        _filter.Focus();
        _filter.SelectAll();
        return true;
    }

    public override void ApplyLanguage()
    {
        _filterLabel.Text = Strings.Get("gui.startup.filter");
        _reload.Text = Strings.Get("gui.common.reload");
        _filter.AccessibleName = Strings.Get("gui.startup.filter");
        _grid.AccessibleName = Strings.Get("gui.nav.startup");
        _tips.SetToolTip(_filter, Strings.Get("gui.startup.filter.tip"));
        _tips.SetToolTip(_reload, Strings.Get("gui.startup.reload.tip"));
        string[] keys = ["name", "provider", "state", "command"];
        for (var i = 0; i < keys.Length && i < _grid.Columns.Count; i++)
        {
            _grid.Columns[i].Text = Strings.Get("gui.startup.col." + keys[i]);
        }

        UpdateFooter();
        UpdateButtons();
        _grid.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task LoadAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateButtons();
        try
        {
            var list = await Context.Services.Startup.ListAsync().ConfigureAwait(true);
            _table.Set(list);
            _loaded = true;
        }
        finally
        {
            _busy = false;
        }

        Reload();
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

            if (_selectedId is { } id)
            {
                var index = _table.IndexOf(i => i.Id == id);
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

    private StartupItem? Selected => _grid.SelectedRow is >= 0 and var row && row < _table.Count ? _table[row] : null;

    private void OnSelectionChanged()
    {
        if (_updating)
        {
            return;
        }

        _selectedId = Selected?.Id;
        UpdateFooter();
        UpdateButtons();
    }

    internal static string StateKey(StartupState state) => "gui.startup.state." + state.ToString().ToLowerInvariant();

    internal static string ProviderKey(StartupProvider provider) => "gui.startup.provider." + provider.ToString().ToLowerInvariant();

    private GridCell Cell(int row, int column)
    {
        if (row < 0 || row >= _table.Count)
        {
            return default;
        }

        var item = _table[row];
        return column switch
        {
            NameColumn => new GridCell(item.Name),
            LocationColumn => new GridCell(Strings.Get(ProviderKey(item.Provider))),
            StateColumn => new GridCell(Strings.Get(StateKey(item.State)), item.State == StartupState.Enabled ? CellTone.Normal : CellTone.Dim),
            CommandColumn => new GridCell(item.Command, CellTone.Dim),
            _ => default,
        };
    }

    private string? CellTip(int row, int column)
    {
        if (row < 0 || row >= _table.Count)
        {
            return null;
        }

        var item = _table[row];
        return column switch
        {
            NameColumn when !item.CanToggle => Strings.Get("gui.startup.listonly.tip"),
            StateColumn when item.State == StartupState.Unknown => Strings.Get("gui.startup.unknown.tip"),
            CommandColumn => item.Command,
            _ => null,
        };
    }

    private void UpdateButtons()
    {
        var item = Selected;
        var can = !_busy && item is { CanToggle: true };
        _toggle.Enabled = can;
        _toggle.Text = Strings.Get(item is { State: StartupState.Enabled } ? "gui.startup.act.disable" : "gui.startup.act.enable");
        _tips.SetToolTip(_toggle, item switch
        {
            null => Strings.Get("gui.startup.act.none"),
            { CanToggle: false } => Strings.Get("gui.startup.listonly.tip"),
            { NeedsHelper: true } => Strings.Get("gui.startup.act.helper"),
            _ => Strings.Get("gui.startup.act.tip"),
        });
    }

    private void UpdateFooter()
    {
        _count.Text = Strings.Format("gui.startup.count", Fmt.Dict(
            ("shown", _table.Count.ToString(CultureInfo.CurrentCulture)),
            ("total", _table.TotalCount.ToString(CultureInfo.CurrentCulture))));
        _hint.Text = Selected is { } item
            ? string.Join("  ", new[] { item.Detail, item.CanToggle ? null : Strings.Get("gui.startup.listonly"), item.NeedsHelper ? Strings.Get("gui.startup.needshelper") : null }.Where(t => !string.IsNullOrEmpty(t)))
            : Strings.Get("gui.startup.hint");
    }

    private async Task ToggleAsync()
    {
        if (Selected is not { CanToggle: true } item || _busy)
        {
            return;
        }

        var enable = item.State != StartupState.Enabled;
        if (item.NeedsHelper)
        {
            var confirmed = Dialogs.Confirm(
                FindForm(),
                Context.Theme,
                Strings.Get("gui.startup.confirm.title"),
                Strings.Get("gui.startup.confirm.helper"),
                [
                    new(Strings.Get("gui.startup.col.name"), item.Name),
                    new(Strings.Get("gui.startup.col.provider"), Strings.Get(ProviderKey(item.Provider))),
                ],
                Strings.Get(enable ? "gui.startup.act.enable" : "gui.startup.act.disable"),
                destructive: false);
            if (!confirmed)
            {
                return;
            }
        }

        _busy = true;
        UpdateButtons();
        try
        {
            var result = await Context.Services.Startup.SetEnabledAsync(item.Id, enable).ConfigureAwait(true);
            var line = Fmt.OperationLine(item.Name, result);
            Context.Report(line);
            if (!result.IsSuccess)
            {
                Dialogs.Info(FindForm(), Context.Theme, Strings.Get(enable ? "gui.startup.act.enable" : "gui.startup.act.disable"), line);
            }
        }
        finally
        {
            _busy = false;
        }

        await LoadAsync().ConfigureAwait(true);
    }
}

internal static class StartupStrings
{
    public static readonly StringEntry[] All =
    [
        new("gui.startup.col.name", "名前", "Name"),
        new("gui.startup.col.provider", "登録の場所", "Where it is registered"),
        new("gui.startup.col.state", "状態", "Status"),
        new("gui.startup.col.command", "起動するもの", "What it starts"),
        new("gui.startup.filter", "検索", "Search"),
        new("gui.startup.filter.tip", "名前や起動するものの一部で絞り込みます（Ctrl+F）", "Filter by part of the name or of what it starts (Ctrl+F)"),
        new("gui.startup.reload.tip", "一覧を読み直します（F5）", "Read the list again (F5)"),
        new("gui.startup.count", "{shown} / {total} 件", "{shown} of {total}"),
        new("gui.startup.hint", "Windows と一緒に起動するものの一覧です。RunOnce とグループポリシーによる起動は、ここには出ません。", "Things that start with Windows. Run-once entries and Group Policy start-up are not shown here."),
        new("gui.startup.listonly", "表示のみ（ここでは切り替えられません）", "View only (it cannot be switched here)"),
        new("gui.startup.listonly.tip", "この項目は一覧に表示するだけで、ここから有効・無効は切り替えられません", "This item is only listed; it cannot be switched on or off from here"),
        new("gui.startup.unknown.tip", "有効か無効かを確かめられませんでした", "Could not tell whether it is on or off"),
        new("gui.startup.needshelper", "切り替えには管理者の確認が要ります", "Switching it needs administrator approval"),
        new("gui.startup.act.enable", "有効にする", "Turn on"),
        new("gui.startup.act.disable", "無効にする", "Turn off"),
        new("gui.startup.act.none", "切り替える項目を選んでください", "Select an item to switch"),
        new("gui.startup.act.tip", "選んだ項目の起動を切り替えます。アプリは消えません", "Switches whether the selected item starts. The app itself is not removed"),
        new("gui.startup.act.helper", "管理者の確認画面が出ます。PC 全体の設定を切り替えるためです", "A Windows approval prompt appears, because this changes a setting for the whole PC"),
        new("gui.startup.confirm.title", "管理者の確認が必要です", "Administrator approval needed"),
        new("gui.startup.confirm.helper", "この項目は PC 全体の設定です。切り替えるには、Windows の確認画面で許可する必要があります。", "This item is a setting for the whole PC. You need to approve a Windows prompt to switch it."),
        new("gui.startup.state.enabled", "有効", "On"),
        new("gui.startup.state.disabled", "無効", "Off"),
        new("gui.startup.state.unknown", "不明", "Unknown"),
        new("gui.startup.provider.runhkcu", "ログオン時（このユーザー）", "Sign-in (this user)"),
        new("gui.startup.provider.runhklm", "ログオン時（全ユーザー）", "Sign-in (all users)"),
        new("gui.startup.provider.runhklm32", "ログオン時（全ユーザー・32 ビット）", "Sign-in (all users, 32-bit)"),
        new("gui.startup.provider.startupfolderuser", "スタートアップ フォルダー（このユーザー）", "Startup folder (this user)"),
        new("gui.startup.provider.startupfoldercommon", "スタートアップ フォルダー（全ユーザー）", "Startup folder (all users)"),
        new("gui.startup.provider.scheduledtask", "タスク スケジューラ", "Task Scheduler"),
        new("gui.startup.provider.packagedapp", "ストア アプリ", "Store app"),
    ];
}
