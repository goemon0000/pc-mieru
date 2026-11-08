namespace PcMieru.Gui;

/// <summary>Everything a page may use. Pages take this in their constructor and never reach for a global.</summary>
internal sealed class GuiContext(PcMieruServices services, SnapshotPump pump, ThemeService theme, GuiSettings settings)
{
    public PcMieruServices Services { get; } = services;

    public SnapshotPump Pump { get; } = pump;

    public ThemeService Theme { get; } = theme;

    public GuiSettings Settings { get; } = settings;

    /// <summary>Raised after the language changed. MainForm re-applies every page's text.</summary>
    public event Action? LanguageChanged;

    /// <summary>Short message for the status bar (a failed read, "copied").</summary>
    public event Action<string>? StatusMessage;

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

    public void Report(string message) => StatusMessage?.Invoke(message);
}
