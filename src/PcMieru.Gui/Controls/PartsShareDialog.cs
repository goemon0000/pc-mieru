namespace PcMieru.Gui;

/// <summary>
/// "型番だけコピー（サイト用）": shows the exact text that Copy puts on the clipboard (the "pcm-parts/1" JSON of model numbers),
/// so the person can read it before anything leaves the app. Nothing is sent anywhere; the dialog has no "open the site" button.
/// </summary>
internal sealed class PartsShareDialog : Form
{
    private readonly TextBox _json = new() { Name = "shareJson", Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, AcceptsReturn = false };
    private readonly Label _lead = new() { Name = "shareLead", AutoSize = true };
    private readonly Label _note = new() { Name = "shareNote", AutoSize = true };
    private readonly Label _result = new() { Name = "shareResult", AutoSize = true };
    private readonly Button _copy = new() { Name = "shareCopy", AutoSize = true };
    private readonly Button _close = new() { Name = "shareClose", DialogResult = DialogResult.Cancel, AutoSize = true };
    private readonly Func<string, bool> _copyToClipboard;
    private readonly Font _mono;
    private readonly Font _bold;

    /// <param name="copyToClipboard">Puts the text on the clipboard; false when the clipboard was busy.</param>
    public PartsShareDialog(ThemeService theme, string json, Func<string, bool> copyToClipboard)
    {
        _copyToClipboard = copyToClipboard;
        Name = "PartsShareDialog";
        Text = Strings.Get("gui.hw.share");
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
        var width = LogicalToDeviceUnits(680);
        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(pad),
            Dock = DockStyle.Fill,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _lead.Text = Strings.Get("gui.hw.share.lead");
        _lead.MaximumSize = new Size(width, 0);
        _lead.Margin = new Padding(0, 0, 0, pad / 2);
        root.Controls.Add(_lead);

        _json.Text = json.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        _json.Size = new Size(width, LogicalToDeviceUnits(300));
        _json.Margin = new Padding(0, 0, 0, pad / 2);
        _json.AccessibleName = Strings.Get("gui.hw.share.json");
        _mono = new Font("Consolas", Font.SizeInPoints);
        root.Controls.Add(_json);

        _note.Text = Strings.Get("gui.hw.share.note");
        _note.MaximumSize = new Size(width, 0);
        _note.Margin = new Padding(0, 0, 0, pad / 2);
        _bold = new Font(Font, FontStyle.Bold);
        _note.Font = _bold;
        root.Controls.Add(_note);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, pad / 2, 0, 0) };
        _close.Text = Strings.Get("gui.common.close");
        _close.MinimumSize = new Size(LogicalToDeviceUnits(96), LogicalToDeviceUnits(32));
        _copy.Text = Strings.Get("gui.common.copy");
        _copy.MinimumSize = new Size(LogicalToDeviceUnits(96), LogicalToDeviceUnits(32));
        _result.Margin = new Padding(0, LogicalToDeviceUnits(8), LogicalToDeviceUnits(12), 0);
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_copy);
        buttons.Controls.Add(_result);
        root.Controls.Add(buttons);
        Controls.Add(root);

        AcceptButton = _copy;
        CancelButton = _close;
        ActiveControl = _copy;
        _copy.Click += (_, _) => CopyNow();
        theme.Apply(this);
        _json.Font = _mono;
        _result.ForeColor = theme.Palette.TextDim;
    }

    /// <summary>The text the dialog shows, line breaks as in the copied text. For tests.</summary>
    internal string ShownText => _json.Text.Replace(Environment.NewLine, "\n", StringComparison.Ordinal);

    internal string Note => _note.Text;

    internal string Result => _result.Text;

    /// <summary>What the Copy button does; the tests call it without a mouse.</summary>
    internal void CopyNow()
    {
        var ok = _copyToClipboard(ShownText);
        _result.Text = Strings.Get(ok ? "gui.common.copied" : "gui.hw.share.failed");
    }

    /// <summary>
    /// Shows the dialog. With <see cref="Dialogs.AutoAnswer"/> set (smoke run and tests) it only logs, so an automated run never waits
    /// for a click. Without it nothing is logged: <see cref="Dialogs.Log"/> lives as long as the app, and the document is not kept there.
    /// </summary>
    public static void Open(IWin32Window? owner, ThemeService theme, string json, Func<string, bool> copyToClipboard)
    {
        if (Dialogs.AutoAnswer is not null)
        {
            Dialogs.Log.Add(Strings.Get("gui.hw.share") + " | " + json);
            return;
        }

        using var form = new PartsShareDialog(theme, json, copyToClipboard);
        form.ShowDialog(owner);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _mono.Dispose();
            _bold.Dispose();
        }

        base.Dispose(disposing);
    }
}
