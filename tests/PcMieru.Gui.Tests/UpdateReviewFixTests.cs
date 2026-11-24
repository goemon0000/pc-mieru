using System.Globalization;
using PcMieru.Update.Feed;

namespace PcMieru.Gui.Tests;

/// <summary>
/// Stage B step 2, review fixes (the Gui side). Each test here fails on the code the review was written about (fd3d221) and passes on the fix: the check runs on a worker
/// thread (S1), a page that is not on show neither probes the folder nor draws, and a tick that finds nothing due says nothing (S2), a tick never replaces what the owner
/// sees with "another check is running" (S4), a run with --fakes never reads or writes the owner's choice (S5, see UpdateIniTests), the theme box follows the theme (item 9),
/// the lock a save waits for is short (M3), an unknown mode word survives a save (M4), another window's choice is followed (M2) and times read without the year (item 7).
/// Nothing here asks the network: the services are fakes, and the folder is a temp folder.
/// </summary>
public sealed class UpdateReviewFixTests
{
    /// <summary>Remembers where its check ran, and what the thread that ran it saw as its synchronization context.</summary>
    private sealed class ThreadNote : IUpdateService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public int Thread { get; private set; }

        public SynchronizationContext? Context { get; private set; }

        public Task<UpdateCheck> CheckAsync(UpdateMode mode, bool manual, CancellationToken ct)
        {
            Thread = Environment.CurrentManagedThreadId;
            Context = SynchronizationContext.Current;
            Interlocked.Increment(ref _calls);
            return Task.FromResult(UpdateCheck.NoCandidate(fetched: true));
        }

        public UpdateStatus ReadStatus() => UpdateStatus.Empty;
    }

    /// <summary>A service that does its work in the synchronous part of the call, as a lock, a file read and a proxy lookup do, until the test lets it go.</summary>
    private sealed class BlockingService : IUpdateService
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public Task<UpdateCheck> CheckAsync(UpdateMode mode, bool manual, CancellationToken ct)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10)); // only a bound for a broken test: a fixed run is let go by the test long before
            return Task.FromResult(UpdateCheck.NoCandidate(fetched: true));
        }

        public UpdateStatus ReadStatus() => UpdateStatus.Empty;
    }

    private static UpdateController Controller(IUpdateService service, string mode, ManualTimers? timers = null, Func<Placement>? placement = null) =>
        new(service, UpdateKit.Impatient(UpdateKit.Settings(null, mode)), timers ?? new ManualTimers(), placement ?? (() => new Placement(PlacementKind.CanSwap, "sample")), UpdateKit.Running);

    // ---- S1: the check does not run on the UI thread ----

    [Fact]
    public void The_check_runs_on_a_worker_thread_and_its_result_comes_back_on_the_thread_that_asked()
    {
        using var _ = Harness.Locale();
        Ui.Run(ui =>
        {
            var asking = Environment.CurrentManagedThreadId;
            var service = new ThreadNote();
            using var controller = Controller(service, "notify");
            var changedOn = new List<int>();
            controller.Changed += () => changedOn.Add(Environment.CurrentManagedThreadId);

            var task = controller.CheckNowAsync();
            Assert.True(ui.Until(() => task.IsCompleted));

            Assert.Equal(1, service.Calls);
            Assert.NotEqual(asking, service.Thread);   // not the thread that owns the controls
            Assert.Null(service.Context);              // and not one that posts back to it
            Assert.NotEmpty(changedOn);
            Assert.All(changedOn, id => Assert.Equal(asking, id)); // what the page draws is drawn on the thread that asked
        });
    }

    [Fact]
    public void A_service_that_works_before_it_returns_a_task_does_not_hold_the_thread_that_asked()
    {
        using var _ = Harness.Locale();
        Ui.Run(ui =>
        {
            var service = new BlockingService();
            using var controller = Controller(service, "notify");
            try
            {
                var task = controller.CheckNowAsync();

                // Back here at once: the service is still inside its call, on another thread.
                Assert.False(task.IsCompleted);
                Assert.True(controller.Checking);
                Assert.True(service.Entered.Wait(TimeSpan.FromSeconds(10)));

                service.Release.Set();
                Assert.True(ui.Until(() => task.IsCompleted));
                Assert.False(controller.Checking);
            }
            finally
            {
                service.Release.Set();
            }
        });
    }

    // ---- S2: a tick that finds nothing due says nothing; a page that is not on show does not probe or draw ----

    [Fact]
    public void Hourly_ticks_that_find_nothing_due_raise_no_Changed_and_leave_the_page_as_it_was()
    {
        using var _ = Harness.Locale();
        Ui.Run(ui =>
        {
            var fake = new FakeUpdateService(UpdateKit.Running) { Answer = (_, _) => UpdateCheck.NoCandidate(fetched: false) };
            var timers = new ManualTimers();
            using var controller = new UpdateController(fake, UpdateKit.Impatient(UpdateKit.Settings(null, "notify")), timers, () => fake.Placement, UpdateKit.Running);
            var changes = 0;
            controller.Changed += () => changes++;
            controller.Start();

            for (var hour = 1; hour <= 24; hour++)
            {
                timers.Poll().Fire();
                Assert.True(ui.Until(() => controller.Pending.IsCompleted));
            }

            Assert.Equal(24, fake.Calls);  // every tick asked the service, which decided from its file that it was not time
            Assert.Equal(0, changes);
            Assert.Null(controller.Last);
        });
    }

    [Fact]
    public void A_page_that_is_built_but_not_on_show_neither_probes_the_folder_nor_hears_of_not_due_ticks()
    {
        using var _ = Harness.Locale();
        Ui.Run(ui =>
        {
            using var rig = new PageRig();
            var probesAfterBuild = rig.Probes;
            var changesAfterBuild = rig.Changes;

            for (var hour = 1; hour <= 24; hour++)
            {
                rig.Timers.Poll().Fire();
                Assert.True(ui.Until(() => rig.Controller.Pending.IsCompleted));
            }

            Assert.Equal(probesAfterBuild, rig.Probes);
            Assert.Equal(changesAfterBuild, rig.Changes);
            Assert.Equal(0, rig.Probes);   // nothing asks for the folder until the page is on show
        });
    }

    [Fact]
    public void A_check_that_finds_something_while_the_page_is_not_on_show_changes_the_answer_but_never_probes_the_folder()
    {
        using var _ = Harness.Locale();
        Ui.Run(ui =>
        {
            using var rig = new PageRig();
            rig.Fake.Answer = (_, _) => rig.Fake.Candidate();
            var changesBefore = rig.Changes;

            rig.Timers.Poll().Fire();
            Assert.True(ui.Until(() => rig.Controller.Pending.IsCompleted));

            Assert.NotNull(rig.Controller.Candidate());    // the state changed, so the controller says so ...
            Assert.True(rig.Changes > changesBefore);
            Assert.Equal(0, rig.Probes);                   // ... and the hidden page did not turn that into a write probe
        });
    }

    // ---- S4: a tick that finds another check running does not replace what was shown ----

    [Fact]
    public async Task A_tick_that_finds_another_check_running_keeps_what_the_last_check_said_and_says_nothing_but_the_owner_is_told()
    {
        using var _ = Harness.Locale();
        var fake = new FakeUpdateService(UpdateKit.Running);
        var timers = new ManualTimers();
        using var controller = new UpdateController(fake, UpdateKit.Impatient(UpdateKit.Settings(null, "notify")), timers, () => fake.Placement, UpdateKit.Running);
        var changes = 0;
        controller.Changed += () => changes++;
        controller.Start();

        fake.Answer = (_, _) => fake.Candidate();
        await controller.CheckNowAsync();
        Assert.Equal(UpdateCheckKind.Candidate, controller.Last!.Kind);
        var shown = controller.Last;
        var changesBefore = changes;

        // A timer finds another check running (a second window, a slow earlier look): the other one will say; nothing the owner sees changes.
        fake.Answer = (_, _) => UpdateCheck.Refuse(UpdateRefusal.AnotherCheckRunning, "another run holds the lock", fetched: false);
        await controller.TickAsync();
        Assert.Same(shown, controller.Last);
        Assert.Equal(changesBefore, changes);

        // The owner pressed 「今すぐ確認」: the answer is shown, so the page can say it is waiting for the other check.
        await controller.CheckNowAsync();
        Assert.Equal(UpdateRefusal.AnotherCheckRunning, controller.Last!.Refusal);
        Assert.True(changes > changesBefore);
    }

    // ---- item 9: the theme box ----

    [Theory]
    [InlineData(Language.En, "Light", "Dark")]
    [InlineData(Language.Ja, "ライト", "ダーク")]
    public void The_theme_box_shows_the_theme_the_app_has_however_it_was_changed_not_the_one_it_was_built_with(Language language, string light, string dark)
    {
        using var _ = Harness.Locale(language);
        using var rig = new PageRig(language);

        Assert.Equal(light, rig.Page.ThemeBox.Text);

        // The smoke run's looks and the command-line theme go through the context (or the service) while the page exists; the box must follow, shown or not.
        rig.Context.SetTheme(ThemeMode.Dark);
        Assert.Equal(dark, rig.Page.ThemeBox.Text);

        rig.Context.SetTheme(ThemeMode.Light);
        Assert.Equal(light, rig.Page.ThemeBox.Text);
    }

    // ---- M3, M4: gui.ini ----

    [Fact]
    public void A_save_waits_for_the_lock_of_another_writer_for_about_half_a_second_and_then_says_it_could_not()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, "language=en\n");
        var settings = GuiSettings.Load(ini); // the defaults, not the quick tries of a test

        Assert.InRange(settings.LockAttempts * settings.LockWaitMs, 1, 600);

        using (new HeldLock(ini))
        {
            Assert.False(settings.TrySaveUpdateMode(UpdateMode.Notify));
        }

        Assert.Equal(UpdateMode.Ask, settings.UpdateMode);
    }

    [Fact]
    public void A_mode_word_this_build_does_not_know_reads_as_not_chosen_but_stays_in_the_file_when_something_else_is_saved()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllLines(ini, ["language=en", "update.mode=weekly", "keep.me=1"]);

        var settings = GuiSettings.Load(ini);
        Assert.Equal(UpdateMode.Ask, settings.UpdateMode);
        settings.Theme = ThemeMode.Dark;
        Assert.True(settings.TrySave());

        var lines = File.ReadAllLines(ini);
        Assert.Contains("update.mode=weekly", lines);   // a newer build's choice is not erased by an older build saving the theme
        Assert.Contains("theme=dark", lines);
        Assert.Contains("keep.me=1", lines);
        Assert.Equal(UpdateMode.Ask, GuiSettings.Load(ini).UpdateMode);

        // A choice made here replaces it.
        Assert.True(settings.TrySaveUpdateMode(UpdateMode.Notify));
        lines = File.ReadAllLines(ini);
        Assert.Contains("update.mode=notify", lines);
        Assert.DoesNotContain("update.mode=weekly", lines);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ask")]
    [InlineData("  ASK ")]
    public void The_words_that_mean_not_chosen_leave_no_line_behind(string word)
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllLines(ini, ["update.mode=" + word, "keep.me=1"]);

        var settings = GuiSettings.Load(ini);
        Assert.True(settings.TrySave());

        Assert.DoesNotContain(File.ReadAllLines(ini), line => line.StartsWith("update.mode", StringComparison.Ordinal));
    }

    // ---- M2: another window's choice is followed ----

    [Fact]
    public async Task A_save_that_takes_another_windows_Off_from_the_file_stops_the_timers_and_the_checks_of_this_one()
    {
        using var _ = Harness.Locale();
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, "update.mode=notify\n");
        var settings = UpdateKit.Impatient(GuiSettings.Load(ini));
        var fake = new FakeUpdateService(UpdateKit.Running);
        var timers = new ManualTimers();
        using var controller = new UpdateController(fake, settings, timers, () => fake.Placement, UpdateKit.Running);
        controller.Start();
        Assert.True(controller.TimersRunning);

        // Another window of the app: the owner turned the check off there.
        Assert.True(new GuiSettings(ini).TrySaveUpdateMode(UpdateMode.Off));
        // This window saves something unrelated and learns the newest choice from the file.
        settings.Theme = ThemeMode.Dark;
        Assert.True(settings.TrySave());

        Assert.Equal(UpdateMode.Off, settings.UpdateMode);
        Assert.False(controller.TimersRunning);
        Assert.Equal(0, timers.Live);
        Assert.False(controller.Allowed);
        Assert.Null(await controller.CheckNowAsync());
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public void A_save_that_takes_another_windows_Notify_from_the_file_starts_the_timers_of_a_window_that_was_waiting_for_a_choice()
    {
        using var _ = Harness.Locale();
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, "language=en\n");
        var settings = UpdateKit.Impatient(GuiSettings.Load(ini));
        var fake = new FakeUpdateService(UpdateKit.Running);
        var timers = new ManualTimers();
        using var controller = new UpdateController(fake, settings, timers, () => fake.Placement, UpdateKit.Running);
        var changes = 0;
        controller.Changed += () => changes++;
        controller.Start();
        Assert.False(controller.TimersRunning);

        Assert.True(new GuiSettings(ini).TrySaveUpdateMode(UpdateMode.Notify));
        settings.Theme = ThemeMode.Dark;
        Assert.True(settings.TrySave());

        Assert.Equal(UpdateMode.Notify, settings.UpdateMode);
        Assert.True(controller.TimersRunning);
        Assert.Equal(1, timers.OnceMade);
        Assert.Equal(1, timers.EveryMade);
        Assert.True(changes > 0);   // the page shows the mode that is now in force
    }

    // ---- item 7 (and the second review, item 9): a time without the year, in the language of the screen ----

    private static string When(DateTimeOffset moment, DateTimeOffset now) =>
#if BEFORE_TREE
        UpdateText.When(moment);
#else
        UpdateText.When(moment, now);
#endif

    [Theory]
    [InlineData("ja-JP")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("")]
    public void A_time_follows_the_language_of_the_screen_and_not_the_culture_of_the_thread(string threadCulture)
    {
        var now = new DateTimeOffset(new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Local));
        var sameYear = new DateTimeOffset(new DateTime(2026, 11, 22, 15, 44, 0, DateTimeKind.Local));
        var morning = new DateTimeOffset(new DateTime(2026, 12, 24, 9, 5, 0, DateTimeKind.Local));
        var nextYear = new DateTimeOffset(new DateTime(2027, 1, 5, 14, 30, 0, DateTimeKind.Local));

        // The English screen of a Japanese Windows (thread culture ja-JP): English words and an English order, 24-hour clock.
        using (Harness.Locale(Language.En))
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(threadCulture);
            Assert.Equal("Nov 22, 15:44", When(sameYear, now));
            Assert.Equal("Dec 24, 09:05", When(morning, now));
            Assert.Equal("Jan 5, 2027, 14:30", When(nextYear, now));
        }

        // The Japanese screen of an English Windows (thread culture en-US): Japanese words, never "November".
        using (Harness.Locale(Language.Ja))
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(threadCulture);
            Assert.Equal("11月22日 15:44", When(sameYear, now));
            Assert.Equal("12月24日 9:05", When(morning, now));
            Assert.Equal("2027年1月5日 14:30", When(nextYear, now));
        }
    }

    [Fact]
    public void A_time_in_a_calendar_that_is_not_the_gregorian_one_is_still_written_in_the_gregorian_one()
    {
        // ja-JP on a Windows that counts years from the emperor's era must not turn 2027 into 令和9年: the patterns are written out, the calendar is the invariant one.
        var now = new DateTimeOffset(new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Local));
        var nextYear = new DateTimeOffset(new DateTime(2027, 1, 5, 14, 30, 0, DateTimeKind.Local));
        using var _ = Harness.Locale(Language.Ja);
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("ja-JP").Clone();
        culture.DateTimeFormat.Calendar = new JapaneseCalendar();
        CultureInfo.CurrentCulture = culture;

        Assert.Equal("2027年1月5日 14:30", When(nextYear, now));
    }

    // ---- a window to look at: the page with a controller of its own ----

    private sealed class PageRig : IDisposable
    {
        private readonly IDisposable _locale;
        private readonly ThemeService _theme = new(ThemeMode.Light);
        private int _probes;

        public PageRig(Language language = Language.En)
        {
            _locale = Harness.Locale(language);
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Settings = UpdateKit.Impatient(UpdateKit.Settings(null, "notify"));
            Controller = new UpdateController(Fake, Settings, Timers, Probe, UpdateKit.Running);
            Controller.Changed += () => Changes++;
            Context = new GuiContext(Services, Pump, _theme, Settings, updates: Controller);
            Page = new SettingsPage(Context);
            Controller.Start();
        }

        public int Probes => Volatile.Read(ref _probes);

        public int Changes { get; private set; }

        public ManualTimers Timers { get; } = new();

        public FakeUpdateService Fake { get; } = new(UpdateKit.Running) { Answer = (_, _) => UpdateCheck.NoCandidate(fetched: false) };

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public GuiSettings Settings { get; }

        public UpdateController Controller { get; }

        public GuiContext Context { get; }

        public SettingsPage Page { get; }

        private Placement Probe()
        {
            Interlocked.Increment(ref _probes);
            return Fake.Placement;
        }

        public void Dispose()
        {
            Page.Dispose();
            Controller.Dispose();
            Pump.Dispose();
            Services.Dispose();
            _theme.Dispose();
            _locale.Dispose();
        }
    }
}
