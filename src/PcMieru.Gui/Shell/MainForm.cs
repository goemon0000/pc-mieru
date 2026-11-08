using System.Globalization;

namespace PcMieru.Gui;

/// <summary>Navigation on the left, one page at a time on the right, a thin status bar below.</summary>
internal sealed class MainForm : Form
{
    private readonly GuiContext _context;
    private readonly Font _systemFont = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
    private Font? _sizedFont;
    private readonly NavListBox _nav = new() { Name = "nav" };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, Name = "pageHost" };
    private readonly Panel _statusBar = new() { Dock = DockStyle.Bottom, Name = "statusBar", AccessibleRole = AccessibleRole.StatusBar };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Name = "statusText" };
    private readonly Label _updated = new() { AutoSize = false, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight, Name = "updated" };
    private readonly Button _pause = new() { Name = "pauseButton", TabStop = true, UseVisualStyleBackColor = false };
    private readonly ComboBox _interval = new() { Name = "intervalCombo", DropDownStyle = ComboBoxStyle.DropDownList, TabStop = true };
    private readonly Panel _controls = new() { Dock = DockStyle.Right, Name = "statusControls" };
    private readonly ToolTip _tips = new();
    private readonly PageBase?[] _pages = new PageBase?[PageCatalog.All.Count];
    private int _current = -1;
    private bool _rebuildingNav;

    /// <summary>
    /// The pause button and the interval box are the same height and centred in the bar. A docked combo box would stay at
    /// its own (smaller) height at the top of the bar, next to a button as tall as the bar.
    /// </summary>
    private void PlaceStatusControls()
    {
        var comboHeight = _interval.Height;
        var buttonHeight = Math.Max(comboHeight, _pause.GetPreferredSize(Size.Empty).Height);
        var barHeight = _controls.ClientSize.Height;
        var right = _controls.ClientSize.Width - LogicalToDeviceUnits(12);
        _interval.SetBounds(right - _interval.Width, Math.Max((barHeight - comboHeight) / 2, 0), _interval.Width, comboHeight);
        _pause.SetBounds(_interval.Left - LogicalToDeviceUnits(8) - _pause.Width, Math.Max((barHeight - buttonHeight) / 2, 0), _pause.Width, buttonHeight);
    }

    public MainForm(GuiContext context)
    {
        _context = context;
        ApplyFont();
        AutoScaleMode = AutoScaleMode.Dpi;
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(480));
        ClientSize = StartSize();
        KeyPreview = true;

        _statusBar.Padding = new Padding(0, LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(4));
        _status.Padding = new Padding(LogicalToDeviceUnits(10), 0, 0, 0);
        _updated.Padding = new Padding(0, 0, LogicalToDeviceUnits(12), 0);
        _pause.Margin = Padding.Empty;
        SizeChrome();
        _controls.Controls.Add(_pause);
        _controls.Controls.Add(_interval);
        _controls.Layout += (_, _) => PlaceStatusControls();

        // Dock order: the last control added is laid out first (outermost on its edge), so Fill goes in first.
        _statusBar.Controls.Add(_status);
        _statusBar.Controls.Add(_updated);
        _statusBar.Controls.Add(_controls);
        Controls.Add(_host);
        Controls.Add(_nav);
        Controls.Add(_statusBar);

        _pause.Click += (_, _) => _context.SetPaused(!_context.Pump.Paused);
        _interval.SelectedIndexChanged += (_, _) =>
        {
            if (!_rebuildingNav && _interval.SelectedIndex >= 0)
            {
                _context.SetInterval(GuiSettings.IntervalChoices[_interval.SelectedIndex]);
            }
        };
        _nav.SelectedIndexChanged += (_, _) =>
        {
            if (!_rebuildingNav)
            {
                Activate(_nav.SelectedIndex);
            }
        };
        _context.LanguageChanged += ApplyLanguage;
        _context.StatusMessage += message => _status.Text = message;
        _context.Theme.Changed += ApplyTheme;
        _context.Pump.Sampled += OnSampled;
        _context.Pump.Failed += ex => _status.Text = Strings.Format("gui.status.failed", new Dictionary<string, string> { ["error"] = ex.Message });
        _context.Pump.StateChanged += ShowPumpState;
        _context.NavigateRequested += OnNavigateRequested;
        _context.AlwaysOnTopChanged += () => TopMost = _context.Settings.AlwaysOnTop;
        _context.TextSizeChanged += OnTextSizeChanged;
        TopMost = _context.Settings.AlwaysOnTop;
        Resize += (_, _) => FollowWindowState();
        VisibleChanged += (_, _) => FollowWindowState();

        ApplyLanguage();
    }

    /// <summary>
    /// The window font follows the text-size choice; every control that takes its font from the window (all of them except
    /// the few that derive their own) follows by itself. The things in the frame that have a fixed size are re-sized to match.
    /// </summary>
    private void ApplyFont()
    {
        var old = _sizedFont;
        _sizedFont = new Font(_systemFont.FontFamily, TextSizes.Points(_context.Settings.TextSize, _systemFont.SizeInPoints), _systemFont.Style, GraphicsUnit.Point);
        Font = _sizedFont;
        old?.Dispose();
    }

    private double TextFactor => Font.SizeInPoints / _systemFont.SizeInPoints;

    private int Chrome(int logical) => (int)Math.Round(LogicalToDeviceUnits(logical) * TextFactor);

    private void SizeChrome()
    {
        _nav.Width = Chrome(188);
        _statusBar.Height = Chrome(38);
        _updated.Width = Chrome(230);
        _pause.Width = Chrome(140);
        _interval.Width = Chrome(120);
        _controls.Width = LogicalToDeviceUnits(12 + 8) + _pause.Width + _interval.Width;
    }

    private void OnTextSizeChanged()
    {
        SuspendLayout();
        try
        {
            ApplyFont();
            SizeChrome();
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }

        PlaceStatusControls();
    }

    private bool _noActivate;

    /// <summary>Unattended runs (smoke) must not take the keyboard focus: a stray key from another program would change the page under test.</summary>
    internal void PreventActivation() => _noActivate = true;

    protected override bool ShowWithoutActivation => _noActivate;

    public int PageCount => _pages.Length;

    public PageBase? CurrentPage => _current >= 0 ? _pages[_current] : null;

    public PageBase? PageAt(int index) => _pages[index];

    public int SelectedNavIndex => _nav.SelectedIndex;

    /// <summary>The page with this id if it has been created (opened at least once). Smoke and tests use it.</summary>
    public PageBase? PageById(string id)
    {
        var index = IndexOf(id);
        return index >= 0 ? _pages[index] : null;
    }

    /// <summary>The page with this id, created now if it was never opened.</summary>
    public PageBase? EnsurePage(string id)
    {
        var index = IndexOf(id);
        return index >= 0 ? _pages[index] ??= CreatePage(index) : null;
    }

    /// <summary>Shows a page by id. Returns false for an unknown id.</summary>
    public bool ShowPage(string id)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            return false;
        }

        _nav.SelectedIndex = index;
        return true;
    }

    public void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Length)
        {
            return;
        }

        _nav.SelectedIndex = index;
        Activate(index);
    }

    private static int IndexOf(string id)
    {
        for (var i = 0; i < PageCatalog.All.Count; i++)
        {
            if (PageCatalog.All[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private void Activate(int index)
    {
        if (index < 0 || index >= _pages.Length || index == _current)
        {
            return;
        }

        if (_current >= 0 && _pages[_current] is { } old)
        {
            old.OnDeactivated();
            old.Visible = false;
        }

        var page = _pages[index] ??= CreatePage(index);
        _current = index;
        page.Visible = true;
        page.BringToFront();
        page.OnActivated();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _context.Theme.Apply(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_nav.SelectedIndex < 0)
        {
            _nav.SelectedIndex = 0;
        }

        _context.Theme.Apply(this);
        _context.Pump.SetPaused(_context.Settings.Paused);
        _context.Pump.Start();
        ShowPumpState();
    }

    /// <summary>
    /// The size the window opens with: the last one (gui.ini), else 1280 x 800, never larger than the screen it opens on.
    /// Wide enough that the default process columns and the detail pane fit without a horizontal scroll bar.
    /// </summary>
    private Size StartSize()
    {
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        var width = LogicalToDeviceUnits(Math.Clamp(_context.Settings.GetInt("win.w", 1280), 760, 6000));
        var height = LogicalToDeviceUnits(Math.Clamp(_context.Settings.GetInt("win.h", 800), 480, 4000));
        var frame = SystemInformation.CaptionHeight + (SystemInformation.FrameBorderSize.Height * 2) + LogicalToDeviceUnits(24);
        return new Size(
            Math.Min(width, Math.Max(work.Width - LogicalToDeviceUnits(24), MinimumSize.Width)),
            Math.Min(height, Math.Max(work.Height - frame, MinimumSize.Height)));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        foreach (var page in _pages)
        {
            page?.SaveState();
        }

        if (WindowState == FormWindowState.Normal)
        {
            _context.Settings.SetInt("win.w", (int)Math.Round(ClientSize.Width * 96.0 / DeviceDpi));
            _context.Settings.SetInt("win.h", (int)Math.Round(ClientSize.Height * 96.0 / DeviceDpi));
        }

        _context.Settings.Save();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _context.Pump.Stop();
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        // A plugged or removed device makes the hardware facts stale (B.md 6). Cheap: it only drops the provider's cache.
        _ = DeviceChange.Handle(m, _context.Services.Hardware);
        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F5)
        {
            if (CurrentPage is { } page)
            {
                page.RefreshNow();
            }
            else
            {
                _ = _context.Pump.TickAsync();
            }

            return true;
        }

        if (keyData == (Keys.Control | Keys.F))
        {
            if (CurrentPage?.FocusSearch() == true)
            {
                return true;
            }

            ShowPage("processes");
            return CurrentPage?.FocusSearch() == true;
        }

        if (keyData == Keys.Space && !TypesText(ActiveControl))
        {
            _context.SetPaused(!_context.Pump.Paused);
            return true;
        }

        if (keyData == Keys.Delete && !TypesText(ActiveControl) && CurrentPage?.HandleDelete() == true)
        {
            return true;
        }

        if ((keyData & Keys.Modifiers) == Keys.Control)
        {
            var number = (keyData & Keys.KeyCode) - Keys.D1;
            if (number >= 0 && number < _pages.Length)
            {
                _nav.SelectedIndex = (int)number;
                return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Keys that mean something to the focused control itself (typing, activating a button) must not be taken as shortcuts.</summary>
    private static bool TypesText(Control? control) =>
        control is TextBoxBase or ComboBox or NumericUpDown or ButtonBase or TreeView or TabControl;

    private PageBase CreatePage(int index)
    {
        var page = PageCatalog.All[index].Create(_context);
        page.Visible = false;
        _host.Controls.Add(page);
        _context.Theme.Apply(page);
        return page;
    }

    /// <summary>A minimized or hidden window samples every 5 s instead of every interval; it never stops, so the history stays whole.</summary>
    private void FollowWindowState()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        _context.Pump.SetSlowMode(WindowState == FormWindowState.Minimized || !Visible);
    }

    private void OnNavigateRequested(string id, ProcessKey? process)
    {
        if (EnsurePage(id) is not { } page)
        {
            return;
        }

        ShowPage(id);
        page.OnNavigatedTo(process);
    }

    private void OnSampled(Snapshot snapshot)
    {
        if (_context.Pump.Paused)
        {
            return;
        }

        _updated.Text = Strings.Format("gui.status.updated", new Dictionary<string, string>
        {
            ["time"] = snapshot.TakenAtUtc.ToLocalTime().ToString("T", CultureInfo.CurrentCulture),
        });
    }

    private void ShowPumpState()
    {
        var paused = _context.Pump.Paused;
        _pause.Text = Strings.Get(paused ? "gui.pause.resume" : "gui.pause.pause");
        if (paused)
        {
            _updated.Text = Strings.Get("gui.status.paused");
        }
        else if (_context.Pump.Latest is { } latest)
        {
            OnSampled(latest);
        }

        var index = Array.IndexOf(GuiSettings.IntervalChoices, _context.Pump.IntervalMs);
        if (index >= 0 && _interval.SelectedIndex != index)
        {
            _rebuildingNav = true;
            try
            {
                _interval.SelectedIndex = index;
            }
            finally
            {
                _rebuildingNav = false;
            }
        }

        _updated.ForeColor = paused ? _context.Theme.Palette.Accent : _context.Theme.Palette.Text;
    }

    private void ApplyTheme()
    {
        _context.Theme.Apply(this);
        ShowPumpState();
    }

    private void ApplyLanguage()
    {
        Text = Strings.Get("core.app.name") + (_context.Services.UsesFakeData ? " - " + Strings.Get("core.fake.notice") : string.Empty);
        _nav.AccessibleName = Strings.Get("gui.nav.label");
        _interval.AccessibleName = Strings.Get("gui.interval.label");
        _pause.AccessibleName = Strings.Get("gui.pause.tip");
        _tips.SetToolTip(_interval, Strings.Get("gui.interval.label"));
        _tips.SetToolTip(_pause, Strings.Get("gui.pause.tip"));

        var keep = _nav.SelectedIndex;
        _rebuildingNav = true;
        try
        {
            _nav.BeginUpdate();
            _nav.Items.Clear();
            foreach (var entry in PageCatalog.All)
            {
                _nav.Items.Add(Strings.Get(entry.TitleKey));
            }

            _nav.EndUpdate();
            if (keep >= 0)
            {
                _nav.SelectedIndex = keep;
            }

            _interval.BeginUpdate();
            _interval.Items.Clear();
            foreach (var ms in GuiSettings.IntervalChoices)
            {
                _interval.Items.Add(Strings.Get("gui.interval." + ms));
            }

            _interval.EndUpdate();
            _interval.SelectedIndex = Math.Max(Array.IndexOf(GuiSettings.IntervalChoices, _context.Pump.IntervalMs), 0);
        }
        finally
        {
            _rebuildingNav = false;
        }

        foreach (var page in _pages)
        {
            page?.ApplyLanguage();
        }

        ShowPumpState();
    }
}
