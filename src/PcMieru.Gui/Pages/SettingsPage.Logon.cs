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
        var (store, exe, launcher) = (Context.LogonStore!, Context.PartyExePath, Context.PartyLauncherOverride ?? PartyLauncher.Real());
        _logonBusy = true;
        SyncLogon();
        var (ok, state, there) = await Task.Run(() =>
        {
            var result = on ? PartyLogon.Enable(store, exe) : PartyLogon.Disable(store);
            return (result.Ok, PartyLogon.Read(store, exe), launcher.Exists(exe));
        }).ConfigureAwait(true);
        _logonBusy = false;
        FinishLogon(state, there, failed: !ok);
    }

    private void FinishLogon(PartyLogonState state, bool exeThere, bool failed)
    {
        if (IsDisposed)
        {
            return;
        }

        _logonRead = true;
        _logonState = state;
        _logonExeThere = exeThere;
        _logonFailed = failed;
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
        // A change of this session that failed comes first, then an unfinished switch of an earlier run that could not be settled (set by the recovery at start), then what the state itself says.
        ShowLogonLine(_logonFailed ? "gui.settings.partylogon.failed" : Context.LogonNeedsCheck ? "gui.settings.partylogon.check" : _logonState switch
        {
            PartyLogonState.DisabledInWindows => "gui.settings.partylogon.disabled",
            PartyLogonState.OtherPlacement => "gui.settings.partylogon.other",
            PartyLogonState.Unknown => "gui.settings.partylogon.unknown",
            _ => _logonExeThere ? null : "gui.party.missing",
        });
    }

    private void ShowLogonLine(string? key)
    {
        _partyLogonLine.Text = key is null ? string.Empty : Strings.Get(key);
        _partyLogonLine.Visible = key is not null;
    }
}
