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
            (SynchronizationContext.Current as QueueContext)?.Pump();
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

    /// <summary>Runs the message loop until the condition holds (a check now starts on a worker thread, so "the service was called" is a thing to wait for). The bound only ends a broken test.</summary>
    private static void WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 10_000)
        {
            Application.DoEvents();
            (SynchronizationContext.Current as QueueContext)?.Pump();
            Thread.Sleep(2);
        }

        Assert.True(condition(), "the condition did not come true");
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
        private readonly SynchronizationContext? _outerContext = SynchronizationContext.Current;
        private readonly QueueContext _uiContext = new();

        private int _probes;

        /// <param name="activate">True (the default): the page is the one on show, as the main window makes it (<c>OnActivated</c>), and its first probe has ended. False: the page is built and hidden.</param>
        public PageRig(string? mode = null, string? iniPath = null, IUpdateService? service = null, Language language = Language.En, TimeProvider? clock = null, bool activate = true, Func<Placement>? probe = null)
        {
            // The test thread stands in for the UI thread: what a check or a probe hands back is queued here and runs when the test pumps (Finish, WaitUntil). Without this,
            // xunit's own context would run the continuations (and so the page's handlers) on a pool thread, as no window of the app ever does.
            SynchronizationContext.SetSynchronizationContext(_uiContext);
            _locale = Harness.Locale(language);
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Settings = UpdateKit.Impatient(UpdateKit.Settings(iniPath, mode));
            Controller = new UpdateController(service ?? Fake, Settings, Timers, probe ?? Probe, UpdateKit.Running, clock);
            Context = new GuiContext(Services, Pump, _theme, Settings, updates: Controller);
            Context.StatusMessage += Messages.Add;
            Page = new SettingsPage(Context);
            if (activate)
            {
                Activate();
            }
        }

        /// <summary>How many times the write probe of the install folder ran, and on which thread the last one ran.</summary>
        public int Probes => Volatile.Read(ref _probes);

        public int ProbeThread { get; private set; }


        private Placement Probe()
        {
            Interlocked.Increment(ref _probes);
            ProbeThread = Environment.CurrentManagedThreadId;
            return Fake.Placement;
        }

        /// <summary>The page becomes the one on show (what the main window does when it is opened) and the probe it starts ends.</summary>
        public void Activate()
        {
            Page.OnActivated();
            Finish(Controller.RefreshPlacement());
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
            SynchronizationContext.SetSynchronizationContext(_outerContext);
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
        Assert.Equal(CellTone.Warning, line.Tone);
    }

    [Fact]
    public void A_wait_that_GitHub_asked_for_is_said_with_its_time_while_it_lasts_from_this_run_or_from_state_json()
    {
        using var _ = Harness.Locale();
        var until = Now.AddHours(20);
        var expected = Say("gui.update.state.waiting", "time", UpdateText.When(until, Now));

        var fromThisRun = Describe(UpdateMode.Notify, last: UpdateCheck.Wait(until, fetched: true));
        Assert.Equal(expected, fromThisRun.Text);
        Assert.Equal(CellTone.Dim, fromThisRun.Tone);

        var fromState = Describe(UpdateMode.Notify, status: UpdateStatus.Empty with { BackoffUntilUtc = until });
        Assert.Equal(expected, fromState.Text);
        Assert.Equal(CellTone.Dim, fromState.Tone);

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
    public void A_release_that_broke_its_own_rules_is_said_with_its_version_and_reason_from_this_run_and_from_state_json()
    {
        using var _ = Harness.Locale();
        var version = new AppVersion(0, 5, 0);
        var expected = Strings.Format("gui.update.state.refused", Fmt.Dict(("version", "0.5.0"), ("reason", UpdateText.RefusalText(UpdateRefusal.AssetMissing))));

        var fromThisRun = Describe(UpdateMode.Notify, last: UpdateCheck.Refuse(UpdateRefusal.AssetMissing, "SHA256.txt", fetched: true, version));
        Assert.Equal(expected, fromThisRun.Text);
        Assert.Equal(CellTone.Warning, fromThisRun.Tone);
        Assert.Equal("Version 0.5.0 could not be verified and will not be used: A required file is missing from the update.", fromThisRun.Text);
        Assert.DoesNotContain("release", fromThisRun.Text, StringComparison.OrdinalIgnoreCase);

        var fromState = Describe(UpdateMode.Notify, status: UpdateStatus.Empty with { LastRefusal = UpdateRefusal.AssetMissing, RefusedVersion = version });
        Assert.Equal(expected, fromState.Text);

        // No version known (the release was refused before its tag was believed): the sentence has no version in it.
        var unknown = Describe(UpdateMode.Notify, last: UpdateCheck.Refuse(UpdateRefusal.AssetMissing, "SHA256.txt", fetched: true));
        Assert.Equal(Say("gui.update.state.refusednoversion", "reason", UpdateText.RefusalText(UpdateRefusal.AssetMissing)), unknown.Text);
        Assert.DoesNotContain("{", unknown.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_check_that_did_not_finish_says_so_and_that_it_will_try_again_and_is_not_a_refusal_of_a_release()
    {
        using var _ = Harness.Locale();
        var expected = Say("gui.update.state.failed", "reason", UpdateText.RefusalText(UpdateRefusal.NetworkFailure));

        var fromThisRun = Describe(UpdateMode.Notify, last: UpdateCheck.Refuse(UpdateRefusal.NetworkFailure, "no answer", fetched: true));
        Assert.Equal(expected, fromThisRun.Text);
        Assert.Equal(CellTone.Warning, fromThisRun.Tone);
        Assert.Contains("will try again later", fromThisRun.Text);
        Assert.DoesNotContain("will not be used", fromThisRun.Text);

        var fromState = Describe(UpdateMode.Notify, status: UpdateStatus.Empty with { LastRefusal = UpdateRefusal.HttpError });
        Assert.Equal(Say("gui.update.state.failed", "reason", UpdateText.RefusalText(UpdateRefusal.HttpError)), fromState.Text);
    }

    [Theory]
    [InlineData(Language.En, "The latest check failed. Showing the version 0.5.0 found earlier.")]
    [InlineData(Language.Ja, "今回の確認はできませんでした。以前見つかった版 0.5.0 の情報を表示しています。")]
    public void A_check_that_did_not_finish_while_an_earlier_candidate_is_on_file_says_that_the_version_shown_was_found_earlier(Language language, string expected)
    {
        using var _ = Harness.Locale(language);
        var earlier = new AppVersion(0, 5, 0);

        // This run's answer is the failure, state.json still holds the candidate: the candidate row, the notes and the notice show it, so the line names it.
        var fromThisRun = Describe(
            UpdateMode.Notify,
            last: UpdateCheck.Refuse(UpdateRefusal.NetworkFailure, "no answer", fetched: true),
            status: UpdateStatus.Empty with { CandidateVersion = earlier });
        Assert.Equal(expected, fromThisRun.Text);
        Assert.Equal(CellTone.Warning, fromThisRun.Tone);

        // The same after a restart: nothing from this run, the failure and the candidate both come from state.json.
        var fromState = Describe(UpdateMode.Notify, status: UpdateStatus.Empty with { LastRefusal = UpdateRefusal.HttpError, CandidateVersion = earlier });
        Assert.Equal(expected, fromState.Text);

        // Only a failure: the reason of a refused release (its version is not usable) and a candidate that is not newer than this app do not turn into this sentence.
        var refused = Describe(
            UpdateMode.Notify,
            last: UpdateCheck.Refuse(UpdateRefusal.AssetMissing, "SHA256.txt", fetched: true, earlier),
            status: UpdateStatus.Empty with { CandidateVersion = earlier });
        Assert.NotEqual(expected, refused.Text);
        var notNewer = Describe(
            UpdateMode.Notify,
            last: UpdateCheck.Refuse(UpdateRefusal.NetworkFailure, "no answer", fetched: true),
            status: UpdateStatus.Empty with { CandidateVersion = earlier },
            running: new AppVersion(0, 5, 0));
        Assert.Equal(Say("gui.update.state.failed", "reason", UpdateText.RefusalText(UpdateRefusal.NetworkFailure)), notNewer.Text);
    }

    [Fact]
    public void Another_check_running_is_said_as_waiting_for_it_never_as_a_refusal()
    {
        using var _ = Harness.Locale();

        var line = Describe(UpdateMode.Notify, last: UpdateCheck.Refuse(UpdateRefusal.AnotherCheckRunning, "lock held", fetched: false));

        Assert.Equal(Strings.Get("gui.update.state.otherrunning"), line.Text);
        Assert.Equal(CellTone.Dim, line.Tone);
        Assert.Contains("Waiting for another check", line.Text);
        Assert.DoesNotContain("could not", line.Text);

        // What state.json remembers of that is not said on a later start.
        var stored = Describe(UpdateMode.Notify, status: UpdateStatus.Empty with { LastRefusal = UpdateRefusal.AnotherCheckRunning });
        Assert.Equal(Strings.Get("gui.update.state.scheduled"), stored.Text);
    }

    [Fact]
    public void No_status_line_is_in_the_link_tone_because_nothing_in_it_can_be_clicked()
    {
        using var _ = Harness.Locale();
        var newer = Newer();
        var lines = new[]
        {
            Describe(UpdateMode.Ask),
            Describe(UpdateMode.Off),
            Describe(UpdateMode.Notify, checking: true),
            Describe(UpdateMode.Notify, allowed: false),
            Describe(UpdateMode.Notify, last: UpdateCheck.Wait(Now.AddHours(5), fetched: true)),
            Describe(UpdateMode.Notify, last: newer),
            Describe(UpdateMode.Notify, last: UpdateCheck.Refuse(UpdateRefusal.AssetMissing, "x", fetched: true, newer.Version)),
            Describe(UpdateMode.Notify, last: UpdateCheck.Refuse(UpdateRefusal.NetworkFailure, "x", fetched: true)),
            Describe(UpdateMode.Notify, last: UpdateCheck.Refuse(UpdateRefusal.AnotherCheckRunning, "x", fetched: false)),
            Describe(UpdateMode.Notify, status: UpdateStatus.Empty with { LastCheckUtc = Now.AddHours(-1) }),
            Describe(UpdateMode.Notify),
        };

        Assert.All(lines, line => Assert.NotEqual(CellTone.Accent, line.Tone));
    }

    [Theory]
    [InlineData(Language.En, "Version 0.5.0 is available. Open the update notes in your browser to see what changed and how to get it.")]
    [InlineData(Language.Ja, "新しい版 0.5.0 があります。更新内容と入手のしかたはブラウザーで開けます。")]
    public void A_candidate_says_what_it_is_and_where_the_notes_are_without_sounding_like_a_link(Language language, string expected)
    {
        using var _ = Harness.Locale(language);

        var line = Describe(UpdateMode.Notify, last: Newer());

        Assert.Equal(expected, line.Text);
        Assert.Equal(CellTone.Normal, line.Tone);
    }

    [Fact]
    public void A_candidate_newer_than_the_running_version_is_announced_in_the_normal_tone_and_one_that_is_not_newer_is_not()
    {
        using var _ = Harness.Locale();
        var newer = Newer();
        var expected = Say("gui.update.state.candidate", "version", newer.Version!.Value.ToString());

        var fromThisRun = Describe(UpdateMode.Notify, last: newer);
        Assert.Equal(expected, fromThisRun.Text);
        Assert.Equal(CellTone.Normal, fromThisRun.Tone);

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
        Assert.Equal(Say("gui.update.state.current", "time", UpdateText.When(recent + UpdateSchedule.Interval, Now)), soon.Text);
        Assert.Equal(CellTone.Normal, soon.Tone);

        // Overdue (the PC was off): "after now", never a time in the past.
        var stale = Now.AddHours(-30);
        var overdue = Describe(UpdateMode.Notify, status: UpdateStatus.Empty with { LastCheckUtc = stale });
        Assert.Equal(Say("gui.update.state.current", "time", UpdateText.When(Now, Now)), overdue.Text);
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
    [InlineData(Language.En, "Automatic", "Notify only (recommended)", "Off", "without a Windows digital signature (proof of the publisher)", "Confirm")]
    [InlineData(Language.Ja, "自動", "知らせるだけ（おすすめ）", "切る", "Windows の電子署名（発行元の証明）がない版も入れ替えます", "決定")]
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

    [Theory]
    [InlineData(
        Language.Ja,
        "新しい版を確認なしで入れ替えます。Windows の電子署名（発行元の証明）がない版も入れ替えます。",
        "新しい版が出たらお知らせします。入れ替えは自動ではしません。",
        "『決定』を押さずに閉じると、確認はせず、次の起動でもう一度お聞きします。")]
    [InlineData(
        Language.En,
        "Installs new versions without asking, including versions without a Windows digital signature (proof of the publisher).",
        "Tells you when a new version is out. It does not install it by itself.",
        "If you close this window without choosing Confirm, nothing is checked and you will be asked again at the next start.")]
    public void The_first_run_dialog_says_what_each_choice_does_and_what_closing_it_does_in_the_words_of_the_review(Language language, string automatic, string notify, string later)
    {
        using var _ = Harness.Locale(language);
        using var theme = new ThemeService(ThemeMode.Light);
        var bare = new Bare();
        using var dialog = new UpdateChoiceDialog(theme, bare.Controller);

        var texts = Descendants<Label>(dialog).Select(label => label.Text).ToArray();
        Assert.Contains(automatic, texts);
        Assert.Contains(notify, texts);
        Assert.Contains(later, texts);
        Assert.Contains(Strings.Get("gui.update.dialog.off"), texts);

        // The top text says what is sent and what GitHub sees, in less than it did (it was three sentences and a half).
        var lead = Descendants<Label>(dialog).Single(label => label.Name == "updateLead").Text;
        Assert.Contains("GitHub", lead);
        Assert.InRange(lead.Length, 20, language == Language.Ja ? 110 : 190);
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(1.5f)]
    [InlineData(2.0f)]
    public void The_first_run_dialog_grows_with_the_size_of_its_text_and_clips_nothing(float scale)
    {
        using var _ = Harness.Locale(Language.Ja);
        using var theme = new ThemeService(ThemeMode.Light);
        var bare = new Bare();
        using var dialog = new UpdateChoiceDialog(theme, bare.Controller);

        // The text of the window at 150 % and 200 % (the same layout code runs when the screen is scaled and when the text is).
        dialog.Font = new Font(dialog.Font.FontFamily, dialog.Font.Size * scale);
        Assert.NotEqual(IntPtr.Zero, dialog.Handle);
        dialog.PerformLayout();

        var area = dialog.ClientRectangle;
        foreach (var control in Descendants<Control>(dialog).Where(c => c is Label or RadioButton or Button))
        {
            if (!control.Visible)
            {
                continue;
            }

            var spot = dialog.RectangleToClient(control.Parent!.RectangleToScreen(control.Bounds));
            Assert.True(area.Contains(spot), $"{control.Name} ({spot}) is outside the window ({area}) at {scale}");
            if (control is Label label)
            {
                // A line of text is as high as the text needs at its width: nothing is cut off at the bottom.
                var needed = label.GetPreferredSize(new Size(label.MaximumSize.Width > 0 ? label.MaximumSize.Width : label.Width, 0));
                Assert.True(label.Height >= needed.Height, $"{control.Name} is {label.Height} high and needs {needed.Height} at {scale}");
            }
        }

        // The window is as high as everything in it, with the OK button under the last line.
        Assert.True(dialog.OkButton.Bottom <= dialog.ClientSize.Height || dialog.RectangleToClient(dialog.OkButton.Parent!.RectangleToScreen(dialog.OkButton.Bounds)).Bottom <= dialog.ClientSize.Height);
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
        // The words of the title are the decided ones: it is the place where the setting is chosen, not a check being made.
        Assert.StartsWith("Update settings", Dialogs.Log[0], StringComparison.Ordinal);
        Assert.Equal("更新の設定", GuiStrings.All.Single(entry => entry.Key == "gui.update.dialog.title").Ja);
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

        Assert.Equal(new[] { "Automatic", "Notify only (recommended)", "Off", "Choose later (no checks)" }, page.UpdateModeBox.Items.Cast<string>().ToArray());
        Assert.Equal(3, page.UpdateModeBox.SelectedIndex);
        Assert.Equal(AppInfo.Version, page.UpdateVersionText);
        Assert.Equal("Not checked yet", page.UpdateLastText);
        Assert.Equal("none", page.UpdateCandidateText);
        Assert.False(page.UpdateCheckNowButton.Enabled);
        Assert.False(page.UpdateNotesButton.Enabled);
        Assert.Null(page.NotesUrl);
        Assert.Equal(Strings.Get("gui.update.state.ask"), page.UpdateStatusText);
        Assert.Equal(CellTone.Dim, page.UpdateStatusTone);
        Assert.Equal("Writable", page.UpdatePlacementText);

        // Every button that is off says why, right beside it.
        Assert.Equal("Choose a mode above to use this", page.UpdateCheckNowReasonText);
        Assert.Equal("No newer version found, so there is nothing to open", page.UpdateNotesReasonText);

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
        Assert.Equal(string.Empty, page.UpdateCheckNowReasonText);
        rig.Fake.Answer = (_, _) => rig.Fake.Candidate(1);

        page.UpdateCheckNowButton.PerformClick();
        Finish(rig.Controller.Pending);

        Assert.Equal(1, rig.Fake.Calls);
        Assert.Equal("0.5.0", page.UpdateCandidateText);
        Assert.Equal(UpdateEndpoints.ReleasePageUrl("v0.5.0"), page.NotesUrl);
        Assert.True(page.UpdateNotesButton.Enabled);
        Assert.Equal(string.Empty, page.UpdateNotesReasonText);
        Assert.Equal("Open the update notes in browser", page.UpdateNotesButton.Text);
        Assert.Equal(Say("gui.update.state.candidate", "version", "0.5.0"), page.UpdateStatusText);
        Assert.Equal(CellTone.Normal, page.UpdateStatusTone);
        var checkedAt = rig.Controller.Status().LastCheckUtc;
        Assert.NotNull(checkedAt);
        Assert.Equal(UpdateText.When(checkedAt.Value, rig.Controller.Now), page.UpdateLastText);
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
        Assert.Equal("A check is running", page.UpdateCheckNowReasonText);
        WaitUntil(() => gated.Calls == 1);

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
    public void A_refusal_is_shown_with_its_version_and_reason_in_both_languages_and_offers_no_notes()
    {
        using var rig = new PageRig("notify");
        rig.Controller.Start();
        var page = rig.Page;
        var version = rig.Fake.Candidate(1).Version;
        rig.Fake.Answer = (_, _) => UpdateCheck.Refuse(UpdateRefusal.AssetMissing, "SHA256.txt", fetched: true, version);

        page.UpdateCheckNowButton.PerformClick();
        Finish(rig.Controller.Pending);

        Assert.Equal(
            "Version 0.5.0 could not be verified and will not be used: A required file is missing from the update.",
            page.UpdateStatusText);
        Assert.Equal(CellTone.Warning, page.UpdateStatusTone);
        Assert.False(page.UpdateNotesButton.Enabled);
        // The line under the notes button says what happened to THIS version, not that there is none (a version was found; it could not be verified).
        Assert.Equal("This version could not be verified, so its notes cannot be opened.", page.UpdateNotesReasonText);
        Assert.Equal("none", page.UpdateCandidateText);
        Assert.Equal("Available version", page.UpdateCandidateLabelText);

        Strings.Current = Language.Ja;
        page.ApplyLanguage();

        Assert.Equal("版 0.5.0 は確かめられなかったため使いません: 公開された更新に必要なファイルがありません。", page.UpdateStatusText);
        Assert.Equal("利用できる新しい版", page.UpdateCandidateLabelText);
        Assert.Equal(new[] { "自動", "知らせるだけ（おすすめ）", "切る", "あとで選ぶ（確認しません）" }, page.UpdateModeBox.Items.Cast<string>().ToArray());
        Assert.Equal(1, page.UpdateModeBox.SelectedIndex);
        Assert.Equal("今すぐ確認", page.UpdateCheckNowButton.Text);
        Assert.Equal("更新内容をブラウザーで開く", page.UpdateNotesButton.Text);
        Assert.Equal("この版は確かめられなかったため開けません", page.UpdateNotesReasonText);
    }

    [Fact]
    public void A_check_that_did_not_finish_is_not_a_refusal_of_a_version_so_its_notes_button_says_there_is_nothing_to_open()
    {
        using var rig = new PageRig("notify");
        rig.Controller.Start();
        var page = rig.Page;
        rig.Fake.Answer = (_, _) => UpdateCheck.Refuse(UpdateRefusal.NetworkFailure, "no answer", fetched: true);

        page.UpdateCheckNowButton.PerformClick();
        Finish(rig.Controller.Pending);

        Assert.False(page.UpdateNotesButton.Enabled);
        Assert.Equal("No newer version found, so there is nothing to open", page.UpdateNotesReasonText);
    }

    [Fact]
    public void A_wait_is_shown_with_the_time_until_which_nothing_is_asked_and_Check_now_is_off_with_that_time_beside_it()
    {
        using var rig = new PageRig("notify");
        rig.Controller.Start();
        var page = rig.Page;
        var until = DateTimeOffset.UtcNow.AddDays(3); // far from now on any clock: no race with the test's own speed
        rig.Fake.Answer = (_, _) => UpdateCheck.Wait(until, fetched: true);

        page.UpdateCheckNowButton.PerformClick();
        Finish(rig.Controller.Pending);

        var time = UpdateText.When(until, rig.Controller.Now);
        Assert.Equal(Say("gui.update.state.waiting", "time", time), page.UpdateStatusText);
        Assert.Equal(CellTone.Dim, page.UpdateStatusTone);
        Assert.Contains(time, page.UpdateStatusText);
        Assert.False(page.UpdateCheckNowButton.Enabled);
        Assert.Equal(Say("gui.update.reason.backoff", "time", time), page.UpdateCheckNowReasonText);
        Assert.Equal("Checks resume " + time, page.UpdateCheckNowReasonText);
    }

    [Fact]
    public void No_text_of_the_update_screens_or_the_start_gate_says_release_in_either_language()
    {
        // The person has not heard of a "release" (GitHub's word): the texts say the update, the version or the downloaded ZIP file. A new text that brings the word back fails here.
        var shown = GuiStrings.All
            .Where(entry => entry.Key.StartsWith("gui.update.", StringComparison.Ordinal) || entry.Key.StartsWith("gui.gate.", StringComparison.Ordinal))
            .ToArray();

        Assert.Contains(shown, entry => entry.Key == "gui.update.refusal.assetmissing");
        Assert.Contains(shown, entry => entry.Key == "gui.gate.missinggate.hint");
        var offenders = shown
            .Where(entry => entry.Ja.Contains("リリース", StringComparison.Ordinal) || entry.En.Contains("release", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .ToArray();
        Assert.Empty(offenders);

        // The one sentence per outcome that the console programs write to stderr says the same.
        foreach (var outcome in Enum.GetValues<PcMieru.Wire.Gate.GateOutcome>())
        {
            Assert.DoesNotContain("release", PcMieru.Wire.Gate.InstallGate.ReasonOf(outcome), StringComparison.OrdinalIgnoreCase);
        }

        // The text of the gate FILE (what a person who opens PCMieru.gate.txt reads, in both languages) says the same: the ZIP file, never a release. It is frozen (the same bytes in every
        // release), so a word that slipped in cannot be taken back by a later version.
        Assert.DoesNotContain("release", PcMieru.Wire.Gate.GateFile.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("リリース", PcMieru.Wire.Gate.GateFile.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Language.En, "Checks resume Nov 25, 09:00")]
    [InlineData(Language.Ja, "11月25日 9:00 まで確認できません")]
    public void The_line_under_Check_now_for_a_wait_is_short_and_has_the_time_in_the_language_of_the_screen(Language language, string expected)
    {
        using var _ = Harness.Locale(language);
        var until = new DateTimeOffset(new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Local));
        var now = new DateTimeOffset(new DateTime(2026, 11, 22, 9, 0, 0, DateTimeKind.Local));
        var status = UpdateStatus.Empty with { BackoffUntilUtc = until };

        var line = UpdateText.CheckNowReason(UpdateMode.Notify, allowed: true, checking: false, last: null, status, now);

        Assert.Equal(expected, line);
        Assert.DoesNotContain("{", line, StringComparison.Ordinal);
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
        WaitUntil(() => gated.Calls == 1);

        rig.Page.UpdateModeBox.SelectedIndex = 2;

        Assert.True(gated.Seen.IsCancellationRequested);
        Assert.False(rig.Controller.Checking);
        Assert.Equal(Strings.Get("gui.update.state.off"), rig.Page.UpdateStatusText);
        Assert.False(rig.Page.UpdateCheckNowButton.Enabled);
        Assert.Equal("none", rig.Page.UpdateCandidateText);
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
        Assert.Equal(CellTone.Warning, rig.Page.UpdateStatusTone);
        Assert.Equal("Not available: the setting could not be saved", rig.Page.UpdateCheckNowReasonText);
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

        rig.Activate();

        // The sentence is about the folder, never about the mode the person chose (it stays Notify only): a folder that cannot be written says so, the others say the app cannot update itself.
        var reason = Strings.Get("gui.update.placement." + kind.ToString().ToLowerInvariant());
        var expected = kind == PlacementKind.ReadOnly
            ? "This folder cannot be written, so the app cannot update itself (" + reason + ")"
            : "The app cannot update itself in this folder (" + reason + ")";
        Assert.Equal(expected, rig.Page.UpdatePlacementText);
        Assert.Equal(CellTone.Warning, rig.Page.UpdatePlacementTone);
        Assert.True(rig.Page.UpdateCheckNowButton.Enabled); // a read-only folder still hears about new versions
        Assert.Equal(1, rig.Page.UpdateModeBox.SelectedIndex);
        Assert.DoesNotContain("announce", rig.Page.UpdatePlacementText, StringComparison.OrdinalIgnoreCase);

        Strings.Current = Language.Ja;
        rig.Page.ApplyLanguage();
        Assert.StartsWith(kind == PlacementKind.ReadOnly ? "この場所には書き込めないため、自動で入れ替えることはできません（" : "この場所では、自動で入れ替えることはできません（", rig.Page.UpdatePlacementText, StringComparison.Ordinal);
        Assert.EndsWith(Strings.Get("gui.update.placement." + kind.ToString().ToLowerInvariant()) + "）", rig.Page.UpdatePlacementText, StringComparison.Ordinal);
        Assert.DoesNotContain("お知らせだけ", rig.Page.UpdatePlacementText, StringComparison.Ordinal);
        Assert.Equal(1, rig.Page.UpdateModeBox.SelectedIndex);
    }

    /// <summary>Takes the right to create files and folders from the current user on a folder (a read-only manual unzip, as far as this process can tell); give the rights back on dispose.</summary>
    private sealed class DeniedWrite : IDisposable
    {
        private readonly DirectoryInfo _folder;
        private readonly System.Security.AccessControl.FileSystemAccessRule _rule;

        public DeniedWrite(string folder)
        {
            _folder = new DirectoryInfo(folder);
            _rule = new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.CreateDirectories,
                System.Security.AccessControl.AccessControlType.Deny);
            var security = _folder.GetAccessControl();
            security.AddAccessRule(_rule);
            _folder.SetAccessControl(security);
        }

        public void Dispose()
        {
            var security = _folder.GetAccessControl();
            security.RemoveAccessRule(_rule);
            _folder.SetAccessControl(security);
        }
    }

    [Fact]
    public void A_read_only_manual_unzip_still_has_its_gate_and_the_page_says_updates_are_only_announced_with_the_reason()
    {
        using var root = new Ui.TempRoot();
        var folder = Path.Combine(root.Path, "unzip");
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, "PCMieru.exe");
        File.WriteAllText(exe, "not a program");
        var gate = Path.Combine(folder, PcMieru.Wire.Gate.GateFile.FileName);
        File.WriteAllBytes(gate, PcMieru.Wire.Gate.GateFile.Bytes());
        File.SetAttributes(exe, FileAttributes.ReadOnly);
        File.SetAttributes(gate, FileAttributes.ReadOnly);
        try
        {
            using var denied = new DeniedWrite(folder);

            // The gate needs reading only: a folder nobody can write to starts normally ...
            using (var entry = PcMieru.Wire.Gate.InstallGate.EnterStartup(exe))
            {
                Assert.True(entry.IsOpen, entry.Detail);
            }

            // ... and the page says why nothing will be put in place here, and still lets the person look for a new version.
            Assert.Equal(PlacementKind.ReadOnly, InstallPlacement.Check(folder).Kind);
            using var rig = new PageRig("notify", probe: () => InstallPlacement.Check(folder));
            rig.Controller.Start();
            rig.Activate();

            var reason = Strings.Get("gui.update.placement.readonly");
            Assert.Equal("This folder cannot be written, so the app cannot update itself (" + reason + ")", rig.Page.UpdatePlacementText);
            Assert.Equal(CellTone.Warning, rig.Page.UpdatePlacementTone);
            Assert.True(rig.Page.UpdateCheckNowButton.Enabled);
        }
        finally
        {
            File.SetAttributes(exe, FileAttributes.Normal);
            File.SetAttributes(gate, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_writable_install_says_writable_and_the_probe_is_made_on_activation_and_on_a_mode_change_only_and_remembered()
    {
        using var rig = new PageRig("notify");
        Assert.Equal("Writable", rig.Page.UpdatePlacementText);
        Assert.Equal(1, rig.Probes); // the activation in the rig's constructor

        // Remembered: the folder became read-only, but nothing asked again, so the page does not know yet.
        rig.Fake.Placement = new Placement(PlacementKind.ReadOnly, "test");
        Assert.Equal("Writable", rig.Page.UpdatePlacementText);
        rig.Page.UpdateModeBox.SelectedIndex = 0; // another mode: the probe is made again
        WaitUntil(() => rig.Probes == 2 && rig.Page.UpdatePlacementText.StartsWith("This folder cannot be written", StringComparison.Ordinal));

        rig.Fake.Placement = new Placement(PlacementKind.CanSwap, "test");
        rig.Page.OnDeactivated();
        rig.Activate();
        WaitUntil(() => rig.Probes == 3 && rig.Page.UpdatePlacementText == "Writable"); // the probe runs on a worker thread
        Assert.Equal(3, rig.Probes);
        Assert.Equal("Writable", rig.Page.UpdatePlacementText);

        // Refreshing the page for any other reason (a check, a tick) never probes.
        rig.Page.UpdateCheckNowButton.PerformClick();
        Finish(rig.Controller.Pending);
        Assert.Equal(3, rig.Probes);
    }

    [Fact]
    public void The_write_probe_runs_off_the_UI_thread()
    {
        using var rig = new PageRig("notify");

        Assert.NotEqual(0, rig.ProbeThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, rig.ProbeThread);
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
    public void The_notice_follows_the_candidate_on_file_it_stays_through_a_failure_that_judged_no_release_and_goes_when_a_check_clears_it()
    {
        using var rig = new PageRig("notify");
        rig.Controller.Start();
        var previous = Dialogs.AutoAnswer;
        Dialogs.Log.Clear();
        Dialogs.AutoAnswer = false; // a dialog would be logged here (and never wait for a click)
        try
        {
            Assert.Null(rig.Context.UpdateNotice);

            rig.Fake.Answer = (_, _) => rig.Fake.Candidate(1);
            Finish(rig.Controller.CheckNowAsync());
            Assert.Equal(Say("gui.update.notice", "version", "0.5.0"), rig.Context.UpdateNotice);

            // The network is down on the next check: nothing was learned about the release, so the notice stays.
            rig.Fake.Answer = (_, _) => UpdateCheck.Refuse(UpdateRefusal.NetworkFailure, "down", fetched: true);
            Finish(rig.Controller.CheckNowAsync());
            Assert.Equal(Say("gui.update.notice", "version", "0.5.0"), rig.Context.UpdateNotice);

            // A newer one: the notice names it.
            rig.Fake.Answer = (_, _) => rig.Fake.Candidate(2);
            Finish(rig.Controller.CheckNowAsync());
            Assert.Equal(Say("gui.update.notice", "version", "0.6.0"), rig.Context.UpdateNotice);

            // The feed now says nothing is newer: the candidate is cleared, so is the notice.
            rig.Fake.Answer = (_, _) => UpdateCheck.NoCandidate(fetched: true);
            Finish(rig.Controller.CheckNowAsync());
            Assert.Null(rig.Context.UpdateNotice);

            // A release that broke its own rules clears it as well.
            rig.Fake.Answer = (_, _) => rig.Fake.Candidate(1);
            Finish(rig.Controller.CheckNowAsync());
            Assert.NotNull(rig.Context.UpdateNotice);
            rig.Fake.Answer = (_, _) => UpdateCheck.Refuse(UpdateRefusal.DigestMismatch, "x", fetched: true, version: new AppVersion(0, 5, 0));
            Finish(rig.Controller.CheckNowAsync());
            Assert.Null(rig.Context.UpdateNotice);

            // Never a dialog, never a flash on the status line.
            Assert.Empty(Dialogs.Log);
            Assert.Empty(rig.Messages);
        }
        finally
        {
            Dialogs.AutoAnswer = previous;
        }
    }

    [Fact]
    public void The_notice_goes_with_the_mode_when_the_owner_turns_the_check_off_or_back_to_ask()
    {
        using var rig = new PageRig("notify");
        rig.Fake.SetStatus(UpdateStatus.Empty with { CandidateVersion = new AppVersion(0, 5, 0) });
        rig.Controller.Start();
        Assert.NotNull(rig.Context.UpdateNotice);

        Assert.True(rig.Controller.ChooseMode(UpdateMode.Off));
        Assert.Null(rig.Context.UpdateNotice);

        Assert.True(rig.Controller.ChooseMode(UpdateMode.Notify));
        Assert.NotNull(rig.Context.UpdateNotice); // the candidate is still on file, and the mode asks again

        rig.Page.UpdateModeBox.SelectedIndex = 3; // Ask
        Assert.Null(rig.Context.UpdateNotice);
    }

    [Fact]
    public void A_candidate_that_an_earlier_run_left_is_on_the_status_bar_at_the_start_with_no_request()
    {
        var before = Traffic();
        using var rig = new PageRig("notify");
        rig.Fake.SetStatus(UpdateStatus.Empty with { CandidateVersion = new AppVersion(0, 5, 0), CandidatePageUrl = UpdateEndpoints.ReleasePageUrl("v0.5.0") });

        rig.Controller.Start();

        Assert.Equal(Say("gui.update.notice", "version", "0.5.0"), rig.Context.UpdateNotice);
        Assert.Empty(rig.Messages);
        Assert.Equal(0, rig.Fake.Calls);
        Assert.Equal(before, Traffic());
    }

    [Fact]
    public void In_Ask_and_Off_a_stored_candidate_has_no_notice()
    {
        foreach (var word in new string?[] { null, "off" })
        {
            using var rig = new PageRig(word);
            rig.Fake.SetStatus(UpdateStatus.Empty with { CandidateVersion = new AppVersion(0, 5, 0) });

            rig.Controller.Start();

            Assert.Null(rig.Context.UpdateNotice);
            Assert.Empty(rig.Messages);
        }
    }

    [Theory]
    [InlineData(Language.En, "Version 0.5.0 is available")]
    [InlineData(Language.Ja, "新しい版 0.5.0 があります")]
    public void The_notice_is_written_in_the_language_shown_not_the_one_of_the_moment_it_was_first_seen(Language language, string expectedStart)
    {
        Ui.Run(_ =>
        {
            using var locale = Harness.Locale(Language.En);
            using var theme = new ThemeService(ThemeMode.Light);
            using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            using var pump = new SnapshotPump(services);
            var fake = new FakeUpdateService(UpdateKit.Running);
            fake.SetStatus(UpdateStatus.Empty with { CandidateVersion = new AppVersion(0, 5, 0) });
            var settings = UpdateKit.Impatient(UpdateKit.Settings(mode: "notify"));
            settings.Language = Language.En;
            using var controller = new UpdateController(fake, settings, new ManualTimers(), () => fake.Placement, UpdateKit.Running);
            var context = new GuiContext(services, pump, theme, settings, updates: controller);
            using var form = new MainForm(context);
            Assert.NotEqual(IntPtr.Zero, form.Handle);

            // The notice is first seen with English on screen, then the window is switched to the other language: the label says it in that one.
            controller.Start();
            context.SetLanguage(language);

            var label = Descendants<Label>(form).Single(l => l.Name == "updateNotice");
            // (The form is built but never shown here, so Control.Visible is false for every child; the text is what the person would read: empty while hidden.)
            Assert.NotEqual(string.Empty, label.Text);
            Assert.StartsWith(expectedStart, label.Text, StringComparison.Ordinal);
            if (language == Language.Ja)
            {
                Assert.DoesNotContain("is available", label.Text, StringComparison.Ordinal);
            }
        });
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

        var text = Strings.Get("gui.settings.privacy");
        Assert.Contains("GitHub", text);
        Assert.Contains("SHA256.txt", text);
        Assert.Contains(language == Language.Ja ? "IP アドレス" : "IP address", text);
        Assert.Contains(language == Language.Ja ? "1 日に 1 回ほど" : "about once a day", text);
    }

    [Fact]
    public void The_english_privacy_text_reads_as_plain_english()
    {
        using var _ = Harness.Locale(Language.En);

        var text = Strings.Get("gui.settings.privacy");
        Assert.DoesNotContain("The question carries", text);
        Assert.DoesNotContain("put in place", text);
        Assert.DoesNotContain("sends the information it collects nowhere", text);
        Assert.StartsWith("Nothing this app collects is sent anywhere.", text);
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
