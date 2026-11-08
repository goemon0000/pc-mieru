using System.Globalization;

namespace PcMieru.Gui;

/// <summary>One line of the Users page: a session (with its totals) or one of the processes under an expanded session.</summary>
internal sealed record UserRow(SessionInfo Session, ProcessInfo? Process, int ProcessCount, double Cpu, bool CpuPartial, long MemoryBytes, bool Visible)
{
    public bool IsSession => Process is null;
}

/// <summary>
/// Who is signed in and what each session uses. Sessions come from the session provider; CPU and memory are summed from
/// the latest snapshot by SessionId. A row opens to show that session's processes. Another user's processes may not be
/// readable without administrator rights, and then the numbers are partial and the page says so instead of showing zero.
/// </summary>
internal sealed class UsersPage : PageBase
{
    private const int UserColumn = 0;
    private const int SessionColumn = 1;
    private const int StateColumn = 2;
    private const int CpuColumn = 3;
    private const int MemoryColumn = 4;
    private const int CountColumn = 5;

    private const int SessionRefreshEvery = 10;

    private readonly GridView _grid = new() { Name = "userList" };
    private readonly Button _reload = new() { Name = "userReload", AutoSize = true };
    private readonly Label _count = new() { AutoSize = true, Name = "userCount", Margin = new Padding(0, 0, 16, 0) };
    private readonly ToneLabel _partial = new() { AutoSize = true, Name = "userPartial" };
    private readonly ToneLabel _hint = new() { AutoSize = true, Name = "userHint" };
    private readonly List<UserRow> _rows = [];
    private readonly HashSet<uint> _expanded = [];
    private readonly uint _ownSession;

    private IReadOnlyList<SessionInfo> _sessions = [];
    private string? _selectedKey;
    private bool _loaded;
    private bool _busy;
    private bool _updating;
    private bool _anyPartial;
    private int _ticks;

    public UsersPage(GuiContext context)
        : base(context, "users", "gui.nav.users")
    {
        _ownSession = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
        PageLayout.Prepare(
            _grid,
            Strings.Get("gui.nav.users"),
            (string.Empty, Px(280), HorizontalAlignment.Left),
            (string.Empty, Px(100), HorizontalAlignment.Right),
            (string.Empty, Px(130), HorizontalAlignment.Left),
            (string.Empty, Px(100), HorizontalAlignment.Right),
            (string.Empty, Px(130), HorizontalAlignment.Right),
            (string.Empty, Px(130), HorizontalAlignment.Right));
        _grid.Cells = Cell;
        _grid.RowStyles = row => row >= 0 && row < _rows.Count && _rows[row].IsSession ? RowStyle.Header : RowStyle.Normal;
        _grid.CellTip = CellTip;
        _grid.ExpanderClicked += Toggle;
        _grid.SelectedIndexChanged += (_, _) =>
        {
            if (!_updating)
            {
                _selectedKey = Selected is { } row ? KeyOf(row) : null;
            }
        };
        _grid.DoubleClick += (_, _) => Open();
        _grid.KeyDown += OnGridKey;
        _grid.FillColumn = 0;
        _grid.SizeChanged += (_, _) => _grid.FitLastColumn();
        _reload.MinimumSize = new Size(Px(88), Px(30));
        _reload.Click += (_, _) => RefreshNow();
        _reload.TabIndex = 0;
        _grid.TabIndex = 1;

        var pad = Px(12);
        var footer = PageLayout.Footer("userFooter", pad, _count, _partial);
        footer.Controls.Add(_hint);
        Controls.Add(_grid);
        Controls.Add(footer);
        Controls.Add(PageLayout.Bar("userBar", pad, [], [_reload]));
        ApplyLanguage();
    }

    public override bool IsReady => _loaded;

    internal GridView Grid => _grid;

    internal IReadOnlyList<UserRow> Rows => _rows;

    internal bool ShowsPartialNote => _anyPartial;

    public override void OnActivated()
    {
        Context.Pump.Sampled -= OnSampled;
        Context.Pump.Sampled += OnSampled;
        if (!_loaded)
        {
            Run(LoadSessionsAsync);
        }
        else if (Context.Pump.Latest is not null)
        {
            Rebuild();
        }
    }

    public override void OnDeactivated() => Context.Pump.Sampled -= OnSampled;

    public override void RefreshNow()
    {
        Run(LoadSessionsAsync);
        _ = Context.Pump.TickAsync();
    }

    public override void ApplyLanguage()
    {
        _reload.Text = Strings.Get("gui.common.reload");
        _grid.AccessibleName = Strings.Get("gui.nav.users");
        string[] keys = ["user", "session", "state", "cpu", "memory", "count"];
        for (var i = 0; i < keys.Length && i < _grid.Columns.Count; i++)
        {
            _grid.Columns[i].Text = Strings.Get("gui.users.col." + keys[i]);
        }

        UpdateFooter();
        _grid.Invalidate();
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
        if (++_ticks % SessionRefreshEvery == 0 && !_busy)
        {
            Run(LoadSessionsAsync);
        }
        else
        {
            Rebuild();
        }
    }

    private async Task LoadSessionsAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            _sessions = await Context.Services.Sessions.ListAsync().ConfigureAwait(true);
            _loaded = true;
        }
        finally
        {
            _busy = false;
        }

        Rebuild();
    }

    /// <summary>Builds the session totals and the rows of the expanded sessions from the latest snapshot.</summary>
    internal static List<UserRow> BuildRows(IReadOnlyList<SessionInfo> sessions, IReadOnlyList<ProcessInfo> processes, ISet<uint> expanded, out bool anyPartial)
    {
        var bySession = processes.GroupBy(p => p.SessionId).ToDictionary(g => g.Key, g => g.ToList());
        var all = sessions.ToList();
        foreach (var id in bySession.Keys.Where(id => all.All(s => s.SessionId != id)).Order())
        {
            all.Add(new SessionInfo(id, string.Empty, SessionConnectState.Other, null));
        }

        anyPartial = false;
        var rows = new List<UserRow>();
        foreach (var session in all.OrderBy(s => s.SessionId))
        {
            bySession.TryGetValue(session.SessionId, out var list);
            list ??= [];
            var cpu = 0.0;
            var cpuPartial = false;
            long memory = 0;
            foreach (var p in list)
            {
                if (p.Key.Pid == 0)
                {
                    // "System Idle Process" is the CPU time nobody used: it is not the system's load, so it is not added.
                    continue;
                }

                if (p.CpuPercent.TryGet(out var c))
                {
                    cpu += c;
                }
                else
                {
                    cpuPartial = true;
                }

                memory += p.PrivateWorkingSetBytes;
            }

            // A session that has a user but no process we can read: the numbers are not "zero", they are not visible.
            var hasUser = session.UserName.Length > 0;
            var visible = list.Count > 0;
            if (hasUser && (!visible || cpuPartial))
            {
                anyPartial = true;
            }

            rows.Add(new UserRow(session, null, list.Count, cpu, cpuPartial, memory, visible));
            if (expanded.Contains(session.SessionId))
            {
                foreach (var p in list.OrderByDescending(p => p.CpuPercent.GetOr(-1)).ThenByDescending(p => p.PrivateWorkingSetBytes).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    rows.Add(new UserRow(session, p, 0, p.CpuPercent.GetOr(0), !p.CpuPercent.IsOk, p.PrivateWorkingSetBytes, true));
                }
            }
        }

        return rows;
    }

    private void Rebuild()
    {
        if (!IsHandleCreated || Context.Pump.Latest is not { } latest)
        {
            return;
        }

        _rows.Clear();
        _rows.AddRange(BuildRows(_sessions, latest.Processes, _expanded, out _anyPartial));
        _updating = true;
        try
        {
            if (_grid.VirtualListSize != _rows.Count)
            {
                _grid.VirtualListSize = _rows.Count;
            }

            if (_selectedKey is { } key)
            {
                var index = _rows.FindIndex(r => KeyOf(r) == key);
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
    }

    private static string KeyOf(UserRow row) =>
        row.Process is { } p ? "p:" + row.Session.SessionId + ":" + p.Key.Pid + ":" + p.Key.CreateTime : "s:" + row.Session.SessionId;

    private UserRow? Selected => _grid.SelectedRow is >= 0 and var row && row < _rows.Count ? _rows[row] : null;

    private void UpdateFooter()
    {
        var sessions = _rows.Count(r => r.IsSession);
        _count.Text = Strings.Format("gui.users.count", Fmt.Dict(("count", sessions.ToString(CultureInfo.CurrentCulture))));
        _partial.SetTone(CellTone.Accent);
        _partial.Text = _anyPartial ? Strings.Get("gui.users.partial") : string.Empty;
        _partial.Margin = new Padding(0, 0, _anyPartial ? Px(16) : 0, 0);
        _hint.SetTone(CellTone.Dim);
        _hint.Text = Strings.Get("gui.users.hint") + (_rows.Any(r => r.IsSession && r.Visible && r.CpuPartial) ? " " + Strings.Get("gui.users.asterisk") : string.Empty);
    }

    private void Toggle(int row)
    {
        if (row < 0 || row >= _rows.Count || !_rows[row].IsSession || _rows[row].ProcessCount == 0)
        {
            return;
        }

        var id = _rows[row].Session.SessionId;
        if (!_expanded.Remove(id))
        {
            _expanded.Add(id);
        }

        Rebuild();
    }

    private void OnGridKey(object? sender, KeyEventArgs e)
    {
        if (e.Modifiers != Keys.None || Selected is not { } row)
        {
            return;
        }

        var id = row.Session.SessionId;
        if ((e.KeyCode == Keys.Right && row.IsSession && !_expanded.Contains(id)) || (e.KeyCode == Keys.Left && row.IsSession && _expanded.Contains(id)))
        {
            Toggle(_grid.SelectedRow);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            Open();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    /// <summary>Enter or double-click: a session opens or closes, a process opens on the Processes page.</summary>
    internal void Open()
    {
        if (Selected is not { } row)
        {
            return;
        }

        if (row.Process is { } p)
        {
            Context.Navigate("processes", p.Key);
        }
        else
        {
            Toggle(_grid.SelectedRow);
        }
    }

    internal static string StateKey(SessionConnectState state) => "gui.users.state." + state.ToString().ToLowerInvariant();

    private GridCell Cell(int row, int column)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return default;
        }

        var r = _rows[row];
        if (r.Process is { } p)
        {
            return column switch
            {
                UserColumn => new GridCell(p.Name) { Indent = 1 },
                SessionColumn => new GridCell(p.Key.Pid.ToString(CultureInfo.CurrentCulture), CellTone.Dim),
                CpuColumn => p.CpuPercent.TryGet(out var cpu) ? new GridCell(cpu.ToString("0.0", CultureInfo.CurrentCulture) + " %") : new GridCell(Fmt.Dash, CellTone.Dim),
                MemoryColumn => new GridCell(Fmt.Bytes(p.PrivateWorkingSetBytes)),
                _ => new GridCell(string.Empty),
            };
        }

        var name = r.Session.UserName.Length > 0 ? r.Session.UserName : Strings.Get("gui.users.system");
        return column switch
        {
            UserColumn => new GridCell(name)
            {
                Bold = true,
                Glyph = r.ProcessCount == 0 ? ExpanderGlyph.None : _expanded.Contains(r.Session.SessionId) ? ExpanderGlyph.Expanded : ExpanderGlyph.Collapsed,
            },
            SessionColumn => new GridCell(r.Session.SessionId.ToString(CultureInfo.CurrentCulture) + (r.Session.SessionId == _ownSession ? " " + Strings.Get("gui.users.you") : string.Empty)),
            StateColumn => new GridCell(Strings.Get(r.Session.UserName.Length == 0 ? "gui.users.state.system" : StateKey(r.Session.State)), r.Session.State == SessionConnectState.Active ? CellTone.Normal : CellTone.Dim),
            CpuColumn => !r.Visible
                ? new GridCell(Fmt.Dash, CellTone.Dim)
                : new GridCell(r.Cpu.ToString("0.0", CultureInfo.CurrentCulture) + " %" + (r.CpuPartial ? " *" : string.Empty), r.Cpu >= 50 ? CellTone.Accent : CellTone.Normal),
            MemoryColumn => r.Visible ? new GridCell(Fmt.Bytes(r.MemoryBytes)) : new GridCell(Fmt.Dash, CellTone.Dim),
            CountColumn => r.Visible ? new GridCell(r.ProcessCount.ToString(CultureInfo.CurrentCulture)) : new GridCell(Fmt.Dash, CellTone.Dim),
            _ => default,
        };
    }

    private string? CellTip(int row, int column)
    {
        if (row < 0 || row >= _rows.Count || !_rows[row].IsSession)
        {
            return null;
        }

        var r = _rows[row];
        if (!r.Visible && r.Session.UserName.Length > 0)
        {
            return Strings.Get("gui.users.invisible.tip");
        }

        return column == CpuColumn && r.CpuPartial ? Strings.Get("gui.users.cpupartial.tip") : null;
    }
}

internal static class UsersStrings
{
    public static readonly StringEntry[] All =
    [
        new("gui.users.col.user", "ユーザー", "User"),
        new("gui.users.col.session", "セッション", "Session"),
        new("gui.users.col.state", "状態", "Status"),
        new("gui.users.col.cpu", "CPU", "CPU"),
        new("gui.users.col.memory", "メモリ", "Memory"),
        new("gui.users.col.count", "プロセス数", "Processes"),
        new("gui.users.system", "システム（ユーザーなし）", "System (no user)"),
        new("gui.users.you", "（自分）", "(you)"),
        new("gui.users.count", "{count} 件のセッション", "{count} sessions"),
        new("gui.users.hint", "行を開くと、そのセッションのプロセスが見えます。", "Open a row to see that session's processes."),
        new("gui.users.partial", "一部のみ表示: ほかのユーザーのプロセスは、管理者でないと見えないことがあります。", "Partial only: other users' processes may be hidden unless you are an administrator."),
        new("gui.users.invisible.tip", "このユーザーのプロセスは見えません（権限がないためです）。0 ではありません", "This user's processes are not visible (no permission). It is not zero"),
        new("gui.users.cpupartial.tip", "一部のプロセスの CPU を取得できなかったため、合計は足りない可能性があります", "The CPU of some processes could not be read, so the total may be short"),
        new("gui.users.asterisk", "* は読めなかった分があります。", "* means some values could not be read."),
        new("gui.users.state.system", "システム用", "For the system"),
        new("gui.users.state.active", "使用中", "Active"),
        new("gui.users.state.connected", "接続中", "Connected"),
        new("gui.users.state.disconnected", "切断", "Disconnected"),
        new("gui.users.state.idle", "待機中", "Idle"),
        new("gui.users.state.other", "その他", "Other"),
    ];
}
