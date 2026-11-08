namespace PcMieru.Gui;

/// <summary>Everything a page may use. Pages take this in their constructor and never reach for a global.</summary>
internal sealed class GuiContext
{
    public GuiContext(PcMieruServices services, SnapshotPump pump, ThemeService theme, GuiSettings settings)
    {
        Services = services;
        Pump = pump;
        Theme = theme;
        Settings = settings;
    }

    public PcMieruServices Services { get; }

    public SnapshotPump Pump { get; }

    public ThemeService Theme { get; }

    public GuiSettings Settings { get; }

    /// <summary>Raised after the language changed. MainForm re-applies every page's text.</summary>
    public event Action? LanguageChanged;

    /// <summary>Short message for the status bar (a failed read, "copied").</summary>
    public event Action<string>? StatusMessage;

    /// <summary>A page asks the shell to show another page, optionally with a process to select there.</summary>
    public event Action<string, ProcessKey?>? NavigateRequested;

    public event Action? AlwaysOnTopChanged;

    public void SetLanguage(Language language)
    {
        if (Settings.Language == language && Strings.Current == language)
        {
            return;
        }

        Settings.Language = language;
        Strings.Current = language;
        Settings.Save();
        LanguageChanged?.Invoke();
    }

    public void SetTheme(ThemeMode mode)
    {
        Settings.Theme = mode;
        Theme.Mode = mode;
        Settings.Save();
    }

    public void SetInterval(int intervalMs)
    {
        Settings.IntervalMs = intervalMs;
        Pump.SetInterval(Settings.IntervalMs);
        Settings.Save();
    }

    public void SetPaused(bool paused)
    {
        Settings.Paused = paused;
        Pump.SetPaused(paused);
    }

    public void SetAlwaysOnTop(bool onTop)
    {
        if (Settings.AlwaysOnTop == onTop)
        {
            return;
        }

        Settings.AlwaysOnTop = onTop;
        Settings.Save();
        AlwaysOnTopChanged?.Invoke();
    }

    public void Report(string message) => StatusMessage?.Invoke(message);

    public void Report(string key, params (string Name, string Value)[] values) =>
        Report(Strings.Format(key, values.ToDictionary(v => v.Name, v => v.Value)));

    /// <summary>"gui.common.copied" and other plain keys go through here so a page never builds a status text by hand.</summary>
    public void ReportKey(string key) => Report(Strings.Get(key));

    /// <summary>Extra word for the page being navigated to (a service name for the Services page). Taken once by that page.</summary>
    public string? NavigationArgument { get; private set; }

    public string? TakeNavigationArgument()
    {
        var value = NavigationArgument;
        NavigationArgument = null;
        return value;
    }

    public void Navigate(string pageId, ProcessKey? process = null, string? argument = null)
    {
        NavigationArgument = argument;
        NavigateRequested?.Invoke(pageId, process);
    }

    /// <summary>Text to the clipboard. A busy clipboard is a normal failure on Windows: it goes to the status bar, not a crash.</summary>
    public void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            ReportKey("gui.common.copied");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            Report(Strings.Format("gui.status.failed", new Dictionary<string, string> { ["error"] = ex.Message }));
        }
    }
}
