namespace PcMieru.Gui;

/// <summary>
/// The few settings there are: update interval and pause, theme, language, always on top. Each takes effect at once and is
/// written to gui.ini; there is no Apply button. The same pause switch is in the status bar and the two stay in step.
/// </summary>
internal sealed class SettingsPage : PageBase
{
    private readonly Label _intervalLabel = new() { AutoSize = true };
    private readonly ComboBox _interval = new() { DropDownStyle = ComboBoxStyle.DropDownList, Name = "settingsInterval" };
    private readonly CheckBox _pause = new() { AutoSize = true, Name = "settingsPause" };
    private readonly Label _themeLabel = new() { AutoSize = true };
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Name = "settingsTheme" };
    private readonly Label _languageLabel = new() { AutoSize = true };
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Name = "settingsLanguage" };
    private readonly Label _textSizeLabel = new() { AutoSize = true };
    private readonly ComboBox _textSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Name = "settingsTextSize" };
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
        // Explicit cells: a hidden spacer would be skipped by the automatic flow and shift every control after it.
        foreach (var box in new[] { _interval, _theme, _language, _textSize })
        {
            box.Width = Px(260);
        }

        AddRow(grid, _intervalLabel, _interval, 0);
        AddWide(grid, _pause, 1);
        AddRow(grid, _themeLabel, _theme, 2);
        AddRow(grid, _languageLabel, _language, 3);
        AddRow(grid, _textSizeLabel, _textSize, 4);
        AddWide(grid, _onTop, 5);
        _about.Margin = new Padding(0, Px(20), 0, Px(4));
        _privacy.Margin = new Padding(0, 0, 0, 0);
        _privacy.SetTone(CellTone.Dim);
        _privacy.MaximumSize = new Size(Px(640), 0);
        grid.Controls.Add(_about, 0, 6);
        grid.SetColumnSpan(_about, 2);
        grid.Controls.Add(_privacy, 0, 7);
        grid.SetColumnSpan(_privacy, 2);
        Controls.Add(grid);

        _interval.TabIndex = 0;
        _pause.TabIndex = 1;
        _theme.TabIndex = 2;
        _language.TabIndex = 3;
        _textSize.TabIndex = 4;
        _onTop.TabIndex = 5;
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
        _textSize.SelectedIndexChanged += (_, _) =>
        {
            if (!_filling && _textSize.SelectedIndex >= 0)
            {
                Context.SetTextSize((TextSize)_textSize.SelectedIndex);
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

    internal ComboBox TextSizeBox => _textSize;

    internal CheckBox OnTopBox => _onTop;

    /// <summary>The controls are brought back in line with the live state every time the page opens (the theme or language may have been changed by a switch, a command-line option or the status bar).</summary>
    public override void OnActivated() => Sync();

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

            _pause.Text = Strings.Get("gui.settings.pause");
            _themeLabel.Text = Strings.Get("gui.settings.theme");
            _theme.Items.Clear();
            _theme.Items.AddRange([Strings.Get("gui.settings.theme.system"), Strings.Get("gui.settings.theme.light"), Strings.Get("gui.settings.theme.dark")]);
            _languageLabel.Text = Strings.Get("gui.settings.language");
            _language.Items.Clear();
            _language.Items.AddRange(["日本語", "English"]);
            _textSizeLabel.Text = Strings.Get("gui.settings.textsize");
            _textSize.Items.Clear();
            _textSize.Items.AddRange([Strings.Get("gui.settings.textsize.small"), Strings.Get("gui.settings.textsize.normal"), Strings.Get("gui.settings.textsize.large")]);
            _onTop.Text = Strings.Get("gui.settings.ontop");
            ShowCurrent();
            _about.Text = Strings.Format("gui.settings.about", Fmt.Dict(("version", AppInfo.Version)));
            _privacy.Text = Strings.Get("gui.settings.privacy");
            _interval.AccessibleName = _intervalLabel.Text;
            _theme.AccessibleName = _themeLabel.Text;
            _language.AccessibleName = _languageLabel.Text;
            _textSize.AccessibleName = _textSizeLabel.Text;
            _tips.SetToolTip(_textSize, Strings.Get("gui.settings.textsize.tip"));
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

    private void Sync()
    {
        _filling = true;
        try
        {
            ShowCurrent();
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>Puts every control on the value the app has now. The caller holds <c>_filling</c>.</summary>
    private void ShowCurrent()
    {
        _interval.SelectedIndex = Math.Max(0, Array.IndexOf(GuiSettings.IntervalChoices, Context.Settings.IntervalMs));
        _pause.Checked = Context.Pump.Paused;
        _theme.SelectedIndex = (int)Context.Theme.Mode;
        _language.SelectedIndex = (int)Strings.Current;
        _textSize.SelectedIndex = (int)Context.Settings.TextSize;
        _onTop.Checked = Context.Settings.AlwaysOnTop;
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

    private void AddRow(TableLayoutPanel grid, Control label, Control field, int row)
    {
        label.Anchor = AnchorStyles.Left;
        label.Margin = new Padding(0, 0, Px(24), Px(14));
        field.Anchor = AnchorStyles.Left;
        field.Margin = new Padding(0, 0, 0, Px(14));
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(field, 1, row);
    }

    /// <summary>A check box on its own line, under the label column too, so the check boxes line up with the labels.</summary>
    private void AddWide(TableLayoutPanel grid, Control control, int row)
    {
        control.Anchor = AnchorStyles.Left;
        control.Margin = new Padding(0, 0, 0, Px(14));
        grid.Controls.Add(control, 0, row);
        grid.SetColumnSpan(control, 2);
    }
}

internal static class SettingsStrings
{
    public static readonly StringEntry[] All =
    [
        new("gui.settings.pause", "更新を一時停止する", "Pause updates"),
        new("gui.settings.language", "表示言語", "Language"),
        new("gui.settings.theme", "テーマ", "Theme"),
        new("gui.settings.theme.system", "Windows の設定に合わせる", "Follow Windows"),
        new("gui.settings.theme.light", "ライト", "Light"),
        new("gui.settings.theme.dark", "ダーク", "Dark"),
        new("gui.settings.textsize", "文字サイズ", "Text size"),
        new("gui.settings.textsize.small", "小（密度が高い）", "Small (dense)"),
        new("gui.settings.textsize.normal", "標準", "Normal"),
        new("gui.settings.textsize.large", "大", "Large"),
        new("gui.settings.textsize.tip", "画面全体の文字の大きさです。表と詳細欄も一緒に変わります", "The size of the text everywhere. Tables and detail panes follow"),
        new("gui.settings.ontop", "常に最前面に表示", "Always on top"),
        new("gui.settings.ontop.tip", "ほかのウィンドウの前に出したままにします", "Keeps this window in front of the others"),
        new("gui.settings.about", "PCミエル {version}", "PC Mieru {version}"),
        new("gui.settings.privacy", "集めた情報は、このアプリからどこにも送りません。診断レポートは、コピーや保存をしたときだけ外に出ます。", "This app sends nothing anywhere. A diagnostic report leaves the app only when you copy or save it."),
    ];
}
