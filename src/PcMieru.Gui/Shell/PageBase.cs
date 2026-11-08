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
    }

    protected GuiContext Context { get; }

    /// <summary>Stable id ("processes"): used by --page and in screenshots, never shown.</summary>
    public string Id { get; }

    public string TitleKey { get; }

    public string Title => Strings.Get(TitleKey);

    public virtual void OnActivated()
    {
    }

    public virtual void OnDeactivated()
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

    protected int Px(int logical) => LogicalToDeviceUnits(logical);
}
