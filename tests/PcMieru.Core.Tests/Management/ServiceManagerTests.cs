using System.Diagnostics;
using PcMieru.Core.Management;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.Management;

internal sealed class RecordingHelper(Func<HelperRequest, HelperOutcome>? script = null) : IHelperClient
{
    public List<HelperRequest> Requests { get; } = [];

    public Task<HelperOutcome> RunAsync(HelperRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(script?.Invoke(request) ?? new HelperOutcome(OperationCode.Ok, false, null));
    }
}

internal sealed class FakeCatalog(params RawService[] services) : IServiceCatalog
{
    public IReadOnlyList<RawService> Read(CancellationToken ct) => services;
}

/// <summary>Ends the wait the first time it is asked to sleep, the way a user pressing Cancel would.</summary>
internal sealed class CancellingClock(CancellationTokenSource source) : ITransitionClock
{
    public long NowMs { get; private set; } = 1_000;

    public void Delay(int milliseconds)
    {
        NowMs += milliseconds;
        source.Cancel();
    }
}

public class ServiceManagerTests
{
    private static RawService Raw(string name, string[]? dependsOn = null, string group = "", uint state = 4, uint startType = 3, uint pid = 0) =>
        new(name, name + " display", state, startType, false, pid, Value.Ok("about " + name, "scm"), dependsOn ?? [], group);

    private static (ServiceManager Manager, FakeServiceApi Api, RecordingHelper Helper, FakeClock Clock) Build(
        ServiceSnapshot initial, bool elevated, Func<HelperRequest, HelperOutcome>? helperScript = null)
    {
        Strings.Register(ManagementStrings.All);
        var clock = new FakeClock();
        var api = new FakeServiceApi(clock) { Initial = initial };
        var helper = new RecordingHelper(helperScript);
        var manager = new ServiceManager(new FakeCatalog(), api, clock, helper, () => elevated, null);
        return (manager, api, helper, clock);
    }

    [Fact]
    public void Win32_state_numbers_map_to_the_states_and_anything_else_is_unknown()
    {
        Assert.Equal(ServiceState.Stopped, ServiceManager.MapState(1));
        Assert.Equal(ServiceState.StartPending, ServiceManager.MapState(2));
        Assert.Equal(ServiceState.StopPending, ServiceManager.MapState(3));
        Assert.Equal(ServiceState.Running, ServiceManager.MapState(4));
        Assert.Equal(ServiceState.ContinuePending, ServiceManager.MapState(5));
        Assert.Equal(ServiceState.PausePending, ServiceManager.MapState(6));
        Assert.Equal(ServiceState.Paused, ServiceManager.MapState(7));
        Assert.Equal(ServiceState.Unknown, ServiceManager.MapState(0));
        Assert.Equal(ServiceState.Unknown, ServiceManager.MapState(99));
    }

    [Fact]
    public void Start_type_numbers_map_and_delayed_applies_to_automatic_only()
    {
        Assert.Equal(ServiceStartType.Boot, ServiceManager.MapStartType(0, false));
        Assert.Equal(ServiceStartType.System, ServiceManager.MapStartType(1, false));
        Assert.Equal(ServiceStartType.Automatic, ServiceManager.MapStartType(2, false));
        Assert.Equal(ServiceStartType.AutomaticDelayed, ServiceManager.MapStartType(2, true));
        Assert.Equal(ServiceStartType.Manual, ServiceManager.MapStartType(3, true));
        Assert.Equal(ServiceStartType.Disabled, ServiceManager.MapStartType(4, false));
        Assert.Equal(ServiceStartType.Unknown, ServiceManager.MapStartType(uint.MaxValue, false));
    }

    [Fact]
    public void Dependents_are_found_through_other_services_and_never_include_the_service_itself()
    {
        var services = new[]
        {
            Raw("Base"),
            Raw("Mid", ["Base"]),
            Raw("Top", ["Mid"]),
            Raw("Other", ["Base"]),
            Raw("Loner"),
        };
        var map = ServiceManager.Invert(services);
        Assert.Equal(["Mid", "Other", "Top"], map["Base"]);
        Assert.Equal(["Top"], map["Mid"]);
        Assert.Empty(map["Top"]);
        Assert.Empty(map["Loner"]);
    }

    [Fact]
    public void A_dependency_on_a_load_order_group_counts_for_every_member_and_names_are_case_blind()
    {
        var services = new[]
        {
            Raw("DriverA", group: "NDIS"),
            Raw("DriverB", group: "NDIS"),
            Raw("Net", ["+NDIS"]),
            Raw("Shout", ["driverA"]),
        };
        var map = ServiceManager.Invert(services);
        Assert.Equal(["Net", "Shout"], map["DriverA"]);
        Assert.Equal(["Net"], map["DriverB"]);
    }

    [Fact]
    public void A_dependency_cycle_ends_and_a_service_that_needs_itself_is_not_its_own_dependent()
    {
        var services = new[] { Raw("A", ["B"]), Raw("B", ["A"]), Raw("Self", ["Self"]) };
        var map = ServiceManager.Invert(services);
        Assert.Equal(["B"], map["A"]);
        Assert.Equal(["A"], map["B"]);
        Assert.Empty(map["Self"]);
    }

    [Fact]
    public async Task The_list_is_sorted_by_name_and_carries_state_pid_and_dependents()
    {
        Strings.Register(ManagementStrings.All);
        var catalog = new FakeCatalog(
            Raw("zeta", state: 1, startType: 4),
            Raw("Alpha", state: 4, startType: 2, pid: 812),
            Raw("beta", ["Alpha"], state: 4, startType: 3, pid: 0));
        var manager = new ServiceManager(catalog, new FakeServiceApi(new FakeClock()), new FakeClock(), new RecordingHelper(), () => false, null);

        var list = await manager.ListAsync();

        Assert.Equal(["Alpha", "beta", "zeta"], list.Select(s => s.Name));
        var alpha = list[0];
        Assert.Equal(ServiceState.Running, alpha.State);
        Assert.Equal(ServiceStartType.Automatic, alpha.StartType);
        Assert.Equal(812u, alpha.Pid);
        Assert.Equal(["beta"], alpha.Dependents);
        Assert.Null(list[1].Pid);
        Assert.Equal(["Alpha"], list[1].DependsOn);
        Assert.Equal(ServiceStartType.Disabled, list[2].StartType);
        Assert.Equal(ServiceState.Stopped, list[2].State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("tab\there")]
    public async Task A_bad_name_is_refused_without_asking_anything(string name)
    {
        var (manager, api, helper, _) = Build(FakeServiceApi.Stopped(), elevated: false);
        foreach (var result in new[] { await manager.StartAsync(name), await manager.StopAsync(name), await manager.RestartAsync(name) })
        {
            Assert.Equal(OperationCode.InvalidArguments, result.Code);
        }

        Assert.Empty(helper.Requests);
        Assert.Equal(0, api.StartCalls + api.StopCalls);
    }

    [Fact]
    public async Task A_name_that_is_too_long_or_null_is_refused_too()
    {
        var (manager, _, helper, _) = Build(FakeServiceApi.Stopped(), elevated: false);
        Assert.Equal(OperationCode.InvalidArguments, (await manager.StartAsync(new string('x', 300))).Code);
        Assert.Equal(OperationCode.InvalidArguments, (await manager.StartAsync(null!)).Code);
        Assert.Empty(helper.Requests);
    }

    [Fact]
    public async Task Not_elevated_a_missing_service_or_nothing_to_do_is_answered_without_a_prompt()
    {
        var (manager, api, helper, _) = Build(FakeServiceApi.Stopped(), elevated: false);
        api.QueryError = 1060;
        Assert.Equal(OperationCode.NotFound, (await manager.StartAsync("Nope")).Code);
        Assert.Empty(helper.Requests);

        var (runningManager, _, runningHelper, _) = Build(FakeServiceApi.Running(), elevated: false);
        var already = await runningManager.StartAsync("Demo");
        Assert.Equal(OperationCode.Ok, already.Code);
        Assert.Empty(runningHelper.Requests);

        var (stoppedManager, _, stoppedHelper, _) = Build(FakeServiceApi.Stopped(), elevated: false);
        Assert.Equal(OperationCode.Ok, (await stoppedManager.StopAsync("Demo")).Code);
        Assert.Empty(stoppedHelper.Requests);
    }

    [Fact]
    public async Task Not_elevated_a_real_action_goes_to_the_helper_once_with_exactly_the_request()
    {
        var (manager, api, helper, _) = Build(
            FakeServiceApi.Stopped(),
            elevated: false,
            request => new HelperOutcome(
                OperationCode.Ok,
                false,
                new HelperResult("n", HelperOperations.Get(request.Op).Name, OperationCode.Ok, MessageKeys.ServiceStarted, new() { ["arg.name"] = "Demo" })));

        var result = await manager.StartAsync("Demo");

        Assert.Equal(OperationCode.Ok, result.Code);
        var request = Assert.Single(helper.Requests);
        Assert.Equal(HelperOperation.ServiceStart, request.Op);
        Assert.Equal("Demo", request.ServiceName);
        Assert.Contains("Demo", result.Message);
        Assert.Equal(0, api.StartCalls);
    }

    [Fact]
    public async Task Not_elevated_each_of_the_three_actions_is_its_own_helper_operation()
    {
        var (manager, _, helper, _) = Build(FakeServiceApi.Running(), elevated: false);
        await manager.StopAsync("Demo");
        await manager.RestartAsync("Demo");
        Assert.Equal([HelperOperation.ServiceStop, HelperOperation.ServiceRestart], helper.Requests.Select(r => r.Op));
    }

    [Fact]
    public async Task A_declined_prompt_is_cancelled_and_a_query_that_failed_still_lets_the_helper_decide()
    {
        var (manager, api, helper, _) = Build(FakeServiceApi.Stopped(), elevated: false, _ => new HelperOutcome(OperationCode.Cancelled, true, null));
        api.QueryError = 5;
        var result = await manager.StartAsync("Demo");
        Assert.Equal(OperationCode.Cancelled, result.Code);
        Assert.Single(helper.Requests);
    }

    [Fact]
    public async Task Elevated_start_runs_in_this_process_with_the_staged_wait_and_never_asks_the_helper()
    {
        var (manager, api, helper, _) = Build(FakeServiceApi.Stopped(), elevated: true);
        api.Script.Add((0, FakeServiceApi.StartPending(1)));
        api.Script.Add((2000, FakeServiceApi.Running()));

        var result = await manager.StartAsync("Demo");

        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.Equal(1, api.StartCalls);
        Assert.Empty(helper.Requests);
    }

    [Fact]
    public async Task Elevated_stop_is_refused_while_a_running_service_depends_on_it()
    {
        var (manager, api, _, _) = Build(FakeServiceApi.Running(), elevated: true);
        api.Dependents = ["Child1", "Child2"];

        var result = await manager.StopAsync("Demo");

        Assert.Equal(OperationCode.Unsupported, result.Code);
        Assert.Contains("Child1", result.Message);
        Assert.Equal(0, api.StopCalls);
    }

    [Fact]
    public async Task Elevated_restart_stops_then_starts_and_a_failed_start_after_the_stop_is_partial_never_rolled_back()
    {
        var (manager, api, _, _) = Build(FakeServiceApi.Running(), elevated: true);
        api.Script.Add((0, FakeServiceApi.Stopped()));
        // After the stop the next start fails: the service goes back to stopped with an exit code.
        api.StartError = 1053;

        var result = await manager.RestartAsync("Demo");

        Assert.Equal(OperationCode.PartialSuccess, result.Code);
        Assert.Equal(1, api.StopCalls);
        Assert.Equal(1, api.StartCalls);
    }

    [Fact]
    public async Task Cancelling_during_the_wait_ends_it_and_leaves_the_service_as_it_is()
    {
        Strings.Register(ManagementStrings.All);
        using var source = new CancellationTokenSource();
        var clock = new CancellingClock(source);
        var fakeClock = new FakeClock();
        var api = new FakeServiceApi(fakeClock) { Initial = FakeServiceApi.Stopped() };
        api.Script.Add((0, FakeServiceApi.StartPending(1)));
        var manager = new ServiceManager(new FakeCatalog(), api, clock, new RecordingHelper(), () => true, null);

        var result = await manager.StartAsync("Demo", source.Token);

        Assert.Equal(OperationCode.Cancelled, result.Code);
        Assert.Equal(1, api.StartCalls);
        Assert.Equal(0, api.StopCalls);
    }

    [Fact]
    public async Task A_call_that_was_cancelled_before_it_began_does_nothing()
    {
        var (manager, api, helper, _) = Build(FakeServiceApi.Stopped(), elevated: false);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var result = await manager.StartAsync("Demo", source.Token);
        Assert.Equal(OperationCode.Cancelled, result.Code);
        Assert.Empty(helper.Requests);
        Assert.Equal(0, api.StartCalls);
    }

    [Trait("Category", "RealMachine")]
    [Fact]
    public async Task Real_list_agrees_with_Get_Service_on_this_PC()
    {
        // Read-only. The SCM is asked as an ordinary caller: no start, no stop, no prompt.
        var manager = new ServiceManager(new RecordingHelper());
        var ours = (await manager.ListAsync()).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        Assert.True(ours.Count > 50, "services found: " + ours.Count);

        var reference = ReadGetService();
        Assert.NotNull(reference); // Windows PowerShell 5.1 must answer: a silent skip would hide a broken comparison

        var common = reference.Keys.Count(ours.ContainsKey);
        Assert.True(common >= reference.Count * 0.95, $"only {common} of {reference.Count} services of Get-Service are in the list");

        var disagreements = new List<string>();
        foreach (var (name, expected) in reference)
        {
            if (!ours.TryGetValue(name, out var info))
            {
                continue;
            }

            // A service can change state between the two reads, so only the stable facts are held against it.
            if (expected.StartType is { Length: > 0 } startType && !StartTypeMatches(startType, info.StartType))
            {
                disagreements.Add($"{name}: Get-Service says {startType}, we say {info.StartType}");
            }
        }

        Assert.True(disagreements.Count <= 3, string.Join("; ", disagreements));

        // Two services that are always running on a normal Windows.
        Assert.Equal(ServiceState.Running, ours["RpcSs"].State);
        Assert.Equal(ServiceState.Running, ours["EventLog"].State);
        Assert.NotNull(ours["RpcSs"].Pid);
    }

    [Trait("Category", "RealMachine")]
    [Fact]
    public async Task Real_descriptions_are_resolved_text_and_equal_what_WMI_says_for_well_known_services()
    {
        var ours = (await new ServiceManager(new RecordingHelper()).ListAsync()).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        // A description like "@%SystemRoot%\system32\x.dll,-123" must have been turned into the text it points at.
        var unresolved = ours.Values.Where(s => s.Description.TryGet(out var d) && d.StartsWith('@')).Select(s => s.Name).ToArray();
        Assert.True(unresolved.Length <= 5, "descriptions left as resource pointers: " + string.Join(", ", unresolved));
        var described = ours.Values.Count(s => s.Description.IsOk);
        Assert.True(described > ours.Count / 2, $"only {described} of {ours.Count} services have a description");

        var names = new[] { "Spooler", "EventLog", "RpcSs", "Dhcp", "W32Time" };
        var script = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; Get-CimInstance Win32_Service | Where-Object { $_.Name -in '"
            + string.Join("','", names)
            + "' } | ForEach-Object { $_.Name + '|' + ($_.Description -replace '\\s+', ' ') }";
        var info = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var process = Process.Start(info)!;
        var text = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(60_000) && process.ExitCode == 0);

        var compared = 0;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bar = line.IndexOf('|');
            var name = line[..bar];
            var wmi = line[(bar + 1)..].Trim();
            if (wmi.Length == 0)
            {
                continue;
            }

            Assert.True(ours[name].Description.TryGet(out var mine), name + " has no description of ours");
            Assert.Equal(wmi, System.Text.RegularExpressions.Regex.Replace(mine, @"\s+", " ").Trim());
            compared++;
        }

        Assert.True(compared >= 3, "compared only " + compared);
    }

    private static bool StartTypeMatches(string powerShell, ServiceStartType ours) => powerShell switch
    {
        "Automatic" => ours is ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed,
        "Manual" => ours == ServiceStartType.Manual,
        "Disabled" => ours == ServiceStartType.Disabled,
        "Boot" => ours == ServiceStartType.Boot,
        "System" => ours == ServiceStartType.System,
        _ => true,
    };

    private static Dictionary<string, (string Status, string StartType)>? ReadGetService()
    {
        try
        {
            var info = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                "-NoProfile -NonInteractive -Command \"Get-Service | ForEach-Object { $_.Name + '|' + $_.Status + '|' + $_.StartType }\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var text = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000) || process.ExitCode != 0)
            {
                return null;
            }

            var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('|');
                if (parts.Length == 3)
                {
                    result[parts[0]] = (parts[1], parts[2]);
                }
            }

            return result.Count == 0 ? null : result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}

public class SessionProviderTests
{
    [Fact]
    public void Terminal_services_states_map_and_anything_else_is_other()
    {
        Assert.Equal(SessionConnectState.Active, SessionProvider.MapState(0));
        Assert.Equal(SessionConnectState.Connected, SessionProvider.MapState(1));
        Assert.Equal(SessionConnectState.Disconnected, SessionProvider.MapState(4));
        Assert.Equal(SessionConnectState.Idle, SessionProvider.MapState(5));
        Assert.Equal(SessionConnectState.Other, SessionProvider.MapState(2));
        Assert.Equal(SessionConnectState.Other, SessionProvider.MapState(9));
    }

    [Fact]
    public async Task Sessions_are_listed_by_id_and_an_empty_window_station_is_null()
    {
        var provider = new SessionProvider(() =>
        [
            new RawSession(3, "RDP-Tcp#1", 0, "carol"),
            new RawSession(0, "Services", 4, ""),
            new RawSession(1, "", 0, "alice"),
        ]);

        var list = await provider.ListAsync();

        Assert.Equal([0u, 1u, 3u], list.Select(s => s.SessionId));
        Assert.Equal(SessionConnectState.Disconnected, list[0].State);
        Assert.Equal("Services", list[0].WindowStation);
        Assert.Null(list[1].WindowStation);
        Assert.Equal("alice", list[1].UserName);
    }

    [Trait("Category", "RealMachine")]
    [Fact]
    public async Task Real_list_has_our_own_session_with_our_own_user_name()
    {
        var list = await new SessionProvider().ListAsync();
        var own = Process.GetCurrentProcess().SessionId;
        var session = list.SingleOrDefault(s => s.SessionId == (uint)own);
        Assert.NotNull(session);
        Assert.Equal(Environment.UserName, session!.UserName, ignoreCase: true);
        Assert.Contains(session.State, new[] { SessionConnectState.Active, SessionConnectState.Connected });
        Assert.Contains(list, s => s.SessionId == 0);
    }
}

internal sealed class FakeShellHost : IShellHost
{
    public bool IsElevated { get; set; }

    public List<ProcessKey> Explorers { get; set; } = [new ProcessKey(100, 1)];

    public Dictionary<uint, OperationCode> TerminateCodes { get; } = [];

    public List<uint> Terminated { get; } = [];

    /// <summary>Delays after which a new explorer exists by itself (Windows brings the shell back). Null: never.</summary>
    public int? WindowsRestartsAfterMs { get; set; }

    public int StartCalls { get; private set; }

    public bool StartWorks { get; set; } = true;

    public int ElapsedMs { get; private set; }

    public Action? OnDelay { get; set; }

    public ActionResult Terminate(ProcessKey key)
    {
        Terminated.Add(key.Pid);
        var code = TerminateCodes.TryGetValue(key.Pid, out var c) ? c : OperationCode.Ok;
        return ActionResult.Of(code, code == OperationCode.Ok ? MessageKeys.ProcessTerminated : MessageKeys.ProcessDenied);
    }

    public IReadOnlyList<ProcessKey> FindOwnExplorers() => Explorers;

    public bool HasNewExplorer(IReadOnlyCollection<uint> oldPids) =>
        (WindowsRestartsAfterMs is { } after && ElapsedMs >= after) || (StartCalls > 0 && StartWorks);

    public bool StartExplorer()
    {
        StartCalls++;
        return StartWorks;
    }

    public void Delay(int milliseconds, CancellationToken ct)
    {
        ElapsedMs += milliseconds;
        OnDelay?.Invoke();
    }
}

public class ShellActionsTests
{
    [Fact]
    public async Task Elevated_it_is_refused_and_nothing_is_touched()
    {
        Strings.Register(ManagementStrings.All);
        var host = new FakeShellHost { IsElevated = true };
        var result = await new ShellActions(host).RestartExplorerAsync();
        Assert.Equal(OperationCode.Unsupported, result.Code);
        Assert.Empty(host.Terminated);
        Assert.Equal(0, host.StartCalls);
    }

    [Fact]
    public async Task When_windows_brings_the_shell_back_we_start_nothing()
    {
        Strings.Register(ManagementStrings.All);
        var host = new FakeShellHost { Explorers = [new ProcessKey(100, 1), new ProcessKey(101, 2)], WindowsRestartsAfterMs = 1500 };
        var result = await new ShellActions(host).RestartExplorerAsync();
        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.Equal([100u, 101u], host.Terminated);
        Assert.Equal(0, host.StartCalls);
    }

    [Fact]
    public async Task When_windows_does_not_we_start_it_once_ourselves()
    {
        Strings.Register(ManagementStrings.All);
        var host = new FakeShellHost();
        var result = await new ShellActions(host).RestartExplorerAsync();
        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.Equal(1, host.StartCalls);
        Assert.InRange(host.ElapsedMs, 8000, 9000);
    }

    [Fact]
    public async Task If_the_shell_cannot_be_started_it_is_an_error()
    {
        Strings.Register(ManagementStrings.All);
        var host = new FakeShellHost { StartWorks = false };
        var result = await new ShellActions(host).RestartExplorerAsync();
        Assert.Equal(OperationCode.Error, result.Code);
    }

    [Fact]
    public async Task If_nothing_could_be_ended_nothing_is_started()
    {
        Strings.Register(ManagementStrings.All);
        var host = new FakeShellHost();
        host.TerminateCodes[100] = OperationCode.Denied;
        var result = await new ShellActions(host).RestartExplorerAsync();
        Assert.Equal(OperationCode.Denied, result.Code);
        Assert.Equal(0, host.StartCalls);
        Assert.Equal(0, host.ElapsedMs);
    }

    [Fact]
    public async Task If_one_of_two_ended_it_goes_on()
    {
        Strings.Register(ManagementStrings.All);
        var host = new FakeShellHost { Explorers = [new ProcessKey(100, 1), new ProcessKey(101, 2)], WindowsRestartsAfterMs = 250 };
        host.TerminateCodes[100] = OperationCode.Denied;
        var result = await new ShellActions(host).RestartExplorerAsync();
        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.Equal([100u, 101u], host.Terminated);
    }

    [Fact]
    public async Task With_no_explorer_running_it_is_started()
    {
        Strings.Register(ManagementStrings.All);
        var host = new FakeShellHost { Explorers = [] };
        var result = await new ShellActions(host).RestartExplorerAsync();
        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.Empty(host.Terminated);
        Assert.Equal(1, host.StartCalls);
    }

    [Fact]
    public async Task Cancelling_while_waiting_does_not_start_anything()
    {
        Strings.Register(ManagementStrings.All);
        using var source = new CancellationTokenSource();
        var host = new FakeShellHost { OnDelay = source.Cancel };
        var result = await new ShellActions(host).RestartExplorerAsync(source.Token);
        Assert.Equal(OperationCode.Cancelled, result.Code);
        Assert.Equal(0, host.StartCalls);
    }

    [Trait("Category", "RealMachine")]
    [Fact]
    public void Real_host_finds_our_own_explorer_in_a_normal_session_without_touching_it()
    {
        // Read-only: only the lookup is run, never Terminate or StartExplorer.
        var host = new SystemShellHost();
        var found = host.FindOwnExplorers();
        var running = Process.GetProcessesByName("explorer").Count(p =>
        {
            using (p)
            {
                return p.SessionId == Process.GetCurrentProcess().SessionId;
            }
        });
        Assert.Equal(running, found.Count);
    }
}
