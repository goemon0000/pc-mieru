namespace PcMieru.Gui;

/// <summary>
/// The checks page: system, load, storage and history, one tab at a time (DESIGN-A §7). The window's one run lives in
/// <see cref="GuiContext.Checks"/>, not here: the tab and the navigation can change while it runs, the page can even be built after it
/// started, and each tab draws what the controller says. Building the page reads nothing and writes nothing; the history tab reads (never
/// creating a folder) when it is shown, and nothing is created on disk until the user starts a check.
/// </summary>
internal sealed class ChecksPage : PageBase
{
    private readonly FlowLayoutPanel _strip = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Dock = DockStyle.Top, Name = "checksTabs" };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, Name = "checksHost" };
    private readonly List<TabEntry> _tabs = [];
    private int _current;
    private bool _active;

    public ChecksPage(GuiContext context)
        : base(context, "checks", "gui.nav.checks")
    {
        var checks = context.Checks;

        // One line per tab, in the order of the strip. Stage B puts the record of tests between Storage and History with one more line here,
        // naming its own title key and its tab class; nothing else in this file changes.
        _tabs.Add(new TabEntry("system", "gui.chk.tab.system", new SystemCheckTab(checks, Px)));
        _tabs.Add(new TabEntry("load", "gui.chk.tab.load", new LoadCheckTab(checks, Px)));
        _tabs.Add(new TabEntry("storage", "gui.chk.tab.storage", new StorageCheckTab(checks, Px)));
        _tabs.Add(new TabEntry("history", "gui.chk.tab.history", new CheckHistoryTab(checks, Px)));

        var pad = Px(12);
        _strip.Padding = new Padding(pad, pad * 5 / 6, pad, pad / 3);
        for (var i = 0; i < _tabs.Count; i++)
        {
            var index = i;
            var entry = _tabs[i];
            entry.Button.Name = "checksTab." + entry.Id;
            entry.Button.Margin = new Padding(0, 0, Px(4), 0);
            entry.Button.Padding = new Padding(Px(6), 0, Px(6), 0);
            entry.Button.MinimumSize = new Size(0, Px(30));
            entry.Button.TabIndex = i;
            entry.Button.Click += (_, _) => SelectTab(index);
            entry.Tab.Visible = false;
            _strip.Controls.Add(entry.Button);
            _host.Controls.Add(entry.Tab);
        }

        var saved = _tabs.FindIndex(e => e.Id == context.Settings.GetValue("checks.tab"));
        if (saved >= 0)
        {
            _current = saved;
        }

        Controls.Add(_host);
        Controls.Add(_strip);
        ShowCurrent();
        ApplyLanguage();
        checks.Changed += OnChecksChanged;
        checks.RunFinished += OnRunFinished;
    }

    /// <summary>The tabs' ids, in the order of the strip.</summary>
    internal IReadOnlyList<string> TabIds => _tabs.Select(static e => e.Id).ToArray();

    internal string CurrentId => _tabs[_current].Id;

    internal CheckTab CurrentTab => _tabs[_current].Tab;

    internal CheckTab TabOf(string id) => _tabs.First(e => e.Id == id).Tab;

    internal ToggleButton ButtonOf(string id) => _tabs.First(e => e.Id == id).Button;

    public override bool IsReady => CurrentTab.IsReady;

    /// <summary>Shows a tab. The tab that was in front is told it went to the back, and the new one that it is shown.</summary>
    internal void SelectTab(string id)
    {
        var index = _tabs.FindIndex(e => e.Id == id);
        if (index >= 0)
        {
            SelectTab(index);
        }
    }

    public override void OnActivated()
    {
        _active = true;
        CurrentTab.OnShown();
    }

    public override void OnDeactivated()
    {
        _active = false;
        CurrentTab.OnHidden();
    }

    /// <summary>F5: the history reads its list again; the tabs that run a check have nothing to read.</summary>
    public override void RefreshNow() => CurrentTab.Reload();

    public override void SaveState() => Context.Settings.SetValue("checks.tab", CurrentId);

    public override void ApplyLanguage()
    {
        foreach (var entry in _tabs)
        {
            entry.Button.Text = Strings.Get(entry.TitleKey);
            entry.Tab.ApplyLanguage();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Context.Checks.Changed -= OnChecksChanged;
            Context.Checks.RunFinished -= OnRunFinished;
        }

        base.Dispose(disposing);
    }

    private void SelectTab(int index)
    {
        if (index == _current || index < 0 || index >= _tabs.Count)
        {
            ShowCurrent();
            return;
        }

        if (_active)
        {
            CurrentTab.OnHidden();
        }

        _current = index;
        ShowCurrent();
        if (_active)
        {
            CurrentTab.OnShown();
        }
    }

    private void ShowCurrent()
    {
        for (var i = 0; i < _tabs.Count; i++)
        {
            _tabs[i].Button.Checked = i == _current;
            _tabs[i].Tab.Visible = i == _current;
        }
    }

    /// <summary>The controller said something changed (a state, a progress step). It arrives on the UI thread, possibly after the page is gone.</summary>
    private void OnChecksChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        // One tab that cannot draw its state must not leave the others stale: each is guarded on its own.
        foreach (var entry in _tabs)
        {
            Guard(entry.Tab.RefreshState);
        }
    }

    private void OnRunFinished(PcMieru.Checks.CheckKind kind)
    {
        if (IsDisposed)
        {
            return;
        }

        foreach (var entry in _tabs)
        {
            if (entry.Tab is CheckHistoryTab history)
            {
                Guard(history.OnRunFinished);
            }
        }
    }

    /// <summary>A refresh that throws is told in the status line (the type and the HRESULT, never the message) and does not stop the next one.</summary>
    private void Guard(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            Context.Report(Strings.Format("gui.status.failed", new Dictionary<string, string> { ["error"] = CheckFormat.ExceptionDetail(ex) }));
        }
    }

    private sealed record TabEntry(string Id, string TitleKey, CheckTab Tab)
    {
        public ToggleButton Button { get; } = new();
    }
}
