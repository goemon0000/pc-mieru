namespace PcMieru.Gui;

/// <summary>Start-up items, read only for now. Switching one off goes through IStartupManager (never deletes).</summary>
internal sealed class StartupPage : PageBase
{
    private readonly BufferedListView _list = new() { Name = "startupList" };
    private readonly Button _refresh = new() { AutoSize = true, Name = "startupRefresh" };
    private bool _loaded;

    public StartupPage(GuiContext context)
        : base(context, "startup", "gui.nav.startup")
    {
        foreach (var width in new[] { 220, 150, 100, 480 })
        {
            _list.Columns.Add(new ColumnHeader { Width = Px(width) });
        }

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(Px(8)) };
        bar.Controls.Add(_refresh);
        _refresh.Click += (_, _) => Run(LoadAsync);
        Controls.Add(_list);
        Controls.Add(bar);
        ApplyLanguage();
    }

    public override void OnActivated()
    {
        if (!_loaded)
        {
            Run(LoadAsync);
        }
    }

    public override void ApplyLanguage()
    {
        string[] keys = ["gui.startup.col.name", "gui.startup.col.provider", "gui.startup.col.state", "gui.startup.col.command"];
        for (var i = 0; i < keys.Length; i++)
        {
            _list.Columns[i].Text = Strings.Get(keys[i]);
        }

        _refresh.Text = Strings.Get("gui.startup.refresh");
        _list.AccessibleName = Strings.Get("gui.nav.startup");
        if (_loaded)
        {
            Run(LoadAsync);
        }
    }

    private async Task LoadAsync()
    {
        var items = await Context.Services.Startup.ListAsync().ConfigureAwait(true);
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var item in items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _list.Items.Add(new ListViewItem(
            [
                item.Name,
                item.Provider.ToString(),
                Strings.Get("gui.startup.state." + item.State.ToString().ToLowerInvariant()),
                item.Command,
            ]));
        }

        _list.EndUpdate();
        _loaded = true;
    }
}
