namespace PcMieru.Gui.Tests;

public class ThemeAndPagesTests
{
    [Fact]
    public void System_mode_follows_the_windows_setting_and_an_explicit_mode_ignores_it()
    {
        var systemDark = true;
        using var theme = new ThemeService(ThemeMode.System, () => systemDark);
        Assert.True(theme.IsDark);
        systemDark = false;
        Assert.False(theme.IsDark);
        theme.Mode = ThemeMode.Dark;
        systemDark = false;
        Assert.True(theme.IsDark);
    }

    [Fact]
    public void Changing_the_mode_raises_Changed_once_and_setting_the_same_mode_does_not()
    {
        using var theme = new ThemeService(ThemeMode.Light, () => false);
        var raised = 0;
        theme.Changed += () => raised++;
        theme.Mode = ThemeMode.Dark;
        theme.Mode = ThemeMode.Dark;
        Assert.Equal(1, raised);
    }

    [Fact]
    public void The_two_palettes_differ_and_text_stays_readable_on_its_background()
    {
        using var light = new ThemeService(ThemeMode.Light);
        using var dark = new ThemeService(ThemeMode.Dark);
        Assert.NotEqual(light.Palette, dark.Palette);
        foreach (var palette in new[] { light.Palette, dark.Palette })
        {
            Assert.True(Math.Abs(palette.Window.GetBrightness() - palette.Text.GetBrightness()) > 0.5f);
            Assert.True(Math.Abs(palette.Surface.GetBrightness() - palette.Text.GetBrightness()) > 0.5f);
            Assert.True(Math.Abs(palette.Selection.GetBrightness() - palette.SelectionText.GetBrightness()) > 0.3f);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_disabled_toggle_keeps_its_chosen_side_readable_and_every_other_disabled_label_stays_dim(bool dark)
    {
        using var theme = new ThemeService(dark ? ThemeMode.Dark : ThemeMode.Light);
        var palette = theme.Palette;
        using var chosen = new ToggleButton { Checked = true, Enabled = false };
        using var other = new ToggleButton { Enabled = false };
        using var plain = new Button { Enabled = false };

        // The chosen side is drawn on the selection's colour in the selection's own text colour: readable, as it is while the toggle is enabled.
        var chosenText = ThemeService.DisabledTextColor(chosen, palette);
        Assert.Equal(palette.SelectionText, chosenText);
        Assert.True(BuildCodeUiTests.ContrastRatio(chosenText, palette.Selection) >= 4.5, BuildCodeUiTests.ContrastRatio(chosenText, palette.Selection).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

        // The side that is not chosen and any other button keep the dim disabled colour.
        Assert.Equal(palette.TextDisabled, ThemeService.DisabledTextColor(other, palette));
        Assert.Equal(palette.TextDisabled, ThemeService.DisabledTextColor(plain, palette));

        // A test that nothing can fail proves nothing: the dim grey on the selection's background was under the line in both themes.
        Assert.True(BuildCodeUiTests.ContrastRatio(palette.TextDisabled, palette.Selection) < 4.5);
    }

    [Fact]
    public void A_button_on_the_dark_window_has_an_outline_that_shows_against_the_window_and_against_the_button_itself()
    {
        // WCAG 2.1 1.4.11: the outline of a control needs 3:1 against what it sits on. The dark outline was (70,70,70): 1.7:1 on the window, so the 「回復する」 button of the start gate window
        // read as a dim rectangle with no edge. The number is in the failure text so that a change of the colour shows what it came to.
        using var dark = new ThemeService(ThemeMode.Dark);
        var palette = dark.Palette;
        var onWindow = BuildCodeUiTests.ContrastRatio(palette.ButtonBorder, palette.Window);
        var onItself = BuildCodeUiTests.ContrastRatio(palette.ButtonBorder, palette.Surface);
        Assert.True(onWindow >= 3.0, "outline on the window: " + onWindow.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ":1");
        Assert.True(onItself >= 3.0, "outline on the button's own fill: " + onItself.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ":1");

        // The theme really puts that colour on a button and on an unchosen toggle (a palette entry nobody reads would prove nothing).
        using var button = new Button();
        using var toggle = new ToggleButton();
        dark.Apply(button);
        dark.Apply(toggle);
        Assert.Equal(palette.ButtonBorder, button.FlatAppearance.BorderColor);
        Assert.Equal(palette.ButtonBorder, toggle.FlatAppearance.BorderColor);
        Assert.NotEqual(palette.Border, button.FlatAppearance.BorderColor);
    }

    [Fact]
    public void Switching_the_theme_repaints_panels_and_labels_instead_of_freezing_the_first_colour()
    {
        using var theme = new ThemeService(ThemeMode.Dark);
        using var page = new UserControl();
        var panel = new Panel();
        var label = new Label();
        panel.Controls.Add(label);
        page.Controls.Add(panel);

        theme.Apply(page);
        Assert.Equal(theme.Palette.Window, panel.BackColor);

        theme.Mode = ThemeMode.Light;
        theme.Apply(page);
        Assert.Equal(theme.Palette.Window, page.BackColor);
        Assert.Equal(theme.Palette.Window, panel.BackColor);
        Assert.Equal(theme.Palette.Window, label.BackColor);
        Assert.Equal(theme.Palette.Text, label.ForeColor);
        Assert.Empty(SmokeTheme.Mismatches(page, theme.Palette));
    }

    [Fact]
    public void Page_ids_are_unique_lowercase_words_and_each_has_a_navigation_text_in_both_languages()
    {
        Strings.Register(GuiStrings.All);
        var ids = PageCatalog.All.Select(p => p.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches("^[a-z]+$", id));
        foreach (var entry in PageCatalog.All)
        {
            Assert.StartsWith("gui.nav.", entry.TitleKey, StringComparison.Ordinal);
            Assert.False(Strings.Get(entry.TitleKey, Language.Ja).StartsWith('['));
            Assert.False(Strings.Get(entry.TitleKey, Language.En).StartsWith('['));
        }
    }

    [Fact]
    public void Every_page_class_is_in_the_catalog_and_builds_without_a_window()
    {
        using var _ = Harness.Locale();
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
        using var pump = new SnapshotPump(services);
        using var theme = new ThemeService(ThemeMode.Light);
        var context = new GuiContext(services, pump, theme, new GuiSettings());

        var built = new List<PageBase>();
        try
        {
            foreach (var entry in PageCatalog.All)
            {
                var page = entry.Create(context);
                built.Add(page);
                Assert.Equal(entry.Id, page.Id);
                Assert.Equal(entry.TitleKey, page.TitleKey);
                Assert.Equal(Strings.Get(entry.TitleKey), page.Title);
                page.ApplyLanguage();
            }

            var declared = typeof(PageBase).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(PageBase)))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToArray();
            var inCatalog = built.Select(p => p.GetType()).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
            Assert.Equal(declared, inCatalog);
        }
        finally
        {
            foreach (var page in built)
            {
                page.Dispose();
            }
        }
    }
}
