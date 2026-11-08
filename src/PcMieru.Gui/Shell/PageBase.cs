namespace PcMieru.Gui;

/// <summary>
/// One navigation entry. A page is a UserControl in its own file under Pages/, registered once in
/// <see cref="PageCatalog"/>. The shell calls <see cref="OnActivated"/> / <see cref="OnDeactivated"/> as the page
/// is shown and hidden, and <see cref="ApplyLanguage"/> after a language change (and once at start).
/// </summary>
internal abstract class PageBase : UserControl
{
    protected PageBase(GuiContext context, string id, string titleKey)
    {
        Context = context;
        Id = id;
        TitleKey = titleKey;
        Dock = DockStyle.Fill;
        Name = "page." + id;
        _createdDpi = DeviceDpi;
    }

    /// <summary>The DPI this page was built at: every size its constructor computed is in this scale.</summary>
    private readonly int _createdDpi;

    private bool _dpiMatched;

    protected GuiContext Context { get; }

    /// <summary>Stable id ("processes"): used by --page and in screenshots, never shown.</summary>
    public string Id { get; }

    public string TitleKey { get; }

    public string Title => Strings.Get(TitleKey);

    /// <summary>False while the page waits for its first data (services, start-up items, parts). Smoke runs wait on it before a capture.</summary>
    public virtual bool IsReady => true;

    public virtual void OnActivated()
    {
    }

    public virtual void OnDeactivated()
    {
    }

    /// <summary>F5. The default takes a fresh sample; pages that read something slower (hardware, services) override it.</summary>
    public virtual void RefreshNow() => _ = Context.Pump.TickAsync();

    /// <summary>Ctrl+F. Returns true if the page has a search box and focused it.</summary>
    public virtual bool FocusSearch() => false;

    /// <summary>Del. Returns true if the page took the key (the Processes page asks to end the selected process).</summary>
    public virtual bool HandleDelete() => false;

    /// <summary>Another page asked to be shown, optionally on one process (the Overview's top list, the Users page).</summary>
    public virtual void OnNavigatedTo(ProcessKey? process)
    {
    }

    /// <summary>Called once as the window closes: write widths, view mode and the like into the settings.</summary>
    public virtual void SaveState()
    {
    }

    public abstract void ApplyLanguage();

    /// <summary>Marks a handler as UI-thread work that must not take the window down: the failure goes to the status bar.</summary>
    protected async void Run(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Context.Report(Strings.Format("gui.status.failed", new Dictionary<string, string> { ["error"] = ex.Message }));
        }
    }

    /// <summary>The DPI of the window the page is in (the page's own value until it has a parent).</summary>
    protected int WindowDpi => FindForm()?.DeviceDpi ?? DeviceDpi;

    protected int Px(int logical) => (int)Math.Round(logical * WindowDpi / 96.0);

    /// <summary>
    /// A page is built the first time it is opened, which can be after the window moved to a denser screen. Windows
    /// rescales what already existed at that moment; a page built later would keep the sizes of the scale it was built at
    /// next to fonts of the new one (cut-off text). If nothing rescaled it by the time it gets its parent, it is scaled once here.
    /// </summary>
    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (_dpiMatched || FindForm() is not { } form)
        {
            return;
        }

        _dpiMatched = true;
        if (DeviceDpi == _createdDpi && form.DeviceDpi != _createdDpi)
        {
            var factor = (float)form.DeviceDpi / _createdDpi;
#pragma warning disable CS0618 // Control.Scale: the only call that scales a whole subtree by a given ratio
            Scale(new SizeF(factor, factor));
#pragma warning restore CS0618
        }
    }
}
