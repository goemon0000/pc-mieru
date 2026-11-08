namespace PcMieru.Gui;

/// <summary>One "label: value" line of a confirmation (name, PID, start time).</summary>
internal readonly record struct DialogFact(string Label, string Value);

/// <summary>
/// The small modal windows of the app: confirm an operation (target name + PID + start time shown, Cancel is the
/// default button of a destructive question) and show a result with its per-item lines. Themed like the main window.
/// </summary>
internal static class Dialogs
{
    /// <summary>When set (smoke and tests) dialogs return this instead of opening, so an automated run never waits for a click.</summary>
    internal static bool? AutoAnswer { get; set; }

    internal static List<string> Log { get; } = [];

    public static bool Confirm(
        IWin32Window? owner,
        ThemeService theme,
        string title,
        string message,
        IReadOnlyList<DialogFact> facts,
        string confirmText,
        bool destructive = true,
        IReadOnlyList<string>? lines = null)
    {
        Log.Add(title + " | " + message);
        if (AutoAnswer is { } answer)
        {
            return answer;
        }

        using var form = Build(theme, title, message, facts, lines, confirmText, Strings.Get("gui.common.cancel"), destructive);
        return form.ShowDialog(owner) == DialogResult.OK;
    }

    public static void Info(IWin32Window? owner, ThemeService theme, string title, string message, IReadOnlyList<DialogFact>? facts = null, IReadOnlyList<string>? lines = null)
    {
        Log.Add(title + " | " + message);
        if (AutoAnswer is not null)
        {
            return;
        }

        using var form = Build(theme, title, message, facts ?? [], lines, Strings.Get("gui.common.close"), null, false);
        form.ShowDialog(owner);
    }

    private static Form Build(
        ThemeService theme,
        string title,
        string message,
        IReadOnlyList<DialogFact> facts,
        IReadOnlyList<string>? lines,
        string okText,
        string? cancelText,
        bool destructive)
    {
        var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = SystemFonts.MessageBoxFont ?? Control.DefaultFont,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Name = "dialog",
        };
        var pad = form.LogicalToDeviceUnits(16);
        var width = form.LogicalToDeviceUnits(460);
        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(pad),
            Dock = DockStyle.Fill,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var text = new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Margin = new Padding(0, 0, 0, pad / 2),
            Name = "dialogMessage",
        };
        root.Controls.Add(text);

        if (facts.Count > 0)
        {
            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, pad / 2), Name = "dialogFacts" };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foreach (var fact in facts)
            {
                grid.Controls.Add(new Label { Text = fact.Label, AutoSize = true, Margin = new Padding(0, 2, form.LogicalToDeviceUnits(14), 2), Tag = "dim" });
                grid.Controls.Add(new Label { Text = fact.Value, AutoSize = true, MaximumSize = new Size(width - form.LogicalToDeviceUnits(100), 0), Margin = new Padding(0, 2, 0, 2) });
            }

            root.Controls.Add(grid);
        }

        if (lines is { Count: > 0 })
        {
            var box = new ListBox
            {
                Width = width,
                Height = Math.Min(lines.Count, 8) * (form.Font.Height + 4) + 8,
                IntegralHeight = false,
                Margin = new Padding(0, 0, 0, pad / 2),
                Name = "dialogLines",
            };
            foreach (var line in lines)
            {
                box.Items.Add(line);
            }

            root.Controls.Add(box);
        }

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, pad / 2, 0, 0),
            WrapContents = false,
        };
        var ok = new Button { Text = okText, DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(form.LogicalToDeviceUnits(96), form.LogicalToDeviceUnits(32)), Name = "dialogOk" };
        buttons.Controls.Add(ok);
        Button? cancel = null;
        if (cancelText is not null)
        {
            cancel = new Button { Text = cancelText, DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(form.LogicalToDeviceUnits(96), form.LogicalToDeviceUnits(32)), Name = "dialogCancel" };
            buttons.Controls.Add(cancel);
        }

        root.Controls.Add(buttons);
        form.Controls.Add(root);
        form.AcceptButton = destructive && cancel is not null ? cancel : ok;
        form.CancelButton = cancel ?? ok;
        form.ActiveControl = destructive && cancel is not null ? cancel : ok;
        theme.Apply(form);
        foreach (var dim in root.Controls.OfType<TableLayoutPanel>().SelectMany(t => t.Controls.OfType<Label>()).Where(l => l.Tag is "dim"))
        {
            dim.ForeColor = theme.Palette.TextDim;
        }

        return form;
    }
}
