using System.Globalization;

namespace PcMieru.Wire;

/// <summary>The few facts about a service that the staged wait looks at (SERVICE_STATUS_PROCESS).</summary>
public readonly record struct ServiceSnapshot(
    uint State,
    uint CheckPoint,
    uint WaitHint,
    uint ProcessId,
    uint ControlsAccepted,
    uint Win32ExitCode)
{
    public const uint Stopped = 1;
    public const uint StartPending = 2;
    public const uint StopPending = 3;
    public const uint Running = 4;
    public const uint ContinuePending = 5;
    public const uint PausePending = 6;
    public const uint Paused = 7;

    public const uint AcceptStop = 0x1;

    public bool IsPending => State is StartPending or StopPending or ContinuePending or PausePending;
}

/// <summary>The service control manager as the staged wait needs it. A fake stands in for it in the tests.</summary>
public interface IServiceControlApi
{
    /// <summary>Win32 error 0 = ok.</summary>
    int Query(string name, out ServiceSnapshot snapshot);

    int Start(string name);

    int Stop(string name);

    /// <summary>The services that are running and depend on this one (they would block a stop).</summary>
    int RunningDependents(string name, out IReadOnlyList<string> dependents);
}

public interface ITransitionClock
{
    long NowMs { get; }

    void Delay(int milliseconds);
}

public sealed class SystemTransitionClock : ITransitionClock
{
    public static SystemTransitionClock Instance { get; } = new();

    public long NowMs => Environment.TickCount64;

    public void Delay(int milliseconds) => Thread.Sleep(milliseconds);
}

public sealed record ServiceWaitOptions
{
    /// <summary>How long a state may stay pending without its checkpoint moving, at least (the wait hint raises it).</summary>
    public int MinStallMs { get; init; } = 10_000;

    /// <summary>The longest we trust a service's own wait hint.</summary>
    public int MaxStallMs { get; init; } = 60_000;

    /// <summary>The longest one start or stop is waited for, whatever the checkpoint says.</summary>
    public int MaxTotalMs { get; init; } = 120_000;

    public int MinPollMs { get; init; } = 200;

    public int MaxPollMs { get; init; } = 2_000;
}

/// <summary>
/// Start, stop and restart as a staged wait (SPEC 5.5): the request being accepted is one thing, the service reaching
/// RUNNING or STOPPED is another, and both are reported apart. While a *_PENDING state shows progress (its
/// checkpoint rises) the wait goes on; no progress for the wait hint ends it. Nothing is rolled back automatically.
/// </summary>
public static class ServiceTransition
{
    public static ActionResult Start(IServiceControlApi api, ITransitionClock clock, string name, ServiceWaitOptions? options = null)
    {
        options ??= new ServiceWaitOptions();
        var named = ("name", name);

        var error = api.Query(name, out var now);
        if (error != 0)
        {
            return FromError(name, error);
        }

        switch (now.State)
        {
            case ServiceSnapshot.Running:
                return ActionResult.Of(OperationCode.Ok, MessageKeys.ServiceAlreadyRunning, named);

            case ServiceSnapshot.StartPending:
                return AwaitRunning(api, clock, name, options, now);

            case ServiceSnapshot.StopPending:
                {
                    var stopped = AwaitState(api, clock, name, options, ServiceSnapshot.Stopped, out var after);
                    if (!stopped)
                    {
                        return ActionResult.Of(OperationCode.PartialSuccess, MessageKeys.ServiceStopPending, named);
                    }

                    break;
                }

            case ServiceSnapshot.Stopped:
                break;

            default:
                return ActionResult.Of(OperationCode.Unsupported, MessageKeys.ServiceError, named, ("code", "state-" + now.State.ToString(CultureInfo.InvariantCulture)));
        }

        error = api.Start(name);
        if (error == Native.ERROR_SERVICE_ALREADY_RUNNING)
        {
            return ActionResult.Of(OperationCode.Ok, MessageKeys.ServiceAlreadyRunning, named);
        }

        if (error != 0)
        {
            return FromError(name, error);
        }

        error = api.Query(name, out now);
        return error != 0 ? FromError(name, error) : AwaitRunning(api, clock, name, options, now);
    }

    public static ActionResult Stop(IServiceControlApi api, ITransitionClock clock, string name, ServiceWaitOptions? options = null)
    {
        options ??= new ServiceWaitOptions();
        var named = ("name", name);

        var error = api.Query(name, out var now);
        if (error != 0)
        {
            return FromError(name, error);
        }

        if (now.State == ServiceSnapshot.Stopped)
        {
            return ActionResult.Of(OperationCode.Ok, MessageKeys.ServiceAlreadyStopped, named);
        }

        if (now.State == ServiceSnapshot.StopPending)
        {
            return AwaitStopped(api, clock, name, options, now);
        }

        error = api.RunningDependents(name, out var dependents);
        if (error != 0)
        {
            return FromError(name, error);
        }

        if (dependents.Count > 0)
        {
            return ActionResult.Of(OperationCode.Unsupported, MessageKeys.ServiceDependentsRunning, named, ("dependents", string.Join(", ", dependents)));
        }

        if ((now.ControlsAccepted & ServiceSnapshot.AcceptStop) == 0 && !now.IsPending)
        {
            return ActionResult.Of(OperationCode.Unsupported, MessageKeys.ServiceCannotStop, named);
        }

        error = api.Stop(name);
        if (error == Native.ERROR_SERVICE_NOT_ACTIVE)
        {
            return ActionResult.Of(OperationCode.Ok, MessageKeys.ServiceAlreadyStopped, named);
        }

        if (error != 0)
        {
            return FromError(name, error);
        }

        error = api.Query(name, out now);
        return error != 0 ? FromError(name, error) : AwaitStopped(api, clock, name, options, now);
    }

    /// <summary>Stop, then start. Stopping something already stopped is fine. If the start fails the service stays stopped and that is said.</summary>
    public static ActionResult Restart(IServiceControlApi api, ITransitionClock clock, string name, ServiceWaitOptions? options = null)
    {
        var stop = Stop(api, clock, name, options);
        if (!stop.IsSuccess)
        {
            return stop;
        }

        var start = Start(api, clock, name, options);
        if (start.IsSuccess)
        {
            return ActionResult.Of(OperationCode.Ok, MessageKeys.ServiceRestarted, ("name", name));
        }

        if (start.Code == OperationCode.PartialSuccess)
        {
            return start;
        }

        return ActionResult.Of(OperationCode.PartialSuccess, MessageKeys.ServiceRestartStoppedOnly, ("name", name))
            .WithArg("reason", start.MessageKey);
    }

    private static ActionResult AwaitRunning(IServiceControlApi api, ITransitionClock clock, string name, ServiceWaitOptions options, ServiceSnapshot now)
    {
        var named = ("name", name);
        var outcome = Wait(api, clock, name, options, now, ServiceSnapshot.Running, out var last);
        return outcome switch
        {
            WaitOutcome.Reached => ActionResult.Of(OperationCode.Ok, MessageKeys.ServiceStarted, named),
            WaitOutcome.StoppedInstead => ActionResult.Of(
                OperationCode.Error, MessageKeys.ServiceStartFailed, named, ("exitCode", last.Win32ExitCode.ToString(CultureInfo.InvariantCulture))),
            WaitOutcome.Error => FromError(name, last.Win32ExitCode == 0 ? 1 : (int)last.Win32ExitCode),
            _ => ActionResult.Of(OperationCode.PartialSuccess, MessageKeys.ServiceStartPending, named),
        };
    }

    private static ActionResult AwaitStopped(IServiceControlApi api, ITransitionClock clock, string name, ServiceWaitOptions options, ServiceSnapshot now)
    {
        var named = ("name", name);
        var outcome = Wait(api, clock, name, options, now, ServiceSnapshot.Stopped, out var last);
        return outcome switch
        {
            WaitOutcome.Reached => ActionResult.Of(OperationCode.Ok, MessageKeys.ServiceStopped, named),
            WaitOutcome.Error => FromError(name, last.Win32ExitCode == 0 ? 1 : (int)last.Win32ExitCode),
            _ => ActionResult.Of(OperationCode.PartialSuccess, MessageKeys.ServiceStopPending, named),
        };
    }

    private static bool AwaitState(IServiceControlApi api, ITransitionClock clock, string name, ServiceWaitOptions options, uint target, out ServiceSnapshot last)
    {
        var error = api.Query(name, out var now);
        if (error != 0)
        {
            last = default;
            return false;
        }

        return Wait(api, clock, name, options, now, target, out last) == WaitOutcome.Reached;
    }

    private enum WaitOutcome
    {
        Reached,
        StoppedInstead,
        TimedOut,
        Error,
    }

    /// <summary>
    /// Polls until <paramref name="target"/>. A pending state keeps being waited for while its checkpoint rises; with no
    /// progress for max(wait hint, MinStall) (at most MaxStall) it ends, and nothing runs past MaxTotal.
    /// Waiting for RUNNING and seeing STOPPED means the start failed (the exit code says why).
    /// </summary>
    private static WaitOutcome Wait(
        IServiceControlApi api, ITransitionClock clock, string name, ServiceWaitOptions options, ServiceSnapshot now, uint target, out ServiceSnapshot last)
    {
        var begin = clock.NowMs;
        var lastProgress = begin;
        var checkpoint = now.CheckPoint;
        var state = now.State;
        last = now;

        while (true)
        {
            if (now.State == target)
            {
                last = now;
                return WaitOutcome.Reached;
            }

            if (target == ServiceSnapshot.Running && now.State == ServiceSnapshot.Stopped)
            {
                last = now;
                return WaitOutcome.StoppedInstead;
            }

            var current = clock.NowMs;
            if (now.State != state || now.CheckPoint != checkpoint)
            {
                lastProgress = current;
                state = now.State;
                checkpoint = now.CheckPoint;
            }

            var stall = Math.Clamp((int)Math.Min(now.WaitHint, int.MaxValue), options.MinStallMs, options.MaxStallMs);
            if (current - lastProgress >= stall || current - begin >= options.MaxTotalMs)
            {
                last = now;
                return WaitOutcome.TimedOut;
            }

            var poll = Math.Clamp((int)Math.Min(now.WaitHint / 10, int.MaxValue), options.MinPollMs, options.MaxPollMs);
            clock.Delay(poll);

            var error = api.Query(name, out now);
            if (error != 0)
            {
                last = new ServiceSnapshot(0, 0, 0, 0, 0, (uint)error);
                return WaitOutcome.Error;
            }

            last = now;
        }
    }

    private static ActionResult FromError(string name, int error)
    {
        var named = ("name", name);
        return error switch
        {
            Native.ERROR_SERVICE_DOES_NOT_EXIST => ActionResult.Of(OperationCode.NotFound, MessageKeys.ServiceNotFound, named),
            Native.ERROR_ACCESS_DENIED => ActionResult.Of(OperationCode.Denied, MessageKeys.ServiceDenied, named),
            Native.ERROR_SERVICE_DISABLED => ActionResult.Of(OperationCode.Unsupported, MessageKeys.ServiceDisabled, named),
            Native.ERROR_SERVICE_CANNOT_ACCEPT_CTRL => ActionResult.Of(OperationCode.Unsupported, MessageKeys.ServiceCannotStop, named),
            Native.ERROR_DEPENDENT_SERVICES_RUNNING => ActionResult.Of(OperationCode.Unsupported, MessageKeys.ServiceDependentsRunning, named, ("dependents", "?")),
            _ => ActionResult.Of(OperationCode.Error, MessageKeys.ServiceError, named, ("code", error.ToString(CultureInfo.InvariantCulture))),
        };
    }
}
