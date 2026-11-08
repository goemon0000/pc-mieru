using PcMieru.Wire;

namespace PcMieru.Core.Tests.Management;

/// <summary>The staged start/stop wait, driven by a scripted fake and a fake clock: no real service is involved.</summary>
public class ServiceTransitionTests
{
    private static (FakeServiceApi Api, FakeClock Clock) Setup(ServiceSnapshot initial)
    {
        var clock = new FakeClock();
        return (new FakeServiceApi(clock) { Initial = initial }, clock);
    }

    [Fact]
    public void Start_a_stopped_service_waits_for_running()
    {
        var (api, clock) = Setup(FakeServiceApi.Stopped());
        api.Script.Add((0, FakeServiceApi.StartPending(1)));
        api.Script.Add((1500, FakeServiceApi.StartPending(2)));
        api.Script.Add((3000, FakeServiceApi.Running()));

        var result = ServiceTransition.Start(api, clock, "Demo");

        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.Equal(MessageKeys.ServiceStarted, result.MessageKey);
        Assert.Equal(1, api.StartCalls);
        Assert.True(clock.Delays > 0);
    }

    [Fact]
    public void Start_of_a_running_service_does_nothing()
    {
        var (api, clock) = Setup(FakeServiceApi.Running());
        var result = ServiceTransition.Start(api, clock, "Demo");
        Assert.Equal(MessageKeys.ServiceAlreadyRunning, result.MessageKey);
        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.Equal(0, api.StartCalls);
    }

    [Fact]
    public void A_start_that_falls_back_to_stopped_is_an_error_with_the_exit_code()
    {
        var (api, clock) = Setup(FakeServiceApi.Stopped());
        api.Script.Add((0, FakeServiceApi.StartPending(1)));
        api.Script.Add((1000, FakeServiceApi.Stopped(1053)));

        var result = ServiceTransition.Start(api, clock, "Demo");

        Assert.Equal(OperationCode.Error, result.Code);
        Assert.Equal(MessageKeys.ServiceStartFailed, result.MessageKey);
        Assert.Equal("1053", result.Args!["exitCode"]);
    }

    [Fact]
    public void A_start_that_makes_no_progress_ends_as_pending_after_the_wait_hint_and_is_not_rolled_back()
    {
        var (api, clock) = Setup(FakeServiceApi.Stopped());
        api.Script.Add((0, FakeServiceApi.StartPending(1, waitHint: 5000)));
        var begin = clock.NowMs;

        var result = ServiceTransition.Start(api, clock, "Demo");

        Assert.Equal(OperationCode.PartialSuccess, result.Code);
        Assert.Equal(MessageKeys.ServiceStartPending, result.MessageKey);
        Assert.Equal(0, api.StopCalls);
        Assert.InRange(clock.NowMs - begin, 10_000, 15_000);
    }

    [Fact]
    public void A_rising_checkpoint_keeps_the_wait_going_past_the_stall_time_but_never_past_the_total_limit()
    {
        var (api, clock) = Setup(FakeServiceApi.Stopped());
        api.Script.Add((0, FakeServiceApi.StartPending(0, waitHint: 2000)));
        for (uint i = 1; i < 400; i++)
        {
            api.Script.Add((i * 1000L, FakeServiceApi.StartPending(i, waitHint: 2000)));
        }

        var begin = clock.NowMs;
        var result = ServiceTransition.Start(api, clock, "Demo");

        Assert.Equal(OperationCode.PartialSuccess, result.Code);
        Assert.InRange(clock.NowMs - begin, 120_000, 125_000);
    }

    [Fact]
    public void A_disabled_service_is_reported_as_such()
    {
        var (api, clock) = Setup(FakeServiceApi.Stopped());
        api.StartError = Native.ERROR_SERVICE_DISABLED;
        Assert.Equal(MessageKeys.ServiceDisabled, ServiceTransition.Start(api, clock, "Demo").MessageKey);
    }

    [Fact]
    public void Stop_with_running_dependents_is_refused_and_lists_them_without_stopping_anything()
    {
        var (api, clock) = Setup(FakeServiceApi.Running());
        api.Dependents = ["Alpha", "Beta"];

        var result = ServiceTransition.Stop(api, clock, "Demo");

        Assert.Equal(OperationCode.Unsupported, result.Code);
        Assert.Equal(MessageKeys.ServiceDependentsRunning, result.MessageKey);
        Assert.Equal("Alpha, Beta", result.Args!["dependents"]);
        Assert.Equal(0, api.StopCalls);
    }

    [Fact]
    public void Stop_waits_through_stop_pending()
    {
        var (api, clock) = Setup(FakeServiceApi.Running());
        api.Script.Add((0, FakeServiceApi.StopPending(1)));
        api.Script.Add((2500, FakeServiceApi.Stopped()));

        var result = ServiceTransition.Stop(api, clock, "Demo");

        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.Equal(MessageKeys.ServiceStopped, result.MessageKey);
        Assert.Equal(1, api.StopCalls);
    }

    [Fact]
    public void A_service_that_does_not_accept_stop_is_not_asked_to()
    {
        var (api, clock) = Setup(new ServiceSnapshot(ServiceSnapshot.Running, 0, 0, 99, 0, 0));
        var result = ServiceTransition.Stop(api, clock, "Demo");
        Assert.Equal(MessageKeys.ServiceCannotStop, result.MessageKey);
        Assert.Equal(0, api.StopCalls);
    }

    [Fact]
    public void Stop_of_a_stopped_service_is_fine()
    {
        var (api, clock) = Setup(FakeServiceApi.Stopped());
        var result = ServiceTransition.Stop(api, clock, "Demo");
        Assert.Equal(MessageKeys.ServiceAlreadyStopped, result.MessageKey);
        Assert.Equal(OperationCode.Ok, result.Code);
    }

    [Fact]
    public void Restart_runs_stop_then_start()
    {
        var (api, clock) = Setup(FakeServiceApi.Running());
        api.Script.Add((0, FakeServiceApi.Stopped()));

        // After Stop the fake reports Stopped, after Start the fake must report Running: switch the script on the Start call.
        var restarting = new RestartingApi(api);
        var result = ServiceTransition.Restart(restarting, clock, "Demo");

        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.Equal(MessageKeys.ServiceRestarted, result.MessageKey);
    }

    [Fact]
    public void A_restart_whose_start_fails_says_the_service_stays_stopped()
    {
        var (api, clock) = Setup(FakeServiceApi.Running());
        api.Script.Add((0, FakeServiceApi.Stopped()));
        api.StartError = Native.ERROR_ACCESS_DENIED;

        var result = ServiceTransition.Restart(api, clock, "Demo");

        Assert.Equal(OperationCode.PartialSuccess, result.Code);
        Assert.Equal(MessageKeys.ServiceRestartStoppedOnly, result.MessageKey);
        Assert.Equal(MessageKeys.ServiceDenied, result.Args!["reason"]);
        Assert.Equal(1, api.StopCalls);
    }

    [Theory]
    [InlineData(Native.ERROR_SERVICE_DOES_NOT_EXIST, OperationCode.NotFound)]
    [InlineData(Native.ERROR_ACCESS_DENIED, OperationCode.Denied)]
    public void Query_errors_map_to_codes(int error, OperationCode expected)
    {
        var (api, clock) = Setup(FakeServiceApi.Running());
        api.QueryError = error;
        Assert.Equal(expected, ServiceTransition.Start(api, clock, "Demo").Code);
        Assert.Equal(expected, ServiceTransition.Stop(api, clock, "Demo").Code);
    }

    /// <summary>Wraps the fake so that Stop leads to Stopped and the following Start leads to Running.</summary>
    private sealed class RestartingApi(FakeServiceApi inner) : IServiceControlApi
    {
        private bool _stopped;
        private bool _started;

        public int Query(string name, out ServiceSnapshot snapshot)
        {
            snapshot = _started ? FakeServiceApi.Running() : _stopped ? FakeServiceApi.Stopped() : inner.Initial;
            return 0;
        }

        public int Start(string name)
        {
            _started = true;
            return 0;
        }

        public int Stop(string name)
        {
            _stopped = true;
            return 0;
        }

        public int RunningDependents(string name, out IReadOnlyList<string> dependents) => inner.RunningDependents(name, out dependents);
    }
}
