using System.Diagnostics;
using PcMieru.Update.Feed;

namespace PcMieru.Gui.Tests;

/// <summary>
/// What the person sees of the update check (DESIGN-B 4): the words of the status line, the first-run dialog, the "Updates" section of the settings page and the quiet notice.
/// Nothing here opens a window or waits for time: the page and the dialog are built and read without being shown, the timers are fired by hand and the service is a fake.
/// </summary>
public sealed class UpdateUiTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private static string Say(string key, string name, string value) =>
        Strings.Format(key, new Dictionary<string, string> { [name] = value });

    private static UpdateLine Describe(
        UpdateMode mode,
        bool allowed = true,
        bool checking = false,
        UpdateCheck? last = null,
        UpdateStatus? status = null,
        AppVersion? running = null) =>
        UpdateText.Describe(mode, allowed, checking, last, status ?? UpdateStatus.Empty, running ?? UpdateKit.Running, Now);

    private static UpdateCheck Newer() => new FakeUpdateService(UpdateKit.Running).Candidate(1);

    // The page's continuations come back through the message loop, so the test thread has to run it while it waits.
    private static void Finish(Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.ElapsedMilliseconds < 10_000)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }

        Assert.True(task.IsCompleted, "the action did not finish");
        task.GetAwaiter().GetResult();
    }

    private static T Finish<T>(Task<T> task)
    {
        Finish((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static IEnumerable<T> Descendants<T>(Control root)
        where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }

    private static (long Requests, long Clients) Traffic() => (FeedTraffic.Requests, FeedTraffic.ClientsCreated);

    /// <summary>A controller on a fake service with timers fired by hand: what the dialog tests need, no window and no services.</summary>
    private sealed class Bare
    {
        public Bare(string? iniPath = null, string? mode = null)
        {
            Settings = UpdateKit.Impatient(UpdateKit.Settings(iniPath, mode));
            Controller = new UpdateController(Fake, Settings, Timers, () => Fake.Placement, UpdateKit.Running);
        }

        public ManualTimers Timers { get; } = new();

        public FakeUpdateService Fake { get; } = new(UpdateKit.Running);

        public GuiSettings Settings { get; }

        public UpdateController Controller { get; }
    }

    /// <summary>The settings page on fake services, the window's context around it and a controller whose timers and service the test drives.</summary>
    private sealed class PageRig : IDisposable
    {
        private readonly IDisposable _locale;
        private readonly ThemeService _theme = new(ThemeMode.Light);

        public PageRig(string? mode = null, string? iniPath = null, IUpdateService? service = null, Language language = Language.En)
        {
            _locale = Harness.Locale(language);
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Settings = UpdateKit.Impatient(UpdateKit.Settings(iniPath, mode));
            Controller = new UpdateController(service ?? Fake, Settings, Timers, () => Fake.Placement, UpdateKit.Running);
            Context = new GuiContext(Services, Pump, _theme, Settings, updates: Controller);
            Context.StatusMessage += Messages.Add;
            Page = new SettingsPage(Context);
        }

        public ManualTimers Timers { get; } = new();

        public FakeUpdateService Fake { get; } = new(UpdateKit.Running);

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public GuiSettings Settings { get; }

        public UpdateController Controller { get; }

        public GuiContext Context { get; }

        public SettingsPage Page { get; }

        public List<string> Messages { get; } = [];

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

    private sealed class ThrowingService : IUpdateService
    {
        public Task<UpdateCheck> CheckAsync(UpdateMode mode, bool manual, CancellationToken ct) => throw new InvalidOperationException("boom");

        public UpdateStatus ReadStatus() => UpdateStatus.Empty;
    }

    // ---- the status line: a pure function of what the controller knows ----

    [Theory]
    [InlineData(UpdateMode.Ask)]
    [InlineData(UpdateMode.Notify)]
    [InlineData(UpdateMode.Automatic)]
    [InlineData(UpdateMode.Off)]
    public void A_check_in_progress_is_said_before_anything_else(UpdateMode mode)
    {
        using var _ = Harness.Locale();

        var line = Describe(mode, checking: true, last: UpdateCheck.Refuse(UpdateRefusal.AssetMissing, "x", fetched: true));

        Assert.Equal(Strings.Get("gui.update.state.checking"), line.Text);
        Assert.Equal(CellTone.Normal, line.Tone);
    }

    [Fact]
    public void Ask_and_Off_say_that_nothing_is_checked_in_a_dim_tone()
    {
        using var _ = Harness.Locale();

        var ask = Describe(UpdateMode.Ask);
        Assert.Equal(Strings.Get("gui.update.state.ask"), ask.Text);
        Assert.Equal(CellTone.Dim, ask.Tone);

        // Even a candidate that is still in state.json does not turn the line into "available" while the mode forbids asking.
        var off = Describe(UpdateMode.Off, status: UpdateStatus.Empty with { CandidateVersion = new AppVersion(0, 9, 0) });
        Assert.Equal(Strings.Get("gui.update.state.off"), off.Text);
        Assert.Equal(CellTone.Dim, off.Tone);
    }

    [Fact]
    public void A_choice_that_could_not_be_saved_is_said_as_such_in_Notify()
    {
        using var _ = Harness.Locale();

        var line = Describe(UpdateMode.Notify, allowed: false);

        Assert.Equal(Strings.Get("gui.update.savefailed"), line.Text);
        Assert.Equal(CellTone.Accent, line.Tone);
    }

    [Fact]
    public void A_wait_that_GitHub_asked_for_is_said_with_its_time_while_it_lasts_from_this_run_or_from_state_json()
    {
        using var _ = Harness.Locale();
        var until = Now.AddHours(20);
        var expected = Say("gui.update.state.waiting", "time", UpdateText.When(until));

        var fromThisRun = Describe(UpdateMode.Notify, last: UpdateCheck.Wait(until, fetched: true));
        Assert.Equal(expected, fromThisRun.Text);
        Assert.Equal(CellTone.Accent, fromThisRun.Tone);

        var fromState = Describe(UpdateMode.Notify, status: UpdateStatus.Empty with { BackoffUntilUtc = until });
        Assert.Equal(expected, fromState.Text);
        Assert.Equal(CellTone.Accent, fromState.Tone);

        // A wait outranks a refusal and a candidate that were found before it.
        var both = Describe(UpdateMode.Notify, last: UpdateCheck.Wait(until, fetched: true), status: UpdateStatus.Empty with { LastRefusal = UpdateRefusal.AssetMissing });
        Assert.Equal(expected, both.Text);
    }

    [Fact]
    public void A_wait_that_is_over_is_not_said()
    {
        using var _ = Harness.Locale();
        var over = Now.AddMinutes(-1);

        var fromThisRun = Describe(UpdateMode.Notify, last: UpdateCheck.Wait(over, fetched: true));
        var fromState = Describe(UpdateMode.Notify, status: UpdateStatus.Empty with { BackoffUntilUtc = over });

        Assert.Equal(Strings.Get("gui.update.state.scheduled"), fromThisRun.Text);
        Assert.Equal(Strings.Get("gui.update.state.scheduled"), fromState.Text);
    }

    [Fact]
    public void A_refusal_is_said_with_its_reason_from_this_run_and_from_state_json()
    {
        using var _ = Harness.Locale();
        var expected = Say("gui.update.state.refused", "reason", UpdateText.RefusalText(UpdateRefusal.AssetMissing));

        var fromThisRun = Describe(UpdateMode.Notify, last: UpdateCheck.Refuse(UpdateRefusal.AssetMissing, "SHA256.txt", fetched: true));
        Assert.Equal(expected, fromThisRun.Text);
        Assert.Equal(CellTone.Accent, fromThisRun.Tone);
        Assert.Contains("required file is missing", fromThisRun.Text);

        var fromState = Describe(UpdateMode.Notify, status: UpdateStatus.Empty with { LastRefusal = UpdateRefusal.AssetMissing });
        Assert.Equal(expected, fromState.Text);
    }

    [Fact]
    public void A_candidate_newer_than_the_running_version_is_announced_in_the_accent_tone_and_one_that_is_not_newer_is_not()
    {
        using var _ = Harness.Locale();
        var newer = Newer();
        var expected = Say("gui.update.state.candidate", "version", newer.Version!.Value.ToString());

        var fromThisRun = Describe(UpdateMode.Notify, last: newer);
        Assert.Equal(expected, fromThisRun.Text);
        Assert.Equal(CellTone.Accent, fromThisRun.Tone);

        var stored = Describe(UpdateMode.Notify, status: UpdateStatus.Empty with { CandidateVersion = newer.Version });
        Assert.Equal(expected, stored.Text);

        // The program has since been updated to that very version: the candidate is history.
        var caughtUp = Describe(UpdateMode.Notify, last: newer, running: newer.Version);
        Assert.Equal(Strings.Get("gui.update.state.scheduled"), caughtUp.Text);
        var storedCaughtUp = Describe(UpdateMode.Notify, status: UpdateStatus.Empty with { CandidateVersion = newer.Version }, running: newer.Version);
        Assert.Equal(Strings.Get("gui.update.state.scheduled"), storedCaughtUp.Text);
    }

    [Fact]
    public void After_a_check_the_next_one_is_said_as_the_later_of_the_due_time_and_now()
    {
        using var _ = Harness.Locale();

        var recent = Now.AddHours(-2);
        var soon = Describe(UpdateMode.Notify, status: UpdateStatus.Empty with { LastCheckUtc = recent });
        Assert.Equal(Say("gui.update.state.current", "time", UpdateText.When(recent + UpdateSchedule.Interval)), soon.Text);
        Assert.Equal(CellTone.Normal, soon.Tone);

        // Overdue (the PC was off): "after now", never a time in the past.
        var stale = Now.AddHours(-30);
        var overdue = Describe(UpdateMode.Notify, status: UpdateStatus.Empty with { LastCheckUtc = stale });
        Assert.Equal(Say("gui.update.state.current", "time", UpdateText.When(Now)), overdue.Text);
    }

    [Fact]
    public void Before_any_check_the_line_says_the_first_one_comes_a_little_after_the_start()
    {
        using var _ = Harness.Locale();

        var line = Describe(UpdateMode.Automatic);

        Assert.Equal(Strings.Get("gui.update.state.scheduled"), line.Text);
        Assert.Equal(CellTone.Dim, line.Tone);
    }

    // ---- the first-run dialog ----

    [Theory]
    [InlineData(Language.En, "Automatic", "Notify only", "Off", "not signed", "Confirm")]
    [InlineData(Language.Ja, "自動", "知らせるだけ", "切る", "署名のない版", "決定")]
    public void The_first_run_dialog_has_three_choices_with_Notify_selected_and_says_unsigned_versions_are_installed_unattended(
        Language language, string automatic, string notify, string off, string unsigned, string ok)
    {
        using var _ = Harness.Locale(language);
        using var theme = new ThemeService(ThemeMode.Light);
        var bare = new Bare();
        using var dialog = new UpdateChoiceDialog(theme, bare.Controller);

        Assert.Equal(3, Descendants<RadioButton>(dialog).Count());
        Assert.Equal(automatic, dialog.AutomaticChoice.Text);
        Assert.Equal(notify, dialog.NotifyChoice.Text);
        Assert.Equal(off, dialog.OffChoice.Text);
        Assert.True(dialog.NotifyChoice.Checked);
        Assert.False(dialog.AutomaticChoice.Checked);
        Assert.False(dialog.OffChoice.Checked);
        Assert.Equal(UpdateMode.Notify, dialog.SelectedMode);
        Assert.Contains(unsigned, dialog.AutomaticLine);
        Assert.Equal(ok, dialog.OkButton.Text);
        Assert.Same(dialog.OkButton, dialog.AcceptButton);
        Assert.Equal(string.Empty, dialog.FailureText);
    }

    [Fact]
    public void Picking_another_radio_button_changes_the_selected_mode()
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        var bare = new Bare();
        using var dialog = new UpdateChoiceDialog(theme, bare.Controller);

        dialog.AutomaticChoice.Checked = true;
        Assert.Equal(UpdateMode.Automatic, dialog.SelectedMode);
        Assert.False(dialog.NotifyChoice.Checked);

        dialog.OffChoice.Checked = true;
        Assert.Equal(UpdateMode.Off, dialog.SelectedMode);
        Assert.False(dialog.AutomaticChoice.Checked);
    }

    [Fact]
    public void Confirming_saves_the_choice_first_and_only_then_makes_the_timers()
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "L", "gui.ini");
        var bare = new Bare(ini);
        var seen = new List<string>();
        bare.Timers.OnMade = _ => seen.Add(File.Exists(ini) ? UpdateKit.Text(ini) : string.Empty);
        var before = Traffic();
        bare.Controller.Start();
        Assert.Empty(bare.Timers.Made); // the dialog is still to come: Ask makes no timer

        using (var dialog = new UpdateChoiceDialog(theme, bare.Controller))
        {
            Assert.True(dialog.ConfirmNow());
        }

        Assert.Equal(UpdateMode.Notify, bare.Controller.Mode);
        Assert.Equal(2, seen.Count);
        Assert.All(seen, text => Assert.Contains("update.mode=notify", text));
        Assert.Equal(0, bare.Fake.Calls); // nothing is asked before a timer fires
        Assert.Equal(before, Traffic());
    }

    [Theory]
    [InlineData(UpdateMode.Automatic, "update.mode=auto", true)]
    [InlineData(UpdateMode.Notify, "update.mode=notify", true)]
    [InlineData(UpdateMode.Off, "update.mode=off", false)]
    public void Each_choice_is_written_in_its_word_and_only_the_two_that_may_ask_start_timers(UpdateMode choice, string line, bool timers)
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "L", "gui.ini");
        var bare = new Bare(ini);
        bare.Controller.Start();

        using (var dialog = new UpdateChoiceDialog(theme, bare.Controller))
        {
            dialog.AutomaticChoice.Checked = choice == UpdateMode.Automatic;
            dialog.NotifyChoice.Checked = choice == UpdateMode.Notify;
            dialog.OffChoice.Checked = choice == UpdateMode.Off;
            Assert.True(dialog.ConfirmNow());
        }

        Assert.Equal(choice, bare.Controller.Mode);
        Assert.Contains(line, UpdateKit.Text(ini));
        Assert.Equal(timers, bare.Controller.TimersRunning);
        Assert.Equal(timers ? 2 : 0, bare.Timers.Made.Count);
        Assert.Equal(timers, bare.Controller.Allowed);
    }

    [Fact]
    public void Closing_the_dialog_without_confirming_leaves_the_mode_not_chosen_and_makes_nothing()
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "L", "gui.ini");
        var bare = new Bare(ini);
        var before = Traffic();
        bare.Controller.Start();

        using (var dialog = new UpdateChoiceDialog(theme, bare.Controller))
        {
            dialog.OffChoice.Checked = true; // picked, but never confirmed
            dialog.Close();
        }

        Assert.Equal(UpdateMode.Ask, bare.Controller.Mode);
        Assert.False(bare.Controller.Allowed);
        Assert.Empty(bare.Timers.Made);
        Assert.Equal(0, bare.Fake.Calls);
        Assert.Empty(root.Entries()); // not even the folder of gui.ini was made
        Assert.Equal(before, Traffic());
    }

    [Fact]
    public void An_automated_run_never_opens_the_dialog_and_never_chooses_for_the_owner()
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        var bare = new Bare();
        var previous = Dialogs.AutoAnswer;
        Dialogs.Log.Clear();
        Dialogs.AutoAnswer = false;
        try
        {
            Assert.False(UpdateChoiceDialog.Open(null, theme, bare.Controller));
        }
        finally
        {
            Dialogs.AutoAnswer = previous;
        }

        Assert.Single(Dialogs.Log);
        Assert.StartsWith(Strings.Get("gui.update.dialog.title"), Dialogs.Log[0], StringComparison.Ordinal);
        Assert.Equal(UpdateMode.Ask, bare.Controller.Mode);
        Assert.Empty(bare.Timers.Made);
    }

    [Fact]
    public void A_choice_that_cannot_be_saved_keeps_the_dialog_open_says_so_and_checks_nothing_until_it_can_be_saved()
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "L", "gui.ini");
        var bare = new Bare(ini);
        bare.Controller.Start();
        var before = Traffic();
        using var dialog = new UpdateChoiceDialog(theme, bare.Controller);

        var held = new HeldLock(ini);
        try
        {
            Assert.False(dialog.ConfirmNow());
            Assert.Equal(Strings.Get("gui.update.dialog.savefailed"), dialog.FailureText);
            Assert.Equal(UpdateMode.Ask, bare.Controller.Mode);
            Assert.False(bare.Controller.Allowed);
            Assert.Empty(bare.Timers.Made);
        }
        finally
        {
            held.Dispose();
        }

        Assert.Equal(0, bare.Fake.Calls);
        Assert.Equal(before, Traffic());

        // The lock is gone: the same dialog can still be confirmed, and then the timers start.
        Assert.True(dialog.ConfirmNow());
        Assert.Equal(UpdateMode.Notify, bare.Controller.Mode);
        Assert.Equal(2, bare.Timers.Made.Count);
    }

    [Fact]
    public void A_choice_of_Off_that_cannot_be_saved_still_stops_the_network_for_this_run()
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "L", "gui.ini");
        var bare = new Bare(ini, "notify");
        bare.Controller.Start();
        Assert.Equal(2, bare.Timers.Live);
        using var dialog = new UpdateChoiceDialog(theme, bare.Controller);
        dialog.OffChoice.Checked = true;

        using var held = new HeldLock(ini);

        Assert.False(dialog.ConfirmNow());
        Assert.False(bare.Controller.Allowed);
        Assert.Equal(0, bare.Timers.Live);
        Assert.NotEqual(string.Empty, dialog.FailureText);
    }

    // ---- the Updates section of the settings page ----

    [Fact]
    public void In_Ask_the_section_lists_the_four_choices_shows_Ask_and_offers_nothing_to_press()
    {
        var before = Traffic();
        using var rig = new PageRig();
        var page = rig.Page;

        Assert.Equal(new[] { "Automatic", "Notify only", "Off", "Choose later (no checks)" }, page.UpdateModeBox.Items.Cast<string>().ToArray());
        Assert.Equal(3, page.UpdateModeBox.SelectedIndex);
        Assert.Equal(AppInfo.Version, page.UpdateVersionText);
        Assert.Equal("Not checked yet", page.UpdateLastText);
        Assert.Equal("None", page.UpdateCandidateText);
        Assert.False(page.UpdateCheckNowButton.Enabled);
        Assert.False(page.UpdateNotesButton.Enabled);
        Assert.Null(page.NotesUrl);
        Assert.Equal(Strings.Get("gui.update.state.ask"), page.UpdateStatusText);
        Assert.Equal(CellTone.Dim, page.UpdateStatusTone);
        Assert.Equal(string.Empty, page.UpdatePlacementNoteText);

        // Opening the page made no timer, asked no service, built no HTTP client.
        Assert.Empty(rig.Timers.Made);
        Assert.Equal(0, rig.Fake.Calls);
        Assert.Equal(before, Traffic());
    }

    [Fact]
    public void Pressing_Check_now_in_Notify_shows_the_candidate_its_notes_button_and_the_time_of_the_check()
    {
        using var rig = new PageRig("notify");
        rig.Controller.Start();
        var page = rig.Page;
        Assert.True(page.UpdateCheckNowButton.Enabled);
        Assert.Equal(3 - 2, page.UpdateModeBox.SelectedIndex); // Notify only is the second entry
        rig.Fake.Answer = (_, _) => rig.Fake.Candidate(1);

        page.UpdateCheckNowButton.PerformClick();
        Finish(rig.Controller.Pending);

        Assert.Equal(1, rig.Fake.Calls);
        Assert.Equal("0.5.0", page.UpdateCandidateText);
        Assert.Equal(UpdateEndpoints.ReleasePageUrl("v0.5.0"), page.NotesUrl);
        Assert.True(page.UpdateNotesButton.Enabled);
        Assert.Equal(Say("gui.update.state.candidate", "version", "0.5.0"), page.UpdateStatusText);
        Assert.Equal(CellTone.Accent, page.UpdateStatusTone);
        var checkedAt = rig.Controller.Status().LastCheckUtc;
        Assert.NotNull(checkedAt);
        Assert.Equal(UpdateText.When(checkedAt.Value), page.UpdateLastText);
        Assert.True(page.UpdateCheckNowButton.Enabled); // free again
    }

    [Fact]
    public void While_a_check_runs_the_page_says_so_and_Check_now_is_off_and_it_comes_back_when_the_check_ends()
    {
        var gated = new GatedUpdateService();
        using var rig = new PageRig("notify", service: gated);
        rig.Controller.Start();
        var page = rig.Page;

        page.UpdateCheckNowButton.PerformClick();
        Assert.True(rig.Controller.Checking);
        Assert.Equal(Strings.Get("gui.update.state.checking"), page.UpdateStatusText);
        Assert.False(page.UpdateCheckNowButton.Enabled);

        // A second press while the first check runs asks nobody.
        page.UpdateCheckNowButton.PerformClick();
        Assert.Equal(1, gated.Calls);

        gated.Release(UpdateCheck.NoCandidate(fetched: true));
        Finish(rig.Controller.Pending);

        Assert.False(rig.Controller.Checking);
        Assert.NotEqual(Strings.Get("gui.update.state.checking"), page.UpdateStatusText);
        Assert.True(page.UpdateCheckNowButton.Enabled);
    }

    [Fact]
    public void A_refusal_is_shown_with_its_reason_in_both_languages_and_offers_no_notes()
    {
        using var rig = new PageRig("notify");
        rig.Controller.Start();
        var page = rig.Page;
        rig.Fake.Answer = (_, _) => UpdateCheck.Refuse(UpdateRefusal.AssetMissing, "SHA256.txt", fetched: true);

        page.UpdateCheckNowButton.PerformClick();
        Finish(rig.Controller.Pending);

        Assert.Equal(Say("gui.update.state.refused", "reason", "A required file is missing from the release"), page.UpdateStatusText);
        Assert.Equal(CellTone.Accent, page.UpdateStatusTone);
        Assert.False(page.UpdateNotesButton.Enabled);
        Assert.Equal("None", page.UpdateCandidateText);

        Strings.Current = Language.Ja;
        page.ApplyLanguage();

        Assert.Equal(Strings.Format("gui.update.state.refused", new Dictionary<string, string> { ["reason"] = "必要なファイルが、リリースに足りません" }), page.UpdateStatusText);
        Assert.Equal(new[] { "自動", "知らせるだけ", "切る", "あとで選ぶ（確認しません）" }, page.UpdateModeBox.Items.Cast<string>().ToArray());
        Assert.Equal(1, page.UpdateModeBox.SelectedIndex);
        Assert.Equal("今すぐ確認", page.UpdateCheckNowButton.Text);
        Assert.Equal("更新内容を開く", page.UpdateNotesButton.Text);
    }

    [Fact]
    public void A_wait_is_shown_with_the_time_until_which_nothing_is_asked()
    {
        using var rig = new PageRig("notify");
        rig.Controller.Start();
        var page = rig.Page;
        var until = DateTimeOffset.UtcNow.AddDays(3); // far from now on any clock: no race with the test's own speed
        rig.Fake.Answer = (_, _) => UpdateCheck.Wait(until, fetched: true);

        page.UpdateCheckNowButton.PerformClick();
        Finish(rig.Controller.Pending);

        Assert.Equal(Say("gui.update.state.waiting", "time", UpdateText.When(until)), page.UpdateStatusText);
        Assert.Equal(CellTone.Accent, page.UpdateStatusTone);
        Assert.Contains(UpdateText.When(until), page.UpdateStatusText);
    }

    [Fact]
    public void A_candidate_left_by_an_earlier_run_is_shown_without_any_check_and_a_foreign_address_is_not_followed()
    {
        using var rig = new PageRig("notify");
        rig.Fake.SetStatus(UpdateStatus.Empty with
        {
            LastCheckUtc = DateTimeOffset.UtcNow.AddHours(-3),
            CandidateVersion = new AppVersion(0, 5, 0),
            CandidatePageUrl = "https://evil.example/pc-mieru/releases/tag/v0.5.0", // state.json is an ordinary file: anybody can edit it
        });

        rig.Page.OnActivated();

        Assert.Equal(0, rig.Fake.Calls);
        Assert.Equal("0.5.0", rig.Page.UpdateCandidateText);
        Assert.Equal(UpdateEndpoints.ReleasePageUrl("v0.5.0"), rig.Page.NotesUrl);
        Assert.True(rig.Page.UpdateNotesButton.Enabled);
    }

    [Fact]
    public void Open_the_release_notes_opens_only_a_release_page_of_this_project()
    {
        using var rig = new PageRig("notify");
        rig.Controller.Start();
        var opened = new List<string>();
        var previous = ShellActions.Start;
        ShellActions.Start = info => opened.Add(info.FileName);
        try
        {
            // Nothing found yet: the button is off, a press does nothing.
            rig.Page.UpdateNotesButton.PerformClick();
            Assert.Empty(opened);

            rig.Fake.Answer = (_, _) => rig.Fake.Candidate(1);
            rig.Page.UpdateCheckNowButton.PerformClick();
            Finish(rig.Controller.Pending);
            rig.Page.UpdateNotesButton.PerformClick();
            Assert.Equal([UpdateEndpoints.ReleasePageUrl("v0.5.0")], opened);

            // The shell is asked for nothing else.
            Assert.False(rig.Context.OpenReleaseNotes(null));
            Assert.False(rig.Context.OpenReleaseNotes("https://example.com/"));
            Assert.False(rig.Context.OpenReleaseNotes("https://github.com/goemon0000/pc-mieru/releases/download/v0.5.0/PCMieru-0.5.0-win-x64.zip"));
            Assert.False(rig.Context.OpenReleaseNotes("file:///C:/Windows/System32/calc.exe"));
            Assert.Single(opened);
        }
        finally
        {
            ShellActions.Start = previous;
        }
    }

    [Fact]
    public void Choosing_Off_in_the_box_is_saved_stops_the_timers_and_turns_Check_now_off()
    {
        using var rig = new PageRig("notify");
        rig.Controller.Start();
        Assert.Equal(2, rig.Timers.Live);

        rig.Page.UpdateModeBox.SelectedIndex = 2;

        Assert.Equal(UpdateMode.Off, rig.Controller.Mode);
        Assert.Equal(2, rig.Page.UpdateModeBox.SelectedIndex);
        Assert.Equal(0, rig.Timers.Live);
        Assert.False(rig.Page.UpdateCheckNowButton.Enabled);
        Assert.Equal(Strings.Get("gui.update.state.off"), rig.Page.UpdateStatusText);
        Assert.Equal(CellTone.Dim, rig.Page.UpdateStatusTone);
        Assert.Empty(rig.Messages); // a saved choice is not worth a message
    }

    [Fact]
    public void Choosing_Notify_after_Ask_makes_the_timers_and_turns_Check_now_on()
    {
        using var rig = new PageRig();
        rig.Controller.Start();
        Assert.Empty(rig.Timers.Made);

        rig.Page.UpdateModeBox.SelectedIndex = 1;

        Assert.Equal(UpdateMode.Notify, rig.Controller.Mode);
        Assert.Equal(1, rig.Timers.OnceMade);
        Assert.Equal(1, rig.Timers.EveryMade);
        Assert.True(rig.Page.UpdateCheckNowButton.Enabled);
        Assert.Equal(1, rig.Page.UpdateModeBox.SelectedIndex);
        Assert.Equal(0, rig.Fake.Calls);
    }

    [Fact]
    public void Changing_to_Off_while_a_check_runs_cancels_it_and_the_page_shows_Off_and_no_candidate()
    {
        var gated = new GatedUpdateService();
        using var rig = new PageRig("notify", service: gated);
        rig.Controller.Start();
        rig.Page.UpdateCheckNowButton.PerformClick();
        Assert.True(rig.Controller.Checking);

        rig.Page.UpdateModeBox.SelectedIndex = 2;

        Assert.True(gated.Seen.IsCancellationRequested);
        Assert.False(rig.Controller.Checking);
        Assert.Equal(Strings.Get("gui.update.state.off"), rig.Page.UpdateStatusText);
        Assert.False(rig.Page.UpdateCheckNowButton.Enabled);
        Assert.Equal("None", rig.Page.UpdateCandidateText);
    }

    [Fact]
    public void A_choice_that_cannot_be_saved_is_said_on_the_status_bar_and_the_box_shows_the_mode_as_it_is()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "L", "gui.ini");
        using var rig = new PageRig(iniPath: ini);
        rig.Controller.Start();
        using var held = new HeldLock(ini);

        rig.Page.UpdateModeBox.SelectedIndex = 1; // Notify only

        Assert.Equal([Strings.Get("gui.update.savefailed")], rig.Messages);
        Assert.Equal(UpdateMode.Ask, rig.Controller.Mode);
        Assert.Equal(3, rig.Page.UpdateModeBox.SelectedIndex);
        Assert.False(rig.Page.UpdateCheckNowButton.Enabled);
        Assert.Empty(rig.Timers.Made);
        Assert.Equal(0, rig.Fake.Calls);
    }

    [Fact]
    public void A_failed_Off_keeps_the_old_mode_in_the_box_but_stops_the_network_and_says_why()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "L", "gui.ini");
        using var rig = new PageRig("notify", ini);
        rig.Controller.Start();
        Assert.Equal(2, rig.Timers.Live);
        using var held = new HeldLock(ini);

        rig.Page.UpdateModeBox.SelectedIndex = 2; // Off

        Assert.Equal([Strings.Get("gui.update.savefailed")], rig.Messages);
        Assert.Equal(UpdateMode.Notify, rig.Controller.Mode);
        Assert.Equal(1, rig.Page.UpdateModeBox.SelectedIndex);
        Assert.False(rig.Controller.Allowed);
        Assert.Equal(0, rig.Timers.Live);
        Assert.False(rig.Page.UpdateCheckNowButton.Enabled);
        Assert.Equal(Strings.Get("gui.update.savefailed"), rig.Page.UpdateStatusText);
        Assert.Equal(CellTone.Accent, rig.Page.UpdateStatusTone);
    }

    [Theory]
    [InlineData(PlacementKind.ReadOnly)]
    [InlineData(PlacementKind.Elevated)]
    [InlineData(PlacementKind.NotFixedDrive)]
    public void An_install_that_cannot_be_updated_in_place_says_so_and_why_but_still_announces(PlacementKind kind)
    {
        using var rig = new PageRig("notify");
        rig.Controller.Start();
        rig.Fake.Placement = new Placement(kind, "test");

        rig.Page.OnActivated();

        Assert.Equal("This location cannot be written to, so updates are only announced", rig.Page.UpdatePlacementNoteText);
        Assert.Equal(Strings.Get("gui.update.placement." + kind.ToString().ToLowerInvariant()), rig.Page.UpdatePlacementText);
        Assert.StartsWith("Notify only", rig.Page.UpdatePlacementText, StringComparison.Ordinal);
        Assert.True(rig.Page.UpdateCheckNowButton.Enabled); // a read-only folder still hears about new versions

        Strings.Current = Language.Ja;
        rig.Page.ApplyLanguage();
        Assert.Equal("この配置には書き込めないため、更新を通知します", rig.Page.UpdatePlacementNoteText);
        Assert.StartsWith("通知だけ", rig.Page.UpdatePlacementText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_writable_install_says_writable_and_shows_no_note_and_the_probe_is_made_afresh_each_time()
    {
        using var rig = new PageRig("notify");
        Assert.Equal("Writable", rig.Page.UpdatePlacementText);
        Assert.Equal(string.Empty, rig.Page.UpdatePlacementNoteText);

        // Not remembered: the folder became read-only since the page was made.
        rig.Fake.Placement = new Placement(PlacementKind.ReadOnly, "test");
        rig.Page.OnActivated();
        Assert.NotEqual(string.Empty, rig.Page.UpdatePlacementNoteText);

        rig.Fake.Placement = new Placement(PlacementKind.CanSwap, "test");
        rig.Page.OnActivated();
        Assert.Equal(string.Empty, rig.Page.UpdatePlacementNoteText);
        Assert.Equal("Writable", rig.Page.UpdatePlacementText);
    }

    [Fact]
    public void The_page_follows_a_choice_made_elsewhere_such_as_the_first_run_dialog()
    {
        using var rig = new PageRig();
        rig.Controller.Start();
        Assert.Equal(3, rig.Page.UpdateModeBox.SelectedIndex);

        Assert.True(rig.Controller.ChooseMode(UpdateMode.Automatic));

        Assert.Equal(0, rig.Page.UpdateModeBox.SelectedIndex);
        Assert.True(rig.Page.UpdateCheckNowButton.Enabled);
        Assert.Equal(Strings.Get("gui.update.state.scheduled"), rig.Page.UpdateStatusText);
    }

    // ---- the quiet notice ----

    [Fact]
    public void A_candidate_is_said_once_per_version_on_the_status_bar_and_never_in_a_dialog()
    {
        using var rig = new PageRig("notify");
        rig.Controller.Start();
        var previous = Dialogs.AutoAnswer;
        Dialogs.Log.Clear();
        Dialogs.AutoAnswer = false; // a dialog would be logged here (and never wait for a click)
        try
        {
            rig.Fake.Answer = (_, _) => rig.Fake.Candidate(1);
            Finish(rig.Controller.CheckNowAsync());
            Assert.Equal([Say("gui.update.notice", "version", "0.5.0")], rig.Messages);

            Finish(rig.Controller.CheckNowAsync()); // the same version again: nothing more is said
            Assert.Single(rig.Messages);

            rig.Fake.Answer = (_, _) => rig.Fake.Candidate(2); // a newer one: said once more
            Finish(rig.Controller.CheckNowAsync());
            Assert.Equal(2, rig.Messages.Count);
            Assert.Contains("0.6.0", rig.Messages[1]);
            Assert.Empty(Dialogs.Log);
        }
        finally
        {
            Dialogs.AutoAnswer = previous;
        }
    }

    [Fact]
    public void A_candidate_that_an_earlier_run_left_is_said_at_the_start_with_no_request()
    {
        var before = Traffic();
        using var rig = new PageRig("notify");
        rig.Fake.SetStatus(UpdateStatus.Empty with { CandidateVersion = new AppVersion(0, 5, 0), CandidatePageUrl = UpdateEndpoints.ReleasePageUrl("v0.5.0") });

        rig.Controller.Start();

        Assert.Equal([Say("gui.update.notice", "version", "0.5.0")], rig.Messages);
        Assert.Equal(0, rig.Fake.Calls);
        Assert.Equal(before, Traffic());
    }

    [Fact]
    public void In_Ask_and_Off_a_stored_candidate_is_not_said()
    {
        foreach (var word in new string?[] { null, "off" })
        {
            using var rig = new PageRig(word);
            rig.Fake.SetStatus(UpdateStatus.Empty with { CandidateVersion = new AppVersion(0, 5, 0) });

            rig.Controller.Start();

            Assert.Empty(rig.Messages);
        }
    }

    [Fact]
    public void A_check_that_throws_in_a_timer_is_said_on_the_status_bar_and_does_not_end_the_window()
    {
        using var rig = new PageRig("notify", service: new ThrowingService());
        rig.Controller.Start();

        rig.Timers.Once().Fire();
        Finish(rig.Controller.Pending);

        Assert.Single(rig.Messages);
        // The window names the kind of failure and its code, never the exception's own text (it may hold a path).
        Assert.Equal(Say("gui.status.failed", "error", CheckFormat.ExceptionDetail(new InvalidOperationException("boom"))), rig.Messages[0]);
        Assert.DoesNotContain("boom", rig.Messages[0], StringComparison.Ordinal);
        Assert.False(rig.Controller.Checking);
        Assert.True(rig.Page.UpdateCheckNowButton.Enabled);
    }

    // ---- the words, and a window on fakes ----

    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Ja)]
    public void The_privacy_text_names_the_one_exception_to_sending_nothing(Language language)
    {
        using var _ = Harness.Locale(language);

        Assert.Contains("GitHub", Strings.Get("gui.settings.privacy"));
    }

    [Fact]
    public void A_window_on_fakes_has_the_fake_update_service_and_in_Ask_asks_nobody_and_makes_no_timer()
    {
        using var _ = Harness.Locale();
        var before = Traffic();
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
        using var pump = new SnapshotPump(services);
        using var theme = new ThemeService(ThemeMode.Light);
        var context = new GuiContext(services, pump, theme, new GuiSettings());
        using var page = new SettingsPage(context);

        Assert.NotNull(context.FakeUpdates);
        Assert.Equal(UpdateMode.Ask, context.Updates.Mode);
        Assert.False(context.Updates.Allowed);

        context.Updates.Start();
        page.OnActivated();

        Assert.False(context.Updates.TimersRunning);
        Assert.Equal(0, context.FakeUpdates.Calls);
        Assert.False(page.UpdateCheckNowButton.Enabled);
        Assert.Equal(before, Traffic());
        context.Updates.Dispose();
    }
}
