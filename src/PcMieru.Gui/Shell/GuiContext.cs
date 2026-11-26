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

    private readonly object _roamingGate = new();
    private string? _partyRoamingRoot;
    private bool _roamingResolved;

    /// <summary>
    /// The roaming application-data folder the hint that old Parts Life progress is waiting looks in (<see cref="PcMieru.Core.Siblings.PartyImportHint"/>, file existence only), as far as it is KNOWN:
    /// the smoke run sets a scratch folder; a real run leaves it null until <see cref="ResolvePartyRoamingRoot"/> (called off the UI thread) has asked <see cref="PartyRoamingRootResolver"/>. Null (every
    /// test, a measurement, a trial on fakes, or "not resolved yet") means the code on the UI thread looks nowhere and says nothing, so no automated run ever names the owner's real folders.
    /// </summary>
    internal string? PartyRoamingRoot
    {
        get => Volatile.Read(ref _partyRoamingRoot);
        set => Volatile.Write(ref _partyRoamingRoot, value);
    }

    /// <summary>
    /// How a real run finds its roaming folder (Program.cs sets it in its real-run block only; the smoke run, a measurement and every test leave it null). Asking can stall when the folder is redirected to a
    /// slow or disconnected place, so only <see cref="ResolvePartyRoamingRoot"/> calls it, on a worker thread, never the code that starts the window.
    /// </summary>
    internal Func<string?>? PartyRoamingRootResolver { get; set; }

    /// <summary>True when there is a roaming folder to look in, or a way to find one: the work that needs it is worth starting (and starts off the UI thread).</summary>
    internal bool PartyRoamingRootKnown => PartyRoamingRoot is not null || PartyRoamingRootResolver is not null;

    /// <summary>
    /// The roaming folder, asking <see cref="PartyRoamingRootResolver"/> the first time (at most once for the run, whoever asks first; the others wait for it and get the same answer). Call it from a worker
    /// thread: the resolver may stall. Null when there is none (no resolver, or it failed).
    /// </summary>
    internal string? ResolvePartyRoamingRoot()
    {
        if (PartyRoamingRoot is { } known)
        {
            return known;
        }

        lock (_roamingGate)
        {
            if (PartyRoamingRoot is { } resolvedMeanwhile)
            {
                return resolvedMeanwhile;
            }

            if (_roamingResolved || PartyRoamingRootResolver is not { } resolver)
            {
                return null;
            }

            _roamingResolved = true;
            try
            {
                var root = resolver();
                PartyRoamingRoot = string.IsNullOrWhiteSpace(root) ? null : root;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // a folder that cannot be found is no hint: the window says nothing
            }

            return PartyRoamingRoot;
        }
    }

    /// <summary>The launcher of the Parts Life page. Null (every real run) is the real one; a test passes one that starts nothing, so pressing the page's button never starts a process.</summary>
    internal PartyLauncher? PartyLauncherOverride { get; set; }

    /// <summary>
    /// Where the setting "Start Parts Life at sign-in" keeps its Run value (DESIGN-C 3 item 5). Set to the real registry store only by a real run (Program.cs, the same block that sets
    /// <see cref="PartyRoamingRootResolver"/>) and to an in-memory store by the smoke run; null (every test that does not pass one, a measurement, a trial on fakes) means the setting is shown
    /// unavailable and does nothing, so no automated run ever reads or writes the owner's Run key.
    /// </summary>
    internal PcMieru.Core.Siblings.IRunStore? LogonStore { get; set; }

    /// <summary>
    /// True once the recovery at a real start (<see cref="MainForm.RecoverLogonAsync"/>) found a switch of the sign-in start that it could not settle (a restore failed, or an entry was changed in Windows
    /// meanwhile): the setting's status line then says "please check the sign-in setting". Never set by a smoke run, a measurement or a test that does not ask for it.
    /// </summary>
    internal bool LogonNeedsCheck { get; set; }

    /// <summary>
    /// Seams of the sign-in setting for tests and the smoke run (null/default in every real run): what "the old exe is there" means for the offer, the name and the wait of the lock of the sign-in entries
    /// (a test uses a lock of its own and a short wait), and how long the recovery at start waits between its tries while that lock is busy.
    /// </summary>
    internal LogonSeams LogonSeam { get; set; } = new();

    /// <summary>
    /// The journal of the switch from the old sign-in start: <c>run-switch.json</c> beside the save of Parts Life (the roaming folder this context names). Null when the folder is not known or cannot be
    /// found. Call it off the UI thread (it may ask <see cref="PartyRoamingRootResolver"/>).
    /// </summary>
    internal string? ResolveLogonJournalPath()
    {
        try
        {
            return PartyRoamingRootKnown && ResolvePartyRoamingRoot() is { } root
                ? PcMieru.Core.Siblings.RunSwitch.JournalPathFor(Path.Combine(root, "PCMieru", "party"))
                : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The PCMieru.Party.exe the sign-in entry starts: the one beside the running PCMieru.exe (absolute). A test may point it elsewhere.</summary>
    internal string PartyExePath { get; set; } = Path.GetFullPath(PartyLauncher.ExePath);

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

/// <summary>See <see cref="GuiContext.LogonSeam"/>. Every member is null in a real run, which means the real behaviour (File.Exists, the per-user lock with its default wait, 5 seconds between tries).</summary>
/// <param name="FileExists">The "is this file there" of the offer to switch from the old sign-in start.</param>
/// <param name="LockName">The name of the lock of the sign-in entries.</param>
/// <param name="LockWait">How long a change waits for that lock.</param>
/// <param name="RetryDelay">The pause between the tries of the recovery at start while the lock is busy.</param>
internal sealed record LogonSeams(Func<string, bool>? FileExists = null, string? LockName = null, TimeSpan? LockWait = null, TimeSpan? RetryDelay = null);
