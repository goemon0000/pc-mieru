using System.Runtime.InteropServices;
using PcMieru.Wire;

namespace PcMieru.Core.Management;

/// <summary>One service as the service control manager reports it, before it is turned into <see cref="ServiceInfo"/>.</summary>
internal sealed record RawService(
    string Name,
    string DisplayName,
    uint State,
    uint StartType,
    bool DelayedAutoStart,
    uint ProcessId,
    Value<string> Description,
    IReadOnlyList<string> DependsOn,
    string Group);

/// <summary>Where the list of services comes from. The real one reads the SCM; tests supply their own.</summary>
internal interface IServiceCatalog
{
    IReadOnlyList<RawService> Read(CancellationToken ct);
}

/// <summary>
/// Reads every Win32 service from the local service control manager: EnumServicesStatusEx for the list, then for each
/// one its configuration, description and delayed-start flag. Read-only (SC_MANAGER_ENUMERATE_SERVICE and
/// SERVICE_QUERY_CONFIG, which a standard user has).
/// </summary>
internal sealed unsafe class ScmServiceCatalog : IServiceCatalog
{
    private const int ScEnumProcessInfo = 0;
    private const uint ConfigDescription = 1;
    private const uint ConfigDelayedAutoStart = 3;

    public IReadOnlyList<RawService> Read(CancellationToken ct)
    {
        var result = new List<RawService>(400);
        using var manager = ManagementNative.OpenSCManager(
            null, null, ManagementNative.SC_MANAGER_CONNECT | ManagementNative.SC_MANAGER_ENUMERATE_SERVICE);
        if (manager.IsInvalid)
        {
            return result;
        }

        var buffer = new byte[256 * 1024];
        uint resume = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            bool ok;
            uint returned;
            uint needed;
            fixed (byte* p = buffer)
            {
                ok = ManagementNative.EnumServicesStatusEx(
                    manager, ScEnumProcessInfo, ManagementNative.SERVICE_WIN32, ManagementNative.SERVICE_STATE_ALL,
                    p, (uint)buffer.Length, out needed, out returned, ref resume, null);
                if (!ok && Marshal.GetLastPInvokeError() != ManagementNative.ERROR_MORE_DATA)
                {
                    return result;
                }

                // The name and display name pointers point into this buffer: they are read before it is unpinned.
                var entries = (MgmtEnumServiceStatusProcess*)p;
                for (var i = 0; i < returned; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    result.Add(ReadOne(manager, entries[i]));
                }
            }

            if (ok)
            {
                return result;
            }

            if (returned == 0)
            {
                // More data but nothing fitted: a bigger buffer, within reason.
                if (buffer.Length >= 8 * 1024 * 1024)
                {
                    return result;
                }

                buffer = new byte[Math.Max((int)Math.Min(needed, 4 * 1024 * 1024) + 4096, buffer.Length * 2)];
            }
        }
    }

    private static RawService ReadOne(MgmtScHandle manager, MgmtEnumServiceStatusProcess entry)
    {
        var name = ManagementNative.ReadString(entry.ServiceName);
        var display = Resolve(ManagementNative.ReadString(entry.DisplayName));
        var status = entry.Status;

        uint startType = uint.MaxValue;
        var dependsOn = new List<string>();
        var group = "";
        var delayed = false;
        var description = Value.NotCollected<string>("scm");

        using var service = ManagementNative.OpenService(manager, name, ManagementNative.SERVICE_QUERY_CONFIG);
        if (service.IsInvalid)
        {
            var status2 = Marshal.GetLastPInvokeError() == 5 ? ValueStatus.Denied : ValueStatus.Error;
            description = Value.Missing<string>(status2, "scm");
        }
        else
        {
            var config = QueryConfig(service);
            if (config is { } c)
            {
                startType = c.StartType;
                dependsOn = c.DependsOn;
                group = c.Group;
            }

            description = QueryDescription(service);
            delayed = startType == 2 && QueryDelayed(service);
        }

        return new RawService(name, display, status.CurrentState, startType, delayed, status.ProcessId, description, dependsOn, group);
    }

    private static (uint StartType, List<string> DependsOn, string Group)? QueryConfig(MgmtScHandle service)
    {
        var size = 8192u;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                if (ManagementNative.QueryServiceConfig(service, p, size, out var needed))
                {
                    var config = *(MgmtQueryServiceConfig*)p;
                    return (config.StartType, ManagementNative.ReadMultiString(config.Dependencies), ManagementNative.ReadString(config.LoadOrderGroup));
                }

                if (Marshal.GetLastPInvokeError() != ManagementNative.ERROR_MORE_DATA)
                {
                    return null;
                }

                size = needed;
            }
        }

        return null;
    }

    private static Value<string> QueryDescription(MgmtScHandle service)
    {
        var size = 4096u;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                if (ManagementNative.QueryServiceConfig2(service, ConfigDescription, p, size, out var needed))
                {
                    var text = ManagementNative.ReadString(*(nint*)p);
                    text = Resolve(text);
                    return text.Length == 0
                        ? Value.Missing<string>(ValueStatus.Unsupported, "scm", "no description")
                        : Value.Ok(text, "scm");
                }

                var error = Marshal.GetLastPInvokeError();
                if (error != ManagementNative.ERROR_MORE_DATA)
                {
                    return Value.Missing<string>(error == 5 ? ValueStatus.Denied : ValueStatus.Error, "scm");
                }

                size = needed;
            }
        }

        return Value.Missing<string>(ValueStatus.Error, "scm");
    }

    private static bool QueryDelayed(MgmtScHandle service)
    {
        var buffer = stackalloc byte[16];
        return ManagementNative.QueryServiceConfig2(service, ConfigDelayedAutoStart, buffer, 16, out _) && *(int*)buffer != 0;
    }

    /// <summary>"@%SystemRoot%\system32\x.dll,-123" is a pointer to a string resource: resolve it, or keep the text.</summary>
    private static string Resolve(string text)
    {
        if (text.Length == 0 || text[0] != '@')
        {
            return text;
        }

        var output = stackalloc char[512];
        return ManagementNative.SHLoadIndirectString(text, output, 512, 0) == 0 ? new string(output) : text;
    }
}

/// <summary>
/// The real <see cref="IServiceManager"/>. Listing is read-only. Start, stop and restart run as the staged wait of Wire
/// (<see cref="ServiceTransition"/>): in this process when it is already elevated, otherwise through the Helper (one
/// UAC prompt for the one action the user asked for). A stop is refused while a running service depends on the target,
/// and nothing is ever rolled back by itself.
/// </summary>
public sealed class ServiceManager : IServiceManager
{
    private const int ErrorServiceDoesNotExist = 1060;

    private readonly IServiceCatalog _catalog;
    private readonly IServiceControlApi _api;
    private readonly ITransitionClock _clock;
    private readonly IHelperClient _helper;
    private readonly Func<bool> _isElevated;
    private readonly ServiceWaitOptions? _options;

    public ServiceManager(IHelperClient helper)
        : this(new ScmServiceCatalog(), new ScmServiceApi(), SystemTransitionClock.Instance, helper, ElevationCheck.IsElevated, null)
    {
    }

    internal ServiceManager(
        IServiceCatalog catalog, IServiceControlApi api, ITransitionClock clock, IHelperClient helper, Func<bool> isElevated, ServiceWaitOptions? options)
    {
        _catalog = catalog;
        _api = api;
        _clock = clock;
        _helper = helper;
        _isElevated = isElevated;
        _options = options;
    }

    public Task<IReadOnlyList<ServiceInfo>> ListAsync(CancellationToken ct = default) =>
        Task.Run(() => List(ct), ct);

    public Task<OperationResult> StartAsync(string name, CancellationToken ct = default) =>
        RunAsync(HelperOperation.ServiceStart, name, ct);

    public Task<OperationResult> StopAsync(string name, CancellationToken ct = default) =>
        RunAsync(HelperOperation.ServiceStop, name, ct);

    public Task<OperationResult> RestartAsync(string name, CancellationToken ct = default) =>
        RunAsync(HelperOperation.ServiceRestart, name, ct);

    private IReadOnlyList<ServiceInfo> List(CancellationToken ct)
    {
        var raw = _catalog.Read(ct);
        var dependents = Invert(raw);
        var list = new List<ServiceInfo>(raw.Count);
        foreach (var s in raw)
        {
            list.Add(new ServiceInfo(
                s.Name,
                s.DisplayName,
                MapState(s.State),
                MapStartType(s.StartType, s.DelayedAutoStart),
                s.ProcessId == 0 ? null : s.ProcessId,
                s.Description,
                s.DependsOn,
                dependents.TryGetValue(s.Name, out var d) ? d : []));
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    internal static ServiceState MapState(uint state) => state switch
    {
        ServiceSnapshot.Stopped => ServiceState.Stopped,
        ServiceSnapshot.StartPending => ServiceState.StartPending,
        ServiceSnapshot.StopPending => ServiceState.StopPending,
        ServiceSnapshot.Running => ServiceState.Running,
        ServiceSnapshot.ContinuePending => ServiceState.ContinuePending,
        ServiceSnapshot.PausePending => ServiceState.PausePending,
        ServiceSnapshot.Paused => ServiceState.Paused,
        _ => ServiceState.Unknown,
    };

    /// <summary>SERVICE_BOOT_START 0, SYSTEM 1, AUTO 2 (delayed is a separate flag), DEMAND 3, DISABLED 4.</summary>
    internal static ServiceStartType MapStartType(uint startType, bool delayed) => startType switch
    {
        0 => ServiceStartType.Boot,
        1 => ServiceStartType.System,
        2 => delayed ? ServiceStartType.AutomaticDelayed : ServiceStartType.Automatic,
        3 => ServiceStartType.Manual,
        4 => ServiceStartType.Disabled,
        _ => ServiceStartType.Unknown,
    };

    /// <summary>
    /// For every service the services that need it, directly or through others (stopping it would stop all of them).
    /// A dependency on a load-order group ("+Group") counts as a dependency on every member of that group. Cycles are
    /// harmless, a service is never its own dependent, and each list is sorted by name.
    /// </summary>
    internal static Dictionary<string, IReadOnlyList<string>> Invert(IReadOnlyList<RawService> services)
    {
        var direct = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var members = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in services.Where(s => s.Group.Length > 0))
        {
            if (!members.TryGetValue(s.Group, out var list))
            {
                members[s.Group] = list = [];
            }

            list.Add(s.Name);
        }

        void Link(string target, string dependent)
        {
            if (!direct.TryGetValue(target, out var list))
            {
                direct[target] = list = [];
            }

            list.Add(dependent);
        }

        foreach (var s in services)
        {
            foreach (var dependency in s.DependsOn)
            {
                if (dependency.StartsWith('+'))
                {
                    if (members.TryGetValue(dependency[1..], out var inGroup))
                    {
                        foreach (var member in inGroup)
                        {
                            Link(member, s.Name);
                        }
                    }
                }
                else
                {
                    Link(dependency, s.Name);
                }
            }
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in services)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { s.Name };
            var stack = new Stack<string>();
            stack.Push(s.Name);
            var all = new List<string>();
            while (stack.Count > 0)
            {
                if (!direct.TryGetValue(stack.Pop(), out var next))
                {
                    continue;
                }

                foreach (var d in next)
                {
                    if (seen.Add(d))
                    {
                        all.Add(d);
                        stack.Push(d);
                    }
                }
            }

            all.Sort(StringComparer.OrdinalIgnoreCase);
            result[s.Name] = all;
        }

        return result;
    }

    /// <summary>True for a name the Helper would also accept. Checked here first so a bad name never costs a UAC prompt.</summary>
    internal static bool IsValidName(string? name)
    {
        if (name is null)
        {
            return false;
        }

        const string nonce = "ValidationNonce0000";
        var probe = new HelperRequest(HelperOperation.ServiceStart, nonce, @"C:\" + nonce + ".json") { ServiceName = name };
        return HelperRequest.TryParse(probe.ToArgs(), out _, out _);
    }

    private async Task<OperationResult> RunAsync(HelperOperation op, string name, CancellationToken ct)
    {
        if (!IsValidName(name))
        {
            return OperationMapper.ToResult(OperationCode.InvalidArguments, MessageKeys.ServiceBadName);
        }

        if (ct.IsCancellationRequested)
        {
            return new OperationResult(OperationCode.Cancelled, "");
        }

        if (_isElevated())
        {
            return await Task.Run(() => RunInProcess(op, name, ct), CancellationToken.None).ConfigureAwait(false);
        }

        // Not elevated: say what can be said without a prompt (no such service, nothing to do), and only then ask.
        var early = await Task.Run(() => PreCheck(op, name), CancellationToken.None).ConfigureAwait(false);
        if (early is not null)
        {
            return OperationMapper.ToResult(early);
        }

        var request = new HelperRequest(op, "", "") { ServiceName = name };
        return OperationMapper.FromHelper(await _helper.RunAsync(request, ct).ConfigureAwait(false));
    }

    private OperationResult RunInProcess(HelperOperation op, string name, CancellationToken ct)
    {
        try
        {
            var clock = new CancellableClock(_clock, ct);
            var action = op switch
            {
                HelperOperation.ServiceStart => ServiceTransition.Start(_api, clock, name, _options),
                HelperOperation.ServiceStop => ServiceTransition.Stop(_api, clock, name, _options),
                _ => ServiceTransition.Restart(_api, clock, name, _options),
            };
            return OperationMapper.ToResult(action);
        }
        catch (OperationCanceledException)
        {
            return new OperationResult(OperationCode.Cancelled, "");
        }
    }

    private ActionResult? PreCheck(HelperOperation op, string name)
    {
        var error = _api.Query(name, out var now);
        var named = ("name", name);
        if (error == ErrorServiceDoesNotExist)
        {
            return ActionResult.Of(OperationCode.NotFound, MessageKeys.ServiceNotFound, named);
        }

        if (error != 0)
        {
            return null;
        }

        return op switch
        {
            HelperOperation.ServiceStart when now.State == ServiceSnapshot.Running =>
                ActionResult.Of(OperationCode.Ok, MessageKeys.ServiceAlreadyRunning, named),
            HelperOperation.ServiceStop when now.State == ServiceSnapshot.Stopped =>
                ActionResult.Of(OperationCode.Ok, MessageKeys.ServiceAlreadyStopped, named),
            _ => null,
        };
    }

    /// <summary>The staged wait sleeps through this clock; cancelling ends the wait (the service itself is left as it is).</summary>
    private sealed class CancellableClock(ITransitionClock inner, CancellationToken ct) : ITransitionClock
    {
        public long NowMs => inner.NowMs;

        public void Delay(int milliseconds)
        {
            ct.ThrowIfCancellationRequested();
            inner.Delay(milliseconds);
            ct.ThrowIfCancellationRequested();
        }
    }
}
