using Microsoft.Win32;

namespace PcMieru.Wire;

/// <summary>
/// Everything the executor touches outside itself. The real one points at the machine; the tests pass fakes, so that
/// service and startup logic can be exercised without a single real service or startup entry being changed.
/// </summary>
public sealed class HelperEnvironment
{
    public required SelfIdentity Self { get; init; }

    public required string SystemDirectory { get; init; }

    public required IServiceControlApi Services { get; init; }

    public required ITransitionClock Clock { get; init; }

    public ServiceWaitOptions? WaitOptions { get; init; }

    /// <summary>The registry root the StartupApproved path is below. Each call gets its own key (the executor disposes it).</summary>
    public required Func<RegistryKey> OpenStartupRoot { get; init; }

    public string StartupApprovedRoot { get; init; } = StartupApprovedCodec.ApprovedRoot;

    /// <summary>Whether the startup entry itself exists (the Helper never creates an approval for something that is not there).</summary>
    public required Func<StartupProvider, string, bool> EntryExists { get; init; }

    public required Func<long> NowFileTime { get; init; }

    /// <summary>The machine: SCM, HKLM (64-bit view), this process's identity.</summary>
    public static HelperEnvironment Real() => new()
    {
        Self = ProcessOperations.CurrentSelf(),
        SystemDirectory = Environment.SystemDirectory,
        Services = new ScmServiceApi(),
        Clock = SystemTransitionClock.Instance,
        OpenStartupRoot = () => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        EntryExists = StartupEntryProbe.Exists,
        NowFileTime = () => DateTime.UtcNow.ToFileTimeUtc(),
    };
}

/// <summary>
/// Runs one already-parsed <see cref="HelperRequest"/>. The table is closed: every operation is one of the nine, each
/// one re-checks its own preconditions (process identity and protection, service dependents, that the startup entry
/// exists), and nothing here can run a program, write a file or open a socket.
/// </summary>
public static class HelperExecutor
{
    public static ActionResult Execute(HelperRequest request, HelperEnvironment env)
    {
        switch (request.Op)
        {
            case HelperOperation.ServiceStart:
                return ServiceTransition.Start(env.Services, env.Clock, request.ServiceName!, env.WaitOptions);

            case HelperOperation.ServiceStop:
                return ServiceTransition.Stop(env.Services, env.Clock, request.ServiceName!, env.WaitOptions);

            case HelperOperation.ServiceRestart:
                return ServiceTransition.Restart(env.Services, env.Clock, request.ServiceName!, env.WaitOptions);

            case HelperOperation.StartupHklmSet:
                return SetStartup(request, env);

            case HelperOperation.ProcessTerminate:
                return ProcessOperations.Terminate(request.Pid!.Value, request.CreateTime!.Value, env.Self, env.SystemDirectory);

            case HelperOperation.ProcessPriority:
                return ProcessOperations.SetPriority(request.Pid!.Value, request.CreateTime!.Value, request.Priority!.Value, env.Self, env.SystemDirectory);

            case HelperOperation.ProcessEfficiency:
                {
                    EfficiencyRestore? restore = request.PreviousPriorityClass is { } priority
                        ? new EfficiencyRestore(priority, request.PreviousThrottleControl ?? 0, request.PreviousThrottleState ?? 0)
                        : null;
                    return ProcessOperations.SetEfficiency(
                        request.Pid!.Value, request.CreateTime!.Value, request.Enable == true, restore, env.Self, env.SystemDirectory);
                }

            case HelperOperation.SmartRead:
                return DiskOperations.SmartRead(request.DriveNumber!.Value);

            case HelperOperation.NvmeHealth:
                return DiskOperations.NvmeHealth(request.DriveNumber!.Value);

            default:
                return ActionResult.Of(OperationCode.Unsupported, MessageKeys.HelperUnexpectedExit, ("code", "op-" + request.Op));
        }
    }

    private static ActionResult SetStartup(HelperRequest request, HelperEnvironment env)
    {
        var provider = request.Provider!.Value;
        var name = request.ValueName!;
        if (provider is not (StartupProvider.RunHklm or StartupProvider.RunHklm32 or StartupProvider.StartupFolderCommon))
        {
            return ActionResult.Of(OperationCode.Unsupported, MessageKeys.StartupListOnly);
        }

        if (!env.EntryExists(provider, name))
        {
            return ActionResult.Of(OperationCode.NotFound, MessageKeys.StartupNotFound, ("name", name));
        }

        using var root = env.OpenStartupRoot();
        return StartupApprovedToggle.Apply(root, env.StartupApprovedRoot, provider, name, request.Enable == true, env.NowFileTime(), out _);
    }
}
