using PcMieru.Checks;

namespace PcMieru.Gui;

/// <summary>Where the list of the history stands.</summary>
internal enum HistoryView
{
    /// <summary>Not asked yet (the tab was never shown).</summary>
    NotLoaded,

    Loading,

    /// <summary>Read, and there is nothing in it (the folder is not there, or holds no run).</summary>
    Empty,

    List,

    /// <summary>The folder could not be opened.</summary>
    Failed,
}

/// <summary>
/// The history: the saved runs, newest first (date, kind, verdict, outcome), and under the list the values of the one that is selected.
/// It only reads, on a worker, through the read-only history: an absent folder is the empty state and nothing is created by looking.
/// The list is read when the tab is shown and again when a run ends; a list that is being read never blocks the window. Files that could not
/// be listed are named in a notice above the list, they do not hide the ones that could.
/// </summary>
internal sealed class CheckHistoryTab : CheckTab
{
    private const int WhenColumn = 0;
    private const int KindColumn = 1;
    private const int VerdictColumn = 2;
    private const int OutcomeColumn = 3;

    private readonly Button _reload = new() { AutoSize = true, Name = "checksHistoryReload" };
    private readonly GridView _list = new() { Name = "checksHistoryList" };
    private readonly ToneLabel _notice = new() { AutoSize = false, Name = "checksHistoryNotice", Visible = false };
    private readonly ToneLabel _detailHead = new() { AutoSize = false, Name = "checksHistoryDetailHead" };
    private readonly CheckResultGrid _detail = new() { Name = "checksHistoryDetail" };
    private readonly Label _count = new() { AutoSize = true, Name = "checksHistoryCount" };
    private readonly ToneLabel _hint = new() { AutoSize = true, Name = "checksHistoryHint" };
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private readonly TableLayoutPanel _rows = new() { Name = "checksHistoryRows" };
    private readonly Panel _detailPanel = new() { Name = "checksHistoryDetailPanel" };

    private IReadOnlyList<CheckHistoryEntry> _entries = [];
    private IReadOnlyList<CheckHistoryWarning> _warnings = [];
    private HistoryView _view = HistoryView.NotLoaded;
    private string? _failure;
    private CancellationTokenSource? _loadCancel;
    private CancellationTokenSource? _detailCancel;
    private int _loadGeneration;
    private int _detailGeneration;
    private bool _stale;
    private bool _isShown;
    private bool _suppressSelection;
    private Guid? _selected;
    private DetailState _detailState = DetailState.None;
    private CheckResult? _detailResult;
    private string? _detailCode;
    private string? _detailError;

    public CheckHistoryTab(CheckController controller, Func<int, int> px)
        : base(controller, px, "checksHistory")
    {
        var pad = Px(12);
        _reload.MinimumSize = new Size(Px(88), Px(30));
        _reload.Click += (_, _) => Reload();
        _reload.TabIndex = 0;
        _list.TabIndex = 1;
        _detail.TabIndex = 2;

        PageLayout.Prepare(
            _list,
            Strings.Get("gui.chk.hist.list.name"),
            (Strings.Get("gui.chk.hist.col.when"), Px(190), HorizontalAlignment.Left),
            (Strings.Get("gui.chk.hist.col.kind"), Px(110), HorizontalAlignment.Left),
            (Strings.Get("gui.chk.hist.col.verdict"), Px(110), HorizontalAlignment.Left),
            (Strings.Get("gui.chk.hist.col.outcome"), Px(130), HorizontalAlignment.Left));
        _list.Cells = ListCell;
        _list.FillColumn = OutcomeColumn;
        _list.SizeChanged += (_, _) => _list.FitLastColumn();
        _list.SelectedIndexChanged += (_, _) => OnListSelection();
        _detail.PrepareColumns(Px, Strings.Get("gui.chk.table.name"));

        _notice.SetTone(CellTone.Accent);
        _notice.Dock = DockStyle.Top;
        _notice.Height = Px(24);
        _notice.Padding = new Padding(pad, 0, pad, 0);
        _notice.TextAlign = ContentAlignment.MiddleLeft;
        _notice.AutoEllipsis = true;

        _detailHead.SetTone(CellTone.Dim);
        _detailHead.Dock = DockStyle.Top;
        _detailHead.Height = Px(28);
        _detailHead.Padding = new Padding(pad, Px(4), pad, 0);
        _detailHead.TextAlign = ContentAlignment.MiddleLeft;
        _detailHead.AutoEllipsis = true;

        _detailPanel.Dock = DockStyle.Fill;
        _detailPanel.Margin = new Padding(0);
        _detailPanel.Controls.Add(_detail);
        _detailPanel.Controls.Add(_detailHead);

        _rows.Dock = DockStyle.Fill;
        _rows.ColumnCount = 1;
        _rows.RowCount = 2;
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _rows.RowStyles.Add(new System.Windows.Forms.RowStyle(SizeType.Percent, 38));
        _rows.RowStyles.Add(new System.Windows.Forms.RowStyle(SizeType.Percent, 62));
        _list.Margin = new Padding(0);
        _rows.Controls.Add(_list, 0, 0);
        _rows.Controls.Add(_detailPanel, 0, 1);

        // The list ends on a whole row: its share of the height is a header and N rows as the list measures them (not 38% of whatever the
        // window has, which left half a row under the last one, most of all at 150%). The detail below takes the rest.
        _rows.SizeChanged += (_, _) => FitListRows();
        // Not in the call that makes the handle: the native list has no columns or rows yet then, so it is asked once that is over.
        _list.HandleCreated += (_, _) => _list.BeginInvoke(FitListRows);
        _list.ColumnsRescaled += (_, _) =>
        {
            FitListRows();
            if (_list.IsHandleCreated)
            {
                _list.BeginInvoke(FitListRows);
            }
        };

        _count.Margin = new Padding(0, 0, Px(16), 0);
        var bar = PageLayout.Bar("checksHistoryBar", pad, [_reload], []);
        Controls.Add(_rows);
        Controls.Add(PageLayout.Footer("checksHistoryFooter", pad, _count, _hint));
        Controls.Add(_notice);
        Controls.Add(bar);
        ApplyLanguage();
    }

    /// <summary>The fewest runs the list shows when the window is low.</summary>
    private const int MinListRows = 3;

    /// <summary>How tall the list may be, as a share of the area it and the detail divide: about as much as before.</summary>
    private const int ListSharePercent = 38;

    /// <summary>The height of the list row of the page: the header and a whole number of rows, as the list measures them now.</summary>
    internal int WholeRowsHeight()
    {
        var available = _rows.ClientSize.Height;
        var rows = Math.Max(_list.RowsThatFit(available * ListSharePercent / 100), MinListRows);
        return _list.HeightForRows(rows);
    }

    private void FitListRows()
    {
        if (_rows.ClientSize.Height <= 0 || _rows.RowStyles.Count != 2 || _list.IsDisposed)
        {
            return;
        }

        var height = WholeRowsHeight();
        var first = _rows.RowStyles[0];
        if (first.SizeType != SizeType.Absolute || Math.Abs(first.Height - height) > 0.5f)
        {
            first.SizeType = SizeType.Absolute;
            first.Height = height;
            var second = _rows.RowStyles[1];
            second.SizeType = SizeType.Percent;
            second.Height = 100;
        }
    }

    private enum DetailState
    {
        None,
        Opening,
        Found,
        NotFound,
        Unreadable,
        Failed,
    }

    internal Button ReloadButton => _reload;

    internal GridView List => _list;

    internal CheckResultGrid Detail => _detail;

    internal ToneLabel DetailHead => _detailHead;

    internal ToneLabel Notice => _notice;

    internal Label CountLabel => _count;

    internal HistoryView View => _view;

    internal IReadOnlyList<CheckHistoryEntry> Entries => _entries;

    /// <summary>The latest read of the list. A test awaits it; the window never does.</summary>
    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>The latest open of one run. A test awaits it; the window never does.</summary>
    internal Task DetailTask { get; private set; } = Task.CompletedTask;

    /// <summary>The run on show under the list (null: none).</summary>
    internal Guid? SelectedRunId => _selected;

    public override bool IsReady => _view != HistoryView.Loading;

    /// <summary>The text of one cell of the list, as it is drawn.</summary>
    internal string ListText(int row, int column) => ListCell(row, column).Text;

    /// <summary>Selects a row of the list, as a click would (the values of that run are opened).</summary>
    internal void SelectEntry(int row) => _list.SelectRow(row);

    public override void OnShown()
    {
        _isShown = true;
        if (_view == HistoryView.NotLoaded || _stale)
        {
            StartLoad();
        }
    }

    public override void OnHidden() => _isShown = false;

    public override void Reload() => StartLoad();

    public override void RefreshState()
    {
        // The list changes only when a run is saved; nothing here follows the controller's progress.
    }

    /// <summary>A run ended: its file may be new. The list is read again when it is on screen, or when it next is.</summary>
    internal void OnRunFinished()
    {
        _stale = true;
        if (_isShown)
        {
            StartLoad();
        }
    }

    public override void ApplyLanguage()
    {
        _reload.Text = Strings.Get("gui.chk.hist.reload");
        if (_list.Columns.Count == OutcomeColumn + 1)
        {
            _list.Columns[WhenColumn].Text = Strings.Get("gui.chk.hist.col.when");
            _list.Columns[KindColumn].Text = Strings.Get("gui.chk.hist.col.kind");
            _list.Columns[VerdictColumn].Text = Strings.Get("gui.chk.hist.col.verdict");
            _list.Columns[OutcomeColumn].Text = Strings.Get("gui.chk.hist.col.outcome");
        }

        _list.AccessibleName = Strings.Get("gui.chk.hist.list.name");
        _detail.AccessibleName = Strings.Get("gui.chk.table.name");
        _detail.ApplyLanguage();
        _list.Invalidate();
        RenderView();
        RenderDetail();
    }

    public override void Release()
    {
        _loadGeneration++;
        _detailGeneration++;
        Cancel(ref _loadCancel);
        Cancel(ref _detailCancel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Release();
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void Cancel(ref CancellationTokenSource? source)
    {
        var old = source;
        source = null;
        if (old is null)
        {
            return;
        }

        try
        {
            old.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        old.Dispose();
    }

    // ---- the list ----

    private void StartLoad()
    {
        Cancel(ref _loadCancel);
        var cancel = new CancellationTokenSource();
        _loadCancel = cancel;
        var generation = ++_loadGeneration;
        _stale = false;
        _view = HistoryView.Loading;
        _failure = null;
        RenderView();
        LoadTask = LoadAsync(generation, cancel.Token);
    }

    private async Task LoadAsync(int generation, CancellationToken token)
    {
        CheckHistoryPage? page = null;
        string? failure = null;
        try
        {
            var history = Controller.History;
            page = await Task.Run(() => history.ListAsync(new CheckHistoryQuery(null, null), token), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = Describe(ex);
        }

        if (generation != _loadGeneration || IsDisposed)
        {
            return;
        }

        if (page is null)
        {
            _entries = [];
            _warnings = [];
            _view = HistoryView.Failed;
            _failure = failure ?? string.Empty;
        }
        else
        {
            _entries = page.Entries;
            _warnings = page.Warnings;
            _view = page.Entries.Count == 0 ? HistoryView.Empty : HistoryView.List;
        }

        ShowList();
    }

    /// <summary>What the reader said, as the type and the HRESULT: a path or a user name is never in the text that reaches the screen.</summary>
    private static string Describe(Exception ex)
    {
        var inner = ex is CheckHistoryFolderException && ex.InnerException is { } cause ? cause : ex;
        return inner.GetType().Name + " 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void ShowList()
    {
        _suppressSelection = true;
        try
        {
            _list.SelectedIndices.Clear();
            _list.VirtualListSize = _entries.Count;
            var keep = _selected is { } id ? IndexOf(id) : -1;
            if (keep >= 0)
            {
                _list.SelectRow(keep, scroll: false);
            }
        }
        finally
        {
            _suppressSelection = false;
        }

        // The list measures its rows from the rows it holds: its share of the page is made again now that it has them (and once more when the
        // native list has laid them out).
        FitListRows();
        if (_list.IsHandleCreated)
        {
            _list.BeginInvoke(FitListRows);
        }

        _list.Invalidate();
        RenderView();
        if (_selected is { } selected && IndexOf(selected) < 0)
        {
            // The run that was open is not in the list any more (it was removed): its values go with it.
            ClearDetail();
        }
        else if (_selected is null && _detailState == DetailState.None)
        {
            RenderDetail();
        }
    }

    private int IndexOf(Guid runId)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].RunId == runId)
            {
                return i;
            }
        }

        return -1;
    }

    private void RenderView()
    {
        _count.Text = _view switch
        {
            HistoryView.Loading => Strings.Get("gui.chk.hist.loading"),
            HistoryView.Empty => Strings.Get("gui.chk.hist.empty"),
            HistoryView.List => Strings.Format("gui.chk.hist.count", Fmt.Dict(("count", _entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
            _ => string.Empty,
        };

        string? notice = null;
        if (_view == HistoryView.Failed)
        {
            notice = Strings.Format("gui.chk.hist.folderfail", Fmt.Dict(("error", _failure ?? string.Empty)));
        }
        else if (_warnings.Count > 0)
        {
            notice = _warnings.Select(CheckFormat.WarningText).Aggregate(CheckFormat.Pair);
        }

        _notice.Text = notice ?? string.Empty;
        _notice.Visible = notice is not null;
        _tips.SetToolTip(_notice, notice ?? string.Empty);
    }

    private GridCell ListCell(int row, int column)
    {
        if (row < 0 || row >= _entries.Count)
        {
            return new GridCell(string.Empty);
        }

        var entry = _entries[row];
        return column switch
        {
            WhenColumn => new GridCell(CheckFormat.LocalTime(entry.EndedAtUtc)),
            KindColumn => new GridCell(CheckFormat.KindName(entry.Kind)),
            VerdictColumn => new GridCell(CheckFormat.VerdictWord(entry.Verdict), CheckFormat.Tone(entry.Verdict)) { Bold = entry.Verdict == CheckVerdict.Check },
            _ => new GridCell(CheckFormat.OutcomeName(entry.Outcome)),
        };
    }

    // ---- one run ----

    private void OnListSelection()
    {
        if (_suppressSelection)
        {
            return;
        }

        var row = _list.SelectedRow;
        if (row < 0 || row >= _entries.Count)
        {
            return;
        }

        var id = _entries[row].RunId;
        if (_selected == id && _detailState is DetailState.Found or DetailState.Opening)
        {
            return;
        }

        OpenDetail(id);
    }

    private void OpenDetail(Guid runId)
    {
        Cancel(ref _detailCancel);
        var cancel = new CancellationTokenSource();
        _detailCancel = cancel;
        var generation = ++_detailGeneration;
        _selected = runId;
        _detailState = DetailState.Opening;
        _detailResult = null;
        _detail.Show(null);
        RenderDetail();
        DetailTask = OpenAsync(runId, generation, cancel.Token);
    }

    private async Task OpenAsync(Guid runId, int generation, CancellationToken token)
    {
        CheckHistoryGetResult? answer = null;
        string? failure = null;
        try
        {
            var history = Controller.History;
            answer = await Task.Run(() => history.GetAsync(runId, token), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = Describe(ex);
        }

        if (generation != _detailGeneration || IsDisposed)
        {
            return;
        }

        _detailResult = null;
        _detailCode = null;
        _detailError = failure;
        _detailState = answer is null
            ? DetailState.Failed
            : answer.Status switch
            {
                CheckHistoryGetStatus.Found => DetailState.Found,
                CheckHistoryGetStatus.Unreadable => DetailState.Unreadable,
                _ => DetailState.NotFound,
            };
        if (answer is { Status: CheckHistoryGetStatus.Found })
        {
            _detailResult = answer.Result;
        }
        else if (answer is { Status: CheckHistoryGetStatus.Unreadable })
        {
            _detailCode = answer.WarningCode;
        }

        _detail.Show(_detailResult);
        RenderDetail();
    }

    private void ClearDetail()
    {
        Cancel(ref _detailCancel);
        _detailGeneration++;
        _selected = null;
        _detailState = DetailState.None;
        _detailResult = null;
        _detail.Show(null);
        RenderDetail();
    }

    private void RenderDetail()
    {
        var text = _detailState switch
        {
            DetailState.Opening => Strings.Get("gui.chk.hist.opening"),
            DetailState.Found when _detailResult is { } result => CheckFormat.Pair(CheckFormat.KindName(result.Kind), CheckFormat.ResultLine(result)),
            DetailState.NotFound => Strings.Get("gui.chk.hist.notfound"),
            DetailState.Unreadable => Strings.Format("gui.chk.hist.unreadable", Fmt.Dict(("reason", CheckFormat.WarningOneText(_detailCode)))),
            DetailState.Failed => Strings.Format("gui.chk.hist.folderfail", Fmt.Dict(("error", _detailError ?? string.Empty))),
            _ => _view == HistoryView.List ? Strings.Get("gui.chk.hist.pick") : string.Empty,
        };
        _detailHead.Text = text;
        _tips.SetToolTip(_detailHead, text);
    }
}
