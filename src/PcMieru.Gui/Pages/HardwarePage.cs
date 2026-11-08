using System.Globalization;
using System.Text.Json.Nodes;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Gui;

/// <summary>
/// The PC's parts. A tree of categories on the left (items such as each GPU, memory module or drive volume below them) and,
/// on the right, a property grid with the value, the status and the source of every value. Ids and serial numbers are
/// folded under a "details" row. Copy as text or JSON, with personal data hidden by default. What could not be read is
/// shown as such, with the reason on hover, never as an empty cell or a zero.
/// </summary>
internal sealed class HardwarePage : PageBase
{
    private const int LabelColumn = 0;
    private const int ValueColumn = 1;
    private const int StatusColumn = 2;
    private const int SourceColumn = 3;

    private readonly TreeView _tree = new() { Name = "partsTree" };
    private readonly GridView _grid = new() { Name = "partsGrid" };
    private readonly Button _reload = new() { Name = "partsReload", AutoSize = true };
    private readonly Button _copyText = new() { Name = "partsCopyText", AutoSize = true };
    private readonly Button _copyJson = new() { Name = "partsCopyJson", AutoSize = true };
    private readonly CheckBox _redact = new() { Name = "partsRedact", AutoSize = true, Checked = true };
    private readonly Label _stamp = new() { AutoSize = true, Name = "partsStamp", Margin = new Padding(0, 0, 16, 0) };
    private readonly ToneLabel _hint = new() { AutoSize = true, Name = "partsHint" };
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private readonly List<Line> _lines = [];

    private JsonObject? _root;
    private List<PartsNode> _nodes = [];
    private DateTime _collected;
    private string _selectedId = "cpu";
    private bool _expertOpen;
    private bool _loaded;
    private bool _loading;
    private bool _building;

    public HardwarePage(GuiContext context)
        : base(context, "hardware", "gui.nav.hardware")
    {
        _expertOpen = context.Settings.GetBool("hw.expert", false);

        _tree.Dock = DockStyle.Fill;
        _tree.BorderStyle = BorderStyle.None;
        _tree.HideSelection = false;
        _tree.ShowLines = false;
        _tree.FullRowSelect = true;
        _tree.ItemHeight = Px(28);
        _tree.AfterSelect += (_, e) =>
        {
            if (!_building && e.Node?.Tag is PartsNode node)
            {
                _selectedId = node.Id;
                ShowNode(node);
            }
        };

        PageLayout.Prepare(
            _grid,
            Strings.Get("gui.nav.hardware"),
            (string.Empty, Px(210), HorizontalAlignment.Left),
            (string.Empty, Px(380), HorizontalAlignment.Left),
            (string.Empty, Px(110), HorizontalAlignment.Left),
            (string.Empty, Px(120), HorizontalAlignment.Left));
        _grid.Cells = Cell;
        _grid.RowStyles = row => row >= 0 && row < _lines.Count && _lines[row].Row is null ? RowStyle.Header : RowStyle.Normal;
        _grid.CellTip = CellTip;
        _grid.ExpanderClicked += ToggleExpert;
        _grid.KeyDown += OnGridKey;
        _grid.SizeChanged += (_, _) => _grid.FitLastColumn();

        _reload.MinimumSize = new Size(Px(88), Px(30));
        _copyText.MinimumSize = new Size(Px(88), Px(30));
        _copyJson.MinimumSize = new Size(Px(88), Px(30));
        _reload.Click += (_, _) => RefreshNow();
        _copyText.Click += (_, _) => Copy(json: false);
        _copyJson.Click += (_, _) => Copy(json: true);
        _redact.Checked = Context.Services.Redactor.Enabled && context.Settings.GetBool("hw.redact", true);
        _redact.Enabled = Context.Services.Redactor.Enabled;
        _redact.CheckedChanged += (_, _) => context.Settings.SetBool("hw.redact", _redact.Checked);

        _reload.TabIndex = 0;
        _tree.TabIndex = 1;
        _grid.TabIndex = 2;
        _redact.TabIndex = 3;
        _copyText.TabIndex = 4;
        _copyJson.TabIndex = 5;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Name = "partsBody" };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(260)));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new System.Windows.Forms.RowStyle(SizeType.Percent, 100));
        _tree.Margin = Padding.Empty;
        _grid.Margin = Padding.Empty;
        body.Controls.Add(_tree, 0, 0);
        body.Controls.Add(_grid, 1, 0);

        var pad = Px(12);
        Controls.Add(body);
        Controls.Add(PageLayout.Footer("partsFooter", pad, _stamp, _hint));
        Controls.Add(PageLayout.Bar("partsBar", pad, [_reload], [_redact, _copyText, _copyJson]));
        ApplyLanguage();
    }

    internal TreeView Tree => _tree;

    internal GridView Grid => _grid;

    internal CheckBox RedactBox => _redact;

    internal IReadOnlyList<PartsNode> Nodes => _nodes;

    internal bool ExpertOpen => _expertOpen;

    internal int LineCount => _lines.Count;

    internal PartsRow? RowAt(int index) => index >= 0 && index < _lines.Count ? _lines[index].Row : null;

    public override bool IsReady => _loaded;

    internal bool Loaded => _loaded;

    public override void OnActivated()
    {
        if (!_loaded && !_loading)
        {
            Run(() => LoadAsync(invalidate: false));
        }
    }

    public override void RefreshNow() => Run(() => LoadAsync(invalidate: true));

    public override void ApplyLanguage()
    {
        _reload.Text = Strings.Get("gui.common.reload");
        _copyText.Text = Strings.Get("gui.hw.copy.text");
        _copyJson.Text = Strings.Get("gui.hw.copy.json");
        _redact.Text = Strings.Get("gui.hw.redact");
        _tips.SetToolTip(_redact, Strings.Get(Context.Services.Redactor.Enabled ? "gui.hw.redact.tip" : "gui.hw.redact.off"));
        _tips.SetToolTip(_copyText, Strings.Get("gui.hw.copy.tip"));
        _tips.SetToolTip(_copyJson, Strings.Get("gui.hw.copy.tip"));
        _tree.AccessibleName = Strings.Get("gui.hw.tree");
        _grid.AccessibleName = Strings.Get("gui.nav.hardware");
        string[] keys = ["item", "value", "status", "source"];
        for (var i = 0; i < keys.Length && i < _grid.Columns.Count; i++)
        {
            _grid.Columns[i].Text = Strings.Get("gui.hw.col." + keys[i]);
        }

        _hint.SetTone(CellTone.Dim);
        _hint.Text = Strings.Get("gui.hw.hint");
        UpdateStamp();
        if (_loaded)
        {
            RebuildTree();
        }

        _grid.Invalidate();
    }

    public override void SaveState() => Context.Settings.SetBool("hw.expert", _expertOpen);

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        _tree.ItemHeight = Px(28);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task LoadAsync(bool invalidate)
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        _stamp.Text = Strings.Get("gui.common.loading");
        _reload.Enabled = false;
        try
        {
            if (invalidate)
            {
                Context.Services.Hardware.Invalidate();
            }

            var info = await Context.Services.Hardware.GetAsync(HardwareCategory.All).ConfigureAwait(true);
            Show(info);
        }
        finally
        {
            _loading = false;
            _reload.Enabled = true;
            UpdateStamp();
        }
    }

    /// <summary>Takes a finished read: rebuilds the tree from its JSON form and keeps the selection when the same part is still there.</summary>
    internal void Show(HardwareInfo info)
    {
        _root = PcMieruJson.ToNode(info) as JsonObject;
        _collected = info.CollectedAtUtc;
        _nodes = _root is null ? [] : PartsModel.Build(_root);
        _loaded = true;
        RebuildTree();
        UpdateStamp();
    }

    private void RebuildTree()
    {
        _building = true;
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            TreeNode? select = null;
            foreach (var node in _nodes)
            {
                var item = Add(_tree.Nodes, node);
                select ??= item;
                if (node.Id == _selectedId)
                {
                    select = item;
                }

                if (FindTreeNode(item, _selectedId) is { } inner)
                {
                    select = inner;
                    item.Expand();
                }
            }

            _tree.SelectedNode = select;
        }
        finally
        {
            _tree.EndUpdate();
            _building = false;
        }

        ShowNode(_tree.SelectedNode?.Tag as PartsNode);
    }

    private static TreeNode Add(TreeNodeCollection into, PartsNode node)
    {
        var item = into.Add(node.DisplayTitle);
        item.Tag = node;
        foreach (var child in node.Children)
        {
            Add(item.Nodes, child);
        }

        return item;
    }

    private static TreeNode? FindTreeNode(TreeNode from, string id)
    {
        foreach (TreeNode child in from.Nodes)
        {
            if (child.Tag is PartsNode node && node.Id == id)
            {
                return child;
            }

            if (FindTreeNode(child, id) is { } inner)
            {
                return inner;
            }
        }

        return null;
    }

    private void ShowNode(PartsNode? node)
    {
        _lines.Clear();
        if (node is not null)
        {
            var rows = PartsModel.Rows(node);
            var expert = new List<PartsRow>();
            foreach (var row in rows)
            {
                if (row.Expert)
                {
                    expert.Add(row);
                }
                else
                {
                    _lines.Add(new Line(row, 0));
                }
            }

            if (expert.Count > 0)
            {
                _lines.Add(new Line(null, expert.Count));
                if (_expertOpen)
                {
                    foreach (var row in expert)
                    {
                        _lines.Add(new Line(row, 0));
                    }
                }
            }
        }

        _grid.VirtualListSize = _lines.Count;
        _grid.Invalidate();
    }

    private void ToggleExpert(int row)
    {
        if (row < 0 || row >= _lines.Count || _lines[row].Row is not null)
        {
            return;
        }

        _expertOpen = !_expertOpen;
        Context.Settings.SetBool("hw.expert", _expertOpen);
        ShowNode(_tree.SelectedNode?.Tag as PartsNode);
        _grid.SelectRow(row, scroll: false);
    }

    private void OnGridKey(object? sender, KeyEventArgs e)
    {
        var row = _grid.SelectedRow;
        if (row < 0 || row >= _lines.Count || e.Modifiers != Keys.None)
        {
            return;
        }

        var header = _lines[row].Row is null;
        if (header && (e.KeyCode is Keys.Enter or Keys.Space || (e.KeyCode == Keys.Right && !_expertOpen) || (e.KeyCode == Keys.Left && _expertOpen)))
        {
            ToggleExpert(row);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private GridCell Cell(int row, int column)
    {
        if (row < 0 || row >= _lines.Count)
        {
            return new GridCell(string.Empty);
        }

        var line = _lines[row];
        if (line.Row is not { } r)
        {
            return column == LabelColumn
                ? new GridCell(Strings.Format("gui.hw.expert", Fmt.Dict(("count", line.ExpertCount.ToString(CultureInfo.CurrentCulture)))))
                {
                    Bold = true,
                    Glyph = _expertOpen ? ExpanderGlyph.Expanded : ExpanderGlyph.Collapsed,
                }
                : new GridCell(string.Empty);
        }

        var ok = r.Status == ValueStatus.Ok;
        return column switch
        {
            LabelColumn => new GridCell(r.Label, CellTone.Dim),
            ValueColumn => new GridCell(r.Text, ok ? CellTone.Normal : CellTone.Dim),
            StatusColumn => new GridCell(PartsModel.StatusText(r.Status), CellTone.Dim),
            SourceColumn => new GridCell(r.Source, CellTone.Dim),
            _ => new GridCell(string.Empty),
        };
    }

    private string? CellTip(int row, int column)
    {
        if (row < 0 || row >= _lines.Count || _lines[row].Row is not { } r)
        {
            return null;
        }

        if (r.Status != ValueStatus.Ok)
        {
            return Reason(r);
        }

        return column == ValueColumn && r.Text.Length > 40 ? r.Text : null;
    }

    /// <summary>Why a value is missing: its status, the reason the reader gave, and where it looked.</summary>
    internal static string Reason(PartsRow row)
    {
        var status = PartsModel.StatusText(row.Status);
        var source = row.Source.Length > 0 ? row.Source : "-";
        return string.IsNullOrWhiteSpace(row.Detail)
            ? Strings.Format("gui.hw.tip", Fmt.Dict(("status", status), ("source", source)))
            : Strings.Format("gui.hw.tip.detail", Fmt.Dict(("status", status), ("detail", row.Detail), ("source", source)));
    }

    private void UpdateStamp()
    {
        if (_loading)
        {
            return;
        }

        _stamp.Text = _loaded
            ? Strings.Format("gui.hw.stamp", Fmt.Dict(("time", _collected.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture))))
            : string.Empty;
    }

    /// <summary>The text or JSON that Copy puts on the clipboard. Redacted from a copy of the tree, so the screen keeps its values.</summary>
    internal string BuildCopy(bool json, bool redact)
    {
        if (_root is null)
        {
            return string.Empty;
        }

        var redactor = Context.Services.Redactor;
        var tree = redact ? (redactor.RedactJson(_root.DeepClone()) as JsonObject ?? _root) : _root;
        if (json)
        {
            return tree.ToJsonString(PcMieruJson.Indented);
        }

        var text = PartsModel.ToText(PartsModel.Build(tree));
        return redact ? redactor.RedactText(text) : text;
    }

    private void Copy(bool json)
    {
        if (_root is null)
        {
            return;
        }

        var redact = _redact.Checked && Context.Services.Redactor.Enabled;
        Context.CopyText(BuildCopy(json, redact), redact ? "gui.hw.copied.redacted" : "gui.hw.copied.plain");
    }

    private readonly record struct Line(PartsRow? Row, int ExpertCount);
}
