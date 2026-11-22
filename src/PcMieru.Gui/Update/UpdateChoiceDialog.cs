using PcMieru.Update.Feed;

namespace PcMieru.Gui;

/// <summary>
/// The first-run choice of the update check (DESIGN-B 4): 「自動」「知らせるだけ（おすすめ）」「切る」, with 「知らせるだけ」 selected. 「自動」 says in its own line that versions that are not signed
/// are also installed without asking. 「決定」 saves the choice first (<see cref="UpdateController.ChooseMode"/>) and only then does the timer start; when the choice cannot be saved the
/// dialog says so and stays open, and nothing is checked. Closing the window without 「決定」 is "not chosen yet": no check, and the dialog comes again at the next start.
/// </summary>
internal sealed class UpdateChoiceDialog : Form
{
    private readonly UpdateController _controller;
    private readonly Label _lead = new() { Name = "updateLead", AutoSize = true };
    private readonly RadioButton _automatic = new() { Name = "updateChoiceAutomatic", AutoSize = true };
    private readonly Label _automaticLine = new() { Name = "updateChoiceAutomaticLine", AutoSize = true };
    private readonly RadioButton _notify = new() { Name = "updateChoiceNotify", AutoSize = true, Checked = true };
    private readonly Label _notifyLine = new() { Name = "updateChoiceNotifyLine", AutoSize = true };
    private readonly RadioButton _off = new() { Name = "updateChoiceOff", AutoSize = true };
    private readonly Label _offLine = new() { Name = "updateChoiceOffLine", AutoSize = true };
    private readonly Label _later = new() { Name = "updateChoiceLater", AutoSize = true };
    private readonly Label _failed = new() { Name = "updateChoiceFailed", AutoSize = true, Visible = false };
    private readonly Button _ok = new() { Name = "updateChoiceOk", AutoSize = true };
    private readonly ThemeService _theme;
    private bool _noActivate;
    private bool _failureShown;

    public UpdateChoiceDialog(ThemeService theme, UpdateController controller)
    {
        _theme = theme;
        _controller = controller;
        Name = "UpdateChoiceDialog";
        Text = Strings.Get("gui.update.dialog.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var pad = LogicalToDeviceUnits(16);
        var width = LogicalToDeviceUnits(560);
        var indent = LogicalToDeviceUnits(26);
        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(pad),
            Dock = DockStyle.Fill,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _lead.Text = Strings.Get("gui.update.dialog.lead");
        _lead.MaximumSize = new Size(width, 0);
        _lead.Margin = new Padding(0, 0, 0, pad);
        root.Controls.Add(_lead);

        AddChoice(root, _automatic, _automaticLine, UpdateMode.Automatic, "gui.update.dialog.automatic", width, indent, pad);
        AddChoice(root, _notify, _notifyLine, UpdateMode.Notify, "gui.update.dialog.notify", width, indent, pad);
        AddChoice(root, _off, _offLine, UpdateMode.Off, "gui.update.dialog.off", width, indent, pad);

        _later.Text = Strings.Get("gui.update.dialog.later");
        _later.MaximumSize = new Size(width, 0);
        _later.Margin = new Padding(0, pad / 2, 0, pad / 2);
        root.Controls.Add(_later);

        _failed.Text = Strings.Get("gui.update.dialog.savefailed");
        _failed.MaximumSize = new Size(width, 0);
        _failed.Margin = new Padding(0, 0, 0, pad / 2);
        root.Controls.Add(_failed);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, pad / 2, 0, 0) };
        _ok.Text = Strings.Get("gui.update.dialog.ok");
        _ok.MinimumSize = new Size(LogicalToDeviceUnits(96), LogicalToDeviceUnits(32));
        buttons.Controls.Add(_ok);
        root.Controls.Add(buttons);
        Controls.Add(root);

        AcceptButton = _ok;
        ActiveControl = _notify;
        _ok.Click += (_, _) => ConfirmNow();
        _theme.Apply(this);
        _later.ForeColor = _theme.Palette.TextDim;
        _failed.ForeColor = _theme.Palette.Accent;
        _automaticLine.ForeColor = _theme.Palette.TextDim;
        _notifyLine.ForeColor = _theme.Palette.TextDim;
        _offLine.ForeColor = _theme.Palette.TextDim;
    }

    /// <summary>The mode the selected choice stands for.</summary>
    internal UpdateMode SelectedMode => _automatic.Checked ? UpdateMode.Automatic : _off.Checked ? UpdateMode.Off : UpdateMode.Notify;

    internal RadioButton AutomaticChoice => _automatic;

    internal RadioButton NotifyChoice => _notify;

    internal RadioButton OffChoice => _off;

    internal Button OkButton => _ok;

    /// <summary>The line under 「自動」 (it carries the unsigned-versions sentence).</summary>
    internal string AutomaticLine => _automaticLine.Text;

    /// <summary>The save-failed text while it is shown; empty before.</summary>
    internal string FailureText => _failureShown ? _failed.Text : string.Empty;

    /// <summary>
    /// What 「決定」 does; the tests call it without a mouse. The choice is saved first. True (and the window closes) when it was saved; false leaves the
    /// window open with the failure said: the mode is then still "not chosen", so nothing is checked.
    /// </summary>
    internal bool ConfirmNow()
    {
        var saved = _controller.ChooseMode(SelectedMode);
        if (saved)
        {
            DialogResult = DialogResult.OK;
            Close();
            return true;
        }

        _failureShown = true;
        _failed.Visible = true;
        return false;
    }

    /// <summary>Unattended runs (smoke) must not take the keyboard focus.</summary>
    internal void PreventActivation() => _noActivate = true;

    protected override bool ShowWithoutActivation => _noActivate;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Shows the dialog and returns true when a choice was saved. With <see cref="Dialogs.AutoAnswer"/> set (smoke run and tests) it only logs and returns
    /// false: an automated run never waits for a click and never chooses for the owner.
    /// </summary>
    public static bool Open(IWin32Window? owner, ThemeService theme, UpdateController controller)
    {
        if (Dialogs.AutoAnswer is not null)
        {
            Dialogs.Log.Add(Strings.Get("gui.update.dialog.title") + " | " + Strings.Get("gui.update.dialog.lead"));
            return false;
        }

        using var form = new UpdateChoiceDialog(theme, controller);
        return form.ShowDialog(owner) == DialogResult.OK;
    }

    private void AddChoice(TableLayoutPanel root, RadioButton choice, Label line, UpdateMode mode, string lineKey, int width, int indent, int pad)
    {
        choice.Text = UpdateText.ModeName(mode);
        // Room between the options: each choice and its line, then a full gap (the dialog is read at a glance, three options must not run together).
        choice.Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(2));
        line.Text = Strings.Get(lineKey);
        line.MaximumSize = new Size(width - indent, 0);
        line.Margin = new Padding(indent, 0, 0, pad + pad / 2);
        root.Controls.Add(choice);
        root.Controls.Add(line);
    }
}
