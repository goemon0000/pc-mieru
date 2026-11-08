using System.Globalization;

namespace PcMieru.Gui;

/// <summary>Navigation on the left, one page at a time on the right, a thin status bar below.</summary>
internal sealed class MainForm : Form
{
    private readonly GuiContext _context;
    private readonly NavListBox _nav = new() { Name = "nav" };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, Name = "pageHost" };
    private readonly Panel _statusBar = new() { Dock = DockStyle.Bottom, Name = "statusBar", AccessibleRole = AccessibleRole.StatusBar };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Name = "statusText" };
    private readonly Label _updated = new() { AutoSize = false, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight, Name = "updated" };
    private readonly Button _pause = new() { Name = "pauseButton", Dock = DockStyle.Right, TabStop = true, UseVisualStyleBackColor = false };
    private readonly ComboBox _interval = new() { Name = "intervalCombo", Dock = DockStyle.Right, DropDownStyle = ComboBoxStyle.DropDownList, TabStop = true };
    private readonly ToolTip _tips = new();
    private readonly PageBase?[] _pages = new PageBase?[PageCatalog.All.Count];
    private int _current = -1;
    private bool _rebuildingNav;

    public MainForm(GuiContext context)
    {
        _context = context;
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Dpi;
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(LogicalToDeviceUnits(1120), LogicalToDeviceUnits(700));
        MinimumSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(480));
        KeyPreview = true;

        _nav.Width = LogicalToDeviceUnits(188);
        _statusBar.Height = LogicalToDeviceUnits(38);
        _statusBar.Padding = new Padding(0, LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(4));
        _status.Padding = new Padding(LogicalToDeviceUnits(10), 0, 0, 0);
        _updated.Padding = new Padding(0, 0, LogicalToDeviceUnits(12), 0);
        _updated.Width = LogicalToDeviceUnits(230);
        _pause.Width = LogicalToDeviceUnits(140);
        _pause.Margin = Padding.Empty;
        _interval.Width = LogicalToDeviceUnits(120);

        // Dock order: the last control added is laid out first (outermost on its edge), so Fill goes in first.
        _statusBar.Controls.Add(_status);
        _statusBar.Controls.Add(_updated);
        _statusBar.Controls.Add(_pause);
        _statusBar.Controls.Add(_interval);
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
        TopMost = _context.Settings.AlwaysOnTop;
        Resize += (_, _) => FollowWindowState();
        VisibleChanged += (_, _) => FollowWindowState();

        ApplyLanguage();
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        foreach (var page in _pages)
        {
            page?.SaveState();
        }

        _context.Settings.Save();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _context.Pump.Stop();
        base.OnFormClosed(e);
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
