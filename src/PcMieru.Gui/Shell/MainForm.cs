using System.Globalization;

namespace PcMieru.Gui;

/// <summary>Navigation on the left, one page at a time on the right, a thin status bar below.</summary>
internal sealed class MainForm : Form
{
    private readonly GuiContext _context;
    private readonly NavListBox _nav = new() { Name = "nav" };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, Name = "pageHost" };
    private readonly Panel _statusBar = new() { Dock = DockStyle.Bottom, Name = "statusBar", AccessibleRole = AccessibleRole.StatusBar };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _updated = new() { AutoSize = false, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight };
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
        _statusBar.Height = LogicalToDeviceUnits(26);
        _status.Padding = new Padding(LogicalToDeviceUnits(10), 0, 0, 0);
        _updated.Padding = new Padding(0, 0, LogicalToDeviceUnits(10), 0);
        _updated.Width = LogicalToDeviceUnits(240);
        _statusBar.Controls.Add(_status);
        _statusBar.Controls.Add(_updated);

        // Dock order: the last control added is laid out first, so Fill goes in first.
        Controls.Add(_host);
        Controls.Add(_nav);
        Controls.Add(_statusBar);

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
        Resize += (_, _) => FollowWindowState();

        ApplyLanguage();
    }

    public int PageCount => _pages.Length;

    public PageBase? CurrentPage => _current >= 0 ? _pages[_current] : null;

    public PageBase? PageAt(int index) => _pages[index];

    public int SelectedNavIndex => _nav.SelectedIndex;

    /// <summary>Shows a page by id. Returns false for an unknown id.</summary>
    public bool ShowPage(string id)
    {
        for (var i = 0; i < PageCatalog.All.Count; i++)
        {
            if (PageCatalog.All[i].Id == id)
            {
                _nav.SelectedIndex = i;
                return true;
            }
        }

        return false;
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
        _context.Pump.Start();
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
            _ = _context.Pump.TickAsync();
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

    private PageBase CreatePage(int index)
    {
        var page = PageCatalog.All[index].Create(_context);
        page.Visible = false;
        _host.Controls.Add(page);
        _context.Theme.Apply(page);
        return page;
    }

    private void FollowWindowState()
    {
        if (WindowState == FormWindowState.Minimized)
        {
            _context.Pump.Stop();
        }
        else if (IsHandleCreated && !_context.Pump.Running)
        {
            _context.Pump.Start();
        }
    }

    private void OnSampled(Snapshot snapshot) =>
        _updated.Text = Strings.Format("gui.status.updated", new Dictionary<string, string>
        {
            ["time"] = snapshot.TakenAtUtc.ToLocalTime().ToString("T", CultureInfo.CurrentCulture),
        });

    private void ApplyTheme() => _context.Theme.Apply(this);

    private void ApplyLanguage()
    {
        Text = Strings.Get("core.app.name") + (_context.Services.UsesFakeData ? " - " + Strings.Get("core.fake.notice") : string.Empty);
        _nav.AccessibleName = Strings.Get("gui.nav.label");

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
        }
        finally
        {
            _rebuildingNav = false;
        }

        foreach (var page in _pages)
        {
            page?.ApplyLanguage();
        }

        if (_context.Pump.Latest is { } latest)
        {
            OnSampled(latest);
        }
    }
}
