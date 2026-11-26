using System.Globalization;
using PcMieru.Core.Siblings;
using PcMieru.Update.Feed;
using PcMieru.Update.Launch;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Gui;

/// <summary>Navigation on the left, one page at a time on the right, a thin status bar below.</summary>
internal sealed class MainForm : Form
{
    private readonly GuiContext _context;
    private readonly Font _systemFont = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
    private Font? _sizedFont;
    private readonly NavListBox _nav = new() { Name = "nav" };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, Name = "pageHost" };
    private readonly Panel _statusBar = new() { Dock = DockStyle.Bottom, Name = "statusBar", AccessibleRole = AccessibleRole.StatusBar };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Name = "statusText" };
    private readonly Label _updated = new() { AutoSize = false, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight, Name = "updated" };
    private readonly Label _recording = new() { AutoSize = false, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight, Name = "recordingIndicator", Cursor = Cursors.Hand, Visible = false, AccessibleRole = AccessibleRole.PushButton };

    /// <summary>The quiet line about a newer version (<see cref="GuiContext.UpdateNotice"/>): between the messages and the time of the last update, in the normal text colour (it is not a link and not an alarm).</summary>
    private readonly Label _notice = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight, Name = "updateNotice", Visible = false };
    private readonly Button _pause = new() { Name = "pauseButton", TabStop = true, UseVisualStyleBackColor = false };
    private readonly ComboBox _interval = new() { Name = "intervalCombo", DropDownStyle = ComboBoxStyle.DropDownList, TabStop = true };
    private readonly Panel _controls = new() { Dock = DockStyle.Right, Name = "statusControls" };
    private readonly ToolTip _tips = new();
    private readonly PageBase?[] _pages = new PageBase?[PageCatalog.All.Count];
    private readonly CheckCloseGuard _closeGuard;
    private int _current = -1;
    private bool _rebuildingNav;
    private bool _updatesStarted;
    private System.Windows.Forms.Timer? _handOffTimer;
    private Action? _syncHandOffTimer;

    /// <summary>
    /// The pause button and the interval box are the same height and centred in the bar. A docked combo box would stay at
    /// its own (smaller) height at the top of the bar, next to a button as tall as the bar.
    /// </summary>
    private void PlaceStatusControls()
    {
        var comboHeight = _interval.Height;
        var buttonHeight = Math.Max(comboHeight, _pause.GetPreferredSize(Size.Empty).Height);
        var barHeight = _controls.ClientSize.Height;
        var right = _controls.ClientSize.Width - LogicalToDeviceUnits(12);
        _interval.SetBounds(right - _interval.Width, Math.Max((barHeight - comboHeight) / 2, 0), _interval.Width, comboHeight);
        _pause.SetBounds(_interval.Left - LogicalToDeviceUnits(8) - _pause.Width, Math.Max((barHeight - buttonHeight) / 2, 0), _pause.Width, buttonHeight);
    }

    public MainForm(GuiContext context)
    {
        _context = context;
        Enabled = !context.Verifying;
        _closeGuard = new CheckCloseGuard(
            context.Checks,
            () => Dialogs.Confirm(
                this,
                _context.Theme,
                Strings.Get("gui.chk.close.title"),
                Strings.Get("gui.chk.close.message"),
                [],
                Strings.Get("gui.chk.close.yes"),
                destructive: false),
            Close,
            () => _context.ReportKey("gui.chk.close.stopping"));
        ApplyFont();
        AutoScaleMode = AutoScaleMode.Dpi;
        Name = "MainForm";
        Icon = AppIcon.Load() ?? Icon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(480));
        ClientSize = StartSize();
        KeyPreview = true;

        _statusBar.Padding = new Padding(0, LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(4));
        _status.Padding = new Padding(LogicalToDeviceUnits(10), 0, 0, 0);
        _updated.Padding = new Padding(0, 0, LogicalToDeviceUnits(12), 0);
        _recording.Padding = new Padding(0, 0, LogicalToDeviceUnits(12), 0);
        _pause.Margin = Padding.Empty;
        SizeChrome();
        _controls.Controls.Add(_pause);
        _controls.Controls.Add(_interval);
        _controls.Layout += (_, _) => PlaceStatusControls();

        // Dock order: the last control added is laid out first (outermost on its edge), so Fill goes in first.
        _statusBar.Controls.Add(_status);
        _statusBar.Controls.Add(_notice);
        _statusBar.Controls.Add(_recording);
        _statusBar.Controls.Add(_updated);
        _statusBar.Controls.Add(_controls);
        Controls.Add(_host);
        Controls.Add(_nav);
        Controls.Add(_statusBar);

        _pause.Click += (_, _) => _context.SetPaused(!_context.Pump.Paused);
        _interval.SelectedIndexChanged += (_, _) =>
        {
            if (!_rebuildingNav && _interval.SelectedIndex >= 0)
            {
                _context.SetInterval(GuiSettings.IntervalChoices[_interval.SelectedIndex]);
            }
        };
        _nav.SelectedIndexChanged += (_, _) =>
        {
            if (!_rebuildingNav)
            {
                Activate(_nav.SelectedIndex);
            }
        };
        _context.LanguageChanged += ApplyLanguage;
        _context.StatusMessage += message => _status.Text = message;
        _context.UpdateNoticeChanged += ShowNotice;
        _statusBar.Resize += (_, _) => SizeNotice();
        _context.Theme.Changed += ApplyTheme;
        _context.Pump.Sampled += OnSampled;
        _context.Pump.Failed += ex => _status.Text = Strings.Format("gui.status.failed", new Dictionary<string, string> { ["error"] = ex.Message });
        _context.Pump.StateChanged += ShowPumpState;
        _context.NavigateRequested += OnNavigateRequested;
        _context.Recording.Changed += ShowRecording;
        _recording.Click += (_, _) => _context.Navigate("checks", argument: "record");
        _context.AlwaysOnTopChanged += () => TopMost = _context.Settings.AlwaysOnTop;
        _context.TextSizeChanged += OnTextSizeChanged;
        TopMost = _context.Settings.AlwaysOnTop;
        Resize += (_, _) => FollowWindowState();
        VisibleChanged += (_, _) => FollowWindowState();

        ApplyLanguage();
    }

    /// <summary>
    /// The window font follows the text-size choice; every control that takes its font from the window (all of them except
    /// the few that derive their own) follows by itself. The things in the frame that have a fixed size are re-sized to match.
    /// </summary>
    private void ApplyFont()
    {
        var old = _sizedFont;
        _sizedFont = new Font(_systemFont.FontFamily, TextSizes.Points(_context.Settings.TextSize, _systemFont.SizeInPoints), _systemFont.Style, GraphicsUnit.Point);
        Font = _sizedFont;
        old?.Dispose();
    }

    private double TextFactor => Font.SizeInPoints / _systemFont.SizeInPoints;

    private int Chrome(int logical) => (int)Math.Round(LogicalToDeviceUnits(logical) * TextFactor);

    private void SizeChrome()
    {
        _nav.Width = Chrome(188);
        _statusBar.Height = Chrome(38);
        _updated.Width = Chrome(230);
        _pause.Width = Chrome(140);
        _interval.Width = Chrome(120);
        _controls.Width = LogicalToDeviceUnits(12 + 8) + _pause.Width + _interval.Width;
    }

    private void OnTextSizeChanged()
    {
        SuspendLayout();
        try
        {
            ApplyFont();
            SizeChrome();
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }

        PlaceStatusControls();
    }

    private bool _noActivate;

    /// <summary>Unattended runs (smoke) must not take the keyboard focus: a stray key from another program would change the page under test.</summary>
    internal void PreventActivation() => _noActivate = true;

    protected override bool ShowWithoutActivation => _noActivate;

    public int PageCount => _pages.Length;

    public PageBase? CurrentPage => _current >= 0 ? _pages[_current] : null;

    public PageBase? PageAt(int index) => _pages[index];

    public int SelectedNavIndex => _nav.SelectedIndex;

    /// <summary>The page with this id if it has been created (opened at least once). Smoke and tests use it.</summary>
    public PageBase? PageById(string id)
    {
        var index = IndexOf(id);
        return index >= 0 ? _pages[index] : null;
    }

    /// <summary>The page with this id, created now if it was never opened.</summary>
    public PageBase? EnsurePage(string id)
    {
        var index = IndexOf(id);
        return index >= 0 ? _pages[index] ??= CreatePage(index) : null;
    }

    /// <summary>Shows a page by id. Returns false for an unknown id.</summary>
    public bool ShowPage(string id)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            return false;
        }

        _nav.SelectedIndex = index;
        return true;
    }

    public void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Length)
        {
            return;
        }

        _nav.SelectedIndex = index;
        Activate(index);
    }

    private static int IndexOf(string id)
    {
        for (var i = 0; i < PageCatalog.All.Count; i++)
        {
            if (PageCatalog.All[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private void Activate(int index)
    {
        if (index < 0 || index >= _pages.Length || index == _current)
        {
            return;
        }

        if (_current >= 0 && _pages[_current] is { } old)
        {
            old.OnDeactivated();
            old.Visible = false;
        }

        var page = _pages[index] ??= CreatePage(index);
        _current = index;
        page.Visible = true;
        page.BringToFront();
        page.OnActivated();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _context.Theme.Apply(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_nav.SelectedIndex < 0)
        {
            _nav.SelectedIndex = 0;
        }

        _context.Theme.Apply(this);
        _context.Pump.SetPaused(_context.Settings.Paused);
        _context.Pump.Start();
        ShowPumpState();
        if (!_context.Verifying && _context.Settings.NetMeterAutostart)
        {
            StartNetMeterAtLaunch();
        }
    }

    /// <summary>
    /// Real runs only (Program.cs calls this after the window is shown; a smoke run and a measurement never do): starts the update check's timers when the
    /// mode allows the network, and while the mode is still "not chosen" (Ask) shows the first-run choice. The timer starts only after 「決定」 saved the
    /// choice (<see cref="UpdateController.ChooseMode"/>); in Ask and Off nothing is created.
    /// </summary>
    internal void StartUpdates()
    {
        if (IsDisposed || _updatesStarted || _context.Verifying)
        {
            return;
        }

        _updatesStarted = true;
        var updates = _context.Updates;
        updates.Start();
        if (updates.Mode == UpdateMode.Ask)
        {
            UpdateChoiceDialog.Open(this, _context.Theme, updates);
        }

        if (!IsDisposed && !_context.Services.Options.ForceFakes && Environment.ProcessPath is { } executable)
        {
            StartHandOff(executable);
        }

        // Last, and only after the update choice above has closed (it is modal): the notice that old Parts Life progress is waiting. Never two dialogs at once.
        PartyNotice = NoticePartyImportAsync();

        // And off the UI thread, quietly: an unfinished switch of the sign-in start (Parts Life's first start offers it) is put back; only one that cannot be settled shows a line on the setting.
        LogonRecovery = RecoverLogonAsync();
    }

    /// <summary>The recovery that <see cref="StartUpdates"/> started (completed when nothing was started or it is over). A test awaits it; the window never does.</summary>
    internal Task LogonRecovery { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// DESIGN-C 4.3, the window's side: at a real start, puts back what an unfinished switch of the sign-in start left (<see cref="RunSwitch.Recover"/>), off the UI thread. Nothing is asked and no
    /// dialog opens; when the result needs a person (<see cref="RunSwitchResult.NeedsAttention"/>) the status line of the setting says so (<see cref="GuiContext.LogonNeedsCheck"/>). It needs both the
    /// store and the roaming folder the context names (a real run sets them; a smoke run, a measurement and a test leave at least one out), and the journal is the one beside the save of Parts Life.
    /// An exception shows nothing and never reaches the window.
    /// </summary>
    internal async Task RecoverLogonAsync()
    {
        try
        {
            if (_context.LogonStore is not { } store || !_context.PartyRoamingRootKnown)
            {
                return;
            }

            // The roaming folder is found on the worker thread too (a redirected folder may be slow or gone), with the recovery itself. While another program holds the lock of the sign-in entries
            // (Parts Life's own start may be switching right now) the recovery says Busy: it is tried up to 3 more times, 5 seconds apart, in the background; still Busy = nothing this run, the journal
            // stays for the next start.
            var seam = _context.LogonSeam;
            var delay = seam.RetryDelay ?? TimeSpan.FromSeconds(5);
            RunSwitchResult? result = null;
            for (var attempt = 0; attempt <= 3; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(delay).ConfigureAwait(true);
                }

                if (IsDisposed)
                {
                    return;
                }

                result = await Task.Run(() =>
                {
                    if (_context.ResolveLogonJournalPath() is not { } journal)
                    {
                        return null;
                    }

                    return RunSwitch.Recover(store, new FileRunSwitchJournal(journal), seam.LockName, seam.LockWait, seam.FileExists);
                }).ConfigureAwait(true);
                if (result is not { Status: RunSwitchStatus.Busy })
                {
                    break;
                }
            }

            if (result is not { NeedsAttention: true } || IsDisposed)
            {
                return;
            }

            _context.LogonNeedsCheck = true;
            if (PageById("settings") is SettingsPage page)
            {
                await page.RefreshPartyLogon().ConfigureAwait(true);   // a page that is already open shows the line now; one that is not yet made shows it when it opens
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // nothing shown, nothing blocked: the setting still works as it did
        }
    }

    /// <summary>The notice that <see cref="StartUpdates"/> started (completed when nothing was started or it is over). A test awaits it; the window never does.</summary>
    internal Task PartyNotice { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The first-run notice that old Parts Life progress is waiting (DESIGN-C 4.1 items 1-2). It only notices and sends the owner to PCMieru.Party.exe, whose first start does the import and
    /// its choices. The look is a file-existence hint off the UI thread (<see cref="PartyImportHint"/>) in the folder the context names, which only a real run sets. Nothing is shown when a save
    /// of the new window is there, when nothing old is, or when 「あとで」 was chosen before (gui.ini party.import=later). 「パーツぐらしを開く」 shows the Parts Life page and presses its button
    /// (so a failure to start is told there); 「あとで」 is saved; the cross is "not answered" and saves nothing, so it comes again at the next start. An unattended run only logs.
    /// </summary>
    internal async Task NoticePartyImportAsync()
    {
        try
        {
            if (!_context.PartyRoamingRootKnown || _context.Settings.PartyImportLater)
            {
                return;
            }

            // The folder is found on the worker thread too: the real one may be redirected to a slow or disconnected place, and the window must not wait for it.
            var state = await Task.Run(() => _context.ResolvePartyRoamingRoot() is { } root ? PartyImportHint.Check(root) : PartyImportHintState.None).ConfigureAwait(true);
            if (state == PartyImportHintState.None || IsDisposed || _context.Settings.PartyImportLater)
            {
                return;
            }

            // The look may end while a dialog is open (the confirmation of a close during the hardware check, any message box): never open the notice over it, wait until the window is free.
            if (!await WaitUntilFreeAsync().ConfigureAwait(true) || _context.Settings.PartyImportLater)
            {
                return;
            }

            ApplyPartyImportAnswer(PartyImportDialog.Show(this, _context.Theme, state));
        }
        catch (Exception ex)
        {
            _context.Report(Strings.Format("gui.status.failed", new Dictionary<string, string> { ["error"] = ex.Message }));
        }
    }

    /// <summary>How often <see cref="WaitUntilFreeAsync"/> looks again while a dialog is open (a test lowers it).</summary>
    internal int FreePollMs = 250;

    /// <summary>
    /// True while the window cannot take a new dialog of its own: it is closed or disposed, it is disabled (the native window of the owner of a dialog is disabled while that dialog is open, and so is the
    /// managed state of a window a caller disabled), or a modal form of the app is open.
    /// </summary>
    internal bool IsBlockedByDialog =>
        IsDisposed || !Enabled || (IsHandleCreated && !IsWindowEnabled(Handle)) || Application.OpenForms.Cast<Form>().Any(window => window != this && window.Modal);

    /// <summary>Waits on the UI thread (a short timer, nothing blocked: the dialog that is open keeps running) until no dialog is open over the window. False when the window went away meanwhile.</summary>
    internal async Task<bool> WaitUntilFreeAsync()
    {
        while (!IsDisposed && IsBlockedByDialog)
        {
            await Task.Delay(Math.Max(1, FreePollMs)).ConfigureAwait(true);
        }

        return !IsDisposed;
    }

    /// <summary>What the owner's answer to the notice does. A test calls it with a launcher that starts nothing (<see cref="GuiContext.PartyLauncherOverride"/>).</summary>
    internal void ApplyPartyImportAnswer(PartyImportAnswer answer)
    {
        switch (answer)
        {
            case PartyImportAnswer.Open when !IsDisposed:
                ShowPage("party");
                (CurrentPage as PartyPage)?.Open();
                break;
            case PartyImportAnswer.Later:
                PartyLaterSave = SavePartyImportLaterAsync();
                break;
        }
    }

    /// <summary>The save that 「あとで」 started (completed when none was). A test awaits it; the window never does.</summary>
    internal Task PartyLaterSave { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Saves 「あとで」 off the UI thread (the lock retries and the flush to disk must not hold the window). A save that did not reach the file is said once on the status line: the notice then comes again at
    /// the next start, which is what the line tells.
    /// </summary>
    private async Task SavePartyImportLaterAsync()
    {
        var saved = false;
        try
        {
            saved = await _context.Settings.TrySavePartyImportLaterAsync().ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
        }

        if (!saved && !IsDisposed)
        {
            _context.ReportKey("gui.party.import.later.failed");
        }
    }

    private void StartHandOff(string executable)
    {
        var install = InstallGate.InstallFolderOf(executable);
        var gateFile = Path.Combine(install, GateFile.FileName);
        var updates = _context.Updates;
        var handOff = new UpdateHandOff(
            () => !IsDisposed && !Disposing && updates.Allowed ? (updates.Mode, updates.Status().Staged) : (UpdateMode.Off, null),
            () => UpdateHandOff.ReadFacts(this, HasOpenMenu(this), _context.Checks.IsBusy || updates.Checking
                || PageById("diagnose") is DiagnosePage { IsRunning: true },
                // The window's recording (DESIGN-B §8): starting, recording and stopping all hold the hand-off back.
                UpdateHandOff.ActivityOf(_context.Recording.Phase), _context.PendingUac, ProcessElevation.IsElevated()),
            () =>
            {
                if (!InstallPlacement.Check(install).CanSwap)
                {
                    return GateHolders.ReadOnly;
                }

                var holders = RestartManager.ReadHolders(gateFile);
                var own = _context.Services.OwnedChildPids();
                own.Add(Environment.ProcessId);
                return RestartManager.Classify(holders, own);
            },
            (stage, id) => GuiHandOff.Run(install, stage, attemptId: id), updates.SetDeferral, Close,
            attempts: updates.HandOffService, failure: updates.SetHandOffFailure);

        _handOffTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _handOffTimer.Tick += async (_, _) => await handOff.TryAsync().ConfigureAwait(true);
        _syncHandOffTimer = () => _handOffTimer.Enabled = updates.Allowed && updates.Mode == UpdateMode.Automatic;
        updates.Changed += _syncHandOffTimer;
        _syncHandOffTimer();
        _ = handOff.TryAsync(reconcileOnly: true); // reconcile the previous run before the first timer tick
    }

    private static bool HasOpenMenu(Control control) =>
        control.ContextMenuStrip is { Visible: true } || control is ComboBox { DroppedDown: true }
        || control.Controls.Cast<Control>().Any(HasOpenMenu);

    /// <summary>
    /// The "start measuring when the app opens" setting: asks for administrator permission once, off the UI thread, so the
    /// window is already up (and never waits on the prompt). The state the meter reaches shows on the Processes page's button.
    /// </summary>
    private async void StartNetMeterAtLaunch()
    {
        try
        {
            var state = await _context.StartNetMeterAsync().ConfigureAwait(true);
            if (state.Status == NetMeterStatus.Declined)
            {
                _context.ReportKey("gui.proc.net.tip.declined");
            }
        }
        catch (Exception ex)
        {
            _context.Report(Strings.Format("gui.status.failed", new Dictionary<string, string> { ["error"] = ex.Message }));
        }
    }

    /// <summary>
    /// The size the window opens with: the last one (gui.ini), else 1280 x 800, never larger than the screen it opens on.
    /// Wide enough that the default process columns and the detail pane fit without a horizontal scroll bar.
    /// </summary>
    private Size StartSize()
    {
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        var width = LogicalToDeviceUnits(Math.Clamp(_context.Settings.GetInt("win.w", 1280), 760, 6000));
        var height = LogicalToDeviceUnits(Math.Clamp(_context.Settings.GetInt("win.h", 800), 480, 4000));
        var frame = SystemInformation.CaptionHeight + (SystemInformation.FrameBorderSize.Height * 2) + LogicalToDeviceUnits(24);
        return new Size(
            Math.Min(width, Math.Max(work.Width - LogicalToDeviceUnits(24), MinimumSize.Width)),
            Math.Min(height, Math.Max(work.Height - frame, MinimumSize.Height)));
    }

    /// <summary>The stop-and-close that "yes" started (null before). A test awaits it; the window never does.</summary>
    internal Task? ClosePending => _closeGuard.Pending;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_context.Verifying)
        {
            base.OnFormClosing(e);
            return;
        }

        // A check is running (DESIGN-A §7): the first request is cancelled and asked about; on yes the stop and its save are awaited and the window is
        // closed again, and nothing below runs until then. Windows ending the session cannot be asked: the stop is requested and the close goes on.
        if (!_closeGuard.OnCloseRequested(e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing))
        {
            e.Cancel = true;
            return;
        }

        // A recording is never a reason to refuse the close (no question): it is stopped and saved first, the wait bounded to ExitBound (2 s). This
        // comes after the check's guard, so a check and a recording at once are both dealt with, the check first; a cancelled first pass left the recording alone.
        StopRecordingForClose();

        foreach (var page in _pages)
        {
            page?.SaveState();
        }

        if (WindowState == FormWindowState.Normal)
        {
            _context.Settings.SetInt("win.w", (int)Math.Round(ClientSize.Width * 96.0 / DeviceDpi));
            _context.Settings.SetInt("win.h", (int)Math.Round(ClientSize.Height * 96.0 / DeviceDpi));
        }

        _context.Settings.Save();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _context.Updates.Changed -= _syncHandOffTimer;
        _handOffTimer?.Dispose();
        _context.Pump.Stop();
        _context.Updates.Dispose();
        _context.Checks.Dispose();
        _context.Recording.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>Stops the recording for the close (and for Windows ending the session, which also comes as a close): the library's bounded stop, reason window_close.</summary>
    private void StopRecordingForClose()
    {
        if (!_context.Recording.IsActive)
        {
            return;
        }

        if (IsHandleCreated && Visible)
        {
            _recording.Text = Strings.Get("gui.rtab.saving");
            _recording.Width = IndicatorWidth();
            _recording.Visible = true;
            _statusBar.Refresh();
        }

        _ = _context.Recording.StopForClose();
    }

    /// <summary>The status-bar label of a recording: its elapsed time while it runs, "saving" while it is stopped. Hidden when nothing is recording.</summary>
    private void ShowRecording()
    {
        var hub = _context.Recording;
        var phase = hub.Phase;
        var show = phase is RecordingPhase.Recording or RecordingPhase.Stopping;
        RecordingIndicatorOn = show;
        _recording.Visible = show;
        if (!show)
        {
            // Back to what the theme gives every label: the accent is only for a recording that is on screen (the settings section's theme check walks hidden controls too).
            _recording.ForeColor = _context.Theme.Palette.Text;
            return;
        }

        _recording.Text = phase == RecordingPhase.Stopping
            ? Strings.Get("gui.rtab.saving")
            : Strings.Format("gui.rtab.indicator", Fmt.Dict(("time", RecordFormat.Elapsed(hub.ShownElapsed))));
        _recording.ForeColor = _context.Theme.Palette.Accent;
        _recording.Width = IndicatorWidth();
        _tips.SetToolTip(_recording, Strings.Get("gui.rtab.indicator.tip"));
    }

    private int IndicatorWidth() => TextRenderer.MeasureText(_recording.Text, _recording.Font).Width + _recording.Padding.Horizontal + Chrome(8);

    /// <summary>The indicator, as a read for tests and the smoke run.</summary>
    internal Label RecordingIndicator => _recording;

    /// <summary>True while the indicator is meant to be on screen (a window that was never shown has a label whose own Visible reads false either way).</summary>
    internal bool RecordingIndicatorOn { get; private set; }

    /// <summary>A click on the indicator as the label gets it (its own click handler runs), for tests and the smoke run.</summary>
    internal void ClickRecordingIndicator() =>
        typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(_recording, [EventArgs.Empty]);

    /// <summary>
    /// The one window message the Parts Life window may send (DESIGN-C 2.2): show the checks page, restore the window if it is minimised and bring it to the front. wParam and lParam are
    /// not looked at and nothing else is done. True when the message was that one (the caller then does not pass it on).
    /// </summary>
    internal bool HandleSiblingMessage(uint message)
    {
        if (PcMieru.Core.Siblings.MieruMessages.ForMainWindow(message) != PcMieru.Core.Siblings.MieruMessageAction.ShowChecks)
        {
            return false;
        }

        ShowPage("checks");
        PcMieru.Core.Siblings.SiblingWindows.RestoreIfMinimized(Handle);
        Activate();
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        // A plugged or removed device makes the hardware facts stale (B.md 6). Cheap: it only drops the provider's cache.
        _ = DeviceChange.Handle(m, _context.Services.Hardware);
        if (HandleSiblingMessage((uint)m.Msg))
        {
            return;
        }

        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F5)
        {
            if (CurrentPage is { } page)
            {
                page.RefreshNow();
            }
            else
            {
                _ = _context.Pump.TickAsync();
            }

            return true;
        }

        if (keyData == (Keys.Control | Keys.F))
        {
            if (CurrentPage?.FocusSearch() == true)
            {
                return true;
            }

            ShowPage("processes");
            return CurrentPage?.FocusSearch() == true;
        }

        if (keyData == Keys.Space && !TypesText(ActiveControl))
        {
            _context.SetPaused(!_context.Pump.Paused);
            return true;
        }

        if (keyData == Keys.Delete && !TypesText(ActiveControl) && CurrentPage?.HandleDelete() == true)
        {
            return true;
        }

        if ((keyData & Keys.Modifiers) == Keys.Control)
        {
            var number = (keyData & Keys.KeyCode) - Keys.D1;
            if (number >= 0 && number < _pages.Length)
            {
                _nav.SelectedIndex = (int)number;
                return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Keys that mean something to the focused control itself (typing, activating a button) must not be taken as shortcuts.</summary>
    private static bool TypesText(Control? control) =>
        control is TextBoxBase or ComboBox or NumericUpDown or ButtonBase or TreeView or TabControl;

    private PageBase CreatePage(int index)
    {
        var page = PageCatalog.All[index].Create(_context);
        page.Visible = false;
        _host.Controls.Add(page);
        _context.Theme.Apply(page);
        return page;
    }

    /// <summary>A minimized or hidden window samples every 5 s instead of every interval; it never stops, so the history stays whole.</summary>
    private void FollowWindowState()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        _context.Pump.SetSlowMode(WindowState == FormWindowState.Minimized || !Visible);
    }

    private void OnNavigateRequested(string id, ProcessKey? process)
    {
        if (EnsurePage(id) is not { } page)
        {
            return;
        }

        ShowPage(id);
        page.OnNavigatedTo(process);
    }

    private void OnSampled(Snapshot snapshot)
    {
        if (_context.Pump.Paused)
        {
            return;
        }

        _updated.Text = Strings.Format("gui.status.updated", new Dictionary<string, string>
        {
            ["time"] = snapshot.TakenAtUtc.ToLocalTime().ToString("T", CultureInfo.CurrentCulture),
        });
    }

    private void ShowPumpState()
    {
        var paused = _context.Pump.Paused;
        _pause.Text = Strings.Get(paused ? "gui.pause.resume" : "gui.pause.pause");
        if (paused)
        {
            _updated.Text = Strings.Get("gui.status.paused");
        }
        else if (_context.Pump.Latest is { } latest)
        {
            OnSampled(latest);
        }

        var index = Array.IndexOf(GuiSettings.IntervalChoices, _context.Pump.IntervalMs);
        if (index >= 0 && _interval.SelectedIndex != index)
        {
            _rebuildingNav = true;
            try
            {
                _interval.SelectedIndex = index;
            }
            finally
            {
                _rebuildingNav = false;
            }
        }

        _updated.ForeColor = paused ? _context.Theme.Palette.Accent : _context.Theme.Palette.Text;
    }

    private void ApplyTheme()
    {
        _context.Theme.Apply(this);
        ShowPumpState();
        SizeNotice();
        ShowRecording();
    }

    private void ApplyLanguage()
    {
        Text = Strings.Get("core.app.name") + (_context.Services.UsesFakeData ? " - " + Strings.Get("core.fake.notice") : string.Empty);
        _nav.AccessibleName = Strings.Get("gui.nav.label");
        _interval.AccessibleName = Strings.Get("gui.interval.label");
        _pause.AccessibleName = Strings.Get("gui.pause.tip");
        _tips.SetToolTip(_interval, Strings.Get("gui.interval.label"));
        _tips.SetToolTip(_pause, Strings.Get("gui.pause.tip"));

        var keep = _nav.SelectedIndex;
        _rebuildingNav = true;
        try
        {
            _nav.BeginUpdate();
            _nav.Items.Clear();
            foreach (var entry in PageCatalog.All)
            {
                _nav.Items.Add(Strings.Get(entry.TitleKey));
            }

            _nav.EndUpdate();
            if (keep >= 0)
            {
                _nav.SelectedIndex = keep;
            }

            _interval.BeginUpdate();
            _interval.Items.Clear();
            foreach (var ms in GuiSettings.IntervalChoices)
            {
                _interval.Items.Add(Strings.Get("gui.interval." + ms));
            }

            _interval.EndUpdate();
            _interval.SelectedIndex = Math.Max(Array.IndexOf(GuiSettings.IntervalChoices, _context.Pump.IntervalMs), 0);
        }
        finally
        {
            _rebuildingNav = false;
        }

        foreach (var page in _pages)
        {
            page?.ApplyLanguage();
        }

        ShowPumpState();
        ShowNotice();
        ShowRecording();
    }

    /// <summary>Writes the notice in the language shown now (so a language change rewrites it) or hides it when there is none.</summary>
    private void ShowNotice()
    {
        var text = _context.UpdateNotice;
        _notice.Text = text ?? string.Empty;
        _notice.Visible = text is not null;
        _tips.SetToolTip(_notice, text ?? string.Empty);
        SizeNotice();
    }

    /// <summary>The notice is as wide as its text, at most 40 % of the bar (an ellipsis and the tool tip carry the rest), so the messages on its left keep their room.</summary>
    private void SizeNotice()
    {
        if (!_notice.Visible)
        {
            return;
        }

        var wanted = TextRenderer.MeasureText(_notice.Text, _notice.Font).Width + LogicalToDeviceUnits(24);
        var most = Math.Max((int)(_statusBar.ClientSize.Width * 0.4), LogicalToDeviceUnits(160));
        _notice.Width = Math.Min(wanted, most);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);
}

