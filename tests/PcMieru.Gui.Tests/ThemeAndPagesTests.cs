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
