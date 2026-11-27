using PcMieru.Core.Siblings;

namespace PcMieru.Gui;

/// <summary>
/// The "Start Parts Life at sign-in" setting of the settings page (DESIGN-C 3 item 5). Off by default. The state is read from the store the context holds
/// (<see cref="GuiContext.LogonStore"/>: the real one in a real run only, a memory store in the smoke run, none in a test unless it passes one) and changed through
/// <see cref="PartyLogon"/>; both the read and the change run off the UI thread and the page re-reads afterwards, so what the box shows is what the store holds. With no store the box is
/// shown off and unavailable and does nothing. One status line under the box says why the setting is not simply on or off (turned off in Windows, another folder's registration, not readable,
/// a change that failed, a switch of the sign-in start that could not be settled, no Party exe beside this one).
/// </summary>
internal sealed partial class SettingsPage
{
    private readonly CheckBox _partyLogon = new() { AutoSize = true, Name = "settingsPartyLogon" };
    private readonly ToneLabel _partyLogonLine = new() { AutoSize = true, Name = "settingsPartyLogonLine", Visible = false };

    /// <summary>False until the first read ended: the box is shown unavailable and off meanwhile.</summary>
    private bool _logonRead;

    private bool _logonBusy;
    private bool _logonFailed;

    /// <summary>The string key of the line that says how the last click on the box ended when that is neither a plain success nor a plain failure (switched from the old start, could not switch). Cleared by the next read.</summary>
    private string? _logonNote;
    private bool _logonExeThere;
    private PartyLogonState _logonState = PartyLogonState.Off;

    /// <summary>The read or change in flight (completed when nothing is). The smoke run and the tests wait on it; a person never sees it.</summary>
    internal Task PartyLogonPending { get; private set; } = Task.CompletedTask;

    internal CheckBox PartyLogonBox => _partyLogon;

    /// <summary>The status line under the box; empty when there is none.</summary>
    internal string PartyLogonLineText => _partyLogonLine.Visible ? _partyLogonLine.Text : string.Empty;

    internal CellTone PartyLogonLineTone => _partyLogonLine.Tone;

    /// <summary>What the page last read from the store.</summary>
    internal PartyLogonState PartyLogonShown => _logonState;

    /// <summary>Scrolls the page so that the logon box is the first thing on show (the smoke run's pictures of it; a person scrolls with the wheel).</summary>
    internal void ScrollToPartyLogon() => AutoScrollPosition = new Point(0, _partyLogon.Parent?.Top ?? _partyLogon.Top);

    /// <summary>Puts the box and its line in the grid at <paramref name="row"/>. They share one cell, so a line that is not there takes no room.</summary>
    private void BuildLogonRow(TableLayoutPanel grid, int row)
    {
        _partyLogonLine.MaximumSize = new Size(Px(520), 0);
        _partyLogonLine.Margin = new Padding(Px(22), Px(2), 0, 0);
        _partyLogonLine.SetTone(CellTone.Warning);
        _partyLogon.Margin = new Padding(0);
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
            Name = "settingsPartyLogonPanel",
        };
        panel.Controls.Add(_partyLogon);
        panel.Controls.Add(_partyLogonLine);
        AddWide(grid, panel, row);
        _partyLogon.TabIndex = row;
    }

    private void ConnectLogon()
    {
        _partyLogon.CheckedChanged += (_, _) =>
        {
            if (!_filling && !_logonBusy && Context.LogonStore is not null)
            {
                PartyLogonPending = ChangeLogonAsync(_partyLogon.Checked);
            }
        };
    }

    private void ApplyLogonLanguage()
    {
        _partyLogon.Text = Strings.Get("gui.settings.partylogon");
        _tips.SetToolTip(_partyLogon, Strings.Get("gui.settings.partylogon.tip"));
    }

    /// <summary>Reads the store again and returns the task that ends when the box and its line show the result (the smoke run and the tests wait on it).</summary>
    internal Task RefreshPartyLogon()
    {
        RefreshLogon();
        return PartyLogonPending;
    }

    /// <summary>Reads the store again (when the page opens): off the UI thread, then the box and its line follow.</summary>
    private void RefreshLogon()
    {
        if (Context.LogonStore is null || _logonBusy)
        {
            return;
        }

        PartyLogonPending = ReadLogonAsync();
    }

    private async Task ReadLogonAsync()
    {
        var (store, exe, launcher) = (Context.LogonStore!, Context.PartyExePath, Context.PartyLauncherOverride ?? PartyLauncher.Real());
        var (state, there) = await Task.Run(() => (PartyLogon.Read(store, exe), launcher.Exists(exe))).ConfigureAwait(true);
        FinishLogon(state, there, failed: false);
    }

    private async Task ChangeLogonAsync(bool on)
    {
        var (context, store, exe, launcher) = (Context, Context.LogonStore!, Context.PartyExePath, Context.PartyLauncherOverride ?? PartyLauncher.Real());
        _logonBusy = true;
        SyncLogon();
        var change = await Task.Run(() =>
        {
            var applied = ApplyLogonChange(context, store, exe, on);
            return applied with { State = PartyLogon.Read(store, exe), ExeThere = launcher.Exists(exe) };
        }).ConfigureAwait(true);
        _logonBusy = false;
        if (change.Done)
        {
            Context.LogonNeedsCheck = false;   // the setting now says what the owner chose: a recovery that was stuck is settled by it
        }
        else if (change.NeedsCheck)
        {
            Context.LogonNeedsCheck = true;
        }

        FinishLogon(change.State, change.ExeThere, failed: change.Failed, note: change.Note);
    }

    /// <summary>What a change of the box did (<see cref="State"/> and <see cref="ExeThere"/> are filled in afterwards, from a fresh read).</summary>
    private sealed record LogonChange(bool Done, bool Failed, bool NeedsCheck, string? Note, PartyLogonState State = PartyLogonState.Off, bool ExeThere = false);

    /// <summary>
    /// The owner's click, on a worker thread. Off removes our entry (a stuck journal is settled by it, see <see cref="PartyLogon.Disable"/>). On: when the old Parts Life start is on and points at the old exe,
    /// checking the box is the consent to the switch (<see cref="RunSwitch.Switch"/>: the old start is replaced, so there are never two starts, also when a new value of ours already exists and Windows turned
    /// it off: it is overwritten and enabled by the same journaled steps); otherwise a plain <see cref="PartyLogon.Enable"/>, which only runs when the switch, deciding under the shared lock, says the old
    /// start is not on (never a plain Enable while it is). A journal of an earlier switch is recovered first; one that cannot be settled makes the owner's choice win (plain Enable, which deletes the journal once it was written).
    /// </summary>
    private static LogonChange ApplyLogonChange(GuiContext context, PcMieru.Core.Siblings.IRunStore store, string exe, bool on)
    {
        var seam = context.LogonSeam;
        var journalPath = context.ResolveLogonJournalPath();
        if (!on)
        {
            return Plain(PartyLogon.Disable(store, seam.LockName, seam.LockWait, journalPath, seam.FileExists));
        }

        if (journalPath is not null)
        {
            var journal = new FileRunSwitchJournal(journalPath);
            var recovery = RunSwitch.Recover(store, journal, seam.LockName, seam.LockWait, seam.FileExists);
            if (recovery.Status == RunSwitchStatus.Busy)
            {
                return new LogonChange(false, true, false, null);
            }

            if (!recovery.NeedsAttention && RunSwitch.Offer(store, exe, seam.FileExists, replaceExisting: true).Ask)
            {
                var switched = RunSwitch.Switch(store, journal, exe, seam.FileExists, seam.LockName, seam.LockWait, replaceExisting: true);
                switch (switched.Status)
                {
                    case RunSwitchStatus.Switched:
                        return new LogonChange(true, false, false, "gui.settings.partylogon.switched");
                    case RunSwitchStatus.RestoreFailed or RunSwitchStatus.NeedsUserCheck or RunSwitchStatus.RecoveryFailed:
                        return new LogonChange(false, false, true, null);
                    case RunSwitchStatus.NotOffered when OldStartIsNotOn(switched.Detail):
                        break;   // the values changed in between and the old start is not on: the plain change below
                    case RunSwitchStatus.NotOffered:
                        return new LogonChange(false, true, false, null);   // it is on, but the switch cannot be made (e.g. a stamp that cannot be put back): not a plain Enable
                    default:
                        return new LogonChange(false, false, false, "gui.settings.partylogon.notswitched");
                }
            }
        }
        else if (RunSwitch.Offer(store, exe, seam.FileExists, replaceExisting: true).Ask)
        {
            // the old start is on but there is nowhere to keep the journal of a switch: not a plain Enable (that would start both), nothing is written
            return new LogonChange(false, true, false, null);
        }

        return Plain(PartyLogon.Enable(store, exe, seam.LockName, seam.LockWait, journalPath, seam.FileExists));

        static LogonChange Plain(PartyLogonResult result) =>
            result.Failure == PartyLogonFailure.JournalUnreadable ? new LogonChange(false, false, true, null) : new LogonChange(result.Ok, !result.Ok, false, null);
    }

    /// <summary>True for the reasons of a refused switch that mean "the old start is not (effectively) on", where a plain change cannot make two starts.</summary>
    private static bool OldStartIsNotOn(string reason) =>
        reason is nameof(RunSwitchReason.OldNotThere) or nameof(RunSwitchReason.NotOldExe) or nameof(RunSwitchReason.NotEnabled);

    private void FinishLogon(PartyLogonState state, bool exeThere, bool failed, string? note = null)
    {
        if (IsDisposed)
        {
            return;
        }

        _logonRead = true;
        _logonState = state;
        _logonExeThere = exeThere;
        _logonFailed = failed;
        _logonNote = note;
        SyncLogon();
    }

    private void SyncLogon()
    {
        var before = _filling;
        _filling = true;
        try
        {
            ShowLogon();
        }
        finally
        {
            _filling = before;
        }
    }

    /// <summary>Puts the box and its line on what was last read. The caller holds <c>_filling</c> (setting <c>Checked</c> must not count as a click).</summary>
    private void ShowLogon()
    {
        if (Context.LogonStore is null || !_logonRead)
        {
            _partyLogon.Checked = false;
            _partyLogon.Enabled = false;
            ShowLogonLine(null);
            return;
        }

        if (_logonBusy)
        {
            // A change is on its way: the box keeps what the person just chose (it is not put back to the old state meanwhile) and cannot be clicked again.
            _partyLogon.Enabled = false;
            ShowLogonLine(null);
            return;
        }

        _partyLogon.Checked = _logonState == PartyLogonState.On;
        // An unreadable state is never rewritten blind; a Party exe that is not there cannot be registered (but a registration that exists can still be taken off).
        _partyLogon.Enabled = _logonState != PartyLogonState.Unknown && (_logonExeThere || _logonState == PartyLogonState.On);
        // A change of this session that failed comes first, then how a switch from the old start ended, then an unfinished switch that could not be settled (set by the recovery at start or by a click),
        // then what the state itself says.
        ShowLogonLine(_logonFailed ? "gui.settings.partylogon.failed" : _logonNote ?? (Context.LogonNeedsCheck ? "gui.settings.partylogon.check" : _logonState switch
        {
            PartyLogonState.DisabledInWindows => "gui.settings.partylogon.disabled",
            PartyLogonState.OtherPlacement => "gui.settings.partylogon.other",
            PartyLogonState.Unknown => "gui.settings.partylogon.unknown",
            _ => _logonExeThere ? null : "gui.party.missing",
        }));
    }

    private void ShowLogonLine(string? key)
    {
        _partyLogonLine.Text = key is null ? string.Empty : Strings.Get(key);
        _partyLogonLine.Visible = key is not null;
        _partyLogonLine.SetTone(key == "gui.settings.partylogon.switched" ? CellTone.Good : CellTone.Warning);
    }
}
