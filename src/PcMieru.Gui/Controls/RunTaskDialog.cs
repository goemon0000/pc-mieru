namespace PcMieru.Gui;

/// <summary>
/// "新しいタスクを実行": one line to type, Browse, and "run as administrator". It only collects the text; the caller
/// hands it to <see cref="ITaskLauncher"/> (ShellExecute, with the runas verb for the administrator box).
/// </summary>
internal sealed class RunTaskDialog : Form
{
    private readonly TextBox _command = new() { Name = "runCommand", Dock = DockStyle.Fill };
    private readonly CheckBox _admin = new() { Name = "runAdmin", AutoSize = true };
    private readonly Button _browse = new() { Name = "runBrowse", AutoSize = true };
    private readonly Button _run = new() { Name = "runOk", DialogResult = DialogResult.OK, AutoSize = true };
    private readonly Button _cancel = new() { Name = "runCancel", DialogResult = DialogResult.Cancel, AutoSize = true };
    private readonly Label _label = new() { Name = "runLabel", AutoSize = true };

    public RunTaskDialog(ThemeService theme)
    {
        Name = "RunTaskDialog";
        Text = Strings.Get("gui.run.title");
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
        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(pad),
            Dock = DockStyle.Fill,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _label.Text = Strings.Get("gui.run.label");
        _label.MaximumSize = new Size(LogicalToDeviceUnits(460), 0);
        _label.Margin = new Padding(0, 0, 0, pad / 2);
        root.Controls.Add(_label, 0, 0);
        root.SetColumnSpan(_label, 2);

        _command.MinimumSize = new Size(LogicalToDeviceUnits(340), 0);
        _command.AccessibleName = Strings.Get("gui.run.label");
        _command.Margin = new Padding(0, 0, LogicalToDeviceUnits(8), pad / 2);
        _browse.Text = Strings.Get("gui.run.browse");
        _browse.MinimumSize = new Size(LogicalToDeviceUnits(96), LogicalToDeviceUnits(30));
        _browse.Margin = new Padding(0, 0, 0, pad / 2);
        root.Controls.Add(_command, 0, 1);
        root.Controls.Add(_browse, 1, 1);

        _admin.Text = Strings.Get("gui.run.admin");
        _admin.Margin = new Padding(0, 0, 0, pad);
        root.Controls.Add(_admin, 0, 2);
        root.SetColumnSpan(_admin, 2);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        _run.Text = Strings.Get("gui.run.run");
        _run.MinimumSize = new Size(LogicalToDeviceUnits(96), LogicalToDeviceUnits(32));
        _cancel.Text = Strings.Get("gui.common.cancel");
        _cancel.MinimumSize = new Size(LogicalToDeviceUnits(96), LogicalToDeviceUnits(32));
        buttons.Controls.Add(_run);
        buttons.Controls.Add(_cancel);
        root.Controls.Add(buttons, 0, 3);
        root.SetColumnSpan(buttons, 2);
        Controls.Add(root);

        AcceptButton = _run;
        CancelButton = _cancel;
        ActiveControl = _command;
        _browse.Click += (_, _) => Browse();
        _run.Click += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(_command.Text))
            {
                DialogResult = DialogResult.None;
                _command.Focus();
                MessageBox.Show(this, Strings.Get("gui.run.empty"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        theme.Apply(this);
    }

    public string CommandLine => _command.Text.Trim();

    public bool RunAsAdministrator => _admin.Checked;

    /// <summary>For tests: fills the box without a keyboard.</summary>
    internal void Fill(string command, bool admin)
    {
        _command.Text = command;
        _admin.Checked = admin;
    }

    private void Browse()
    {
        using var dialog = new OpenFileDialog { CheckFileExists = true, Filter = "*.exe;*.bat;*.cmd;*.lnk|*.exe;*.bat;*.cmd;*.lnk|*.*|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _command.Text = "\"" + dialog.FileName + "\"";
        }
    }
}
