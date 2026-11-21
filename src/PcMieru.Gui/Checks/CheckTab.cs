namespace PcMieru.Gui;

/// <summary>
/// The body of one tab of the checks page. It is a UserControl, not a page: the page owns the tab strip and shows one body at a time, and a
/// tab that is added later (the record of tests, stage B) is one more class and one line in <see cref="ChecksPage"/>. A tab reads the
/// <see cref="CheckController"/> (the window's one run) and draws what it says; it never starts work on the UI thread.
/// </summary>
internal abstract class CheckTab : UserControl
{
    private readonly Func<int, int> _px;

    protected CheckTab(CheckController controller, Func<int, int> px, string name)
    {
        Controller = controller;
        _px = px;
        Name = name;
        Dock = DockStyle.Fill;
    }

    protected CheckController Controller { get; }

    /// <summary>Logical to device pixels, at the page's scale.</summary>
    protected int Px(int logical) => _px(logical);

    /// <summary>False while the tab waits for data it asked for (the history while it loads). The smoke run waits on it before a capture.</summary>
    public virtual bool IsReady => true;

    /// <summary>The tab came to the front (or the page did): draw the controller's current state; a tab that reads something starts here.</summary>
    public virtual void OnShown() => RefreshState();

    /// <summary>The tab went to the back (or the page did).</summary>
    public virtual void OnHidden()
    {
    }

    /// <summary>F5 on this tab. Most tabs have nothing to read again.</summary>
    public virtual void Reload()
    {
    }

    /// <summary>The window's language changed: every text of the tab is written again.</summary>
    public abstract void ApplyLanguage();

    /// <summary>The controller's state or progress changed: the tab draws it again. Cheap; it rebuilds a table only when its result changed.</summary>
    public abstract void RefreshState();

    /// <summary>The run was told to stop and its tab is closing for good: a tab that waits for something lets go of it.</summary>
    public virtual void Release()
    {
    }
}
