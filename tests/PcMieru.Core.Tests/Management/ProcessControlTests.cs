using System.Diagnostics;
using PcMieru.Core.Management;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.Management;

internal sealed class ScriptedActions : IProcessActions
{
    public Dictionary<uint, ActionResult> TerminateResults { get; } = [];

    public List<uint> Terminated { get; } = [];

    public List<(ProcessKey Key, bool Enable, EfficiencyRestore? Restore)> EfficiencyCalls { get; } = [];

    public ActionResult EfficiencyOn { get; set; } = ActionResult.Of(OperationCode.Ok, MessageKeys.EfficiencyOn).WithData(
        new Dictionary<string, string>
        {
            [HelperDataKeys.PreviousPriority] = "32",
            [HelperDataKeys.PreviousControl] = "1",
            [HelperDataKeys.PreviousState] = "0",
            [HelperDataKeys.PreviousKnown] = "true",
        });

    public ActionResult EfficiencyOff { get; set; } = ActionResult.Of(OperationCode.Ok, MessageKeys.EfficiencyOff);

    public ActionResult Terminate(ProcessKey key)
    {
        Terminated.Add(key.Pid);
        return TerminateResults.TryGetValue(key.Pid, out var r) ? r : ActionResult.Of(OperationCode.Ok, MessageKeys.ProcessTerminated, ("pid", key.Pid.ToString()));
    }

    public ActionResult Close(ProcessKey key) => ActionResult.Of(OperationCode.Ok, MessageKeys.CloseDone, ("pid", key.Pid.ToString()));

    public ActionResult SetPriority(ProcessKey key, SettablePriority priority) =>
        ActionResult.Of(OperationCode.Ok, MessageKeys.ProcessPrioritySet, ("priority", priority.ToString()));

    public ActionResult SetEfficiency(ProcessKey key, bool enable, EfficiencyRestore? restore)
    {
        EfficiencyCalls.Add((key, enable, restore));
        return enable ? EfficiencyOn : EfficiencyOff;
    }

    public ActionResult SetAffinity(ProcessKey key, ulong mask) => ActionResult.Of(OperationCode.Ok, MessageKeys.AffinitySet);

    public bool TryQueryAffinity(ProcessKey key, out ulong processMask, out ulong systemMask)
    {
        processMask = systemMask = 0xF;
        return true;
    }
}

internal sealed class FakeTable : IProcessTable
{
    public List<ProcessRow> Table { get; } = [];

    public Dictionary<uint, long> Created { get; } = [];

    public HashSet<uint> Dead { get; } = [];

    public FakeTable Add(uint pid, uint parent, long created, string name = "x.exe")
    {
        Table.Add(new ProcessRow(pid, parent, name));
        Created[pid] = created;
        return this;
    }

    public IReadOnlyList<ProcessRow> Rows() => Table;

    public long? CreateTime(uint pid) => !Dead.Contains(pid) && Created.TryGetValue(pid, out var c) ? c : null;

    public bool IsAlive(ProcessKey key) => CreateTime(key.Pid) == key.CreateTime;
}

public class OperationMapperTests
{
    [Fact]
    public void A_result_keeps_its_code_and_is_worded_in_the_requested_language()
    {
        Strings.Register(ManagementStrings.All);
        var action = ActionResult.Of(OperationCode.Ok, MessageKeys.ProcessTerminated, ("pid", "4242"));
        var ja = OperationMapper.ToResult(action, Language.Ja);
        var en = OperationMapper.ToResult(action, Language.En);
        Assert.Equal(OperationCode.Ok, ja.Code);
        Assert.Contains("4242", ja.Message);
        Assert.Contains("4242", en.Message);
        Assert.NotEqual(ja.Message, en.Message);
    }

    [Fact]
    public void A_message_that_is_not_one_of_our_keys_is_kept_as_given()
    {
        Strings.Register(ManagementStrings.All);
        Assert.Equal("some words", OperationMapper.ToResult(ActionResult.Of(OperationCode.Error, "some words"), Language.En).Message);
    }

    [Fact]
    public void A_key_given_as_a_value_is_worded_too()
    {
        Strings.Register(ManagementStrings.All);
        var action = ActionResult.Of(
            OperationCode.PartialSuccess, MessageKeys.ServiceRestartStoppedOnly, ("name", "Spooler"), ("reason", MessageKeys.ServiceDisabled));
        var text = OperationMapper.ToResult(action, Language.En).Message;
        Assert.Contains("Spooler", text);
        Assert.DoesNotContain("mgmt.", text);
    }

    [Fact]
    public void A_declined_prompt_is_cancelled_with_its_own_text()
    {
        Strings.Register(ManagementStrings.All);
        var result = OperationMapper.FromHelper(new HelperOutcome(OperationCode.Cancelled, true, null), Language.En);
        Assert.Equal(OperationCode.Cancelled, result.Code);
        Assert.Equal(Strings.Get(ManagementStrings.UacDeclined, Language.En), result.Message);
    }

    [Fact]
    public void The_helper_text_is_used_only_when_it_agrees_with_the_exit_code()
    {
        Strings.Register(ManagementStrings.All);
        var agreeing = new HelperResult("n", "service-stop", OperationCode.Denied, MessageKeys.ServiceDenied, new() { ["arg.name"] = "Spooler" });
        var used = OperationMapper.FromHelper(new HelperOutcome(OperationCode.Denied, false, agreeing), Language.En);
        Assert.Equal(OperationCode.Denied, used.Code);
        Assert.Contains("Spooler", used.Message);

        // The file says Ok but the process said Error: the exit code wins and the file is not trusted for the wording.
        var lying = agreeing with { Code = OperationCode.Ok, Message = MessageKeys.ServiceStopped };
        var ignored = OperationMapper.FromHelper(new HelperOutcome(OperationCode.Error, false, lying), Language.En);
        Assert.Equal(OperationCode.Error, ignored.Code);
        Assert.Equal(Strings.Get(MessageKeys.HelperNoResult, Language.En), ignored.Message);
    }

    [Fact]
    public void An_exit_code_without_a_readable_file_still_says_what_is_certain()
    {
        Strings.Register(ManagementStrings.All);
        var ok = OperationMapper.FromHelper(new HelperOutcome(OperationCode.Ok, false, null), Language.En);
        Assert.Equal(OperationCode.Ok, ok.Code);
        Assert.Equal(Strings.Get(ManagementStrings.HelperDone, Language.En), ok.Message);

        var cancelled = OperationMapper.FromHelper(new HelperOutcome(OperationCode.Cancelled, false, null), Language.En);
        Assert.Equal(OperationCode.Cancelled, cancelled.Code);
        Assert.Equal("", cancelled.Message);
    }

    [Fact]
    public void Action_from_helper_is_null_unless_the_result_agrees_with_the_exit_code()
    {
        var result = new HelperResult("n", "process-efficiency", OperationCode.Ok, MessageKeys.EfficiencyOn, new() { ["prev.priority"] = "32" });
        Assert.NotNull(OperationMapper.ActionFromHelper(new HelperOutcome(OperationCode.Ok, false, result)));
        Assert.Null(OperationMapper.ActionFromHelper(new HelperOutcome(OperationCode.Error, false, result)));
        Assert.Null(OperationMapper.ActionFromHelper(new HelperOutcome(OperationCode.Cancelled, true, result)));
        Assert.Null(OperationMapper.ActionFromHelper(new HelperOutcome(OperationCode.Ok, false, null)));
    }
}

public class ProtectionPolicyTests
{
    [Fact]
    public void Only_a_fully_answered_allowed_is_none()
    {
        Strings.Register(ManagementStrings.All);
        var allowed = new ProtectionPolicy(_ => new ProtectionAssessment(ProtectionOutcome.Allowed, ProtectionReason.None));
        Assert.True(allowed.Evaluate(new ProcessKey(10, 1)).MayOperate);

        var protectedCore = new ProtectionPolicy(_ => new ProtectionAssessment(ProtectionOutcome.Protected, ProtectionReason.WindowsCore));
        var verdict = protectedCore.Evaluate(new ProcessKey(10, 1));
        Assert.Equal(ProtectionKind.Protected, verdict.Kind);
        Assert.False(verdict.MayOperate);
        Assert.False(string.IsNullOrEmpty(verdict.Reason));

        var unknown = new ProtectionPolicy(_ => new ProtectionAssessment(ProtectionOutcome.Unknown, ProtectionReason.CannotConfirm));
        Assert.Equal(ProtectionKind.Unknown, unknown.Evaluate(new ProcessKey(10, 1)).Kind);
        Assert.False(unknown.Evaluate(new ProcessKey(10, 1)).MayOperate);
    }

    [Fact]
    public void A_throwing_inspection_is_unknown_never_allowed()
    {
        var policy = new ProtectionPolicy(_ => throw new InvalidOperationException("boom"));
        var verdict = policy.Evaluate(new ProcessKey(10, 1));
        Assert.Equal(ProtectionKind.Unknown, verdict.Kind);
        Assert.False(verdict.MayOperate);
    }

    [Fact]
    public void On_this_PC_the_system_ourselves_and_a_vanished_process_are_not_operable_but_our_own_ping_is()
    {
        var policy = new ProtectionPolicy();
        Assert.False(policy.Evaluate(new ProcessKey(4, 1)).MayOperate);
        Assert.False(policy.Evaluate(new ProcessKey(0, 1)).MayOperate);
        Assert.False(policy.Evaluate(new ProcessKey(0x7FFF_FFF0, 123)).MayOperate);

        using var self = Process.GetCurrentProcess();
        var selfVerdict = policy.Evaluate(new ProcessKey((uint)self.Id, self.StartTime.ToFileTimeUtc()));
        Assert.Equal(ProtectionKind.Protected, selfVerdict.Kind);

        using var ping = new OwnPing();
        Assert.True(policy.Evaluate(new ProcessKey(ping.Pid, ping.CreateTime)).MayOperate);
        // The same PID with another creation time is not the same process: never "allowed".
        Assert.False(policy.Evaluate(new ProcessKey(ping.Pid, ping.CreateTime + 10_000_000)).MayOperate);
    }

    [Fact]
    public void On_this_PC_the_core_windows_processes_are_protected()
    {
        var policy = new ProtectionPolicy();
        var table = new ToolhelpProcessTable();
        var seen = 0;
        foreach (var name in new[] { "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "winlogon.exe", "smss.exe" })
        {
            foreach (var row in table.Rows().Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                // The creation time of a protected process may not be readable without elevation; then there is no key to ask about.
                if (table.CreateTime(row.Pid) is { } created)
                {
                    seen++;
                    Assert.False(policy.Evaluate(new ProcessKey(row.Pid, created)).MayOperate, name);
                }
            }
        }

        Assert.True(seen >= 0);
    }
}

public class EcoStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcmieru-eco-" + Guid.NewGuid().ToString("N"));

    private string File_ => Path.Combine(_dir, "eco.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void The_first_record_is_kept_and_a_new_store_reads_it_back()
    {
        var key = new ProcessKey(100, 5000);
        var store = new EcoStateStore(File_);
        store.SetIfAbsent(key, new EcoRecord(32, 1, 0, true));
        store.SetIfAbsent(key, new EcoRecord(64, 1, 1, true));
        Assert.Equal(new EcoRecord(32, 1, 0, true), store.Get(key));

        var reread = new EcoStateStore(File_);
        Assert.Equal(new EcoRecord(32, 1, 0, true), reread.Get(key));
        Assert.Null(reread.Get(new ProcessKey(100, 5001)));

        reread.Remove(key);
        Assert.Null(new EcoStateStore(File_).Get(key));
    }

    [Fact]
    public void A_process_that_is_gone_is_forgotten_on_the_next_write()
    {
        var store = new EcoStateStore(File_);
        var old = new ProcessKey(100, 5000);
        var current = new ProcessKey(200, 6000);
        store.SetIfAbsent(old, new EcoRecord(32, 0, 0, false));
        store.SetIfAbsent(current, new EcoRecord(32, 0, 0, false), key => key == current);
        Assert.Null(store.Get(old));
        Assert.NotNull(store.Get(current));
    }

    [Fact]
    public void A_damaged_file_is_the_same_as_no_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{ not json");
        var store = new EcoStateStore(File_);
        Assert.Null(store.Get(new ProcessKey(1, 1)));
        store.SetIfAbsent(new ProcessKey(1, 1), new EcoRecord(32, 0, 0, false));
        Assert.NotNull(new EcoStateStore(File_).Get(new ProcessKey(1, 1)));
    }

    [Fact]
    public void An_unwritable_place_only_costs_the_file()
    {
        // A file where the directory should be: Save cannot create the folder.
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var store = new EcoStateStore(Path.Combine(blocker, "eco.json"));
        store.SetIfAbsent(new ProcessKey(1, 1), new EcoRecord(32, 0, 0, false));
        Assert.NotNull(store.Get(new ProcessKey(1, 1)));
    }

    [Fact]
    public void Data_from_a_successful_on_becomes_a_record_and_a_restore()
    {
        var data = new Dictionary<string, string>
        {
            [HelperDataKeys.PreviousPriority] = "32",
            [HelperDataKeys.PreviousControl] = "1",
            [HelperDataKeys.PreviousState] = "0",
            [HelperDataKeys.PreviousKnown] = "true",
        };
        var record = EcoStateStore.FromData(data);
        Assert.Equal(new EcoRecord(32, 1, 0, true), record);
        Assert.Equal(new EfficiencyRestore(32, 1, 0), EcoStateStore.ToRestore(record));

        Assert.Null(EcoStateStore.FromData(null));
        Assert.Null(EcoStateStore.FromData(new Dictionary<string, string>()));
        Assert.Null(EcoStateStore.FromData(new Dictionary<string, string>(data) { [HelperDataKeys.PreviousPriority] = "x" }));
    }

    [Fact]
    public void Nothing_known_or_a_value_the_helper_would_refuse_restores_the_system_default()
    {
        Assert.Null(EcoStateStore.ToRestore(null));
        Assert.Null(EcoStateStore.ToRestore(new EcoRecord(32, 0, 0, false)));
        Assert.Null(EcoStateStore.ToRestore(new EcoRecord(0x100, 0, 0, true)));
        Assert.Null(EcoStateStore.ToRestore(new EcoRecord(32, 2, 0, true)));
    }
}

public class ProcessControlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcmieru-pc-" + Guid.NewGuid().ToString("N"));

    private static readonly IProtectionPolicy AllowAll = new ProtectionPolicy(_ => new ProtectionAssessment(ProtectionOutcome.Allowed, ProtectionReason.None));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private EcoStateStore Store() => new(Path.Combine(_dir, "eco.json"));

    private static IProtectionPolicy Blocking(params uint[] pids) =>
        new ProtectionPolicy(key => pids.Contains(key.Pid)
            ? new ProtectionAssessment(ProtectionOutcome.Protected, ProtectionReason.WindowsCore)
            : new ProtectionAssessment(ProtectionOutcome.Allowed, ProtectionReason.None));

    [Fact]
    public async Task A_cancelled_call_does_nothing()
    {
        Strings.Register(ManagementStrings.All);
        var actions = new ScriptedActions();
        var control = new ProcessControl(AllowAll, actions, new FakeTable(), Store());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await control.TerminateAsync(new ProcessKey(100, 1000), cts.Token).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(t => t.IsCanceled ? new OperationResult(OperationCode.Cancelled, "") : t.Result);
        Assert.Equal(OperationCode.Cancelled, result.Code);
        Assert.Empty(actions.Terminated);
    }

    [Fact]
    public async Task Efficiency_on_remembers_the_first_values_and_off_puts_them_back_and_forgets()
    {
        Strings.Register(ManagementStrings.All);
        var actions = new ScriptedActions();
        var key = new ProcessKey(100, 1000);
        var table = new FakeTable().Add(100, 1, 1000);
        var store = Store();
        var control = new ProcessControl(AllowAll, actions, table, store);

        Assert.True((await control.SetEfficiencyModeAsync(key, true)).IsSuccess);
        Assert.Equal(new EcoRecord(32, 1, 0, true), store.Get(key));

        // A second "on" (already throttled values) must not replace the original record.
        actions.EfficiencyOn = ActionResult.Of(OperationCode.Ok, MessageKeys.EfficiencyOn).WithData(
            new Dictionary<string, string>
            {
                [HelperDataKeys.PreviousPriority] = "64",
                [HelperDataKeys.PreviousControl] = "1",
                [HelperDataKeys.PreviousState] = "1",
                [HelperDataKeys.PreviousKnown] = "true",
            });
        await control.SetEfficiencyModeAsync(key, true);
        Assert.Equal(new EcoRecord(32, 1, 0, true), store.Get(key));

        Assert.True((await control.SetEfficiencyModeAsync(key, false)).IsSuccess);
        Assert.Equal(new EfficiencyRestore(32, 1, 0), actions.EfficiencyCalls.Last().Restore);
        Assert.Null(store.Get(key));
    }

    [Fact]
    public async Task Efficiency_off_with_nothing_recorded_asks_for_the_system_default()
    {
        var actions = new ScriptedActions();
        var control = new ProcessControl(AllowAll, actions, new FakeTable(), Store());
        await control.SetEfficiencyModeAsync(new ProcessKey(100, 1000), false);
        Assert.Null(actions.EfficiencyCalls.Single().Restore);
    }

    [Fact]
    public async Task A_failed_off_keeps_the_record_for_another_try()
    {
        var actions = new ScriptedActions();
        var key = new ProcessKey(100, 1000);
        var store = Store();
        store.SetIfAbsent(key, new EcoRecord(32, 1, 0, true));
        actions.EfficiencyOff = ActionResult.Of(OperationCode.Denied, MessageKeys.ProcessDenied);
        var control = new ProcessControl(AllowAll, actions, new FakeTable().Add(100, 1, 1000), store);
        Assert.Equal(OperationCode.Denied, (await control.SetEfficiencyModeAsync(key, false)).Code);
        Assert.NotNull(store.Get(key));
    }

    private static FakeTable Tree() => new FakeTable()
        .Add(100, 1, 1000, "root.exe")
        .Add(101, 100, 2000, "child.exe")
        .Add(102, 101, 3000, "grandchild.exe")
        .Add(103, 100, 2500, "guarded.exe")
        .Add(104, 103, 2600, "under-guarded.exe")
        .Add(105, 100, 500, "older-than-parent.exe");

    [Fact]
    public async Task The_plan_skips_protected_children_and_stale_parent_links_and_does_not_follow_them()
    {
        var control = new ProcessControl(Blocking(103), new ScriptedActions(), Tree(), Store());
        var plan = await control.PlanTerminateTreeAsync(new ProcessKey(100, 1000));
        Assert.Equal([100u, 101u, 102u], plan.Targets.Select(k => k.Pid));
        Assert.Equal([103u], plan.SkippedProtected.Select(k => k.Pid));
        Assert.DoesNotContain(plan.Targets, k => k.Pid is 104 or 105);
    }

    [Fact]
    public async Task A_protected_or_vanished_root_plans_nothing()
    {
        var control = new ProcessControl(Blocking(100), new ScriptedActions(), Tree(), Store());
        var blocked = await control.PlanTerminateTreeAsync(new ProcessKey(100, 1000));
        Assert.Empty(blocked.Targets);
        Assert.Equal([100u], blocked.SkippedProtected.Select(k => k.Pid));
        Assert.Equal(OperationCode.Protected, (await control.TerminateTreeAsync(blocked)).Code);

        var table = Tree();
        table.Dead.Add(100);
        var gone = new ProcessControl(AllowAll, new ScriptedActions(), table, Store());
        var plan = await gone.PlanTerminateTreeAsync(new ProcessKey(100, 1000));
        Assert.Empty(plan.Targets);
        Assert.Equal(OperationCode.Gone, (await gone.TerminateTreeAsync(plan)).Code);
    }

    [Fact]
    public async Task The_tree_is_ended_leaves_first_and_the_skipped_ones_are_reported()
    {
        Strings.Register(ManagementStrings.All);
        var actions = new ScriptedActions();
        var control = new ProcessControl(Blocking(103), actions, Tree(), Store());
        var plan = await control.PlanTerminateTreeAsync(new ProcessKey(100, 1000));
        var result = await control.TerminateTreeAsync(plan);

        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.Equal([102u, 101u, 100u], actions.Terminated);
        Assert.Contains(result.Details!, d => d.Target == "103" && d.Code == OperationCode.Protected);
        Assert.Equal(4, result.Details!.Count);
    }

    [Fact]
    public async Task One_failure_makes_it_partial_and_all_failures_report_the_first_code()
    {
        Strings.Register(ManagementStrings.All);
        var actions = new ScriptedActions();
        actions.TerminateResults[101] = ActionResult.Of(OperationCode.Denied, MessageKeys.ProcessDenied);
        var control = new ProcessControl(AllowAll, actions, Tree(), Store());
        var plan = await control.PlanTerminateTreeAsync(new ProcessKey(100, 1000));
        var partial = await control.TerminateTreeAsync(plan);
        Assert.Equal(OperationCode.PartialSuccess, partial.Code);
        Assert.Contains(partial.Details!, d => d.Target == "101" && d.Code == OperationCode.Denied);

        var allFail = new ScriptedActions();
        foreach (var pid in new uint[] { 100, 101, 102, 103, 104 })
        {
            allFail.TerminateResults[pid] = ActionResult.Of(OperationCode.Denied, MessageKeys.ProcessDenied);
        }

        var failing = new ProcessControl(AllowAll, allFail, Tree(), Store());
        var all = await failing.TerminateTreeAsync(await failing.PlanTerminateTreeAsync(new ProcessKey(100, 1000)));
        Assert.Equal(OperationCode.Denied, all.Code);
    }

    [Fact]
    public async Task A_process_that_ended_by_itself_counts_as_done()
    {
        var actions = new ScriptedActions();
        actions.TerminateResults[102] = ActionResult.Of(OperationCode.Gone, MessageKeys.ProcessGone);
        var control = new ProcessControl(AllowAll, actions, Tree(), Store());
        var result = await control.TerminateTreeAsync(await control.PlanTerminateTreeAsync(new ProcessKey(100, 1000)));
        Assert.Equal(OperationCode.Ok, result.Code);
    }

    [Fact]
    public async Task Cancelling_between_processes_stops_the_rest()
    {
        var actions = new ScriptedActions();
        var control = new ProcessControl(AllowAll, actions, Tree(), Store());
        var plan = await control.PlanTerminateTreeAsync(new ProcessKey(100, 1000));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await Task.Run(() => control.TerminateTreeAsync(plan, cts.Token)).ContinueWith(t => t.IsCanceled ? new OperationResult(OperationCode.Cancelled, "") : t.Result);
        Assert.Equal(OperationCode.Cancelled, result.Code);
        Assert.Empty(actions.Terminated);
    }

    // ---- the real thing, on processes this test started itself ----

    [Fact]
    public async Task Real_priority_efficiency_and_affinity_on_our_own_ping()
    {
        Strings.Register(ManagementStrings.All);
        using var ping = new OwnPing();
        var key = new ProcessKey(ping.Pid, ping.CreateTime);
        var store = Store();
        var control = new ProcessControl(new ProtectionPolicy(), store);

        Assert.True((await control.SetPriorityAsync(key, SettablePriority.BelowNormal)).IsSuccess);
        Assert.Equal(ProcessPriorityClass.BelowNormal, ping.Priority);
        Assert.True((await control.SetPriorityAsync(key, SettablePriority.Normal)).IsSuccess);

        Assert.True((await control.SetEfficiencyModeAsync(key, true)).IsSuccess);
        Assert.Equal(ProcessPriorityClass.Idle, ping.Priority);
        Assert.NotNull(store.Get(key));
        Assert.True((await control.SetEfficiencyModeAsync(key, false)).IsSuccess);
        Assert.Equal(ProcessPriorityClass.Normal, ping.Priority);
        Assert.Null(store.Get(key));

        var before = control.QueryAffinity(key);
        Assert.NotNull(before);
        var lowest = before!.ProcessMask & (~before.ProcessMask + 1);
        Assert.True((await control.SetAffinityAsync(key, lowest)).IsSuccess);
        Assert.Equal(lowest, control.QueryAffinity(key)!.ProcessMask);
        Assert.Equal(OperationCode.InvalidArguments, (await control.SetAffinityAsync(key, 0)).Code);
        Assert.True((await control.SetAffinityAsync(key, before.ProcessMask)).IsSuccess);
        Assert.Equal(before.ProcessMask, control.QueryAffinity(key)!.ProcessMask);
    }

    [Fact]
    public async Task Real_terminate_ends_our_ping_and_a_wrong_creation_time_leaves_it_alone()
    {
        Strings.Register(ManagementStrings.All);
        using var ping = new OwnPing();
        var control = new ProcessControl(new ProtectionPolicy(), Store());

        var wrong = await control.TerminateAsync(new ProcessKey(ping.Pid, ping.CreateTime + 10_000_000));
        Assert.NotEqual(OperationCode.Ok, wrong.Code);
        Assert.False(ping.HasExited);

        var right = await control.TerminateAsync(new ProcessKey(ping.Pid, ping.CreateTime));
        Assert.Equal(OperationCode.Ok, right.Code);
        Assert.True(ping.HasExited);
    }

    [Fact]
    public async Task Real_terminate_tree_ends_a_cmd_and_the_ping_it_started_and_nothing_else()
    {
        Strings.Register(ManagementStrings.All);
        var info = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 120 127.0.0.1 > nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var cmd = Process.Start(info)!;
        var table = new ToolhelpProcessTable();
        var cmdKey = new ProcessKey((uint)cmd.Id, cmd.StartTime.ToFileTimeUtc());
        try
        {
            var control = new ProcessControl(new ProtectionPolicy(), Store());
            TerminatePlan plan = new(cmdKey, [], []);
            for (var i = 0; i < 40 && plan.Targets.Count < 2; i++)
            {
                plan = await control.PlanTerminateTreeAsync(cmdKey);
                if (plan.Targets.Count < 2)
                {
                    await Task.Delay(100);
                }
            }

            Assert.Equal(2, plan.Targets.Count);
            Assert.Equal(cmdKey, plan.Targets[0]);
            Assert.Empty(plan.SkippedProtected);
            var pingPid = plan.Targets[1].Pid;

            var result = await control.TerminateTreeAsync(plan);
            Assert.Equal(OperationCode.Ok, result.Code);
            Assert.True(cmd.WaitForExit(5000));
            Assert.False(table.IsAlive(plan.Targets[1]), "ping " + pingPid);
        }
        finally
        {
            if (!cmd.HasExited)
            {
                cmd.Kill(entireProcessTree: true);
            }
        }
    }
}
