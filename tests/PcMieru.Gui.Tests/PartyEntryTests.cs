using PcMieru.Core.Siblings;

namespace PcMieru.Gui.Tests;

/// <summary>DESIGN-C 8 step 4b, the main-window side: the Parts Life entry (3 item 3), --open-checks, and the two window messages (2.2).</summary>
public sealed class PartyEntryTests
{
    /// <summary>A launcher that starts nothing: it counts what it was asked and answers as scripted.</summary>
    private sealed class FakeLauncher
    {
        public int Activations { get; private set; }

        public List<(string Path, string Arguments)> Starts { get; } = [];

        public bool Exists { get; set; } = true;

        public bool CanActivate { get; set; }

        public bool CanStart { get; set; } = true;

        public PartyLauncher Launcher => new(
            () =>
            {
                Activations++;
                return CanActivate;
            },
            _ => Exists,
            (path, arguments) =>
            {
                Starts.Add((path, arguments));
                return CanStart;
            });
    }

    private sealed class Rig : IDisposable
    {
        private readonly IDisposable _locale;

        public Rig(Language language = Language.En)
        {
            _locale = Harness.Locale(language);
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Context = new GuiContext(Services, Pump, new ThemeService(ThemeMode.Light), new GuiSettings());
        }

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public GuiContext Context { get; }

        public void Dispose()
        {
            Pump.Dispose();
            Services.Dispose();
            _locale.Dispose();
        }
    }

    [Fact]
    public void Parts_Life_is_the_last_entry_of_the_navigation()
    {
        var last = PageCatalog.All[^1];
        Assert.Equal("party", last.Id);
        Assert.Equal("gui.nav.party", last.TitleKey);
        Assert.Equal("Parts Life", PartyStrings.All.Single(e => e.Key == "gui.nav.party").En);
    }

    [Fact]
    public void The_arguments_carry_the_language_the_gui_shows()
    {
        Assert.Equal("--lang ja", PartyLauncher.Arguments(Language.Ja));
        Assert.Equal("--lang en", PartyLauncher.Arguments(Language.En));
    }

    [Fact]
    public void The_launcher_starts_the_exe_of_its_own_folder_with_the_language()
    {
        var fake = new FakeLauncher();
        Assert.Equal(PartyOpenResult.Started, fake.Launcher.Open(Language.En));
        var start = Assert.Single(fake.Starts);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "PCMieru.Party.exe"), start.Path);
        Assert.Equal("--lang en", start.Arguments);
    }

    [Fact]
    public void An_open_window_is_brought_forward_and_nothing_is_started()
    {
        var fake = new FakeLauncher { CanActivate = true };
        Assert.Equal(PartyOpenResult.Activated, fake.Launcher.Open(Language.Ja));
        Assert.Empty(fake.Starts);
    }

    [Fact]
    public void A_missing_exe_is_told_and_nothing_is_started_or_activated()
    {
        var fake = new FakeLauncher { Exists = false };
        Assert.Equal(PartyOpenResult.Missing, fake.Launcher.Open(Language.Ja));
        Assert.Empty(fake.Starts);
        Assert.Equal(0, fake.Activations);
    }

    [Fact]
    public void An_exe_that_cannot_be_started_is_told()
    {
        var fake = new FakeLauncher { CanStart = false };
        Assert.Equal(PartyOpenResult.Failed, fake.Launcher.Open(Language.Ja));
    }

    [Fact]
    public void Showing_the_page_launches_nothing_and_only_the_button_does()
    {
        using var rig = new Rig();
        var fake = new FakeLauncher();
        using var page = new PartyPage(rig.Context, fake.Launcher);
        page.OnActivated();
        page.ApplyLanguage();
        Assert.Equal(0, fake.Activations);
        Assert.Empty(fake.Starts);
        Assert.Equal(string.Empty, page.StatusLabel.Text);

        page.OpenButton.PerformClick();
        Assert.Equal(1, fake.Activations);
        var start = Assert.Single(fake.Starts);
        Assert.Equal("--lang en", start.Arguments);
    }

    [Fact]
    public void A_missing_exe_shows_a_line_on_the_page_and_does_not_throw()
    {
        using var rig = new Rig(Language.Ja);
        var fake = new FakeLauncher { Exists = false };
        using var page = new PartyPage(rig.Context, fake.Launcher);
        page.OpenButton.PerformClick();
        Assert.Equal(PartyOpenResult.Missing, page.LastResult);
        Assert.Equal(Strings.Get("gui.party.missing"), page.StatusLabel.Text);
        Assert.Empty(fake.Starts);
    }

    [Fact]
    public void Open_checks_is_page_checks_and_cannot_name_another_page()
    {
        Assert.True(GuiArgs.TryParse(["--open-checks"], out var options, out _));
        Assert.Equal("checks", options.StartPage);
        Assert.True(GuiArgs.TryParse(["--open-checks", "--page", "checks"], out options, out _));
        Assert.Equal("checks", options.StartPage);
        Assert.False(GuiArgs.TryParse(["--open-checks", "--page", "hardware"], out _, out var problem));
        Assert.Contains("--open-checks", problem);
        Assert.False(GuiArgs.TryParse(["--page", "hardware", "--open-checks"], out _, out _));
        Assert.False(GuiArgs.TryParse(["--open-checks", "--measure", "out.json"], out _, out _));
    }

    [Fact]
    public void The_open_checks_message_shows_the_checks_page_and_any_other_message_does_nothing()
    {
        using var rig = new Rig();
        using var form = new MainForm(rig.Context);
        _ = form.Handle;
        Assert.True(form.ShowPage("processes"));

        Assert.False(form.HandleSiblingMessage(0x0010));                           // WM_CLOSE
        Assert.False(form.HandleSiblingMessage(MieruMessages.LanguageChanged));   // not for this window
        Assert.False(form.HandleSiblingMessage(0));
        Assert.Equal("processes", form.CurrentPage?.Id);

        Assert.True(form.HandleSiblingMessage(MieruMessages.OpenChecks));
        Assert.Equal("checks", form.CurrentPage?.Id);
    }

    [Fact]
    public void A_language_change_tells_the_sibling_once_and_a_run_without_a_notifier_tells_nobody()
    {
        using var rig = new Rig();
        Assert.Null(rig.Context.SiblingLanguageNotifier);   // the default of every test, smoke and measure run: no real window is ever messaged
        rig.Context.SetLanguage(Language.Ja);                // no notifier: nothing to call, nothing thrown

        var told = 0;
        rig.Context.SiblingLanguageNotifier = () => told++;
        rig.Context.SetLanguage(Language.En);
        Assert.Equal(1, told);
        rig.Context.SetLanguage(Language.En);                // not a change
        Assert.Equal(1, told);
    }
}
