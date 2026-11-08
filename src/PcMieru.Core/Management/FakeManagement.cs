using PcMieru.Wire;

namespace PcMieru.Core.Management;

/// <summary>PID 0/4, the Windows core names and anything unknown are protected; "unknown" is never "allowed".</summary>
public sealed class FakeProtectionPolicy : IProtectionPolicy, IFakeImplementation
{
    private static readonly HashSet<string> CoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "winlogon.exe", "fontdrvhost.exe", "dwm.exe",
    };

    public ProtectionVerdict Evaluate(ProcessKey key)
    {
        if (key.Pid is 0 or 4)
        {
            return new ProtectionVerdict(ProtectionKind.Protected, "system process");
        }

        var process = FakeSnapshots_Find(key);
        if (process is null)
        {
            return new ProtectionVerdict(ProtectionKind.Unknown, "could not confirm the process is not protected");
        }

        return CoreNames.Contains(process.Name)
            ? new ProtectionVerdict(ProtectionKind.Protected, "Windows core process")
            : new ProtectionVerdict(ProtectionKind.None, null);
    }

    private static ProcessInfo? FakeSnapshots_Find(ProcessKey key) =>
        Collection.FakeSnapshots.Create(DateTime.UtcNow, 1).Processes.FirstOrDefault(p => p.Key == key);
}

public sealed class FakeProcessControl : IProcessControl, IFakeImplementation
{
    private readonly IProtectionPolicy _policy;

    public FakeProcessControl(IProtectionPolicy policy) => _policy = policy;

    public Task<OperationResult> CloseWindowAsync(ProcessKey key, CancellationToken ct = default) =>
        Guarded(key, "close requested");

    public Task<OperationResult> TerminateAsync(ProcessKey key, CancellationToken ct = default) =>
        Guarded(key, "terminated");

    public Task<TerminatePlan> PlanTerminateTreeAsync(ProcessKey root, CancellationToken ct = default)
    {
        var all = Collection.FakeSnapshots.Create(DateTime.UtcNow, 1).Processes;
        var targets = new List<ProcessKey> { root };
        var skipped = new List<ProcessKey>();
        for (var i = 0; i < targets.Count; i++)
        {
            foreach (var child in all.Where(p => p.ParentKey == targets[i]))
            {
                if (_policy.Evaluate(child.Key).MayOperate)
                {
                    targets.Add(child.Key);
                }
                else
                {
                    skipped.Add(child.Key);
                }
            }
        }

        return Task.FromResult(new TerminatePlan(root, targets, skipped));
    }

    public Task<OperationResult> TerminateTreeAsync(TerminatePlan plan, CancellationToken ct = default) =>
        Task.FromResult(OperationResult.Success($"{plan.Targets.Count} process(es) terminated (fake)"));

    public Task<OperationResult> SetPriorityAsync(ProcessKey key, SettablePriority priority, CancellationToken ct = default) =>
        Guarded(key, $"priority set to {priority}");

    public Task<OperationResult> SetEfficiencyModeAsync(ProcessKey key, bool enabled, CancellationToken ct = default) =>
        Guarded(key, enabled ? "efficiency mode on" : "efficiency mode off");

    private Task<OperationResult> Guarded(ProcessKey key, string done)
    {
        var verdict = _policy.Evaluate(key);
        return Task.FromResult(verdict.MayOperate
            ? OperationResult.Success(done + " (fake)")
            : OperationResult.Fail(OperationCode.Protected, verdict.Reason ?? "protected"));
    }
}

public sealed class FakeServiceManager : IServiceManager, IFakeImplementation
{
    private readonly List<ServiceInfo> _services =
    [
        new("Spooler", "Print Spooler", ServiceState.Running, ServiceStartType.Automatic, 3120, Value.Ok("Loads files to memory for later printing.", "fake"), ["RPCSS", "http"], ["Fax"]),
        new("Dnscache", "DNS Client", ServiceState.Running, ServiceStartType.Automatic, 1100, Value.Ok("Caches Domain Name System names.", "fake"), ["nsi"], []),
        new("wuauserv", "Windows Update", ServiceState.Stopped, ServiceStartType.Manual, null, Value.Ok("Detects, downloads and installs updates.", "fake"), ["rpcss"], []),
        new("Fax", "Fax", ServiceState.Stopped, ServiceStartType.Manual, null, Value.NotCollected<string>("fake"), ["Spooler"], []),
    ];

    public Task<IReadOnlyList<ServiceInfo>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ServiceInfo>>(_services.ToArray());

    public Task<OperationResult> StartAsync(string name, CancellationToken ct = default) => Set(name, ServiceState.Running, "started");

    public Task<OperationResult> StopAsync(string name, CancellationToken ct = default) => Set(name, ServiceState.Stopped, "stopped");

    public async Task<OperationResult> RestartAsync(string name, CancellationToken ct = default)
    {
        var stop = await StopAsync(name, ct).ConfigureAwait(false);
        return stop.IsSuccess ? await StartAsync(name, ct).ConfigureAwait(false) : stop;
    }

    private Task<OperationResult> Set(string name, ServiceState state, string done)
    {
        var index = _services.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return Task.FromResult(OperationResult.Fail(OperationCode.NotFound, $"no service named '{name}'"));
        }

        _services[index] = _services[index] with { State = state };
        return Task.FromResult(OperationResult.Success($"{name} {done} (fake)"));
    }
}

public sealed class FakeStartupManager : IStartupManager, IFakeImplementation
{
    private readonly List<StartupItem> _items =
    [
        new("runHkcu:Discord", StartupProvider.RunHkcu, "Discord", "\"C:\\Users\\user\\AppData\\Local\\Discord\\Update.exe\" --processStart Discord.exe", StartupState.Enabled, true, false),
        new("runHkcu:Steam", StartupProvider.RunHkcu, "Steam", "\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent", StartupState.Disabled, true, false),
        new("runHklm:SecurityHealth", StartupProvider.RunHklm, "SecurityHealth", "%windir%\\system32\\SecurityHealthSystray.exe", StartupState.Enabled, true, true),
        new("scheduledTask:OneDrive Standalone Update", StartupProvider.ScheduledTask, "OneDrive Standalone Update", "OneDriveStandaloneUpdater.exe", StartupState.Unknown, false, false, "list only in v1"),
    ];

    public Task<IReadOnlyList<StartupItem>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StartupItem>>(_items.ToArray());

    public Task<OperationResult> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0)
        {
            return Task.FromResult(OperationResult.Fail(OperationCode.NotFound, $"no startup item '{id}'"));
        }

        if (!_items[index].CanToggle)
        {
            return Task.FromResult(OperationResult.Fail(OperationCode.Unsupported, "this entry is list-only"));
        }

        _items[index] = _items[index] with { State = enabled ? StartupState.Enabled : StartupState.Disabled };
        return Task.FromResult(OperationResult.Success((enabled ? "enabled" : "disabled") + " (fake)"));
    }
}

public sealed class FakeSessionProvider : ISessionProvider, IFakeImplementation
{
    public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SessionInfo>>(
        [
            new SessionInfo(0, "", SessionConnectState.Other, "Services"),
            new SessionInfo(1, "user", SessionConnectState.Active, "Console"),
        ]);
}

public sealed class FakeHelperClient : IHelperClient, IFakeImplementation
{
    public Task<HelperOutcome> RunAsync(HelperRequest request, CancellationToken ct = default) =>
        Task.FromResult(new HelperOutcome(
            OperationCode.Ok,
            false,
            new HelperResult(request.Nonce, HelperOperations.Get(request.Op).Name, OperationCode.Ok, "fake helper: nothing was run"),
            "fake"));
}

public sealed class FakeTaskLauncher : ITaskLauncher, IFakeImplementation
{
    public OperationResult Run(string commandLine, bool elevated) =>
        string.IsNullOrWhiteSpace(commandLine)
            ? OperationResult.Fail(OperationCode.InvalidArguments, "empty command line")
            : OperationResult.Success($"would run '{commandLine}'{(elevated ? " as administrator" : "")} (fake)");
}

public static class ManagementModule
{
    /// <summary>Owner D: replace the Fakes with the real implementations here (keep the Fakes for options.ForceFakes).</summary>
    public static void Register(ServiceRegistry registry, PcMieruOptions options)
    {
        Strings.Register(ManagementStrings.All);
        var policy = new FakeProtectionPolicy();
        registry.Set<IProtectionPolicy>(policy);
        registry.Set<IProcessControl>(new FakeProcessControl(policy));
        registry.Set<IServiceManager>(new FakeServiceManager());
        registry.Set<IStartupManager>(new FakeStartupManager());
        registry.Set<ISessionProvider>(new FakeSessionProvider());
        registry.Set<IHelperClient>(new FakeHelperClient());
        registry.Set<ITaskLauncher>(new FakeTaskLauncher());
    }
}

public static class ManagementStrings
{
    public static readonly StringEntry[] All =
    [
        new("mgmt.protected.reason", "保護されたプロセスのため操作できません", "This process is protected and cannot be changed"),
        new("mgmt.protected.unknown", "守られているかを確かめられないため操作しません", "Cannot confirm the process is not protected, so nothing was done"),
        new("mgmt.uac.declined", "管理者への昇格が拒否されました", "The administrator prompt was declined"),
    ];
}
