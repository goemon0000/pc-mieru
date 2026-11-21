using PcMieru.Checks.Build;

namespace PcMieru.Gui;

/// <summary>Everything a page may use. Pages take this in their constructor and never reach for a global.</summary>
internal sealed class GuiContext
{
    /// <param name="netMeter">The network meter the pages control. Null: the one in <paramref name="services"/>. A test passes its own.</param>
    /// <param name="checks">The window's one check run. Null: the real one, or the fake one with <c>--fakes</c> (made without reading or creating anything, see <see cref="CheckComposition"/>). A test passes its own.</param>
    /// <param name="buildCode">The maker of the configuration code (PCB1). Null: the Windows one, or the sample one with <c>--fakes</c> (making either reads nothing). A test passes its own.</param>
    public GuiContext(PcMieruServices services, SnapshotPump pump, ThemeService theme, GuiSettings settings, INetMeterControl? netMeter = null, CheckController? checks = null, IBuildCodeProvider? buildCode = null)
    {
        Services = services;
        Pump = pump;
        Theme = theme;
        Settings = settings;
        NetMeter = netMeter ?? services.NetMeter;
        if (checks is null && services.Options.ForceFakes)
        {
            // Only with --fakes: the smoke run may hold one fake load run still to look at it (see FakeCheckHold).
            FakeHold = new FakeCheckHold();
            checks = CheckComposition.Create(services, hold: FakeHold);
        }

        Checks = checks ?? CheckComposition.Create(services);
        BuildCode = buildCode ?? CheckComposition.CreateBuildCode(services);
    }

    public PcMieruServices Services { get; }

    public INetMeterControl NetMeter { get; }

    /// <summary>The maker of the configuration code (PCB1). The hardware page asks it only when its button is pressed.</summary>
    public IBuildCodeProvider BuildCode { get; }

    /// <summary>With <c>--fakes</c> and no controller given: the smoke run's way to hold one fake load run still. Null otherwise.</summary>
    internal FakeCheckHold? FakeHold { get; }

    /// <summary>The check that is running in this window, its state and its last results. Owned here, not by a page: the pages come and go while it runs.</summary>
    public CheckController Checks { get; }

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

    /// <summary>Raised after the text size changed. MainForm sets the window font; every control that takes its font from the window follows.</summary>
    public event Action? TextSizeChanged;

    /// <summary>Raised after the network unit changed. Every page that writes a network rate shows it again at once.</summary>
    public event Action? NetworkUnitChanged;

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

    public void SetTextSize(TextSize size)
    {
        if (Settings.TextSize == size)
        {
            return;
        }

        Settings.TextSize = size;
        Settings.Save();
        TextSizeChanged?.Invoke();
    }

    public void SetNetworkUnit(NetworkUnit unit)
    {
        if (Settings.NetworkUnit == unit)
        {
            return;
        }

        Settings.NetworkUnit = unit;
        Settings.Save();
        NetworkUnitChanged?.Invoke();
    }

    public void SetNetMeterAutostart(bool autostart)
    {
        if (Settings.NetMeterAutostart == autostart)
        {
            return;
        }

        Settings.NetMeterAutostart = autostart;
        Settings.Save();
    }

    /// <summary>
    /// Starts the network meter. The call runs on a pool thread: it waits for the UAC prompt, and the window must keep
    /// painting meanwhile. The meter reports where it is through <see cref="INetMeterControl.StateChanged"/>.
    /// </summary>
    public Task<NetMeterState> StartNetMeterAsync() => Task.Run(() => NetMeter.StartAsync());

    public Task StopNetMeterAsync() => Task.Run(NetMeter.Stop);

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

    /// <summary>What puts the text on the clipboard. The system's by default; a test puts one that fails.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action<string> ClipboardWriter { get; set; } = static text => Clipboard.SetText(text);

    /// <summary>
    /// Text to the clipboard. A busy clipboard is a normal failure on Windows: it goes to the status bar, not a crash. The failure is said with the
    /// fixed text and the type and HRESULT of the exception, never its message (the message is the OS's, in its language, and can carry a path or
    /// a user name; DESIGN-A §7).
    /// </summary>
    /// <param name="doneKey">The status text for success (default "copied"). Returns false when the clipboard was busy.</param>
    public bool CopyText(string text, string doneKey = "gui.common.copied")
    {
        try
        {
            ClipboardWriter(text);
            ReportKey(doneKey);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            Report(Strings.Format("gui.status.failed", new Dictionary<string, string> { ["error"] = CheckFormat.ExceptionDetail(ex) }));
            return false;
        }
    }
}
