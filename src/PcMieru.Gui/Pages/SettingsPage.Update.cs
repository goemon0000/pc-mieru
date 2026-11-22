using PcMieru.Update.Feed;

namespace PcMieru.Gui;

/// <summary>
/// The "Updates" section of the settings page (DESIGN-B 4): this version, the mode (changeable here at any time), the last check, whether this install could
/// be updated in place (the §5 probe; if not, "notify only" and why), the newer version found, where the check stands, 「今すぐ確認」 and 「更新内容をブラウザーで開く」.
/// Everything shown comes from <see cref="UpdateController"/>; nothing here asks the network. 「今すぐ確認」 is the one button that does, and it is
/// off in Ask and Off, during a wait that GitHub asked for and while a check runs; every button that is off has a short line under it saying why. A choice is written first
/// (<see cref="UpdateController.ChooseMode"/>): when it cannot be saved the page says so and shows the mode as it really is.
///
/// Stage B step 2 review: the section is drawn only while the page is the one on show (<c>_active</c>, see <see cref="OnActivated"/>); the write probe of the install
/// folder runs when the page is activated and when the mode is chosen, off the UI thread, and the section shows what the controller remembers of it.
/// </summary>
internal sealed partial class SettingsPage
{
    /// <summary>The order of the mode box: the three choices of the first-run dialog, then "choose later" (Ask).</summary>
    private static readonly UpdateMode[] UpdateModeOrder = [UpdateMode.Automatic, UpdateMode.Notify, UpdateMode.Off, UpdateMode.Ask];

    private readonly Label _updateSection = new() { AutoSize = true, Name = "settingsUpdateSection" };
    private readonly Label _updateVersionLabel = new() { AutoSize = true };
    private readonly Label _updateVersion = new() { AutoSize = true, Name = "settingsUpdateVersion" };
    private readonly Label _updateModeLabel = new() { AutoSize = true };
    private readonly ComboBox _updateMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Name = "settingsUpdateMode" };
    private readonly Label _updateLastLabel = new() { AutoSize = true };
    private readonly Label _updateLast = new() { AutoSize = true, Name = "settingsUpdateLast" };
    private readonly Label _updatePlacementLabel = new() { AutoSize = true };
    private readonly ToneLabel _updatePlacement = new() { AutoSize = true, Name = "settingsUpdatePlacement" };
    private readonly Label _updateCandidateLabel = new() { AutoSize = true };
    private readonly Label _updateCandidate = new() { AutoSize = true, Name = "settingsUpdateCandidate" };
    private readonly Label _updateStatusLabel = new() { AutoSize = true };
    private readonly ToneLabel _updateStatus = new() { AutoSize = true, Name = "settingsUpdateStatus" };
    private readonly Button _updateCheckNow = new() { AutoSize = true, Name = "settingsUpdateCheckNow" };
    private readonly Button _updateNotes = new() { AutoSize = true, Name = "settingsUpdateNotes" };
    private readonly ToneLabel _updateCheckNowReason = new() { AutoSize = true, Name = "settingsUpdateCheckNowReason" };
    private readonly ToneLabel _updateNotesReason = new() { AutoSize = true, Name = "settingsUpdateNotesReason" };
    private string? _notesUrl;

    /// <summary>The version of this program as the section shows it.</summary>
    internal string UpdateVersionText => _updateVersion.Text;

    internal ComboBox UpdateModeBox => _updateMode;

    internal Button UpdateCheckNowButton => _updateCheckNow;

    internal Button UpdateNotesButton => _updateNotes;

    internal string UpdateLastText => _updateLast.Text;

    /// <summary>The "Install folder" value: "Writable", or that updates can only be announced here and why; "checking" until the first probe ended.</summary>
    internal string UpdatePlacementText => _updatePlacement.Text;

    internal CellTone UpdatePlacementTone => _updatePlacement.Tone;

    internal string UpdateCandidateText => _updateCandidate.Text;
    internal string UpdateCandidateLabelText => _updateCandidateLabel.Text;

    internal string UpdateStatusText => _updateStatus.Text;

    internal CellTone UpdateStatusTone => _updateStatus.Tone;

    /// <summary>The short line under 「今すぐ確認」 that says why it is off; empty while it is on.</summary>
    internal string UpdateCheckNowReasonText => _updateCheckNowReason.Visible ? _updateCheckNowReason.Text : string.Empty;

    /// <summary>The short line under 「更新内容をブラウザーで開く」 that says why it is off; empty while it is on.</summary>
    internal string UpdateNotesReasonText => _updateNotesReason.Visible ? _updateNotesReason.Text : string.Empty;

    /// <summary>The Release page that 「更新内容をブラウザーで開く」 opens; null when no newer version is known.</summary>
    internal string? NotesUrl => _notesUrl;

    /// <summary>Scrolls the page so that the update section is the first thing on show (the smoke run's pictures of it; a person scrolls with the wheel).</summary>
    internal void ScrollToUpdateSection() => AutoScrollPosition = new Point(0, _updateSection.Top);

    /// <summary>Puts the section's rows in the grid from <paramref name="row"/> on and returns the first free row.</summary>
    private int BuildUpdateSection(TableLayoutPanel grid, int row)
    {
        _updateMode.Width = Px(260);
        _updateSection.Margin = new Padding(0, Px(20), 0, Px(10));
        grid.Controls.Add(_updateSection, 0, row);
        grid.SetColumnSpan(_updateSection, 2);

        AddUpdateRow(grid, _updateVersionLabel, _updateVersion, row + 1);
        AddUpdateRow(grid, _updateModeLabel, _updateMode, row + 2);
        AddUpdateRow(grid, _updateLastLabel, _updateLast, row + 3);
        _updatePlacement.MaximumSize = new Size(Px(520), 0);
        AddUpdateRow(grid, _updatePlacementLabel, _updatePlacement, row + 4);
        AddUpdateRow(grid, _updateCandidateLabel, _updateCandidate, row + 5);
        _updateStatus.MaximumSize = new Size(Px(520), 0);
        AddUpdateRow(grid, _updateStatusLabel, _updateStatus, row + 6);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, Px(2), 0, Px(4)),
            Anchor = AnchorStyles.Left,
            Name = "settingsUpdateButtons",
        };
        foreach (var (button, reason) in new[] { (_updateCheckNow, _updateCheckNowReason), (_updateNotes, _updateNotesReason) })
        {
            // A button with its reason under it: the reason is in the same column, so a person sees which button it belongs to.
            var column = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                FlowDirection = FlowDirection.TopDown,
                Margin = new Padding(0, 0, Px(20), 0),
            };
            button.MinimumSize = new Size(Px(120), Px(30));
            button.Margin = new Padding(0, 0, 0, Px(2));
            reason.SetTone(CellTone.Dim);
            reason.MaximumSize = new Size(Px(240), 0);
            reason.Margin = new Padding(0);
            reason.Visible = false;
            column.Controls.Add(button);
            column.Controls.Add(reason);
            buttons.Controls.Add(column);
        }

        grid.Controls.Add(buttons, 0, row + 7);
        grid.SetColumnSpan(buttons, 2);

        _updateMode.TabIndex = 8;
        _updateCheckNow.TabIndex = 9;
        _updateNotes.TabIndex = 10;
        return row + 8;
    }

    /// <summary>The rows of this section are closer together than the ones above: eight more rows must still fit a normal window.</summary>
    private void AddUpdateRow(TableLayoutPanel grid, Control label, Control field, int row)
    {
        label.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        label.Margin = new Padding(0, 0, Px(24), Px(8));
        field.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        field.Margin = new Padding(0, 0, 0, Px(8));
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(field, 1, row);
    }

    private void ConnectUpdateSection()
    {
        _updateMode.SelectedIndexChanged += (_, _) =>
        {
            if (_filling || _updateMode.SelectedIndex < 0)
            {
                return;
            }

            var mode = UpdateModeOrder[_updateMode.SelectedIndex];
            if (mode != Context.Updates.Mode && !Context.Updates.ChooseMode(mode))
            {
                Context.ReportKey("gui.update.savefailed");
            }

            // Whatever was saved (or not), the box shows the mode as it really is. A new mode may change whether the folder matters: the probe is made again (off this thread).
            SyncUpdate();
            _ = Context.Updates.RefreshPlacement();
        };
        _updateCheckNow.Click += (_, _) => Run(async () => await Context.Updates.CheckNowAsync().ConfigureAwait(true));
        _updateNotes.Click += (_, _) => Context.OpenReleaseNotes(_notesUrl);
        Context.Updates.Changed += OnUpdateChanged;
    }

    private void DisconnectUpdateSection() => Context.Updates.Changed -= OnUpdateChanged;

    private void ApplyUpdateLanguage()
    {
        _updateSection.Text = Strings.Get("gui.update.section");
        _updateVersionLabel.Text = Strings.Get("gui.update.version");
        _updateModeLabel.Text = Strings.Get("gui.update.mode.label");
        _updateMode.Items.Clear();
        _updateMode.Items.AddRange([.. UpdateModeOrder.Select(UpdateText.ModeName).Cast<object>()]);
        _updateLastLabel.Text = Strings.Get("gui.update.lastcheck");
        _updatePlacementLabel.Text = Strings.Get("gui.update.placement.label");
        _updateCandidateLabel.Text = Strings.Get("gui.update.candidate.label");
        _updateStatusLabel.Text = Strings.Get("gui.update.status.label");
        _updateCheckNow.Text = Strings.Get("gui.update.checknow");
        _updateNotes.Text = Strings.Get("gui.update.notes");
        _updateMode.AccessibleName = _updateModeLabel.Text;
        _tips.SetToolTip(_updateCheckNow, Strings.Get("gui.update.checknow.tip"));
        _tips.SetToolTip(_updateNotes, Strings.Get("gui.update.notes.tip"));
    }

    /// <summary>
    /// Shows the controller's state in the section, when this page is the one on show (activation draws it, see <see cref="OnActivated"/>). The caller holds
    /// <c>_filling</c> (the mode box must not choose again).
    /// </summary>
    private void ShowUpdate()
    {
        if (!_active)
        {
            return;
        }

        var updates = Context.Updates;
        var status = updates.Status();
        var placement = updates.PlacementSeen;
        var candidate = updates.Candidate();
        var now = updates.Now;

        _updateVersion.Text = AppInfo.Version;
        _updateMode.SelectedIndex = Array.IndexOf(UpdateModeOrder, updates.Mode);
        _updateLast.Text = status.LastCheckUtc is { } at ? UpdateText.When(at, now) : Strings.Get("gui.update.lastcheck.never");
        if (placement is { } seen)
        {
            _updatePlacement.Text = UpdateText.PlacementText(seen);
            _updatePlacement.SetTone(seen.CanSwap ? CellTone.Normal : CellTone.Warning);
        }
        else
        {
            _updatePlacement.Text = Strings.Get("gui.update.placement.checking");
            _updatePlacement.SetTone(CellTone.Dim);
        }

        if (candidate is { } found)
        {
            _updateCandidate.Text = found.Version.ToString();
            _notesUrl = found.PageUrl is { } url && UpdateEndpoints.IsReleasePage(url) ? url : UpdateEndpoints.ReleasePageUrl(found.Version.Tag);
        }
        else
        {
            _updateCandidate.Text = Strings.Get("gui.update.candidate.none");
            _notesUrl = null;
        }

        var line = UpdateText.Describe(updates.Mode, updates.Allowed, updates.Checking, updates.Last, status, updates.Running, now);
        _updateStatus.Text = line.Text;
        _updateStatus.SetTone(line.Tone);

        // Ask and Off never ask the network, so the button that would is off; so is it during a wait GitHub asked for and while a check runs. Each off button says why.
        var checkReason = UpdateText.CheckNowReason(updates.Mode, updates.Allowed, updates.Checking, updates.Last, status, now);
        _updateCheckNow.Enabled = checkReason.Length == 0;
        ShowReason(_updateCheckNowReason, checkReason);
        var notesReason = UpdateText.NotesReason(_notesUrl is not null);
        _updateNotes.Enabled = notesReason.Length == 0;
        ShowReason(_updateNotesReason, notesReason);
    }

    private static void ShowReason(ToneLabel label, string reason)
    {
        label.Text = reason;
        label.Visible = reason.Length > 0;
    }

    private void SyncUpdate()
    {
        _filling = true;
        try
        {
            ShowUpdate();
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>The check started, ended, the probe ended or a mode was chosen. The controller raises it on the UI thread; a call from another thread is handed over.</summary>
    private void OnUpdateChanged()
    {
        if (IsDisposed || !_active)
        {
            return;
        }

        if (InvokeRequired && IsHandleCreated)
        {
            BeginInvoke(SyncUpdate);
            return;
        }

        SyncUpdate();
    }
}
