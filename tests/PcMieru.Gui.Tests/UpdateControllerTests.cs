using PcMieru.Update.Feed;

namespace PcMieru.Gui.Tests;

/// <summary>
/// What the window's update check does and, above all, what it does not do (DESIGN-B 4): with no choice (Ask), with "off", with a choice that could not be saved, in a
/// smoke run and while nothing is due, no timer is made, the service is not called and no HTTP client exists. The counters of the feed's HTTP file
/// (<see cref="FeedTraffic"/>) are process-wide; this assembly does not run tests in parallel, so a before/after difference of zero means this test made no request.
/// </summary>
public sealed class UpdateControllerTests
{
    private sealed class Rig
    {
        public Rig(string? mode = null, string? iniPath = null, IUpdateService? service = null)
        {
            Settings = UpdateKit.Impatient(UpdateKit.Settings(iniPath, mode));
            Controller = new UpdateController(service ?? Fake, Settings, Timers, () => Fake.Placement, UpdateKit.Running);
            Controller.CandidateFound += (version, page) => Found.Add((version, page));
            Controller.Failed += Failures.Add;
            Controller.Changed += () => Changes++;
        }

        public ManualTimers Timers { get; } = new();

        public FakeUpdateService Fake { get; } = new(UpdateKit.Running);

        public GuiSettings Settings { get; }

        public UpdateController Controller { get; }

        public List<(AppVersion Version, string? Page)> Found { get; } = [];

        public List<Exception> Failures { get; } = [];

        public int Changes { get; private set; }
    }

    private static (long Requests, long Clients) Traffic() => (FeedTraffic.Requests, FeedTraffic.ClientsCreated);

    // ---- Ask and Off: nothing runs ----

    [Theory]
    [InlineData(null)]
    [InlineData("off")]
    public async Task In_Ask_and_Off_no_timer_is_made_and_nobody_is_asked(string? mode)
    {
        var before = Traffic();
        var rig = new Rig(mode);

        rig.Controller.Start();
        Assert.Empty(rig.Timers.Made);
        Assert.False(rig.Controller.Allowed);
        Assert.False(rig.Controller.TimersRunning);

        Assert.Null(await rig.Controller.TickAsync());
        Assert.Null(await rig.Controller.CheckNowAsync());
        Assert.Equal(0, rig.Fake.Calls);
        Assert.Null(rig.Controller.Last);
        Assert.False(rig.Controller.Checking);
        Assert.Empty(rig.Found);
        Assert.Equal(before, Traffic());
    }

    [Fact]
    public void A_choice_of_Ask_is_the_state_of_a_first_run_and_starting_before_a_choice_makes_nothing()
    {
        var rig = new Rig();

        Assert.Equal(UpdateMode.Ask, rig.Controller.Mode);
        rig.Controller.Start();
        Assert.Empty(rig.Timers.Made);

        // The choice comes later: now the timers are made, once.
        Assert.True(rig.Controller.ChooseMode(UpdateMode.Notify));
        Assert.Equal(1, rig.Timers.OnceMade);
        Assert.Equal(1, rig.Timers.EveryMade);
    }

    [Fact]
    public void A_choice_made_before_the_window_is_started_makes_no_timer_until_the_window_starts()
    {
        var rig = new Rig();

        Assert.True(rig.Controller.ChooseMode(UpdateMode.Notify));
        Assert.Empty(rig.Timers.Made);

        rig.Controller.Start();
        Assert.Equal(2, rig.Timers.Made.Count);
    }

    // ---- Notify and Automatic ----

    [Theory]
    [InlineData("notify", UpdateMode.Notify)]
    [InlineData("auto", UpdateMode.Automatic)]
    public async Task Notify_and_Automatic_make_one_first_timer_and_one_hourly_timer_and_ask_only_when_they_fire(string word, UpdateMode expected)
    {
        var rig = new Rig(word);
        var asked = new List<(UpdateMode Mode, bool Manual)>();
        rig.Fake.Answer = (mode, manual) =>
        {
            asked.Add((mode, manual));
            return UpdateCheck.NoCandidate(fetched: true);
        };

        rig.Controller.Start();
        Assert.True(rig.Controller.Allowed);
        Assert.Equal(1, rig.Timers.OnceMade);
        Assert.Equal(1, rig.Timers.EveryMade);
        Assert.Equal(TimeSpan.FromSeconds(60), rig.Timers.Once().Interval);
        Assert.Equal(TimeSpan.FromHours(1), rig.Timers.Poll().Interval);
        Assert.Equal(UpdateSchedule.FirstDelay, rig.Timers.Once().Interval);
        Assert.Equal(UpdateSchedule.Poll, rig.Timers.Poll().Interval);
        Assert.Empty(asked); // nothing is asked until a timer fires

        rig.Timers.Once().Fire();
        await rig.Controller.Pending;
        Assert.Single(asked);
        Assert.Equal((expected, false), asked[0]);

        rig.Timers.Poll().Fire();
        await rig.Controller.Pending;
        rig.Timers.Poll().Fire();
        await rig.Controller.Pending;
        Assert.Equal(3, asked.Count);
        Assert.All(asked, a => Assert.False(a.Manual)); // a timer is never a manual check

        await rig.Controller.CheckNowAsync();
        Assert.Equal((expected, true), asked[^1]);
    }

    [Fact]
    public void Starting_twice_does_not_double_the_timers()
    {
        var rig = new Rig("notify");

        rig.Controller.Start();
        rig.Controller.Start();

        Assert.Equal(2, rig.Timers.Made.Count);
        Assert.Equal(2, rig.Timers.Live);
    }

    [Fact]
    public async Task Choosing_Off_drops_the_timers_and_a_timer_that_still_fires_does_nothing()
    {
        var rig = new Rig("notify");
        rig.Controller.Start();
        var first = rig.Timers.Once();
        var poll = rig.Timers.Poll();

        Assert.True(rig.Controller.ChooseMode(UpdateMode.Off));

        Assert.Equal(UpdateMode.Off, rig.Controller.Mode);
        Assert.Equal(0, rig.Timers.Live);
        Assert.True(first.Disposed);
        Assert.True(poll.Disposed);
        Assert.False(rig.Controller.TimersRunning);
        first.Fire();
        poll.Fire();
        await rig.Controller.Pending;
        Assert.Equal(0, rig.Fake.Calls);
        Assert.True(rig.Changes > 0);
    }

    [Fact]
    public void The_choice_is_in_the_file_before_the_timer_is_made()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        var rig = new Rig(iniPath: ini);
        var seenInTheFile = new List<string>();
        rig.Timers.OnMade = _ => seenInTheFile.Add(File.Exists(ini) ? File.ReadAllText(ini) : "(no file)");
        rig.Controller.Start();

        Assert.True(rig.Controller.ChooseMode(UpdateMode.Notify));

        Assert.Equal(2, seenInTheFile.Count);
        Assert.All(seenInTheFile, text => Assert.Contains("update.mode=notify", text));
    }

    [Fact]
    public void A_choice_that_was_made_in_an_earlier_run_starts_the_timers_without_a_dialog()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        File.WriteAllText(ini, "update.mode=notify\n");
        var rig = new Rig(iniPath: ini);
        rig.Settings.Apply("update.mode=notify"); // as GuiSettings.Load would have read it
        Assert.Equal(UpdateMode.Notify, rig.Controller.Mode);

        rig.Controller.Start();

        Assert.Equal(2, rig.Timers.Made.Count);
    }

    // ---- a choice that could not be written ----

    [Fact]
    public async Task A_choice_that_could_not_be_saved_is_not_a_choice_and_nothing_runs()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        var before = Traffic();
        var rig = new Rig(iniPath: ini);
        rig.Controller.Start();

        using (new HeldLock(ini))
        {
            Assert.False(rig.Controller.ChooseMode(UpdateMode.Notify));
            Assert.Equal(UpdateMode.Ask, rig.Controller.Mode);
            Assert.False(rig.Controller.Allowed);
            Assert.Empty(rig.Timers.Made);
            Assert.Null(await rig.Controller.CheckNowAsync());
            Assert.Null(await rig.Controller.TickAsync());
        }

        Assert.Equal(0, rig.Fake.Calls);
        Assert.Equal(before, Traffic());
        Assert.False(File.Exists(ini));

        // When the other writer is gone the same choice is saved and the timers appear.
        Assert.True(rig.Controller.ChooseMode(UpdateMode.Notify));
        Assert.Equal(2, rig.Timers.Made.Count);
    }

    [Fact]
    public async Task An_Off_that_could_not_be_saved_still_stops_the_network_for_this_run()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        Assert.True(new GuiSettings(ini).TrySaveUpdateMode(UpdateMode.Notify));
        var rig = new Rig(iniPath: ini);
        rig.Settings.Apply("update.mode=notify");
        rig.Controller.Start();
        Assert.Equal(2, rig.Timers.Live);

        using (new HeldLock(ini))
        {
            Assert.False(rig.Controller.ChooseMode(UpdateMode.Off));
        }

        Assert.Equal(UpdateMode.Notify, rig.Controller.Mode); // the file still says notify ...
        Assert.False(rig.Controller.Allowed);                  // ... and still nothing goes out
        Assert.Equal(0, rig.Timers.Live);
        Assert.Null(await rig.Controller.CheckNowAsync());
        Assert.Equal(0, rig.Fake.Calls);

        // A later choice that is saved lifts it.
        Assert.True(rig.Controller.ChooseMode(UpdateMode.Notify));
        Assert.True(rig.Controller.Allowed);
        Assert.Equal(2, rig.Timers.Live);
    }

    [Fact]
    public void A_change_between_two_allowed_modes_that_could_not_be_saved_leaves_the_checks_running_as_they_were()
    {
        using var root = new Ui.TempRoot();
        var ini = Path.Combine(root.Path, "gui.ini");
        Assert.True(new GuiSettings(ini).TrySaveUpdateMode(UpdateMode.Notify));
        var rig = new Rig(iniPath: ini);
        rig.Settings.Apply("update.mode=notify");
        rig.Controller.Start();

        using (new HeldLock(ini))
        {
            Assert.False(rig.Controller.ChooseMode(UpdateMode.Automatic));
        }

        Assert.Equal(UpdateMode.Notify, rig.Controller.Mode);
        Assert.True(rig.Controller.Allowed);
        Assert.Equal(2, rig.Timers.Live);
    }

    // ---- a check that is running ----

    [Fact]
    public async Task Choosing_Off_cancels_a_check_that_is_running_and_shows_nothing_of_it()
    {
        var gate = new GatedUpdateService();
        var rig = new Rig("notify", service: gate);
        rig.Controller.Start();

        var check = rig.Controller.CheckNowAsync();
        Assert.True(rig.Controller.Checking);
        Assert.Equal(1, gate.Calls);
        Assert.False(gate.Seen.IsCancellationRequested);

        Assert.True(rig.Controller.ChooseMode(UpdateMode.Off));

        Assert.True(gate.Seen.IsCancellationRequested);
        Assert.False(rig.Controller.Checking);
        Assert.Null(await check);
        Assert.Null(rig.Controller.Last);
        Assert.Empty(rig.Found);
    }

    [Fact]
    public async Task A_slow_answer_that_comes_after_Off_is_not_shown_and_not_announced()
    {
        var gate = new GatedUpdateService { ObeyCancel = false };
        var rig = new Rig("notify", service: gate);
        rig.Controller.Start();
        var check = rig.Controller.CheckNowAsync();

        Assert.True(rig.Controller.ChooseMode(UpdateMode.Off));
        gate.Release(rig.Fake.Candidate());

        Assert.Null(await check);
        Assert.Null(rig.Controller.Last);
        Assert.Empty(rig.Found);
        Assert.False(rig.Controller.Checking);
    }

    [Fact]
    public async Task Only_one_check_runs_at_a_time()
    {
        var gate = new GatedUpdateService();
        var rig = new Rig("notify", service: gate);
        rig.Controller.Start();

        var first = rig.Controller.CheckNowAsync();
        Assert.Null(await rig.Controller.CheckNowAsync());
        Assert.Null(await rig.Controller.TickAsync());
        rig.Timers.Poll().Fire();
        await rig.Controller.Pending;
        Assert.Equal(1, gate.Calls);

        gate.Release(UpdateCheck.NoCandidate(fetched: true));
        Assert.NotNull(await first);
        Assert.False(rig.Controller.Checking);
    }

    [Fact]
    public async Task A_check_that_throws_in_a_timer_is_reported_and_never_leaves_the_timer()
    {
        var rig = new Rig("notify");
        rig.Fake.Answer = (_, _) => throw new InvalidOperationException("the service broke");
        rig.Controller.Start();

        rig.Timers.Once().Fire();
        await rig.Controller.Pending;

        var failure = Assert.Single(rig.Failures);
        Assert.Equal("the service broke", failure.Message);
        Assert.False(rig.Controller.Checking); // and the next tick is free to try again

        rig.Fake.Answer = (_, _) => UpdateCheck.NoCandidate(fetched: true);
        rig.Timers.Poll().Fire();
        await rig.Controller.Pending;
        Assert.Single(rig.Failures);
        Assert.Equal(UpdateCheckKind.NoCandidate, rig.Controller.Last?.Kind);
    }

    // ---- what a check found ----

    [Fact]
    public async Task A_candidate_is_announced_once_per_version()
    {
        var rig = new Rig("notify");
        rig.Controller.Start();
        rig.Fake.Answer = (_, _) => rig.Fake.Candidate(minorsAhead: 1);

        await rig.Controller.CheckNowAsync();
        await rig.Controller.CheckNowAsync();
        await rig.Controller.CheckNowAsync();

        var only = Assert.Single(rig.Found);
        Assert.Equal(new AppVersion(0, 5, 0), only.Version);
        Assert.Equal(UpdateEndpoints.ReleasePageUrl("v0.5.0"), only.Page);
        Assert.Equal(UpdateCheckKind.Candidate, rig.Controller.Last?.Kind);

        rig.Fake.Answer = (_, _) => rig.Fake.Candidate(minorsAhead: 2);
        await rig.Controller.CheckNowAsync();
        Assert.Equal(new[] { new AppVersion(0, 5, 0), new AppVersion(0, 6, 0) }, rig.Found.Select(f => f.Version).ToArray());
    }

    [Fact]
    public async Task The_candidate_of_this_run_is_the_one_the_page_shows_and_a_stored_one_is_shown_only_when_newer()
    {
        var rig = new Rig("notify");
        rig.Controller.Start();

        Assert.Null(rig.Controller.Candidate());

        // A stored candidate that is not newer than the running version (the app was updated by hand since) is not a candidate.
        rig.Fake.SetStatus(UpdateStatus.Empty with { CandidateVersion = UpdateKit.Running, CandidatePageUrl = UpdateEndpoints.ReleasePageUrl("v0.4.0") });
        Assert.Null(rig.Controller.Candidate());
        rig.Fake.SetStatus(UpdateStatus.Empty with { CandidateVersion = new AppVersion(0, 3, 0), CandidatePageUrl = UpdateEndpoints.ReleasePageUrl("v0.3.0") });
        Assert.Null(rig.Controller.Candidate());

        rig.Fake.SetStatus(UpdateStatus.Empty with { CandidateVersion = new AppVersion(0, 5, 0), CandidatePageUrl = UpdateEndpoints.ReleasePageUrl("v0.5.0") });
        var stored = rig.Controller.Candidate();
        Assert.NotNull(stored);
        Assert.Equal(new AppVersion(0, 5, 0), stored.Value.Version);
        Assert.Equal(UpdateEndpoints.ReleasePageUrl("v0.5.0"), stored.Value.PageUrl);

        // What this run found wins over what the file says.
        rig.Fake.Answer = (_, _) => rig.Fake.Candidate(minorsAhead: 2);
        await rig.Controller.CheckNowAsync();
        Assert.Equal(new AppVersion(0, 6, 0), rig.Controller.Candidate()?.Version);
    }

    [Fact]
    public void A_candidate_an_earlier_run_left_is_announced_at_start_from_the_file_and_asks_nobody()
    {
        var before = Traffic();
        var rig = new Rig("notify");
        rig.Fake.SetStatus(UpdateStatus.Empty with { CandidateVersion = new AppVersion(0, 5, 0), CandidatePageUrl = UpdateEndpoints.ReleasePageUrl("v0.5.0") });

        rig.Controller.Start();

        var only = Assert.Single(rig.Found);
        Assert.Equal(new AppVersion(0, 5, 0), only.Version);
        Assert.Equal(0, rig.Fake.Calls);
        Assert.Equal(before, Traffic());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("off")]
    public void In_Ask_and_Off_a_stored_candidate_is_not_announced(string? mode)
    {
        var rig = new Rig(mode);
        rig.Fake.SetStatus(UpdateStatus.Empty with { CandidateVersion = new AppVersion(0, 5, 0), CandidatePageUrl = UpdateEndpoints.ReleasePageUrl("v0.5.0") });

        rig.Controller.Start();

        Assert.Empty(rig.Found);
    }

    [Fact]
    public void A_stored_candidate_that_is_not_newer_than_the_running_version_is_not_announced()
    {
        var rig = new Rig("notify");
        rig.Fake.SetStatus(UpdateStatus.Empty with { CandidateVersion = UpdateKit.Running, CandidatePageUrl = UpdateEndpoints.ReleasePageUrl("v0.4.0") });

        rig.Controller.Start();

        Assert.Empty(rig.Found);
    }

    [Fact]
    public async Task A_check_that_asked_nobody_does_not_wipe_what_the_last_real_check_found()
    {
        var rig = new Rig("notify");
        rig.Controller.Start();
        rig.Fake.Answer = (_, _) => rig.Fake.Candidate();
        await rig.Controller.CheckNowAsync();
        Assert.Equal(UpdateCheckKind.Candidate, rig.Controller.Last?.Kind);

        rig.Fake.Answer = (_, _) => UpdateCheck.NoCandidate(fetched: false); // "not due yet"
        var notDue = await rig.Controller.TickAsync();

        Assert.Equal(UpdateCheckKind.NoCandidate, notDue?.Kind);
        Assert.Equal(UpdateCheckKind.Candidate, rig.Controller.Last?.Kind);

        rig.Fake.Answer = (_, _) => UpdateCheck.NoCandidate(fetched: true); // asked, nothing newer any more
        await rig.Controller.CheckNowAsync();
        Assert.Equal(UpdateCheckKind.NoCandidate, rig.Controller.Last?.Kind);
    }

    [Fact]
    public async Task A_refusal_and_a_wait_are_kept_as_what_the_last_check_said()
    {
        var rig = new Rig("notify");
        rig.Controller.Start();

        rig.Fake.Answer = (_, _) => UpdateCheck.Refuse(UpdateRefusal.AssetMissing, "no manifest", fetched: true);
        await rig.Controller.CheckNowAsync();
        Assert.Equal(UpdateRefusal.AssetMissing, rig.Controller.Last?.Refusal);
        Assert.Empty(rig.Found);

        var until = new DateTimeOffset(2026, 11, 22, 3, 0, 0, TimeSpan.Zero);
        rig.Fake.Answer = (_, _) => UpdateCheck.Wait(until, fetched: true);
        await rig.Controller.CheckNowAsync();
        Assert.Equal(UpdateCheckKind.Waiting, rig.Controller.Last?.Kind);
        Assert.Equal(until, rig.Controller.Last?.WaitUntil);
    }

    [Fact]
    public async Task Disposing_stops_the_timers_and_the_check_and_nothing_is_asked_afterwards()
    {
        var gate = new GatedUpdateService();
        var rig = new Rig("notify", service: gate);
        rig.Controller.Start();
        var check = rig.Controller.CheckNowAsync();

        rig.Controller.Dispose();

        Assert.Equal(0, rig.Timers.Live);
        Assert.True(gate.Seen.IsCancellationRequested);
        Assert.False(rig.Controller.Allowed);
        Assert.Null(await check);
        Assert.Null(await rig.Controller.CheckNowAsync());
        Assert.Equal(1, gate.Calls);
    }

    // ---- the real service behind the controller: still no request in the states that must make none ----

    private sealed class RealRig : IDisposable
    {
        private readonly Ui.TempRoot _root = new();

        public RealRig(string? mode = null, string versionText = "0.4.0", bool withIni = false)
        {
            Local = Path.Combine(_root.Path, "L");
            var appFolder = Path.Combine(_root.Path, "app");
            Directory.CreateDirectory(appFolder);
            Program = Path.Combine(appFolder, "PCMieru.exe");
            File.WriteAllText(Program, "not a program");
            IniPath = withIni ? Path.Combine(Local, "gui.ini") : null;
            Settings = UpdateKit.Impatient(UpdateKit.Settings(IniPath, mode));
            Timers = new ManualTimers();
            Wiring = UpdateComposition.Create(fakes: false, Settings, versionText, Local, Program, Timers);
        }

        public string Local { get; }

        public string Program { get; }

        public string? IniPath { get; }

        public GuiSettings Settings { get; }

        public ManualTimers Timers { get; }

        public UpdateWiring Wiring { get; }

        public UpdateController Controller => Wiring.Controller;

        public bool LocalFolderExists => Directory.Exists(Local);

        public void Dispose()
        {
            Controller.Dispose();
            _root.Dispose();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("off")]
    public async Task The_real_service_in_Ask_and_Off_makes_no_request_no_client_and_no_file(string? mode)
    {
        var before = Traffic();
        using var rig = new RealRig(mode);

        Assert.Null(rig.Wiring.Fake);
        rig.Controller.Start();
        Assert.Equal(UpdateStatus.Empty, rig.Controller.Status());
        Assert.Null(await rig.Controller.TickAsync());
        Assert.Null(await rig.Controller.CheckNowAsync());

        Assert.Empty(rig.Timers.Made);
        Assert.Equal(before, Traffic());
        Assert.False(rig.LocalFolderExists); // not even L\update was made
    }

    [Fact]
    public void The_real_service_in_Notify_does_nothing_until_a_timer_fires()
    {
        var before = Traffic();
        using var rig = new RealRig("notify");

        rig.Controller.Start();
        Assert.Equal(UpdateStatus.Empty, rig.Controller.Status());

        Assert.Equal(2, rig.Timers.Made.Count);
        Assert.Equal(before, Traffic());
        Assert.False(rig.LocalFolderExists);
    }

    [Fact]
    public async Task A_version_that_is_not_three_numbers_gives_a_refusal_and_no_request()
    {
        var before = Traffic();
        using var rig = new RealRig("notify", versionText: "dev-build");

        rig.Controller.Start();
        var answer = await rig.Controller.CheckNowAsync();

        Assert.NotNull(answer);
        Assert.Equal(UpdateCheckKind.Refused, answer.Kind);
        Assert.Equal(UpdateRefusal.OwnVersionUnknown, answer.Refusal);
        Assert.False(answer.Fetched);
        Assert.Equal(before, Traffic());
        Assert.False(rig.LocalFolderExists);
    }

    [Fact]
    public async Task A_choice_that_could_not_be_saved_makes_no_request_with_the_real_service()
    {
        var before = Traffic();
        using var rig = new RealRig(withIni: true);
        rig.Controller.Start();

        using (new HeldLock(rig.IniPath!))
        {
            Assert.False(rig.Controller.ChooseMode(UpdateMode.Notify));
            Assert.Null(await rig.Controller.CheckNowAsync());
            Assert.Null(await rig.Controller.TickAsync());
        }

        Assert.Empty(rig.Timers.Made);
        Assert.Equal(before, Traffic());
    }

    // ---- the services around it ----

    [Fact]
    public async Task The_deferred_service_is_not_made_by_Ask_or_Off_and_only_made_by_something_that_needs_it()
    {
        var made = 0;
        var deferred = new DeferredUpdateService(() =>
        {
            made++;
            return new FakeUpdateService(UpdateKit.Running);
        });

        foreach (var mode in new[] { UpdateMode.Ask, UpdateMode.Off })
        {
            var answer = await deferred.CheckAsync(mode, manual: true, CancellationToken.None);
            Assert.Equal(UpdateCheckKind.NotAllowed, answer.Kind);
            Assert.Equal(mode, answer.Mode);
        }

        Assert.Equal(0, made);

        _ = deferred.ReadStatus();
        Assert.Equal(1, made);
    }

    [Fact]
    public async Task The_service_that_cannot_check_refuses_with_its_reason_in_Notify_and_says_not_allowed_otherwise()
    {
        var service = new UnavailableUpdateService(UpdateRefusal.StateUnavailable, "no id");

        var notify = await service.CheckAsync(UpdateMode.Notify, manual: true, CancellationToken.None);
        Assert.Equal(UpdateCheckKind.Refused, notify.Kind);
        Assert.Equal(UpdateRefusal.StateUnavailable, notify.Refusal);
        Assert.False(notify.Fetched);

        Assert.Equal(UpdateCheckKind.NotAllowed, (await service.CheckAsync(UpdateMode.Ask, manual: true, CancellationToken.None)).Kind);
        Assert.Equal(UpdateStatus.Empty, service.ReadStatus());
    }

    [Fact]
    public async Task The_fake_service_never_answers_in_Ask_or_Off_and_counts_only_what_passed_the_gate()
    {
        var fake = new FakeUpdateService(UpdateKit.Running);

        Assert.Equal(UpdateCheckKind.NotAllowed, (await fake.CheckAsync(UpdateMode.Ask, manual: true, CancellationToken.None)).Kind);
        Assert.Equal(UpdateCheckKind.NotAllowed, (await fake.CheckAsync(UpdateMode.Off, manual: false, CancellationToken.None)).Kind);
        Assert.Equal(0, fake.Calls);

        Assert.Equal(UpdateCheckKind.NoCandidate, (await fake.CheckAsync(UpdateMode.Notify, manual: false, CancellationToken.None)).Kind);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void The_fake_candidate_is_newer_than_the_running_version_and_points_at_this_repos_release_page()
    {
        var fake = new FakeUpdateService(UpdateKit.Running);

        var candidate = fake.Candidate();

        Assert.Equal(UpdateCheckKind.Candidate, candidate.Kind);
        Assert.True(candidate.Version > UpdateKit.Running);
        Assert.True(UpdateEndpoints.IsReleasePage(candidate.PageUrl!));
        Assert.StartsWith("https://github.com/goemon0000/pc-mieru/releases/download/", candidate.ZipUrl!.AbsoluteUri, StringComparison.Ordinal);
    }
}
