namespace PcMieru.Gui;

/// <summary>
/// The few settings there are: update interval and pause, theme, language, always on top. Each takes effect at once and is
/// written to gui.ini; there is no Apply button. The same pause switch is in the status bar and the two stay in step.
/// </summary>
internal sealed class SettingsPage : PageBase
{
    private readonly Label _intervalLabel = new() { AutoSize = true };
    private readonly ComboBox _interval = new() { DropDownStyle = ComboBoxStyle.DropDownList, Name = "settingsInterval", Width = 220 };
    private readonly CheckBox _pause = new() { AutoSize = true, Name = "settingsPause" };
    private readonly Label _themeLabel = new() { AutoSize = true };
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Name = "settingsTheme", Width = 220 };
    private readonly Label _languageLabel = new() { AutoSize = true };
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Name = "settingsLanguage", Width = 220 };
    private readonly CheckBox _onTop = new() { AutoSize = true, Name = "settingsOnTop" };
    private readonly Label _about = new() { AutoSize = true, Name = "settingsAbout" };
    private readonly ToneLabel _privacy = new() { AutoSize = true, Name = "settingsPrivacy" };
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private bool _filling;

    public SettingsPage(GuiContext context)
        : base(context, "settings", "gui.nav.settings")
    {
        var pad = Px(24);
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(pad, pad, pad, Px(8)),
            Name = "settingsGrid",
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(grid, _intervalLabel, _interval);
        AddRow(grid, new Label { AutoSize = true, Visible = false }, _pause);
        AddRow(grid, _themeLabel, _theme);
        AddRow(grid, _languageLabel, _language);
        AddRow(grid, new Label { AutoSize = true, Visible = false }, _onTop);
        _about.Margin = new Padding(0, Px(16), 0, Px(4));
        _privacy.Margin = new Padding(0, 0, 0, 0);
        _privacy.SetTone(CellTone.Dim);
        grid.Controls.Add(_about, 0, 5);
        grid.SetColumnSpan(_about, 2);
        grid.Controls.Add(_privacy, 0, 6);
        grid.SetColumnSpan(_privacy, 2);
        Controls.Add(grid);

        _interval.TabIndex = 0;
        _pause.TabIndex = 1;
        _theme.TabIndex = 2;
        _language.TabIndex = 3;
        _onTop.TabIndex = 4;
        _interval.SelectedIndexChanged += (_, _) =>
        {
            if (!_filling && _interval.SelectedIndex >= 0)
            {
                Context.SetInterval(GuiSettings.IntervalChoices[_interval.SelectedIndex]);
            }
        };
        _pause.CheckedChanged += (_, _) =>
        {
            if (!_filling && Context.Pump.Paused != _pause.Checked)
            {
                Context.SetPaused(_pause.Checked);
            }
        };
        _theme.SelectedIndexChanged += (_, _) =>
        {
            if (!_filling && _theme.SelectedIndex >= 0)
            {
                Context.SetTheme((ThemeMode)_theme.SelectedIndex);
            }
        };
        _language.SelectedIndexChanged += (_, _) =>
        {
            if (!_filling && _language.SelectedIndex >= 0)
            {
                Context.SetLanguage((Language)_language.SelectedIndex);
            }
        };
        _onTop.CheckedChanged += (_, _) =>
        {
            if (!_filling)
            {
                Context.SetAlwaysOnTop(_onTop.Checked);
            }
        };
        context.Pump.StateChanged += OnPumpState;
        ApplyLanguage();
    }

    internal ComboBox IntervalBox => _interval;

    internal CheckBox PauseBox => _pause;

    internal ComboBox ThemeBox => _theme;

    internal ComboBox LanguageBox => _language;

    internal CheckBox OnTopBox => _onTop;

    public override void OnActivated() => OnPumpState();

    public override void ApplyLanguage()
    {
        _filling = true;
        try
        {
            _intervalLabel.Text = Strings.Get("gui.interval.label");
            _interval.Items.Clear();
            foreach (var choice in GuiSettings.IntervalChoices)
            {
                _interval.Items.Add(Strings.Get("gui.interval." + choice));
            }

            _interval.SelectedIndex = Math.Max(0, Array.IndexOf(GuiSettings.IntervalChoices, Context.Settings.IntervalMs));
            _pause.Text = Strings.Get("gui.settings.pause");
            _pause.Checked = Context.Pump.Paused;
            _themeLabel.Text = Strings.Get("gui.settings.theme");
            _theme.Items.Clear();
            _theme.Items.AddRange([Strings.Get("gui.settings.theme.system"), Strings.Get("gui.settings.theme.light"), Strings.Get("gui.settings.theme.dark")]);
            _theme.SelectedIndex = (int)Context.Theme.Mode;
            _languageLabel.Text = Strings.Get("gui.settings.language");
            _language.Items.Clear();
            _language.Items.AddRange(["日本語", "English"]);
            _language.SelectedIndex = (int)Strings.Current;
            _onTop.Text = Strings.Get("gui.settings.ontop");
            _onTop.Checked = Context.Settings.AlwaysOnTop;
            _about.Text = Strings.Format("gui.settings.about", Fmt.Dict(("version", AppInfo.Version)));
            _privacy.Text = Strings.Get("gui.settings.privacy");
            _interval.AccessibleName = _intervalLabel.Text;
            _theme.AccessibleName = _themeLabel.Text;
            _language.AccessibleName = _languageLabel.Text;
            _tips.SetToolTip(_pause, Strings.Get("gui.pause.tip"));
            _tips.SetToolTip(_onTop, Strings.Get("gui.settings.ontop.tip"));
        }
        finally
        {
            _filling = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Context.Pump.StateChanged -= OnPumpState;
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnPumpState()
    {
        _filling = true;
        try
        {
            _pause.Checked = Context.Pump.Paused;
        }
        finally
        {
            _filling = false;
        }
    }

    private static void AddRow(TableLayoutPanel grid, Control label, Control field)
    {
        label.Anchor = AnchorStyles.Left;
        label.Margin = new Padding(0, 0, 24, 14);
        field.Anchor = AnchorStyles.Left;
        field.Margin = new Padding(0, 0, 0, 14);
        grid.Controls.Add(label);
        grid.Controls.Add(field);
    }
}

internal static class SettingsStrings
{
    public static readonly StringEntry[] All =
    [
        new("gui.settings.pause", "数値の更新を一時停止する", "Pause the numbers"),
        new("gui.settings.language", "表示言語", "Language"),
        new("gui.settings.theme", "テーマ", "Theme"),
        new("gui.settings.theme.system", "Windows の設定に合わせる", "Follow Windows"),
        new("gui.settings.theme.light", "ライト", "Light"),
        new("gui.settings.theme.dark", "ダーク", "Dark"),
        new("gui.settings.ontop", "ウィンドウを常に手前に表示する", "Keep the window on top"),
        new("gui.settings.ontop.tip", "ほかのウィンドウの前に出したままにします", "Keeps this window in front of the others"),
        new("gui.settings.about", "PCミエル {version}", "PC Mieru {version}"),
        new("gui.settings.privacy", "集めた情報は、このアプリからどこにも送りません。診断レポートは、コピーや保存をしたときだけ外に出ます。", "This app sends nothing anywhere. A diagnostic report leaves the app only when you copy or save it."),
    ];
}
