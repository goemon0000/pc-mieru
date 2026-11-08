using System.ComponentModel;

namespace PcMieru.Gui;

/// <summary>
/// The right-hand pane of the Processes page: what the selected process is. The labels are created once; showing another
/// process only changes their text, so a sample never allocates controls. A fact that could not be read shows a grey dash
/// whose tooltip says why (denied, not collected, gone...), never a zero.
/// </summary>
internal sealed class ProcessDetailPane : UserControl
{
    private enum Row
    {
        Executable,
        Product,
        Description,
        Company,
        Signature,
        Path,
        Parent,
        Started,
        User,
        Architecture,
        Efficiency,
        Services,
        Memory,
        Counts,
    }

    private static readonly string[] RowKeys =
    [
        "gui.proc.detail.exe",
        "gui.proc.detail.product",
        "gui.proc.detail.description",
        "gui.proc.detail.company",
        "gui.proc.detail.signature",
        "gui.proc.detail.path",
        "gui.proc.detail.parent",
        "gui.proc.detail.started",
        "gui.proc.detail.user",
        "gui.proc.detail.arch",
        "gui.proc.detail.efficiency",
        "gui.proc.detail.services",
        "gui.proc.detail.memory",
        "gui.proc.detail.counts",
    ];

    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, Name = "detailScroll" };
    private readonly TableLayoutPanel _table = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Name = "detailTable" };
    private readonly ToneLabel _title = new() { AutoSize = true, Name = "detailTitle", Anchor = AnchorStyles.Left };
    private readonly PictureBox _icon = new() { Name = "detailIcon", SizeMode = PictureBoxSizeMode.Zoom, Visible = false, Anchor = AnchorStyles.Left };
    private readonly FlowLayoutPanel _header = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Name = "detailHeader" };
    private readonly ToneLabel _empty = new() { AutoSize = true, Name = "detailEmpty" };
    private readonly ToneLabel[] _keys = new ToneLabel[RowKeys.Length];
    private readonly ToneLabel[] _values = new ToneLabel[RowKeys.Length];
    private readonly ToneLabel _knownTitle = new() { AutoSize = true, Name = "detailKnownTitle" };
    private readonly ToneLabel _knownText = new() { AutoSize = true, Name = "detailKnownText" };
    private readonly ToneLabel _protect = new() { AutoSize = true, Name = "detailProtect" };
    private readonly Button _cmdToggle = new() { AutoSize = true, Name = "detailCmdToggle" };
    private readonly TextBox _cmdBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Name = "detailCmdBox", Visible = false };
    private readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Name = "detailActions" };
    private readonly Button _end = new() { AutoSize = true, Name = "detailEnd" };
    private readonly Button _folder = new() { AutoSize = true, Name = "detailFolder" };
    private readonly Button _web = new() { AutoSize = true, Name = "detailWeb" };
    private readonly ToolTip _tip = new() { ShowAlways = true };

    /// <summary>Logical pixels of the program icon beside the name, and the gap after it.</summary>
    private const int IconSide = 32;

    private const int IconGap = 10;

    private bool _contentShown;

    private Font? _titleFont;
    private ProcessInfo? _process;
    private ProcessDetails? _details;
    private ProtectionVerdict? _verdict;
    private string _parentText = string.Empty;
    private string? _goneName;
    private ProcessKey? _shownKey;
    private bool _cmdVisible;

    public ProcessDetailPane()
    {
        Name = "processDetail";
        Dock = DockStyle.Fill;

        for (var i = 0; i < RowKeys.Length; i++)
        {
            _keys[i] = new ToneLabel { AutoSize = true, Name = "detailKey" + i, Margin = new Padding(0, LogicalToDeviceUnits(4), LogicalToDeviceUnits(10), LogicalToDeviceUnits(4)), Anchor = AnchorStyles.Top | AnchorStyles.Left };
            _keys[i].SetTone(CellTone.Dim);
            _values[i] = new ToneLabel { AutoSize = true, Name = "detailValue" + i, Margin = new Padding(0, LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(4)), Anchor = AnchorStyles.Top | AnchorStyles.Left };
        }

        _table.Padding = new Padding(LogicalToDeviceUnits(12), LogicalToDeviceUnits(10), LogicalToDeviceUnits(12), LogicalToDeviceUnits(10));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _empty.SetTone(CellTone.Dim);
        _empty.Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(8));
        _title.Margin = new Padding(0);
        _header.Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(8));
        _header.Padding = new Padding(0);
        _icon.Size = new Size(LogicalToDeviceUnits(IconSide), LogicalToDeviceUnits(IconSide));
        _icon.Margin = new Padding(0, 0, LogicalToDeviceUnits(IconGap), 0);
        _header.Controls.Add(_icon);
        _header.Controls.Add(_title);
        _knownTitle.Margin = new Padding(0, LogicalToDeviceUnits(12), 0, LogicalToDeviceUnits(2));
        _knownText.Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(4));
        _protect.Margin = new Padding(0, LogicalToDeviceUnits(10), 0, LogicalToDeviceUnits(4));
        _protect.SetTone(CellTone.Accent);
        _cmdToggle.Margin = new Padding(0, LogicalToDeviceUnits(10), 0, LogicalToDeviceUnits(4));
        _cmdBox.Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(4));
        _cmdBox.Height = LogicalToDeviceUnits(84);

        var row = 0;
        AddSpan(_empty, row++);
        AddSpan(_header, row++);
        for (var i = 0; i < RowKeys.Length; i++)
        {
            _table.Controls.Add(_keys[i], 0, row);
            _table.Controls.Add(_values[i], 1, row);
            row++;
        }

        AddSpan(_knownTitle, row++);
        AddSpan(_knownText, row++);
        AddSpan(_protect, row++);
        AddSpan(_cmdToggle, row++);
        AddSpan(_cmdBox, row);

        _actions.Padding = new Padding(LogicalToDeviceUnits(12), LogicalToDeviceUnits(8), LogicalToDeviceUnits(12), LogicalToDeviceUnits(10));
        foreach (var button in new[] { _end, _folder, _web })
        {
            button.MinimumSize = new Size(0, LogicalToDeviceUnits(30));
            button.Margin = new Padding(0, 0, LogicalToDeviceUnits(8), LogicalToDeviceUnits(4));
            _actions.Controls.Add(button);
        }

        _scroll.Controls.Add(_table);
        Controls.Add(_scroll);
        Controls.Add(_actions);

        _cmdToggle.Click += (_, _) =>
        {
            _cmdVisible = !_cmdVisible;
            ApplyCommandLineState();
        };
        _end.Click += (_, _) => EndRequested?.Invoke();
        _folder.Click += (_, _) => FolderRequested?.Invoke();
        _web.Click += (_, _) => WebRequested?.Invoke();

        ApplyLanguage();
        ShowNothing();
    }

    public event Action? EndRequested;

    public event Action? FolderRequested;

    public event Action? WebRequested;

    public bool ShowsProcess => _process is not null;

    public void ApplyLanguage()
    {
        var win10 = Fmt.IsWindows10;
        for (var i = 0; i < RowKeys.Length; i++)
        {
            _keys[i].Text = Strings.Get(i == (int)Row.Efficiency && win10 ? "gui.proc.detail.efficiency.win10" : RowKeys[i]);
        }

        _empty.Text = Strings.Get("gui.proc.detail.none");
        _knownTitle.Text = Strings.Get("gui.proc.known");
        _end.Text = Strings.Get("gui.proc.act.terminate");
        _folder.Text = Strings.Get("gui.proc.act.folder");
        _web.Text = Strings.Get("gui.proc.act.web");
        _tip.SetToolTip(_web, Strings.Get("gui.proc.act.web.tip"));
        _cmdToggle.Text = Strings.Get(_cmdVisible ? "gui.proc.cmdline.hide" : "gui.proc.cmdline.show");
        if (_process is { } process)
        {
            ShowProcess(process, _details, _verdict, _parentText);
        }
        else if (_goneName is { } gone)
        {
            ShowGone(gone);
        }

        Relayout();
    }

    public void ShowNothing()
    {
        _process = null;
        _details = null;
        _verdict = null;
        _goneName = null;
        _shownKey = null;
        _icon.Image = null;
        SetVisibility(content: false, empty: true);
        _title.Text = string.Empty;
    }

    /// <summary>The selected process ended. The pane keeps its name so the user sees what happened, with the facts blanked.</summary>
    public void ShowGone(string name)
    {
        _process = null;
        _details = null;
        _verdict = null;
        _goneName = name;
        _shownKey = null;
        _icon.Image = null;
        SetVisibility(content: false, empty: false);
        _title.Text = name;
        _empty.Text = Strings.Get("gui.proc.detail.gone");
        _empty.Visible = true;
        _end.Enabled = false;
        _folder.Enabled = false;
    }

    public void ShowProcess(ProcessInfo process, ProcessDetails? details, ProtectionVerdict? verdict, string parentText)
    {
        if (_shownKey != process.Key)
        {
            _shownKey = process.Key;
            _icon.Image = null;
            _cmdVisible = false;
            ApplyCommandLineState();
        }

        _process = process;
        _details = details;
        _verdict = verdict;
        _parentText = parentText;
        _goneName = null;
        _empty.Text = Strings.Get("gui.proc.detail.none");
        SetVisibility(content: true, empty: false);
        _title.Text = FriendlyName(details, process.Name) ?? process.Name;

        // The title leads with what the program calls itself; the file name it runs as is always here, as it is.
        Set(Row.Executable, process.Name, CellTone.Normal, null);
        Set(Row.Parent, parentText.Length > 0 ? parentText : Fmt.Dash, parentText.Length > 0 ? CellTone.Normal : CellTone.Dim, null);
        Set(Row.Started, Fmt.Started(process.Key), CellTone.Normal, null);
        UpdateNumbers(process);

        if (details is null)
        {
            var loading = Strings.Get("gui.common.loading");
            foreach (var row in new[] { Row.Product, Row.Description, Row.Company, Row.Signature, Row.Path, Row.User, Row.Architecture, Row.Efficiency, Row.Services })
            {
                Set(row, loading, CellTone.Dim, null);
            }

            _knownTitle.Visible = false;
            _knownText.Visible = false;
            _cmdToggle.Visible = false;
            _cmdBox.Visible = false;
            _folder.Enabled = false;
        }
        else
        {
            FromValue(Row.Product, details.Product);
            FromValue(Row.Description, details.Description);
            FromValue(Row.Company, details.Company);
            FromValue(Row.Path, details.Path);
            FromValue(Row.User, details.Owner);
            FromValue(Row.Architecture, details.Architecture);
            ShowSignature(details.Signature);
            if (details.EfficiencyMode.TryGet(out var efficient))
            {
                Set(Row.Efficiency, Strings.Get(efficient ? "gui.proc.detail.on" : "gui.proc.detail.off"), CellTone.Normal, null);
            }
            else
            {
                Set(Row.Efficiency, Fmt.Dash, CellTone.Dim, Fmt.MissingTip(details.EfficiencyMode));
            }

            if (details.ServiceNames.Count > 0)
            {
                Set(Row.Services, string.Join(", ", details.ServiceNames), CellTone.Normal, null);
            }
            else
            {
                Set(Row.Services, Strings.Get("gui.proc.detail.nothing"), CellTone.Dim, null);
            }

            ShowKnown(details.Known);
            _cmdToggle.Visible = true;
            _cmdBox.Text = details.CommandLine.TryGet(out var line) ? line : Fmt.Dash;
            ApplyCommandLineState();
            _folder.Enabled = details.Path.IsOk;
            _tip.SetToolTip(_folder, details.Path.IsOk ? null : Strings.Get("gui.proc.act.off.path"));
        }

        var allowed = verdict?.MayOperate == true;
        _end.Enabled = allowed;
        _tip.SetToolTip(_end, allowed ? null : ProtectionText(verdict));
        if (verdict is { MayOperate: false } blocked)
        {
            _protect.Text = ProtectionText(blocked);
            _protect.Visible = true;
        }
        else
        {
            _protect.Visible = false;
        }

        Relayout();
    }

    /// <summary>Per-sample refresh of the two rows that move. Setting an unchanged Text is a no-op, so this never repaints needlessly.</summary>
    public void UpdateNumbers(ProcessInfo process)
    {
        Set(Row.Memory, Fmt.Bytes(process.PrivateWorkingSetBytes), CellTone.Normal, null);
        Set(Row.Counts, Fmt.Int(process.ThreadCount) + " / " + Fmt.Int(process.HandleCount), CellTone.Normal, null);
    }

    /// <summary>True when an icon is on screen beside the name. Only meaningful while the page is shown (the smoke run checks it there).</summary>
    internal bool HasIcon => _icon.Visible && _icon.Image is not null;

    /// <summary>The program icon beside the name. Not owned by the pane (the page's cache owns it). Null hides the slot.</summary>
    public void SetIcon(Image? icon)
    {
        _icon.Image = icon;
        // Own flag, not _title.Visible: that one reads false while the whole page is hidden, and the icon would stay hidden for good.
        _icon.Visible = icon is not null && _contentShown;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _titleFont?.Dispose();
        _titleFont = new Font(Font.FontFamily, Font.Size * 1.25f, FontStyle.Bold, GraphicsUnit.Point);
        _title.Font = _titleFont;
        _knownTitle.Font = new Font(Font, FontStyle.Bold);
        Relayout();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Relayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _titleFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// What the program calls itself: its file description ("Google Chrome"), else its product name. Null while the details are
    /// not in, when neither is known, or when it is just the exe name again.
    /// </summary>
    internal static string? FriendlyName(ProcessDetails? details, string exeName)
    {
        if (details is null)
        {
            return null;
        }

        foreach (var value in new[] { details.Description, details.Product })
        {
            if (value.TryGet(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                var name = text.Trim();
                if (!string.Equals(name, exeName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name + ".exe", exeName, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return null;
    }

    internal static string ProtectionText(ProtectionVerdict? verdict)
    {
        var reason = string.IsNullOrWhiteSpace(verdict?.Reason) ? Fmt.Dash : verdict!.Reason!;
        return Strings.Format(
            verdict?.Kind == ProtectionKind.Protected ? "gui.proc.protected.protected" : "gui.proc.protected.unknown",
            Fmt.Dict(("reason", reason)));
    }

    private void AddSpan(Control control, int row)
    {
        _table.Controls.Add(control, 0, row);
        _table.SetColumnSpan(control, 2);
    }

    private void SetVisibility(bool content, bool empty)
    {
        _empty.Visible = empty;
        _title.Visible = content;
        _contentShown = content;
        _icon.Visible = content && _icon.Image is not null;
        foreach (var label in _keys)
        {
            label.Visible = content;
        }

        foreach (var label in _values)
        {
            label.Visible = content;
        }

        if (!content)
        {
            _knownTitle.Visible = false;
            _knownText.Visible = false;
            _protect.Visible = false;
            _cmdToggle.Visible = false;
            _cmdBox.Visible = false;
            _end.Enabled = false;
            _folder.Enabled = false;
            _web.Enabled = false;
        }
        else
        {
            _web.Enabled = true;
        }
    }

    private void Set(Row row, string text, CellTone tone, string? tip)
    {
        var label = _values[(int)row];
        label.Text = text;
        label.SetTone(tone);
        if (!Equals(label.Tag as string, tip))
        {
            label.Tag = tip;
            _tip.SetToolTip(label, tip);
        }
    }

    private void FromValue(Row row, Value<string> value)
    {
        if (value.TryGet(out var text) && text.Length > 0)
        {
            Set(row, text, CellTone.Normal, null);
        }
        else
        {
            Set(row, Fmt.Dash, CellTone.Dim, value.IsOk ? null : Fmt.MissingTip(value));
        }
    }

    private void ShowSignature(Value<SignatureInfo> value)
    {
        if (!value.TryGet(out var info))
        {
            Set(Row.Signature, Fmt.Dash, CellTone.Dim, Fmt.MissingTip(value));
            return;
        }

        switch (info.State)
        {
            case SignatureState.Unsigned:
                Set(Row.Signature, Strings.Get("gui.proc.sig.unsigned"), CellTone.Normal, null);
                break;
            case SignatureState.Invalid:
                Set(Row.Signature, Strings.Get("gui.proc.sig.invalid"), CellTone.Accent, null);
                break;
            default:
                Set(
                    Row.Signature,
                    string.IsNullOrWhiteSpace(info.Signer)
                        ? Strings.Get("gui.proc.sig.signed")
                        : Strings.Format("gui.proc.sig.signedby", Fmt.Dict(("signer", info.Signer))),
                    CellTone.Normal,
                    null);
                break;
        }
    }

    private void ShowKnown(KnownProcess? known)
    {
        if (known is null)
        {
            _knownTitle.Visible = false;
            _knownText.Visible = false;
            return;
        }

        var text = Strings.Current == Language.Ja ? known.DescriptionJa : known.DescriptionEn;
        if (!known.PathMatches)
        {
            text += Environment.NewLine + Strings.Get("gui.proc.known.path");
        }
        else if (!known.SignerMatches)
        {
            text += Environment.NewLine + Strings.Get("gui.proc.known.signer");
        }

        _knownText.Text = text;
        _knownTitle.Visible = true;
        _knownText.Visible = true;
    }

    private void ApplyCommandLineState()
    {
        _cmdBox.Visible = _cmdVisible && _cmdToggle.Visible;
        _cmdToggle.Text = Strings.Get(_cmdVisible ? "gui.proc.cmdline.hide" : "gui.proc.cmdline.show");
    }

    /// <summary>Wrapping labels need a maximum width, or they grow past the pane instead of breaking the line.</summary>
    private void Relayout()
    {
        if (_keys[0] is null || _values[^1] is null)
        {
            return;
        }

        var keyWidth = 0;
        foreach (var label in _keys)
        {
            label.MaximumSize = new Size(LogicalToDeviceUnits(150), 0);
            keyWidth = Math.Max(keyWidth, label.GetPreferredSize(Size.Empty).Width);
        }

        var inner = Math.Max(
            _scroll.ClientSize.Width - _table.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth,
            LogicalToDeviceUnits(160));
        var valueWidth = Math.Max(inner - keyWidth - LogicalToDeviceUnits(14), LogicalToDeviceUnits(90));
        foreach (var label in _values)
        {
            label.MaximumSize = new Size(valueWidth, 0);
        }

        foreach (var label in new[] { _empty, _knownTitle, _knownText, _protect })
        {
            label.MaximumSize = new Size(inner, 0);
        }

        _title.MaximumSize = new Size(Math.Max(inner - LogicalToDeviceUnits(IconSide + IconGap), LogicalToDeviceUnits(60)), 0);

        _cmdBox.Width = inner;
        _cmdBox.MinimumSize = new Size(inner, LogicalToDeviceUnits(84));
    }
}
