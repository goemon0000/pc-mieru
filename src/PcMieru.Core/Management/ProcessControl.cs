using System.Globalization;
using PcMieru.Wire;

namespace PcMieru.Core.Management;

/// <summary>
/// The single-process operations Core relies on. The real one is Wire (the same code the Helper runs); tests supply a
/// scripted one for the parts that must not touch a real process (tree order, partial results).
/// </summary>
internal interface IProcessActions
{
    ActionResult Terminate(ProcessKey key);

    ActionResult Close(ProcessKey key);

    ActionResult SetPriority(ProcessKey key, SettablePriority priority);

    ActionResult SetEfficiency(ProcessKey key, bool enable, EfficiencyRestore? restore);

    ActionResult SetAffinity(ProcessKey key, ulong mask);

    bool TryQueryAffinity(ProcessKey key, out ulong processMask, out ulong systemMask);
}

internal sealed class WireProcessActions : IProcessActions
{
    private readonly SelfIdentity _self = ProcessOperations.CurrentSelf();
    private readonly string _systemDirectory = Environment.SystemDirectory;

    public ActionResult Terminate(ProcessKey key) => ProcessOperations.Terminate(key.Pid, key.CreateTime, _self, _systemDirectory);

    public ActionResult Close(ProcessKey key) => WindowOperations.Close(key.Pid, key.CreateTime, _self, _systemDirectory);

    public ActionResult SetPriority(ProcessKey key, SettablePriority priority) =>
        ProcessOperations.SetPriority(key.Pid, key.CreateTime, priority, _self, _systemDirectory);

    public ActionResult SetEfficiency(ProcessKey key, bool enable, EfficiencyRestore? restore) =>
        ProcessOperations.SetEfficiency(key.Pid, key.CreateTime, enable, restore, _self, _systemDirectory);

    public ActionResult SetAffinity(ProcessKey key, ulong mask) =>
        ProcessOperations.SetAffinity(key.Pid, key.CreateTime, mask, _self, _systemDirectory);

    public bool TryQueryAffinity(ProcessKey key, out ulong processMask, out ulong systemMask) =>
        ProcessOperations.TryQueryAffinity(key.Pid, key.CreateTime, out processMask, out systemMask);
}

/// <summary>
/// The real <see cref="IProcessControl"/> (as the current user). Every operation re-opens the process, verifies PID
/// plus creation time and the protection rules on that one handle (in Wire), so what the UI showed a moment ago can
/// never be applied to a different process. Nothing here runs elevated and nothing prompts.
/// </summary>
public sealed class ProcessControl : IProcessControl, IProcessAffinityControl
{
    private readonly IProtectionPolicy _policy;
    private readonly IProcessActions _actions;
    private readonly IProcessTable _table;
    private readonly EcoStateStore _eco;

    public ProcessControl(IProtectionPolicy policy, EcoStateStore eco)
        : this(policy, new WireProcessActions(), new ToolhelpProcessTable(), eco)
    {
    }

    internal ProcessControl(IProtectionPolicy policy, IProcessActions actions, IProcessTable table, EcoStateStore eco)
    {
        _policy = policy;
        _actions = actions;
        _table = table;
        _eco = eco;
    }

    public Task<OperationResult> CloseWindowAsync(ProcessKey key, CancellationToken ct = default) =>
        Run(() => OperationMapper.ToResult(_actions.Close(key)), ct);

    public Task<OperationResult> TerminateAsync(ProcessKey key, CancellationToken ct = default) =>
        Run(() => OperationMapper.ToResult(_actions.Terminate(key)), ct);

    public Task<OperationResult> SetPriorityAsync(ProcessKey key, SettablePriority priority, CancellationToken ct = default) =>
        Run(() => OperationMapper.ToResult(_actions.SetPriority(key, priority)), ct);

    public Task<OperationResult> SetEfficiencyModeAsync(ProcessKey key, bool enabled, CancellationToken ct = default) =>
        Run(() => SetEfficiency(key, enabled), ct);

    public Task<TerminatePlan> PlanTerminateTreeAsync(ProcessKey root, CancellationToken ct = default) =>
        Task.Run(() => Plan(root, ct), ct);

    public Task<OperationResult> TerminateTreeAsync(TerminatePlan plan, CancellationToken ct = default) =>
        Task.Run(() => TerminateTree(plan, ct), ct);

    public AffinityInfo? QueryAffinity(ProcessKey key) =>
        _actions.TryQueryAffinity(key, out var process, out var system) ? new AffinityInfo(process, system) : null;

    public Task<OperationResult> SetAffinityAsync(ProcessKey key, ulong mask, CancellationToken ct = default) =>
        Run(() => OperationMapper.ToResult(_actions.SetAffinity(key, mask)), ct);

    private static Task<OperationResult> Run(Func<OperationResult> work, CancellationToken ct) =>
        Task.Run(
            () => ct.IsCancellationRequested
                ? new OperationResult(OperationCode.Cancelled, "")
                : work(),
            ct);

    private OperationResult SetEfficiency(ProcessKey key, bool enable)
    {
        if (enable)
        {
            var on = _actions.SetEfficiency(key, true, null);
            if (on.Code == OperationCode.Ok && EcoStateStore.FromData(on.Data) is { } record)
            {
                _eco.SetIfAbsent(key, record, _table.IsAlive);
            }

            return OperationMapper.ToResult(on);
        }

        var restore = EcoStateStore.ToRestore(_eco.Get(key));
        var off = _actions.SetEfficiency(key, false, restore);
        if (off.Code is OperationCode.Ok or OperationCode.Gone)
        {
            _eco.Remove(key, _table.IsAlive);
        }

        return OperationMapper.ToResult(off);
    }

    private TerminatePlan Plan(ProcessKey root, CancellationToken ct)
    {
        var verdict = _policy.Evaluate(root);
        if (!verdict.MayOperate)
        {
            // A protected (or unconfirmable) root: nothing is planned at all, and the root is said to be skipped.
            return new TerminatePlan(root, [], [root]);
        }

        if (!_table.IsAlive(root))
        {
            return new TerminatePlan(root, [], []);
        }

        var index = new ProcessTreeIndex(_table);
        var targets = new List<ProcessKey> { root };
        var skipped = new List<ProcessKey>();
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var child in index.ChildrenOf(targets[i]))
            {
                if (_policy.Evaluate(child).MayOperate)
                {
                    targets.Add(child);
                }
                else
                {
                    // Not followed either: what a protected process started is not ours to take along.
                    skipped.Add(child);
                }
            }
        }

        return new TerminatePlan(root, targets, skipped);
    }

    private OperationResult TerminateTree(TerminatePlan plan, CancellationToken ct)
    {
        var details = new List<OperationDetail>();
        foreach (var key in plan.SkippedProtected)
        {
            details.Add(new OperationDetail(Target(key), OperationCode.Protected, _policy.Evaluate(key).Reason ?? Strings.Get(ManagementStrings.ProtectedReason)));
        }

        if (plan.Targets.Count == 0)
        {
            return plan.SkippedProtected.Contains(plan.Root)
                ? new OperationResult(OperationCode.Protected, _policy.Evaluate(plan.Root).Reason ?? Strings.Get(ManagementStrings.ProtectedReason), details)
                : OperationMapper.ToResult(OperationCode.Gone, MessageKeys.TreeNothing) with { Details = details };
        }

        // Leaves first: the plan is breadth first, so the reverse puts the root last. A child never outlives its parent by our doing.
        var done = 0;
        var firstFailure = OperationCode.Ok;
        var cancelled = false;
        for (var i = plan.Targets.Count - 1; i >= 0; i--)
        {
            var key = plan.Targets[i];
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                details.Add(new OperationDetail(Target(key), OperationCode.Cancelled, ""));
                continue;
            }

            var result = _actions.Terminate(key);
            details.Add(new OperationDetail(Target(key), result.Code, OperationMapper.Text(result.MessageKey, result.Args)));
            if (result.Code is OperationCode.Ok or OperationCode.Gone)
            {
                done++;
            }
            else if (firstFailure == OperationCode.Ok)
            {
                firstFailure = result.Code;
            }
        }

        var total = plan.Targets.Count;
        if (done == total)
        {
            return new OperationResult(
                OperationCode.Ok,
                OperationMapper.Text(MessageKeys.TreeDone, Args(("count", total))),
                details);
        }

        var code = done > 0
            ? OperationCode.PartialSuccess
            : cancelled ? OperationCode.Cancelled : firstFailure;
        return new OperationResult(
            code,
            OperationMapper.Text(MessageKeys.TreePartial, Args(("done", done), ("total", total))),
            details);
    }

    private static string Target(ProcessKey key) => key.Pid.ToString(CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Args(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);
}

/// <summary>
/// Process operations through the Helper, for "retry as administrator". One UAC prompt per call; the Helper repeats the
/// identity and protection checks, so nothing the unelevated path would refuse is allowed here.
/// </summary>
public sealed class ElevatedProcessActions : IElevatedProcessActions
{
    private readonly IHelperClient _helper;
    private readonly EcoStateStore _eco;

    public ElevatedProcessActions(IHelperClient helper, EcoStateStore eco)
    {
        _helper = helper;
        _eco = eco;
    }

    public async Task<OperationResult> TerminateAsync(ProcessKey key, CancellationToken ct = default) =>
        OperationMapper.FromHelper(await _helper.RunAsync(Request(HelperOperation.ProcessTerminate, key), ct).ConfigureAwait(false));

    public async Task<OperationResult> SetPriorityAsync(ProcessKey key, SettablePriority priority, CancellationToken ct = default) =>
        OperationMapper.FromHelper(
            await _helper.RunAsync(Request(HelperOperation.ProcessPriority, key) with { Priority = priority }, ct).ConfigureAwait(false));

    public async Task<OperationResult> SetEfficiencyModeAsync(ProcessKey key, bool enabled, CancellationToken ct = default)
    {
        var request = Request(HelperOperation.ProcessEfficiency, key) with { Enable = enabled };
        if (!enabled && EcoStateStore.ToRestore(_eco.Get(key)) is { } restore)
        {
            request = request with
            {
                PreviousPriorityClass = restore.PriorityClass,
                PreviousThrottleControl = restore.ThrottleControl,
                PreviousThrottleState = restore.ThrottleState,
            };
        }

        var outcome = await _helper.RunAsync(request, ct).ConfigureAwait(false);
        if (outcome.Code == OperationCode.Ok && ActionFromHelper(outcome) is { } action)
        {
            if (enabled && EcoStateStore.FromData(action.Data) is { } record)
            {
                _eco.SetIfAbsent(key, record);
            }
        }

        if (!enabled && outcome.Code is OperationCode.Ok or OperationCode.Gone)
        {
            _eco.Remove(key);
        }

        return OperationMapper.FromHelper(outcome);
    }

    private static ActionResult? ActionFromHelper(HelperOutcome outcome) => OperationMapper.ActionFromHelper(outcome);

    // The client fills in the nonce and the result path (callers cannot choose them).
    private static HelperRequest Request(HelperOperation op, ProcessKey key) =>
        new(op, "", "") { Pid = key.Pid, CreateTime = key.CreateTime };
}
