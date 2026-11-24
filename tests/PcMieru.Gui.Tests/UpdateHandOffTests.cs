using PcMieru.Update.Feed;
using PcMieru.Update.Launch;
using PcMieru.Update.Transaction;
using RestartManager = PcMieru.Gui.RestartManager;

namespace PcMieru.Gui.Tests;

public sealed class UpdateHandOffTests
{
    private static readonly StagedUpdate Stage = new()
    {
        Folder = "stage", Version = "1.2.3", BuildId = "build", ManifestSha256 = "sha", Tag = "v1.2.3",
    };

    private static readonly UpdateIdleFacts Idle = new(1000, false, false, false, false, RecordingActivity.Idle, false, false);

    [Theory]
    [InlineData("diskFull")]
    [InlineData("filesInUse")]
    [InlineData("swapIoFailure")]
    [InlineData("ioFailure")]
    [InlineData(null)]
    public async Task An_aborted_build_gets_three_attempts_across_restarts_with_one_then_six_hours_between_them(string? reason)
    {
        using var rig = new Attempts();
        await rig.Make(reason).TryAsync();
        Assert.Equal(1, rig.Starts);

        rig.Clock.Utc -= TimeSpan.FromMinutes(1);
        await rig.Make(reason).TryAsync();
        rig.Clock.Utc += TimeSpan.FromMinutes(60);
        await rig.Make(reason).TryAsync();
        Assert.Equal(1, rig.Starts);
        Assert.Equal(UpdateAttemptFailure.Waiting, rig.Decision.Failure);

        rig.Clock.Utc += TimeSpan.FromMinutes(1);
        await rig.Make(reason).TryAsync();
        await rig.Make(reason).TryAsync();
        Assert.Equal(2, rig.Starts);
        rig.Clock.Utc += TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1);
        await rig.Make(reason).TryAsync();
        Assert.Equal(2, rig.Starts);

        rig.Clock.Utc += TimeSpan.FromSeconds(1);
        await rig.Make(reason).TryAsync();
        Assert.Equal(3, rig.Starts);
        rig.Clock.Utc += TimeSpan.FromDays(1);
        await rig.Make(reason).TryAsync();
        Assert.Null(rig.Store.Read().Staged);
        Assert.Equal(UpdateAttemptFailure.Permanent, rig.Decision.Failure);
        Assert.Equal(3, rig.Starts);
        Assert.Equal(3, rig.Closes);
    }

    [Theory]
    [InlineData("nameCollision")]
    [InlineData("installedFilesChanged")]
    [InlineData("notNtfs")]
    [InlineData("noRecord")]
    [InlineData("stagedMismatch")]
    [InlineData("backupIncomplete")]
    [InlineData("unknownReason")]
    public async Task A_permanent_abort_discards_the_stage_and_its_notice_is_acknowledged_once(string reason)
    {
        using var rig = new Attempts();
        await rig.Make(reason).TryAsync();
        await rig.Make(reason).TryAsync();
        Assert.Null(rig.Store.Read().Staged);
        Assert.False(Directory.Exists(rig.Staged.Folder));
        Assert.Equal(UpdateAttemptFailure.Permanent, rig.Decision.Failure);
        rig.Service().MarkHandOffFailureShown(rig.Decision.AttemptId!);

        rig.SetStage(rig.Staged, generation: "another check");
        await rig.Make(reason).TryAsync();
        Assert.Equal(1, rig.Starts);
        Assert.Null(rig.Store.Read().Staged);
        Assert.Null(rig.Decision.Failure);
    }

    [Fact]
    public async Task An_old_result_for_the_same_build_does_not_classify_a_new_attempt()
    {
        using var rig = new Attempts();
        rig.WriteResult("000000000000", "nameCollision");
        await rig.Make().TryAsync();
        await rig.Make().TryAsync();
        Assert.Equal(UpdateAttemptFailure.Waiting, rig.Decision.Failure);
        Assert.Equal(rig.Staged, rig.Store.Read().Staged);
    }

    [Fact]
    public async Task The_committed_version_clears_the_attempt_even_if_the_stage_is_still_recorded()
    {
        using var rig = new Attempts();
        await rig.Make().TryAsync();
        Assert.Single(rig.Store.Read().HandOffAttempts!);

        var committed = rig.Service(new AppVersion(1, 2, 3));
        Assert.Null(committed.InspectHandOff().Failure);
        Assert.Empty(rig.Store.Read().HandOffAttempts!);
        Assert.Null(rig.Store.Read().Staged);
    }

    [Theory]
    [InlineData(ApplyRefusal.StageChanged)]
    [InlineData(ApplyRefusal.NotNewer)]
    [InlineData(ApplyRefusal.NotAutomatic)]
    public async Task A_refusal_is_remembered_until_the_check_or_the_staged_build_changes(ApplyRefusal refusal)
    {
        using var rig = new Attempts();
        var first = rig.Make(refusal: refusal);
        await first.TryAsync();
        await first.TryAsync();
        await rig.Make(refusal: refusal).TryAsync();
        Assert.Equal(1, rig.Starts);

        rig.SetStage(rig.Staged, generation: "second check");
        await rig.Make(refusal: refusal).TryAsync();
        Assert.Equal(2, rig.Starts);

        rig.SetStage(rig.Staged with { ManifestSha256 = new string('b', 64) }, generation: "second check");
        await rig.Make(refusal: refusal).TryAsync();
        Assert.Equal(3, rig.Starts);
        Assert.Equal(0, rig.Closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_blocked_build_is_discarded_and_never_handed_off_again(bool blockedBeforeTheFirstTry)
    {
        using var rig = new Attempts();
        if (blockedBeforeTheFirstTry)
        {
            File.WriteAllText(rig.Paths.Blocked, $$"""
                { "format": "pcm-blocked/1", "tags": [
                    { "version": "{{rig.Staged.Version}}", "buildId": "{{rig.Staged.BuildId}}", "reason": "launch-failed", "utc": "2026-11-25T01:02:03Z" }
                ] }
                """);
            Assert.Null(rig.Service().ReadStatus().CandidateVersion);
        }

        await rig.Make(refusal: ApplyRefusal.Blocked).TryAsync();
        Assert.Null(rig.Store.Read().Staged);
        rig.SetStage(rig.Staged, generation: "another check");
        await rig.Make(refusal: ApplyRefusal.Blocked).TryAsync();
        Assert.Equal(blockedBeforeTheFirstTry ? 0 : 1, rig.Starts);
        Assert.Null(rig.Store.Read().Staged);
    }

    private sealed class AttemptClock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private sealed class Attempts : IDisposable
    {
        private readonly UpdateTestFolder _folder = new();

        public Attempts()
        {
            Store = new UpdateStateStore(Path.Combine(Paths.Install, "state"));
            Staged = Stage with
            {
                Folder = Path.Combine(Store.Folder, "cache", "0123456789ab"),
                BuildId = Guid.NewGuid().ToString("D"),
                ManifestSha256 = new string('a', 64),
            };
            SetStage(Staged, generation: "first check");
        }

        public TxPaths Paths => _folder.Paths;
        public UpdateStateStore Store { get; }
        public StagedUpdate Staged { get; }
        public AttemptClock Clock { get; } = new();
        public int Starts { get; private set; }
        public int Closes { get; private set; }
        public HandOffDecision Decision { get; private set; }

        public UpdateService Service(AppVersion? running = null) => new(new UpdateServiceOptions
        {
            Running = running ?? new AppVersion(1, 2, 2),
            StateFolder = Store.Folder,
            InstallFolder = Paths.Install,
            Clock = Clock,
            TransportFactory = () => throw new InvalidOperationException("the hand-off must not use the network"),
        });

        public void SetStage(StagedUpdate staged, string generation)
        {
            Directory.CreateDirectory(staged.Folder);
            var state = Store.Read();
            state.Staged = staged;
            state.LatestVersion = staged.Version;
            state.CheckGeneration = generation;
            Assert.True(Store.TryWrite(state));
        }

        public void WriteResult(string id, string reason) => File.WriteAllText(Paths.LastResult, $$"""
            { "format": "pcm-last-result/1", "tx": "{{id}}", "outcome": "aborted", "reason": "{{reason}}",
              "to": { "version": "{{Staged.Version}}", "buildId": "{{Staged.BuildId}}" } }
            """);

        public UpdateHandOff Make(string? reason = null, ApplyRefusal? refusal = null)
        {
            var service = Service();
            return new UpdateHandOff(() => (UpdateMode.Automatic, service.ReadStatus().Staged), () => Idle,
                () => GateHolders.Clear, (_, id) =>
                {
                    Assert.Contains(Store.Read().HandOffAttempts!, attempt => attempt.Id == id);
                    Starts++;
                    if (reason is not null)
                    {
                        WriteResult(id!, reason);
                    }

                    return refusal is { } why ? ApplyHandOffResult.No(why, "test") : ApplyHandOffResult.Yes;
                },
                _ => { }, () => Closes++, () => 61_000, service, decision => Decision = decision);
        }

        public void Dispose() => _folder.Dispose();
    }

    [Theory]
    [InlineData("notify")]
    [InlineData("ask")]
    [InlineData("off")]
    [InlineData("no stage")]
    [InlineData("input")]
    [InlineData("unknown input")]
    [InlineData("dialog")]
    [InlineData("menu")]
    [InlineData("drag")]
    [InlineData("check")]
    [InlineData("preparing recording")]
    [InlineData("recording")]
    [InlineData("stopping recording")]
    [InlineData("UAC")]
    [InlineData("elevated")]
    public async Task Blockers_leave_the_window_open_without_querying_or_starting_the_runner(string blocker)
    {
        var mode = blocker switch
        {
            "notify" => UpdateMode.Notify,
            "ask" => UpdateMode.Ask,
            "off" => UpdateMode.Off,
            _ => UpdateMode.Automatic,
        };
        var observed = blocker switch
        {
            "input" => Idle with { LastInput = 1001 },
            "unknown input" => Idle with { LastInput = null },
            "dialog" => Idle with { Dialog = true },
            "menu" => Idle with { Menu = true },
            "drag" => Idle with { Drag = true },
            "check" => Idle with { Check = true },
            "preparing recording" => Idle with { Recording = RecordingActivity.Preparing },
            "recording" => Idle with { Recording = RecordingActivity.Recording },
            "stopping recording" => Idle with { Recording = RecordingActivity.Stopping },
            "UAC" => Idle with { PendingUac = true },
            "elevated" => Idle with { Elevated = true },
            _ => Idle,
        };
        var handOff = new UpdateHandOff(() => (mode, blocker == "no stage" ? null : Stage), () => observed,
            () => throw new InvalidOperationException("queried RM"), _ => throw new InvalidOperationException("started runner"),
            _ => { }, () => Assert.Fail("closed"), () => 61_000);
        await handOff.TryAsync();
    }

    [Theory]
    [InlineData("Idle", "Idle")]
    [InlineData("Starting", "Preparing")]
    [InlineData("Recording", "Recording")]
    [InlineData("Stopping", "Stopping")]
    [InlineData("99", "Recording")]
    public void The_recording_phase_of_the_window_maps_onto_the_hand_off_activity_and_only_idle_is_idle(string phase, string expected)
    {
        // The enums are internal: the theory carries their names (99: a phase the map does not know, which must never read as idle).
        var read = Enum.TryParse<RecordingPhase>(phase, out var named) ? named : (RecordingPhase)int.Parse(phase);
        Assert.Equal(expected, UpdateHandOff.ActivityOf(read).ToString());
    }

    [Fact]
    public void A_real_recording_hub_that_is_starting_recording_or_stopping_keeps_the_hand_off_from_starting_and_an_idle_one_does_not()
    {
        Ui.Run(ui =>
        {
            var kit = new FakeRecordingKit();
            using var hub = kit.CreateHub();
            var started = 0;
            var handOff = new UpdateHandOff(() => (UpdateMode.Automatic, Stage),
                () => Idle with { Recording = UpdateHandOff.ActivityOf(hub.Phase) }, () => GateHolders.Clear,
                _ =>
                {
                    started++;
                    return ApplyHandOffResult.Yes;
                },
                _ => { }, () => { }, () => 61_000);

            // Every state the hub raises while a recording is not idle is tried at the moment it is raised: the hand-off must give up at once, before
            // it asks for the gate holders (a task that is not finished here has gone on to the runner's side).
            var tried = new List<(RecordingPhase Phase, bool Refused)>();
            hub.Changed += () =>
            {
                if (hub.Phase != RecordingPhase.Idle)
                {
                    tried.Add((hub.Phase, handOff.TryAsync().IsCompleted));
                }
            };

            var start = hub.StartAsync();
            Assert.True(ui.Until(() => start.IsCompleted));
            Assert.True(start.GetAwaiter().GetResult());
            Assert.Contains(RecordingPhase.Starting, tried.Select(t => t.Phase));
            Assert.Contains(RecordingPhase.Recording, tried.Select(t => t.Phase));

            kit.Backend.HangStop = true;
            var stop = hub.StopAsync();
            Assert.Equal(RecordingPhase.Stopping, hub.Phase);
            Assert.Contains(RecordingPhase.Stopping, tried.Select(t => t.Phase));
            kit.Backend.ReleaseStop();
            Assert.True(ui.Until(() => stop.IsCompleted));

            Assert.All(tried, t => Assert.True(t.Refused, $"the hand-off went on while the recording was {t.Phase}"));
            Assert.Equal(0, started);

            // The recording is over: nothing is in the way any more, and the same hand-off now goes through.
            Assert.Equal(RecordingPhase.Idle, hub.Phase);
            var handedOff = handOff.TryAsync();
            Assert.True(ui.Until(() => handedOff.IsCompleted));
            Assert.Equal(1, started);
        });
    }

    [Fact]
    public async Task Rechecks_when_the_gate_query_finishes()
    {
        var reads = 0;
        var handOff = new UpdateHandOff(() => (UpdateMode.Automatic, Stage),
            () => ++reads == 1 ? Idle : Idle with { Dialog = true }, () => GateHolders.Clear,
            _ => throw new InvalidOperationException("started runner"), _ => { }, () => Assert.Fail("closed"), () => 61_000);
        await handOff.TryAsync();
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData("pcmi", "Pcmi")]
    [InlineData("PCMieru", "Other")]
    [InlineData(null, "Unknown")]
    public async Task Restart_Manager_defers_for_another_holder_or_a_failed_query(string? name, string expected)
    {
        IReadOnlyList<RestartManager.Holder>? rows = name is null ? null : [new(1, "PCMieru"), new(2, "pcmi"), new(3, name)];
        UpdateDeferral? reason = null;
        var handOff = new UpdateHandOff(() => (UpdateMode.Automatic, Stage), () => Idle,
            () => RestartManager.Classify(rows, new HashSet<int> { 1, 2 }), _ => throw new InvalidOperationException("started runner"),
            value => reason = value, () => Assert.Fail("closed"), () => 61_000);
        await handOff.TryAsync();
        Assert.Equal(expected, reason?.Holders.ToString());
    }

    [Fact]
    public void Restart_Manager_lists_the_process_that_holds_a_file_and_nobody_for_a_free_one()
    {
        // A broken query would read as "RM failed" and defer every update for ever, with no error anywhere: look at the real thing once.
        using var folder = new UpdateTestFolder();
        var held = Path.Combine(folder.Paths.Install, "held.txt");
        var free = Path.Combine(folder.Paths.Install, "free.txt");
        File.WriteAllText(held, "x");
        File.WriteAllText(free, "x");
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var holders = RestartManager.ReadHolders(held);
            Assert.NotNull(holders);
            Assert.Contains(holders, holder => holder.Pid == Environment.ProcessId);
            Assert.Equal(GateHolders.Clear, RestartManager.Classify(holders, new HashSet<int> { Environment.ProcessId }));
            Assert.Equal(GateHolders.Other, RestartManager.Classify(holders, new HashSet<int>()));
        }

        Assert.Empty(RestartManager.ReadHolders(free) ?? [new(0, "query failed")]);
    }

    [Fact]
    public void The_idle_facts_of_a_real_window_are_read_and_a_quiet_one_blocks_nothing_but_the_clock()
    {
        using var form = new Form();
        _ = form.Handle;
        var facts = UpdateHandOff.ReadFacts(form, menu: false, check: false, RecordingActivity.Idle, pendingUac: false, elevated: false);

        Assert.NotNull(facts.LastInput);
        Assert.False(facts.Dialog);
        Assert.False(facts.Menu);
        Assert.False(facts.Drag);
    }

    [Theory]
    [InlineData(ApplyRefusal.StageChanged)]
    [InlineData(ApplyRefusal.NoAnswer)]
    public async Task Refusal_or_timeout_never_enters_the_exit_path(ApplyRefusal refusal)
    {
        UpdateDeferral? reason = null;
        var handOff = new UpdateHandOff(() => (UpdateMode.Automatic, Stage), () => Idle, () => GateHolders.Clear,
            _ => ApplyHandOffResult.No(refusal, "runner reply"), value => reason = value, () => Assert.Fail("closed or stopped children"), () => 61_000);
        await handOff.TryAsync();
        Assert.Equal(refusal, reason?.Refusal);
    }

    [Fact]
    public async Task Acceptance_enters_the_exit_path_after_the_runner_answers()
    {
        var trace = new List<string>();
        var handOff = new UpdateHandOff(() => (UpdateMode.Automatic, Stage), () => Idle,
            () => RestartManager.Classify([new(1, "PCMieru"), new(2, "PCMieru.SensorHost")], new HashSet<int> { 1, 2 }),
            _ => { trace.Add("accepted"); return ApplyHandOffResult.Yes; },
            _ => Assert.Fail("deferred"), () => trace.Add("close"), () => 61_000);
        await handOff.TryAsync();
        Assert.Equal(["accepted", "close"], trace);
    }
}
