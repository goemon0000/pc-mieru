using PcMieru.Update.Feed;
using PcMieru.Update.Launch;

namespace PcMieru.Gui.Tests;

public sealed class UpdateHandOffTests
{
    private static readonly StagedUpdate Stage = new()
    {
        Folder = "stage", Version = "1.2.3", BuildId = "build", ManifestSha256 = "sha", Tag = "v1.2.3",
    };

    private static readonly UpdateIdleFacts Idle = new(1000, false, false, false, false, RecordingActivity.Idle, false, false);

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
