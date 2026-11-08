using PcMieru.Core.Hardware.Model;

namespace PcMieru.Gui;

/// <summary>
/// One panel per thing that can be slow: the CPU (all cores together or one graph each), the memory, and every GPU, disk
/// and network adapter. The list at the left shows each one's current value; the selected one has its graph and facts.
/// Only the selected panel is refilled each sample, and only while this page is showing.
/// </summary>
internal sealed class PerformancePage : PageBase
{
    private const string PerCoreSetting = "perf.percore";
    private const int SeriesPerGraph = 3;

    private static readonly Func<double, string> PercentAxis = v => v.ToString("0", System.Globalization.CultureInfo.CurrentCulture) + " %";
    private static readonly Func<double, string> RateAxis = v => Fmt.Bytes((long)v) + "/s";

    private readonly SummaryListBox _list = new() { Name = "perfList", Dock = DockStyle.Left };
    private readonly SectionLabel _heading = new() { Name = "perfHeading" };
    private readonly ToneLabel _detail = new() { AutoSize = true, Name = "perfDetail" };
    private readonly CheckBox _perCore = new() { AutoSize = true, Name = "perfPerCore" };
    private readonly TableLayoutPanel _graphHost = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Name = "perfGraphs" };
    private readonly GraphControl[] _graphs = [new() { Name = "perfGraph0" }, new() { Name = "perfGraph1" }];
    private readonly double[][][] _data = [[new double[SnapshotPump.HistoryLength], new double[SnapshotPump.HistoryLength], new double[SnapshotPump.HistoryLength]], [new double[SnapshotPump.HistoryLength], new double[SnapshotPump.HistoryLength], new double[SnapshotPump.HistoryLength]]];
    private readonly Panel _coreHost = new() { Dock = DockStyle.Fill, AutoScroll = true, Visible = false, Name = "perfCores" };
    private readonly List<GraphControl> _coreGraphs = [];
    private readonly List<double[]> _coreData = [];
    private readonly ToneLabel _note = new() { Dock = DockStyle.Bottom, AutoSize = false, Name = "perfNote" };
    private readonly ToneLabel _listNote = new() { Dock = DockStyle.Bottom, AutoSize = false, Visible = false, Name = "perfListNote" };
    private readonly TableLayoutPanel _stats = new() { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, Name = "perfStats" };
    private readonly ToneLabel[] _captions = new ToneLabel[PerfMetrics.MaxStats];
    private readonly ToneLabel[] _values = new ToneLabel[PerfMetrics.MaxStats];
    private readonly string?[] _tipText = new string?[PerfMetrics.MaxStats];
    private readonly ToolTip _tips = new() { ShowAlways = true };

    private List<PerfPanel> _panels = [];
    private PerfPanel? _current;
    private GraphSpec[] _specs = [];
    private Snapshot? _last;
    private PerfFacts _facts = PerfFacts.Empty;
    private bool _factsRequested;
    private bool _rebuilding;
    private bool _layingOut;
    private int _scaleMs;
    private string? _wanted;

    public PerformancePage(GuiContext context)
        : base(context, "performance", "gui.nav.performance")
    {
        var pad = Px(16);
        _list.Width = Px(236);
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (!_rebuilding)
            {
                SelectionChanged();
            }
        };
        _detail.SetTone(CellTone.Dim);
        _note.SetTone(CellTone.Dim);
        _note.Height = Px(28);
        _note.Padding = new Padding(pad, Px(6), pad, 0);
        _note.SizeChanged += (_, _) => FitNote();
        _note.FontChanged += (_, _) => FitNote();
        _listNote.SetTone(CellTone.Dim);
        _listNote.Padding = new Padding(pad, Px(6), pad, 0);
        _perCore.Checked = context.Settings.GetBool(PerCoreSetting, false);
        _perCore.CheckedChanged += (_, _) =>
        {
            Context.Settings.SetBool(PerCoreSetting, _perCore.Checked);
            if (_current is not null)
            {
                ConfigureView(_current);
                if (_last is { } snapshot)
                {
                    RefreshView(snapshot);
                }
            }
        };

        _graphHost.Padding = new Padding(pad, 0, pad, 0);
        _graphHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _graphHost.RowStyles.Add(new System.Windows.Forms.RowStyle(SizeType.Percent, 100));
        _graphHost.RowStyles.Add(new System.Windows.Forms.RowStyle(SizeType.Percent, 0));
        for (var g = 0; g < _graphs.Length; g++)
        {
            _graphs[g].Dock = DockStyle.Fill;
            _graphs[g].Margin = new Padding(0, 0, 0, g == 0 ? Px(10) : 0);
            _graphs[g].MinimumSize = new Size(Px(120), Px(80));
            _graphHost.Controls.Add(_graphs[g], 0, g);
        }

        _coreHost.SizeChanged += (_, _) => LayoutCores();

        _stats.Padding = new Padding(pad, Px(4), pad, 0);
        _stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        _stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        _stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        _stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        _stats.RowCount = PerfMetrics.MaxStats / 2;
        for (var i = 0; i < PerfMetrics.MaxStats; i++)
        {
            if (i % 2 == 0)
            {
                _stats.RowStyles.Add(new System.Windows.Forms.RowStyle(SizeType.AutoSize));
            }

            _captions[i] = new ToneLabel { AutoSize = false, Height = Px(24), Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Visible = false, UseMnemonic = false };
            _values[i] = new ToneLabel { AutoSize = false, Height = Px(24), Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Visible = false, UseMnemonic = false };
            _captions[i].SetTone(CellTone.Dim);
            _stats.Controls.Add(_captions[i], (i % 2) * 2, i / 2);
            _stats.Controls.Add(_values[i], ((i % 2) * 2) + 1, i / 2);
        }

        var bar = PageLayout.Bar("perfHead", pad, [_heading, _detail], [_perCore]);
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, pad), Name = "perfRight" };
        right.Controls.Add(_graphHost);
        right.Controls.Add(_coreHost);
        right.Controls.Add(_listNote);
        right.Controls.Add(_note);
        right.Controls.Add(_stats);
        right.Controls.Add(bar);

        Controls.Add(right);
        Controls.Add(_list);
        _list.TabIndex = 0;
        _perCore.TabIndex = 1;
        ApplyLanguage();
    }

    internal SummaryListBox List => _list;

    internal ToneLabel ListNote => _listNote;

    internal CheckBox PerCoreBox => _perCore;

    internal PerfPanel? Current => _current;

    internal IReadOnlyList<GraphControl> CoreGraphs => _coreGraphs;

    internal GraphControl[] Graphs => _graphs;

    internal IReadOnlyList<string> ShownStats => _values.Where(v => v.Visible).Select(v => _captions[Array.IndexOf(_values, v)].Text + ": " + v.Text).ToList();

    public override void OnActivated()
    {
        Context.Pump.Sampled -= OnSampled;
        Context.Pump.Sampled += OnSampled;
        if (!_factsRequested)
        {
            _factsRequested = true;
            Run(LoadFactsAsync);
        }

        if (Context.Pump.Latest is { } latest)
        {
            OnSampled(latest);
        }
    }

    public override void OnDeactivated() => Context.Pump.Sampled -= OnSampled;

    public override void OnNavigatedTo(ProcessKey? process)
    {
        if (Context.TakeNavigationArgument() is { Length: > 0 } argument)
        {
            Select(argument);
        }
    }

    public override void RefreshNow()
    {
        Context.Services.Hardware.Invalidate();
        _factsRequested = true;
        Run(LoadFactsAsync);
        base.RefreshNow();
    }

    public override void ApplyLanguage()
    {
        _perCore.Text = Strings.Get("gui.perf.percore");
        _list.AccessibleName = Strings.Get("gui.nav.performance");
        UpdateTimeScale(force: true);
        for (var g = 0; g < _graphs.Length; g++)
        {
            _graphs[g].EmptyText = Strings.Get("gui.perf.collecting");
        }

        for (var i = 0; i < _coreGraphs.Count; i++)
        {
            _coreGraphs[i].Title = Strings.Format("gui.perf.core", Fmt.Dict(("n", i.ToString(System.Globalization.CultureInfo.CurrentCulture))));
        }

        if (_last is { } snapshot)
        {
            ShowListNotices(snapshot.System);
            Rebuild(PerfMetrics.Panels(snapshot.System), snapshot);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Context.Pump.Sampled -= OnSampled;
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Select(string argument)
    {
        var index = _panels.FindIndex(p => PerfMetrics.Argument(p.Kind) == argument || p.Key == argument);
        if (index >= 0)
        {
            _list.SelectedIndex = index;
        }
        else
        {
            _wanted = argument;
        }
    }

    private async Task LoadFactsAsync()
    {
        var cpu = await Context.Services.Hardware.GetAsync(HardwareCategory.Cpu).ConfigureAwait(true);
        var memory = await Context.Services.Hardware.GetAsync(HardwareCategory.Memory).ConfigureAwait(true);
        _facts = PerfFacts.From(cpu, memory);
        if (_last is { } snapshot && IsHandleCreated)
        {
            RefreshView(snapshot);
        }
    }

    private void OnSampled(Snapshot snapshot)
    {
        _last = snapshot;
        ShowListNotices(snapshot.System);
        var panels = PerfMetrics.Panels(snapshot.System);
        if (!_list.HasKeys(panels.ConvertAll(p => p.Key)))
        {
            Rebuild(panels, snapshot);
            return;
        }

        _panels = panels;
        for (var i = 0; i < panels.Count; i++)
        {
            _list.SetValue(i, PerfMetrics.Headline(panels[i], snapshot.System));
        }

        if (_current is not null)
        {
            RefreshView(snapshot);
        }
    }

    /// <summary>A list that is empty because it could not be read gets a line saying so, instead of silently having no panel.</summary>
    private void ShowListNotices(SystemInfo system)
    {
        var notices = PerfMetrics.ListNotices(system);
        var text = string.Join(Environment.NewLine, notices);
        if (_listNote.Text != text)
        {
            _listNote.Text = text;
        }

        _listNote.Height = (Px(18) * Math.Max(1, notices.Count)) + Px(8);
        _listNote.Visible = notices.Count > 0;
    }

    private void Rebuild(List<PerfPanel> panels, Snapshot snapshot)
    {
        _last = snapshot;
        _panels = panels;
        var select = _current?.Key;
        if (_wanted is not null)
        {
            select = panels.FirstOrDefault(p => PerfMetrics.Argument(p.Kind) == _wanted || p.Key == _wanted)?.Key ?? select;
            _wanted = null;
        }

        _rebuilding = true;
        try
        {
            _list.SetItems([.. panels.Select(p => new SummaryItem(p.Key, p.Name, PerfMetrics.Headline(p, snapshot.System)))], select);
        }
        finally
        {
            _rebuilding = false;
        }

        SelectionChanged();
    }

    private void SelectionChanged()
    {
        var index = _list.SelectedIndex;
        _current = index >= 0 && index < _panels.Count ? _panels[index] : null;
        if (_current is null)
        {
            return;
        }

        ConfigureView(_current);
        if (_last is { } snapshot)
        {
            RefreshView(snapshot);
        }
    }

    /// <summary>
    /// Says how far back the graphs reach: the left edge is "N s ago" (the readings a graph holds times the time between them),
    /// the right edge "now". Follows the interval in force, so a slower setting does not leave a wrong "60 s" on the axis.
    /// </summary>
    private void UpdateTimeScale(bool force = false)
    {
        var interval = _last?.IntervalMs is > 0 ? _last.IntervalMs : Context.Pump.EffectiveIntervalMs;
        if (interval <= 0 || (!force && interval == _scaleMs))
        {
            return;
        }

        _scaleMs = interval;
        var now = Strings.Get("gui.perf.now");
        var ago = Strings.Format("gui.perf.ago", Fmt.Dict(("span", PerfMetrics.Span(PerfMetrics.HistorySeconds(interval)))));
        for (var g = 0; g < _graphs.Length; g++)
        {
            _graphs[g].TimeStartText = ago;
            _graphs[g].TimeEndText = now;
        }

        _note.Text = Strings.Format("gui.perf.note", Fmt.Dict(("now", now), ("ago", ago), ("interval", PerfMetrics.Span(interval / 1000.0)), ("count", SnapshotPump.HistoryLength.ToString(System.Globalization.CultureInfo.CurrentCulture))));
        FitNote();
    }

    /// <summary>The note under the graphs (smoke run and tests read it).</summary>
    internal string NoteText => _note.Text;

    /// <summary>Gives the note the height its wrapped text needs at the current width and text size, so a longer language or a larger font does not cut it off.</summary>
    private void FitNote()
    {
        var width = _note.ClientSize.Width - _note.Padding.Horizontal;
        if (width <= 0 || _note.Text.Length == 0)
        {
            return;
        }

        var text = TextRenderer.MeasureText(_note.Text, _note.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Height;
        var height = Math.Max(Px(28), text + _note.Padding.Vertical + Px(4));
        if (_note.Height != height)
        {
            _note.Height = height;
        }
    }

    private bool ShowingCores => _perCore.Checked && _current is { Kind: PerfKind.Cpu } && _last is { System.Cpu.PerLogicalPercent.Count: > 0 };

    /// <summary>Everything that depends on which panel is selected and not on the numbers: titles, axes, which graph is visible.</summary>
    private void ConfigureView(PerfPanel panel)
    {
        _specs = PerfMetrics.Graphs(panel.Kind);
        _heading.Text = panel.Name;
        _perCore.Visible = panel.Kind == PerfKind.Cpu;
        var cores = ShowingCores;
        _graphHost.Visible = !cores;
        _coreHost.Visible = cores;
        for (var g = 0; g < _graphs.Length; g++)
        {
            var on = g < _specs.Length;
            _graphs[g].Visible = on;
            if (!on)
            {
                continue;
            }

            var spec = _specs[g];
            _graphs[g].Title = Strings.Get(spec.TitleKey);
            _graphs[g].FixedMaximum = spec.FixedMaximum;
            _graphs[g].AutoFloor = spec.AutoFloor;
            _graphs[g].AxisFormatter = spec.BytesAxis ? RateAxis : PercentAxis;
            for (var s = 0; s < SeriesPerGraph; s++)
            {
                _graphs[g].SetSeriesName(s, s < spec.SeriesKeys.Length ? Strings.Get(spec.SeriesKeys[s]) : string.Empty);
            }
        }

        _graphHost.RowStyles[0].Height = _specs.Length == 1 ? 100 : 60;
        _graphHost.RowStyles[1].Height = _specs.Length == 1 ? 0 : 40;
    }

    /// <summary>The per-sample work for the selected panel: refill its arrays, set the values, repaint what is on screen.</summary>
    private void RefreshView(Snapshot snapshot)
    {
        if (_current is not { } panel)
        {
            return;
        }

        var history = Context.Pump.History;
        var count = Math.Min(history.Count, SnapshotPump.HistoryLength);
        var offset = history.Count - count;
        UpdateTimeScale();
        var headline = PerfMetrics.Headline(panel, snapshot.System);
        _detail.Text = PerfMetrics.Detail(panel, snapshot.System, _facts);
        _perCore.Enabled = snapshot.System.Cpu.PerLogicalPercent.Count > 0;

        if (ShowingCores)
        {
            if (!_coreHost.Visible)
            {
                ConfigureView(panel);
            }

            FillCores(snapshot, history, count, offset);
        }
        else
        {
            if (_coreHost.Visible)
            {
                ConfigureView(panel);
            }

            for (var g = 0; g < _specs.Length; g++)
            {
                var spec = _specs[g];
                for (var s = 0; s < SeriesPerGraph; s++)
                {
                    var values = _data[g][s];
                    var used = s < spec.Series ? count : 0;
                    for (var t = 0; t < used; t++)
                    {
                        values[t] = PerfMetrics.Sample(panel, history[offset + t].System, g, s);
                    }

                    _graphs[g].SetSeries(s, values, used);
                }

                _graphs[g].ValueText = g == 0 ? headline : GraphValue(panel, snapshot, g);
                _graphs[g].Invalidate();
            }
        }

        ShowStats(PerfMetrics.Stats(panel, snapshot, _facts));
    }

    private static string GraphValue(PerfPanel panel, Snapshot snapshot, int graph)
    {
        var system = snapshot.System;
        switch (panel.Kind)
        {
            case PerfKind.Gpu when system.Gpus.FirstOrDefault(g => g.Id == panel.DeviceId) is { } gpu:
                return gpu.DedicatedUsedBytes.TryGet(out var used) && gpu.DedicatedTotalBytes.TryGet(out var total)
                    ? Fmt.Bytes(used) + " / " + Fmt.Bytes(total)
                    : Fmt.Dash;
            case PerfKind.Disk when system.Disks.FirstOrDefault(d => d.Id == panel.DeviceId) is { } disk:
                return Fmt.Rate(disk.ReadBytesPerSec, disk.WriteBytesPerSec);
            default:
                return Fmt.Dash;
        }
    }

    private void FillCores(Snapshot snapshot, IReadOnlyList<Snapshot> history, int count, int offset)
    {
        var cores = snapshot.System.Cpu.PerLogicalPercent;
        EnsureCores(cores.Count);
        for (var c = 0; c < _coreGraphs.Count; c++)
        {
            var values = _coreData[c];
            for (var t = 0; t < count; t++)
            {
                var per = history[offset + t].System.Cpu.PerLogicalPercent;
                values[t] = c < per.Count && per[c].TryGet(out var v) ? v : double.NaN;
            }

            _coreGraphs[c].SetSeries(0, values, count);
            _coreGraphs[c].ValueText = c < cores.Count ? Fmt.Percent(cores[c]) : Fmt.Dash;
            _coreGraphs[c].Invalidate();
        }
    }

    private void EnsureCores(int count)
    {
        if (count == _coreGraphs.Count)
        {
            return;
        }

        _coreHost.SuspendLayout();
        foreach (var old in _coreGraphs)
        {
            _coreHost.Controls.Remove(old);
            old.Dispose();
        }

        _coreGraphs.Clear();
        _coreData.Clear();
        for (var i = 0; i < count; i++)
        {
            var graph = new GraphControl
            {
                Name = "perfCore" + i,
                MinimumSize = Size.Empty,
                FixedMaximum = 100,
                Title = Strings.Format("gui.perf.core", Fmt.Dict(("n", i.ToString(System.Globalization.CultureInfo.CurrentCulture)))),
                AxisFormatter = null,
            };
            _coreGraphs.Add(graph);
            _coreData.Add(new double[SnapshotPump.HistoryLength]);
            _coreHost.Controls.Add(graph);
        }

        Context.Theme.Apply(_coreHost);
        _coreHost.ResumeLayout(performLayout: false);
        LayoutCores();
    }

    /// <summary>Lays the per-processor graphs out in rows of equal cells; a page that does not fit scrolls.</summary>
    private void LayoutCores()
    {
        if (_coreGraphs.Count == 0 || _layingOut)
        {
            return;
        }

        _layingOut = true;
        try
        {
            var pad = Px(16);
            var gap = Px(8);
            var width = _coreHost.ClientSize.Width - (pad * 2);
            var height = _coreHost.ClientSize.Height;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            _coreHost.AutoScrollPosition = Point.Empty;
            var n = _coreGraphs.Count;
            var columns = Math.Clamp((width + gap) / (Px(150) + gap), 1, n);
            var rows = (n + columns - 1) / columns;
            var cellWidth = (width - (gap * (columns - 1))) / columns;
            var cellHeight = Math.Max(Px(84), (height - (gap * (rows - 1))) / rows);
            for (var i = 0; i < n; i++)
            {
                _coreGraphs[i].SetBounds(pad + ((i % columns) * (cellWidth + gap)), i / columns * (cellHeight + gap), cellWidth, cellHeight);
            }

            var total = (rows * (cellHeight + gap)) - gap;
            _coreHost.AutoScrollMinSize = total > height ? new Size(0, total) : Size.Empty;
        }
        finally
        {
            _layingOut = false;
        }
    }

    private void ShowStats(List<StatLine> lines)
    {
        for (var i = 0; i < PerfMetrics.MaxStats; i++)
        {
            if (i >= lines.Count)
            {
                _captions[i].Visible = false;
                _values[i].Visible = false;
                continue;
            }

            var line = lines[i];
            _captions[i].Text = line.Label;
            _values[i].Text = line.Value;
            _values[i].SetTone(line.Dim ? CellTone.Dim : CellTone.Normal);
            _captions[i].Visible = true;
            _values[i].Visible = true;
            var tip = string.IsNullOrEmpty(line.Tip) ? null : line.Tip;
            if (_tipText[i] != tip)
            {
                _tipText[i] = tip;
                _tips.SetToolTip(_values[i], tip);
            }
        }
    }
}

internal static class PerfStrings
{
    public static readonly StringEntry[] All =
    [
        new("gui.perf.cpu", "CPU", "CPU"),
        new("gui.perf.memory", "メモリ", "Memory"),
        new("gui.perf.disk", "ディスク {name}", "Disk {name}"),
        new("gui.perf.percore", "論理CPUごとに表示", "Show each logical CPU separately"),
        new("gui.perf.core", "論理CPU {n}", "Logical CPU {n}"),
        new("gui.perf.note", "グラフの右端が「{now}」、左端が「{ago}」です（{interval}ごとに測った直近 {count} 回分）", "The right edge is \"{now}\" and the left edge is \"{ago}\" (the last {count} readings, one every {interval})"),
        new("gui.perf.now", "現在", "Now"),
        new("gui.perf.ago", "{span}前", "{span} ago"),
        new("gui.perf.span.sec", "{n} 秒", "{n} s"),
        new("gui.perf.span.min", "{n} 分", "{n} min"),
        new("gui.perf.collecting", "データ収集中…", "Collecting data…"),
        new("gui.perf.list.gpu", "GPU", "GPU"),
        new("gui.perf.list.disks", "ディスク", "Disks"),
        new("gui.perf.list.nics", "ネットワーク", "Network adapters"),
        new("gui.perf.list.unread", "{list} を一覧に出せません（{status}）", "{list} cannot be listed ({status})"),
        new("gui.perf.total", "合計 {size}", "{size} in total"),
        new("gui.perf.link", "リンク速度 {speed}", "Link speed {speed}"),
        new("gui.fmt.temp", "{value} ℃", "{value} C"),

        new("gui.perf.g.cpu", "使用率", "Usage"),
        new("gui.perf.g.memory", "メモリの使用量", "Memory in use"),
        new("gui.perf.g.gpu", "GPU の使用率（いちばん忙しい処理）", "GPU usage (busiest engine)"),
        new("gui.perf.g.gpumem", "専用メモリの使用量", "Dedicated memory in use"),
        new("gui.perf.g.diskactive", "アクティブな時間", "Active time"),
        new("gui.perf.g.diskrate", "読み書きの速さ", "Read and write speed"),
        new("gui.perf.g.netrate", "通信の速さ", "Network speed"),
        new("gui.perf.read", "読み取り", "Read"),
        new("gui.perf.write", "書き込み", "Write"),
        new("gui.perf.send", "送信", "Send"),
        new("gui.perf.receive", "受信", "Receive"),

        new("gui.perf.s.usage", "使用率", "Usage"),
        new("gui.perf.s.speed", "現在のクロック", "Current clock"),
        new("gui.perf.s.logical", "論理CPU数", "Logical CPUs"),
        new("gui.perf.s.cores", "コア数", "Cores"),
        new("gui.perf.s.base", "基本クロック", "Base clock"),
        new("gui.perf.s.l1", "L1 キャッシュ", "L1 cache"),
        new("gui.perf.s.l2", "L2 キャッシュ", "L2 cache"),
        new("gui.perf.s.l3", "L3 キャッシュ", "L3 cache"),
        new("gui.perf.s.processes", "プロセスの数", "Processes"),
        new("gui.perf.s.uptime", "起動してからの時間", "Up time"),
        new("gui.perf.s.used", "使用中", "In use"),
        new("gui.perf.s.available", "空き", "Available"),
        new("gui.perf.s.commit", "コミット済み", "Committed"),
        new("gui.perf.s.cache", "キャッシュ", "Cached"),
        new("gui.perf.s.paged", "ページプール", "Paged pool"),
        new("gui.perf.s.nonpaged", "非ページプール", "Non-paged pool"),
        new("gui.perf.s.slots", "スロットの使用", "Slots used"),
        new("gui.perf.s.memtype", "種類", "Type"),
        new("gui.perf.s.memspeed", "動作速度（設定値）", "Speed (configured)"),
        new("gui.perf.s.active", "アクティブな時間", "Active time"),
        new("gui.perf.s.read", "読み取り", "Read"),
        new("gui.perf.s.write", "書き込み", "Write"),
        new("gui.perf.s.response", "平均の応答時間", "Average response time"),
        new("gui.perf.s.send", "送信", "Send"),
        new("gui.perf.s.receive", "受信", "Receive"),
        new("gui.perf.s.link", "リンク速度", "Link speed"),
        new("gui.perf.s.state", "状態", "Status"),
        new("gui.perf.s.up", "接続中", "Connected"),
        new("gui.perf.s.down", "つながっていません", "Not connected"),
        new("gui.perf.s.dedicated", "専用メモリ", "Dedicated memory"),
        new("gui.perf.s.shared", "共有メモリ", "Shared memory"),
        new("gui.perf.s.temp", "温度", "Temperature"),
        new("gui.perf.s.power", "消費電力", "Power"),
        new("gui.perf.s.power.driver", "消費電力（ドライバーの報告値）", "Power (driver report)"),
        new(
            "gui.perf.s.power.driver.tip",
            "AMD のドライバーが報告する GPU の電力です。チップが実際に使っている電力とは限りません。",
            "The GPU power the AMD driver reports. It may not be the power the chip really draws."),
        new("gui.perf.s.fan", "ファン", "Fan"),
        new("gui.perf.s.clock", "クロック", "Clock"),
        new("gui.perf.s.engine", "処理 {type}", "Engine {type}"),
    ];
}
