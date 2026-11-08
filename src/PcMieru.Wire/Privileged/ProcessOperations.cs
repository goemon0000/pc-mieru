using System.Globalization;
using System.Runtime.InteropServices;

namespace PcMieru.Wire;

/// <summary>One open process whose identity and protection status were checked on this very handle.</summary>
internal sealed class GuardedProcess : IDisposable
{
    public GuardedProcess(SafeKernelHandle handle, ProcessFacts facts)
    {
        Handle = handle;
        Facts = facts;
    }

    public SafeKernelHandle Handle { get; }

    public ProcessFacts Facts { get; }

    public void Dispose() => Handle.Dispose();
}

/// <summary>
/// The one way every process operation starts (SPEC section 6): open the process again by PID, read the creation
/// time from that handle and compare it with the ProcessKey, read the protection facts from the same handle, run
/// the rules, and only then hand the handle to the operation. A bare PID is never acted on.
/// </summary>
public static class ProcessGuard
{
    internal static ProtectionAssessment Assess(SafeKernelHandle handle, uint pid, SelfIdentity self, string systemDirectory)
    {
        var facts = ReadFacts(handle, pid);
        return ProtectionRules.Evaluate(facts, self, systemDirectory);
    }

    internal static ProcessFacts ReadFacts(SafeKernelHandle handle, uint pid) =>
        new(pid, ReadImagePath(handle), ReadCritical(handle), ReadProtection(handle));

    /// <summary>
    /// Opens for <paramref name="access"/> (plus query and synchronize). Returns null on success; otherwise the reason
    /// nothing may be done (gone, replaced, protected, unknown, denied, error).
    /// </summary>
    internal static ActionResult? Open(
        uint pid, long createTime, uint access, SelfIdentity self, string systemDirectory, out GuardedProcess? guarded)
    {
        guarded = null;
        if (pid is 0 or 4)
        {
            return Protected(ProtectionReason.SystemProcess);
        }

        const uint Basic = Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.SYNCHRONIZE;
        var denied = false;
        var handle = Native.OpenProcess(access | Basic, false, pid);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error == Native.ERROR_INVALID_PARAMETER)
            {
                return ActionResult.Of(OperationCode.Gone, MessageKeys.ProcessGone);
            }

            if (error != Native.ERROR_ACCESS_DENIED)
            {
                return ActionResult.Of(OperationCode.Error, MessageKeys.ProcessError, ("code", Code(error)));
            }

            // Not allowed to operate. Look with query rights only, so "protected" and "needs administrator" stay apart.
            handle = Native.OpenProcess(Basic, false, pid);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return ActionResult.Of(OperationCode.Denied, MessageKeys.ProcessDenied);
            }

            denied = true;
        }

        try
        {
            if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _))
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                return ActionResult.Of(OperationCode.Error, MessageKeys.ProcessError, ("code", Code(error)));
            }

            if (created != createTime)
            {
                handle.Dispose();
                return ActionResult.Of(OperationCode.Gone, MessageKeys.ProcessReplaced);
            }

            if (Native.WaitForSingleObject(handle, 0) == Native.WAIT_OBJECT_0)
            {
                handle.Dispose();
                return ActionResult.Of(OperationCode.Gone, MessageKeys.ProcessGone);
            }

            var facts = ReadFacts(handle, pid);
            var verdict = ProtectionRules.Evaluate(facts, self, systemDirectory);
            if (!verdict.MayOperate)
            {
                handle.Dispose();
                return Protected(verdict.Reason);
            }

            if (denied)
            {
                handle.Dispose();
                return ActionResult.Of(OperationCode.Denied, MessageKeys.ProcessDenied);
            }

            guarded = new GuardedProcess(handle, facts);
            return null;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static ActionResult Protected(ProtectionReason reason) => ActionResult.Of(OperationCode.Protected, KeyFor(reason));

    public static string KeyFor(ProtectionReason reason) => reason switch
    {
        ProtectionReason.SystemProcess => MessageKeys.ProtectedSystem,
        ProtectionReason.Self => MessageKeys.ProtectedSelf,
        ProtectionReason.Critical => MessageKeys.ProtectedCritical,
        ProtectionReason.ProtectedProcess => MessageKeys.ProtectedProcess,
        ProtectionReason.WindowsCore => MessageKeys.ProtectedCore,
        _ => MessageKeys.ProtectedUnknown,
    };

    internal static string Code(int error) => error.ToString(CultureInfo.InvariantCulture);

    private static unsafe string? ReadImagePath(SafeKernelHandle handle)
    {
        var buffer = new char[32768];
        fixed (char* p = buffer)
        {
            var size = (uint)buffer.Length;
            return Native.QueryFullProcessImageNameW(handle, 0, p, ref size) && size > 0 ? new string(p, 0, (int)size) : null;
        }
    }

    private static bool? ReadCritical(SafeKernelHandle handle) =>
        Native.IsProcessCritical(handle, out var critical) ? critical : null;

    private static unsafe bool? ReadProtection(SafeKernelHandle handle)
    {
        uint level = 0;
        return Native.GetProcessInformation(handle, Native.ProcessProtectionLevelInfo, &level, sizeof(uint))
            ? level != Native.PROTECTION_LEVEL_NONE
            : null;
    }
}

/// <summary>Values that put a process's priority class and throttling back as they were (SPEC 5.2 efficiency mode).</summary>
public sealed record EfficiencyRestore(uint PriorityClass, uint ThrottleControl, uint ThrottleState)
{
    /// <summary>Nothing was recorded: Normal priority and "the system decides" (Control 0, State 0).</summary>
    public static EfficiencyRestore SystemDefault { get; } = new(Native.NORMAL_PRIORITY_CLASS, 0, 0);
}

/// <summary>The process operations, shared by the Helper (elevated) and Core (as the current user).</summary>
public static class ProcessOperations
{
    private const uint ExitCodeWeUse = 1;
    private const uint TerminateWaitMs = 3000;

    public static SelfIdentity CurrentSelf() =>
        new(Native.GetCurrentProcessId(), Path.GetDirectoryName(Environment.ProcessPath));

    public static ActionResult Terminate(uint pid, long createTime, SelfIdentity self, string systemDirectory)
    {
        var refused = ProcessGuard.Open(pid, createTime, Native.PROCESS_TERMINATE, self, systemDirectory, out var guarded);
        if (refused is not null)
        {
            return refused;
        }

        using (guarded)
        {
            var handle = guarded!.Handle;
            if (!Native.TerminateProcess(handle, ExitCodeWeUse))
            {
                var error = Marshal.GetLastPInvokeError();
                if (Native.WaitForSingleObject(handle, 0) == Native.WAIT_OBJECT_0)
                {
                    return ActionResult.Of(OperationCode.Gone, MessageKeys.ProcessGone);
                }

                return error == Native.ERROR_ACCESS_DENIED
                    ? ActionResult.Of(OperationCode.Denied, MessageKeys.ProcessDenied)
                    : ActionResult.Of(OperationCode.Error, MessageKeys.ProcessError, ("code", ProcessGuard.Code(error)));
            }

            return Native.WaitForSingleObject(handle, TerminateWaitMs) == Native.WAIT_OBJECT_0
                ? ActionResult.Of(OperationCode.Ok, MessageKeys.ProcessTerminated, ("pid", pid.ToString(CultureInfo.InvariantCulture)))
                : ActionResult.Of(OperationCode.Error, MessageKeys.ProcessTerminatePending, ("pid", pid.ToString(CultureInfo.InvariantCulture)));
        }
    }

    public static uint ToPriorityClass(SettablePriority priority) => priority switch
    {
        SettablePriority.Idle => Native.IDLE_PRIORITY_CLASS,
        SettablePriority.BelowNormal => Native.BELOW_NORMAL_PRIORITY_CLASS,
        SettablePriority.Normal => Native.NORMAL_PRIORITY_CLASS,
        SettablePriority.AboveNormal => Native.ABOVE_NORMAL_PRIORITY_CLASS,
        SettablePriority.High => Native.HIGH_PRIORITY_CLASS,
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, "realtime is never offered"),
    };

    public static ActionResult SetPriority(uint pid, long createTime, SettablePriority priority, SelfIdentity self, string systemDirectory)
    {
        var wanted = ToPriorityClass(priority);
        var refused = ProcessGuard.Open(pid, createTime, Native.PROCESS_SET_INFORMATION, self, systemDirectory, out var guarded);
        if (refused is not null)
        {
            return refused;
        }

        using (guarded)
        {
            var handle = guarded!.Handle;
            if (!Native.SetPriorityClass(handle, wanted))
            {
                return FromLastError();
            }

            // Windows clamps what it will not give (for example high for a job with a limit): say what really happened.
            return Native.GetPriorityClass(handle) == wanted
                ? ActionResult.Of(OperationCode.Ok, MessageKeys.ProcessPrioritySet, ("priority", priority.ToString()))
                : ActionResult.Of(OperationCode.Error, MessageKeys.ProcessError, ("code", "priority-not-applied"));
        }
    }

    /// <summary>
    /// Efficiency mode = EXECUTION_SPEED throttling (Control and State both set) plus IDLE priority. Two calls, so it
    /// can end half done: that is rolled back at once and said out loud. On "on" the previous priority class and
    /// masks come back in <see cref="ActionResult.Data"/> (<see cref="HelperDataKeys"/>) for the caller to keep.
    /// On "off" <paramref name="restore"/> (or the system default if nothing was recorded) is put back.
    /// </summary>
    public static ActionResult SetEfficiency(
        uint pid, long createTime, bool enable, EfficiencyRestore? restore, SelfIdentity self, string systemDirectory)
    {
        if (restore is not null
            && (!HelperRequest.IsRestorablePriorityClass(restore.PriorityClass) || restore.ThrottleControl > 1 || restore.ThrottleState > 1))
        {
            return ActionResult.Of(OperationCode.InvalidArguments, MessageKeys.ProcessError, ("code", "restore-values"));
        }

        var refused = ProcessGuard.Open(pid, createTime, Native.PROCESS_SET_INFORMATION, self, systemDirectory, out var guarded);
        if (refused is not null)
        {
            return refused;
        }

        using (guarded)
        {
            var handle = guarded!.Handle;
            return enable ? TurnOn(handle) : TurnOff(handle, restore ?? EfficiencyRestore.SystemDefault);
        }
    }

    private static ActionResult TurnOn(SafeKernelHandle handle)
    {
        var previousClass = Native.GetPriorityClass(handle);
        var previousKnown = TryGetThrottle(handle, out var previous);
        if (!HelperRequest.IsRestorablePriorityClass(previousClass))
        {
            // Realtime (or an unreadable class) cannot be put back by us, so do not touch a process whose original we cannot restore.
            return ActionResult.Of(OperationCode.Unsupported, MessageKeys.EfficiencyUnsupported);
        }

        if (!TrySetThrottle(handle, 1, 1, out var error))
        {
            return error is Native.ERROR_INVALID_PARAMETER or 50
                ? ActionResult.Of(OperationCode.Unsupported, MessageKeys.EfficiencyUnsupported)
                : FromError(error);
        }

        if (!Native.SetPriorityClass(handle, Native.IDLE_PRIORITY_CLASS))
        {
            var priorityError = Marshal.GetLastPInvokeError();
            var back = previousKnown ? TrySetThrottle(handle, previous.ControlMask, previous.StateMask, out _) : TrySetThrottle(handle, 0, 0, out _);
            return ActionResult.Of(
                OperationCode.PartialSuccess,
                back ? MessageKeys.EfficiencyRolledBack : MessageKeys.EfficiencyRollbackFailed,
                ("code", ProcessGuard.Code(priorityError)));
        }

        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HelperDataKeys.PreviousPriority] = previousClass.ToString(CultureInfo.InvariantCulture),
            [HelperDataKeys.PreviousControl] = (previousKnown ? previous.ControlMask : 0).ToString(CultureInfo.InvariantCulture),
            [HelperDataKeys.PreviousState] = (previousKnown ? previous.StateMask : 0).ToString(CultureInfo.InvariantCulture),
            [HelperDataKeys.PreviousKnown] = previousKnown ? "true" : "false",
        };
        return ActionResult.Of(OperationCode.Ok, MessageKeys.EfficiencyOn).WithData(data);
    }

    private static ActionResult TurnOff(SafeKernelHandle handle, EfficiencyRestore restore)
    {
        if (!TrySetThrottle(handle, restore.ThrottleControl, restore.ThrottleState, out var error))
        {
            return error is Native.ERROR_INVALID_PARAMETER or 50
                ? ActionResult.Of(OperationCode.Unsupported, MessageKeys.EfficiencyUnsupported)
                : FromError(error);
        }

        if (!Native.SetPriorityClass(handle, restore.PriorityClass))
        {
            var priorityError = Marshal.GetLastPInvokeError();
            return ActionResult.Of(OperationCode.PartialSuccess, MessageKeys.ProcessError, ("code", ProcessGuard.Code(priorityError)));
        }

        return ActionResult.Of(OperationCode.Ok, MessageKeys.EfficiencyOff);
    }

    /// <summary>Reads the current power-throttling masks of a process (query rights only). For tests and for showing the state.</summary>
    internal static bool QueryThrottle(uint pid, out uint control, out uint state)
    {
        control = state = 0;
        using var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid || !TryGetThrottle(handle, out var s))
        {
            return false;
        }

        control = s.ControlMask;
        state = s.StateMask;
        return true;
    }

    private static unsafe bool TryGetThrottle(SafeKernelHandle handle, out PowerThrottlingState state)
    {
        var s = new PowerThrottlingState { Version = Native.PROCESS_POWER_THROTTLING_CURRENT_VERSION };
        var ok = Native.GetProcessInformation(handle, Native.ProcessPowerThrottling, &s, (uint)sizeof(PowerThrottlingState));
        state = ok ? s : default;
        return ok;
    }

    private static unsafe bool TrySetThrottle(SafeKernelHandle handle, uint control, uint state, out int error)
    {
        var s = new PowerThrottlingState
        {
            Version = Native.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = control == 0 ? 0 : Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = state == 0 ? 0 : Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
        };
        if (Native.SetProcessInformation(handle, Native.ProcessPowerThrottling, &s, (uint)sizeof(PowerThrottlingState)))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        return false;
    }

    private static ActionResult FromLastError() => FromError(Marshal.GetLastPInvokeError());

    private static ActionResult FromError(int error) => error == Native.ERROR_ACCESS_DENIED
        ? ActionResult.Of(OperationCode.Denied, MessageKeys.ProcessDenied)
        : ActionResult.Of(OperationCode.Error, MessageKeys.ProcessError, ("code", ProcessGuard.Code(error)));
}
