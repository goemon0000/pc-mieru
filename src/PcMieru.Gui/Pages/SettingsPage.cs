namespace PcMieru.Gui;

internal sealed class SettingsPage : PageBase
{
    private readonly Label _languageLabel = new() { AutoSize = true };
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Name = "settingsLanguage", Width = 200 };
    private readonly Label _themeLabel = new() { AutoSize = true };
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Name = "settingsTheme", Width = 200 };
    private readonly Label _about = new() { AutoSize = true };
    private bool _filling;

    public SettingsPage(GuiContext context)
        : base(context, "settings", "gui.nav.settings")
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(Px(16)),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(grid, _languageLabel, _language);
        AddRow(grid, _themeLabel, _theme);
        grid.Controls.Add(_about, 0, 2);
        grid.SetColumnSpan(_about, 2);
        Controls.Add(grid);

        _language.SelectedIndexChanged += (_, _) =>
        {
            if (!_filling && _language.SelectedIndex >= 0)
            {
                Context.SetLanguage((Language)_language.SelectedIndex);
            }
        };
        _theme.SelectedIndexChanged += (_, _) =>
        {
            if (!_filling && _theme.SelectedIndex >= 0)
            {
                Context.SetTheme((ThemeMode)_theme.SelectedIndex);
            }
        };
        ApplyLanguage();
    }

    public override void ApplyLanguage()
    {
        _filling = true;
        try
        {
            _languageLabel.Text = Strings.Get("gui.settings.language");
            _themeLabel.Text = Strings.Get("gui.settings.theme");
            _language.Items.Clear();
            _language.Items.AddRange(["日本語", "English"]);
            _language.SelectedIndex = (int)Strings.Current;
            _theme.Items.Clear();
            _theme.Items.AddRange([Strings.Get("gui.settings.theme.system"), Strings.Get("gui.settings.theme.light"), Strings.Get("gui.settings.theme.dark")]);
            _theme.SelectedIndex = (int)Context.Theme.Mode;
            _about.Text = Strings.Format("gui.settings.about", new Dictionary<string, string> { ["version"] = AppInfo.Version });
        }
        finally
        {
            _filling = false;
        }
    }

    private static void AddRow(TableLayoutPanel grid, Control label, Control field)
    {
        label.Anchor = AnchorStyles.Left;
        label.Margin = new Padding(0, 0, 16, 12);
        field.Margin = new Padding(0, 0, 0, 12);
        grid.Controls.Add(label);
        grid.Controls.Add(field);
    }
}

internal static class AppInfo
{
    public static string Version { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
