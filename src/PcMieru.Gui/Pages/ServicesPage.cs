using System.Globalization;

namespace PcMieru.Gui;

/// <summary>Windows services, read only for now. Start/stop go through IServiceManager once the Management area lands.</summary>
internal sealed class ServicesPage : PageBase
{
    private readonly BufferedListView _list = new() { Name = "serviceList" };
    private readonly Button _refresh = new() { AutoSize = true, Name = "servicesRefresh" };
    private bool _loaded;

    public ServicesPage(GuiContext context)
        : base(context, "services", "gui.nav.services")
    {
        foreach (var width in new[] { 200, 280, 110, 130, 70 })
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
        string[] keys = ["gui.svc.col.name", "gui.svc.col.display", "gui.svc.col.state", "gui.svc.col.start", "gui.svc.col.pid"];
        for (var i = 0; i < keys.Length; i++)
        {
            _list.Columns[i].Text = Strings.Get(keys[i]);
        }

        _refresh.Text = Strings.Get("gui.svc.refresh");
        _list.AccessibleName = Strings.Get("gui.nav.services");
        if (_loaded)
        {
            Run(LoadAsync);
        }
    }

    private async Task LoadAsync()
    {
        var services = await Context.Services.Services.ListAsync().ConfigureAwait(true);
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var s in services.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            _list.Items.Add(new ListViewItem(
            [
                s.Name,
                s.DisplayName,
                Strings.Get("gui.svc.state." + s.State.ToString().ToLowerInvariant()),
                Strings.Get("gui.svc.start." + s.StartType.ToString().ToLowerInvariant()),
                s.Pid?.ToString(CultureInfo.CurrentCulture) ?? Fmt.Dash,
            ]));
        }

        _list.EndUpdate();
        _loaded = true;
    }
}
