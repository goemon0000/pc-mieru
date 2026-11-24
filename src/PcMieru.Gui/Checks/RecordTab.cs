using System.Globalization;
using PcMieru.Checks;

namespace PcMieru.Gui;

/// <summary>What the lower half of the recording tab shows.</summary>
internal enum RecordDetailState
{
    None,

    /// <summary>The run is being read (its CSV is counted on a worker).</summary>
    Opening,

    /// <summary>The selected run is the one being recorded: there is nothing to read yet, the summary appears when it stops.</summary>
    Running,

    Found,

    NotFound,

    /// <summary>The run could not be read (malformed, too big, the disk said no).</summary>
    Unreadable,
}

/// <summary>
/// The recording tab (DESIGN-B §2, §6, §7, §13): start, mark, stop, the list of recordings, the summary of the selected one, the CSV, delete, and the
/// old records of the earlier app. The recording itself is the window's (<see cref="RecordingHub"/>), not this tab's: it goes on when the tab, the page or
/// the window changes, and nothing here stops it (<see cref="OnHidden"/> only notes that the tab is out of sight). It is independent of the checks:
/// no mark is set by a check, and a check does not touch the recording.
/// Nothing is read or made until the tab is first shown (the list is read then, on a worker, through the hub's store) or a recording is started
/// (the hub makes the recorder and its folder). Building the tab, and changing its language, read nothing.
/// </summary>
internal sealed class RecordTab : CheckTab
{
    private const int StartColumn = 0;
    private const int LengthColumn = 1;
    private const int StatusColumn = 2;
    private const int NoteColumn = 3;

    /// <summary>The fewest recordings the list shows by default when the window is low (the person can drag the bar for fewer).</summary>
    private const int MinListRows = 2;

    /// <summary>How tall the list is by default, as a share of the area it and the summary divide: the summary, which is what the page is for, gets the rest (well over half).</summary>
    private const int DefaultListShare = 24;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly GuiContext _context;
    private readonly RecordingHub _hub;
    private readonly Button _start = new() { AutoSize = true, Name = "recordStart" };
    private readonly Button _mark = new() { AutoSize = true, Name = "recordMark" };
    private readonly Button _stop = new() { AutoSize = true, Name = "recordStop" };
    private readonly Button _openFolder = new() { AutoSize = true, Name = "recordOpenFolder" };
    private readonly ToggleButton _legacy = new() { Name = "recordLegacy" };
    private readonly ContextMenuStrip _markMenu = new() { Name = "recordMarkMenu" };
    private readonly List<ToolStripMenuItem> _markItems = [];
    private readonly WrapLabel _state = new() { Name = "recordState" };
    private readonly ToneLabel _folder = new() { AutoSize = true, Name = "recordFolder" };
    private readonly WrapLabel _notice = new() { Name = "recordNotice", Visible = false };
    private readonly GridView _list = new() { Name = "recordList" };
    private readonly WrapLabel _detailHead = new() { Name = "recordDetailHead" };
    private readonly RecordDetailHeader _detailHeader;
    private readonly RunSummaryGrid _summary = new() { Name = "recordSummary" };
    private readonly ToggleButton _how = new() { Name = "recordHow" };
    private readonly Button _csv = new() { AutoSize = true, Name = "recordCsv" };
    private readonly Button _delete = new() { AutoSize = true, Name = "recordDelete" };
    private readonly Label _count = new() { AutoSize = true, Name = "recordCount" };
    private readonly ToneLabel _hint = new() { AutoSize = true, Name = "recordHint" };
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private readonly Panel _body = new() { Name = "recordBody" };
    private readonly SplitBar _split = new() { Name = "recordSplit" };
    private readonly Panel _detailPanel = new() { Name = "recordDetailPanel" };

    private IReadOnlyList<RunInfo> _all = [];
    private IReadOnlyList<RunInfo> _runs = [];
    private int _problemCount;
    private HistoryView _view = HistoryView.NotLoaded;
    private string? _failure;
    private bool? _folderPresent;
    private CancellationTokenSource? _loadCancel;
    private CancellationTokenSource? _detailCancel;
    private int _loadGeneration;
    private int _detailGeneration;
    private bool _stale;
    private bool _isShown;
    private bool _suppressSelection;
    private Guid? _selected;
    private Guid? _selectAfterLoad;
    private RunInfo? _detailInfo;
    private RecordDetailState _detailState = RecordDetailState.None;
    private string? _detailError;
    private string? _actionNote;
    private bool _deleting;
    private bool _fittingColumns;
    private RecordingPhase _lastPhase = RecordingPhase.Idle;

    /// <summary>The share of the body the list takes once the person has moved the bar (a percentage, kept for this window's session only: nothing is saved to gui.ini). Null: the default.</summary>
    private int? _listShare;

    /// <summary>How long the run had been going when it was told to stop: the list's row shows that while the run is being saved, instead of a clock that goes on.</summary>
    private TimeSpan _frozenElapsed;

    public RecordTab(GuiContext context, Func<int, int> px)
        : base(context.Checks, px, "checksRecord")
    {
        _context = context;
        _hub = context.Recording;
        var pad = Px(12);

        _start.MinimumSize = new Size(Px(88), Px(30));
        _stop.MinimumSize = new Size(Px(88), Px(30));
        _mark.MinimumSize = new Size(Px(88), Px(30));
        _openFolder.MinimumSize = new Size(Px(88), Px(30));
        _legacy.MinimumSize = new Size(Px(88), Px(30));
        _legacy.AccessibleRole = AccessibleRole.CheckButton;
        _how.MinimumSize = new Size(Px(88), Px(30));
        _how.AccessibleRole = AccessibleRole.CheckButton;
        _csv.MinimumSize = new Size(Px(88), Px(30));
        _delete.MinimumSize = new Size(Px(88), Px(30));
        _start.TabIndex = 0;
        _mark.TabIndex = 1;
        _stop.TabIndex = 2;
        _openFolder.TabIndex = 3;
        _legacy.TabIndex = 4;
        _list.TabIndex = 5;
        _split.TabIndex = 6;
        _how.TabIndex = 6;
        _csv.TabIndex = 7;
        _delete.TabIndex = 8;
        _summary.TabIndex = 9;

        _start.Click += (_, _) => _ = PressStart();
        _stop.Click += (_, _) => _ = PressStop();
        _mark.Click += (_, _) => ShowMarkMenu();
        _openFolder.Click += (_, _) => _ = PressOpenFolder();
        _legacy.Click += (_, _) => ToggleLegacy();
        _how.Click += (_, _) => ToggleHow();
        _csv.Click += (_, _) => _ = PressOpenCsv();
        _delete.Click += (_, _) => _ = PressDelete();

        BuildMarkMenu();

        PageLayout.Prepare(
            _list,
            Strings.Get("gui.rtab.list.name"),
            (Strings.Get("gui.rtab.col.start"), Px(170), HorizontalAlignment.Left),
            (Strings.Get("gui.rtab.col.length"), Px(110), HorizontalAlignment.Left),
            (Strings.Get("gui.rtab.col.status"), Px(90), HorizontalAlignment.Left),
            (Strings.Get("gui.rtab.col.note"), Px(260), HorizontalAlignment.Left));
        _list.Dock = DockStyle.Top;
        _list.RowPadding = 10;
        _list.Cells = ListCell;
        _list.ScrollsBackWhenWhole = true;
        _list.CellTip = ListTip;
        _list.FillColumn = NoteColumn;
        _list.SizeChanged += (_, _) =>
        {
            _list.FitLastColumn();

            // A list that got shorter (the window, the bar) must not scroll the selected record out of sight: the person chose it and is looking for it.
            _list.EnsureSelectedVisible();
        };
        _list.SelectedIndexChanged += (_, _) => OnListSelection();
        _summary.PrepareColumns(Px, Strings.Get("gui.rtab.sum.name"));

        StyleLine(_state, pad, CellTone.Dim);
        StyleLine(_notice, pad, CellTone.Accent);
        StyleLine(_detailHead, 0, CellTone.Dim);
        _folder.Margin = new Padding(0, 0, 0, 0);

        // The line above the summary (what the run is, the buttons) is one block that is as tall as it needs; the summary fills the rest (the note on the
        // sustained value is a row of its table). The list and the summary are divided by a bar the person can move: the list ends on a whole row, as the history's does.
        _detailHeader = new RecordDetailHeader(_detailHead, pad, _how, _csv, _delete);
        _detailPanel.Dock = DockStyle.Fill;
        _detailPanel.Margin = new Padding(0);
        _detailPanel.Controls.Add(_summary);
        _detailPanel.Controls.Add(_detailHeader);

        _split.Height = Px(14);
        _split.Moved += OnSplitMoved;
        _body.Dock = DockStyle.Fill;
        _body.Controls.Add(_detailPanel);
        _body.Controls.Add(_split);
        _body.Controls.Add(_list);

        _body.SizeChanged += (_, _) => FitBody();
        _list.HandleCreated += (_, _) => _list.BeginInvoke(() =>
        {
            FitListColumns();
            FitBody();
        });
        _list.ColumnsRescaled += (_, _) =>
        {
            FitListColumns();
            FitBody();
            if (_list.IsHandleCreated)
            {
                _list.BeginInvoke(FitBody);
            }
        };

        _count.Margin = new Padding(0, 0, Px(16), 0);
        var bar = PageLayout.Bar("recordBar", pad, [_start, _mark, _stop, _openFolder, _legacy], []);
        Controls.Add(_body);
        Controls.Add(PageLayout.Footer("recordFooter", pad, _count, _folder, _hint));
        Controls.Add(_notice);
        Controls.Add(_state);
        Controls.Add(bar);

        _hub.Changed += OnHubChanged;
        _hub.RunEnded += OnRunEnded;
        _lastPhase = _hub.Phase;
        ApplyLanguage();
    }

    // ---- what a test or the smoke run reaches ----

    internal Button StartButton => _start;

    internal Button StopButton => _stop;

    internal Button MarkButton => _mark;

    internal Button OpenFolderButton => _openFolder;

    internal ToggleButton LegacyToggle => _legacy;

    internal ToggleButton HowToggle => _how;

    internal Button CsvButton => _csv;

    internal Button DeleteButton => _delete;

    internal GridView List => _list;

    internal RunSummaryGrid Summary => _summary;

    internal ToneLabel StateLabel => _state;

    internal SplitBar Splitter => _split;

    internal Panel Body => _body;

    internal Panel DetailPanel => _detailPanel;

    internal RecordDetailHeader DetailHeader => _detailHeader;

    internal ToneLabel FolderLabel => _folder;

    internal ToneLabel Notice => _notice;

    internal ToneLabel DetailHead => _detailHead;

    internal Label CountLabel => _count;

    internal ToneLabel HintLabel => _hint;

    internal ContextMenuStrip MarkMenu => _markMenu;

    internal IReadOnlyList<ToolStripMenuItem> MarkItems => _markItems;

    internal HistoryView View => _view;

    /// <summary>The runs the list shows (the old records only when asked for).</summary>
    internal IReadOnlyList<RunInfo> Runs => _runs;

    /// <summary>The latest read of the list. A test awaits it; the window never does.</summary>
    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>The latest open of one run. A test awaits it; the window never does.</summary>
    internal Task DetailTask { get; private set; } = Task.CompletedTask;

    /// <summary>The latest start, stop, mark or delete. A test awaits it; the window never does.</summary>
    internal Task ActionTask { get; private set; } = Task.CompletedTask;

    internal Guid? SelectedRunId => _selected;

    internal RecordDetailState DetailState => _detailState;

    public override bool IsReady => _view != HistoryView.Loading && _detailState != RecordDetailState.Opening;

    /// <summary>The text of one cell of the list, as it is drawn.</summary>
    internal string ListText(int row, int column) => ListCell(row, column).Text;

    /// <summary>Selects a row of the list, as a click would (the run is opened).</summary>
    internal void SelectRun(int row) => _list.SelectRow(row);

    // ---- the tab's life ----

    public override void OnShown()
    {
        _isShown = true;
        RenderAll();
        if (_view == HistoryView.NotLoaded || _stale)
        {
            StartLoad();
        }
    }

    /// <summary>The tab (or the page, or the window) went out of sight. The recording is not touched: it goes on.</summary>
    public override void OnHidden() => _isShown = false;

    public override void Reload() => StartLoad();

    public override void RefreshState()
    {
        // A check's progress has nothing to do with the recording (DESIGN-B §13): nothing here follows it.
    }

    public override void ApplyLanguage()
    {
        _start.Text = Strings.Get("gui.rtab.start");
        _stop.Text = Strings.Get("gui.rtab.stop");
        _mark.Text = Strings.Get("gui.rtab.mark") + " ▾";
        _openFolder.Text = Strings.Get("gui.rtab.openfolder");
        _legacy.Text = Strings.Get("gui.rtab.legacy");
        _how.Text = Strings.Get("gui.rtab.sum.sustained.how");
        _csv.Text = Strings.Get("gui.rtab.csv");
        _delete.Text = Strings.Get("gui.rtab.delete");
        _tips.SetToolTip(_mark, Strings.Get("gui.rtab.mark.tip"));
        _tips.SetToolTip(_legacy, Strings.Get("gui.rtab.legacy.tip"));
        foreach (var item in _markItems)
        {
            item.Text = Strings.Get(MarkTextKey((MarkerKind)item.Tag!));
        }

        if (_list.Columns.Count == NoteColumn + 1)
        {
            _list.Columns[StartColumn].Text = Strings.Get("gui.rtab.col.start");
            _list.Columns[LengthColumn].Text = Strings.Get("gui.rtab.col.length");
            _list.Columns[StatusColumn].Text = Strings.Get("gui.rtab.col.status");
            _list.Columns[NoteColumn].Text = Strings.Get("gui.rtab.col.note");
        }

        _list.AccessibleName = Strings.Get("gui.rtab.list.name");
        _split.AccessibleName = Strings.Get("gui.rtab.split.name");
        _summary.AccessibleName = Strings.Get("gui.rtab.sum.name");
        _summary.ApplyLanguage();
        _list.Invalidate();
        RenderAll();
        FitListColumns();
        FitBody();
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
            _hub.Changed -= OnHubChanged;
            _hub.RunEnded -= OnRunEnded;
            Release();
            _tips.Dispose();
            _markMenu.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>A line of the page that wraps instead of ending in an ellipsis: its height follows its text (see <see cref="WrapLabel"/>).</summary>
    private static void StyleLine(WrapLabel label, int pad, CellTone tone)
    {
        label.SetTone(tone);
        label.Padding = new Padding(pad, label.LogicalToDeviceUnits(4), pad, label.LogicalToDeviceUnits(4));
        label.TextAlign = ContentAlignment.TopLeft;
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

    // ---- the buttons ----

    private static string MarkTextKey(MarkerKind kind) => kind switch
    {
        MarkerKind.BenchStart => "gui.rtab.mark.benchstart",
        MarkerKind.BenchEnd => "gui.rtab.mark.benchend",
        MarkerKind.StressStart => "gui.rtab.mark.loadstart",
        MarkerKind.StressEnd => "gui.rtab.mark.loadend",
        _ => "gui.rtab.mark.plain",
    };

    private void BuildMarkMenu()
    {
        foreach (var kind in new[] { MarkerKind.BenchStart, MarkerKind.BenchEnd, MarkerKind.StressStart, MarkerKind.StressEnd, MarkerKind.Mark })
        {
            var item = new ToolStripMenuItem { Tag = kind, Name = "recordMark." + kind };
            var chosen = kind;
            item.Click += (_, _) => _ = PressMark(chosen);
            _markItems.Add(item);
            _markMenu.Items.Add(item);
        }

        _markMenu.Opening += (_, _) =>
        {
            foreach (var item in _markItems)
            {
                item.Enabled = _hub.Phase == RecordingPhase.Recording;
            }

            _context.Theme.StyleMenu(_markMenu);
        };
    }

    private void ShowMarkMenu()
    {
        if (_hub.Phase == RecordingPhase.Recording)
        {
            _markMenu.Show(_mark, new Point(0, _mark.Height));
        }
    }

    /// <summary>The start button.</summary>
    internal Task PressStart() => Track(async () => await _hub.StartAsync().ConfigureAwait(true));

    /// <summary>The stop button: the recording is stopped and saved (this tab's list shows it when that is done).</summary>
    internal Task PressStop() => Track(async () => await _hub.StopAsync().ConfigureAwait(true));

    /// <summary>A choice of the mark menu.</summary>
    internal Task PressMark(MarkerKind kind) => Track(async () => await _hub.MarkAsync(kind).ConfigureAwait(true));

    /// <summary>The "open folder" button. A folder that is not there yet is said so (it is made by the first recording, not by this). The disk is asked off the UI thread.</summary>
    internal Task PressOpenFolder() => Track(OpenFolderAsync);

    private async Task OpenFolderAsync()
    {
        _actionNote = null;
        var path = _hub.Folder;
        var exists = _hub.FolderExists;
        var present = await Task.Run(() => exists(path)).ConfigureAwait(true);
        if (IsDisposed)
        {
            return;
        }

        if (!present)
        {
            _folderPresent = false;
            _actionNote = Strings.Get("gui.rtab.folder.notyet");
            RenderAll();
            return;
        }

        try
        {
            ShellActions.OpenFolder(path);
        }
        catch (Exception ex)
        {
            _actionNote = Strings.Format("gui.rtab.folder.openfail", Fmt.Dict(("error", CheckFormat.ExceptionDetail(ex))));
        }

        RenderAll();
    }

    /// <summary>
    /// The "open CSV" button: the file goes to the program Windows has for it, through <see cref="ShellActions"/>. An old-app record is not handed over
    /// itself (a program that opens a file can change it, and the earlier app's files are never touched): a copy in the temp folder is (see
    /// <see cref="RecordingHub.CsvToOpen"/>). The disk is asked, and the copy made, off the UI thread.
    /// </summary>
    internal Task PressOpenCsv() => Track(OpenCsvAsync);

    private async Task OpenCsvAsync()
    {
        _actionNote = null;
        if (SelectedInfo() is not { } info || _detailState == RecordDetailState.Running)
        {
            RenderAll();
            return;
        }

        var hub = _hub;
        string? path = null;
        string? failure = null;
        try
        {
            path = await Task.Run(() => hub.FileExists(info.CsvPath) ? hub.CsvToOpen(info) : null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            failure = CheckFormat.ExceptionDetail(ex);
        }

        if (IsDisposed)
        {
            return;
        }

        if (failure is not null)
        {
            _actionNote = Strings.Format("gui.rtab.csv.openfail", Fmt.Dict(("error", failure)));
        }
        else if (path is null)
        {
            _actionNote = Strings.Get("gui.rtab.csv.missing");
        }
        else
        {
            try
            {
                ShellActions.OpenFile(path);
            }
            catch (Exception ex)
            {
                _actionNote = Strings.Format("gui.rtab.csv.openfail", Fmt.Dict(("error", CheckFormat.ExceptionDetail(ex))));
            }
        }

        RenderAll();
    }

    /// <summary>The "old records" button: the earlier app's records join the list, or leave it. They are only read, wherever they are shown.</summary>
    internal void ToggleLegacy()
    {
        _legacy.Checked = !_legacy.Checked;
        if (_view is HistoryView.List or HistoryView.Empty)
        {
            ApplyFilter();
            ShowList();
        }
    }

    /// <summary>
    /// The "How this is calculated" button: the exact conditions of the sustained value are a line of the summary, or not. A button, not a tip, so Tab reaches it
    /// and Space or Enter presses it. It stays as it is while the person goes from one recording to another.
    /// </summary>
    internal void ToggleHow()
    {
        _how.Checked = !_how.Checked;
        _summary.ShowCalculation = _how.Checked;
    }

    /// <summary>The delete button: asks first, then deletes the selected run's files through the store. Never the run that is being recorded.</summary>
    internal Task PressDelete() => Track(DeleteAsync);

    private async Task DeleteAsync()
    {
        _actionNote = null;
        if (!CanDelete(out var info))
        {
            RenderAll();
            return;
        }

        var facts = new List<DialogFact>
        {
            new(Strings.Get("gui.rtab.col.start"), RecordFormat.StartText(info.StartedAtUtc)),
            new(Strings.Get("gui.rtab.col.length"), RecordFormat.ListLength(info)),
        };
        if (!Dialogs.Confirm(FindForm(), _context.Theme, Strings.Get("gui.rtab.delete.title"), Strings.Get("gui.rtab.delete.message"), facts, Strings.Get("gui.rtab.delete.confirm")))
        {
            return;
        }

        if (IsDisposed || _hub.ActiveRunId == info.RunId)
        {
            return;
        }

        RunDeleteResult? result = null;
        string? failure = null;

        // The button is off while the files are being removed: a second click on it would ask the store for the same run again.
        _deleting = true;
        RenderButtons();
        try
        {
            var store = _hub.Store;
            result = await Task.Run(() => store.DeleteAsync(info.RunId, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            failure = CheckFormat.ExceptionDetail(ex);
        }
        finally
        {
            _deleting = false;
        }

        if (IsDisposed)
        {
            return;
        }

        if (result is null)
        {
            _actionNote = Strings.Format("gui.rtab.delete.failed", Fmt.Dict(("error", failure ?? string.Empty)));
            RenderAll();
            return;
        }

        // The store's own answer, in plain words: deleted, not found any more, being recorded by something else, old and read only, or not possible.
        _actionNote = Strings.Get(RecordStrings.KeyOf(result.Outcome));
        _context.Report(_actionNote);
        if (result.Outcome is RunDeleteOutcome.Deleted or RunDeleteOutcome.NotFound)
        {
            _stale = false;
            StartLoad();
        }
        else
        {
            RenderAll();
        }
    }

    private Task Track(Func<Task> work)
    {
        var task = TrackAsync(work);
        ActionTask = task;
        return task;
    }

    private async Task TrackAsync(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _context.Report(Strings.Format("gui.status.failed", Fmt.Dict(("error", CheckFormat.ExceptionDetail(ex)))));
            }
        }
    }

    // ---- the recording's events ----

    private void OnHubChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        var phase = _hub.Phase;
        if (_lastPhase != RecordingPhase.Recording && phase == RecordingPhase.Recording)
        {
            // The run's files are made: the folder is there and the list has a new line.
            _folderPresent = true;
            _stale = true;
            if (_isShown)
            {
                _stale = false;
                StartLoad();
            }
        }

        if (phase == RecordingPhase.Stopping && _lastPhase != RecordingPhase.Stopping)
        {
            // The clock stops where the stop was pressed; the row says the run is being saved until the list has read it again.
            _frozenElapsed = _hub.ShownElapsed;
        }

        var phaseChanged = phase != _lastPhase;
        _lastPhase = phase;
        RenderAll();
        if (phaseChanged)
        {
            // The words of the live row change with the phase (a run being saved says more than one being recorded): its column grows or shrinks with them.
            FitListColumns();
        }

        if (phase != RecordingPhase.Idle && LiveRow() >= 0)
        {
            // The running clock of the run's row.
            _list.Invalidate();
        }
    }

    private void OnRunEnded(RunResult? result)
    {
        if (IsDisposed)
        {
            return;
        }

        // The run that just ended is shown with its summary as soon as the list has it.
        _selectAfterLoad = result?.RunId;
        _stale = true;
        if (_isShown)
        {
            _stale = false;
            StartLoad();
        }
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
        RenderAll();
        LoadTask = LoadAsync(generation, cancel.Token);
    }

    private async Task LoadAsync(int generation, CancellationToken token)
    {
        RunList? list = null;
        bool? folderPresent = null;
        string? failure = null;
        try
        {
            var store = _hub.Store;
            var folder = _hub.Folder;
            var exists = _hub.FolderExists;
            list = await Task.Run(() => store.ListAsync(token), token).ConfigureAwait(true);
            folderPresent = await Task.Run(() => exists(folder), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = CheckFormat.ExceptionDetail(ex);
        }

        if (generation != _loadGeneration || IsDisposed)
        {
            return;
        }

        if (list is null)
        {
            _all = [];
            _runs = [];
            _problemCount = 0;
            _view = HistoryView.Failed;
            _failure = failure ?? string.Empty;
        }
        else
        {
            _all = list.Runs;
            _problemCount = list.Problems.Count;
            _folderPresent = folderPresent;
            ApplyFilter();
            _view = _runs.Count == 0 ? HistoryView.Empty : HistoryView.List;
        }

        ShowList();
    }

    private void ApplyFilter() => _runs = _legacy.Checked ? _all : _all.Where(static run => !run.IsLegacy).ToArray();

    private void ShowList()
    {
        _view = _view == HistoryView.Failed ? _view : _runs.Count == 0 ? HistoryView.Empty : HistoryView.List;
        _suppressSelection = true;
        try
        {
            _list.SelectedIndices.Clear();
            _list.SetRowCount(_runs.Count);
            var keep = _selected is { } id ? IndexOf(id) : -1;
            if (keep >= 0)
            {
                _list.SelectRow(keep, scroll: false);
            }

            if (keep >= 0 && _list.IsHandleCreated)
            {
                _list.EnsureVisible(keep);
            }
        }
        finally
        {
            _suppressSelection = false;
        }

        FitListColumns();
        FitBody();
        if (_list.IsHandleCreated)
        {
            _list.BeginInvoke(FitBody);
        }

        _list.Invalidate();
        if (_selected is { } selected && IndexOf(selected) < 0)
        {
            // The run that was open is not in the list any more (deleted, or an old record hidden again): its summary goes with it.
            ClearDetail();
        }
        else if (_selected is { } open && _detailState == RecordDetailState.Running && _hub.ActiveRunId != open)
        {
            // The run that was being recorded has ended: its summary can be read now.
            OpenDetail(open);
        }

        if (_selectAfterLoad is { } pending)
        {
            _selectAfterLoad = null;
            var index = IndexOf(pending);
            if (index >= 0)
            {
                _list.SelectRow(index);
            }
        }

        // The row that was chosen from outside the list (the run that has just ended) is on show, whatever the list's height came to.
        _list.EnsureSelectedVisible();
        RenderAll();
    }

    private int IndexOf(Guid runId)
    {
        for (var i = 0; i < _runs.Count; i++)
        {
            if (_runs[i].RunId == runId)
            {
                return i;
            }
        }

        return -1;
    }

    private RunInfo? SelectedInfo()
    {
        if (_selected is not { } id)
        {
            return null;
        }

        var index = IndexOf(id);
        return index >= 0 ? _runs[index] : _detailInfo is { } info && info.RunId == id ? info : null;
    }

    /// <summary>The height the list has now, whole rows: the share the bar was left at (or the default), at least two rows, and never so much that the summary has no room.</summary>
    internal int WholeRowsHeight() => _list.HeightForRows(ListRowsFor(_body.ClientSize.Height));

    /// <summary>The room the list and the summary divide: the body less the bar between them.</summary>
    private int Divided(int bodyHeight) => Math.Max(bodyHeight - _split.Height, 1);

    private int ListRowsFor(int bodyHeight)
    {
        var divided = Divided(bodyHeight);
        var rows = _list.RowsThatFit(divided * (_listShare ?? DefaultListShare) / 100);
        rows = Math.Max(rows, _listShare is null ? MinListRows : 1);

        // The summary keeps a minimum of its own, whatever the share asks.
        var most = _list.RowsThatFit(divided - SummaryMinimum());
        return Math.Max(Math.Min(rows, most), 1);
    }

    private int SummaryMinimum() => _list.Scaled(150);

    private void FitBody()
    {
        if (_body.ClientSize.Height <= 0 || _list.IsDisposed)
        {
            return;
        }

        var height = WholeRowsHeight();
        if (_list.Height != height)
        {
            _list.Height = height;
        }

        // The list has its final height now; the selected record is on show in it (it was scrolled into view for the height the list had before this).
        _list.EnsureSelectedVisible();
        _split.KeyStep = Math.Max(_list.MeasuredRowHeight, 1);
    }

    /// <summary>The bar was dragged (or an arrow key was pressed on it) by this many pixels: the list takes the whole number of rows nearest to that, and the share is kept for this window's session.</summary>
    private void OnSplitMoved(int delta)
    {
        var body = _body.ClientSize.Height;
        if (body <= 0 || _list.IsDisposed)
        {
            return;
        }

        var row = Math.Max(_list.MeasuredRowHeight, 1);
        var rows = (int)Math.Round((_list.Height + delta - _list.HeightForRows(0)) / (double)row, MidpointRounding.AwayFromZero);
        var most = Math.Max(_list.RowsThatFit(Divided(body) - SummaryMinimum()), 1);
        rows = Math.Clamp(rows, 1, most);
        var height = _list.HeightForRows(rows);

        // Rounded up, so that the whole rows the person chose fit in the share they are kept as.
        var divided = Divided(body);
        _listShare = Math.Min(((height * 100) + divided - 1) / divided, 100);
        FitBody();
    }

    /// <summary>Content-wide columns for when, how long and the state; the note takes what is left and ends in an ellipsis when it is long (the whole note is in the summary's head and in a tip).</summary>
    private void FitListColumns()
    {
        if (_fittingColumns || _list.Columns.Count != NoteColumn + 1 || _list.IsDisposed)
        {
            return;
        }

        _fittingColumns = true;
        try
        {
            var air = _list.Scaled(8);
            for (var column = StartColumn; column <= StatusColumn; column++)
            {
                var width = _list.ContentNeed(column) + air;
                if (_list.Columns[column].Width != width)
                {
                    _list.Columns[column].Width = width;
                }
            }

            _list.FitLastColumn();
        }
        finally
        {
            _fittingColumns = false;
        }
    }

    /// <summary>The row of the run that is being recorded (or saved), or -1.</summary>
    private int LiveRow()
    {
        if (_hub.Phase is not (RecordingPhase.Recording or RecordingPhase.Stopping) || _hub.ActiveRunId is not { } id)
        {
            return -1;
        }

        return IndexOf(id);
    }

    private bool IsLive(RunInfo run) =>
        _hub.Phase is RecordingPhase.Recording or RecordingPhase.Stopping && _hub.ActiveRunId == run.RunId;

    private GridCell ListCell(int row, int column)
    {
        if (row < 0 || row >= _runs.Count)
        {
            return new GridCell(string.Empty);
        }

        var run = _runs[row];
        if (IsLive(run))
        {
            // The run that is going: a clock, not an estimate; and, once it was told to stop, that it is being saved.
            var stopping = _hub.Phase == RecordingPhase.Stopping;
            return column switch
            {
                StartColumn => new GridCell(RecordFormat.StartText(run.StartedAtUtc)),
                LengthColumn => new GridCell(RecordFormat.Elapsed(stopping ? _frozenElapsed : _hub.ShownElapsed)),
                StatusColumn => new GridCell(Strings.Get(stopping ? "gui.rtab.status.stopping" : "gui.rtab.status.recording"), CellTone.Accent),
                _ => new GridCell(string.Empty, CellTone.Dim),
            };
        }

        return column switch
        {
            StartColumn => new GridCell(RecordFormat.StartText(run.StartedAtUtc)),
            LengthColumn => new GridCell(RecordFormat.ListLength(run)),
            StatusColumn => new GridCell(RecordFormat.StatusWord(run.Status), StatusTone(run.Status)),
            _ => new GridCell(RecordFormat.NoteText(run), CellTone.Dim),
        };
    }

    private static CellTone StatusTone(RunStatus status) => status switch
    {
        RunStatus.Recording => CellTone.Accent,
        RunStatus.Interrupted => CellTone.Warning,
        RunStatus.Failed => CellTone.Danger,
        _ => CellTone.Normal,
    };

    private string? ListTip(int row, int column)
    {
        if (row < 0 || row >= _runs.Count)
        {
            return null;
        }

        return column switch
        {
            LengthColumn when !IsLive(_runs[row]) && RecordFormat.IsApproximate(_runs[row]) => Strings.Get("gui.rtab.footer.approx"),
            NoteColumn when !IsLive(_runs[row]) => RecordFormat.NoteText(_runs[row]) is { Length: > 0 } note ? note : null,
            _ => null,
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
        if (row < 0 || row >= _runs.Count)
        {
            return;
        }

        var info = _runs[row];
        if (_selected == info.RunId && _detailState is RecordDetailState.Found or RecordDetailState.Opening or RecordDetailState.Running)
        {
            return;
        }

        OpenDetail(info.RunId);
    }

    private void OpenDetail(Guid runId)
    {
        Cancel(ref _detailCancel);
        var cancel = new CancellationTokenSource();
        _detailCancel = cancel;
        var generation = ++_detailGeneration;
        _selected = runId;
        _actionNote = null;
        _detailError = null;
        _summary.Show(null);
        var index = IndexOf(runId);
        _detailInfo = index >= 0 ? _runs[index] : null;
        if (_hub.ActiveRunId == runId)
        {
            // Being recorded: its file is half written. There is nothing to read until it stops.
            _detailState = RecordDetailState.Running;
            RenderAll();
            return;
        }

        _detailState = RecordDetailState.Opening;
        RenderAll();
        DetailTask = OpenAsync(runId, generation, cancel.Token);
    }

    private async Task OpenAsync(Guid runId, int generation, CancellationToken token)
    {
        RunDetail? detail = null;
        string? failure = null;
        try
        {
            var store = _hub.Store;
            detail = await Task.Run(() => store.ReadAsync(runId, token), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = CheckFormat.ExceptionDetail(ex);
        }

        if (generation != _detailGeneration || IsDisposed)
        {
            return;
        }

        if (failure is not null)
        {
            _detailState = RecordDetailState.Unreadable;
            _detailError = failure;
            _summary.Show(null);
        }
        else if (detail is null)
        {
            _detailState = RecordDetailState.NotFound;
            _summary.Show(null);
        }
        else
        {
            _detailState = RecordDetailState.Found;
            _detailInfo = detail.Info;
            _summary.Show(detail);
        }

        RenderAll();
    }

    private void ClearDetail()
    {
        Cancel(ref _detailCancel);
        _detailGeneration++;
        _selected = null;
        _detailInfo = null;
        _detailError = null;
        _detailState = RecordDetailState.None;
        _summary.Show(null);
    }

    // ---- drawing ----

    private bool CanDelete(out RunInfo info)
    {
        info = null!;
        if (SelectedInfo() is not { } selected)
        {
            return false;
        }

        info = selected;
        return !_deleting && !selected.IsLegacy && _hub.ActiveRunId != selected.RunId;
    }

    private void RenderAll()
    {
        RenderState();
        RenderFolder();
        RenderNotice();
        RenderFooter();
        RenderDetail();
        RenderButtons();
    }

    private void RenderState()
    {
        var tone = CellTone.Dim;
        string text;
        switch (_hub.Phase)
        {
            case RecordingPhase.Starting:
                text = Strings.Get("gui.rtab.state.starting");
                tone = CellTone.Accent;
                break;
            case RecordingPhase.Recording:
                text = Strings.Format(
                    "gui.rtab.state.recording",
                    Fmt.Dict(("time", RecordFormat.Elapsed(_hub.ShownElapsed)), ("marks", _hub.Marks.Count.ToString(Inv))));
                tone = CellTone.Accent;
                break;
            case RecordingPhase.Stopping:
                text = Strings.Get("gui.rtab.state.stopping");
                tone = CellTone.Accent;
                break;
            default:
                text = IdleText(out tone);
                break;
        }

        _state.Text = text;
        _state.SetTone(tone);
        _tips.SetToolTip(_state, MarksTip());
    }

    private string IdleText(out CellTone tone)
    {
        tone = CellTone.Dim;
        if (_hub.LastResult is not { } result)
        {
            return Strings.Get("gui.rtab.state.idle");
        }

        var length = RecordFormat.ExactLength(result.DurationUs / 1_000_000.0);
        if (result.Status == RunStatus.Completed)
        {
            return result.StopReason == RunStopReason.User
                ? Strings.Format("gui.rtab.state.saved", Fmt.Dict(("length", length)))
                : Strings.Format("gui.rtab.state.ended", Fmt.Dict(("reason", Strings.Get(RecordStrings.KeyOf(result.StopReason))), ("length", length)));
        }

        // Saving failed: what to do about it, in plain words (the reason is a technical one; the run's own note says it where the run is shown).
        tone = CellTone.Warning;
        return Strings.Get("gui.rtab.state.failed");
    }

    /// <summary>The marks set so far, one per line with the time of each, for the state line's tip. Empty when there are none.</summary>
    private string MarksTip()
    {
        if (_hub.Marks.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            _hub.Marks.Select(static mark => RecordFormat.Elapsed(TimeSpan.FromTicks(mark.OffsetUs * 10)) + "  " + Strings.Get(RecordStrings.KeyOf(mark.Kind))));
    }

    private void RenderFolder()
    {
        // Where Documents is gets asked of Windows when the tab is first shown (the hub keeps the answer), not while the page is being built.
        if (!_isShown)
        {
            return;
        }

        var folder = _hub.Folder;
        var key = _folderPresent == false ? "gui.rtab.folder.new" : "gui.rtab.folder";
        _folder.Text = Strings.Format(key, Fmt.Dict(("path", RecordFormat.ShortFolder(folder))));
        _tips.SetToolTip(_folder, folder);
    }

    private void RenderNotice()
    {
        var parts = new List<string>();
        var warning = false;
        if (_hub.ProblemKey is not null)
        {
            parts.Add(_hub.ProblemText);
            warning = true;
        }

        if (_actionNote is { Length: > 0 } note)
        {
            parts.Add(note);
        }

        if (_view == HistoryView.Failed)
        {
            parts.Add(Strings.Format("gui.rtab.notice.listfail", Fmt.Dict(("error", _failure ?? string.Empty))));
            warning = true;
        }
        else if (_problemCount > 0)
        {
            parts.Add(Strings.Format("gui.rtab.notice.problems", Fmt.Dict(("count", _problemCount.ToString(Inv)))));
        }

        var text = parts.Count == 0 ? string.Empty : parts.Aggregate(CheckFormat.Pair);
        _notice.SetTone(warning ? CellTone.Warning : CellTone.Accent);
        _notice.Text = text;
        _notice.Visible = text.Length > 0;
    }

    private void RenderFooter()
    {
        _count.Text = _view switch
        {
            HistoryView.Loading => Strings.Get("gui.rtab.footer.loading"),
            HistoryView.Empty => Strings.Get("gui.rtab.footer.empty"),
            HistoryView.List => RecordFormat.RecordingCount(_runs.Count),
            _ => string.Empty,
        };

        // The run that is being recorded shows a clock, not an estimate: it does not make "about" necessary.
        _hint.Text = _view == HistoryView.List && _runs.Any(run => !IsLive(run) && RecordFormat.IsApproximate(run)) ? Strings.Get("gui.rtab.footer.approx") : string.Empty;
        _hint.Visible = _hint.Text.Length > 0;
    }

    private void RenderDetail()
    {
        var info = _detailInfo;
        var text = _detailState switch
        {
            RecordDetailState.Opening => Strings.Get("gui.rtab.detail.opening"),
            RecordDetailState.Running => _hub.Phase == RecordingPhase.Recording
                ? Strings.Format("gui.rtab.detail.running", Fmt.Dict(("time", RecordFormat.Elapsed(_hub.ShownElapsed))))
                : Strings.Get("gui.rtab.detail.stopping"),
            RecordDetailState.NotFound => Strings.Get("gui.rtab.detail.notfound"),
            RecordDetailState.Unreadable => Strings.Format("gui.rtab.detail.unreadable", Fmt.Dict(("error", _detailError ?? string.Empty))),
            RecordDetailState.Found when info is not null => FoundHead(info),
            _ => _view switch
            {
                HistoryView.List => Strings.Get("gui.rtab.detail.pick"),
                HistoryView.Empty => Strings.Get("gui.rtab.detail.empty"),
                _ => string.Empty,
            },
        };
        _detailHead.Text = text;
        _detailHead.SetTone(info is { IsLegacy: true } && _detailState == RecordDetailState.Found ? CellTone.Accent : CellTone.Dim);
    }

    /// <summary>What the selected run is: when it started, how it ended, the word about it (why it stopped) and, for an old record, that it is read only.</summary>
    private static string FoundHead(RunInfo info)
    {
        var head = CheckFormat.Pair(RecordFormat.StartText(info.StartedAtUtc), RecordFormat.StatusWord(info.Status));
        if (info.IsLegacy)
        {
            return CheckFormat.Pair(head, Strings.Get("gui.rtab.detail.legacy"));
        }

        return RecordFormat.NoteText(info) is { Length: > 0 } note ? CheckFormat.Pair(head, note) : head;
    }

    private void RenderButtons()
    {
        var phase = _hub.Phase;
        _start.Enabled = phase == RecordingPhase.Idle;
        _stop.Enabled = phase == RecordingPhase.Recording;
        _mark.Enabled = phase == RecordingPhase.Recording;
        foreach (var item in _markItems)
        {
            item.Enabled = phase == RecordingPhase.Recording;
        }

        var hasRun = SelectedInfo() is not null;
        _csv.Enabled = hasRun && _detailState is not (RecordDetailState.Running or RecordDetailState.Opening);
        _delete.Enabled = CanDelete(out _);

        // The switch for the sustained value's conditions is there while the summary has that value's sentence, and nowhere else.
        _detailHeader.SetShown(_how, _summary.HasSustainedNote);
        var pick = hasRun ? string.Empty : Strings.Get("gui.rtab.pickfirst");
        _tips.SetToolTip(_csv, pick);
        _tips.SetToolTip(_delete, pick);
    }
}
