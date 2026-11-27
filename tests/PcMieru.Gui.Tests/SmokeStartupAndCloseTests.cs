namespace PcMieru.Gui.Tests;

/// <summary>
/// C31 items 1, 9 and 10. 1: a smoke run (with or without --fakes) serves the Startup page from memory and never reads the real Run keys or Startup folder. 9: a close right after 「あとで」 waits for
/// that save without blocking the UI (bounded) before the synchronous settings save. 10: the import notice also waits while a UAC prompt is pending. Temporary folders only; dialogs answer by themselves.
/// </summary>
public sealed class SmokeStartupAndCloseTests
{
    private sealed class Rig : IDisposable
    {
        private readonly IDisposable _locale;
        private readonly bool? _auto;

        public Rig(string? roamingRoot = null)
        {
            _locale = Harness.Locale(Language.En);
            _auto = Dialogs.AutoAnswer;
            Dialogs.AutoAnswer = false;
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Context = new GuiContext(Services, Pump, new ThemeService(ThemeMode.Light), new GuiSettings())
            {
                PartyRoamingRoot = roamingRoot,
                PartyLauncherOverride = new PartyLauncher(() => false, _ => true, (path, arguments) => true),
            };
        }

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public GuiContext Context { get; }

        public void Dispose()
        {
            Dialogs.AutoAnswer = _auto;
            Pump.Dispose();
            Services.Dispose();
            _locale.Dispose();
        }
    }

    // ---- item 1 ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_smoke_services_serve_the_startup_page_from_memory_with_or_without_fakes(bool fakes)
    {
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = fakes }, SmokeStartupManager.Register);

        Assert.IsType<SmokeStartupManager>(services.Startup);
        Assert.NotEmpty(await services.Startup.ListAsync());   // the page has something to show and enumerated no registry key for it
    }

    [Fact]
    public void Only_a_smoke_run_gets_the_stand_in_and_it_is_given_before_any_window_exists()
    {
        var program = File.ReadAllText(Path.Combine(Harness.Root, "src", "PcMieru.Gui", "Program.cs"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, @"SmokeStartupManager\.Register"));
        Assert.Matches(@"options\.SmokeOutput is not null \? SmokeStartupManager\.Register : null", program);

        // the stand-in does not claim to be a fake implementation (UsesFakeData stays what --fakes decides)
        var source = File.ReadAllText(Path.Combine(Harness.Root, "src", "PcMieru.Gui", "Smoke", "SmokeStartup.cs"));
        Assert.Matches(@"class SmokeStartupManager : IStartupManager, IStartupUndo\s", source);
        Assert.DoesNotMatch(@"class SmokeStartupManager[^{]*IFakeImplementation", source);
    }

    // ---- item 9 ----

    [Fact]
    public void A_close_right_after_Later_waits_for_the_save_without_blocking_the_UI_and_then_closes()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig();
            var form = new MainForm(rig.Context) { LaterCloseWait = TimeSpan.FromSeconds(30) };
            _ = form.Handle;
            var slow = new TaskCompletionSource();
            form.PartyLaterSave = slow.Task;
            var closed = 0;
            form.FormClosed += (_, _) => closed++;

            form.Close();

            Assert.False(ui.Until(() => closed > 0, 300), "the close must wait for the Later save");
            Assert.False(form.IsDisposed);

            slow.SetResult();
            Assert.True(ui.Until(() => closed > 0));
            Assert.True(form.IsDisposed);
        });
    }

    [Fact]
    public void A_later_save_that_never_ends_holds_the_close_for_the_bound_only()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig();
            var form = new MainForm(rig.Context) { LaterCloseWait = TimeSpan.FromMilliseconds(150) };
            _ = form.Handle;
            form.PartyLaterSave = new TaskCompletionSource().Task;
            var closed = 0;
            form.FormClosed += (_, _) => closed++;

            form.Close();

            Assert.True(ui.Until(() => closed > 0));
            Assert.Equal(1, closed);
        });
    }

    [Fact]
    public void A_close_with_no_pending_later_save_closes_at_once()
    {
        Ui.Run(ui =>
        {
            using var rig = new Rig();
            var form = new MainForm(rig.Context);
            _ = form.Handle;

            form.Close();

            Assert.True(form.IsDisposed);
        });
    }

    // ---- item 10 ----

    [Fact]
    public void The_import_notice_waits_while_a_UAC_prompt_is_pending_and_comes_once_it_is_gone()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            var dir = Path.Combine(roaming.Path, "PartsLife");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "state.json"), "{}");
            using var rig = new Rig(roaming.Path);
            using var form = new MainForm(rig.Context) { FreePollMs = 5 };
            _ = form.Handle;
            var prompt = rig.Context.BeginPendingUac();
            Assert.True(rig.Context.PendingUac);
            Assert.True(form.IsBlockedByDialog);
            var logged = Dialogs.Log.Count;

            var done = form.NoticePartyImportAsync();
            Assert.False(ui.Until(() => done.IsCompleted, 400), "the notice is still waiting");
            Assert.Equal(logged, Dialogs.Log.Count);

            prompt.Dispose();
            prompt.Dispose();   // a second dispose does not count twice
            Assert.False(rig.Context.PendingUac);
            Assert.True(ui.Until(() => done.IsCompleted));
            Assert.Equal(logged + 1, Dialogs.Log.Count);
        });
    }
}
