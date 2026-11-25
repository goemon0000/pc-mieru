using PcMieru.Checks.Build;
using PcMieru.Update.Feed;

namespace PcMieru.Gui;

/// <summary>Everything a page may use. Pages take this in their constructor and never reach for a global.</summary>
internal sealed class GuiContext
{
    /// <param name="netMeter">The network meter the pages control. Null: the one in <paramref name="services"/>. A test passes its own.</param>
    /// <param name="checks">The window's one check run. Null: the real one, or the fake one with <c>--fakes</c> (made without reading or creating anything, see <see cref="CheckComposition"/>). A test passes its own.</param>
    /// <param name="buildCode">The maker of the configuration code (PCB1). Null: the Windows one, or the sample one with <c>--fakes</c> (making either reads nothing). A test passes its own.</param>
    /// <param name="measure">The measurement marks of <c>--measure</c>. Null (every normal start): the process list watches nothing and nothing is written.</param>
    /// <param name="updates">The window's update check. Null: the real one, or the fake one with <c>--fakes</c> (making either reads nothing, starts no timer and makes no HTTP client, see <see cref="UpdateComposition"/>). A test passes its own.</param>
    /// <param name="recording">The window's recording (the recording tab and the status bar). Null: the real one, or the fake one with <c>--fakes</c>; nothing of it is built until a recording starts or the tab is shown (see <see cref="RecordingHub"/>). A test passes its own.</param>
    public GuiContext(PcMieruServices services, SnapshotPump pump, ThemeService theme, GuiSettings settings, INetMeterControl? netMeter = null, CheckController? checks = null, IBuildCodeProvider? buildCode = null, MeasureMarks? measure = null, UpdateController? updates = null, bool verifying = false, Func<UpdateResultNote?>? lastUpdate = null, RecordingHub? recording = null)
    {
        Measure = measure;
        Services = services;
        Pump = pump;
        Theme = theme;
        Settings = settings;
        Verifying = verifying;
        UseLastUpdate(lastUpdate);
        NetMeter = netMeter ?? services.NetMeter;
        if (checks is null && services.Options.ForceFakes)
        {
            // Only with --fakes: the smoke run may hold one fake load run still to look at it (see FakeCheckHold).
            FakeHold = new FakeCheckHold();
            checks = CheckComposition.Create(services, hold: FakeHold);
        }

        Checks = checks ?? CheckComposition.Create(services);
        BuildCode = buildCode ?? CheckComposition.CreateBuildCode(services);
        if (updates is null)
        {
            var wiring = UpdateComposition.Create(
                services.Options.ForceFakes,
                settings,
                AppInfo.Version,
                Path.GetDirectoryName(GuiSettings.DefaultPath) ?? string.Empty,
                Environment.ProcessPath ?? string.Empty);
            updates = wiring.Controller;
            FakeUpdates = wiring.Fake;
        }

        Updates = updates;
        // Quiet by design: a line in the status bar that stays while a newer version is on file and goes when it is not (or when the mode no longer asks). No dialog, no
        // balloon, nothing that takes the focus. It is kept as the VERSION and written in the language of the moment it is shown (see UpdateNotice), not as a text said once.
        Updates.CandidateFound += (_, _) => RefreshNotice();
        Updates.Changed += RefreshNotice;
        Updates.Failed += ex => Report("gui.status.failed", ("error", CheckFormat.ExceptionDetail(ex)));
        if (recording is null && services.Options.ForceFakes)
        {
            RecordingFake = new FakeRecordingKit();
            recording = RecordingComposition.Create(services, kit: RecordingFake);
        }

        Recording = recording ?? RecordingComposition.Create(services);
    }

    public PcMieruServices Services { get; }

    internal bool Verifying { get; }

    internal Lazy<UpdateResultNote?> LastUpdate { get; private set; } = null!;

    /// <summary>
    /// Where the note about the last update comes from (the real one reads the install folder's <c>update\last-result.json</c>). A note that the window already showed
    /// once (<see cref="UpdateResultNote.Stamp"/> kept in the settings) is not shown again. The smoke run points this at a temporary install folder of its own.
    /// </summary>
    internal void UseLastUpdate(Func<UpdateResultNote?>? source) =>
        LastUpdate = new Lazy<UpdateResultNote?>(() =>
            source?.Invoke() is { } result && Settings.GetValue(result.Key) != result.Stamp ? result : null);

    /// <summary>The marks of a <c>--measure</c> run; null in a normal start (DESIGN-A 10, <see cref="MeasureMarks"/>).</summary>
    public MeasureMarks? Measure { get; }

    public INetMeterControl NetMeter { get; }

    /// <summary>The maker of the configuration code (PCB1). The hardware page asks it only when its button is pressed.</summary>
    public IBuildCodeProvider BuildCode { get; }

    /// <summary>With <c>--fakes</c> and no controller given: the smoke run's way to hold one fake load run still. Null otherwise.</summary>
    internal FakeCheckHold? FakeHold { get; }

    /// <summary>The check that is running in this window, its state and its last results. Owned here, not by a page: the pages come and go while it runs.</summary>
    public CheckController Checks { get; }

    /// <summary>
    /// The update check of this window: the mode, the two timers, the one check that may run. In Ask and Off it does nothing at all. Owned here, not by the
    /// settings page: the page comes and goes while a check runs.
    /// </summary>
    internal UpdateController Updates { get; }

    /// <summary>With <c>--fakes</c> and no controller given: the fake service the smoke run drives (a scripted answer, never a request). Null otherwise.</summary>
    internal FakeUpdateService? FakeUpdates { get; }

    /// <summary>
    /// "Open the notes" of a candidate: the Release page of this project in the default browser. Only the address the feed handed over and only when it is
    /// a page of this project's releases (<see cref="UpdateEndpoints.IsReleasePage"/>); anything else is not opened. Returns whether a page was opened.
    /// </summary>
    internal bool OpenReleaseNotes(string? url)
    {
        if (url is null || !UpdateEndpoints.IsReleasePage(url))
        {
            return false;
        }

        ShellActions.OpenUrl(url);
        return true;
    }

    /// <summary>
    /// The quiet line about a newer version, in the language the window shows now; null when there is none to say. It follows the CURRENT state (what
    /// <see cref="UpdateController.Candidate"/> says): it stays through a failed check that learned nothing about the release, and goes when the candidate is
    /// cleared by a check that did judge a release, or when the mode is Ask or Off.
    /// </summary>
    internal string? UpdateNotice => Updates.Deferral?.Text ??
        (_noticeVersion is { } version ? Strings.Format("gui.update.notice", Fmt.Dict(("version", version.ToString()))) : null);

    /// <summary>Raised when <see cref="UpdateNotice"/> appeared, went or names another version. (A language change is not one: the shell writes the notice again then.)</summary>
    internal event Action? UpdateNoticeChanged;

    private AppVersion? _noticeVersion;
    private UpdateDeferral? _noticeDeferral;

    private void RefreshNotice()
    {
        var wanted = Updates.Candidate()?.Version;
        if (wanted == _noticeVersion && Updates.Deferral == _noticeDeferral)
        {
            return;
        }

        _noticeVersion = wanted;
        _noticeDeferral = Updates.Deferral;
        UpdateNoticeChanged?.Invoke();
    }
    /// <summary>The recording of this window: started, marked and stopped from the recording tab, shown in the status bar, stopped (bounded) when the window closes. Owned here, not by the tab.</summary>
    public RecordingHub Recording { get; }

    /// <summary>With <c>--fakes</c> and no hub given: the in-memory recorder and store the smoke run seeds. Null otherwise.</summary>
    internal FakeRecordingKit? RecordingFake { get; }

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

    /// <summary>
    /// Tells the Parts Life window that the language changed (DESIGN-C 3 item 4): a message to its window, which reads gui.ini again. Set only by a real run; the smoke, the
    /// measure run, the fakes and every test leave it null, so none of them ever messages a window of the machine.
    /// </summary>
    internal Action? SiblingLanguageNotifier { get; set; }

    public void SetLanguage(Language language)
    {
        if (Settings.Language == language && Strings.Current == language)
        {
            return;
        }

        Settings.Language = language;
        Strings.Current = language;
        Settings.Save();
        SiblingLanguageNotifier?.Invoke();
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
    private int _netMeterStarts;

    internal bool PendingUac => _netMeterStarts != 0 || Services.HelperBusy;

    public async Task<NetMeterState> StartNetMeterAsync()
    {
        Interlocked.Increment(ref _netMeterStarts);
        try
        {
            return await Task.Run(() => NetMeter.StartAsync()).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _netMeterStarts);
        }
    }

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
