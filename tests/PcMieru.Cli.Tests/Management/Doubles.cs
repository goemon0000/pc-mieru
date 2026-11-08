using PcMieru.Wire;

namespace PcMieru.Cli.Tests.Management;

/// <summary>Wraps the Fake process control, records every call, and can force one answer so Denied paths can be tried.</summary>
internal sealed class RecordingControl(IProcessControl inner) : IProcessControl
{
    public List<string> Calls { get; } = [];

    /// <summary>Returned instead of the Fake's answer by the change calls (terminate, tree, priority, eco).</summary>
    public OperationResult? Forced { get; set; }

    public TerminatePlan? PlanOverride { get; set; }

    public Task<OperationResult> CloseWindowAsync(ProcessKey key, CancellationToken ct = default) => inner.CloseWindowAsync(key, ct);

    public Task<OperationResult> TerminateAsync(ProcessKey key, CancellationToken ct = default)
    {
        Calls.Add("terminate:" + key.Pid);
        return Forced is { } forced ? Task.FromResult(forced) : inner.TerminateAsync(key, ct);
    }

    public Task<TerminatePlan> PlanTerminateTreeAsync(ProcessKey root, CancellationToken ct = default)
    {
        Calls.Add("plan:" + root.Pid);
        return PlanOverride is { } plan ? Task.FromResult(plan) : inner.PlanTerminateTreeAsync(root, ct);
    }

    public Task<OperationResult> TerminateTreeAsync(TerminatePlan plan, CancellationToken ct = default)
    {
        Calls.Add("tree:" + plan.Root.Pid + ":" + plan.Targets.Count);
        return Forced is { } forced ? Task.FromResult(forced) : inner.TerminateTreeAsync(plan, ct);
    }

    public Task<OperationResult> SetPriorityAsync(ProcessKey key, SettablePriority priority, CancellationToken ct = default)
    {
        Calls.Add("priority:" + key.Pid + ":" + priority);
        return Forced is { } forced ? Task.FromResult(forced) : inner.SetPriorityAsync(key, priority, ct);
    }

    public Task<OperationResult> SetEfficiencyModeAsync(ProcessKey key, bool enabled, CancellationToken ct = default)
    {
        Calls.Add("eco:" + key.Pid + ":" + enabled);
        return Forced is { } forced ? Task.FromResult(forced) : inner.SetEfficiencyModeAsync(key, enabled, ct);
    }
}

/// <summary>Records the Helper requests and what the result file looked like at the moment of the call.</summary>
internal sealed class RecordingHelper : IHelperClient
{
    public List<HelperRequest> Requests { get; } = [];

    public List<(bool Existed, long Length)> ResultFileAtCall { get; } = [];

    public Func<HelperRequest, HelperOutcome> Answer { get; set; } = request =>
        new HelperOutcome(OperationCode.Ok, false, new HelperResult(request.Nonce, HelperOperations.Get(request.Op).Name, OperationCode.Ok, "helper did it"));

    public Task<HelperOutcome> RunAsync(HelperRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        var info = new FileInfo(request.ResultPath);
        ResultFileAtCall.Add((info.Exists, info.Exists ? info.Length : -1));
        return Task.FromResult(Answer(request));
    }
}

internal sealed class RecordingLauncher : ITaskLauncher
{
    public List<(string CommandLine, bool Elevated)> Calls { get; } = [];

    public OperationResult Answer { get; set; } = OperationResult.Success("started");

    public OperationResult Run(string commandLine, bool elevated)
    {
        Calls.Add((commandLine, elevated));
        return Answer;
    }
}

/// <summary>A service list the test controls, with a log of what was asked of it.</summary>
internal sealed class ScriptedServices(IReadOnlyList<ServiceInfo> services) : IServiceManager
{
    public List<string> Calls { get; } = [];

    public OperationResult? Forced { get; set; }

    public Task<IReadOnlyList<ServiceInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult(services);

    public Task<OperationResult> StartAsync(string name, CancellationToken ct = default) => Do("start", name);

    public Task<OperationResult> StopAsync(string name, CancellationToken ct = default) => Do("stop", name);

    public Task<OperationResult> RestartAsync(string name, CancellationToken ct = default) => Do("restart", name);

    private Task<OperationResult> Do(string verb, string name)
    {
        Calls.Add(verb + ":" + name);
        return Task.FromResult(Forced ?? OperationResult.Success(verb + " done"));
    }

    public static ServiceInfo Service(string name, ServiceState state, params string[] dependents) =>
        new(name, name + " display", state, ServiceStartType.Manual, null, Value.NotCollected<string>("test"), [], dependents);
}

/// <summary>A startup list the test controls; changes are logged and can be forced to a result.</summary>
internal sealed class ScriptedStartup(IReadOnlyList<StartupItem> items) : IStartupManager
{
    public List<string> Calls { get; } = [];

    public OperationResult? Forced { get; set; }

    public Task<IReadOnlyList<StartupItem>> ListAsync(CancellationToken ct = default) => Task.FromResult(items);

    public Task<OperationResult> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        Calls.Add(id + ":" + enabled);
        return Task.FromResult(Forced ?? OperationResult.Success("changed"));
    }
}

internal static class Fx
{
    /// <summary>The key the Fake snapshot gives a PID; the CLI looks the same process up, so the creation times match.</summary>
    public static async Task<ProcessKey> KeyAsync(uint pid)
    {
        using var services = CliRun.Services(new PcMieruOptions());
        var snapshot = await services.Snapshots.SampleAsync();
        return snapshot.Processes.First(p => p.Key.Pid == pid).Key;
    }

    public static async Task<string> StartedAtAsync(uint pid) =>
        (await KeyAsync(pid)).CreateTime.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pcmi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
