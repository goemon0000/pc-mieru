using System.Globalization;

namespace PcMieru.Gui;

/// <summary>
/// The first page: five cards (CPU, memory, GPU, disk, network) with the current value and a small history line, the five
/// heaviest apps by product name, and what the observation rules notice. A card opens the matching panel on the
/// Performance page. Updates only while the page is showing.
/// </summary>
internal sealed class OverviewPage : PageBase
{
    private const double HighPercent = 80;
    private const double HighMemoryPercent = 85;

    /// <summary>The observations are re-evaluated every third sample: they look at the last minute, not at this second.</summary>
    private const int ObservationEvery = 3;

    private readonly MetricCard _cpu = new() { Name = "cardCpu" };
    private readonly MetricCard _memory = new() { Name = "cardMemory" };
    private readonly MetricCard _gpu = new() { Name = "cardGpu" };
    private readonly MetricCard _disk = new() { Name = "cardDisk" };
    private readonly MetricCard _network = new() { Name = "cardNetwork" };
    private readonly SectionLabel _topTitle = new() { Name = "overviewTopTitle" };
    private readonly SectionLabel _noteTitle = new() { Name = "overviewNoteTitle" };
    private readonly GridView _top = new() { Name = "overviewTop" };
    private readonly WrapListBox _notes = new() { Name = "overviewNotes" };
    private readonly ToneLabel _topHint = new() { AutoSize = true, Name = "overviewTopHint" };
    private readonly DetailsCache _details;
    private readonly double[] _cpuHistory = new double[SnapshotPump.HistoryLength];
    private readonly double[] _memoryHistory = new double[SnapshotPump.HistoryLength];
    private readonly double[] _gpuHistory = new double[SnapshotPump.HistoryLength];
    private readonly double[] _diskHistory = new double[SnapshotPump.HistoryLength];
    private readonly double[] _networkHistory = new double[SnapshotPump.HistoryLength];

    private TableLayoutPanel? _lower;
    private List<TopApp> _apps = [];
    private Snapshot? _last;
    private int _ticks;

    public OverviewPage(GuiContext context)
        : base(context, "overview", "gui.nav.overview")
    {
        _details = new DetailsCache(context.Services.ProcessDetails, this);
        _details.Loaded += _ =>
        {
            if (_last is not null && Visible)
            {
                UpdateTop(_last);
            }
        };

        _cpu.Activated += (_, _) => Open("cpu");
        _memory.Activated += (_, _) => Open("memory");
        _gpu.Activated += (_, _) => Open("gpu");
        _disk.Activated += (_, _) => Open("disk");
        _network.Activated += (_, _) => Open("network");
        _cpu.SetHistory(_cpuHistory, 0);
        _memory.SetHistory(_memoryHistory, 0);
        _gpu.SetHistory(_gpuHistory, 0);
        _disk.SetHistory(_diskHistory, 0);
        _network.SetHistory(_networkHistory, 0);

        PageLayout.Prepare(
            _top,
            string.Empty,
            (string.Empty, Px(220), HorizontalAlignment.Left),
            (string.Empty, Px(90), HorizontalAlignment.Right),
            (string.Empty, Px(110), HorizontalAlignment.Right));
        _top.Cells = TopCell;
        _top.CellTip = TopTip;
        _top.DoubleClick += (_, _) => OpenApp();
        _top.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                OpenApp();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        _top.FillColumn = 0;
        _top.SizeChanged += (_, _) => _top.FitLastColumn();
        _top.FontChanged += (_, _) => FitTop();
        _top.HandleCreated += (_, _) => FitTop();
        _topHint.SetTone(CellTone.Dim);

        var pad = Px(16);
        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = Px(182),
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(pad, pad, pad, 0),
            Name = "overviewCards",
        };
        var index = 0;
        foreach (var card in new[] { _cpu, _memory, _gpu, _disk, _network })
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, index == 4 ? 0 : Px(12), 0);
            card.TabIndex = index;
            cards.Controls.Add(card, index++, 0);
        }

        var lower = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(pad, pad, pad, pad),
            Name = "overviewLower",
        };
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lower.RowStyles.Add(new System.Windows.Forms.RowStyle(SizeType.AutoSize));
        lower.RowStyles.Add(new System.Windows.Forms.RowStyle(SizeType.Absolute, Px(200)));
        lower.RowStyles.Add(new System.Windows.Forms.RowStyle(SizeType.Percent, 100));
        _lower = lower;
        _topTitle.Margin = new Padding(0, 0, 0, Px(6));
        _noteTitle.Margin = new Padding(Px(16), 0, 0, Px(6));
        _top.Margin = new Padding(0);
        _notes.Dock = DockStyle.Fill;
        _notes.Margin = new Padding(Px(16), 0, 0, 0);
        _topHint.Margin = new Padding(0, Px(6), 0, 0);
        lower.Controls.Add(_topTitle, 0, 0);
        lower.Controls.Add(_noteTitle, 1, 0);
        lower.Controls.Add(_top, 0, 1);
        lower.Controls.Add(_notes, 1, 1);
        lower.SetRowSpan(_notes, 2);
        lower.Controls.Add(_topHint, 0, 2);
        _topHint.MaximumSize = new Size(Px(420), 0);
        _top.TabIndex = 5;
        _notes.TabIndex = 6;

        Controls.Add(lower);
        Controls.Add(cards);
        ApplyLanguage();
    }

    /// <summary>The heaviest-apps list is always five rows tall (and the notes beside it run to the bottom), so the page has no empty middle.</summary>
    private void FitTop()
    {
        if (_lower is null || !_top.IsHandleCreated)
        {
            return;
        }

        var header = _top.VirtualListSize > 0 ? Math.Max(_top.GetItemRect(0).Top, 0) : 0;
        header = header > 0 ? header : _top.Font.Height + Px(10);
        var wanted = header + (_top.RowHeight * 5) + Px(4);
        if (Math.Abs(_lower.RowStyles[1].Height - wanted) > 1)
        {
            _lower.RowStyles[1].Height = wanted;
        }
    }

    internal MetricCard CpuCard => _cpu;

    internal MetricCard MemoryCard => _memory;

    internal MetricCard GpuCard => _gpu;

    internal MetricCard DiskCard => _disk;

    internal MetricCard NetworkCard => _network;

    internal IReadOnlyList<TopApp> Apps => _apps;

    internal WrapListBox Notes => _notes;

    public override void OnActivated()
    {
        Context.Pump.Sampled -= OnSampled;
        Context.Pump.Sampled += OnSampled;
        if (Context.Pump.Latest is { } latest)
        {
            OnSampled(latest);
        }
    }

    public override void OnDeactivated() => Context.Pump.Sampled -= OnSampled;

    public override void ApplyLanguage()
    {
        _topTitle.Text = Strings.Get("gui.overview.top");
        _noteTitle.Text = Strings.Get("gui.overview.notes");
        _topHint.Text = Strings.Get("gui.overview.top.hint");
        _top.AccessibleName = Strings.Get("gui.overview.top");
        _notes.AccessibleName = Strings.Get("gui.overview.notes");
        string[] keys = ["name", "cpu", "memory"];
        for (var i = 0; i < keys.Length; i++)
        {
            _top.Columns[i].Text = Strings.Get("gui.overview.col." + keys[i]);
        }

        if (_last is not null)
        {
            Fill(_last, evaluate: true);
        }
        else
        {
            SetNotes(null);
        }

        _top.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Context.Pump.Sampled -= OnSampled;
        }

        base.Dispose(disposing);
    }

    private void OnSampled(Snapshot snapshot) => Fill(snapshot, evaluate: ++_ticks % ObservationEvery == 1);

    private void Open(string panel) => Context.Navigate("performance", null, panel);

    private void OpenApp()
    {
        if (_top.SelectedRow is >= 0 and var row && row < _apps.Count)
        {
            Context.Navigate("processes", _apps[row].Heaviest);
        }
    }

    private void Fill(Snapshot snapshot, bool evaluate)
    {
        _last = snapshot;
        FillHistory();
        var system = snapshot.System;

        var cpu = system.Cpu;
        var cpuOk = cpu.TotalPercent.TryGet(out var cpuPercent);
        _cpu.Commit(
            Strings.Get("gui.overview.card.cpu"),
            Fmt.Percent(cpu.TotalPercent),
            Join(Strings.Format("gui.overview.cpu.sub", Fmt.Dict(("count", cpu.LogicalProcessors.ToString(CultureInfo.CurrentCulture)))), cpu.CurrentMhz.TryGet(out _) ? Fmt.Mhz(cpu.CurrentMhz) : null),
            cpuOk && cpuPercent >= HighPercent ? Strings.Get("gui.overview.high") : string.Empty,
            cpuOk && cpuPercent >= HighPercent);

        var memory = system.Memory;
        var total = Math.Max(1, memory.TotalPhysicalBytes);
        var used = Math.Max(0, memory.TotalPhysicalBytes - memory.AvailablePhysicalBytes);
        var memoryPercent = used * 100.0 / total;
        _memory.Commit(
            Strings.Get("gui.overview.card.memory"),
            memoryPercent.ToString("0", CultureInfo.CurrentCulture) + " %",
            Fmt.Bytes(used) + " / " + Fmt.Bytes(memory.TotalPhysicalBytes),
            memoryPercent >= HighMemoryPercent ? Strings.Get("gui.overview.high") : string.Empty,
            memoryPercent >= HighMemoryPercent);

        var gpu = system.Gpus.OrderByDescending(g => g.UtilizationPercent.GetOr(-1)).FirstOrDefault();
        if (gpu is null)
        {
            _gpu.Commit(Strings.Get("gui.overview.card.gpu"), Fmt.Dash, EmptyList(system.GpusState, "gui.overview.gpu.none"), string.Empty, false);
        }
        else
        {
            var gpuOk = gpu.UtilizationPercent.TryGet(out var gpuPercent);
            _gpu.Commit(
                Strings.Get("gui.overview.card.gpu"),
                Fmt.Percent(gpu.UtilizationPercent),
                Join(gpu.Name, gpu.TemperatureC.TryGet(out var temp) ? Strings.Format("gui.fmt.temp", Fmt.Dict(("value", temp.ToString("0", CultureInfo.CurrentCulture)))) : null),
                gpuOk && gpuPercent >= HighPercent ? Strings.Get("gui.overview.high") : string.Empty,
                false);
        }

        var disk = system.Disks.OrderByDescending(d => d.ActivePercent.GetOr(-1)).FirstOrDefault();
        var diskRead = system.Disks.Sum(d => d.ReadBytesPerSec.GetOr(0));
        var diskWrite = system.Disks.Sum(d => d.WriteBytesPerSec.GetOr(0));
        if (disk is null)
        {
            _disk.Commit(Strings.Get("gui.overview.card.disk"), Fmt.Dash, EmptyList(system.DisksState, "gui.common.na"), string.Empty, false);
        }
        else
        {
            var diskOk = disk.ActivePercent.TryGet(out var diskPercent);
            _disk.Commit(
                Strings.Get("gui.overview.card.disk"),
                Fmt.Percent(disk.ActivePercent),
                Strings.Format("gui.overview.disk.sub", Fmt.Dict(("read", Fmt.Bytes((long)diskRead) + "/s"), ("write", Fmt.Bytes((long)diskWrite) + "/s"))),
                diskOk && diskPercent >= HighPercent ? Strings.Get("gui.overview.high") : string.Empty,
                diskOk && diskPercent >= HighPercent);
        }

        var send = system.Nics.Sum(n => n.SendBytesPerSec.GetOr(0));
        var receive = system.Nics.Sum(n => n.ReceiveBytesPerSec.GetOr(0));
        _network.Commit(
            Strings.Get("gui.overview.card.network"),
            system.Nics.Count == 0 ? Fmt.Dash : Fmt.Bytes((long)(send + receive)) + "/s",
            system.Nics.Count == 0 ? EmptyList(system.NicsState, "gui.common.na") : Strings.Format("gui.overview.net.sub", Fmt.Dict(("send", Fmt.Bytes((long)send) + "/s"), ("receive", Fmt.Bytes((long)receive) + "/s"))),
            string.Empty,
            false);

        UpdateTop(snapshot);
        if (evaluate)
        {
            SetNotes(snapshot);
        }
    }

    /// <summary>What a card says when its list is empty: the status word when the list could not be read ("none" would be a lie), else the usual text.</summary>
    private static string EmptyList(Value<int> state, string fallbackKey) =>
        ListStatus.ExplainsEmptyList(0, state) ? Strings.Status(state.Status) : Strings.Get(fallbackKey);

    private static string Join(string first, string? second) => string.IsNullOrEmpty(second) ? first : first + "\n" + second;

    /// <summary>Copies the last minute into the cards' arrays (no allocation: the arrays belong to the page).</summary>
    private void FillHistory()
    {
        var history = Context.Pump.History;
        var count = Math.Min(history.Count, SnapshotPump.HistoryLength);
        var offset = history.Count - count;
        var maxNetwork = 1.0;
        for (var i = 0; i < count; i++)
        {
            var system = history[offset + i].System;
            _cpuHistory[i] = system.Cpu.TotalPercent.TryGet(out var c) ? c : double.NaN;
            _memoryHistory[i] = system.Memory.TotalPhysicalBytes > 0
                ? (system.Memory.TotalPhysicalBytes - system.Memory.AvailablePhysicalBytes) * 100.0 / system.Memory.TotalPhysicalBytes
                : double.NaN;
            var gpuBest = double.NaN;
            foreach (var g in system.Gpus)
            {
                if (g.UtilizationPercent.TryGet(out var gp) && (double.IsNaN(gpuBest) || gp > gpuBest))
                {
                    gpuBest = gp;
                }
            }

            _gpuHistory[i] = gpuBest;
            var diskBest = double.NaN;
            foreach (var d in system.Disks)
            {
                if (d.ActivePercent.TryGet(out var dp) && (double.IsNaN(diskBest) || dp > diskBest))
                {
                    diskBest = dp;
                }
            }

            _diskHistory[i] = diskBest;
            var net = 0.0;
            foreach (var n in system.Nics)
            {
                net += n.SendBytesPerSec.GetOr(0) + n.ReceiveBytesPerSec.GetOr(0);
            }

            _networkHistory[i] = net;
            maxNetwork = Math.Max(maxNetwork, net);
        }

        _network.HistoryMaximum = GraphPainter.NiceMaximum(maxNetwork, 100 * 1024);
        _cpu.SetHistory(_cpuHistory, count);
        _memory.SetHistory(_memoryHistory, count);
        _gpu.SetHistory(_gpuHistory, count);
        _disk.SetHistory(_diskHistory, count);
        _network.SetHistory(_networkHistory, count);
    }

    private void UpdateTop(Snapshot snapshot)
    {
        _apps = TopApps.Compute(
            snapshot.Processes,
            snapshot.System.Memory.TotalPhysicalBytes,
            key => _details.TryGet(key) is { } d && d.Product.TryGet(out var product) ? product : null);
        if (_top.VirtualListSize != _apps.Count)
        {
            _top.VirtualListSize = _apps.Count;
        }

        _top.Invalidate();
    }

    private GridCell TopCell(int row, int column)
    {
        if (row < 0 || row >= _apps.Count)
        {
            return default;
        }

        var app = _apps[row];
        return column switch
        {
            0 => new GridCell(app.ProcessCount > 1 ? app.Name + " (" + app.ProcessCount.ToString(CultureInfo.CurrentCulture) + ")" : app.Name),
            1 => new GridCell(app.Cpu.ToString("0.0", CultureInfo.CurrentCulture) + " %", app.Cpu >= 50 ? CellTone.Accent : CellTone.Normal),
            2 => new GridCell(Fmt.Bytes(app.MemoryBytes)),
            _ => default,
        };
    }

    private string? TopTip(int row, int column) =>
        row >= 0 && row < _apps.Count && column == 0 ? _apps[row].Name + Environment.NewLine + Strings.Get("gui.overview.top.tip") : null;

    private void SetNotes(Snapshot? snapshot)
    {
        if (snapshot is null)
        {
            _notes.SetItems([new WrapItem(string.Empty, Strings.Get("gui.overview.notes.none"), false)]);
            return;
        }

        var observations = Context.Services.Observations.Evaluate(new ObservationInput(snapshot, Context.Pump.History));
        if (observations.Count == 0)
        {
            _notes.SetItems([new WrapItem(string.Empty, Strings.Get("gui.overview.notes.none"), false)]);
            return;
        }

        _notes.SetItems([.. observations.Select(o => new WrapItem(
            Strings.Get("gui.overview.certainty." + o.Certainty.ToString().ToLowerInvariant()),
            Strings.Render(o),
            o.Certainty == ObservationCertainty.Fact))]);
    }
}

internal static class OverviewStrings
{
    public static readonly StringEntry[] All =
    [
        new("gui.overview.card.cpu", "CPU", "CPU"),
        new("gui.overview.card.memory", "メモリ", "Memory"),
        new("gui.overview.card.gpu", "GPU", "GPU"),
        new("gui.overview.card.disk", "ディスクの稼働率", "Disk activity"),
        new("gui.overview.card.network", "ネットワーク（送受信合計）", "Network (send + receive)"),
        new("gui.overview.cpu.sub", "論理プロセッサ {count} 個", "{count} logical processors"),
        new("gui.overview.disk.sub", "読み取り {read}\n書き込み {write}", "Read {read}\nWrite {write}"),
        new("gui.overview.net.sub", "送信 {send}\n受信 {receive}", "Send {send}\nReceive {receive}"),
        new("gui.overview.gpu.none", "GPU が見つかりません", "No GPU found"),
        new("gui.overview.high", "高め", "High"),
        new("gui.overview.top", "いま重いアプリ", "Heaviest apps right now"),
        new("gui.overview.top.hint", "同じアプリのプロセスは 1 行にまとめています。CPU とメモリの両方を見て並べています。", "Processes of the same app are added into one line, ranked by CPU and memory together."),
        new("gui.overview.top.tip", "ダブルクリックで、プロセスの一覧を開きます", "Double-click to open it in the process list"),
        new("gui.overview.col.name", "アプリ", "App"),
        new("gui.overview.col.cpu", "CPU", "CPU"),
        new("gui.overview.col.memory", "メモリ", "Memory"),
        new("gui.overview.notes", "気づいたこと", "What stands out"),
        new("gui.overview.notes.none", "いまのところ、目立った点はありません", "Nothing stands out right now"),
        new("gui.overview.certainty.fact", "[事実]", "[Fact]"),
        new("gui.overview.certainty.candidate", "[可能性]", "[Possible]"),
    ];
}
